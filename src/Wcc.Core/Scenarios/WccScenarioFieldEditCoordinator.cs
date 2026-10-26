using System.Text.Json;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// One source-backed WCC publication. Hosts invalidate before changing source,
/// selection, context, index, project access or profile. No display value grants
/// write authority; this coordinator owns the separate review/save lifetime.
/// </summary>
public enum WccScenarioFieldReviewScope { Scenario, CampaignMenu }

public sealed record WccScenarioFieldEditSessionState(
    string ProjectIdentity,
    string ProjectRootPath,
    string ProjectRevision,
    bool IsWritable,
    WccLibrarySnapshot Snapshot,
    WccPackageRevisionId RootRevisionId,
    WmlStaticSource Source,
    WmlCampaignEvaluationPlan? Plan,
    WmlEvaluationContext Context,
    WmlStaticScalarOwner Owner,
    string? ConfiguredAddonsPath = null,
    string? ProfileIdentity = null)
{
    public WccScenarioFieldReviewScope ReviewScope { get; init; }
    public WccCampaignEntryReviewIntent? EntryReview { get; init; }
}

public enum WccScenarioFieldEditStatus
{
    Prepared, Succeeded, SucceededWithCleanupWarning, InvalidRequest,
    SessionUnavailable, ReadOnly, StaleSession, DiskRevisionRefused,
    ReviewRefused, ReviewChanged, InvalidTicket, TicketAlreadyConsumed,
    FileInUse, SaveRefused, RecoveryRequired
}

public sealed record WccScenarioFieldEditResult(
    WccScenarioFieldEditStatus Status,
    string Diagnostic,
    WccScenarioFieldEditTicket? Ticket = null,
    WccScenarioFieldCandidate? Candidate = null,
    WccScenarioFieldReviewResult? Review = null,
    WccTextAtomicReplaceResult? Save = null)
{
    public bool IsPrepared => Status == WccScenarioFieldEditStatus.Prepared;
    public bool IsSuccess => Status is WccScenarioFieldEditStatus.Succeeded
        or WccScenarioFieldEditStatus.SucceededWithCleanupWarning;
    public bool AuthorizesPersistence => false;
}

/// <summary>Opaque coordinator-owned authority for exactly one attempted save.</summary>
public sealed class WccScenarioFieldEditTicket
{
    private readonly object _owner;
    private int _consumed;
    internal WccScenarioFieldEditTicket(object owner, object binding,
        WccScenarioFieldRoute route, WmlFieldOperation operation, string? requestedValue,
        WccTextDiskRevision disk, WccScenarioFieldCandidate candidate, WccScenarioFieldReviewResult review)
    {
        _owner = owner;
        Binding = binding;
        Route = route;
        Operation = operation;
        RequestedValue = requestedValue;
        Disk = disk;
        Candidate = candidate;
        Review = review;
    }

    public bool IsConsumed => Volatile.Read(ref _consumed) != 0;
    internal object Binding { get; }
    internal WccScenarioFieldRoute Route { get; }
    internal WmlFieldOperation Operation { get; }
    internal string? RequestedValue { get; }
    internal WccTextDiskRevision Disk { get; }
    internal WccScenarioFieldCandidate Candidate { get; }
    internal WccScenarioFieldReviewResult Review { get; }
    internal bool BelongsTo(object owner) => ReferenceEquals(_owner, owner);
    internal bool TryConsume() => Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
}

/// <summary>
/// WCC-only semantic review and single-attempt atomic persistence. Fresh capture
/// is required during preparation and commit. This never starts Wesnoth, writes
/// expanded WML, or changes shared macro definitions.
/// </summary>
public sealed partial class WccScenarioFieldEditCoordinator
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Func<WccTextDiskRevision, string, string, WccTextAtomicReplaceResult> _replace;
    private readonly bool _collectDiagnostics;
    private WccSourceSaveDiagnostics? _lastDiagnostics;
    private SessionBinding? _binding;
    private WccTextAtomicReplaceResult? _recovery;

    public WccScenarioFieldEditCoordinator() : this(collectDiagnostics: false) { }
    public WccScenarioFieldEditCoordinator(bool collectDiagnostics) : this(WccTextAtomicReplacer.Replace)
    { _collectDiagnostics = collectDiagnostics; }

    public WccSourceSaveDiagnostics? LastDiagnostics { get { lock (_stateGate) return _lastDiagnostics; } }

    // Deterministic tests only; the public caller cannot replace the writer.
    private WccScenarioFieldEditCoordinator(
        Func<WccTextDiskRevision, string, string, WccTextAtomicReplaceResult> replace) => _replace = replace;

    public WccTextAtomicReplaceResult? Recovery
    {
        get { lock (_stateGate) return _recovery; }
    }

    public void SetSession(WccScenarioFieldEditSessionState state)
    {
        lock (_stateGate)
        {
            _binding = null;
            ArgumentNullException.ThrowIfNull(state);
            if (string.IsNullOrWhiteSpace(state.ProjectIdentity)
                || string.IsNullOrWhiteSpace(state.ProjectRevision)
                || string.IsNullOrWhiteSpace(state.ProjectRootPath)
                || !Path.IsPathFullyQualified(state.ProjectRootPath)
                || state.Snapshot is null || state.Source is null || state.Context is null || state.Owner is null)
                throw new ArgumentException("A complete project/source/index/context/owner publication is required.", nameof(state));

            // Freeze the exact package closure rather than trusting mutable lists
            // exposed behind the library snapshot's read-only public contract.
            var manifestText = JsonSerializer.Serialize(state.Snapshot.Manifest);
            var manifest = JsonSerializer.Deserialize<WccLibraryManifest>(manifestText)
                ?? throw new ArgumentException("The indexed package manifest could not be frozen.", nameof(state));
            var frozen = state with
            {
                ProjectRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.ProjectRootPath)),
                Snapshot = WccLibrarySnapshotBuilder.Build(manifest, [])
            };
            _binding = new(frozen, state.Snapshot, WccContentHash.ComputeText(manifestText));
        }
    }

    public void InvalidateSession()
    {
        lock (_stateGate) _binding = null;
    }

    public async ValueTask<WccScenarioFieldEditResult> PrepareAsync(
        WccScenarioFieldRoute route, WmlFieldOperation operation, string? requestedValue,
        CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionBinding binding;
            lock (_stateGate)
            {
                var refusal = CheckSession();
                if (refusal is not null) return refusal;
                binding = _binding!;
            }
            if (route is null || !ReferenceEquals(route.Owner, binding.State.Owner)
                || route.ContextSemanticKey != binding.State.Context.SemanticKey
                || !SameSource(route.Source, binding.State.Source))
                return new(WccScenarioFieldEditStatus.InvalidRequest,
                    "This field route does not belong to the current exact source, selected owner and context.");

            var disk = WccTextDiskRevisionCapture.Capture(binding.State.ProjectRootPath,
                binding.State.Source.Path, binding.State.Source.Text);
            if (!disk.IsCaptured || disk.Revision is null) return DiskRefusal(disk);

            var review = await WccScenarioFieldStaticReviewer.ReviewAsync(
                binding.State, route, operation, requestedValue, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refusal = CheckSession(binding);
                if (refusal is not null) return refusal;
                if (!review.IsReadyForReview || review.Candidate is null || review.ReviewIdentity is null || review.Proof is null)
                    return new(WccScenarioFieldEditStatus.ReviewRefused, review.Diagnostic, Review: review);
                if (!SameSource(review.Candidate.Route.Source, binding.State.Source))
                    return new(WccScenarioFieldEditStatus.ReviewChanged,
                        "The reviewed source no longer matches the exact captured baseline.", Review: review);
                var changedDisk = RecheckDisk(disk.Revision);
                if (changedDisk is not null) return changedDisk;
                var ticket = new WccScenarioFieldEditTicket(this, binding, route, operation,
                    requestedValue, disk.Revision, review.Candidate, review);
                return new(WccScenarioFieldEditStatus.Prepared,
                    "The local source change has been reviewed in every declared context; Save will verify that its source and context are unchanged.",
                    ticket, review.Candidate, review);
            }
        }
        finally { _operations.Release(); }
    }

    public async ValueTask<WccScenarioFieldEditResult> CommitAsync(
        WccScenarioFieldEditTicket ticket, CancellationToken cancellationToken = default)
    {
        if (ticket is null || !ticket.BelongsTo(this))
            return new(WccScenarioFieldEditStatus.InvalidTicket, "This save ticket belongs to another editing coordinator.");
        // Spend the attempt before cancellation, queueing or any asynchronous work.
        if (!ticket.TryConsume())
            return new(WccScenarioFieldEditStatus.TicketAlreadyConsumed,
                "This save ticket has already been attempted. Prepare a fresh review before retrying.");

        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionBinding binding;
            lock (_stateGate)
            {
                var refusal = CheckSession(ticket.Binding);
                if (refusal is not null) return refusal;
                binding = _binding!;
            }
            var diskRefusal = RecheckDisk(ticket.Disk);
            if (diskRefusal is not null) return diskRefusal;
            var review = ticket.Review;
            if (review.Proof is null)
                return new(WccScenarioFieldEditStatus.InvalidTicket, "This ticket has no retained source review proof.");
            var verification = await WccScenarioFieldStaticReviewer.VerifyPreparedAsync(
                binding.State, review.Proof, ticket.Candidate, cancellationToken).ConfigureAwait(false);
            if (verification is not null) return verification with { Review = review };

            lock (_stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refusal = CheckSession(ticket.Binding);
                if (refusal is not null) return refusal;
                WccTextAtomicReplaceResult save;
                try
                {
                    save = _replace(ticket.Disk, ticket.Candidate.CandidateSource.Text,
                        ticket.Candidate.CandidateSource.ContentHash);
                }
                catch (Exception exception)
                {
                    save = new(WccTextAtomicReplaceStatus.RecoveryRequired, ticket.Disk.SourcePath,
                        ErrorMessage: "The save boundary failed unexpectedly; inspect the retained source and recovery files: "
                                      + exception.Message);
                }
                if (save.Status == WccTextAtomicReplaceStatus.RecoveryRequired) _recovery = save;
                if (save.IsSuccess) _binding = null;
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
                    ? "The reviewed local source change was saved. Reload and refresh the indexed source before another edit."
                    : "The guarded source save was refused."), Candidate: review.Candidate, Review: review, Save: save);
            }
        }
        finally { _operations.Release(); }
    }

    private WccScenarioFieldEditResult? CheckSession(object? expected = null)
    {
        if (_recovery is not null)
            return new(WccScenarioFieldEditStatus.RecoveryRequired,
                "This coordinator has an unresolved save conflict; automatic writes remain blocked.", Save: _recovery);
        if (expected is not null && !ReferenceEquals(expected, _binding))
            return new(WccScenarioFieldEditStatus.StaleSession,
                "The source, owner, index, context, access or project publication changed.");
        if (_binding is null)
            return new(WccScenarioFieldEditStatus.SessionUnavailable, "No current WCC field editing session is bound.");
        if (!_binding.State.IsWritable)
            return new(WccScenarioFieldEditStatus.ReadOnly, "The current source is read-only.");
        if (!string.Equals(_binding.ManifestHash,
                WccContentHash.ComputeText(JsonSerializer.Serialize(_binding.PublishedSnapshot.Manifest)),
                StringComparison.OrdinalIgnoreCase))
            return new(WccScenarioFieldEditStatus.StaleSession, "The indexed package or dependency records changed.");
        return null;
    }

    private static WccScenarioFieldEditResult? RecheckDisk(WccTextDiskRevision expected)
    {
        var capture = WccTextDiskRevisionCapture.Capture(expected.ProjectRootPath, expected.SourcePath, expected.Text);
        if (!capture.IsCaptured || capture.Revision is null) return DiskRefusal(capture);
        return expected.IsSameRevisionAs(capture.Revision) ? null
            : new(WccScenarioFieldEditStatus.DiskRevisionRefused,
                "The source bytes, file identity, links or directory route changed after review.");
    }

    private static WccScenarioFieldEditResult DiskRefusal(WccTextDiskRevisionCaptureResult capture) =>
        new(capture.NativeErrorCode is 32 or 33 ? WccScenarioFieldEditStatus.FileInUse
            : WccScenarioFieldEditStatus.DiskRevisionRefused,
            capture.ErrorMessage ?? "The exact source disk revision could not be verified.");

    private static bool SameSource(WmlStaticSource? left, WmlStaticSource? right) => left is not null && right is not null
        && left.IsAuthored && right.IsAuthored
        && string.Equals(left.Path, right.Path, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Text, right.Text, StringComparison.Ordinal);

    private sealed record SessionBinding(
        WccScenarioFieldEditSessionState State, WccLibrarySnapshot PublishedSnapshot, string ManifestHash);
}