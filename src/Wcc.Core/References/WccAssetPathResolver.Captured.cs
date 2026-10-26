using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.References;

public static partial class WccAssetPathResolver
{
    /// <summary>Display-only lookup inside one exact indexed source package, then
    /// its exact pinned Core. This does not infer binary_path or activate packages.</summary>
    public static WccAssetResolutionResult ResolveCapturedPackageReference(WccLibrarySnapshot snapshot,
        WccPackageRevisionId rootRevisionId, WmlStaticSource capturedSource, WccAssetReference reference,
        bool preferTransparentPortrait = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(capturedSource);
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Kind is not (WccAssetReferenceKind.Image or WccAssetReferenceKind.Portrait))
            throw new ArgumentOutOfRangeException(nameof(reference), "Captured previews support images and portraits.");
        if (!snapshot.RevisionsById.TryGetValue(rootRevisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable ||
            string.IsNullOrWhiteSpace(package.SourceLocation) || !SafeCapturedRelativePath(reference.LookupPath)) return new();
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SourceLocation));
            var sourcePath = Path.GetFullPath(capturedSource.Path);
            if (!IsInsideCapturedRoot(root, sourcePath) || !HasNoCapturedLinks(root, sourcePath)) return new();
            var owners = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == rootRevisionId &&
                    file.FileType == WccSourceFileTypes.Wml &&
                    SafeCapturedRelativePath(file.RelativePath) &&
                    SameCapturedPath(Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), sourcePath) &&
                    !string.IsNullOrWhiteSpace(file.StoragePath) && SameCapturedPath(file.StoragePath, sourcePath))
                .Take(2).ToArray();
            if (owners.Length != 1 || !string.Equals(owners[0].ContentHash, capturedSource.ContentHash, StringComparison.Ordinal)) return new();

            var candidates = new List<string>();
            AddRoot(root);
            var resolved = candidates.FirstOrDefault(path => HasNoCapturedLinks(root, path) && File.Exists(path));
            if (resolved is not null) return Result(resolved);

            var visible = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, rootRevisionId);
            var coreRoots = visible.Select(id => snapshot.RevisionsById.GetValueOrDefault(id))
                .Where(value => value is not null && snapshot.PackagesById.TryGetValue(value.PackageId, out var candidate) &&
                    candidate.IsAvailable && candidate.ParentPackageId is null &&
                    (candidate.SourceKind == WccPackageSourceKinds.WesnothCore || candidate.SourceKind == WccPackageSourceKinds.WesnothMainline) &&
                    (candidate.ContentKind == WccPackageContentKinds.Core || candidate.ContentKind == WccPackageContentKinds.ResourcePack) &&
                    !string.IsNullOrWhiteSpace(candidate.SourceLocation))
                .Take(2).ToArray();
            if (coreRoots.Length == 1)
            {
                var core = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshot.PackagesById[coreRoots[0]!.PackageId].SourceLocation!));
                AddRoot(core);
                resolved = candidates.Where(path => IsInsideCapturedRoot(core, path))
                    .FirstOrDefault(path => HasNoCapturedLinks(core, path) && File.Exists(path));
            }
            return Result(resolved);

            void AddRoot(string binaryRoot)
            {
                foreach (var path in BuildCandidatesFromRoot(binaryRoot, reference.Kind, reference.LookupPath, preferTransparentPortrait))
                {
                    var full = Path.GetFullPath(path);
                    if (IsInsideCapturedRoot(binaryRoot, full) && !candidates.Contains(full, CapturedPathComparer)) candidates.Add(full);
                }
            }
            WccAssetResolutionResult Result(string? path)
            { var result = new WccAssetResolutionResult { ResolvedAbsolutePath = path }; result.CandidatePaths.AddRange(candidates); return result; }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(); }
    }

    private static StringComparer CapturedPathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool SameCapturedPath(string left, string right) => CapturedPathComparer.Equals(Path.GetFullPath(left), Path.GetFullPath(right));
    private static bool IsInsideCapturedRoot(string root, string path) => path.StartsWith(root + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool SafeCapturedRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || Path.IsPathRooted(path) ||
            path.Any(character => char.IsControl(character) || "<>:\"|?*{}$[]~".Contains(character))) return false;
        var segments = path.Replace('\\', '/').Split('/');
        return segments.Length <= 96 && segments.All(segment => segment.Length != 0 && segment is not ("." or "..") &&
            !segment.EndsWith('.') && !segment.EndsWith(' '));
    }
    private static bool HasNoCapturedLinks(string root, string path)
    {
        if (!IsInsideCapturedRoot(root, path)) return false;
        try
        {
            // Check root ancestors as well as descendants. A junction above an
            // apparently contained source or image cannot redirect this display read.
            var parent = new DirectoryInfo(root);
            for (var depth = 0; parent is not null; depth++, parent = parent.Parent)
            {
                if (depth > 128 || (parent.Attributes & FileAttributes.ReparsePoint) != 0 || !parent.Exists) return false;
            }
            var cursor = root;
            foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                cursor = Path.Combine(cursor, segment);
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
}
