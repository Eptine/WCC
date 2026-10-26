using Wcc.Core.Projects;

namespace Wcc.Core.Library;

public static partial class WccStaticSourceCapture
{
    // Only the reviewed mixed writer supplies complete exact stage evidence; public captures cannot hide files.
    internal static Task<WccStaticSourceCaptureResult> CaptureForStagedStoryAsync(WccLibrarySnapshot snapshot,
        WccPackageRevisionId revision, string entry, IReadOnlyList<WccTextDiskRevision> textStages,
        IReadOnlyList<WccBinaryDiskRevision> binaryStages, CancellationToken cancellationToken, string? configuredAddonsPath)
    {
        var texts = textStages.ToArray(); var binaries = binaryStages.ToArray(); var entryPath = Path.GetFullPath(entry);
        return Task.Run(() => Capture(snapshot, revision, entryPath, [], configuredAddonsPath, cancellationToken, texts, false, binaries), cancellationToken);
    }
}
