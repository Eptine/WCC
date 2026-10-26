namespace Wcc.Core.Library;

internal sealed record WccPlannedPackage(
    WccPackageRecord Package,
    WccPackageRevisionRecord Revision);

internal sealed record WccPackageRevisionVersionMetadata(
    string Version,
    string TargetWesnothVersion);

/// <summary>
/// Shared mutable planning helper. It only changes manifest metadata; source analysis is
/// still refreshed transactionally by WccLibraryCoordinator after the plan is persisted.
/// </summary>
internal sealed class WccPackagePlanEditor
{
    private readonly int _formatVersion;
    private readonly List<WccPackageRecord> _packages;
    private readonly List<WccPackageRevisionRecord> _revisions;
    private readonly List<WccSourceFileRecord> _sourceFiles;
    private readonly HashSet<WccSourceFileId> _removedSourceFileIds = new();

    public WccPackagePlanEditor(WccLibraryManifest manifest)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        _formatVersion = manifest.FormatVersion;
        _packages = manifest.Packages.ToList();
        _revisions = manifest.PackageRevisions.ToList();
        _sourceFiles = manifest.SourceFiles.ToList();
    }

    public IReadOnlyList<WccSourceFileId> RemovedSourceFileIds => _removedSourceFileIds.ToArray();

    public WccPlannedPackage Upsert(
        string name,
        WccPackageSourceKindId sourceKind,
        WccPackageContentKindId contentKind,
        WccPackageStorageMode storageMode,
        string sourceLocation,
        WccPackageId? parentPackageId,
        IReadOnlyCollection<WccPackageRevisionId> dependencyRevisionIds,
        WccPackageId? preferredPackageId = null,
        WccPackageRevisionId? preferredRevisionId = null,
        WccPackageRevisionVersionMetadata? versionMetadata = null,
        string? sourceIdentifier = null,
        WccAddonPackageMetadata? addonMetadata = null)
    {
        var package = UpsertPackage(
            name,
            sourceKind,
            contentKind,
            storageMode,
            sourceLocation,
            parentPackageId,
            preferredPackageId,
            sourceIdentifier);

        var revision = FindLatestRevision(package.Id);

        if (revision is null)
        {
            revision = new WccPackageRevisionRecord
            {
                Id = preferredRevisionId ?? WccPackageRevisionId.New(),
                PackageId = package.Id,
                Version = versionMetadata?.Version,
                TargetWesnothVersion = versionMetadata?.TargetWesnothVersion,
                License = new WccLicenseInfo { Status = WccLicenseStatus.Unknown },
                AddonMetadata = addonMetadata,
                DependencyRevisionIds = dependencyRevisionIds.Distinct().ToList()
            };
            _revisions.Add(revision);
        }
        else
        {
            var dependencies = dependencyRevisionIds.Distinct().ToList();
            var contentChanged = HasPackageContentChanged(package, revision);
            if (!dependencies.SequenceEqual(revision.DependencyRevisionIds) ||
                (versionMetadata is not null &&
                 (!string.Equals(revision.Version, versionMetadata.Version, StringComparison.Ordinal) ||
                  !string.Equals(
                      revision.TargetWesnothVersion,
                      versionMetadata.TargetWesnothVersion,
                      StringComparison.Ordinal))) ||
                (addonMetadata is not null &&
                 !AddonMetadataEquals(revision.AddonMetadata, addonMetadata)) ||
                contentChanged)
            {
                // Version/dependency identity is immutable. Retaining the previous record
                // is what makes project pins meaningful across a Core patch update or an
                // explicit dependency change; content-only rollover is handled separately.
                revision = new WccPackageRevisionRecord
                {
                    Id = WccPackageRevisionId.New(),
                    PackageId = package.Id,
                    Version = versionMetadata?.Version ?? revision.Version,
                    TargetWesnothVersion = versionMetadata?.TargetWesnothVersion ??
                                           revision.TargetWesnothVersion,
                    License = revision.License,
                    AddonMetadata = addonMetadata ?? revision.AddonMetadata,
                    DependencyRevisionIds = dependencies
                };
                _revisions.Add(revision);
            }
        }

        package = SetCurrentRevision(package.Id, revision.Id);
        return new WccPlannedPackage(package, revision);
    }

    public WccPackageRecord UpsertPackage(
        string name,
        WccPackageSourceKindId sourceKind,
        WccPackageContentKindId contentKind,
        WccPackageStorageMode storageMode,
        string sourceLocation,
        WccPackageId? parentPackageId,
        WccPackageId? preferredPackageId = null,
        string? sourceIdentifier = null)
    {
        var normalizedLocation = NormalizeLocation(sourceLocation);
        var packageIndex = preferredPackageId is { } stablePackageId
            ? _packages.FindIndex(x => x.Id == stablePackageId)
            : _packages.FindIndex(x =>
                x.SourceKind == sourceKind &&
                x.StorageMode == storageMode &&
                x.ParentPackageId == parentPackageId &&
                LocationsEqual(x.SourceLocation, normalizedLocation));

        WccPackageRecord package;
        if (packageIndex >= 0)
        {
            var existing = _packages[packageIndex];
            package = new WccPackageRecord
            {
                Id = existing.Id,
                Name = name,
                SourceKind = sourceKind,
                ContentKind = contentKind,
                StorageMode = storageMode,
                IsAvailable = true,
                CurrentRevisionId = existing.CurrentRevisionId,
                SourceIdentifier = sourceIdentifier ?? existing.SourceIdentifier,
                ParentPackageId = parentPackageId,
                SourceLocation = normalizedLocation
            };
            _packages[packageIndex] = package;
        }
        else
        {
            package = new WccPackageRecord
            {
                Id = preferredPackageId ?? WccPackageId.New(),
                Name = name,
                SourceKind = sourceKind,
                ContentKind = contentKind,
                StorageMode = storageMode,
                IsAvailable = true,
                CurrentRevisionId = null,
                SourceIdentifier = sourceIdentifier,
                ParentPackageId = parentPackageId,
                SourceLocation = normalizedLocation
            };
            _packages.Add(package);
        }

        return package;
    }

    public WccPackageRevisionRecord? FindLatestRevision(WccPackageId packageId)
    {
        var selectedId = _packages.FirstOrDefault(x => x.Id == packageId)?.CurrentRevisionId;
        if (selectedId is { } currentRevisionId)
        {
            var selected = _revisions.FirstOrDefault(x =>
                x.Id == currentRevisionId && x.PackageId == packageId);
            if (selected is not null)
                return selected;
        }

        return _revisions
            .Where(x => x.PackageId == packageId)
            .OrderByDescending(x => x.ImportedAtUtc)
            .ThenByDescending(x => x.Id.Value)
            .FirstOrDefault();
    }

    public WccPackageRecord SetCurrentRevision(
        WccPackageId packageId,
        WccPackageRevisionId revisionId)
    {
        if (_revisions.All(x => x.Id != revisionId || x.PackageId != packageId))
            throw new ArgumentException("The selected revision does not belong to the package.", nameof(revisionId));

        var packageIndex = _packages.FindIndex(x => x.Id == packageId);
        if (packageIndex < 0)
            throw new ArgumentException("Package is not present in the plan.", nameof(packageId));

        var existing = _packages[packageIndex];
        var selected = new WccPackageRecord
        {
            Id = existing.Id,
            Name = existing.Name,
            SourceKind = existing.SourceKind,
            ContentKind = existing.ContentKind,
            StorageMode = existing.StorageMode,
            IsAvailable = existing.IsAvailable,
            CurrentRevisionId = revisionId,
            SourceIdentifier = existing.SourceIdentifier,
            ParentPackageId = existing.ParentPackageId,
            SourceLocation = existing.SourceLocation
        };
        _packages[packageIndex] = selected;
        return selected;
    }

    public void AppendRevision(WccPackageRevisionRecord revision)
    {
        if (revision is null) throw new ArgumentNullException(nameof(revision));
        if (_revisions.Any(x => x.Id == revision.Id))
            throw new ArgumentException($"Revision {revision.Id} already exists.", nameof(revision));
        if (_packages.All(x => x.Id != revision.PackageId))
            throw new ArgumentException("Revision package is not present in the plan.", nameof(revision));

        _revisions.Add(revision);
    }

    public bool HasPackageContentChanged(
        WccPackageRecord package,
        WccPackageRevisionRecord revision)
    {
        if (package is null) throw new ArgumentNullException(nameof(package));
        if (revision is null) throw new ArgumentNullException(nameof(revision));
        if (package.StorageMode == WccPackageStorageMode.WorkingCopy ||
            string.IsNullOrWhiteSpace(revision.ContentFingerprint) ||
            string.IsNullOrWhiteSpace(package.SourceLocation) ||
            !Directory.Exists(package.SourceLocation))
        {
            return false;
        }

        try
        {
            var root = NormalizeLocation(package.SourceLocation);
            var existing = _sourceFiles
                .Where(x => x.PackageRevisionId == revision.Id)
                .ToDictionary(
                    x => NormalizeRelativePath(x.RelativePath),
                    StringComparer.Ordinal);
            var discovered = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => WccPackageCfgIndexer.IsIndexedSourceFile(root, path))
                .Select(path => new
                {
                    Path = path,
                    RelativePath = NormalizeRelativePath(Path.GetRelativePath(root, path))
                })
                .ToArray();
            if (discovered.Length != existing.Count)
                return true;

            foreach (var file in discovered)
            {
                if (!existing.TryGetValue(file.RelativePath, out var oldFile))
                    return true;

                var info = new FileInfo(file.Path);
                if (oldFile.Length != info.Length ||
                    oldFile.LastWriteTimeUtc != new DateTimeOffset(info.LastWriteTimeUtc))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Availability is handled by the owning planner/profile. A transient read failure
            // must not fabricate a new immutable revision.
            return false;
        }
    }

    public void RemoveInactivePackages(
        WccPackageSourceKindId sourceKind,
        WccPackageStorageMode storageMode,
        IReadOnlySet<WccPackageId> activePackageIds)
    {
        var removedPackageIds = _packages
            .Where(x =>
                x.SourceKind == sourceKind &&
                x.StorageMode == storageMode &&
                !activePackageIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSet();
        RemovePackages(removedPackageIds);
    }

    public void RemovePackages(IReadOnlySet<WccPackageId> removedPackageIds)
    {
        if (removedPackageIds is null) throw new ArgumentNullException(nameof(removedPackageIds));
        if (removedPackageIds.Count == 0)
            return;

        // A stale external package may still be part of an immutable revision closure pinned
        // by a project or another retained package. Preserve that package and the transitive
        // dependency closure needed to interpret it; only unreferenced stale packages are
        // physically pruned from the manifest/cache.
        var revisionPackages = _revisions.ToDictionary(x => x.Id, x => x.PackageId);
        var protectedPackageIds = new HashSet<WccPackageId>();
        bool protectionChanged;
        do
        {
            protectionChanged = false;
            foreach (var revision in _revisions.Where(revision =>
                         !removedPackageIds.Contains(revision.PackageId) ||
                         protectedPackageIds.Contains(revision.PackageId)))
            {
                foreach (var dependencyId in revision.DependencyRevisionIds)
                {
                    if (revisionPackages.TryGetValue(dependencyId, out var dependencyPackageId) &&
                        removedPackageIds.Contains(dependencyPackageId) &&
                        protectedPackageIds.Add(dependencyPackageId))
                    {
                        protectionChanged = true;
                    }
                }
            }
        } while (protectionChanged);

        var removablePackageIds = removedPackageIds
            .Where(id => !protectedPackageIds.Contains(id))
            .ToHashSet();
        MarkPackagesUnavailable(protectedPackageIds);
        if (removablePackageIds.Count == 0)
            return;

        var removedRevisionIds = _revisions
            .Where(x => removablePackageIds.Contains(x.PackageId))
            .Select(x => x.Id)
            .ToHashSet();

        foreach (var sourceFile in _sourceFiles.Where(x =>
                     removedRevisionIds.Contains(x.PackageRevisionId)))
        {
            _removedSourceFileIds.Add(sourceFile.Id);
        }

        _sourceFiles.RemoveAll(x => removedRevisionIds.Contains(x.PackageRevisionId));
        _revisions.RemoveAll(x => removablePackageIds.Contains(x.PackageId));
        _packages.RemoveAll(x => removablePackageIds.Contains(x.Id));

        // Retained revisions are immutable. Any incoming dependency ids intentionally remain
        // intact so the snapshot can diagnose that their pinned target is unavailable.
    }

    private void MarkPackagesUnavailable(IReadOnlySet<WccPackageId> packageIds)
    {
        for (var index = 0; index < _packages.Count; index++)
        {
            var package = _packages[index];
            if (!packageIds.Contains(package.Id) || !package.IsAvailable)
                continue;

            _packages[index] = new WccPackageRecord
            {
                Id = package.Id,
                Name = package.Name,
                SourceKind = package.SourceKind,
                ContentKind = package.ContentKind,
                StorageMode = package.StorageMode,
                IsAvailable = false,
                CurrentRevisionId = package.CurrentRevisionId,
                SourceIdentifier = package.SourceIdentifier,
                ParentPackageId = package.ParentPackageId,
                SourceLocation = package.SourceLocation
            };
        }
    }

    public WccLibraryManifest BuildManifest()
        => new()
        {
            FormatVersion = _formatVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Packages = _packages
                .OrderBy(x => x.SourceKind.Value, StringComparer.Ordinal)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            PackageRevisions = _revisions
                .OrderBy(x => x.PackageId.Value)
                .ThenBy(x => x.ImportedAtUtc)
                .ToList(),
            SourceFiles = _sourceFiles
                .OrderBy(x => x.PackageRevisionId.Value)
                .ThenBy(x => x.RelativePath, StringComparer.Ordinal)
                .ToList()
        };

    public static string NormalizeLocation(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static bool LocationsEqual(string? left, string right)
        => !string.IsNullOrWhiteSpace(left) &&
           string.Equals(NormalizeLocation(left), right, PathComparison);

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    internal static bool AddonMetadataEquals(
        WccAddonPackageMetadata? left,
        WccAddonPackageMetadata? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;

        return string.Equals(left.AddonId, right.AddonId, StringComparison.Ordinal) &&
               string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
               string.Equals(left.DeclaredType, right.DeclaredType, StringComparison.Ordinal) &&
               string.Equals(left.DeclaredVersion, right.DeclaredVersion, StringComparison.Ordinal) &&
               string.Equals(left.DeclaredCore, right.DeclaredCore, StringComparison.Ordinal) &&
               string.Equals(
                   left.MetadataSourceFileName,
                   right.MetadataSourceFileName,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.RawMetadataText, right.RawMetadataText, StringComparison.Ordinal) &&
               left.DeclaredDependencyIds.SequenceEqual(right.DeclaredDependencyIds) &&
               DictionariesEqual(left.RawFields, right.RawFields);
    }

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));
}
