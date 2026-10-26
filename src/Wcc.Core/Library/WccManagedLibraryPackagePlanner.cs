namespace Wcc.Core.Library;

public sealed class WccManagedLibraryPackagePlan
{
    public required WccLibraryManifest Manifest { get; init; }
    public WccPackageId? PackageId { get; init; }
    public WccPackageRevisionId? RevisionId { get; init; }
    public WccPackageRefreshRequest? RefreshRequest { get; init; }
    public required IReadOnlyList<WccSourceFileId> RemovedSourceFileIds { get; init; }
}

/// <summary>
/// Exposes WCC-owned reusable cfg files as one managed package. The established typed
/// snapshot folders remain the physical storage for compatibility with existing libraries;
/// the package snapshot is the catalog and identity authority for every file beneath them.
/// </summary>
public static class WccManagedLibraryPackagePlanner
{
    public static WccManagedLibraryPackagePlan Build(
        WccLibraryManifest manifest,
        string managedLibraryRoot,
        WccPackageRevisionId? coreRevisionId = null)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (string.IsNullOrWhiteSpace(managedLibraryRoot))
            throw new ArgumentException("Managed library root must be set.", nameof(managedLibraryRoot));

        var editor = new WccPackagePlanEditor(manifest);
        var activePackageIds = new HashSet<WccPackageId>();
        WccPlannedPackage? planned = null;
        var fullRoot = Path.GetFullPath(managedLibraryRoot);

        if (Directory.Exists(fullRoot) &&
            Directory.EnumerateFiles(fullRoot, "*.cfg", SearchOption.AllDirectories).Any())
        {
            planned = editor.Upsert(
                "WCC Library",
                WccPackageSourceKinds.WccManagedLibrary,
                WccPackageContentKinds.Mixed,
                WccPackageStorageMode.ManagedSnapshot,
                fullRoot,
                parentPackageId: null,
                dependencyRevisionIds: coreRevisionId is { } dependency
                    ? new[] { dependency }
                    : Array.Empty<WccPackageRevisionId>());
            activePackageIds.Add(planned.Package.Id);
        }

        editor.RemoveInactivePackages(
            WccPackageSourceKinds.WccManagedLibrary,
            WccPackageStorageMode.ManagedSnapshot,
            activePackageIds);

        return new WccManagedLibraryPackagePlan
        {
            Manifest = editor.BuildManifest(),
            PackageId = planned?.Package.Id,
            RevisionId = planned?.Revision.Id,
            RefreshRequest = planned is null
                ? null
                : new WccPackageRefreshRequest(
                    planned.Revision.Id,
                    fullRoot,
                    planned.Package.Name),
            RemovedSourceFileIds = editor.RemovedSourceFileIds
        };
    }
}
