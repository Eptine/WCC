using Wcc.Core.Projects;

namespace Wcc.Core.Library;

public static partial class WccStaticSourceCapture
{
    internal static Task<WccStaticSourceCaptureResult> CaptureForStagedUnitTypeCreationAsync(
        WccLibrarySnapshot snapshot, WccPackageRevisionId revision, string entry,
        IReadOnlyList<WccTextDiskRevision> ownedStages, IReadOnlyList<WccBinaryDiskRevision> ownedBinaryStages,
        CancellationToken cancellationToken, string? configuredAddonsPath)
    {
        var stages = ownedStages.ToArray(); var binaries = ownedBinaryStages.ToArray();
        var entryPath = Path.GetFullPath(entry);
        return Task.Run(() => Capture(snapshot, revision, entryPath, [], configuredAddonsPath,
            cancellationToken, stages, true, binaries), cancellationToken);
    }

    // Explicit creator-only environment. Other editor/writer captures retain their ordinary preludes.
    internal static Task<WccStaticSourceCaptureResult> CaptureForStagedUnitTypeCreationAsync(
        WccLibrarySnapshot snapshot, WccPackageRevisionId revision, string entry,
        IReadOnlyList<WccTextDiskRevision> ownedStages, CancellationToken cancellationToken, string? configuredAddonsPath)
    {
        var stages = ownedStages.ToArray();
        var entryPath = Path.GetFullPath(entry);
        return Task.Run(() => Capture(snapshot, revision, entryPath, [], configuredAddonsPath,
            cancellationToken, stages, includeCoreUnits: true), cancellationToken);
    }
}
