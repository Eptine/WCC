namespace Wcc.Core.Projects;

public sealed partial class WccTextAtomicReplacer
{
    internal static Task<WccTextAtomicBatchReplaceResult> ReplaceResourceMutationBatchAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> binaryCreations,
        IReadOnlyList<WccBinaryAtomicBatchDeletionCandidate> deletions,
        Func<IReadOnlyList<WccAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken) => ReplaceResourceMutationBatchWithTestHooksAsync(candidates, creations,
            binaryCreations, verifyBeforeFirstWrite, commitGuard, cancellationToken, null, null, null, deletions);

    // Retain the existing deterministic seam; every workflow uses one engine.
    private static Task<WccTextAtomicBatchReplaceResult> ReplaceResourceMixedBatchWithTestHooksAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> binaryCreations,
        Func<IReadOnlyList<WccAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken, Func<int, string, string, string, int>? replaceOverride,
        Func<int, string, string, int>? createOverride, Action<int>? afterWrite) =>
        ReplaceResourceMutationBatchWithTestHooksAsync(candidates, creations, binaryCreations,
            verifyBeforeFirstWrite, commitGuard, cancellationToken, replaceOverride, createOverride, afterWrite, null);
    internal static Task<WccTextAtomicBatchReplaceResult> ReplaceResourceMixedBatchAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> binaryCreations,
        Func<IReadOnlyList<WccAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken) => ReplaceResourceMutationBatchWithTestHooksAsync(candidates, creations,
            binaryCreations, verifyBeforeFirstWrite, commitGuard, cancellationToken, null, null, null, null);

    internal static Task<WccTextAtomicBatchReplaceResult> ReplaceBatchAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        Func<IReadOnlyList<WccTextDiskRevision>, CancellationToken, Task<string?>> verifyBeforeFirstReplace,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken) => ReplaceBatchWithTestHooksAsync(candidates,
            verifyBeforeFirstReplace, commitGuard, cancellationToken, null, null);

    // Retain the original reflection-sensitive seam. Both workflows use one engine.
    private static Task<WccTextAtomicBatchReplaceResult> ReplaceBatchWithTestHooksAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        Func<IReadOnlyList<WccTextDiskRevision>, CancellationToken, Task<string?>> verifyBeforeFirstReplace,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken, Func<int, string, string, string, int>? nativeOverride,
        Action<int>? afterReplace) => ReplaceMixedBatchWithTestHooksAsync(candidates, [],
            (stages, token) => verifyBeforeFirstReplace(Array.AsReadOnly(stages.Select(stage => stage.Revision).ToArray()), token),
            commitGuard, cancellationToken, nativeOverride, null, afterReplace);

    internal static Task<WccTextAtomicBatchReplaceResult> ReplaceMixedBatchAsync(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        Func<IReadOnlyList<WccTextAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken) => ReplaceMixedBatchWithTestHooksAsync(candidates, creations,
            verifyBeforeFirstWrite, commitGuard, cancellationToken, null, null, null);
}
