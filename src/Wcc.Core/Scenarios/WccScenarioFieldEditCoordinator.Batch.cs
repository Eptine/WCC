using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.Core.Scenarios;

public sealed record WccScenarioFieldBatchEditResult(
    WccScenarioFieldEditStatus Status,
    string Diagnostic,
    WccScenarioFieldBatchEditTicket? Ticket = null,
    WccScenarioFieldBatchCandidate? Candidate = null,
    WccScenarioFieldBatchReviewResult? Review = null,
    WccTextAtomicReplaceResult? Save = null)
{
    public bool IsPrepared => Status == WccScenarioFieldEditStatus.Prepared;
    public bool IsSuccess => Status is WccScenarioFieldEditStatus.Succeeded
        or WccScenarioFieldEditStatus.SucceededWithCleanupWarning;
    public bool AuthorizesPersistence => false;
}

/// <summary>Opaque authority for exactly one attempted collected source write.</summary>
public sealed class WccScenarioFieldBatchEditTicket
{
    private readonly object _owner;
    private int _consumed;
    internal WccScenarioFieldBatchEditTicket(object owner, object binding, WccTextDiskRevision disk,
        WccScenarioFieldBatchCandidate candidate, WccScenarioFieldBatchReviewResult review)
    { _owner = owner; Binding = binding; Disk = disk; Candidate = candidate; Review = review; }

    public bool IsConsumed => Volatile.Read(ref _consumed) != 0;
    internal object Binding { get; }
    internal WccTextDiskRevision Disk { get; }
    internal WccScenarioFieldBatchCandidate Candidate { get; }
    internal WccScenarioFieldBatchReviewResult Review { get; }
    internal bool BelongsTo(object owner) => ReferenceEquals(_owner, owner);
    internal bool TryConsume() => Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
}

public sealed partial class WccScenarioFieldEditCoordinator
{
    public ValueTask<WccScenarioFieldBatchEditResult> PrepareBatchAsync(
        IEnumerable<WccScenarioFieldEditProposal> proposals, CancellationToken cancellationToken = default) => _collectDiagnostics
        ? ObserveBatchAsync("ScalarBatchPrepare", diagnostics => PrepareBatchCoreAsync(proposals, cancellationToken, diagnostics))
        : PrepareBatchCoreAsync(proposals, cancellationToken, null);

    private async ValueTask<WccScenarioFieldBatchEditResult> PrepareBatchCoreAsync(
        IEnumerable<WccScenarioFieldEditProposal> proposals, CancellationToken cancellationToken,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (proposals is null) return new(WccScenarioFieldEditStatus.InvalidRequest, "A collected field proposal is required.");
        var capturedProposals = Array.AsReadOnly(proposals.ToArray());
        diagnostics?.Add(WccSourceSaveCount.RequestedFields, capturedProposals.Count);
        if (capturedProposals.Count == 0 || capturedProposals.Any(proposal => proposal is null
                || proposal.Route is null || proposal.Route.Owner is null))
            return new(WccScenarioFieldEditStatus.InvalidRequest, "At least one complete authored field draft is required.");
        using (diagnostics?.Measure(WccSourceSavePhase.QueueWait))
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionBinding binding;
            using var sessionMeasurement = diagnostics?.Measure(WccSourceSavePhase.SessionValidation);
            lock (_stateGate)
            {
                var refusal = CheckSession();
                if (refusal is not null) return ToBatch(refusal);
                binding = _binding!;
            }
            diagnostics?.Add(WccSourceSaveCount.RequestedFiles);
            diagnostics?.Add(WccSourceSaveCount.SessionStates);
            var contexts = binding.State.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu
                ? new[] { binding.State.Context } : binding.State.Plan?.Contexts ?? new[] { binding.State.Context };
            if (capturedProposals.Any(proposal => !SameSource(proposal.Route.Source, binding.State.Source)
                    || !contexts.Any(context => context.SemanticKey == proposal.Route.ContextSemanticKey)))
                return new(WccScenarioFieldEditStatus.InvalidRequest,
                    "Every draft must belong to the same exact source and a declared campaign context.");

            WccTextDiskRevisionCaptureResult disk;
            using (diagnostics?.Measure(WccSourceSavePhase.DiskCapture))
            {
                diagnostics?.Add(WccSourceSaveCount.DiskCaptures);
                disk = WccTextDiskRevisionCapture.Capture(binding.State.ProjectRootPath,
                    binding.State.Source.Path, binding.State.Source.Text);
                if (disk.Revision is { } revision) diagnostics?.Add(WccSourceSaveCount.CapturedDiskBytes, revision.RawByteLength);
            }
            if (!disk.IsCaptured || disk.Revision is null) return ToBatch(DiskRefusal(disk));
            sessionMeasurement?.Dispose();
            var review = await WccScenarioFieldStaticReviewer.ReviewBatchWithDiagnosticsAsync(
                binding.State, capturedProposals, cancellationToken, diagnostics).ConfigureAwait(false);
            using var finalMeasurement = diagnostics?.Measure(WccSourceSavePhase.FinalValidation);
            lock (_stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refusal = CheckSession(binding);
                if (refusal is not null) return ToBatch(refusal);
                if (!review.IsReadyForReview || review.Candidate is null || review.ReviewIdentity is null || review.Proof is null)
                    return new(WccScenarioFieldEditStatus.ReviewRefused, review.Diagnostic, Review: review);
                if (!SameSource(review.Candidate.Source, binding.State.Source))
                    return new(WccScenarioFieldEditStatus.ReviewChanged,
                        "The collected reviewed source no longer matches its exact captured baseline.", Review: review);
                var changedDisk = RecheckBatchDiskWithDiagnostics(disk.Revision, diagnostics);
                if (changedDisk is not null) return ToBatch(changedDisk);
                var ticket = new WccScenarioFieldBatchEditTicket(this, binding, disk.Revision, review.Candidate, review);
                return new(WccScenarioFieldEditStatus.Prepared,
                    "The collected source changes have been reviewed in every declared context; Save will verify that their source and context remain unchanged.",
                    ticket, review.Candidate, review);
            }
        }
        finally { _operations.Release(); }
    }

    public ValueTask<WccScenarioFieldBatchEditResult> CommitBatchAsync(
        WccScenarioFieldBatchEditTicket ticket, CancellationToken cancellationToken = default) => _collectDiagnostics
        ? ObserveBatchAsync("ScalarBatchCommit", diagnostics => CommitBatchCoreAsync(ticket, cancellationToken, diagnostics))
        : CommitBatchCoreAsync(ticket, cancellationToken, null);

    private async ValueTask<WccScenarioFieldBatchEditResult> CommitBatchCoreAsync(
        WccScenarioFieldBatchEditTicket ticket, CancellationToken cancellationToken,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (ticket is null || !ticket.BelongsTo(this))
            return new(WccScenarioFieldEditStatus.InvalidTicket, "This collected save ticket belongs to another editing coordinator.");
        if (!ticket.TryConsume())
            return new(WccScenarioFieldEditStatus.TicketAlreadyConsumed,
                "This collected save ticket has already been attempted. Prepare a fresh review before retrying.");
        diagnostics?.Add(WccSourceSaveCount.RequestedFiles);
        diagnostics?.Add(WccSourceSaveCount.RequestedFields, ticket.Candidate.Edits.Count);
        using (diagnostics?.Measure(WccSourceSavePhase.QueueWait))
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionBinding binding;
            using (diagnostics?.Measure(WccSourceSavePhase.SessionValidation))
            lock (_stateGate)
            {
                var refusal = CheckSession(ticket.Binding);
                if (refusal is not null) return ToBatch(refusal);
                binding = _binding!;
            }
            diagnostics?.Add(WccSourceSaveCount.SessionStates);
            var diskRefusal = RecheckBatchDiskWithDiagnostics(ticket.Disk, diagnostics);
            if (diskRefusal is not null) return ToBatch(diskRefusal);
            var review = ticket.Review;
            if (review.Proof is null)
                return new(WccScenarioFieldEditStatus.InvalidTicket, "This collected ticket has no retained source review proof.");
            WccScenarioFieldBatchEditResult? verification;
            using (diagnostics?.Measure(WccSourceSavePhase.CommitFreshness))
            {
                diagnostics?.Add(WccSourceSaveCount.CommitFreshnessChecks);
                verification = await WccScenarioFieldStaticReviewer.VerifyPreparedBatchWithDiagnosticsAsync(
                    binding.State, review.Proof, ticket.Candidate, cancellationToken, diagnostics).ConfigureAwait(false);
            }
            if (verification is not null) return verification with { Review = review };
            using var finalMeasurement = diagnostics?.Measure(WccSourceSavePhase.FinalValidation);
            lock (_stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refusal = CheckSession(ticket.Binding);
                if (refusal is not null) return ToBatch(refusal);
                WccTextAtomicReplaceResult save;
                try
                {
                    using var nativeMeasurement = diagnostics?.Measure(WccSourceSavePhase.NativeCommit);
                    diagnostics?.Add(WccSourceSaveCount.NativeCommitAttempts);
                    save = _replace(ticket.Disk, ticket.Candidate.CandidateSource.Text,
                        ticket.Candidate.CandidateSource.ContentHash);
                }
                catch (Exception exception)
                {
                    save = new(WccTextAtomicReplaceStatus.RecoveryRequired, ticket.Disk.SourcePath,
                        ErrorMessage: "The collected save boundary failed unexpectedly; inspect the retained source and recovery files: "
                                      + exception.Message);
                }
                if (save.Status == WccTextAtomicReplaceStatus.RecoveryRequired) _recovery = save;
                if (save.IsSuccess) _binding = null;
                if (save.IsSuccess) diagnostics?.Add(WccSourceSaveCount.ReplacedFiles);
                var status = save.Status switch
                {
                    WccTextAtomicReplaceStatus.Succeeded => WccScenarioFieldEditStatus.Succeeded,
                    WccTextAtomicReplaceStatus.SucceededWithCleanupWarning => WccScenarioFieldEditStatus.SucceededWithCleanupWarning,
                    WccTextAtomicReplaceStatus.FileInUse => WccScenarioFieldEditStatus.FileInUse,
                    WccTextAtomicReplaceStatus.StaleRevision or WccTextAtomicReplaceStatus.UnsafePath
                        => WccScenarioFieldEditStatus.DiskRevisionRefused,
                    WccTextAtomicReplaceStatus.RecoveryRequired => WccScenarioFieldEditStatus.RecoveryRequired,
                    _ => WccScenarioFieldEditStatus.SaveRefused
                };
                return new(status, save.ErrorMessage ?? (save.IsSuccess
                    ? "The collected local source changes were saved. Reload and refresh the indexed source before another edit."
                    : "The guarded collected source save was refused."), Candidate: ticket.Candidate, Review: review, Save: save);
            }
        }
        finally { _operations.Release(); }
    }

    private static WccScenarioFieldBatchEditResult ToBatch(WccScenarioFieldEditResult result) =>
        new(result.Status, result.Diagnostic, Save: result.Save);

    private async ValueTask<WccScenarioFieldBatchEditResult> ObserveBatchAsync(string operationName,
        Func<WccSourceSaveDiagnosticCollector, ValueTask<WccScenarioFieldBatchEditResult>> operation)
    {
        var diagnostics = new WccSourceSaveDiagnosticCollector(operationName);
        string? status = null;
        var cancelled = false;
        var failed = false;
        try
        {
            var result = await operation(diagnostics).ConfigureAwait(false);
            status = result.Status.ToString();
            return result;
        }
        catch (OperationCanceledException) { cancelled = true; throw; }
        catch { failed = true; throw; }
        finally
        {
            var snapshot = diagnostics.Complete(status, cancelled, failed);
            lock (_stateGate) _lastDiagnostics = snapshot;
        }
    }

    private static WccScenarioFieldEditResult? RecheckBatchDiskWithDiagnostics(WccTextDiskRevision expected,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.DiskRecheck);
        diagnostics?.Add(WccSourceSaveCount.DiskCaptures);
        var result = RecheckDisk(expected);
        // A successful comparison proves these captured bytes match the retained
        // revision; no extra disk read is introduced for observational metrics.
        if (result is null) diagnostics?.Add(WccSourceSaveCount.CapturedDiskBytes, expected.RawByteLength);
        return result;
    }
}
