namespace Wcc.Core.Library;

public sealed class WccAddonPackagePlan
{
    public required WccLibraryManifest Manifest { get; init; }
    public required IReadOnlyList<WccPackageRefreshRequest> RefreshRequests { get; init; }
    public required IReadOnlyList<WccSourceFileId> RemovedSourceFileIds { get; init; }
    public required IReadOnlyList<WccDiagnosticRecord> Diagnostics { get; init; }
}

/// <summary>
/// Reconciles the currently configured add-ons folder. Metadata is read for every package
/// before any revision is selected, then the complete dependency graph is published in one
/// batch. This is required for legitimate dependency cycles such as a campaign and its
/// separately downloaded music package.
/// </summary>
public static class WccAddonPackagePlanner
{
    public static WccAddonPackagePlan Build(
        WccLibraryManifest manifest,
        string? addonsPath,
        WccPackageRevisionId coreRevisionId)
        => Build(manifest, addonsPath, coreRevisionId, additionalAddonRoots: null);

    /// <summary>
    /// Includes explicitly attached read-only add-on roots in the same revision batch as
    /// the configured add-ons folder. Availability does not activate their _main.cfg.
    /// </summary>
    public static WccAddonPackagePlan Build(
        WccLibraryManifest manifest,
        string? addonsPath,
        WccPackageRevisionId coreRevisionId,
        IEnumerable<string>? additionalAddonRoots)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        var corePackageId = manifest.PackageRevisions
            .Where(revision => revision.Id == coreRevisionId)
            .Select(revision => (WccPackageId?)revision.PackageId)
            .SingleOrDefault() ?? throw new ArgumentException(
                "The Core revision must exist in the library manifest.",
                nameof(coreRevisionId));
        return Build(manifest, addonsPath, corePackageId, coreRevisionId, additionalAddonRoots);
    }

    public static WccAddonPackagePlan Build(
        WccLibraryManifest manifest,
        string? addonsPath,
        WccPackageId corePackageId,
        WccPackageRevisionId coreRevisionId)
        => Build(manifest, addonsPath, corePackageId, coreRevisionId, additionalAddonRoots: null);

    public static WccAddonPackagePlan Build(
        WccLibraryManifest manifest,
        string? addonsPath,
        WccPackageId corePackageId,
        WccPackageRevisionId coreRevisionId,
        IEnumerable<string>? additionalAddonRoots)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        var coreRevision = manifest.PackageRevisions.SingleOrDefault(revision =>
            revision.Id == coreRevisionId && revision.PackageId == corePackageId)
            ?? throw new ArgumentException(
                "The Core revision/package pair must exist in the library manifest.",
                nameof(coreRevisionId));

        var editor = new WccPackagePlanEditor(manifest);
        var diagnostics = new List<WccDiagnosticRecord>();
        var descriptors = new List<AddonDescriptor>();
        var claimedPackageIds = new HashSet<WccPackageId>();

        // Identity pass: reserve every stable WCC package id before resolving an edge.
        foreach (var packageRoot in DiscoverPackageRoots(addonsPath, additionalAddonRoots, diagnostics))
        {
            var readResult = WccAddonMetadataReader.Read(packageRoot);
            var metadata = readResult.Metadata;
            var preferredPackageId = FindExistingPackageId(
                manifest,
                packageRoot,
                metadata.AddonId,
                corePackageId,
                claimedPackageIds,
                diagnostics);
            var package = editor.UpsertPackage(
                metadata.Title ?? metadata.AddonId,
                WccPackageSourceKinds.WesnothAddon,
                MapContentKind(metadata.DeclaredType),
                WccPackageStorageMode.ExternalReadOnly,
                packageRoot,
                parentPackageId: corePackageId,
                preferredPackageId,
                sourceIdentifier: metadata.AddonId);
            claimedPackageIds.Add(package.Id);

            foreach (var issue in readResult.Issues)
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = issue.Code,
                    Message = issue.Message,
                    Severity = issue.Severity,
                    PackageId = package.Id
                });
            }

            if (!string.IsNullOrWhiteSpace(metadata.DeclaredType) &&
                MapContentKind(metadata.DeclaredType) == WccPackageContentKinds.Unknown)
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "addon.unknown_package_type",
                    Message = $"Add-on '{metadata.AddonId}' declares unknown type '{metadata.DeclaredType}'. The declaration was preserved.",
                    Severity = WccDiagnosticSeverity.Warning,
                    PackageId = package.Id
                });
            }

            descriptors.Add(new AddonDescriptor(
                packageRoot,
                package,
                metadata,
                editor.FindLatestRevision(package.Id)));
        }

        var byAddonId = descriptors
            .GroupBy(descriptor => descriptor.Metadata.AddonId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var duplicate in byAddonId.Where(pair => pair.Value.Length > 1))
        {
            foreach (var descriptor in duplicate.Value)
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "addon.duplicate_id",
                    Message = $"More than one package in this Wesnoth profile uses add-on id '{duplicate.Key}'. Dependencies to that id remain unresolved.",
                    Severity = WccDiagnosticSeverity.Error,
                    PackageId = descriptor.Package.Id
                });
            }
        }

        // Resolution pass: declarations only resolve inside the containing Core/profile.
        foreach (var descriptor in descriptors)
        {
            ResolveDependencies(
                descriptor,
                byAddonId,
                coreRevisionId,
                diagnostics);
        }

        AddCycleDiagnostics(descriptors, diagnostics);

        // Compare each old head against the graph expressed in terms of the old heads. A
        // change to one node then propagates to every reverse dependent (and across an SCC).
        var existingRevisionsById = manifest.PackageRevisions.ToDictionary(x => x.Id);
        var dirty = descriptors
            .Where(descriptor => RevisionIdentityChanged(
                descriptor,
                coreRevision,
                existingRevisionsById) ||
                descriptor.CurrentRevision is not null &&
                editor.HasPackageContentChanged(descriptor.Package, descriptor.CurrentRevision))
            .ToHashSet();

        bool changed;
        do
        {
            changed = false;
            foreach (var descriptor in descriptors)
            {
                if (!dirty.Contains(descriptor) &&
                    descriptor.PackageDependencies.Any(dirty.Contains))
                {
                    dirty.Add(descriptor);
                    changed = true;
                }
            }
        } while (changed);

        var selectedRevisionIds = descriptors.ToDictionary(
            descriptor => descriptor,
            descriptor => dirty.Contains(descriptor)
                ? WccPackageRevisionId.New()
                : descriptor.CurrentRevision!.Id);
        var importedAtUtc = DateTimeOffset.UtcNow;

        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            if (!dirty.Contains(descriptor))
                continue;

            var dependencyRevisionIds = BuildDependencyRevisionIds(
                descriptor,
                coreRevisionId,
                selectedRevisionIds);
            editor.AppendRevision(new WccPackageRevisionRecord
            {
                Id = selectedRevisionIds[descriptor],
                PackageId = descriptor.Package.Id,
                Version = descriptor.Metadata.DeclaredVersion,
                TargetWesnothVersion = coreRevision.TargetWesnothVersion,
                ImportedAtUtc = importedAtUtc.AddTicks(index),
                License = descriptor.CurrentRevision?.License ??
                          new WccLicenseInfo { Status = WccLicenseStatus.Unknown },
                AddonMetadata = descriptor.Metadata,
                DependencyRevisionIds = dependencyRevisionIds
            });
        }

        foreach (var descriptor in descriptors)
            editor.SetCurrentRevision(descriptor.Package.Id, selectedRevisionIds[descriptor]);

        var activePackageIds = descriptors.Select(x => x.Package.Id).ToHashSet();
        var staleCurrentProfilePackageIds = manifest.Packages
            .Where(package =>
                package.SourceKind == WccPackageSourceKinds.WesnothAddon &&
                package.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
                !activePackageIds.Contains(package.Id) &&
                (package.ParentPackageId == corePackageId ||
                 package.ParentPackageId is null &&
                 IsInsideConfiguredAddonsRoot(package.SourceLocation, addonsPath)))
            .Select(package => package.Id)
            .ToHashSet();
        editor.RemovePackages(staleCurrentProfilePackageIds);

        var requests = descriptors
            .Select(descriptor => new WccPackageRefreshRequest(
                selectedRevisionIds[descriptor],
                descriptor.PackageRoot,
                descriptor.Package.Name))
            .ToArray();

        return new WccAddonPackagePlan
        {
            Manifest = editor.BuildManifest(),
            RefreshRequests = requests,
            RemovedSourceFileIds = editor.RemovedSourceFileIds,
            Diagnostics = diagnostics
        };
    }

    private static WccPackageId? FindExistingPackageId(
        WccLibraryManifest manifest,
        string packageRoot,
        string addonId,
        WccPackageId corePackageId,
        IReadOnlySet<WccPackageId> claimedPackageIds,
        List<WccDiagnosticRecord> diagnostics)
    {
        var candidates = manifest.Packages.Where(package =>
                package.SourceKind == WccPackageSourceKinds.WesnothAddon &&
                package.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
                !claimedPackageIds.Contains(package.Id))
            .ToArray();
        var normalizedRoot = WccPackagePlanEditor.NormalizeLocation(packageRoot);
        var pathMatch = candidates.FirstOrDefault(package =>
            (package.ParentPackageId == corePackageId || package.ParentPackageId is null) &&
            WccPackagePlanEditor.LocationsEqual(package.SourceLocation, normalizedRoot));
        if (pathMatch is not null)
            return pathMatch.Id;

        var idMatches = candidates.Where(package =>
                package.ParentPackageId == corePackageId &&
                string.Equals(package.SourceIdentifier, addonId, StringComparison.Ordinal))
            .ToArray();
        if (idMatches.Length == 1)
            return idMatches[0].Id;

        if (idMatches.Length > 1)
        {
            diagnostics.Add(new WccDiagnosticRecord
            {
                Code = "addon.ambiguous_stored_identity",
                Message = $"Several stored packages use add-on id '{addonId}' in this profile; a new stable package id was assigned.",
                Severity = WccDiagnosticSeverity.Warning
            });
        }

        return null;
    }

    private static void ResolveDependencies(
        AddonDescriptor descriptor,
        IReadOnlyDictionary<string, AddonDescriptor[]> byAddonId,
        WccPackageRevisionId coreRevisionId,
        List<WccDiagnosticRecord> diagnostics)
    {
        var declaredType = descriptor.Metadata.DeclaredType;
        var declaredCore = descriptor.Metadata.DeclaredCore;
        var isCustomCorePackage = string.Equals(declaredType, "core", StringComparison.OrdinalIgnoreCase);

        if (isCustomCorePackage)
        {
            if (!string.IsNullOrWhiteSpace(declaredCore))
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "addon.core_declares_core",
                    Message = $"Custom Core add-on '{descriptor.Metadata.AddonId}' also declares core='{declaredCore}'. The declaration was preserved but not followed recursively.",
                    Severity = WccDiagnosticSeverity.Warning,
                    PackageId = descriptor.Package.Id
                });
            }
        }
        else if (string.IsNullOrWhiteSpace(declaredCore) ||
                 string.Equals(declaredCore, "default", StringComparison.Ordinal))
        {
            descriptor.IncludeDefaultCore = true;
        }
        else
        {
            ResolveAddonEdge(
                descriptor,
                declaredCore,
                "custom Core",
                byAddonId,
                diagnostics);
        }

        foreach (var dependencyId in descriptor.Metadata.DeclaredDependencyIds)
        {
            ResolveAddonEdge(
                descriptor,
                dependencyId,
                "dependency",
                byAddonId,
                diagnostics);
        }

        // A missing metadata file follows Wesnoth's default-Core convention.
        if (descriptor.Metadata.MetadataSourceFileName is null && !isCustomCorePackage)
            descriptor.IncludeDefaultCore = true;

        _ = coreRevisionId; // documents that default Core is fixed by the containing profile
    }

    private static void ResolveAddonEdge(
        AddonDescriptor source,
        string targetId,
        string relationship,
        IReadOnlyDictionary<string, AddonDescriptor[]> byAddonId,
        List<WccDiagnosticRecord> diagnostics)
    {
        if (!byAddonId.TryGetValue(targetId, out var matches) || matches.Length == 0)
        {
            diagnostics.Add(new WccDiagnosticRecord
            {
                Code = relationship == "custom Core"
                    ? "addon.custom_core_unresolved"
                    : "addon.dependency_unresolved",
                Message = $"Add-on '{source.Metadata.AddonId}' declares {relationship} '{targetId}', but it is not installed in this profile.",
                Severity = WccDiagnosticSeverity.Warning,
                PackageId = source.Package.Id
            });
            return;
        }

        if (matches.Length > 1)
        {
            diagnostics.Add(new WccDiagnosticRecord
            {
                Code = relationship == "custom Core"
                    ? "addon.custom_core_ambiguous"
                    : "addon.dependency_ambiguous",
                Message = $"Add-on '{source.Metadata.AddonId}' declares {relationship} '{targetId}', but that id is ambiguous in this profile.",
                Severity = WccDiagnosticSeverity.Error,
                PackageId = source.Package.Id
            });
            return;
        }

        if (source.PackageDependencies.All(existing => existing.Package.Id != matches[0].Package.Id))
            source.PackageDependencies.Add(matches[0]);
    }

    private static bool RevisionIdentityChanged(
        AddonDescriptor descriptor,
        WccPackageRevisionRecord coreRevision,
        IReadOnlyDictionary<WccPackageRevisionId, WccPackageRevisionRecord> existingRevisionsById)
    {
        var current = descriptor.CurrentRevision;
        if (current is null ||
            !string.Equals(current.Version, descriptor.Metadata.DeclaredVersion, StringComparison.Ordinal) ||
            !string.Equals(
                current.TargetWesnothVersion,
                coreRevision.TargetWesnothVersion,
                StringComparison.Ordinal) ||
            !WccPackagePlanEditor.AddonMetadataEquals(current.AddonMetadata, descriptor.Metadata))
        {
            return true;
        }

        var expected = new List<WccPackageRevisionId>();
        if (descriptor.IncludeDefaultCore)
            expected.Add(coreRevision.Id);

        foreach (var dependency in descriptor.PackageDependencies)
        {
            if (dependency.CurrentRevision is null)
                return true;
            expected.Add(dependency.CurrentRevision.Id);
        }

        var canonicalExpected = DistinctInOrder(expected);
        if (!current.DependencyRevisionIds.SequenceEqual(canonicalExpected))
            return true;

        // A retained dangling id is never equivalent to a resolved current graph.
        return current.DependencyRevisionIds.Any(id => !existingRevisionsById.ContainsKey(id));
    }

    private static List<WccPackageRevisionId> BuildDependencyRevisionIds(
        AddonDescriptor descriptor,
        WccPackageRevisionId coreRevisionId,
        IReadOnlyDictionary<AddonDescriptor, WccPackageRevisionId> selectedRevisionIds)
    {
        var result = new List<WccPackageRevisionId>();
        if (descriptor.IncludeDefaultCore)
            result.Add(coreRevisionId);
        result.AddRange(descriptor.PackageDependencies.Select(dependency =>
            selectedRevisionIds[dependency]));
        return DistinctInOrder(result);
    }

    private static List<WccPackageRevisionId> DistinctInOrder(
        IEnumerable<WccPackageRevisionId> values)
    {
        var seen = new HashSet<WccPackageRevisionId>();
        return values.Where(seen.Add).ToList();
    }

    private static WccPackageContentKindId MapContentKind(string? declaredType)
        => declaredType?.Trim().ToLowerInvariant() switch
        {
            "core" => WccPackageContentKinds.Core,
            "campaign" or "campaign_sp_mp" or "campaign_mp" => WccPackageContentKinds.Campaign,
            "scenario" or "scenario_mp" => WccPackageContentKinds.Scenario,
            "era" => WccPackageContentKinds.Era,
            "faction" => WccPackageContentKinds.Faction,
            "map_pack" => WccPackageContentKinds.MapPack,
            "mod_mp" => WccPackageContentKinds.Modification,
            "media" => WccPackageContentKinds.ResourcePack,
            "other" => WccPackageContentKinds.Other,
            null or "" => WccPackageContentKinds.Unknown,
            _ => WccPackageContentKinds.Unknown
        };

    private static void AddCycleDiagnostics(
        IReadOnlyList<AddonDescriptor> descriptors,
        List<WccDiagnosticRecord> diagnostics)
    {
        var index = 0;
        var indexes = new Dictionary<AddonDescriptor, int>();
        var lowLinks = new Dictionary<AddonDescriptor, int>();
        var stack = new Stack<AddonDescriptor>();
        var onStack = new HashSet<AddonDescriptor>();

        void Visit(AddonDescriptor descriptor)
        {
            indexes[descriptor] = index;
            lowLinks[descriptor] = index;
            index++;
            stack.Push(descriptor);
            onStack.Add(descriptor);

            foreach (var dependency in descriptor.PackageDependencies)
            {
                if (!indexes.ContainsKey(dependency))
                {
                    Visit(dependency);
                    lowLinks[descriptor] = Math.Min(lowLinks[descriptor], lowLinks[dependency]);
                }
                else if (onStack.Contains(dependency))
                {
                    lowLinks[descriptor] = Math.Min(lowLinks[descriptor], indexes[dependency]);
                }
            }

            if (lowLinks[descriptor] != indexes[descriptor])
                return;

            var component = new List<AddonDescriptor>();
            AddonDescriptor member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            } while (!ReferenceEquals(member, descriptor));

            var isCycle = component.Count > 1 ||
                          component[0].PackageDependencies.Contains(component[0]);
            if (!isCycle)
                return;

            var ids = component.Select(x => x.Metadata.AddonId)
                .OrderBy(x => x, StringComparer.Ordinal);
            diagnostics.Add(new WccDiagnosticRecord
            {
                Code = "addon.dependency_cycle",
                Message = $"Add-on dependency cycle detected: {string.Join(" -> ", ids)}. The cycle is retained and resolved as one revision graph.",
                Severity = WccDiagnosticSeverity.Information,
                PackageId = component[0].Package.Id
            });
        }

        foreach (var descriptor in descriptors)
        {
            if (!indexes.ContainsKey(descriptor))
                Visit(descriptor);
        }
    }

    private static IEnumerable<string> DiscoverPackageRoots(string? addonsPath)
    {
        if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
            return Array.Empty<string>();

        var fullRoot = WccPackagePlanEditor.NormalizeLocation(addonsPath);
        if (File.Exists(Path.Combine(fullRoot, "_main.cfg")))
            return new[] { fullRoot };

        return Directory.EnumerateDirectories(fullRoot)
            .Where(ContainsCfgFile)
            .OrderBy(x => x, WccPackagePlanEditor.PathComparer)
            .ToArray();
    }

    private static IEnumerable<string> DiscoverPackageRoots(string? addonsPath,
        IEnumerable<string>? additionalAddonRoots, List<WccDiagnosticRecord> diagnostics)
    {
        var roots = new HashSet<string>(WccPackagePlanEditor.PathComparer);
        foreach (var root in DiscoverPackageRoots(addonsPath))
            if (roots.Add(root)) yield return root;

        foreach (var selected in additionalAddonRoots ?? [])
        {
            string? root = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    var normalized = WccPackagePlanEditor.NormalizeLocation(selected);
                    if (Directory.Exists(normalized) && ContainsCfgFile(normalized))
                        root = normalized;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // A missing/moved explicit source remains visible in settings and in
                // diagnostics; another directory is never substituted by add-on name.
            }
            if (root is null)
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "addon.attached_source_unavailable",
                    Message = $"Attached add-on source '{selected}' is unavailable or contains no CFG files.",
                    Severity = WccDiagnosticSeverity.Warning
                });
                continue;
            }
            if (roots.Add(root)) yield return root;
        }
    }

    private static bool ContainsCfgFile(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.cfg", SearchOption.AllDirectories).Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsInsideConfiguredAddonsRoot(
        string? sourceLocation,
        string? addonsPath)
    {
        if (string.IsNullOrWhiteSpace(sourceLocation) ||
            string.IsNullOrWhiteSpace(addonsPath))
        {
            return false;
        }

        try
        {
            var root = WccPackagePlanEditor.NormalizeLocation(addonsPath);
            var candidate = WccPackagePlanEditor.NormalizeLocation(sourceLocation);
            if (WccPackagePlanEditor.LocationsEqual(candidate, root))
                return true;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
                   candidate.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class AddonDescriptor
    {
        public AddonDescriptor(
            string packageRoot,
            WccPackageRecord package,
            WccAddonPackageMetadata metadata,
            WccPackageRevisionRecord? currentRevision)
        {
            PackageRoot = packageRoot;
            Package = package;
            Metadata = metadata;
            CurrentRevision = currentRevision;
        }

        public string PackageRoot { get; }
        public WccPackageRecord Package { get; }
        public WccAddonPackageMetadata Metadata { get; }
        public WccPackageRevisionRecord? CurrentRevision { get; }
        public bool IncludeDefaultCore { get; set; }
        public List<AddonDescriptor> PackageDependencies { get; } = new();
    }
}
