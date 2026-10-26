using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.Core.References;

/// <summary>
/// Best-effort asset resolver for the current WCC stage.
/// For CoreInstall assets, a configured Wesnoth install path gives us a deterministic core root.
/// For everything else, we still use inferred binary roots from the owning source file path.
/// </summary>
public static partial class WccAssetPathResolver
{
    private static StringComparer PhysicalPathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PhysicalPathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static WccAssetResolutionResult ResolveFromLibrarySnapshot(
        WccLibrarySnapshot snapshot,
        WccAssetRegistryEntry entry,
        string? wesnothInstallPath = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (entry is null) throw new ArgumentNullException(nameof(entry));

        var candidates = new List<string>();
        foreach (var reference in entry.References)
        {
            var sourcePath = TryFindOwnerSourcePath(snapshot, reference.OwnerInternalId);
            var origin = ResolveLibraryOrigin(snapshot, reference.OwnerInternalId);
            var partial = ResolveReference(sourcePath, origin, reference, wesnothInstallPath);
            candidates.AddRange(partial.CandidatePaths);
        }

        var distinct = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(PhysicalPathComparer)
            .ToList();
        var result = new WccAssetResolutionResult
        {
            ResolvedAbsolutePath = distinct.FirstOrDefault(File.Exists)
        };
        result.CandidatePaths.AddRange(distinct);
        return result;
    }

    public static WccAssetResolutionResult ResolveReference(
        string? ownerSourcePath,
        WccSourceOrigin origin,
        WccAssetReference reference,
        string? wesnothInstallPath = null,
        bool preferTransparentPortrait = true)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));

        var candidates = new List<string>();

        if (origin == WccSourceOrigin.CoreInstall && !string.IsNullOrWhiteSpace(wesnothInstallPath))
        {
            var coreRoot = WccCoreInstallPaths.GetCoreDataRoot(wesnothInstallPath!);
            candidates.AddRange(BuildCandidatesFromRoot(
                coreRoot,
                reference.Kind,
                reference.LookupPath,
                preferTransparentPortrait));
        }

        if (!string.IsNullOrWhiteSpace(ownerSourcePath))
        {
            candidates.AddRange(BuildCandidatePaths(
                ownerSourcePath!,
                reference,
                preferTransparentPortrait));
        }

        // Every Wesnoth add-on may use Core assets without declaring another add-on dependency.
        // Prefer the owning package above, then use Core as the deterministic implicit fallback.
        if (origin != WccSourceOrigin.CoreInstall && !string.IsNullOrWhiteSpace(wesnothInstallPath))
        {
            var coreRoot = WccCoreInstallPaths.GetCoreDataRoot(wesnothInstallPath!);
            candidates.AddRange(BuildCandidatesFromRoot(
                coreRoot,
                reference.Kind,
                reference.LookupPath,
                preferTransparentPortrait));
        }

        var distinct = candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(PhysicalPathComparer)
            .ToList();

        var resolved = distinct.FirstOrDefault(File.Exists);

        var result = new WccAssetResolutionResult
        {
            ResolvedAbsolutePath = resolved
        };

        result.CandidatePaths.AddRange(distinct);
        return result;
    }

    private static string? TryFindOwnerSourcePath(
        WccLibrarySnapshot snapshot,
        string? ownerInternalId)
    {
        if (!Guid.TryParse(ownerInternalId, out var guid) ||
            !snapshot.EntitiesById.TryGetValue(new WccEntityId(guid), out var entity) ||
            !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile))
        {
            return null;
        }

        return sourceFile.StoragePath;
    }

    private static WccSourceOrigin ResolveLibraryOrigin(
        WccLibrarySnapshot snapshot,
        string? ownerInternalId)
    {
        if (!Guid.TryParse(ownerInternalId, out var guid))
            return WccSourceOrigin.ImportedExternal;

        var package = snapshot.FindPackageForEntity(new WccEntityId(guid));
        if (package?.SourceKind == WccPackageSourceKinds.WesnothMainline)
            return WccSourceOrigin.CoreInstall;
        if (package?.SourceKind == WccPackageSourceKinds.WccManagedLibrary)
            return WccSourceOrigin.WccGenerated;

        return WccSourceOrigin.ImportedExternal;
    }

    private static IEnumerable<string> BuildCandidatePaths(
        string ownerSourcePath,
        WccAssetReference reference,
        bool preferTransparentPortrait)
    {
        var lookup = reference.LookupPath;
        if (string.IsNullOrWhiteSpace(lookup))
            yield break;

        // A copied/imported add-on is still its own package root even when it
        // no longer lives below Wesnoth's data/add-ons directory. Preserve the
        // meaning of {~add-ons/Package/...} by matching Package against an
        // ancestor of the owning source file before trying inferred roots.
        foreach (var candidate in BuildPortableAddOnCandidates(ownerSourcePath, lookup!))
            yield return candidate;

        foreach (var root in InferBinaryRoots(ownerSourcePath).Distinct(PhysicalPathComparer))
        {
            foreach (var candidate in BuildCandidatesFromRoot(
                         root,
                         reference.Kind,
                         lookup!,
                         preferTransparentPortrait))
                yield return candidate;
        }
    }

    private static IEnumerable<string> BuildPortableAddOnCandidates(
        string ownerSourcePath,
        string lookupPath)
    {
        var portable = lookupPath.Replace('\\', '/').TrimStart();
        var prefixLength = portable.StartsWith("~add-ons/", StringComparison.OrdinalIgnoreCase)
            ? "~add-ons/".Length
            : portable.StartsWith("add-ons/", StringComparison.OrdinalIgnoreCase)
                ? "add-ons/".Length
                : 0;
        if (prefixLength == 0)
            yield break;

        var packageAndPath = portable[prefixLength..];
        var separator = packageAndPath.IndexOf('/');
        if (separator <= 0 || separator == packageAndPath.Length - 1)
            yield break;

        var packageName = packageAndPath[..separator];
        var packageRelativePath = packageAndPath[(separator + 1)..];
        if (packageName is "." or ".." ||
            packageRelativePath.Split('/').Any(x => x is "." or ".."))
        {
            yield break;
        }

        DirectoryInfo? directory;
        try
        {
            var fullOwnerPath = Path.GetFullPath(ownerSourcePath);
            directory = new DirectoryInfo(Path.GetDirectoryName(fullOwnerPath) ?? fullOwnerPath);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        while (directory is not null)
        {
            if (string.Equals(directory.Name, packageName, PhysicalPathComparison))
            {
                yield return Path.GetFullPath(Path.Combine(
                    directory.FullName,
                    packageRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                yield break;
            }

            directory = directory.Parent;
        }
    }

    private static IEnumerable<string> BuildCandidatesFromRoot(
        string binaryRoot,
        WccAssetReferenceKind kind,
        string? lookupPath,
        bool preferTransparentPortrait = true)
    {
        if (string.IsNullOrWhiteSpace(lookupPath))
            yield break;

        var portableLookup = lookupPath.Replace('\\', '/');
        if (portableLookup.StartsWith("~add-ons/", StringComparison.OrdinalIgnoreCase))
        {
            var dataRoot = TryCutBeforeSegment(Path.GetFullPath(binaryRoot), "add-ons");
            if (!string.IsNullOrWhiteSpace(dataRoot))
            {
                yield return Path.GetFullPath(Path.Combine(
                    dataRoot!,
                    portableLookup[1..].Replace('/', Path.DirectorySeparatorChar)));
            }

            yield break;
        }

        var normalizedLookup = NormalizeLookupPath(lookupPath);

        foreach (var absolute in BuildAbsoluteCandidates(
                     binaryRoot,
                     kind,
                     normalizedLookup,
                     preferTransparentPortrait))
            yield return absolute;
    }

    private static IEnumerable<string> BuildAbsoluteCandidates(
        string binaryRoot,
        WccAssetReferenceKind kind,
        string lookupPath,
        bool preferTransparentPortrait)
    {
        var gameOrUserRoot = InferGameOrUserRootFromBinaryRoot(binaryRoot);

        switch (kind)
        {
            case WccAssetReferenceKind.Image:
            case WccAssetReferenceKind.Icon:
            case WccAssetReferenceKind.BackgroundImage:
                yield return Path.GetFullPath(Path.Combine(binaryRoot, "images", lookupPath));
                yield return Path.GetFullPath(Path.Combine(binaryRoot, lookupPath));
                break;

            case WccAssetReferenceKind.Portrait:
                var transparentLookup = preferTransparentPortrait
                    ? BuildTransparentPortraitLookup(lookupPath)
                    : null;
                if (!string.IsNullOrWhiteSpace(transparentLookup))
                {
                    yield return Path.GetFullPath(Path.Combine(binaryRoot, "images", transparentLookup));
                    yield return Path.GetFullPath(Path.Combine(binaryRoot, transparentLookup));
                }

                yield return Path.GetFullPath(Path.Combine(binaryRoot, "images", lookupPath));
                yield return Path.GetFullPath(Path.Combine(binaryRoot, lookupPath));
                break;

            case WccAssetReferenceKind.Map:
                yield return Path.GetFullPath(Path.Combine(binaryRoot, "maps", lookupPath));
                yield return Path.GetFullPath(Path.Combine(binaryRoot, lookupPath));
                break;

            case WccAssetReferenceKind.Music:
                yield return Path.GetFullPath(Path.Combine(binaryRoot, "music", lookupPath));
                yield return Path.GetFullPath(Path.Combine(binaryRoot, lookupPath));
                break;

            case WccAssetReferenceKind.Sound:
            case WccAssetReferenceKind.Voice:
                yield return Path.GetFullPath(Path.Combine(binaryRoot, "sounds", lookupPath));
                yield return Path.GetFullPath(Path.Combine(binaryRoot, "sound", lookupPath));
                yield return Path.GetFullPath(Path.Combine(binaryRoot, lookupPath));
                break;
        }

        if (!string.IsNullOrWhiteSpace(gameOrUserRoot))
        {
            yield return Path.GetFullPath(Path.Combine(gameOrUserRoot, lookupPath));
            yield return Path.GetFullPath(Path.Combine(gameOrUserRoot, "data", lookupPath));
        }
    }

    private static string NormalizeLookupPath(string lookupPath)
    {
        var s = lookupPath.Replace('/', Path.DirectorySeparatorChar)
                          .Replace('\\', Path.DirectorySeparatorChar);

        return s.TrimStart(Path.DirectorySeparatorChar);
    }

    private static IEnumerable<string> InferBinaryRoots(string ownerSourcePath)
    {
        var fullOwnerPath = Path.GetFullPath(ownerSourcePath);
        var fullDir = Path.GetDirectoryName(fullOwnerPath) ?? fullOwnerPath;

        foreach (var marker in new[] { "units", "unit-catalog", "scenarios", "macros", "utils", "maps", "images", "music", "sounds", "sound" })
        {
            var root = TryCutBeforeSegment(fullDir, marker);
            if (!string.IsNullOrWhiteSpace(root))
                yield return root!;
        }

        var dataCoreRoot = TryCutAfterSegments(fullDir, "data", "core");
        if (!string.IsNullOrWhiteSpace(dataCoreRoot))
            yield return dataCoreRoot!;

        var addOnRoot = TryCutAfterSegments(fullDir, "data", "add-ons");
        if (!string.IsNullOrWhiteSpace(addOnRoot))
        {
            var addonScoped = TryExpandOneMoreSegment(addOnRoot!, fullDir);
            if (!string.IsNullOrWhiteSpace(addonScoped))
                yield return addonScoped!;
        }

        var directAddOnRoot = TryCutAfterSegments(fullDir, "add-ons");
        if (!string.IsNullOrWhiteSpace(directAddOnRoot))
        {
            var addonScoped = TryExpandOneMoreSegment(directAddOnRoot!, fullDir);
            if (!string.IsNullOrWhiteSpace(addonScoped))
                yield return addonScoped!;
        }
    }

    private static string? BuildTransparentPortraitLookup(string lookupPath)
    {
        var normalized = lookupPath.Replace('\\', '/').TrimStart('/');
        const string portraitsPrefix = "portraits/";
        const string transparentPrefix = "portraits/transparent/";

        if (!normalized.StartsWith(portraitsPrefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(transparentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return "portraits/transparent/" + normalized[portraitsPrefix.Length..];
    }

    private static string? InferGameOrUserRootFromBinaryRoot(string binaryRoot)
    {
        var normalized = binaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], "data", StringComparison.OrdinalIgnoreCase))
            {
                if (i > 0)
                    return string.Join(Path.DirectorySeparatorChar, parts.Take(i));
            }
        }

        return Directory.GetParent(binaryRoot)?.FullName;
    }

    private static string? TryCutBeforeSegment(string fullDir, string segment)
    {
        var parts = fullDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], segment, StringComparison.OrdinalIgnoreCase))
            {
                if (i == 0)
                    return null;

                return string.Join(Path.DirectorySeparatorChar, parts.Take(i));
            }
        }

        return null;
    }

    private static string? TryCutAfterSegments(string fullDir, params string[] segments)
    {
        var parts = fullDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (int i = 0; i <= parts.Length - segments.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < segments.Length; j++)
            {
                if (!string.Equals(parts[i + j], segments[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return string.Join(Path.DirectorySeparatorChar, parts.Take(i + segments.Length));
        }

        return null;
    }

    private static string? TryExpandOneMoreSegment(string prefix, string fullDir)
    {
        var prefixParts = prefix.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullParts = fullDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullParts.Length <= prefixParts.Length)
            return prefix;

        return string.Join(Path.DirectorySeparatorChar, fullParts.Take(prefixParts.Length + 1));
    }
}
