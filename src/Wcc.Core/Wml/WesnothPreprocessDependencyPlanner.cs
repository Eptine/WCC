using Wcc.Core.Library;

namespace Wcc.Core.Wml;

public sealed record WesnothPreprocessDependencyPlan(
    IReadOnlyList<WesnothPreprocessDependencyScope> DependencyScopes,
    IReadOnlyList<WesnothPreprocessRoutingRoot> RoutingRoots,
    string? PreprocessInputPath,
    string Identity,
    string? Diagnostic = null)
{
    public bool IsComplete =>
        Diagnostic is null &&
        DependencyScopes.Count > 0 &&
        RoutingRoots.Count > 0 &&
        !string.IsNullOrWhiteSpace(PreprocessInputPath);
}

/// <summary>
/// Connects an identical installed source file to its Library package revision, follows only
/// that revision's declared dependency closure, and prepares the disk manifests required
/// before Wesnoth output may be treated as exact. A current working-project revision is
/// accepted only when its normalized package root is inside the configured add-ons root;
/// every indexed file is still verified before the engine output can become authoritative.
/// </summary>
public static class WesnothPreprocessDependencyPlanner
{
    public static WesnothPreprocessDependencyPlan Build(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds,
        string runtimeSourcePath,
        string runtimeSourceContentHash,
        string dataMainPath,
        string? addonsRoot = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(visibleRevisionIds);

        if (!TryFullPath(runtimeSourcePath, out var sourcePath) || !File.Exists(sourcePath))
            return Failure("The exact runtime source path is unavailable.");
        if (string.IsNullOrWhiteSpace(runtimeSourceContentHash))
            return Failure("The exact runtime source content hash is unavailable.");
        if (!TryFullPath(dataMainPath, out var mainPath) || !File.Exists(mainPath))
            return Failure("Wesnoth data/_main.cfg is unavailable.");

        var dataRoot = Path.GetDirectoryName(mainPath);
        if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot))
            return Failure("The Wesnoth data routing root is unavailable.");
        dataRoot = Path.GetFullPath(dataRoot);

        string? normalizedAddonsRoot = null;
        if (!string.IsNullOrWhiteSpace(addonsRoot))
        {
            if (!TryFullPath(addonsRoot, out normalizedAddonsRoot) ||
                !Directory.Exists(normalizedAddonsRoot))
            {
                return Failure("The configured Wesnoth add-ons routing root is unavailable.");
            }
        }

        var selectedSourceFiles = snapshot.SourceFilesById.Values
            .Where(file =>
                visibleRevisionIds.Contains(file.PackageRevisionId) &&
                TryFullPath(file.StoragePath, out var storagePath) &&
                PathComparer.Equals(storagePath, sourcePath) &&
                string.Equals(
                    file.ContentHash,
                    runtimeSourceContentHash,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selectedSourceFiles.Length == 0)
        {
            return Failure(
                "The identical installed source is not present as a visible indexed source revision.");
        }
        if (selectedSourceFiles.Length != 1)
        {
            var workingSource = FindVerifiedWorkingSource(snapshot, selectedSourceFiles,
                visibleRevisionIds, dataRoot, normalizedAddonsRoot);
            if (workingSource is null)
                return Failure(
                    "The identical installed source has ambiguous visible indexed source revisions.");
            selectedSourceFiles = [workingSource];
        }

        var selectedSourceFile = selectedSourceFiles[0];
        if (!TryGetSourceRuntimeRevision(
                snapshot,
                selectedSourceFile.PackageRevisionId,
                normalizedAddonsRoot,
                out var sourceRevision) ||
            !IsAllowedRuntimeRoot(sourceRevision.RootPath, dataRoot, normalizedAddonsRoot) ||
            !IsPathInsideOrEqual(sourcePath, sourceRevision.RootPath))
        {
            return Failure(
                "The identical installed source is not owned by a visible indexed runtime package.");
        }
        var dependencyRevisionIds = WccLibraryRevisionVisibility.GetVisibleRevisionIds(
                snapshot,
                sourceRevision.Revision.Id)
            .ToHashSet();
        if (sourceRevision.IsWorkingAddonOverlay &&
            TryFindSelectedCoreRevision(
                snapshot,
                sourceRevision,
                visibleRevisionIds,
                dataRoot,
                out var selectedCoreRevisionId))
        {
            // A declared add-on dependency cycle can retain the installed read-only
            // revision as a back-edge (working IftU -> Music -> read-only IftU). It is the
            // same physical package as the current working overlay, so collapse only a
            // rigorously verified same-root/same-fingerprint/same-Core alias before building
            // dependency scopes. This avoids verifying one root twice without broadening the
            // selected runtime graph.
            var verifiedShadowAliasIds = dependencyRevisionIds
                .Where(candidate => WccWorkingAddonOverlayRevisionAlias.IsVerified(
                    snapshot,
                    sourceRevision.Revision.Id,
                    candidate,
                    selectedCoreRevisionId))
                .ToArray();
            if (verifiedShadowAliasIds.Length > 1)
            {
                return Failure(
                    "The installed working add-on overlay has ambiguous shadowed runtime revisions.");
            }

            dependencyRevisionIds.ExceptWith(verifiedShadowAliasIds);
        }
        if (sourceRevision.IsWorkingAddonOverlay &&
            !dependencyRevisionIds.Any(revisionId => revisionId != sourceRevision.Revision.Id))
        {
            return Failure(
                "The installed working add-on overlay has no verified Wesnoth runtime dependency revision.");
        }
        var scopes = new List<WesnothPreprocessDependencyScope>();
        var identities = new List<string>();
        var usesAddonsRouting = IsPathInsideOrEqual(sourceRevision.RootPath, normalizedAddonsRoot);
        foreach (var revisionId in dependencyRevisionIds.OrderBy(x => x.ToString(), StringComparer.Ordinal))
        {
            if (!TryGetRuntimeRevision(snapshot, revisionId, out var runtimeRevision))
            {
                if (revisionId == sourceRevision.Revision.Id && sourceRevision.IsWorkingAddonOverlay)
                {
                    runtimeRevision = sourceRevision;
                }
                else
                {
                    return Failure(
                        $"Dependency revision '{revisionId}' is unavailable or is not a Wesnoth runtime package.");
                }
            }
            if (!IsAllowedRuntimeRoot(runtimeRevision.RootPath, dataRoot, normalizedAddonsRoot))
            {
                return Failure(
                    $"Dependency revision '{revisionId}' belongs to a different Wesnoth runtime profile.");
            }

            var indexedFiles = snapshot.SourceFilesById.Values
                .Where(file => file.PackageRevisionId == revisionId &&
                               file.RelativePath.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => NormalizeRelativePath(file.RelativePath), StringComparer.Ordinal)
                .ToArray();
            if (indexedFiles.Length == 0)
            {
                return Failure(
                    $"Dependency revision '{revisionId}' has no complete indexed WML manifest.");
            }

            var computedFingerprint = ComputeIndexedPackageFingerprint(indexedFiles);
            if (string.IsNullOrWhiteSpace(runtimeRevision.Revision.ContentFingerprint) ||
                !string.Equals(
                    computedFingerprint,
                    runtimeRevision.Revision.ContentFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    $"Dependency revision '{revisionId}' has an incomplete or inconsistent package fingerprint.");
            }

            var expectedFiles = indexedFiles
                .Select(file => new WesnothPreprocessDependencyFile(
                    NormalizeRelativePath(file.RelativePath),
                    file.ContentHash))
                .ToArray();
            var scopeIdentity = $"{runtimeRevision.Package.Name}:{revisionId}";
            scopes.Add(new WesnothPreprocessDependencyScope(
                scopeIdentity,
                runtimeRevision.RootPath,
                expectedFiles));
            identities.Add($"{revisionId}:{computedFingerprint}");
            usesAddonsRouting |= IsPathInsideOrEqual(
                runtimeRevision.RootPath,
                normalizedAddonsRoot);
        }

        var sourceRelativePath = NormalizeRelativePath(
            Path.GetRelativePath(sourceRevision.RootPath, sourcePath));
        var sourceScope = scopes.Single(scope =>
            PathComparer.Equals(scope.RootPath, sourceRevision.RootPath));
        if (!sourceScope.ExpectedWmlFiles.Any(file =>
                PathComparer.Equals(file.RelativePath, sourceRelativePath) &&
                string.Equals(
                    file.ContentHash,
                    runtimeSourceContentHash,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return Failure(
                "The identical installed source is not present in its selected package revision manifest.");
        }

        var routingRoots = new List<WesnothPreprocessRoutingRoot>
        {
            new("wesnoth-data", dataRoot)
        };
        if (usesAddonsRouting && normalizedAddonsRoot is not null)
        {
            routingRoots.Add(new WesnothPreprocessRoutingRoot(
                "wesnoth-add-ons",
                normalizedAddonsRoot));
        }

        var preprocessInputPath = mainPath;
        if (sourceRevision.Package.SourceKind == WccPackageSourceKinds.WesnothAddon ||
            sourceRevision.IsWorkingAddonOverlay)
        {
            if (normalizedAddonsRoot is null ||
                !IsPathInsideOrEqual(sourceRevision.RootPath, normalizedAddonsRoot))
            {
                return Failure(
                    "The selected add-on source is outside the configured Wesnoth add-ons root.");
            }

            var packageMainPath = Path.Combine(sourceRevision.RootPath, "_main.cfg");
            if (!File.Exists(packageMainPath))
            {
                return Failure(
                    "The selected add-on has no package-root _main.cfg preprocess entry point.");
            }

            var mappedPackageMainFiles = snapshot.SourceFilesById.Values
                .Where(file =>
                    file.PackageRevisionId == sourceRevision.Revision.Id &&
                    TryFullPath(file.StoragePath, out var storagePath) &&
                    PathComparer.Equals(storagePath, packageMainPath))
                .ToArray();
            if (mappedPackageMainFiles.Length == 0)
            {
                return Failure(
                    "The selected add-on's package-root _main.cfg is absent from its indexed revision manifest.");
            }
            if (mappedPackageMainFiles.Length != 1)
            {
                return Failure(
                    "The selected add-on's package-root _main.cfg has ambiguous indexed revision records.");
            }

            var indexedPackageMain = mappedPackageMainFiles[0];
            if (!PathComparer.Equals(
                    NormalizeRelativePath(indexedPackageMain.RelativePath),
                    "_main.cfg"))
            {
                return Failure(
                    "The selected add-on's preprocess entry point is not indexed as its package-root _main.cfg.");
            }
            if (!TryComputeCurrentContentHash(packageMainPath, out var currentPackageMainHash) ||
                !string.Equals(
                    indexedPackageMain.ContentHash,
                    currentPackageMainHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "The selected add-on's package-root _main.cfg changed after Library indexing. Refresh the Library before using exact output.");
            }

            preprocessInputPath = packageMainPath;
        }

        identities.Add(
            $"source:{sourceRevision.Revision.Id}:{sourceRelativePath}:{runtimeSourceContentHash}");
        identities.Add($"entry:{preprocessInputPath}");
        identities.AddRange(routingRoots.Select(root => $"routing:{root.Identity}:{root.RootPath}"));
        return new WesnothPreprocessDependencyPlan(
            scopes,
            routingRoots,
            preprocessInputPath,
            WccContentHash.ComputeText(string.Join('\n', identities.Order(StringComparer.Ordinal))));
    }

    private static WccSourceFileRecord? FindVerifiedWorkingSource(
        WccLibrarySnapshot snapshot, WccSourceFileRecord[] candidates,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds, string dataRoot, string? addonsRoot)
    {
        if (candidates.Length != 2) return null;
        var working = candidates.Where(file => TryGetSourceRuntimeRevision(snapshot,
            file.PackageRevisionId, addonsRoot, out var source) && source.IsWorkingAddonOverlay).ToArray();
        if (working.Length != 1 || !TryGetSourceRuntimeRevision(snapshot, working[0].PackageRevisionId,
                addonsRoot, out var overlay) ||
            !TryFindSelectedCoreRevision(snapshot, overlay, visibleRevisionIds, dataRoot, out var core)) return null;
        var installed = candidates.Single(file => !ReferenceEquals(file, working[0]));
        return WccWorkingAddonOverlayRevisionAlias.IsVerified(snapshot, overlay.Revision.Id,
            installed.PackageRevisionId, core) ? working[0] : null;
    }

    private static bool TryGetRuntimeRevision(
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId revisionId,
        out RuntimeRevision runtimeRevision)
    {
        runtimeRevision = default!;
        if (!snapshot.RevisionsById.TryGetValue(revisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) ||
            !package.IsAvailable ||
            !IsRuntimeSourceKind(package.SourceKind) ||
            string.IsNullOrWhiteSpace(package.SourceLocation) ||
            !TryFullPath(package.SourceLocation, out var rootPath) ||
            !Directory.Exists(rootPath))
        {
            return false;
        }

        runtimeRevision = new RuntimeRevision(package, revision, rootPath, false);
        return true;
    }

    private static bool TryFindSelectedCoreRevision(
        WccLibrarySnapshot snapshot,
        RuntimeRevision workingOverlay,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds,
        string dataRoot,
        out WccPackageRevisionId selectedCoreRevisionId)
    {
        selectedCoreRevisionId = default;
        var candidates = workingOverlay.Revision.DependencyRevisionIds
            .Where(visibleRevisionIds.Contains)
            .Where(revisionId =>
                snapshot.RevisionsById.TryGetValue(revisionId, out var revision) &&
                snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) &&
                package.IsAvailable &&
                package.ParentPackageId is null &&
                package.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
                package.CurrentRevisionId == revisionId &&
                (package.SourceKind == WccPackageSourceKinds.WesnothCore ||
                 package.SourceKind == WccPackageSourceKinds.WesnothMainline) &&
                package.ContentKind == WccPackageContentKinds.ResourcePack &&
                !string.IsNullOrWhiteSpace(package.SourceLocation) &&
                TryFullPath(package.SourceLocation, out var rootPath) &&
                IsPathInsideOrEqual(rootPath, dataRoot))
            .Distinct()
            .ToArray();
        if (candidates.Length != 1)
            return false;

        selectedCoreRevisionId = candidates[0];
        return true;
    }

    private static bool TryGetSourceRuntimeRevision(
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId revisionId,
        string? addonsRoot,
        out RuntimeRevision runtimeRevision)
    {
        if (TryGetRuntimeRevision(snapshot, revisionId, out runtimeRevision))
            return true;

        runtimeRevision = default!;
        if (string.IsNullOrWhiteSpace(addonsRoot) ||
            !snapshot.RevisionsById.TryGetValue(revisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) ||
            !package.IsAvailable ||
            package.SourceKind != WccPackageSourceKinds.WccProject ||
            package.StorageMode != WccPackageStorageMode.WorkingCopy ||
            package.CurrentRevisionId != revisionId ||
            string.IsNullOrWhiteSpace(package.SourceLocation) ||
            !TryFullPath(package.SourceLocation, out var rootPath) ||
            !Directory.Exists(rootPath) ||
            !IsPathInsideOrEqual(rootPath, addonsRoot))
        {
            return false;
        }

        runtimeRevision = new RuntimeRevision(package, revision, rootPath, true);
        return true;
    }

    private static bool IsRuntimeSourceKind(WccPackageSourceKindId sourceKind) =>
        sourceKind == WccPackageSourceKinds.WesnothCore ||
        sourceKind == WccPackageSourceKinds.WesnothMainline ||
        sourceKind == WccPackageSourceKinds.WesnothAddon;

    private static bool IsAllowedRuntimeRoot(
        string rootPath,
        string dataRoot,
        string? addonsRoot) =>
        IsPathInsideOrEqual(rootPath, dataRoot) ||
        IsPathInsideOrEqual(rootPath, addonsRoot);

    private static bool IsPathInsideOrEqual(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return PathComparer.Equals(fullPath, fullRoot) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string ComputeIndexedPackageFingerprint(
        IEnumerable<WccSourceFileRecord> sourceFiles)
    {
        var canonical = string.Join(
            "\n",
            sourceFiles
                .OrderBy(file => NormalizeRelativePath(file.RelativePath), StringComparer.Ordinal)
                .Select(file => $"{NormalizeRelativePath(file.RelativePath)}\0{file.ContentHash}"));
        return WccContentHash.ComputeText(canonical);
    }

    private static bool TryComputeCurrentContentHash(string path, out string contentHash)
    {
        contentHash = string.Empty;
        try
        {
            contentHash = WccContentHash.ComputeText(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryFullPath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static WesnothPreprocessDependencyPlan Failure(string diagnostic) =>
        new([], [], null, string.Empty, diagnostic);

    private sealed record RuntimeRevision(
        WccPackageRecord Package,
        WccPackageRevisionRecord Revision,
        string RootPath,
        bool IsWorkingAddonOverlay);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
