using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wcc.App.ViewModels;

// Readers own their waits; only retirement of the shared job cancels its work.
internal sealed class UnitDisplayWorkLifetime : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationTokenRegistration _parentRegistration;
    private int _readers;
    private bool _retired;
    private bool _workFinished;
    private bool _cancellationFinished;
    private bool _disposed;
    private readonly TaskCompletionSource _cancellationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CancellationToken Token { get; }
    internal UnitDisplayWorkLifetime() : this(CancellationToken.None) { }
    internal UnitDisplayWorkLifetime(CancellationToken parent)
    {
        _cancellation = new();
        Token = _cancellation.Token;
        // Parent cancellation uses the same owned CancelAsync operation. A linked
        // CTS could already be executing callbacks when CancelAsync is invoked,
        // whose returned task would then not describe those earlier callbacks.
        _parentRegistration = parent.UnsafeRegister(static state => ((UnitDisplayWorkLifetime)state!).Dispose(), this);
    }
    internal int Readers { get { lock (_sync) return _readers; } }
    internal bool IsRetired { get { lock (_sync) return _retired; } }
    internal Task CancellationCompletion => _cancellationCompletion.Task;

    internal IDisposable? Acquire()
    {
        lock (_sync)
        {
            if (_retired || Token.IsCancellationRequested) return null;
            _readers++;
            return new Reader(this);
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            _readers--;
            DisposeWhenFinished();
        }
    }

    internal void Complete()
    {
        lock (_sync)
        {
            _workFinished = true;
            DisposeWhenFinished();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_retired) return;
            _retired = true;
        }
        // A caller can itself be inside a collection publication lock. CancelAsync
        // marks the token immediately and runs callbacks outside that outer lock.
        _ = CompleteCancellationAsync();
    }

    private async Task CompleteCancellationAsync()
    {
        Exception? failure = null;
        try { await _cancellation.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        finally
        {
            lock (_sync)
            {
                _cancellationFinished = true;
                DisposeWhenFinished();
            }
            if (failure is null) _cancellationCompletion.TrySetResult();
            else _cancellationCompletion.TrySetException(failure);
        }
    }

    private void DisposeWhenFinished()
    {
        if (!_retired || !_cancellationFinished || !_workFinished || _readers != 0 || _disposed) return;
        _disposed = true;
        // Do not synchronously join a parent callback while holding this lock.
        _parentRegistration.Unregister();
        _cancellation.Dispose();
    }

    private sealed class Reader(UnitDisplayWorkLifetime owner) : IDisposable
    {
        private UnitDisplayWorkLifetime? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
