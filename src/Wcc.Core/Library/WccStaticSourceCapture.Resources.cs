using Wcc.Core.Projects;

namespace Wcc.Core.Library;

/// <summary>One exact captured resource member. Membership is not byte evidence or write authority.</summary>
public sealed record WccCapturedResourceMember(string RootPath, string RelativePath, string PhysicalPath,
    WccPackageRevisionId RevisionId, bool IsCore, string MembershipIdentity)
{
    public bool AuthorizesPersistence => false;
}

/// <summary>A virtual destination and separately captured original bytes; never an early copy.</summary>
public sealed record WccCapturedBinaryCandidate(string DestinationPath, WccBinaryDiskRevision SourceRevision)
{
    public bool AuthorizesPersistence => false;
}

/// <summary>One explicitly reviewed virtual directory; immutable evidence, never disk creation authority.</summary>
public sealed record WccCapturedDirectoryCandidate(string DestinationPath)
{
    public bool AuthorizesPersistence => false;
}

public static partial class WccStaticSourceCapture
{
    /// <summary>Maps one proven captured virtual resource route to its physical display member.</summary>
    public static WccCapturedResourceMember? FindCapturedResourceByVirtualPath(
        WccStaticSourceCaptureResult capture, string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.IsCaptureComplete || capture.EntrySource is null || capture.IncludeResolver is not CapturedResolver resolver ||
            !CapturedResolver.TryNormalizePath(virtualPath, out var normalized, allowResourceParentheses: true)) return null;
        var candidates = resolver.CapturedRoots.SelectMany(root => root.Mounts
            .Where(mount => normalized.StartsWith(mount + "/", StringComparison.Ordinal))
            .Select(mount => (Root: root, Relative: normalized[(mount.Length + 1)..]))).ToArray();
        if (candidates.Length != 1) return null;
        var (root, relative) = candidates[0];
        if (!root.IsComplete || root.IsAmbiguous || !root.Entries.TryGetValue(relative, out var entry) || entry.IsDirectory ||
            resolver.ResolveResource(normalized, capture.EntrySource) is not { Exists: Wcc.Core.Wml.WmlStaticTruth.True } binding ||
            string.IsNullOrWhiteSpace(binding.MembershipIdentity)) return null;
        return new(root.Path, relative, Path.Combine(root.Path, relative.Replace('/', Path.DirectorySeparatorChar)),
            root.Revision, root.IsCore, binding.MembershipIdentity);
    }

    /// <summary>Enumerates one exact frozen package resource inventory without
    /// rereading directories or bytes. Available membership does not activate a
    /// binary path, prove lookup precedence, or authorize persistence.</summary>
    public static IEnumerable<WccCapturedResourceMember> EnumerateCapturedResourceMembers(
        WccStaticSourceCaptureResult capture, WccPackageRevisionId revisionId)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.IsCaptureComplete || capture.EntrySource is null || capture.IncludeResolver is not CapturedResolver resolver)
            yield break;
        var roots = resolver.CapturedRoots.Where(root => root.Revision == revisionId && root.IsComplete && !root.IsAmbiguous).ToArray();
        if (roots.Length != 1) yield break;
        var root = roots[0];
        foreach (var pair in root.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Value.IsDirectory) continue;
            var binding = resolver.ResolveResource(root.Mount + "/" + pair.Key, capture.EntrySource);
            if (binding.Exists != Wcc.Core.Wml.WmlStaticTruth.True || string.IsNullOrWhiteSpace(binding.MembershipIdentity)) continue;
            yield return new(root.Path, pair.Key, Path.Combine(root.Path, pair.Key.Replace('/', Path.DirectorySeparatorChar)),
                revisionId, root.IsCore, binding.MembershipIdentity);
        }
    }
    internal static IReadOnlyList<(string RootPath, WccPackageRevisionId RevisionId)> GetCapturedManagedRoots(WccStaticSourceCaptureResult capture)
    {
        if (!capture.IsCaptureComplete || capture.IncludeResolver is not CapturedResolver resolver)
            throw new InvalidOperationException("Managed source inventory needs one complete captured scope.");
        return Array.AsReadOnly(resolver.CapturedRoots.Where(root => root.IsManagedLibrary && root.IsComplete && !root.IsAmbiguous)
            .Select(root => (root.Path, root.Revision)).ToArray());
    }

    /// <summary>Checks captured membership/stamps without activating stored sibling files.</summary>
    public static string? VerifyCapturedRootInventory(WccStaticSourceCaptureResult capture, WccPackageRevisionId revisionId)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.IsCaptureComplete || capture.IncludeResolver is not CapturedResolver resolver)
            return "The source resource inventory is incomplete.";
        var roots = resolver.CapturedRoots.Where(root => root.Revision == revisionId && root.IsComplete && !root.IsAmbiguous).ToArray();
        if (roots.Length != 1) return "The source resource inventory no longer identifies one exact package root.";
        try
        {
            var count = 0;
            var current = Enumerate(roots[0].Path, ref count, CancellationToken.None, roots[0].OwnedStagePaths);
            return roots[0].Inventory.SequenceEqual(current) ? null : "The captured Library helper or resource inventory changed. Reopen the wizard.";
        }
        catch (Exception error) when (IsReadFailure(error))
        { return "The captured helper or resource inventory cannot be verified: " + error.Message; }
    }

    public static WccCapturedResourceMember? FindCapturedResourceMember(WccStaticSourceCaptureResult capture,
        WccPackageRevisionId revisionId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.IsCaptureComplete || capture.IncludeResolver is not CapturedResolver resolver
            || string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\')
            || !CapturedResolver.TryNormalizePath("~add-ons/member/" + relativePath, out var normalized, allowResourceParentheses: true)
            || normalized != "~add-ons/member/" + relativePath)
            return null;
        var roots = resolver.CapturedRoots.Where(root => root.Revision == revisionId && root.IsComplete && !root.IsAmbiguous).ToArray();
        if (roots.Length != 1) return null;
        var root = roots[0];
        if (!root.Entries.TryGetValue(relativePath, out var entry) || entry.IsDirectory
            || root.Entries.Keys.Count(path => string.Equals(path, relativePath, StringComparison.OrdinalIgnoreCase)) != 1)
            return null;
        var path = Path.Combine(root.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var binding = resolver.ResolveResource(root.Mount + "/" + relativePath, capture.EntrySource!);
        if (binding.Exists != Wcc.Core.Wml.WmlStaticTruth.True || string.IsNullOrWhiteSpace(binding.MembershipIdentity)) return null;
        return new(root.Path, relativePath, path, revisionId, root.IsCore, binding.MembershipIdentity);
    }
}
