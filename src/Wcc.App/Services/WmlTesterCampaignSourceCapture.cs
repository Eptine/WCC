using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.App.Services;

/// <summary>Run-local read-only evidence. Neither this result nor a complete capture authorizes a save.</summary>
public sealed class WmlTesterCampaignSourceCaptureResult
{
    internal WmlTesterCampaignSourceCaptureResult(WccStaticSourceCaptureResult? capture,
        IEnumerable<string> problems, string? campaignRootPath = null,
        IEnumerable<WmlCheckSource>? campaignSources = null,
        IEnumerable<StandaloneWmlFileSnapshot>? campaignSnapshots = null,
        bool isCampaignInventoryComplete = false)
    {
        Capture = capture;
        Problems = Array.AsReadOnly(problems.Distinct(StringComparer.Ordinal).ToArray());
        CampaignRootPath = campaignRootPath;
        CampaignSources = Array.AsReadOnly((campaignSources ?? []).ToArray());
        CampaignSnapshots = Array.AsReadOnly((campaignSnapshots ?? []).ToArray());
        IsCampaignInventoryComplete = isCampaignInventoryComplete;
    }

    public WccStaticSourceCaptureResult? Capture { get; }
    public IReadOnlyList<string> Problems { get; }
    /// <summary>The explicitly selected campaign root, never a dependency or Core root.</summary>
    public string? CampaignRootPath { get; }
    /// <summary>Owned .cfg text from the final immutable capture, regardless of include visitation.</summary>
    public IReadOnlyList<WmlCheckSource> CampaignSources { get; }
    /// <summary>Exact owned pin-read snapshots, retaining their supported original encoding.</summary>
    public IReadOnlyList<StandaloneWmlFileSnapshot> CampaignSnapshots { get; }
    /// <summary>Complete owned file inventory, independent of dependency/preprocessing context completeness.</summary>
    public bool IsCampaignInventoryComplete { get; }
    public bool IsCaptureComplete => Capture is { IsCaptureComplete: true } && Problems.Count == 0;
}

/// <summary>
/// Builds fresh source pins for the explicitly selected campaign, installation and dependencies.
/// The manifest lives in memory; shared Core capture owns include routing and ordering.
/// Installed sibling campaign assets are metadata only; their WML is never loaded.
/// No library refresh, sibling source import, executable launch or source write occurs.
/// </summary>
public sealed class WmlTesterCampaignSourceCapture(StandaloneWmlFileStore? store = null)
{
    private const int MaxEntries = 200_000;
    private const int MaxDepth = 96;
    private const long MaxCfgBytes = 8 * 1024 * 1024;
    private const long MaxTotalCfgBytes = 64 * 1024 * 1024;
    private readonly StandaloneWmlFileStore _store = store ?? new StandaloneWmlFileStore();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public Task<WmlTesterCampaignSourceCaptureResult> CaptureAsync(string entryPath,
        string? installPath, string? exactVersion, IReadOnlyList<WmlCheckSource> targets,
        CancellationToken cancellationToken = default, bool includeCoreUnits = false)
        => CaptureAsync(entryPath, installPath, exactVersion, targets, WccStaticSourceContext.Empty,
            cancellationToken, includeCoreUnits);

    public Task<WmlTesterCampaignSourceCaptureResult> CaptureAsync(string entryPath,
        string? installPath, string? exactVersion, IReadOnlyList<WmlCheckSource> targets,
        WccStaticSourceContext sourceContext, CancellationToken cancellationToken = default,
        bool includeCoreUnits = false)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(sourceContext);
        // Freeze caller-owned inputs before the worker can observe a later UI selection.
        var capturedTargets = targets.ToArray();
        return Task.Run(() => CaptureOnWorkerAsync(entryPath, installPath, exactVersion,
            capturedTargets, sourceContext, cancellationToken, includeCoreUnits), cancellationToken);
    }

    private async Task<WmlTesterCampaignSourceCaptureResult> CaptureOnWorkerAsync(string entryPath,
        string? installPath, string? exactVersion, IReadOnlyList<WmlCheckSource> targets,
        WccStaticSourceContext sourceContext, CancellationToken cancellationToken, bool includeCoreUnits)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var entry = StandaloneWmlFileStore.NormalizePath(entryPath);
            var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(entry)!);
            var targetMap = new Dictionary<string, WmlCheckSource>(PathComparer);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = StandaloneWmlFileStore.NormalizePath(target.Path);
                ArgumentNullException.ThrowIfNull(target.Text);
                if (targetMap.TryGetValue(path, out var previous) && previous.Text != target.Text)
                    throw new InvalidDataException($"Checked source '{path}' has conflicting captured buffers.");
                targetMap[path] = target with { Path = path };
            }

            var manifest = new WccLibraryManifest();
            var roots = new List<CaptureRoot>();
            CaptureRoot? core = null;
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                var coreRoot = Path.TrimEndingDirectorySeparator(WccCoreInstallPaths.GetCoreDataRoot(installPath));
                core = AddRoot(manifest, roots, coreRoot, "Wesnoth Core", WccPackageSourceKinds.WesnothMainline,
                    WccPackageContentKinds.ResourcePack, WccPackageStorageMode.ExternalReadOnly, exactVersion);
                var dataRoot = Path.GetDirectoryName(coreRoot);
                if (dataRoot is not null)
                {
                    foreach (var identifier in new[] { "themes", "internal", "ai", "lua" })
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = Path.Combine(dataRoot, identifier);
                        if (!Directory.Exists(path)) continue;
                        var resource = AddRoot(manifest, roots, path, "Wesnoth " + identifier,
                            WccPackageSourceKinds.WesnothMainline, WccPackageContentKinds.ResourcePack,
                            WccPackageStorageMode.ExternalReadOnly, exactVersion, core.Package.Id, identifier);
                        core.Revision.DependencyRevisionIds.Add(resource.Revision.Id);
                    }
                    // Shared discovery also serves ordinary captured preview consumers.
                    // These roots remain metadata-only and never import sibling WML.
                    foreach (var discovered in WccInstalledResourceRootDiscovery.Discover(coreRoot, projectRoot, cancellationToken))
                    {
                        var resource = AddRoot(manifest, roots, discovered.Path, discovered.Name,
                            WccPackageSourceKinds.WesnothMainline, WccPackageContentKinds.ResourcePack,
                            WccPackageStorageMode.ExternalReadOnly, exactVersion, core.Package.Id, discovered.Mount);
                        core.Revision.DependencyRevisionIds.Add(resource.Revision.Id);
                    }
                }
            }

            // The working-copy record supplies both copied-mainline and add-on spellings;
            // it is transient routing metadata and grants no write authority.
            var project = AddRoot(manifest, roots, projectRoot, Path.GetFileName(projectRoot),
                WccPackageSourceKinds.WccProject, WccPackageContentKinds.Campaign,
                WccPackageStorageMode.WorkingCopy, null, identifier: Path.GetFileName(projectRoot));
            if (core is not null) project.Revision.DependencyRevisionIds.Add(core.Revision.Id);
            var dependencies = new HashSet<CaptureRoot>();
            foreach (var selection in sourceContext.DependencyRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(selection.AddonId) || selection.AddonId is "." or ".." ||
                    selection.AddonId.Any(character => char.IsWhiteSpace(character) ||
                        "/\\~{}$*?[]():\"'|<>#".Contains(character)))
                    throw new InvalidDataException("A dependency requires one literal add-on identifier.");
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selection.RootPath));
                if (dependencies.Any(root => PathComparer.Equals(root.Path, path) && root.Package.SourceIdentifier == selection.AddonId)) continue;
                var dependency = AddRoot(manifest, roots, path, selection.AddonId,
                    WccPackageSourceKinds.WesnothAddon, WccPackageContentKinds.ResourcePack,
                    WccPackageStorageMode.ExternalReadOnly, exactVersion, core?.Package.Id, selection.AddonId);
                dependencies.Add(dependency);
                project.Revision.DependencyRevisionIds.Add(dependency.Revision.Id);
            }
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (roots.Any(other => !ReferenceEquals(other, root)
                    && (Contains(root.Path, other.Path) || Contains(other.Path, root.Path))))
                    throw new InvalidDataException("The campaign, installation and dependency selections have overlapping source roots.");
            }
            var budget = new CaptureBudget();
            var dependencyProblems = new List<string>();
            var campaignPins = new Dictionary<string, StandaloneWmlFileSnapshot>(PathComparer);
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await CapturePinsAsync(manifest, root, budget, cancellationToken,
                        ReferenceEquals(root, project) ? campaignPins : null).ConfigureAwait(false);
                }
                catch (Exception failure) when (dependencies.Contains(root) && IsSourceFailure(failure))
                {
                    // Retain an unavailable pinned dependency in the closure. The
                    // shared capture then preserves uncertainty instead of treating
                    // a selected but unreadable root as absent or losing base checks.
                    manifest.Packages.Remove(root.Package);
                    manifest.Packages.Add(new WccPackageRecord
                    {
                        Id = root.Package.Id, Name = root.Package.Name, SourceKind = root.Package.SourceKind,
                        ContentKind = root.Package.ContentKind, StorageMode = root.Package.StorageMode,
                        SourceLocation = root.Package.SourceLocation, SourceIdentifier = root.Package.SourceIdentifier,
                        ParentPackageId = root.Package.ParentPackageId, CurrentRevisionId = root.Package.CurrentRevisionId,
                        IsAvailable = false
                    });
                    dependencyProblems.Add($"Selected dependency '{root.Package.SourceIdentifier}' could not be captured at '{root.Path}': {failure.Message}");
                }
            }

            var snapshot = WccLibrarySnapshotBuilder.Build(manifest, []);
            var capture = await WccStaticSourceCapture.CaptureAsync(snapshot, project.Revision.Id,
                entry, cancellationToken: cancellationToken,
                configuredAddonsPath: sourceContext.AddonsNamespacePath,
                includeCoreUnits: includeCoreUnits).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var target in targetMap.Values)
            {
                // A mixed tester selection may also contain unrelated loose files.
                // Never import them into this context; the reference adapter reports
                // that they were not visited by the explicit campaign evaluation.
                if (!roots.Any(root => !root.IsResourceOnly && Contains(root.Path, target.Path))) continue;
                var candidates = capture.Sources.Where(source => PathComparer.Equals(source.Path, target.Path)).ToArray();
                if (candidates.Length != 1 || candidates[0].Text != target.Text)
                    throw new InvalidDataException($"Checked source '{target.Path}' changed or has no unique captured revision. Rerun the source checks before evaluating its campaign context.");
            }
            var ownedGroups = capture.Sources.Where(source => source.IsAuthored && Contains(projectRoot, source.Path) &&
                    string.Equals(Path.GetExtension(source.Path), ".cfg", StringComparison.OrdinalIgnoreCase))
                .GroupBy(source => source.Path, PathComparer).ToDictionary(group => group.Key, group => group.ToArray(), PathComparer);
            var campaignSources = new List<WmlCheckSource>();
            var campaignSnapshots = new List<StandaloneWmlFileSnapshot>();
            var inventoryProblems = new List<string>();
            foreach (var group in ownedGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group.Value.Length != 1)
                {
                    inventoryProblems.Add($"Campaign source '{group.Key}' has no unique final captured revision.");
                    continue;
                }
                var source = group.Value[0];
                campaignSources.Add(new(source.Path, source.Text));
                if (!campaignPins.TryGetValue(source.Path, out var pin) || pin.Text != source.Text)
                    inventoryProblems.Add($"Campaign source '{source.Path}' has no unique matching pin snapshot; its original encoding is unproven.");
                else campaignSnapshots.Add(pin with { Path = source.Path, Text = source.Text });
            }
            foreach (var pin in campaignPins.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ownedGroups.ContainsKey(pin.Path))
                    inventoryProblems.Add($"Pinned campaign source '{pin.Path}' is absent from the final captured campaign inventory.");
            }
            var entryMatches = capture.EntrySource is { IsAuthored: true } capturedEntry &&
                PathComparer.Equals(capturedEntry.Path, entry) && ownedGroups.TryGetValue(entry, out var entrySources) &&
                entrySources.Length == 1 && entrySources[0].Text == capturedEntry.Text;
            if (!entryMatches)
                inventoryProblems.Add("The campaign entry has no unique exact captured source in its campaign inventory.");
            var inventoryComplete = entryMatches && inventoryProblems.Count == 0 && campaignSources.Count == campaignPins.Count &&
                campaignSnapshots.Count == campaignPins.Count;
            return new(capture, capture.Diagnostics.Concat(dependencyProblems).Concat(inventoryProblems), projectRoot,
                campaignSources, campaignSnapshots, inventoryComplete);
        }
        catch (Exception failure) when (IsSourceFailure(failure))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(null, ["Campaign source context could not be captured: " + failure.Message]);
        }
    }

    private async Task CapturePinsAsync(WccLibraryManifest manifest, CaptureRoot root,
        CaptureBudget budget, CancellationToken cancellationToken,
        IDictionary<string, StandaloneWmlFileSnapshot>? campaignPins = null)
    {
        var before = Enumerate(root.Path, cancellationToken);
        budget.Entries += before.Count;
        if (budget.Entries > MaxEntries) throw new IOException("The bounded source inventory limit was reached.");
        foreach (var file in before.Where(entry => !root.IsResourceOnly && !entry.IsDirectory
                     && string.Equals(Path.GetExtension(entry.RelativePath), ".cfg", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root.Path, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (file.Length > MaxCfgBytes || budget.ReadBytes + file.Length > MaxTotalCfgBytes)
                throw new IOException($"The bounded source read limit was reached at '{path}'.");
            budget.ReadBytes += file.Length;
            StandaloneWmlFileSnapshot source;
            try { source = await _store.OpenAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (IsSourceFailure(failure))
            {
                throw new IOException($"Could not capture source '{path}': {failure.Message}", failure);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (source is null || source.Text is null || !PathComparer.Equals(source.Path, path))
                throw new InvalidDataException($"The source reader returned a different source for '{path}'.");
            if (!Enum.IsDefined(source.Encoding))
                throw new InvalidDataException($"The source reader returned an unsupported encoding for '{path}'.");
            var info = new FileInfo(path);
            if (info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.ModifiedTicks
                || (info.Attributes & FileAttributes.ReparsePoint) != 0
                || Encoding.UTF8.GetByteCount(source.Text) > MaxCfgBytes)
                throw new IOException($"Source '{path}' changed or exceeded its bounded read limit during capture.");
            manifest.SourceFiles.Add(new WccSourceFileRecord
            {
                Id = WccSourceFileId.New(), PackageRevisionId = root.Revision.Id,
                RelativePath = file.RelativePath, StoragePath = path, FileType = WccSourceFileTypes.Wml,
                ContentHash = WccContentHash.ComputeText(source.Text), Length = file.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc
            });
            if (campaignPins is not null && !campaignPins.TryAdd(path, source with { Path = path }))
                throw new InvalidDataException($"Campaign source '{path}' has duplicate pin snapshots.");
        }
        if (!before.SequenceEqual(Enumerate(root.Path, cancellationToken)))
            throw new IOException($"Source root '{root.Path}' changed during capture.");
    }

    private static IReadOnlyList<InventoryEntry> Enumerate(string root, CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(root);
        if (!directory.Exists) throw new DirectoryNotFoundException(root);
        for (var ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked source root '{ancestor.FullName}' cannot be captured.");
        var entries = new List<InventoryEntry>();
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((directory, 0));
        while (pending.TryPop(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (next.Depth > MaxDepth) throw new IOException("The bounded source directory depth limit was reached.");
            foreach (var item in next.Directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= MaxEntries) throw new IOException("The bounded source inventory limit was reached.");
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Linked source entry '{item.FullName}' cannot be captured.");
                var isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                entries.Add(new(Path.GetRelativePath(root, item.FullName).Replace('\\', '/'), isDirectory,
                    isDirectory ? 0 : ((FileInfo)item).Length, isDirectory ? 0 : item.LastWriteTimeUtc.Ticks));
                if (isDirectory) pending.Push(((DirectoryInfo)item, next.Depth + 1));
            }
        }
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static CaptureRoot AddRoot(WccLibraryManifest manifest, ICollection<CaptureRoot> roots,
        string path, string name, WccPackageSourceKindId kind, WccPackageContentKindId content,
        WccPackageStorageMode storage, string? version, WccPackageId? parent = null, string? identifier = null)
    {
        var packageId = WccPackageId.New();
        var revision = new WccPackageRevisionRecord
            { Id = WccPackageRevisionId.New(), PackageId = packageId, Version = version };
        var package = new WccPackageRecord
        {
            Id = packageId, Name = name, SourceKind = kind, ContentKind = content, StorageMode = storage,
            SourceLocation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            SourceIdentifier = identifier, ParentPackageId = parent, CurrentRevisionId = revision.Id
        };
        manifest.Packages.Add(package); manifest.PackageRevisions.Add(revision);
        var root = new CaptureRoot(package.SourceLocation!, package, revision);
        roots.Add(root);
        return root;
    }

    private static bool Contains(string root, string path) => PathComparer.Equals(root, path)
        || path.StartsWith(root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool IsSourceFailure(Exception failure) => failure is IOException or InvalidDataException or UnauthorizedAccessException
        or DecoderFallbackException or ArgumentException or NotSupportedException;
    private sealed record CaptureRoot(string Path, WccPackageRecord Package, WccPackageRevisionRecord Revision)
    {
        public bool IsResourceOnly => Package.SourceKind == WccPackageSourceKinds.WesnothMainline &&
            Package.ContentKind == WccPackageContentKinds.ResourcePack && Package.ParentPackageId is not null &&
            WmlResourceLookupPaths.IsInstallationResourceMount(Package.SourceIdentifier ?? "");
    }
    private sealed record InventoryEntry(string RelativePath, bool IsDirectory, long Length, long ModifiedTicks);
    private sealed class CaptureBudget { public int Entries; public long ReadBytes; }
}
