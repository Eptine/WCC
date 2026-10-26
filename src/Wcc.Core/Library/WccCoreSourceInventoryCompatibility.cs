using Wcc.Core.References;

namespace Wcc.Core.Library;

/// <summary>
/// An inspection result for the historical CFG-only to CFG-and-music inventory transition.
/// It is not a runtime profile, persistence capability, disk verification, or revision pin.
/// </summary>
public sealed class WccCoreSourceInventoryCompatibilityResult
{
    internal WccCoreSourceInventoryCompatibilityResult(bool isCompatible, string diagnostic,
        int matchedCfgCount = 0, int addedAudioCount = 0)
    {
        IsCompatible = isCompatible;
        Diagnostic = diagnostic;
        MatchedCfgCount = matchedCfgCount;
        AddedAudioCount = addedAudioCount;
    }

    public bool IsCompatible { get; }
    public string Diagnostic { get; }
    public int MatchedCfgCount { get; }
    public int AddedAudioCount { get; }
    public bool AuthorizesRuntime => false;
    public bool AuthorizesPersistence => false;
}

/// <summary>
/// Compares retained manifest inventories without accessing the filesystem. Only an exact
/// CFG-only Core pin and the same package's selected CFG-and-music inventory can match.
/// Callers must separately bind installation/profile metadata and retain all source, disk,
/// closure, runtime and persistence guards. This never changes either revision or its pin.
/// </summary>
public static class WccCoreSourceInventoryCompatibility
{
    public static WccCoreSourceInventoryCompatibilityResult Check(WccLibrarySnapshot snapshot,
        WccPackageRevisionId pinnedRevisionId, WccPackageRevisionId currentRevisionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (pinnedRevisionId == default || currentRevisionId == default || pinnedRevisionId == currentRevisionId
            || !snapshot.RevisionsById.TryGetValue(pinnedRevisionId, out var pinned)
            || !snapshot.RevisionsById.TryGetValue(currentRevisionId, out var current)
            || pinned.PackageId != current.PackageId
            || !snapshot.PackagesById.TryGetValue(pinned.PackageId, out var package))
            return Refuse("The exact retained and current Core revisions are unavailable or do not belong to the same package.");

        if (snapshot.Manifest.PackageRevisions.Count(revision => revision.Id == pinnedRevisionId) != 1
            || snapshot.Manifest.PackageRevisions.Count(revision => revision.Id == currentRevisionId) != 1
            || snapshot.Manifest.Packages.Count(candidate => candidate.Id == package.Id) != 1
            || !snapshot.Manifest.PackageRevisions.Contains(pinned)
            || !snapshot.Manifest.PackageRevisions.Contains(current)
            || !snapshot.Manifest.Packages.Contains(package))
            return Refuse("The Core revision or package inventory is ambiguous or differs from the captured snapshot.");

        if (!package.IsAvailable || package.ParentPackageId is not null
            || package.StorageMode != WccPackageStorageMode.ExternalReadOnly || !IsCorePackage(package)
            || package.CurrentRevisionId != currentRevisionId)
            return Refuse("The current revision must be the selected available external root Core package revision.");

        if (string.IsNullOrWhiteSpace(pinned.Version) || string.IsNullOrWhiteSpace(pinned.TargetWesnothVersion)
            || !string.Equals(pinned.Version, current.Version, StringComparison.Ordinal)
            || !string.Equals(pinned.TargetWesnothVersion, current.TargetWesnothVersion, StringComparison.Ordinal))
            return Refuse("The exact Core version and target must remain unchanged.");

        if (!TryCanonicalAbsolutePath(package.SourceLocation, out var root))
            return Refuse("The Core package has no safe absolute source root.");

        if (!TryInventory(snapshot, pinned, root!, out var oldFiles, out var problem)
            || !TryInventory(snapshot, current, root!, out var newFiles, out problem))
            return Refuse(problem!);

        if (oldFiles!.Count == 0 || oldFiles.Values.Any(file => !IsCfg(file)))
            return Refuse("Only a nonempty historical CFG-only inventory is eligible for this source compatibility check.");

        foreach (var (path, oldFile) in oldFiles)
        {
            if (!newFiles!.TryGetValue(path, out var newFile)
                || newFile.FileType != oldFile.FileType || newFile.Length != oldFile.Length
                || !string.Equals(newFile.ContentHash, oldFile.ContentHash, StringComparison.OrdinalIgnoreCase))
                return Refuse("A retained CFG source path, type, content hash, or length changed or is missing.");
        }

        var additions = newFiles!.Where(entry => !oldFiles.ContainsKey(entry.Key)).ToArray();
        if (additions.Length == 0 || additions.Any(entry => !IsIndexedMusic(entry.Key, entry.Value)))
            return Refuse("The current inventory must add only supported music audio, with no new CFG or other resources.");

        return new(true,
            "The retained CFG inventory is unchanged; the selected Core revision adds only indexed music audio. "
            + "This supports source inspection only and does not authorize runtime execution or persistence.",
            oldFiles.Count, additions.Length);
    }

    private static bool TryInventory(WccLibrarySnapshot snapshot, WccPackageRevisionRecord revision, string root,
        out Dictionary<string, WccSourceFileRecord>? inventory, out string? problem)
    {
        inventory = new(StringComparer.Ordinal);
        problem = null;
        var files = snapshot.Manifest.SourceFiles.Where(file => file.PackageRevisionId == revision.Id).ToArray();
        var ids = files.Select(file => file.Id).ToHashSet();
        if (ids.Count != files.Length || ids.Contains(default)
            || snapshot.Manifest.SourceFiles.Count(file => ids.Contains(file.Id)) != files.Length
            || snapshot.SourceFilesById.Values.Count(file => file.PackageRevisionId == revision.Id) != files.Length)
        {
            problem = "The Core source-file identity inventory is ambiguous or incomplete.";
            return false;
        }

        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storagePaths = new HashSet<string>(PathComparer);
        foreach (var file in files)
        {
            if (!snapshot.SourceFilesById.TryGetValue(file.Id, out var captured) || !ReferenceEquals(captured, file))
            {
                problem = "The Core source inventory differs from the captured snapshot.";
                return false;
            }

            if (!TryRelativePath(file.RelativePath, out var relative)
                || !TryCanonicalAbsolutePath(file.StoragePath, out var storage)
                || !TryCanonicalAbsolutePath(Path.Combine(root,
                    relative!.Replace('/', Path.DirectorySeparatorChar)), out var expectedStorage)
                || !PathComparer.Equals(storage, expectedStorage)
                || !caseInsensitivePaths.Add(relative!) || !storagePaths.Add(storage!))
            {
                problem = "The Core source inventory contains unsafe, ambiguous, or mismatched relative/storage paths.";
                return false;
            }

            if (file.Length < 0 || !IsSha256(file.ContentHash))
            {
                problem = "The Core source inventory has invalid content hash or length metadata.";
                return false;
            }
            inventory.Add(relative!, file);
        }

        // Exactly the indexer's full-inventory algorithm, including the recorded hash spelling.
        var canonical = string.Join("\n", inventory.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}\0{entry.Value.ContentHash}"));
        if (!IsSha256(revision.ContentFingerprint)
            || !string.Equals(revision.ContentFingerprint, WccContentHash.ComputeText(canonical),
                StringComparison.OrdinalIgnoreCase))
        {
            problem = "The Core revision fingerprint does not match its complete recorded source inventory.";
            return false;
        }
        return true;
    }

    private static bool IsCorePackage(WccPackageRecord package) =>
        package.SourceKind == WccPackageSourceKinds.WesnothCore
            && (package.ContentKind == WccPackageContentKinds.Core
                || package.ContentKind == WccPackageContentKinds.ResourcePack)
        || package.SourceKind == WccPackageSourceKinds.WesnothMainline
            && package.ContentKind == WccPackageContentKinds.ResourcePack;

    private static bool IsCfg(WccSourceFileRecord file) => file.FileType == WccSourceFileTypes.Wml
        && string.Equals(Path.GetExtension(file.RelativePath), ".cfg", StringComparison.OrdinalIgnoreCase);

    private static bool IsIndexedMusic(string relative, WccSourceFileRecord file) =>
        file.FileType == WccSourceFileTypes.Audio
        && (relative.StartsWith("music/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/music/", StringComparison.OrdinalIgnoreCase))
        && WccPackageMusicImporter.FilePatterns.Contains("*" + Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase);

    private static bool TryRelativePath(string? value, out string? relative)
    {
        relative = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Any(character => char.IsControl(character)
                || "<>:\"|?*".Contains(character))) return false;
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."
                || segment.EndsWith('.') || segment.EndsWith(' '))) return false;
        relative = normalized;
        return true;
    }

    private static bool TryCanonicalAbsolutePath(string? value, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => char.IsControl(character)
                || "<>\"|?*".Contains(character))
            || value.Contains(':') && !(value.Length > 2 && char.IsAsciiLetter(value[0])
                && value[1] == ':' && value.Count(character => character == ':') == 1)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(value)) return false;
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static WccCoreSourceInventoryCompatibilityResult Refuse(string problem) => new(false, problem);
}
