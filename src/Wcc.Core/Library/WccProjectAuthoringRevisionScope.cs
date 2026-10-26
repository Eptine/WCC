namespace Wcc.Core.Library;

/// <summary>
/// Adds the WCC-owned reusable library that was indexed against a project's exact
/// Core revision to the project's ordinary dependency closure. This is an editor
/// authoring scope: unrelated add-ons and WCC content from another Wesnoth profile
/// remain excluded.
/// </summary>
public static class WccProjectAuthoringRevisionScope
{
    public static IReadOnlySet<WccPackageRevisionId> GetVisibleRevisionIds(
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId projectRevisionId)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        var visible = new HashSet<WccPackageRevisionId>(
            WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, projectRevisionId));
        var coreRevisionIds = visible
            .Where(revisionId => IsDistributionCoreRevision(snapshot, revisionId))
            .ToArray();
        if (coreRevisionIds.Length != 1)
            return visible;

        var coreRevisionId = coreRevisionIds[0];
        foreach (var package in snapshot.PackagesById.Values.Where(package =>
                     package.IsAvailable &&
                     package.SourceKind == WccPackageSourceKinds.WccManagedLibrary &&
                     package.ParentPackageId is null))
        {
            var compatible = FindNewestCompatibleRevision(snapshot, package, coreRevisionId);
            if (compatible is null)
                continue;

            visible.UnionWith(
                WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, compatible.Id));
        }

        // Authoring rows use the current working owner once. The installed owner
        // remains in the immutable dependency graph and in standalone Library scope;
        // capture follows its edges and proves the same physical pair separately.
        var installedFacades = visible.Where(revisionId => WccWorkingAddonOverlayRevisionAlias.IsVerified(
            snapshot, projectRevisionId, revisionId, coreRevisionId)).Take(2).ToArray();
        if (installedFacades.Length == 1) visible.Remove(installedFacades[0]);

        return visible;
    }

    private static bool IsDistributionCoreRevision(
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId revisionId)
    {
        if (!snapshot.RevisionsById.TryGetValue(revisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package))
        {
            return false;
        }

        return package.ParentPackageId is null &&
               (package.SourceKind == WccPackageSourceKinds.WesnothMainline ||
                package.SourceKind == WccPackageSourceKinds.WesnothCore) &&
               (package.ContentKind == WccPackageContentKinds.ResourcePack ||
                package.ContentKind == WccPackageContentKinds.Core);
    }

    private static WccPackageRevisionRecord? FindNewestCompatibleRevision(
        WccLibrarySnapshot snapshot,
        WccPackageRecord package,
        WccPackageRevisionId coreRevisionId)
        => snapshot.RevisionsById.Values
            .Where(revision => revision.PackageId == package.Id)
            .OrderByDescending(revision =>
                package.CurrentRevisionId is { } currentRevisionId &&
                revision.Id == currentRevisionId)
            .ThenByDescending(revision => revision.ImportedAtUtc)
            .ThenByDescending(revision => revision.Id.Value)
            .FirstOrDefault(revision =>
                WccLibraryRevisionVisibility
                    .GetVisibleRevisionIds(snapshot, revision.Id)
                    .Contains(coreRevisionId));
}
