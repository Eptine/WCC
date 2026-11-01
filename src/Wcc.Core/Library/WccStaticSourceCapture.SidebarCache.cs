using Wcc.Core.Wml;

namespace Wcc.Core.Library;

public static partial class WccStaticSourceCapture
{
    /// <summary>
    /// Identity of frozen text, roots, namespaces and resource membership/stamps.
    /// A derived display cache may reuse it only after obtaining a fresh capture.
    /// It is not resource-byte evidence, activation evidence or edit authority.
    /// </summary>
    public static string? GetCapturedContentIdentity(WccStaticSourceCaptureResult capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return capture.IsCaptureComplete && capture.IncludeResolver is CapturedResolver resolver
            ? resolver.CapturedContentIdentity : null;
    }

    /// <summary>Checks only exact frozen resource-file membership; never lookup precedence or edit authority.</summary>
    public static bool IsCapturedResourcePath(WccStaticSourceCaptureResult capture, string path)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.IsCaptureComplete || capture.EntrySource is null || capture.IncludeResolver is not CapturedResolver resolver ||
            string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var canonical = Path.GetFullPath(path);
            if (!DiskComparer.Equals(path, canonical)) return false;
            var roots = resolver.CapturedRoots.Where(root => Contains(root.Path, canonical)).ToArray();
            if (roots.Length != 1) return false;
            var root = roots[0];
            var relative = Relative(root.Path, canonical);
            return root.IsComplete && !root.IsAmbiguous &&
                root.Entries.TryGetValue(relative, out var entry) && !entry.IsDirectory &&
                resolver.ResolveResource(root.Mount + "/" + relative, capture.EntrySource) is
                    { Exists: WmlStaticTruth.True, MembershipIdentity.Length: > 0 };
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }
}
