using Wcc.Core.Wml;

namespace Wcc.Core.Library;

/// <summary>
/// A working overlay of an installed add-on uses that selected add-on's context.
/// Independent working projects compose declared dependencies inside their exact Core
/// context. Available library packages are candidates, not an implicit search path;
/// existing independent-project pins are never replaced by a newer package.
/// </summary>
public static partial class WccWorkingProjectDependencyResolver
{
    /// <summary>
    /// Repairs only an explicitly pinned default Core edge after a Library rebuild.
    /// The generic resolver below keeps its existing independent-project policy.
    /// Neither the caller's manifest nor the project's original sidecar is changed.
    /// </summary>
    public static IReadOnlyCollection<WccPackageRevisionId>? ResolveForPinnedCore(
        WccLibraryManifest manifest, WccWorkingProjectDescriptor? project, WccProjectCorePinResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(resolution);
        var current = WccProjectCorePinResolver.Resolve(manifest, resolution.Pin);
        if (!ReferenceEquals(manifest, resolution.ManifestIdentity) || !resolution.IsResolved || !current.IsResolved ||
            current.Package!.Id != resolution.Package!.Id || current.Revision!.Id != resolution.Revision!.Id)
            throw new ArgumentException("The project Core binding is unresolved or belongs to another manifest.", nameof(resolution));
        if (project is null) return null;
        var core = current.Revision!;
        var metadata = WccAddonMetadataReader.Read(project.RootDirectory).Metadata;
        if (!IsStaticMetadataField(metadata, "core") ||
            !string.IsNullOrWhiteSpace(metadata.DeclaredCore) && metadata.DeclaredCore != "default")
            return Resolve(manifest, project, core.Id);

        var working = manifest.Packages.Where(package => package.SourceKind == WccPackageSourceKinds.WccProject &&
            package.StorageMode == WccPackageStorageMode.WorkingCopy &&
            WccPackagePlanEditor.LocationsEqual(package.SourceLocation, project.RootDirectory)).ToArray();
        if (working.Length > 1) throw new InvalidOperationException("The working project Core edge has ambiguous ownership.");
        WccPackageRevisionRecord? retained = null;
        if (working.Length == 1)
        {
            var selected = manifest.PackageRevisions.Where(revision => revision.PackageId == working[0].Id &&
                (working[0].CurrentRevisionId is not { } head || revision.Id == head)).ToArray();
            if (selected.Length != 1) throw new InvalidOperationException("The working project has no unique selected dependency revision.");
            retained = selected[0];
        }
        var dependencies = retained?.DependencyRevisionIds.ToList() ?? [];
        for (var index = 0; index < dependencies.Count; index++)
            if (current.IsRebuiltIdentity && dependencies[index] == current.Pin.PackageRevisionId)
                dependencies[index] = core.Id;
        if (!dependencies.Contains(core.Id)) dependencies.Add(core.Id);
        CheckExplicitCoreGraph(manifest, dependencies, core);

        // Feed the established declaration/installed-overlay policy a detached retained
        // graph. Only this one proved Core edge changes; original graph records stay exact.
        var input = manifest;
        if (retained is not null)
        {
            var replacement = new WccPackageRevisionRecord
            {
                Id = retained.Id, PackageId = retained.PackageId, Version = retained.Version,
                ContentFingerprint = retained.ContentFingerprint, TargetWesnothVersion = retained.TargetWesnothVersion,
                ImportedAtUtc = retained.ImportedAtUtc, License = retained.License, AddonMetadata = retained.AddonMetadata,
                DependencyRevisionIds = dependencies
            };
            input = new WccLibraryManifest
            {
                FormatVersion = manifest.FormatVersion, UpdatedAtUtc = manifest.UpdatedAtUtc,
                Packages = manifest.Packages.ToList(), SourceFiles = manifest.SourceFiles.ToList(),
                PackageRevisions = manifest.PackageRevisions.Select(revision => ReferenceEquals(revision, retained) ? replacement : revision).ToList()
            };
        }
        var result = Resolve(input, project, core.Id) ?? [];
        CheckExplicitCoreGraph(manifest, result, core);
        return result;
    }

    private static void CheckExplicitCoreGraph(WccLibraryManifest manifest,
        IEnumerable<WccPackageRevisionId> roots, WccPackageRevisionRecord core)
    {
        var selected = new Dictionary<WccPackageId, WccPackageRevisionId>();
        var pending = new Queue<WccPackageRevisionId>(roots);
        while (pending.TryDequeue(out var id))
        {
            var revisions = manifest.PackageRevisions.Where(revision => revision.Id == id).ToArray();
            if (revisions.Length != 1) throw new InvalidOperationException("A retained project dependency is missing or ambiguous; it was not replaced.");
            var revision = revisions[0];
            var packages = manifest.Packages.Where(package => package.Id == revision.PackageId).ToArray();
            if (packages.Length != 1 || !packages[0].IsAvailable)
                throw new InvalidOperationException("A retained project dependency package is unavailable or ambiguous.");
            var package = packages[0];
            if (selected.TryGetValue(package.Id, out var previous))
            {
                if (previous != id) throw new InvalidOperationException("The project dependency graph pins conflicting revisions.");
                continue;
            }
            if (IsDistributionCore(package) && id != core.Id ||
                package.SourceKind == WccPackageSourceKinds.WesnothAddon && package.ParentPackageId != core.PackageId ||
                !string.IsNullOrWhiteSpace(revision.TargetWesnothVersion) && revision.TargetWesnothVersion != core.TargetWesnothVersion)
                throw new InvalidOperationException("The retained project graph conflicts with its explicitly pinned Core.");
            selected.Add(package.Id, id);
            foreach (var dependency in revision.DependencyRevisionIds) pending.Enqueue(dependency);
        }
        if (!selected.TryGetValue(core.PackageId, out var selectedCore) || selectedCore != core.Id)
            throw new InvalidOperationException("The project dependency graph has no exact explicitly pinned Core.");
    }

    public static IReadOnlyCollection<WccPackageRevisionId>? Resolve(
        WccLibraryManifest manifest,
        WccWorkingProjectDescriptor? project,
        WccPackageRevisionId? projectCoreRevisionId)
        => ResolveCore(manifest, project, projectCoreRevisionId, []);

    /// <summary>
    /// Makes literal add-on references observed in captured authored sources
    /// available to includes. This does not execute their entry points or rewrite metadata.
    /// Existing exact project pins remain selected before any new source is considered.
    /// </summary>
    public static IReadOnlyCollection<WccPackageRevisionId>? ResolveForSourceReferences(
        WccLibraryManifest manifest,
        WccWorkingProjectDescriptor? project,
        WccPackageRevisionId? projectCoreRevisionId,
        IReadOnlyCollection<string> referencedAddonIds)
    {
        ArgumentNullException.ThrowIfNull(referencedAddonIds);
        return ResolveCore(manifest, project, projectCoreRevisionId, referencedAddonIds);
    }

    private static IReadOnlyCollection<WccPackageRevisionId>? ResolveCore(
        WccLibraryManifest manifest,
        WccWorkingProjectDescriptor? project,
        WccPackageRevisionId? projectCoreRevisionId,
        IReadOnlyCollection<string> referencedAddonIds)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (project is null)
            return null;

        var revisions = manifest.PackageRevisions.ToDictionary(x => x.Id);
        var packages = manifest.Packages.ToDictionary(x => x.Id);
        var working = manifest.Packages.Where(package =>
            package.SourceKind == WccPackageSourceKinds.WccProject &&
            package.StorageMode == WccPackageStorageMode.WorkingCopy &&
            WccPackagePlanEditor.LocationsEqual(package.SourceLocation, project.RootDirectory)).ToArray();
        if (working.Length > 1)
            return Array.Empty<WccPackageRevisionId>();

        WccPackageRevisionRecord? FindSelectedRevision(WccPackageRecord package)
        {
            if (package.CurrentRevisionId is { } selectedId)
                return revisions.TryGetValue(selectedId, out var selected) && selected.PackageId == package.Id
                    ? selected : null;
            var candidates = manifest.PackageRevisions.Where(x => x.PackageId == package.Id).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }

        var retained = working.Length == 1 ? FindSelectedRevision(working[0]) : null;
        var result = retained?.DependencyRevisionIds.Distinct().ToList() ?? [];
        if (working.Length == 1 && retained is null)
            return result;
        if (projectCoreRevisionId is not { } coreId ||
            !revisions.TryGetValue(coreId, out var core) ||
            !packages.TryGetValue(core.PackageId, out var corePackage) || !corePackage.IsAvailable ||
            !IsDistributionCore(corePackage))
            return result;

        // Preserve the installed-overlay path: these are the same physical files
        // as the selected installed add-on, so use its already planned context.
        // An independently copied project never matches this branch by name alone.
        var sameLocation = manifest.Packages.Where(package =>
            package.SourceKind == WccPackageSourceKinds.WesnothAddon &&
            package.StorageMode == WccPackageStorageMode.ExternalReadOnly && package.IsAvailable &&
            package.ParentPackageId == core.PackageId &&
            WccPackagePlanEditor.LocationsEqual(package.SourceLocation, project.RootDirectory)).ToArray();
        if (sameLocation.Length > 1)
            return Array.Empty<WccPackageRevisionId>();
        if (sameLocation.Length == 1 && FindSelectedRevision(sameLocation[0]) is { } installed)
        {
            result = installed.DependencyRevisionIds.Where(id =>
                !revisions.TryGetValue(id, out var dependency) || dependency.PackageId != installed.PackageId)
                .Distinct().ToList();
            if (referencedAddonIds.Count == 0) return result;
        }
        else if (sameLocation.Length == 1)
            return result;

        // Validate the whole exact graph, including cycle back-edges. A dependency
        // that brings another revision of any pinned package cannot be added.
        bool TryGetClosure(IEnumerable<WccPackageRevisionId> roots,
            out Dictionary<WccPackageId, WccPackageRevisionRecord> closure)
        {
            closure = new();
            var pending = new Queue<WccPackageRevisionId>(roots);
            while (pending.TryDequeue(out var id))
            {
                if (!revisions.TryGetValue(id, out var revision) ||
                    !packages.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable)
                    return false;
                if (closure.TryGetValue(package.Id, out var prior))
                {
                    if (prior.Id != id)
                        return false;
                    continue;
                }
                if (package.Id == core.PackageId && id != coreId ||
                    IsDistributionCore(package) && package.Id != core.PackageId ||
                    package.SourceKind == WccPackageSourceKinds.WesnothAddon &&
                    package.ParentPackageId != core.PackageId ||
                    !string.IsNullOrWhiteSpace(revision.TargetWesnothVersion) &&
                    !string.IsNullOrWhiteSpace(core.TargetWesnothVersion) &&
                    !string.Equals(revision.TargetWesnothVersion, core.TargetWesnothVersion, StringComparison.Ordinal))
                    return false;
                closure.Add(package.Id, revision);
                foreach (var dependency in revision.DependencyRevisionIds)
                    pending.Enqueue(dependency);
            }
            return closure.TryGetValue(core.PackageId, out var foundCore) && foundCore.Id == coreId;
        }

        WccPackageRevisionRecord? FindCompatibleRevision(WccPackageRecord package)
        {
            bool IsCompatible(WccPackageRevisionRecord revision) =>
                TryGetClosure([revision.Id], out _) && TryGetClosure(result.Append(revision.Id), out _);
            if (package.CurrentRevisionId is { } currentId && revisions.TryGetValue(currentId, out var current) &&
                current.PackageId == package.Id && IsCompatible(current))
                return current;

            // An old project Core can have one matching historical add-on revision.
            // Never choose the newest timestamp when several historical graphs fit.
            var candidates = manifest.PackageRevisions.Where(x =>
                x.PackageId == package.Id && IsCompatible(x)).Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }

        var metadata = WccAddonMetadataReader.Read(project.RootDirectory).Metadata;
        if (!IsStaticMetadataField(metadata, "core"))
            return result;
        var usesDefaultCore = string.IsNullOrWhiteSpace(metadata.DeclaredCore) ||
            string.Equals(metadata.DeclaredCore, "default", StringComparison.Ordinal);
        if (retained is null)
        {
            if (usesDefaultCore)
                result.Add(coreId);
        }

        // Custom-Core graphs are supplied only by an existing explicit pin above.
        // Missing/dynamic declarations cannot authorize a default-Core substitution.
        if (!usesDefaultCore || !TryGetClosure(result, out var retainedClosure))
            return result.Distinct().ToArray();

        var declarations = IsStaticMetadataField(metadata, "dependencies")
            ? metadata.DeclaredDependencyIds : [];
        foreach (var addonId in declarations.Concat(referencedAddonIds).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(addonId, metadata.AddonId, StringComparison.Ordinal) ||
                retainedClosure.Values.Any(revision => IsAddonId(packages[revision.PackageId], revision, addonId)))
                continue;
            var candidates = manifest.Packages.Where(package =>
                package.IsAvailable && package.SourceKind == WccPackageSourceKinds.WesnothAddon &&
                package.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
                package.ParentPackageId == core.PackageId &&
                string.Equals(package.SourceIdentifier, addonId, StringComparison.Ordinal) &&
                !WccPackagePlanEditor.LocationsEqual(package.SourceLocation, project.RootDirectory)).ToArray();
            if (candidates.Length != 1 || FindCompatibleRevision(candidates[0]) is not { } selected)
                continue;
            result.Add(selected.Id);
            TryGetClosure(result, out retainedClosure);
        }
        return result.Distinct().ToArray();
    }

    private static bool IsAddonId(WccPackageRecord package, WccPackageRevisionRecord revision, string addonId)
        => package.SourceKind == WccPackageSourceKinds.WesnothAddon &&
           string.Equals(package.SourceIdentifier ?? revision.AddonMetadata?.AddonId, addonId, StringComparison.Ordinal);

    private static bool IsStaticMetadataField(WccAddonPackageMetadata metadata, string key)
    {
        if (metadata.MetadataSourceFileName is null)
            return true;
        if (metadata.RawMetadataText is not { } text)
            return false;
        // The metadata reader preserves unsupported declarations as raw text. In
        // particular, a null normalized Core can mean unknown rather than default.
        // Reject preprocessing even outside [info], where extracting the block
        // alone would lose the condition that decides whether it exists.
        var stringState = new WmlStringScanState();
        foreach (var line in text.Split('\n'))
        {
            var insideString = stringState.InsideMultilineString;
            WmlRawStringScanner.UpdateState(line, ref stringState);
            if (!insideString && WmlTopLevelCodeScanner.ClassifyTrimmedLine(line.Trim()) is not
                    (WmlTopLevelCodeKind.None or WmlTopLevelCodeKind.OtherHashDirective))
                return false;
        }
        if (stringState.InsideMultilineString)
            return false;
        var blocks = WmlAllBlockScanner.FindBlocks(text, "info");
        var block = blocks.Count == 1 ? blocks[0] : blocks.Count == 0 &&
            string.Equals(metadata.MetadataSourceFileName, "_server.pbl", StringComparison.OrdinalIgnoreCase)
                ? new WmlBlock("info", "[info]\n" + text + "\n[/info]") : null;
        if (block is null)
            return false;
        var editable = WmlEditableBlockParser.Parse(block, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key });
        var field = WmlTopLevelFieldAnalyzer.Analyze(editable, key);
        return field.Kind == WmlTopLevelFieldKind.None ||
            field.Kind == WmlTopLevelFieldKind.Plain && field.PlainValue is { } raw &&
            !raw.Contains('{') && !raw.Contains('}') && !raw.Contains('$');
    }

    private static bool IsDistributionCore(WccPackageRecord package)
        => package.ParentPackageId is null &&
           (package.SourceKind == WccPackageSourceKinds.WesnothCore ||
            package.SourceKind == WccPackageSourceKinds.WesnothMainline) &&
           (package.ContentKind == WccPackageContentKinds.Core ||
            package.ContentKind == WccPackageContentKinds.ResourcePack);
}
