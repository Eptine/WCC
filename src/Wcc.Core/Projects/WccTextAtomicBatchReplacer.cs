namespace Wcc.Core.Projects;

internal sealed record WccTextAtomicBatchCandidate(WccTextDiskRevision Expected,
    string CandidateText, string CandidateTextSha256);

internal sealed record WccTextAtomicBatchCreationCandidate(WccTextDiskFileAbsence ExpectedAbsence,
    string CandidateText, string CandidateTextSha256);

internal sealed record WccBinaryAtomicBatchDeletionCandidate(WccBinaryDiskRevision Expected);

internal sealed record WccTextAtomicBatchStagedFile(string SourcePath, WccTextDiskRevision Revision, bool IsCreation);

internal sealed class WccBinaryAtomicBatchCreationCandidate
{
    private readonly byte[] _bytes;
    public WccBinaryAtomicBatchCreationCandidate(WccTextDiskFileAbsence expectedAbsence, WccBinaryDiskRevision expectedSource)
    {
        ExpectedAbsence = expectedAbsence ?? throw new ArgumentNullException(nameof(expectedAbsence));
        ExpectedSource = expectedSource ?? throw new ArgumentNullException(nameof(expectedSource));
        _bytes = expectedSource.CopyBytes();
    }
    internal WccTextDiskFileAbsence ExpectedAbsence { get; }
    internal WccBinaryDiskRevision ExpectedSource { get; }
    internal long CandidateByteLength => _bytes.LongLength;
    internal string CandidateByteSha256 => ExpectedSource.RawByteSha256;
    internal byte[] CopyBytes() => (byte[])_bytes.Clone();
}

// Disjoint observations keep arbitrary bytes out of the text evaluator/capture path.
internal sealed class WccAtomicBatchStagedFile
{
    internal WccAtomicBatchStagedFile(string sourcePath, WccTextDiskRevision revision, bool isCreation)
    { SourcePath = sourcePath; TextRevision = revision; IsCreation = isCreation; }
    internal WccAtomicBatchStagedFile(string sourcePath, WccBinaryDiskRevision revision, bool isDeletion)
    { SourcePath = sourcePath; BinaryRevision = revision; IsCreation = !isDeletion; IsDeletion = isDeletion; }
    internal WccAtomicBatchStagedFile(string sourcePath, WccBinaryDiskRevision revision)
    { SourcePath = sourcePath; BinaryRevision = revision; IsCreation = true; }
    internal bool IsDeletion { get; init; }
    internal string SourcePath { get; }
    internal bool IsCreation { get; }
    internal WccTextDiskRevision? TextRevision { get; }
    internal WccBinaryDiskRevision? BinaryRevision { get; }
}

internal enum WccTextAtomicBatchReplaceStatus { Succeeded, SucceededWithCleanupWarning, Refused, RecoveryRequired }

internal sealed record WccTextAtomicBatchFileOutcome(string SourcePath, WccTextAtomicReplaceResult? Save,
    bool WasReplaced, string Diagnostic)
{
    internal bool WasCreated { get; init; }
    internal bool WasDeleted { get; init; }
    internal bool WasWritten => WasReplaced || WasCreated || WasDeleted;
}

internal sealed class WccTextAtomicBatchReplaceResult
{
    internal WccTextAtomicBatchReplaceResult(WccTextAtomicBatchReplaceStatus status, string diagnostic,
        IEnumerable<WccTextAtomicBatchFileOutcome>? files = null)
    { Status = status; Diagnostic = diagnostic; Files = Array.AsReadOnly((files ?? []).ToArray()); }
    internal WccTextAtomicBatchReplaceStatus Status { get; }
    internal string Diagnostic { get; }
    internal IReadOnlyList<WccTextAtomicBatchFileOutcome> Files { get; }
    internal IReadOnlyList<string> CreatedDirectories { get; init; } = [];
    internal IReadOnlyList<string> RemovedDirectories { get; init; } = [];
    internal bool IsSuccess => Status is WccTextAtomicBatchReplaceStatus.Succeeded
        or WccTextAtomicBatchReplaceStatus.SucceededWithCleanupWarning;
}

internal static class WccTextAtomicBatchReplacer
{
    internal static Task<WccTextAtomicBatchReplaceResult> ReplaceAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> binaryCreations,
        IReadOnlyList<WccBinaryAtomicBatchDeletionCandidate> deletions,
        Func<IReadOnlyList<WccAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken) => WccTextAtomicReplacer.ReplaceResourceMutationBatchAsync(
            candidates, creations, binaryCreations, deletions, verifyBeforeFirstWrite, commitGuard, cancellationToken);
    internal static Task<WccTextAtomicBatchReplaceResult> ReplaceAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> binaryCreations,
        Func<IReadOnlyList<WccAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken) => WccTextAtomicReplacer.ReplaceResourceMixedBatchAsync(
            candidates, creations, binaryCreations, verifyBeforeFirstWrite, commitGuard, cancellationToken);

    internal static Task<WccTextAtomicBatchReplaceResult> ReplaceAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        Func<IReadOnlyList<WccTextDiskRevision>, CancellationToken, Task<string?>> verifyBeforeFirstReplace,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken) => WccTextAtomicReplacer.ReplaceBatchAsync(
            candidates, verifyBeforeFirstReplace, commitGuard, cancellationToken);

    internal static Task<WccTextAtomicBatchReplaceResult> ReplaceAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        Func<IReadOnlyList<WccTextAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken) => WccTextAtomicReplacer.ReplaceMixedBatchAsync(
            candidates, creations, verifyBeforeFirstWrite, commitGuard, cancellationToken);
}
