using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>WCC field proposals stay outside authored models until an explicit guarded save.</summary>
public sealed class WccScenarioFieldEditSession
{
    private readonly object _gate = new();
    private readonly WccScenarioFieldEditCoordinator _coordinator = new(collectDiagnostics: true);
    private readonly SynchronizationContext? _notifications = SynchronizationContext.Current;
    private Publication? _publication;
    private WccScenarioFieldEditDraft? _draft;
    private bool _isBusy;
    private IReadOnlyList<WccSourceSaveDiagnostics> _lastBatchSaveDiagnostics = Array.Empty<WccSourceSaveDiagnostics>();

    public event Action? Changed;
    public bool IsBusy { get { lock (_gate) return _isBusy; } }
    public bool HasSession { get { lock (_gate) return _publication is not null; } }
    public WccTextAtomicReplaceResult? Recovery => _coordinator.Recovery;
    public IReadOnlyList<WccSourceSaveDiagnostics> LastBatchSaveDiagnostics
    { get { lock (_gate) return _lastBatchSaveDiagnostics; } }

    public void Publish(WccScenarioFieldEditSessionState state)
    {
        lock (_gate)
        {
            ClearDraft();
            _publication = null;
            _coordinator.SetSession(state);
            _publication = new(state);
        }
        Notify();
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            ClearDraft();
            _publication = null;
            _coordinator.InvalidateSession();
        }
        Notify();
    }

    public WccScenarioFieldEditDraft? BeginEdit(WccScenarioFieldRoute route)
    {
        WccScenarioFieldEditDraft draft;
        lock (_gate)
        {
            if (_isBusy || Recovery is not null || _publication is not { State.IsWritable: true } publication ||
                !ReferenceEquals(route.Owner, publication.State.Owner) ||
                route.Source.Path != publication.State.Source.Path ||
                route.Source.ContentHash != publication.State.Source.ContentHash ||
                route.ContextSemanticKey != publication.State.Context.SemanticKey ||
                !(route.CanSet || route.CanInsert))
                return null;
            ClearDraft();
            draft = new(this, publication, route);
            _draft = draft;
        }
        Notify();
        return draft;
    }

    public void EndEdit(WccScenarioFieldEditDraft draft)
    {
        lock (_gate)
        {
            if (!IsCurrentCore(draft)) return;
            ClearDraft();
            _publication = null;
            _coordinator.InvalidateSession();
        }
        Notify();
    }

    public Task<WccScenarioFieldEditResult> PrepareAsync(WccScenarioFieldEditDraft draft,
        WmlFieldOperation operation, string? requestedValue, CancellationToken cancellationToken = default) =>
        ExecuteAsync(draft, operation, requestedValue, false, cancellationToken);

    public Task<WccScenarioFieldEditResult> CommitAsync(WccScenarioFieldEditDraft draft,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(draft, draft.Operation, draft.RequestedValue, true, cancellationToken);

    /// <summary>Checks one composed source candidate and writes it once.</summary>
    public async Task<WccScenarioFieldBatchEditResult> SaveBatchAsync(WccScenarioFieldEditSessionState state,
        IReadOnlyList<WccScenarioFieldEditProposal> proposals, CancellationToken cancellationToken = default)
    {
        Publication publication;
        lock (_gate)
        {
            if (_isBusy)
                return new(WccScenarioFieldEditStatus.InvalidRequest, "A WCC field operation is already running.");
            if (Recovery is not null)
                return new(WccScenarioFieldEditStatus.RecoveryRequired, "A previous save requires source recovery.", Save: Recovery);
            ClearDraft();
            _coordinator.SetSession(state);
            publication = new(state);
            _publication = publication;
            _isBusy = true;
            _lastBatchSaveDiagnostics = Array.Empty<WccSourceSaveDiagnostics>();
        }
        Notify();
        var diagnostics = new List<WccSourceSaveDiagnostics>();
        var previousDiagnostics = _coordinator.LastDiagnostics;
        void CaptureDiagnostics()
        {
            if (_coordinator.LastDiagnostics is { } current && !ReferenceEquals(current, previousDiagnostics))
            { diagnostics.Add(current); previousDiagnostics = current; }
        }
        try
        {
            var prepared = await Task.Run(async () => await _coordinator.PrepareBatchAsync(proposals, cancellationToken));
            CaptureDiagnostics();
            if (!prepared.IsPrepared || prepared.Ticket is null)
                return prepared with { Ticket = null };
            lock (_gate)
            {
                if (!ReferenceEquals(_publication, publication))
                    return new(WccScenarioFieldEditStatus.StaleSession,
                        "The source or editing context changed while checking. Your drafts are retained.");
            }
            var result = await Task.Run(async () => await _coordinator.CommitBatchAsync(prepared.Ticket, cancellationToken));
            CaptureDiagnostics();
            return result with { Ticket = null };
        }
        finally
        {
            CaptureDiagnostics();
            lock (_gate)
            {
                _lastBatchSaveDiagnostics = Array.AsReadOnly(diagnostics.ToArray());
                if (ReferenceEquals(_publication, publication))
                {
                    _publication = null;
                    _coordinator.InvalidateSession();
                }
                _isBusy = false;
            }
            Notify();
        }
    }

    private async Task<WccScenarioFieldEditResult> ExecuteAsync(WccScenarioFieldEditDraft draft,
        WmlFieldOperation operation, string? requestedValue, bool commit, CancellationToken cancellationToken)
    {
        Publication publication;
        WccScenarioFieldEditTicket? ticket;
        lock (_gate)
        {
            var refusal = CheckDraft(draft);
            if (refusal is not null) return refusal;
            if (_isBusy) return Refuse(WccScenarioFieldEditStatus.InvalidRequest, "A WCC field operation is already running.");
            if (commit && draft.Ticket is null)
                return Refuse(WccScenarioFieldEditStatus.InvalidTicket, "Check the proposed change before saving it.");
            publication = _publication!;
            ticket = draft.Ticket;
            draft.Ticket = null;
            draft.Result = null;
            if (!commit)
            {
                draft.Operation = operation;
                draft.RequestedValue = requestedValue;
            }
            _isBusy = true;
        }
        Notify();
        try
        {
            var result = await Task.Run(async () =>
            {
                ValueTask<WccScenarioFieldEditResult> pending;
                lock (_gate)
                {
                    var refusal = CheckDraft(draft);
                    if (refusal is not null) return refusal;
                    pending = commit ? _coordinator.CommitAsync(ticket!, cancellationToken) :
                        _coordinator.PrepareAsync(draft.Route, operation, requestedValue, cancellationToken);
                }
                return await pending;
            });
            lock (_gate)
            {
                if (result.IsPrepared && !IsCurrentCore(draft))
                    return Refuse(WccScenarioFieldEditStatus.StaleSession, "The source or editing context changed. Open a fresh field edit.");
                if (IsCurrentCore(draft))
                {
                    draft.Result = result with { Ticket = null };
                    draft.Ticket = result.Ticket;
                }
                if (result.IsSuccess && ReferenceEquals(_publication, publication))
                {
                    _publication = null;
                    ClearDraft();
                }
            }
            return result with { Ticket = null };
        }
        finally
        {
            lock (_gate) _isBusy = false;
            Notify();
        }
    }

    internal bool IsCurrent(WccScenarioFieldEditDraft draft) { lock (_gate) return IsCurrentCore(draft); }
    private bool IsCurrentCore(WccScenarioFieldEditDraft draft) => ReferenceEquals(_draft, draft) &&
        ReferenceEquals(draft.Publication, _publication) && _publication is not null;
    private WccScenarioFieldEditResult? CheckDraft(WccScenarioFieldEditDraft draft) => Recovery is not null
        ? new(WccScenarioFieldEditStatus.RecoveryRequired, "A previous save requires source recovery.", Save: Recovery)
        : !IsCurrentCore(draft) ? Refuse(WccScenarioFieldEditStatus.StaleSession,
            "The source or editing context changed. Open a fresh field edit.") : null;
    private void ClearDraft()
    {
        if (_draft is not null) _draft.Ticket = null;
        _draft = null;
    }
    private static WccScenarioFieldEditResult Refuse(WccScenarioFieldEditStatus status, string message) => new(status, message);
    private void Notify()
    {
        if (_notifications is not null && SynchronizationContext.Current != _notifications)
            _notifications.Post(_ => Changed?.Invoke(), null);
        else Changed?.Invoke();
    }
    internal sealed record Publication(WccScenarioFieldEditSessionState State);
}

public sealed class WccScenarioFieldEditDraft
{
    private readonly WccScenarioFieldEditSession _session;
    internal WccScenarioFieldEditDraft(WccScenarioFieldEditSession session,
        WccScenarioFieldEditSession.Publication publication, WccScenarioFieldRoute route)
    {
        _session = session;
        Publication = publication;
        Route = route;
    }
    internal WccScenarioFieldEditSession.Publication Publication { get; }
    internal WccScenarioFieldEditTicket? Ticket { get; set; }
    public WccScenarioFieldRoute Route { get; }
    public string? OriginalValue => Route.Value;
    public string? RequestedValue { get; internal set; }
    public WmlFieldOperation Operation { get; internal set; } = WmlFieldOperation.Set;
    public WccScenarioFieldEditResult? Result { get; internal set; }
    public bool IsCurrent => _session.IsCurrent(this);
    public bool CanSave => IsCurrent && !_session.IsBusy && Ticket is not null && Result?.IsPrepared == true;
}
