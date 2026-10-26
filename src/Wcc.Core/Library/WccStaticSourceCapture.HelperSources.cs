using Wcc.Core.Wml;

namespace Wcc.Core.Library;

public static partial class WccStaticSourceCapture
{
    internal static IReadOnlyList<(string RootPath, WccPackageRevisionId RevisionId, bool IsCore)> GetCapturedHelperSourceRoots(
        WccStaticSourceCaptureResult capture)
    {
        if (!capture.IsCaptureComplete || capture.IncludeResolver is not CapturedResolver resolver
            || resolver.CapturedRoots.Any(root => !root.IsComplete || root.IsAmbiguous))
            throw new InvalidOperationException("Helper copying needs one complete captured source namespace.");
        return Array.AsReadOnly(resolver.CapturedRoots.Select(root => (root.Path, root.Revision, root.IsCore)).ToArray());
    }

    internal static (string RootPath, WccPackageRevisionId RevisionId) GetCapturedHelperCoreBinding(
        WccStaticSourceCaptureResult capture)
    {
        var cores = GetCapturedHelperSourceRoots(capture).Where(root => root.IsCore).ToArray();
        if (cores.Length != 1) throw new InvalidOperationException("Helper copying needs one exact pinned Core namespace.");
        return (cores[0].RootPath, cores[0].RevisionId);
    }

    internal static WmlStaticTruth CapturedHelperResourcePresence(WccStaticSourceCaptureResult capture,
        WccPackageRevisionId revisionId, string relativePath)
    {
        if (!capture.IsCaptureComplete || capture.IncludeResolver is not CapturedResolver resolver || capture.EntrySource is null)
            return WmlStaticTruth.Unknown;
        var roots = resolver.CapturedRoots.Where(root => root.Revision == revisionId && root.IsComplete && !root.IsAmbiguous).ToArray();
        return roots.Length == 1 ? resolver.ResolveResource(roots[0].Mount + "/" + relativePath, capture.EntrySource).Exists : WmlStaticTruth.Unknown;
    }
}
