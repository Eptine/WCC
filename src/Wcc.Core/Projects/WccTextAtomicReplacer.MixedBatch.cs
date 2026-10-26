using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Wcc.Core.Projects;

public sealed partial class WccTextAtomicReplacer
{
    // This is the sole batch engine. Hooks are private and cannot grant persistence.
    private static async Task<WccTextAtomicBatchReplaceResult> ReplaceMixedBatchWithTestHooksAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> replacements,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        Func<IReadOnlyList<WccTextAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken, Func<int, string, string, string, int>? replaceOverride,
        Func<int, string, string, int>? createOverride, Action<int>? afterWrite)
        => await ReplaceResourceMutationBatchWithTestHooksAsync(replacements, creations, [],
            (stages, token) => verifyBeforeFirstWrite(Array.AsReadOnly(stages.Select(stage =>
                new WccTextAtomicBatchStagedFile(stage.SourcePath, stage.TextRevision!, stage.IsCreation)).ToArray()), token),
            commitGuard, cancellationToken, replaceOverride, createOverride, afterWrite, null).ConfigureAwait(false);

    private static async Task<WccTextAtomicBatchReplaceResult> ReplaceResourceMutationBatchWithTestHooksAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> replacements,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> binaryCreations,
        Func<IReadOnlyList<WccAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken, Func<int, string, string, string, int>? replaceOverride,
        Func<int, string, string, int>? createOverride, Action<int>? afterWrite,
        IReadOnlyList<WccBinaryAtomicBatchDeletionCandidate>? deletions)
    {
        deletions ??= [];
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return new(WccTextAtomicBatchReplaceStatus.Refused,
            "Guarded source replacement and creation are implemented for Windows and Linux.");
        if (replacements is null || creations is null || binaryCreations is null
            || replacements.Count + creations.Count + binaryCreations.Count + deletions.Count is 0 or > 256
            || deletions.Any(candidate => candidate is null || !IsUsableBinaryRevision(candidate.Expected) || candidate.Expected.SourceHardLinkCount != 1)
            || replacements.Any(candidate => candidate is null || !IsUsableExpectedRevision(candidate.Expected))
            || creations.Count + binaryCreations.Count > 64
            || creations.Any(candidate => candidate is null || !IsUsableAbsence(candidate.ExpectedAbsence))
            || binaryCreations.Any(candidate => candidate is null || !IsUsableAbsence(candidate.ExpectedAbsence)
                || !IsUsableBinaryRevision(candidate.ExpectedSource)
                || candidate.CandidateByteLength > WccTextDiskRevisionCapture.MaximumResourceBytes)
            || (binaryCreations.Sum(candidate => candidate.CandidateByteLength) + deletions.Sum(candidate => candidate.Expected.RawByteLength)) > MaximumResourceBatchBytes)
            return new(WccTextAtomicBatchReplaceStatus.Refused,
                "Every source operation requires a bounded exact disk revision or captured file absence.");
        var mutations = creations.Select(creation => new BatchMutation(creation))
            .Concat(binaryCreations.Select(creation => new BatchMutation(creation)))
            .Concat(replacements.Select(replacement => new BatchMutation(replacement)))
            .Concat(deletions.Select(deletion => new BatchMutation(deletion)))
            .OrderBy(mutation => mutation.IsCreation ? 0 : mutation.IsDeletion ? 2 : 1)
            .ThenBy(mutation => mutation.SourcePath, DiskPathComparer).ToArray();
        if (mutations.Select(mutation => mutation.SourcePath).Distinct(DiskPathComparer).Count() != mutations.Length
            || replacements.Select(candidate => candidate.Expected.SourceIdentity).Distinct().Count() != replacements.Count)
            return Refused("Every replacement or creation requires a unique exact source path and file identity.");
        var root = mutations[0];
        var requiredDirectoryPaths = mutations.SelectMany(item => item.ExpectedAbsence?.RequiredDirectories ?? []).ToArray();
        if (requiredDirectoryPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 128
            || requiredDirectoryPaths.Distinct(StringComparer.Ordinal).Count() != requiredDirectoryPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            || requiredDirectoryPaths.Any(path => mutations.Any(item => string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase))))
            return Refused("The reviewed directory chain is unbounded or collides with a file destination or directory casing.");
        if (mutations.Any(mutation => !string.Equals(mutation.ProjectRootPath, root.ProjectRootPath, DiskPathComparison)
                || mutation.ProjectRootIdentity != root.ProjectRootIdentity))
            return Refused("The source operations do not share one exact project root.");
        var stages = new List<MixedBatchStage>();
        var createdDirectories = new Dictionary<string, WccOwnedDirectoryCreation>(StringComparer.OrdinalIgnoreCase);
        var attemptedDirectories = new List<string>();
        var nativeAttempted = false;
        var retainArtifacts = false;
        var cleanupWarnings = new List<(string SourcePath, string StagePath, string Problem)>();
        OperationCanceledException? cancelled = null;
        WccTextAtomicBatchReplaceResult result;
        try { result = await StageAndCommitAsync().ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!nativeAttempted)
        {
            cancelled = exception;
            result = Refused("The combined save was cancelled before writing any source.");
        }
        catch (Exception exception)
        {
            if (nativeAttempted)
            {
                retainArtifacts = true;
                result = Recovery("The combined source write could not be verified. Recovery artifacts were retained: " + exception.Message);
            }
            else result = Refused("The combined source staging was refused: " + exception.Message);
        }
        finally
        {
            foreach (var stage in stages)
            {
                if (!retainArtifacts && !stage.Written)
                {
                    if (stage.Identity is null || stage.Bytes is null)
                    {
                        if (File.Exists(stage.StagePath)) cleanupWarnings.Add((stage.Mutation.SourcePath, stage.StagePath,
                            "The staging pathname exists without verified ownership and was retained."));
                    }
                    else if (TryDeleteOwnedFile(stage.StagePath, stage.Identity, stage.Bytes.LongLength,
                                 HashBytes(stage.Bytes), 1) is { } problem)
                        cleanupWarnings.Add((stage.Mutation.SourcePath, stage.StagePath, problem));
                }
                stage.Lease?.Dispose();
                stage.PublicationLease?.Dispose();
            }
            foreach (var directory in createdDirectories.Values.Reverse()) directory.Dispose();
        }
        if (cleanupWarnings.Count == 0)
        {
            if (cancelled is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancelled).Throw();
            return result;
        }
        return new(result.Status, result.Diagnostic + " Some staging artifacts were retained: "
            + string.Join(" ", cleanupWarnings.Select(warning => warning.StagePath + ": " + warning.Problem)),
            result.Files.Select(file =>
            {
                var warning = cleanupWarnings.FirstOrDefault(warning => warning.SourcePath == file.SourcePath);
                return warning.StagePath is null ? file : file with
                {
                    Save = (file.Save ?? new WccTextAtomicReplaceResult(WccTextAtomicReplaceStatus.IoFailure, file.SourcePath)) with
                    { RecoveryCandidatePath = warning.StagePath, ErrorMessage = warning.Problem },
                    Diagnostic = file.Diagnostic + " The staging artifact was retained: " + warning.StagePath
                };
            })) { CreatedDirectories = result.CreatedDirectories };

        async Task<WccTextAtomicBatchReplaceResult> StageAndCommitAsync()
        {
            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!mutation.IsBinary && (mutation.CandidateText is null || !ContainsOnlyValidUnicode(mutation.CandidateText)
                    || mutation.IsCreation && mutation.CandidateText.Length > 4 * 1024 * 1024
                    || !IsSha256(mutation.CandidateTextSha256) || HashText(mutation.CandidateText) != mutation.CandidateTextSha256
                    || mutation.Replacement is { } replacement && mutation.CandidateText == replacement.Expected.Text))
                    return Refused("A source candidate is invalid, unchanged or does not match its reviewed hash.");
                if (RecaptureMutation(mutation) is { } refusal) return Refused(refusal.ErrorMessage ?? "A source route changed before staging.", refusal);
                var stagePath = Path.Combine(mutation.SourceParentPath, $".wcc-stage-{Guid.NewGuid():N}.tmp");
                var stage = new MixedBatchStage(mutation, stagePath,
                    mutation.IsCreation ? null : mutation.IsDeletion ? (OperatingSystem.IsLinux() ? Path.Combine(mutation.SourceParentPath, $".wcc-removed-{Guid.NewGuid():N}.tmp") : stagePath) : OperatingSystem.IsLinux() ? stagePath
                        : Path.Combine(mutation.SourceParentPath, $".wcc-backup-{Guid.NewGuid():N}.tmp"));
                stages.Add(stage);
                stage.Lease = mutation.IsCreation
                    ? WccTextDiskRevisionCapture.AcquireAbsenceRouteLease(mutation.ExpectedAbsence!)
                    : mutation.Deletion is { } deletion ? WccTextDiskRevisionCapture.AcquireBinaryRouteLease(deletion.Expected) : WccTextDiskRevisionCapture.AcquireRouteLease(mutation.Replacement!.Expected);
                stage.Bytes = mutation.Deletion?.Expected.CopyBytes() ?? mutation.BinaryCreation?.CopyBytes() ?? Encode(mutation.CandidateText!,
                    mutation.Replacement?.Expected.EncodingKind ?? WccTextDiskEncodingKind.Utf8WithoutBom);
                if (!mutation.IsBinary && mutation.IsCreation && stage.Bytes.LongLength > 4L * 1024 * 1024)
                    return Refused("A new text file exceeds the 4 MiB checked creation limit.");
                if (mutation.IsBinary && HashBytes(stage.Bytes) != (mutation.Deletion?.Expected.RawByteSha256 ?? mutation.BinaryCreation!.CandidateByteSha256))
                    return Refused("The resource bytes differ from their exact reviewed original.");
                if ((binaryCreations.Count != 0 || deletions.Count != 0) && stages.Sum(item => item.Bytes?.LongLength ?? 0) > MaximumResourceBatchBytes)
                    return Refused("The combined resource transaction exceeds its 64 MiB staging limit.");
                if (OperatingSystem.IsLinux()) WriteExclusiveLinux(stage.Lease, stage.StagePath, stage.Bytes, out stage.Identity);
                else WriteExclusive(stage.StagePath, stage.Bytes, out stage.Identity);
                if (!CaptureMixedStage(stage))
                    return Refused("A flushed staging file could not be bound to the exact reviewed candidate.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var observations = Array.AsReadOnly(stages.Select(stage => stage.Mutation.IsBinary
                ? new WccAtomicBatchStagedFile(stage.Mutation.SourcePath, stage.BinaryRevision!, stage.Mutation.IsDeletion)
                : new WccAtomicBatchStagedFile(stage.Mutation.SourcePath, stage.Revision!, stage.Mutation.IsCreation)).ToArray());
            if (await verifyBeforeFirstWrite(observations, cancellationToken).ConfigureAwait(false) is { } closureRefusal)
                return Refused(closureRefusal);
            cancellationToken.ThrowIfCancellationRequested();
            return commitGuard(Commit);
        }

        WccTextAtomicBatchReplaceResult Commit()
        {
            var locks = mutations.Select(mutation => mutation.SourcePath).Order(DiskPathComparer)
                .Select(path => SourceLocks.GetOrAdd(path, static _ => new object())).ToArray();
            var acquired = 0;
            try
            {
                foreach (var gate in locks) { Monitor.Enter(gate); acquired++; }
                cancellationToken.ThrowIfCancellationRequested();
                // The asynchronous review cannot lend authority to changed absence, baseline or stages.
                foreach (var stage in stages)
                {
                    if (RecaptureMutation(stage.Mutation) is { } refusal)
                        return Refused(refusal.ErrorMessage ?? "A source changed before the first write.", refusal);
                    if (!IsSameMixedStage(stage))
                        return Refused("A staging file changed after the combined source review.");
                }
                for (var index = 0; index < stages.Count; index++)
                {
                    var stage = stages[index];
                    if (stage.Mutation.ExpectedAbsence is { RequiredDirectories.Count: > 0 } expectedAbsence)
                    {
                        foreach (var path in expectedAbsence.RequiredDirectories)
                        {
                            if (createdDirectories.TryGetValue(path, out var owned))
                            {
                                var observed = WccTextDiskRevisionCapture.CaptureAbsence(stage.Mutation.ProjectRootPath,
                                    Path.Combine(path, ".wcc-directory-check-" + Guid.NewGuid().ToString("N")));
                                if (!observed.IsCaptured || observed.Absence!.SourceParentIdentity != owned.Identity)
                                { retainArtifacts = true; return Recovery("An owned new directory route changed during the combined save."); }
                                continue;
                            }
                            var absent = WccTextDiskRevisionCapture.CaptureAbsence(stage.Mutation.ProjectRootPath, path);
                            if (!absent.IsCaptured || absent.Absence is null)
                            {
                                if (createdDirectories.Count != 0 || stages.Any(item => item.Written))
                                { retainArtifacts = true; return Recovery(absent.ErrorMessage ?? "A reviewed new directory is no longer absent."); }
                                return Refused(absent.ErrorMessage ?? "A reviewed new directory is no longer absent.");
                            }
                            attemptedDirectories.Add(path);
                            nativeAttempted = true;
                            try { createdDirectories.Add(path, WccTextDiskRevisionCapture.CreatePinnedDirectory(absent.Absence)); }
                            catch (Win32Exception exception) when (createdDirectories.Count == 0 && !stages.Any(item => item.Written))
                            {
                                nativeAttempted = false;
                                return Refused("The reviewed directory could not be exclusively created: " + exception.Message);
                            }
                        }
                        var publication = WccTextDiskRevisionCapture.CaptureAbsence(stage.Mutation.ProjectRootPath, stage.Mutation.SourcePath);
                        if (!publication.IsCaptured || publication.Absence is null
                            || publication.Absence.SourceParentIdentity != createdDirectories[expectedAbsence.DestinationParentPath].Identity)
                        { retainArtifacts = true; return Recovery(publication.ErrorMessage ?? "The new asset directory does not match its owned identity."); }
                        stage.PublicationAbsence = publication.Absence;
                        stage.PublicationLease = WccTextDiskRevisionCapture.AcquireAbsenceRouteLease(publication.Absence);
                    }
                    if (RecaptureMutation(stage.Mutation, stage.PublicationAbsence) is { } changed)
                    {
                        if (createdDirectories.Count != 0 || stages.Any(item => item.Written))
                        { retainArtifacts = true; return Recovery(changed.ErrorMessage ?? "A later source changed during the combined save."); }
                        return Refused(changed.ErrorMessage ?? "A source changed before saving.", changed);
                    }
                    if (!IsSameMixedStage(stage))
                    {
                        if (createdDirectories.Count != 0 || stages.Any(item => item.Written))
                        { retainArtifacts = true; return Recovery("A later staged candidate changed during the combined save."); }
                        return Refused("A staged candidate changed before saving.");
                    }
                    nativeAttempted = true;
                    var error = stage.Mutation.IsDeletion ? DeleteNativeChecked(stage) : stage.Mutation.IsCreation
                        ? (createOverride is null ? OperatingSystem.IsLinux()
                                ? CreateLinux(stage.StagePath, stage.Mutation.SourcePath, stage.Identity!, stage.Lease!, stage.PublicationLease ?? stage.Lease!, stage.Bytes!)
                                : CreateNative(stage.StagePath, stage.Mutation.SourcePath)
                            : createOverride(index, stage.Mutation.SourcePath, stage.StagePath))
                        : (replaceOverride is null ? OperatingSystem.IsLinux()
                                ? ReplaceLinux(stage.Mutation.Replacement!.Expected, stage.StagePath, stage.Identity!, stage.Lease!, stage.Bytes!)
                                : ReplaceNative(stage.Mutation.SourcePath, stage.StagePath, stage.BackupPath!)
                            : replaceOverride(index, stage.Mutation.SourcePath, stage.StagePath, stage.BackupPath!));
                    if (error != 0)
                    {
                        if (error == ErrorUnableToMoveReplacement2 || (!stage.Mutation.IsDeletion || OperatingSystem.IsLinux()) && stage.BackupPath is not null && File.Exists(stage.BackupPath)
                            || !File.Exists(stage.StagePath) || createdDirectories.Count != 0 || stages.Any(item => item.Written))
                        { retainArtifacts = true; return Recovery(new Win32Exception(error, "A source operation was incomplete during the combined save.").Message); }
                        nativeAttempted = false;
                        return Refused(new Win32Exception(error, "The platform could not publish the source candidate.").Message,
                            new(IsNativeFileInUseError(error) ? WccTextAtomicReplaceStatus.FileInUse
                                : WccTextAtomicReplaceStatus.IoFailure, stage.Mutation.SourcePath));
                    }
                    stage.Written = true;
                    if (OperatingSystem.IsLinux())
                    {
                        FlushAndVerifyLinuxRoute(stage.PublicationLease ?? stage.Lease!);
                        if (stage.PublicationLease is not null) FlushAndVerifyLinuxRoute(stage.Lease!);
                    }
                    afterWrite?.Invoke(index);
                }
                foreach (var stage in stages)
                {
                    var mutation = stage.Mutation;
                    var baselineVerified = mutation.IsCreation || mutation.IsDeletion && IsExactDeletedBackup(stage) || !mutation.IsDeletion && IsExactDisplacedRevision(
                        WccTextDiskRevisionCapture.Capture(mutation.ProjectRootPath, stage.BackupPath!, mutation.Replacement!.Expected.Text),
                        mutation.Replacement.Expected);
                    if (!baselineVerified || !IsExactMixedDestination(stage)
                        || mutation.IsCreation && (OperatingSystem.IsLinux()
                            ? HasCaseEquivalentOtherEntryLinux(stage) : HasCaseEquivalentOtherEntry(mutation)))
                    { retainArtifacts = true; return Recovery("A combined destination, displaced baseline or new-file namespace changed; every remaining recovery artifact was retained."); }
                }
                var outcomes = stages.Select(stage =>
                {
                    var mutation = stage.Mutation;
                    var expected = mutation.Replacement?.Expected;
                    var warning = mutation.IsDeletion ? CleanupDeletedBackup(stage) : expected is null ? null : TryDeleteOwnedFile(stage.BackupPath!, expected.SourceIdentity,
                        expected.RawByteLength, expected.RawByteSha256, expected.SourceHardLinkCount);
                    var save = new WccTextAtomicReplaceResult(warning is null ? WccTextAtomicReplaceStatus.Succeeded
                            : WccTextAtomicReplaceStatus.SucceededWithCleanupWarning, mutation.SourcePath,
                        warning is null ? null : stage.BackupPath, warning);
                    return new WccTextAtomicBatchFileOutcome(mutation.SourcePath, save, !mutation.IsCreation && !mutation.IsDeletion,
                        warning ?? (mutation.IsDeletion ? "The reviewed owned file was removed." : mutation.IsCreation ? "The reviewed source candidate was created without replacement." : "The reviewed source candidate was saved."))
                        { WasCreated = mutation.IsCreation, WasDeleted = mutation.IsDeletion };
                }).ToArray();
                return new(outcomes.Any(outcome => outcome.Save!.Status == WccTextAtomicReplaceStatus.SucceededWithCleanupWarning)
                    ? WccTextAtomicBatchReplaceStatus.SucceededWithCleanupWarning : WccTextAtomicBatchReplaceStatus.Succeeded,
                    "All reviewed source candidates were saved.", outcomes)
                    { CreatedDirectories = Array.AsReadOnly(createdDirectories.Keys.ToArray()) };
            }
            finally { for (var index = acquired - 1; index >= 0; index--) Monitor.Exit(locks[index]); }
        }

        WccTextAtomicBatchReplaceResult Recovery(string diagnostic) => new(WccTextAtomicBatchReplaceStatus.RecoveryRequired,
            diagnostic + (attemptedDirectories.Count == 0 ? "" : " Reviewed directory paths retained for recovery: "
                + string.Join(", ", attemptedDirectories)), stages.Select(stage => new WccTextAtomicBatchFileOutcome(stage.Mutation.SourcePath,
                new(WccTextAtomicReplaceStatus.RecoveryRequired, stage.Mutation.SourcePath,
                    stage.BackupPath is not null && (!OperatingSystem.IsLinux() || stage.Written) && File.Exists(stage.BackupPath) ? stage.BackupPath : null, diagnostic)
                { RecoveryCandidatePath = stage.Written ? null : stage.StagePath }, stage.Written && !stage.Mutation.IsCreation && !stage.Mutation.IsDeletion, diagnostic)
                { WasCreated = stage.Written && stage.Mutation.IsCreation, WasDeleted = stage.Written && stage.Mutation.IsDeletion }))
                { CreatedDirectories = Array.AsReadOnly(createdDirectories.Keys.ToArray()) };
        WccTextAtomicBatchReplaceResult Refused(string diagnostic, WccTextAtomicReplaceResult? refusal = null) =>
            new(WccTextAtomicBatchReplaceStatus.Refused, diagnostic, mutations.Select(mutation => new WccTextAtomicBatchFileOutcome(
                mutation.SourcePath, refusal?.SourcePath == mutation.SourcePath ? refusal : null, false, diagnostic)));
    }

    private static bool IsUsableAbsence(WccTextDiskFileAbsence? absence) => absence is not null
        && Path.IsPathFullyQualified(absence.ProjectRootPath) && Path.IsPathFullyQualified(absence.SourceParentPath)
        && Path.IsPathFullyQualified(absence.SourcePath)
        && (absence.RequiredDirectories.Count == 0
            ? string.Equals(absence.DestinationParentPath, absence.SourceParentPath, DiskPathComparison)
            : absence.RequiredDirectories.Count <= 16
                && absence.RequiredDirectories[0] == Path.Combine(absence.SourceParentPath, Path.GetFileName(absence.RequiredDirectories[0]))
                && absence.RequiredDirectories[^1] == absence.DestinationParentPath
                && absence.RequiredDirectories.Select((path, index) => index == 0
                    || Path.GetDirectoryName(path) == absence.RequiredDirectories[index - 1]).All(match => match))
        && IsSha256(absence.RouteIdentitySha256) && !absence.ProjectRootIdentity.IsEmpty && !absence.SourceParentIdentity.IsEmpty;

    private const long MaximumResourceBatchBytes = 64L * 1024 * 1024;

    private static bool IsUsableBinaryRevision(WccBinaryDiskRevision? revision) => revision is not null
        && Path.IsPathFullyQualified(revision.ProjectRootPath) && Path.IsPathFullyQualified(revision.SourceParentPath)
        && Path.IsPathFullyQualified(revision.SourcePath)
        && string.Equals(Path.GetDirectoryName(revision.SourcePath), revision.SourceParentPath, DiskPathComparison)
        && revision.RawByteLength is >= 0 and <= WccTextDiskRevisionCapture.MaximumResourceBytes
        && IsSha256(revision.RawByteSha256) && IsSha256(revision.RouteIdentitySha256)
        && !revision.ProjectRootIdentity.IsEmpty && !revision.SourceParentIdentity.IsEmpty
        && !revision.SourceIdentity.IsEmpty && revision.SourceHardLinkCount > 0;

    private static WccTextAtomicReplaceResult? RecaptureMutation(BatchMutation mutation, WccTextDiskFileAbsence? publicationAbsence = null)
    {
        if (mutation.Replacement is { } replacement) return RecaptureExpected(replacement.Expected);
        if (mutation.Deletion is { } deletion)
        {
            var original = deletion.Expected;
            var observedDeletion = WccTextDiskRevisionCapture.CaptureBinary(original.ProjectRootPath, original.SourcePath);
            return observedDeletion.IsCaptured && observedDeletion.Revision is { } revision && original.IsSameRevisionAs(revision) ? null
                : Failure(WccTextAtomicReplaceStatus.StaleRevision, original.SourcePath,
                    observedDeletion.ErrorMessage ?? "The owned file changed before removal.");
        }
        if (mutation.BinaryCreation is { } binary)
        {
            var source = binary.ExpectedSource;
            var captured = WccTextDiskRevisionCapture.CaptureBinary(source.ProjectRootPath, source.SourcePath);
            if (!captured.IsCaptured || captured.Revision is null || !source.IsSameRevisionAs(captured.Revision))
                return Failure(WccTextAtomicReplaceStatus.StaleRevision, mutation.SourcePath,
                    captured.ErrorMessage ?? "The original resource route, identity, link count or bytes changed.");
        }
        var expected = publicationAbsence ?? mutation.ExpectedAbsence!;
        var current = expected.RequiredDirectories.Count == 0
            ? WccTextDiskRevisionCapture.CaptureAbsence(expected.ProjectRootPath, expected.SourcePath)
            : WccTextDiskRevisionCapture.CaptureCreationAbsence(expected.ProjectRootPath, expected.SourcePath);
        return current.IsCaptured && current.Absence is not null && expected.IsSameAbsenceAs(current.Absence) ? null
            : Failure(WccTextAtomicReplaceStatus.StaleRevision, expected.SourcePath,
                current.ErrorMessage ?? "The new-file parent route or missing filename changed.");
    }

    private static bool IsExactMixedStage(WccTextDiskRevisionCaptureResult captured, BatchMutation mutation, long length) =>
        captured.IsCaptured && captured.Revision is { } revision
        && revision.EncodingKind == (mutation.Replacement?.Expected.EncodingKind ?? WccTextDiskEncodingKind.Utf8WithoutBom)
        && revision.RawByteLength == length && revision.DecodedTextSha256 == mutation.CandidateTextSha256
        && revision.ProjectRootIdentity == mutation.ProjectRootIdentity && revision.SourceParentIdentity == mutation.SourceParentIdentity
        && revision.RouteIdentitySha256 == mutation.RouteIdentitySha256 && revision.SourceHardLinkCount == 1;

    private static bool CaptureMixedStage(MixedBatchStage stage)
    {
        var mutation = stage.Mutation;
        if (mutation.IsBinary)
        {
            var captured = WccTextDiskRevisionCapture.CaptureBinary(mutation.ProjectRootPath, stage.StagePath);
            if (!IsExactBinaryStage(captured, mutation) || captured.Revision!.SourceIdentity != stage.Identity) return false;
            stage.BinaryRevision = captured.Revision;
            return true;
        }
        var text = WccTextDiskRevisionCapture.Capture(mutation.ProjectRootPath, stage.StagePath, mutation.CandidateText);
        if (!IsExactMixedStage(text, mutation, stage.Bytes!.LongLength) || text.Revision!.SourceIdentity != stage.Identity) return false;
        stage.Revision = text.Revision;
        return true;
    }

    private static bool IsSameMixedStage(MixedBatchStage stage)
    {
        if (stage.Mutation.IsBinary)
        {
            var current = WccTextDiskRevisionCapture.CaptureBinary(stage.Mutation.ProjectRootPath, stage.StagePath);
            return current.IsCaptured && current.Revision is not null && stage.BinaryRevision!.IsSameRevisionAs(current.Revision);
        }
        var text = WccTextDiskRevisionCapture.Capture(stage.Mutation.ProjectRootPath, stage.StagePath, stage.Mutation.CandidateText);
        return text.IsCaptured && text.Revision is not null && stage.Revision!.IsSameRevisionAs(text.Revision);
    }

    private static bool IsExactBinaryStage(WccBinaryDiskRevisionCaptureResult captured, BatchMutation mutation) =>
        captured.IsCaptured && captured.Revision is { } revision
        && revision.RawByteLength == (mutation.Deletion?.Expected.RawByteLength ?? mutation.BinaryCreation!.CandidateByteLength)
        && revision.RawByteSha256 == (mutation.Deletion?.Expected.RawByteSha256 ?? mutation.BinaryCreation!.CandidateByteSha256)
        && revision.ProjectRootIdentity == mutation.ProjectRootIdentity && revision.SourceParentIdentity == mutation.SourceParentIdentity
        && revision.RouteIdentitySha256 == mutation.RouteIdentitySha256 && revision.SourceHardLinkCount == 1;

    private static bool IsExactMixedDestination(MixedBatchStage stage)
    {
        var mutation = stage.Mutation;
        if (mutation.IsDeletion) return WccTextDiskRevisionCapture.CaptureAbsence(mutation.ProjectRootPath, mutation.SourcePath).IsCaptured;
        if (stage.PublicationAbsence is { } publication)
        {
            if (mutation.IsBinary)
            {
                var saved = WccTextDiskRevisionCapture.CaptureBinary(mutation.ProjectRootPath, mutation.SourcePath);
                return saved.IsCaptured && saved.Revision is { } binary
                    && binary.SourceIdentity == stage.BinaryRevision!.SourceIdentity && binary.SourceHardLinkCount == 1
                    && binary.RawByteSha256 == mutation.BinaryCreation!.CandidateByteSha256
                    && binary.RawByteLength == mutation.BinaryCreation.CandidateByteLength
                    && binary.ProjectRootIdentity == publication.ProjectRootIdentity
                    && binary.SourceParentIdentity == publication.SourceParentIdentity
                    && binary.RouteIdentitySha256 == publication.RouteIdentitySha256;
            }
            var savedText = WccTextDiskRevisionCapture.Capture(mutation.ProjectRootPath, mutation.SourcePath, mutation.CandidateText);
            return savedText.IsCaptured && savedText.Revision is { } published
                && published.SourceIdentity == stage.Revision!.SourceIdentity && published.SourceHardLinkCount == 1
                && published.RawByteSha256 == stage.Revision.RawByteSha256 && published.DecodedTextSha256 == mutation.CandidateTextSha256
                && published.ProjectRootIdentity == publication.ProjectRootIdentity
                && published.SourceParentIdentity == publication.SourceParentIdentity && published.RouteIdentitySha256 == publication.RouteIdentitySha256;
        }
        if (mutation.IsBinary)
        {
            var captured = WccTextDiskRevisionCapture.CaptureBinary(mutation.ProjectRootPath, mutation.SourcePath);
            return IsExactBinaryStage(captured, mutation) && captured.Revision!.SourceIdentity == stage.BinaryRevision!.SourceIdentity;
        }
        var text = WccTextDiskRevisionCapture.Capture(mutation.ProjectRootPath, mutation.SourcePath, mutation.CandidateText);
        return IsExactMixedStage(text, mutation, stage.Bytes!.LongLength)
            && text.Revision!.SourceIdentity == stage.Revision!.SourceIdentity && text.Revision.RawByteSha256 == stage.Revision.RawByteSha256;
    }

    private static bool HasCaseEquivalentOtherEntry(BatchMutation mutation) =>
        Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(mutation.SourcePath)!).Any(path =>
            string.Equals(Path.GetFileName(path), Path.GetFileName(mutation.SourcePath), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFileName(path), Path.GetFileName(mutation.SourcePath), StringComparison.Ordinal));

    private static int CreateNative(string stagePath, string destinationPath) =>
        MoveFileExWindows(stagePath, destinationPath, 0) ? 0 : Marshal.GetLastWin32Error();

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExWindows(string existingFileName, string newFileName, uint flags);

    private sealed class BatchMutation
    {
        internal BatchMutation(WccTextAtomicBatchCandidate replacement) { Replacement = replacement; }
        internal BatchMutation(WccTextAtomicBatchCreationCandidate creation) { Creation = creation; }
        internal BatchMutation(WccBinaryAtomicBatchCreationCandidate creation) { BinaryCreation = creation; }
        internal WccTextAtomicBatchCandidate? Replacement { get; }
        internal WccTextAtomicBatchCreationCandidate? Creation { get; }
        internal BatchMutation(WccBinaryAtomicBatchDeletionCandidate deletion) { Deletion = deletion; }
        internal WccBinaryAtomicBatchDeletionCandidate? Deletion { get; }
        internal bool IsDeletion => Deletion is not null;
        internal WccBinaryAtomicBatchCreationCandidate? BinaryCreation { get; }
        internal bool IsBinary => BinaryCreation is not null || Deletion is not null;
        internal bool IsCreation => ExpectedAbsence is not null;
        internal WccTextDiskFileAbsence? ExpectedAbsence => Creation?.ExpectedAbsence ?? BinaryCreation?.ExpectedAbsence;
        internal string ProjectRootPath => Replacement?.Expected.ProjectRootPath ?? Deletion?.Expected.ProjectRootPath ?? ExpectedAbsence!.ProjectRootPath;
        internal string SourceParentPath => Replacement?.Expected.SourceParentPath ?? Deletion?.Expected.SourceParentPath ?? ExpectedAbsence!.SourceParentPath;
        internal string SourcePath => Replacement?.Expected.SourcePath ?? Deletion?.Expected.SourcePath ?? ExpectedAbsence!.SourcePath;
        internal WccWindowsFileIdentity ProjectRootIdentity => Replacement?.Expected.ProjectRootIdentity ?? Deletion?.Expected.ProjectRootIdentity ?? ExpectedAbsence!.ProjectRootIdentity;
        internal WccWindowsFileIdentity SourceParentIdentity => Replacement?.Expected.SourceParentIdentity ?? Deletion?.Expected.SourceParentIdentity ?? ExpectedAbsence!.SourceParentIdentity;
        internal string RouteIdentitySha256 => Replacement?.Expected.RouteIdentitySha256 ?? Deletion?.Expected.RouteIdentitySha256 ?? ExpectedAbsence!.RouteIdentitySha256;
        internal string? CandidateText => Replacement?.CandidateText ?? Creation?.CandidateText;
        internal string? CandidateTextSha256 => Replacement?.CandidateTextSha256 ?? Creation?.CandidateTextSha256;
    }

    private sealed class MixedBatchStage(BatchMutation mutation, string stagePath, string? backupPath)
    {
        internal BatchMutation Mutation { get; } = mutation;
        internal string StagePath { get; } = stagePath;
        internal string? BackupPath { get; } = backupPath;
        internal WccTextDiskRouteLease? Lease;
        internal WccTextDiskRouteLease? PublicationLease;
        internal WccTextDiskFileAbsence? PublicationAbsence;
        internal WccWindowsFileIdentity? Identity;
        internal WccTextDiskRevision? Revision;
        internal WccBinaryDiskRevision? BinaryRevision;
        internal byte[]? Bytes;
        internal bool Written;
    }
}
