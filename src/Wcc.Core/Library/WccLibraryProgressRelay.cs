namespace Wcc.Core.Library;

/// <summary>
/// Throttles worker progress and schedules it for a consumer while ensuring that
/// callbacks queued by a completed refresh can no longer update that consumer.
/// </summary>
public sealed class WccLibraryProgressRelay : IProgress<WccLibraryProgress>, IDisposable
{
    // Callback publication and Complete share one authority gate. Reporting
    // touches only throttle state, so UI work cannot stall the indexing worker.
    private readonly object _gate = new();
    private readonly object _reportGate = new();
    private readonly Action<Action> _schedule;
    private readonly Action<WccLibraryProgress> _callback;
    private readonly long _minimumIntervalMilliseconds;
    private long _lastPublishedAt;
    private WccLibraryWorkPhase? _lastPhase;
    private volatile bool _isActive = true;

    public WccLibraryProgressRelay(
        Action<Action> schedule,
        Action<WccLibraryProgress> callback,
        TimeSpan? minimumInterval = null)
    {
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _minimumIntervalMilliseconds = Math.Max(
            0,
            (long)(minimumInterval ?? TimeSpan.FromMilliseconds(250)).TotalMilliseconds);
    }

    public void Report(WccLibraryProgress value)
    {
        lock (_reportGate)
        {
            if (!_isActive)
                return;

            var now = Environment.TickCount64;
            var phaseChanged = _lastPhase != value.Phase;
            var completed = value.TotalItems > 0 && value.ProcessedItems >= value.TotalItems;
            if (!phaseChanged &&
                !completed &&
                now - _lastPublishedAt < _minimumIntervalMilliseconds)
            {
                return;
            }

            _lastPhase = value.Phase;
            _lastPublishedAt = now;
        }

        _schedule(() =>
        {
            lock (_gate)
            {
                if (_isActive)
                    _callback(value);
            }
        });
    }

    public void Complete()
    {
        lock (_gate)
            _isActive = false;
    }

    public void Dispose() => Complete();
}
