using System.Text;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Library;

/// <summary>Immutable, read-only source evidence; it never grants editing authority.</summary>
public sealed class WccStaticSourceCaptureResult
{
    internal WccStaticSourceCaptureResult(WmlStaticSource? entrySource,
        IEnumerable<WmlStaticSource> preludes, IEnumerable<WmlStaticSource> sources,
        IWmlStaticIncludeResolver resolver, IEnumerable<string> diagnostics, string? coreVersion = null,
        IEnumerable<WmlStaticSource>? coreUnitPreludes = null, bool hasCompleteCoreUnitPrelude = false)
    {
        EntrySource = entrySource;
        PreludeSources = Array.AsReadOnly(preludes.ToArray());
        Sources = Array.AsReadOnly(sources.ToArray());
        IncludeResolver = resolver;
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
        CoreVersion = coreVersion;
        CoreUnitPreludeSources = Array.AsReadOnly((coreUnitPreludes ?? []).ToArray());
        HasCompleteCoreUnitPrelude = hasCompleteCoreUnitPrelude;
    }

    public WmlStaticSource? EntrySource { get; }
    public IReadOnlyList<WmlStaticSource> PreludeSources { get; }
    /// <summary>
    /// Optional engine Core unit-data include for reference checking. Append after the
    /// ordinary preludes and before the entry; never silently add it to editor/writer scopes.
    /// </summary>
    public IReadOnlyList<WmlStaticSource> CoreUnitPreludeSources { get; }
    /// <summary>
    /// The requested unit-data entry has one exact captured source. This does not prove
    /// its evaluation, nested includes, registry contents or any editing authority.
    /// </summary>
    public bool HasCompleteCoreUnitPrelude { get; }
    public IReadOnlyList<WmlStaticSource> Sources { get; }
    public IWmlStaticIncludeResolver IncludeResolver { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    /// <summary>Exact version of the unique, unambiguous pinned Core revision, when available.</summary>
    public string? CoreVersion { get; }
    public bool IsCaptureComplete => EntrySource is not null && Diagnostics.Count == 0;
}

/// <summary>
/// Captures only the selected revision and its exact dependency closure. Files are
/// available to ordered includes, never preloaded as a catalog of macro names.
/// Enumeration and reads run off the caller's thread. Virtual include mounts keep
/// a copied add-on usable outside a Wesnoth installation while origins retain the
/// actual source paths. This captures evidence for display, not a disk transaction.
/// </summary>
public static partial class WccStaticSourceCapture
{
    private const int MaxEntries = 200_000;
    private const int MaxDepth = 96;
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private const long MaxTextBytes = 64 * 1024 * 1024;
    private static readonly StringComparer DiskComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static Task<WccStaticSourceCaptureResult> CaptureAsync(
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId rootRevisionId,
        string entrySourcePath,
        IReadOnlyList<WmlStaticSource>? overlays = null,
        CancellationToken cancellationToken = default,
        string? configuredAddonsPath = null,
        bool includeCoreUnits = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(entrySourcePath);
        var capturedOverlays = overlays?.ToArray() ?? [];
        var entryPath = Path.GetFullPath(entrySourcePath);
        return Task.Run(() => Capture(snapshot, rootRevisionId, entryPath, capturedOverlays,
            configuredAddonsPath, cancellationToken, [], includeCoreUnits), cancellationToken);
    }

    // Only the private guarded writer supplies these flushed artifact revisions.
    // No public capture option or filename pattern can hide namespace membership.
    internal static Task<WccStaticSourceCaptureResult> CaptureForStagedCommitAsync(
        WccLibrarySnapshot snapshot, WccPackageRevisionId rootRevisionId, string entrySourcePath,
        IReadOnlyList<WccTextDiskRevision> ownedStages, CancellationToken cancellationToken,
        string? configuredAddonsPath)
    {
        var stages = ownedStages.ToArray();
        var entryPath = Path.GetFullPath(entrySourcePath);
        return Task.Run(() => Capture(snapshot, rootRevisionId, entryPath, [], configuredAddonsPath,
            cancellationToken, stages), cancellationToken);
    }

    private static WccStaticSourceCaptureResult Capture(WccLibrarySnapshot snapshot,
        WccPackageRevisionId rootRevisionId, string entryPath, WmlStaticSource[] overlays,
        string? configuredAddonsPath, CancellationToken cancellationToken,
        WccTextDiskRevision[] ownedStages, bool includeCoreUnits = false) => Capture(snapshot, rootRevisionId,
            entryPath, overlays, configuredAddonsPath, cancellationToken, ownedStages, includeCoreUnits, []);

    private static WccStaticSourceCaptureResult Capture(WccLibrarySnapshot snapshot,
        WccPackageRevisionId rootRevisionId, string entryPath, WmlStaticSource[] overlays,
        string? configuredAddonsPath, CancellationToken cancellationToken,
        WccTextDiskRevision[] ownedStages, bool includeCoreUnits, WccBinaryDiskRevision[] ownedBinaryStages,
        bool allowManagedMacroStages = false)
    {
        var diagnostics = new List<string>();
        // The explicitly selected host supplies physical namespace evidence.
        // Literal optional conditions can gain metadata-only existence receipts
        // below; this inventory never loads unpinned add-on contents.
        var addonNamespace = CaptureAddonNamespace(configuredAddonsPath, diagnostics, cancellationToken);
        // An opened working project without a configured host uses the exact
        // selected session mounts as its virtual add-on namespace. Unselected
        // siblings are neither inspected nor made active. Detached Library
        // captures still need an explicit host to establish absence.
        var usesSessionAddonNamespace = string.IsNullOrWhiteSpace(configuredAddonsPath) &&
            snapshot.RevisionsById.TryGetValue(rootRevisionId, out var sessionRevision) &&
            snapshot.PackagesById.TryGetValue(sessionRevision.PackageId, out var sessionPackage) &&
            sessionPackage.IsAvailable && sessionPackage.SourceKind == WccPackageSourceKinds.WccProject &&
            sessionPackage.StorageMode == WccPackageStorageMode.WorkingCopy;
        var roots = new List<Root>();
        var hasUnidentifiedPinnedRoot = false;
        var verifiedShadowAlias = FindVerifiedWorkingShadowAlias(snapshot, rootRevisionId);
        var visited = new HashSet<WccPackageRevisionId>();
        var pending = new Queue<WccPackageRevisionId>();
        pending.Enqueue(rootRevisionId);
        while (pending.TryDequeue(out var revisionId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(revisionId)) continue;
            if (!snapshot.RevisionsById.TryGetValue(revisionId, out var revision) ||
                !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package))
            {
                diagnostics.Add($"Pinned source revision {revisionId} is unavailable.");
                hasUnidentifiedPinnedRoot = true;
                continue;
            }
            foreach (var dependency in revision.DependencyRevisionIds.ToArray()) pending.Enqueue(dependency);
            // A working add-on can replace the read-only package at the same root
            // while a music/resource dependency still points back to that package.
            // Keep traversing its pinned edges, but capture the verified physical
            // source once through the working owner, as engine preprocessing does.
            if (revisionId == verifiedShadowAlias)
                continue;
            if (!package.IsAvailable || string.IsNullOrWhiteSpace(package.SourceLocation))
            {
                diagnostics.Add($"Source package '{package.Name}' has no available captured root.");
                hasUnidentifiedPinnedRoot = true;
                continue;
            }
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SourceLocation));
            var core = package.ParentPackageId is null &&
                (package.SourceKind == WccPackageSourceKinds.WesnothCore ||
                 package.SourceKind == WccPackageSourceKinds.WesnothMainline) &&
                (package.ContentKind == WccPackageContentKinds.ResourcePack ||
                 package.ContentKind == WccPackageContentKinds.Core);
            var identifier = package.SourceIdentifier ?? revision.AddonMetadata?.AddonId ?? Path.GetFileName(path);
            // Mainline resource inventories keep their logical data-root name when
            // moved to a captured package location. Only this exact dependency closure
            // can supply the mount; never reach into a live installation's siblings.
            var mount = core ? "core" : package.SourceKind == WccPackageSourceKinds.WesnothMainline
                ? package.ContentKind == WccPackageContentKinds.ResourcePack &&
                  package.ParentPackageId is not null && !string.IsNullOrWhiteSpace(package.SourceIdentifier)
                    ? package.SourceIdentifier
                    : "campaigns/" + Path.GetFileName(path)
                : "~add-ons/" + identifier;
            var pins = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == revisionId)
                .Select(file => new Pin(file.RelativePath.Replace('\\', '/'), file.StoragePath,
                    file.ContentHash)).ToArray();
            roots.Add(new Root(path, mount, revisionId, package.StorageMode == WccPackageStorageMode.WorkingCopy,
                core, pins)
            {
                IsManagedLibrary = package.SourceKind == WccPackageSourceKinds.WccManagedLibrary,
                IsResourceOnly = package.SourceKind == WccPackageSourceKinds.WesnothMainline &&
                    package.ContentKind == WccPackageContentKinds.ResourcePack && package.ParentPackageId is not null &&
                    WmlResourceLookupPaths.IsInstallationResourceMount(mount),
                // A working campaign owns its include roots wherever it is stored.
                // Both source spellings address this same captured package; this does
                // not identify a copy of a live campaign or load any sibling campaign.
                CampaignMount = package.StorageMode == WccPackageStorageMode.WorkingCopy &&
                    package.SourceKind == WccPackageSourceKinds.WccProject &&
                    (package.ContentKind == WccPackageContentKinds.Campaign ||
                     package.ContentKind == WccPackageContentKinds.Project)
                        ? "campaigns/" + identifier : null
            });
        }

        AddInstalledResourceRoots(snapshot, roots, rootRevisionId, cancellationToken);

        if (ownedStages.Length != 0)
        {
            var stageRoots = roots.Where(root => root.Revision == rootRevisionId
                && (root.IsWorkingCopy || allowManagedMacroStages && root.IsManagedLibrary)).ToArray();
            if (stageRoots.Length != 1 || ownedStages.Select(stage => stage.SourcePath).Distinct(DiskComparer).Count() != ownedStages.Length)
                throw new IOException("The writer staging artifacts do not belong to one unique working source root.");
            var stageRoot = stageRoots[0];
            foreach (var stage in ownedStages)
            {
                if (!DiskComparer.Equals(stage.ProjectRootPath, stageRoot.Path) || !Contains(stageRoot.Path, stage.SourcePath)
                    || DiskComparer.Equals(stage.SourcePath, entryPath)
                    || stageRoot.Pins.Any(pin => DiskComparer.Equals(pin.RelativePath, Relative(stageRoot.Path, stage.SourcePath))))
                    throw new IOException("A writer artifact cannot exclude a source entry or a foreign project route.");
                RequireUnchangedOwnedStage(stage);
                stageRoot.OwnedStagePaths.Add(stage.SourcePath);
            }
        }

        if (ownedBinaryStages.Length != 0)
        {
            var stageRoots = roots.Where(root => root.Revision == rootRevisionId
                && (root.IsWorkingCopy || allowManagedMacroStages && root.IsManagedLibrary)).ToArray();
            if (stageRoots.Length != 1 || ownedBinaryStages.Select(stage => stage.SourcePath).Concat(ownedStages.Select(stage => stage.SourcePath))
                .Distinct(DiskComparer).Count() != ownedBinaryStages.Length + ownedStages.Length)
                throw new IOException("The binary writer artifacts do not belong to one unique working source root.");
            var stageRoot = stageRoots[0];
            foreach (var stage in ownedBinaryStages)
            {
                if (!DiskComparer.Equals(stage.ProjectRootPath, stageRoot.Path) || !Contains(stageRoot.Path, stage.SourcePath)
                    || DiskComparer.Equals(stage.SourcePath, entryPath)
                    || stageRoot.Pins.Any(pin => DiskComparer.Equals(pin.RelativePath, Relative(stageRoot.Path, stage.SourcePath))))
                    throw new IOException("A binary writer artifact cannot exclude a source entry or a foreign route.");
                var current = WccTextDiskRevisionCapture.CaptureBinary(stage.ProjectRootPath, stage.SourcePath);
                if (!current.IsCaptured || current.Revision is null || !stage.IsSameRevisionAs(current.Revision))
                    throw new IOException("A binary staging artifact changed before source capture.");
                stageRoot.OwnedStagePaths.Add(stage.SourcePath);
            }
        }

        // A skipped pinned root may reserve an add-on name absent from this host.
        // Without all pinned mount identities, host absence cannot override the
        // unknown closure evidence, including names of unavailable dependencies.
        if (hasUnidentifiedPinnedRoot) addonNamespace = null;

        foreach (var collision in roots.SelectMany(root => root.Mounts.Select(mount => (Root: root, Mount: mount)))
                     .GroupBy(binding => binding.Mount, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            diagnostics.Add($"More than one pinned package occupies include root '{collision.Key}'.");
            foreach (var binding in collision) binding.Root.IsAmbiguous = true;
        }
        foreach (var collision in roots.GroupBy(root => root.Path, DiskComparer).Where(group => group.Count() > 1))
        {
            diagnostics.Add($"More than one pinned revision occupies source root '{collision.Key}'.");
            foreach (var root in collision) root.IsAmbiguous = true;
        }

        var overlayMap = new Dictionary<string, WmlStaticSource>(DiskComparer);
        var conflictingOverlays = new HashSet<string>(DiskComparer);
        foreach (var overlay in overlays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!overlay.IsAuthored)
            {
                diagnostics.Add("Synthetic environment definitions cannot replace captured campaign source files.");
                continue;
            }
            var owners = roots.Where(root => Contains(root.Path, overlay.Path)).ToArray();
            if (owners.Length != 1 || !owners[0].IsWorkingCopy)
            {
                diagnostics.Add($"The source overlay '{overlay.Path}' is outside a unique working package.");
                continue;
            }
            if (overlayMap.TryGetValue(overlay.Path, out var existing) && existing.ContentHash != overlay.ContentHash)
            {
                diagnostics.Add($"The source overlay '{overlay.Path}' has conflicting revisions.");
                conflictingOverlays.Add(overlay.Path);
            }
            else overlayMap[overlay.Path] = overlay;
        }
        foreach (var path in conflictingOverlays) overlayMap.Remove(path);

        var count = 0;
        long textBytes = 0;
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root.IsAmbiguous) continue;
            var previousCount = count;
            try
            {
                CaptureRoot(root, overlayMap, conflictingOverlays, diagnostics, ref count, ref textBytes,
                    cancellationToken);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                root.IsComplete = false;
                if (root.IsOptionalInstallationResource)
                {
                    // Optional asset evidence cannot make unrelated campaign WML
                    // unavailable. Revoke the whole failed folder and its budget;
                    // a requested route remains Unknown in the ordinary resolver.
                    root.Entries.Clear();
                    count = previousCount;
                }
                else diagnostics.Add($"Could not completely capture '{root.Path}': {ex.Message}");
            }
        }
        // Unknown-format text is optional inventory. Read it only after all cfg and
        // established text sources, so a package's extensionless assets cannot take
        // the bounded budget away from its pinned Core or dependency definitions.
        var optionalReadBytes = MaxTextBytes - textBytes;
        foreach (var root in roots.Where(root => root.IsComplete && !root.IsAmbiguous))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureExtensionlessText(root, ref optionalReadBytes, diagnostics, cancellationToken);
        }
        if (addonNamespace is not null)
        {
            // Literal condition paths in the frozen sources may ask about an
            // optional, unpinned add-on. Capture metadata only: this cannot load
            // its WML, macros or resources into the dependency closure.
            var conditionPaths = roots.SelectMany(root => root.Entries.Values)
                .Select(record => record.Source).OfType<WmlStaticSource>()
                .SelectMany(source => System.Text.RegularExpressions.Regex.Matches(source.Text,
                    @"(?m)^[ \t]*#ifn?have[ \t]+([^\r\n]+)")
                    .Select(match => match.Groups[1].Value.Trim()))
                .Where(path => CapturedResolver.TryNormalizePath(path, out var normalized) &&
                    normalized.StartsWith("~add-ons/", StringComparison.Ordinal))
                .Select(path => { CapturedResolver.TryNormalizePath(path, out var normalized); return normalized; })
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(4097).ToArray();
            if (conditionPaths.Length <= 4096)
                addonNamespace = CaptureAddonConditions(addonNamespace, conditionPaths, cancellationToken);
            var finalNamespace = CaptureAddonNamespace(configuredAddonsPath, diagnostics, cancellationToken);
            if (finalNamespace is not null && conditionPaths.Length <= 4096)
                finalNamespace = CaptureAddonConditions(finalNamespace, conditionPaths, cancellationToken);
            if (finalNamespace is null || addonNamespace.RootStamp != finalNamespace.RootStamp ||
                !addonNamespace.Names.SequenceEqual(finalNamespace.Names, StringComparer.Ordinal) ||
                !addonNamespace.Conditions.SequenceEqual(finalNamespace.Conditions))
            {
                diagnostics.Add("The configured add-on namespace changed during source capture.");
                addonNamespace = null;
            }
        }
        var resolver = new CapturedResolver(roots, addonNamespace, usesSessionAddonNamespace && !hasUnidentifiedPinnedRoot);
        var entryOwners = roots.Where(root => Contains(root.Path, entryPath)).ToArray();
        WmlStaticSource? entry = null;
        if (entryOwners.Length == 1 && !entryOwners[0].IsAmbiguous &&
            entryOwners[0].Entries.TryGetValue(Relative(entryOwners[0].Path, entryPath), out var entryRecord))
            entry = entryRecord.Source;
        if (entry is null) diagnostics.Add($"The selected entry '{entryPath}' has no exact captured source.");

        var cores = roots.Where(root => root.IsCore && !root.IsAmbiguous).ToArray();
        IReadOnlyList<WmlStaticSource> preludes = [];
        IReadOnlyList<WmlStaticSource> coreUnitPreludes = [];
        var hasCompleteCoreUnitPrelude = false;
        if (cores.Length == 1)
        {
            var caller = entry ?? new WmlStaticSource(entryPath, "");
            var orderedPreludes = new List<WmlStaticSource>();
            var hasPinnedThemes = snapshot.RevisionsById.TryGetValue(cores[0].Revision, out var selectedCore) &&
                selectedCore.DependencyRevisionIds.Any(id =>
                    snapshot.RevisionsById.TryGetValue(id, out var dependency) &&
                    snapshot.PackagesById.TryGetValue(dependency.PackageId, out var package) &&
                    package.SourceKind == WccPackageSourceKinds.WesnothMainline &&
                    package.ContentKind == WccPackageContentKinds.ResourcePack &&
                    package.ParentPackageId == selectedCore.PackageId && package.SourceIdentifier == "themes");
            if (hasPinnedThemes)
            {
                // data/_main.cfg unconditionally loads themes before Core. Preserve
                // the include even when its pinned inventory is incomplete, so missing
                // definitions remain unknown rather than silently absent. Minimal/older
                // snapshots without this inventory keep their existing explicit prelude.
                orderedPreludes.Add(new WmlStaticSource(
                    $"wcc-static://core-prelude/{cores[0].Revision}/themes.cfg",
                    "{themes/}\n", isAuthored: false));
                if (resolver.Resolve("themes/", caller).Exists != WmlStaticTruth.True)
                    diagnostics.Add("The selected Core theme prelude is not completely captured.");
            }
            var terrainGraphics = resolver.Resolve("core/terrain-graphics/", caller);
            if (terrainGraphics.Exists != WmlStaticTruth.False)
            {
                // Core/_main.cfg loads terrain graphics before general macros. Keep its
                // condition in generated host evidence so each evaluation context decides
                // whether the captured terrain definitions are loaded.
                orderedPreludes.Add(new WmlStaticSource(
                    $"wcc-static://core-prelude/{cores[0].Revision}/terrain-graphics.cfg",
                    "#ifndef NO_TERRAIN_GFX\n{core/terrain-graphics/}\n#endif\n", isAuthored: false));
                if (terrainGraphics.Exists != WmlStaticTruth.True)
                    diagnostics.Add("The selected Core terrain-graphics prelude is not completely captured.");
            }
            var coreMacros = resolver.Resolve("core/macros", caller);
            if (coreMacros.Exists == WmlStaticTruth.True) orderedPreludes.AddRange(coreMacros.Sources);
            else diagnostics.Add("The selected Core macro prelude is not completely captured.");
            preludes = orderedPreludes;
            if (includeCoreUnits)
            {
                // Installed data/_main.cfg loads Core before campaigns. Core/_main.cfg
                // unconditionally includes units.cfg after its macros; let the same
                // captured evaluator follow the actual nested includes and conditions.
                // Preserve unavailable data as an include, never an empty known registry.
                var units = resolver.Resolve("core/units.cfg", caller);
                coreUnitPreludes = [new WmlStaticSource(
                    $"wcc-static://core-unit-prelude/{cores[0].Revision}/units.cfg",
                    "{core/units.cfg}\n", isAuthored: false)];
                hasCompleteCoreUnitPrelude = units.Exists == WmlStaticTruth.True && units.Sources.Count == 1
                    && units.Sources[0].IsAuthored && DiskComparer.Equals(units.Sources[0].Path,
                        Path.Combine(cores[0].Path, "units.cfg"));
                if (!hasCompleteCoreUnitPrelude)
                    diagnostics.Add("The requested Core unit-data prelude is not completely captured.");
            }
        }
        else diagnostics.Add("A unique pinned Core revision is unavailable for the macro prelude.");
        var coreVersion = cores.Length == 1 && snapshot.RevisionsById.TryGetValue(cores[0].Revision, out var coreRevision)
            ? coreRevision.Version : null;
        foreach (var stage in ownedStages) RequireUnchangedOwnedStage(stage);
        return new(entry, preludes, roots.SelectMany(root => root.Entries.Values)
            .Select(record => record.Source).OfType<WmlStaticSource>(), resolver, diagnostics, coreVersion,
            coreUnitPreludes, hasCompleteCoreUnitPrelude);
    }

    private static void RequireUnchangedOwnedStage(WccTextDiskRevision expected)
    {
        var captured = WccTextDiskRevisionCapture.Capture(expected.ProjectRootPath, expected.SourcePath, expected.Text);
        if (!captured.IsCaptured || captured.Revision is null || !expected.IsSameRevisionAs(captured.Revision))
            throw new IOException("A writer-owned staging artifact changed during source capture.");
    }

    private static AddonNamespace? CaptureAddonNamespace(string? configuredPath, List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;
        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath));
            var directory = new DirectoryInfo(path);
            if (!directory.Exists) throw new DirectoryNotFoundException(path);
            for (var ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0 || ancestor.LinkTarget is not null)
                    throw new IOException("A linked add-on namespace ancestor cannot establish existence or absence.");
            var stamp = directory.CreationTimeUtc.Ticks;
            var names = new List<string>();
            foreach (var child in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (names.Count >= MaxEntries) throw new IOException("The add-on namespace inventory limit was reached.");
                names.Add(child.Name);
            }
            names.Sort(StringComparer.Ordinal);
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                throw new IOException("The add-on namespace contains ambiguous casing.");
            directory.Refresh();
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                directory.CreationTimeUtc.Ticks != stamp)
                throw new IOException("The add-on namespace root changed during capture.");
            return new(path, stamp, names.ToArray());
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            diagnostics.Add($"Could not capture the configured add-on namespace: {ex.Message}");
            return null;
        }
    }

    private static WccPackageRevisionId? FindVerifiedWorkingShadowAlias(
        WccLibrarySnapshot snapshot, WccPackageRevisionId rootRevisionId)
    {
        if (!snapshot.RevisionsById.TryGetValue(rootRevisionId, out var rootRevision))
            return null;

        var closure = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, rootRevisionId);
        // The existing alias contract verifies current working/shadow ownership,
        // identical physical root and fingerprint, and the exact selected Core.
        // Multiple possible aliases (or Core bindings) must remain unresolved.
        var aliases = rootRevision.DependencyRevisionIds
            .SelectMany(coreId => closure.Where(candidate =>
                WccWorkingAddonOverlayRevisionAlias.IsVerified(snapshot, rootRevisionId, candidate, coreId)))
            .Take(2).ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }

    private static void CaptureRoot(Root root, Dictionary<string, WmlStaticSource> overlays,
        HashSet<string> conflictingOverlays, List<string> diagnostics, ref int count, ref long textBytes,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root.Path)) throw new DirectoryNotFoundException(root.Path);
        if ((File.GetAttributes(root.Path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A linked source root cannot be captured by this host.");
        var pins = root.Pins.GroupBy(pin => pin.RelativePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var initial = Enumerate(root.Path, ref count, cancellationToken, root.OwnedStagePaths);
        foreach (var item in initial)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = new Entry(item.IsDirectory, item.Stamp);
            root.Entries.Add(item.RelativePath, record);
            if (item.IsDirectory || root.IsResourceOnly) continue;
            var path = Path.Combine(root.Path, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (conflictingOverlays.Contains(path))
            {
                record.Diagnostic = "The open source has conflicting overlays.";
                continue;
            }
            if (overlays.TryGetValue(path, out var overlay))
            {
                var overlayBytes = Encoding.UTF8.GetByteCount(overlay.Text);
                if (overlayBytes > MaxFileBytes || textBytes + overlayBytes > MaxTextBytes)
                {
                    record.Diagnostic = "The bounded text capture limit was reached by an open source.";
                    diagnostics.Add($"Could not capture '{path}': {record.Diagnostic}");
                }
                else
                {
                    record.Source = overlay;
                    textBytes += overlayBytes;
                }
                continue;
            }
            var isCfg = item.RelativePath.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);
            if (!isCfg && !IsTextFile(item.RelativePath))
            {
                if (Path.GetExtension(item.RelativePath).Length == 0) root.ExtensionlessFiles.Add(item);
                continue;
            }
            try
            {
                if (item.Length > MaxFileBytes || textBytes + item.Length > MaxTextBytes)
                    throw new IOException("The bounded text capture limit was reached.");
                var text = ReadText(path, MaxTextBytes - textBytes, out var actualBytes);
                textBytes += actualBytes;
                if (actualBytes != item.Length)
                    throw new IOException("The source length changed during capture.");
                var source = new WmlStaticSource(path, text);
                if (isCfg)
                {
                    if (!pins.TryGetValue(item.RelativePath, out var candidates) || candidates.Length != 1)
                        throw new IOException("The cfg file has no unique indexed revision.");
                    var pin = candidates[0];
                    if (string.IsNullOrWhiteSpace(pin.StoragePath) ||
                        !DiskComparer.Equals(Path.GetFullPath(pin.StoragePath), path) ||
                        !string.Equals(pin.ContentHash, source.ContentHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The cfg file no longer matches its indexed revision.");
                }
                record.Source = source;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                record.Diagnostic = ex.Message;
                diagnostics.Add($"Could not capture '{path}': {ex.Message}");
            }
        }
        root.Entries.TryAdd("", new Entry(true, "root"));
        foreach (var pin in root.Pins)
        {
            if (root.Entries.ContainsKey(pin.RelativePath)) continue;
            diagnostics.Add($"Indexed source '{root.Path}/{pin.RelativePath}' is missing from the capture.");
        }
        var finalCount = 0;
        var final = Enumerate(root.Path, ref finalCount, cancellationToken, root.OwnedStagePaths);
        if (!initial.SequenceEqual(final))
        {
            root.Entries.Clear();
            throw new IOException("The source directory changed during capture.");
        }
        root.Inventory = initial;
        root.IsComplete = true;
    }

    private static void CaptureExtensionlessText(Root root, ref long remainingReadBytes,
        List<string> diagnostics, CancellationToken cancellationToken)
    {
        if (root.IsResourceOnly || root.ExtensionlessFiles.Count == 0) return;
        foreach (var item in root.ExtensionlessFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = root.Entries[item.RelativePath];
            var path = Path.Combine(root.Path, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (item.Length > MaxFileBytes || item.Length > remainingReadBytes)
                    throw new IOException("The bounded optional text capture limit was reached.");
                // Charge attempted reads too: many binary files must not make the
                // optional decoding pass unbounded even when none produce text.
                remainingReadBytes -= item.Length;
                var text = ReadText(path, item.Length, out var actualBytes);
                if (actualBytes != item.Length)
                    throw new IOException("The source length changed during capture.");
                if (text.Any(character => char.IsControl(character) && character is not ('\t' or '\r' or '\n')))
                    throw new IOException("The existing file contains binary control characters rather than supported text.");
                record.Source = new WmlStaticSource(path, text);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                // An unrelated binary resource does not invalidate cfg evidence.
                // Existence stays known; an explicit include reports this reason.
                record.Diagnostic = ex.Message;
            }
        }
        try
        {
            var finalCount = 0;
            var final = Enumerate(root.Path, ref finalCount, cancellationToken, root.OwnedStagePaths);
            if (!root.Inventory.SequenceEqual(final))
                throw new IOException("The source directory changed during optional text capture.");
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            root.Entries.Clear();
            root.IsComplete = false;
            diagnostics.Add($"Could not completely capture '{root.Path}': {ex.Message}");
        }
    }

    private static List<InventoryItem> Enumerate(string root, ref int count, CancellationToken cancellationToken)
        => Enumerate(root, ref count, cancellationToken, new HashSet<string>(DiskComparer));

    private static List<InventoryItem> Enumerate(string root, ref int count, CancellationToken cancellationToken,
        IReadOnlySet<string> ownedStagePaths)
    {
        var result = new List<InventoryItem>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (next.Depth > MaxDepth) throw new IOException("The source directory depth limit was reached.");
            foreach (var info in new DirectoryInfo(next.Path).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > MaxEntries) throw new IOException("The source inventory limit was reached.");
                var path = info.FullName;
                var attributes = info.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Linked source entry '{path}' cannot be captured by this host.");
                var directory = (attributes & FileAttributes.Directory) != 0;
                if (!directory && ownedStagePaths.Contains(path)) continue;
                var length = directory ? 0 : ((FileInfo)info).Length;
                // Directory times may settle after child creation without membership changes.
                // The complete inventory compares membership; only file stamps track content changes.
                result.Add(new(Relative(root, path), directory, length,
                    directory ? "directory" : $"{length}:{info.LastWriteTimeUtc.Ticks}"));
                if (directory) pending.Push((path, next.Depth + 1));
            }
        }
        result.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return result;
    }

    private static string ReadText(string path, long remainingBytes, out int actualBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        if (length > MaxFileBytes || length > remainingBytes)
            throw new IOException("The bounded text capture limit was reached.");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("The source length changed during capture.");
        actualBytes = bytes.Length;
        Encoding encoding = new UTF8Encoding(false, true);
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
        { encoding = new UTF32Encoding(false, false, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
        { encoding = new UTF32Encoding(true, false, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
        return encoding.GetString(bytes.AsSpan(skip));
    }

    private static bool IsTextFile(string path) =>
        string.Equals(path.Replace('\\', '/'), ".wcc/package-authoring.json", StringComparison.Ordinal) ||
        Path.GetExtension(path).ToLowerInvariant() is
        ".map" or ".mask" or ".lua" or ".wml" or ".inc" or ".txt" or ".pbl";
    private static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        DecoderFallbackException or ArgumentException or NotSupportedException;
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool Contains(string root, string path) => DiskComparer.Equals(root, path) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record Pin(string RelativePath, string? StoragePath, string ContentHash);
    private sealed record AddonCondition(string Path, WmlStaticTruth Exists, string Identity);
    private sealed record AddonNamespace(string Path, long RootStamp, string[] Names)
    {
        public AddonCondition[] Conditions { get; init; } = [];
    }

    private static AddonNamespace CaptureAddonConditions(AddonNamespace host, string[] paths,
        CancellationToken cancellationToken)
    {
        var conditions = new List<AddonCondition>();
        var entries = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = new StringBuilder(host.Path);
            var exists = WmlStaticTruth.Unknown;
            try
            {
                var parts = path[9..].TrimEnd('/').Split('/');
                if (parts.Length > MaxDepth) throw new IOException("Condition path depth limit.");
                FileSystemInfo current = new DirectoryInfo(host.Path);
                foreach (var part in parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current.Refresh();
                    if (current is not DirectoryInfo directory || !current.Exists ||
                        (current.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Unproven condition ancestor.");
                    identity.Append('|').Append(directory.FullName).Append(':')
                        .Append(directory.CreationTimeUtc.Ticks).Append(':').Append(directory.LastWriteTimeUtc.Ticks);
                    var candidates = new List<FileSystemInfo>();
                    foreach (var child in directory.EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++entries > MaxEntries) throw new IOException("Condition inventory limit.");
                        if (string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase)) candidates.Add(child);
                    }
                    if (candidates.Count == 0)
                    {
                        exists = WmlStaticTruth.False;
                        break;
                    }
                    if (candidates.Count != 1 || candidates[0].Name != part)
                        throw new IOException("Ambiguous condition casing.");
                    current = candidates[0];
                    current.Refresh();
                    if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Unproven condition entry.");
                    identity.Append('|').Append(current.Name).Append(':').Append((int)current.Attributes)
                        .Append(':').Append(current.CreationTimeUtc.Ticks).Append(':').Append(current.LastWriteTimeUtc.Ticks);
                    if (current is FileInfo file) identity.Append(':').Append(file.Length);
                    exists = WmlStaticTruth.True;
                }
                if (path.EndsWith('/') && exists == WmlStaticTruth.True && current is not DirectoryInfo)
                    exists = WmlStaticTruth.False;
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                exists = WmlStaticTruth.Unknown;
                identity.Append('|').Append(exception.GetType().Name);
            }
            conditions.Add(new(path, exists, WccContentHash.ComputeText(identity.ToString())));
        }
        return host with { Conditions = conditions.ToArray() };
    }
    private sealed record InventoryItem(string RelativePath, bool IsDirectory, long Length, string Stamp);
    private sealed class Entry(bool isDirectory, string stamp)
    {
        public bool IsDirectory { get; } = isDirectory;
        public string Stamp { get; } = stamp;
        public WmlStaticSource? Source { get; set; }
        public string? Diagnostic { get; set; }
    }
    private sealed class Root(string path, string mount, WccPackageRevisionId revision,
        bool isWorkingCopy, bool isCore, Pin[] pins)
    {
        public string Path { get; } = path;
        public string Mount { get; set; } = mount;
        public string? CampaignMount { get; init; }
        public IEnumerable<string> Mounts => CampaignMount is null || CampaignMount == Mount
            ? [Mount] : [Mount, CampaignMount];
        public WccPackageRevisionId Revision { get; } = revision;
        public bool IsWorkingCopy { get; } = isWorkingCopy;
        public bool IsCore { get; } = isCore;
        public bool IsManagedLibrary { get; init; }
        public bool IsResourceOnly { get; init; }
        public bool IsOptionalInstallationResource { get; init; }
        public Pin[] Pins { get; } = pins;
        public HashSet<string> OwnedStagePaths { get; } = new(DiskComparer);
        public bool IsComplete { get; set; }
        public bool IsAmbiguous { get; set; }
        public IReadOnlyList<InventoryItem> Inventory { get; set; } = [];
        public List<InventoryItem> ExtensionlessFiles { get; } = [];
        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string[]> CaseAliases { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, KeyValuePair<string, Entry>[]> Children { get; private set; } = new(StringComparer.Ordinal);

        public void BuildIndexes()
        {
            CaseAliases = Entries.Keys.GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            Children = Entries.Where(pair => pair.Key.Length > 0)
                .GroupBy(pair => pair.Key.Contains('/') ? pair.Key[..pair.Key.LastIndexOf('/')] : "", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        }
    }

    private sealed class CapturedResolver : IWmlStaticExistenceResolver, IWmlStaticResourceResolver
    {
        private readonly Root[] _roots;
        private readonly AddonNamespace? _addonNamespace;
        private readonly string _identity;
        public IReadOnlyList<Root> CapturedRoots => _roots;
        public AddonNamespace? CapturedAddonNamespace => _addonNamespace;
        public bool UsesSessionAddonNamespace { get; }

        public CapturedResolver(IEnumerable<Root> roots, AddonNamespace? addonNamespace, bool usesSessionAddonNamespace)
        {
            _roots = roots.ToArray();
            _addonNamespace = addonNamespace;
            UsesSessionAddonNamespace = usesSessionAddonNamespace;
            InstallationResourceFolders = Array.AsReadOnly(_roots.Where(root => root.IsResourceOnly)
                .Select(root => root.Mount[WmlResourceLookupPaths.InstallationResourceMountPrefix.Length..])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            var identity = new StringBuilder("wcc-static-capture-v2;");
            Append(identity, usesSessionAddonNamespace ? "session-addon-namespace" : "host-addon-namespace");
            if (addonNamespace is not null)
            {
                Append(identity, addonNamespace.Path);
                foreach (var name in addonNamespace.Names) Append(identity, name);
                foreach (var condition in addonNamespace.Conditions)
                {
                    Append(identity, condition.Path); Append(identity, condition.Exists.ToString());
                    Append(identity, condition.Identity);
                }
            }
            Append(identity, "end-addon-namespace");
            foreach (var root in _roots.OrderBy(root => root.Mount, StringComparer.Ordinal)
                         .ThenBy(root => root.Revision.ToString(), StringComparer.Ordinal))
            {
                root.BuildIndexes();
                Append(identity, root.Path);
                foreach (var mount in root.Mounts.OrderBy(value => value, StringComparer.Ordinal)) Append(identity, mount);
                Append(identity, root.Revision.ToString());
                Append(identity, root.IsResourceOnly.ToString());
                Append(identity, root.IsComplete.ToString()); Append(identity, root.IsAmbiguous.ToString());
                foreach (var entry in root.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    Append(identity, entry.Key); Append(identity, entry.Value.IsDirectory.ToString());
                    Append(identity, entry.Value.Source?.ContentHash ?? entry.Value.Stamp);
                    Append(identity, entry.Value.Diagnostic ?? "");
                }
            }
            _identity = WccContentHash.ComputeText(identity.ToString());
        }

        public WmlStaticIncludeResolution Resolve(string includePath, WmlStaticSource includingSource) =>
            ResolveCore(includePath, includingSource, existenceOnly: false);
        public WmlStaticIncludeResolution ResolveExistence(string includePath, WmlStaticSource includingSource) =>
            ResolveCore(includePath, includingSource, existenceOnly: true);

        public WmlStaticIncludeResolution ResolveResource(string virtualPath, WmlStaticSource includingSource) =>
            ResolveCore(virtualPath, includingSource, existenceOnly: true, requireFile: true);

        public IReadOnlyList<string> InstallationResourceFolders { get; }

        private WmlStaticIncludeResolution ResolveCore(string path, WmlStaticSource caller, bool existenceOnly,
            bool requireFile = false)
        {
            ArgumentNullException.ThrowIfNull(caller);
            if (!TryNormalizePath(path, out var normalized, allowResourceParentheses: requireFile))
                return Result(path, WmlStaticTruth.Unknown, [], "The include path is unsupported or dynamic.");
            if (!TryRoute(normalized, caller, out var root, out var relative))
            {
                if (IsAbsentAddon(normalized)) return Result(path, WmlStaticTruth.False, [], null);
                if (existenceOnly && !requireFile && _addonNamespace is { } host &&
                    host.Conditions.FirstOrDefault(condition => condition.Path == normalized) is { } condition &&
                    !_roots.SelectMany(candidate => candidate.Mounts).Any(mount =>
                        normalized.Equals(mount, StringComparison.OrdinalIgnoreCase) ||
                        normalized.StartsWith(mount + "/", StringComparison.OrdinalIgnoreCase)))
                    return Result(path, condition.Exists, [], condition.Exists == WmlStaticTruth.Unknown
                        ? "The optional add-on condition path has no exact captured existence evidence." : null);
                return Result(path, WmlStaticTruth.Unknown, [], "The include has no unique captured source root.");
            }
            if (root!.IsAmbiguous || HasCaseAmbiguity(root, relative))
                return Result(path, WmlStaticTruth.Unknown, [], "The include path has ambiguous source identities or casing.");
            if (root.IsResourceOnly && !requireFile)
                return Result(path, WmlStaticTruth.Unknown, [], "The captured installation asset mount supplies resource evidence only, not WML includes or preprocessor conditions.");
            if (!root.Entries.TryGetValue(relative, out var entry))
                return Result(path, root.IsComplete ? WmlStaticTruth.False : WmlStaticTruth.Unknown, [],
                    root.IsComplete ? null : "The source root was not completely captured.");
            if (!entry.IsDirectory && path.EndsWith('/'))
                return Result(path, WmlStaticTruth.Unknown, [], "A directory include refers to a file.");
            if (requireFile && entry.IsDirectory)
                return Result(path, WmlStaticTruth.False, [], "The captured path is a directory, not a resource file.");
            if (existenceOnly) return Result(path, WmlStaticTruth.True, [], null);
            if (!entry.IsDirectory)
                return entry.Source is { } source ? Result(path, WmlStaticTruth.True, [source], null) :
                    Result(path, WmlStaticTruth.Unknown, [], entry.Diagnostic ?? "The existing file has no captured text content.");
            var ordered = new List<WmlStaticSource>();
            return Expand(root, relative, ordered, out var diagnostic)
                ? Result(path, WmlStaticTruth.True, ordered, null)
                : Result(path, WmlStaticTruth.Unknown, [], diagnostic);
        }

        private static bool Expand(Root root, string directory, List<WmlStaticSource> output, out string? diagnostic)
        {
            diagnostic = null;
            var prefix = directory.Length == 0 ? "" : directory + "/";
            var mainPath = prefix + "_main.cfg";
            if (HasCaseAmbiguity(root, mainPath))
            {
                diagnostic = "The directory entry has ambiguous casing.";
                return false;
            }
            if (root.Entries.TryGetValue(mainPath, out var main))
            {
                if (main.Source is null || main.IsDirectory)
                {
                    diagnostic = main.Diagnostic ?? "The directory entry has no captured text.";
                    return false;
                }
                output.Add(main.Source);
                return true;
            }
            if (!root.IsComplete)
            {
                diagnostic = "The complete directory include order was not captured.";
                return false;
            }
            var files = new List<KeyValuePair<string, Entry>>();
            foreach (var child in root.Children.GetValueOrDefault(directory) ?? [])
            {
                var name = child.Key[prefix.Length..];
                if (name.StartsWith('.') || child.Value.IsDirectory && name is "images" or "sounds") continue;
                if (HasCaseAmbiguity(root, child.Key))
                {
                    diagnostic = "The directory contains ambiguous source casing.";
                    return false;
                }
                if (child.Value.IsDirectory)
                {
                    // Ordinary subdirectories are not recursively included by Wesnoth;
                    // only an immediate child package entry contributes to this include.
                    var childMainPath = child.Key + "/_main.cfg";
                    if (HasCaseAmbiguity(root, childMainPath))
                    {
                        diagnostic = "The child package entry has ambiguous casing.";
                        return false;
                    }
                    if (root.Entries.TryGetValue(childMainPath, out var childMain))
                        files.Add(new(childMainPath, childMain));
                }
                else if (child.Key.EndsWith(".cfg", StringComparison.Ordinal)) files.Add(child);
            }
            foreach (var file in files.OrderBy(pair => Rank(pair.Key[(pair.Key.LastIndexOf('/') + 1)..]))
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (file.Value.Source is null || file.Value.IsDirectory)
                {
                    diagnostic = file.Value.Diagnostic ?? "An included cfg source was not captured.";
                    return false;
                }
                output.Add(file.Value.Source);
            }
            return true;
        }

        private bool TryRoute(string path, WmlStaticSource caller, out Root? root, out string relative)
        {
            root = null; relative = "";
            var trimmed = path.TrimEnd('/');
            if (trimmed.StartsWith("./", StringComparison.Ordinal))
            {
                var owners = _roots.Where(candidate => Contains(candidate.Path, caller.Path)).ToArray();
                if (owners.Length != 1) return false;
                root = owners[0];
                var callerDirectory = Path.GetDirectoryName(caller.Path)!;
                relative = Relative(root.Path, Path.Combine(callerDirectory, trimmed[2..].Replace('/', Path.DirectorySeparatorChar)));
            }
            else
            {
                var candidates = _roots.SelectMany(candidate => candidate.Mounts
                    .Where(mount => trimmed.Equals(mount, StringComparison.Ordinal) ||
                        trimmed.StartsWith(mount + "/", StringComparison.Ordinal))
                    .Select(mount => (Root: candidate, Mount: mount))).ToArray();
                if (candidates.Length != 1) return false;
                root = candidates[0].Root;
                var mount = candidates[0].Mount;
                relative = trimmed.Length == mount.Length ? "" : trimmed[(mount.Length + 1)..];
            }
            return relative.Length == 0 || relative.Split('/').All(part => part.Length > 0 && part != "." && !part.Contains('~'));
        }

        internal static bool TryNormalizePath(string path, out string normalized, bool allowResourceParentheses = false)
        {
            normalized = "";
            if (string.IsNullOrWhiteSpace(path) || path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) || path.StartsWith('/') ||
                path.IndexOfAny(['\\', '{', '}', '$', '*', '?', '[', ']', ':', '"', '\'', '|', '<', '>', '#']) >= 0 ||
                !allowResourceParentheses && path.IndexOfAny(['(', ')']) >= 0 ||
                path.Contains("..", StringComparison.Ordinal)) return false;
            // Wesnoth accepts repeated separators. Reject traversal and dynamic
            // syntax before collapsing them, retaining the caller-relative prefix.
            normalized = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
            if (path.EndsWith('/')) normalized += "/";
            var remainder = normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
            if (remainder.StartsWith("~add-ons/", StringComparison.Ordinal)) remainder = remainder[9..];
            return remainder.TrimEnd('/').Split('/').All(part => part.Length > 0 && part != "." && !part.Contains('~'));
        }

        private bool IsAbsentAddon(string path)
        {
            if ((_addonNamespace is null && !UsesSessionAddonNamespace) ||
                !path.StartsWith("~add-ons/", StringComparison.Ordinal)) return false;
            var name = path[9..].Split('/')[0];
            // A pinned mount owns its evidence even if it was copied elsewhere,
            // is incomplete, or differs only by case from this request.
            if (_roots.SelectMany(root => root.Mounts).Any(mount =>
                    mount.Equals("~add-ons/" + name, StringComparison.OrdinalIgnoreCase))) return false;
            return UsesSessionAddonNamespace || !_addonNamespace!.Names.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        private static bool HasCaseAmbiguity(Root root, string relative)
        {
            var parts = relative.Split('/');
            for (var length = 1; length <= parts.Length; length++)
            {
                var ancestor = string.Join('/', parts.Take(length));
                if (root.CaseAliases.TryGetValue(ancestor, out var aliases) &&
                    (aliases.Length > 1 || aliases[0] != ancestor)) return true;
            }
            return false;
        }

        private WmlStaticIncludeResolution Result(string requested, WmlStaticTruth truth,
            IEnumerable<WmlStaticSource> sources, string? diagnostic)
        {
            var captured = Array.AsReadOnly(sources.ToArray());
            var identity = new StringBuilder(_identity);
            Append(identity, requested); Append(identity, truth.ToString());
            foreach (var source in captured) { Append(identity, source.Path); Append(identity, source.ContentHash); }
            return new(truth, captured, WccContentHash.ComputeText(identity.ToString()), diagnostic);
        }

        private static int Rank(string name) => name switch { "_initial.cfg" => -1, "_final.cfg" => 1, _ => 0 };
        private static void Append(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value);
    }
}
