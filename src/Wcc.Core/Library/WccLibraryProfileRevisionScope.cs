namespace Wcc.Core.Library;

/// <summary>
/// Builds the ordinary Library view for one Wesnoth installation profile. The durable
/// snapshot may retain several installations and historical revisions for exact project
/// pins, but browsing an installation must not expose definitions from another profile.
/// </summary>
public static class WccLibraryProfileRevisionScope
{
    /// <summary>
    /// Returns only immutable files owned by Wesnoth itself for engine-backed
    /// verification. The ordinary profile scope also includes compatible WCC
    /// projects and managed content, which may intentionally shadow the same
    /// disk path and must not be mistaken for runtime package ownership.
    /// </summary>
    public static IReadOnlySet<WccPackageRevisionId> GetVisibleRuntimeRevisionIds(
        WccLibrarySnapshot snapshot,
        WccPackageId distributionPackageId)
    {
        var visible = GetVisibleRevisionIds(snapshot, distributionPackageId);
        return visible.Where(revisionId =>
                snapshot.RevisionsById.TryGetValue(revisionId, out var revision) &&
                snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) &&
                IsWesnothRuntimePackage(package))
            .ToHashSet();
    }

    public static IReadOnlySet<WccPackageRevisionId> GetVisibleRevisionIds(
        WccLibrarySnapshot snapshot,
        WccPackageId distributionPackageId)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (!snapshot.PackagesById.TryGetValue(distributionPackageId, out var distribution))
            return new HashSet<WccPackageRevisionId>();

        var distributionRevision = FindSelectedRevision(snapshot, distribution);
        if (distributionRevision is null)
            return new HashSet<WccPackageRevisionId>();

        var roots = new HashSet<WccPackageRevisionId> { distributionRevision.Id };

        // Core campaigns and downloaded add-ons are owned directly by their containing
        // distribution package, which is the stable profile boundary in the manifest.
        foreach (var package in snapshot.PackagesById.Values.Where(package =>
                     package.IsAvailable &&
                     package.ParentPackageId == distributionPackageId))
        {
            if (FindSelectedRevision(snapshot, package) is { } revision)
                roots.Add(revision.Id);
        }

        // WCC-owned reusable content and the live project are not children of an install.
        // Select only the revision whose immutable dependency closure reaches this Core.
        foreach (var package in snapshot.PackagesById.Values.Where(package =>
                     package.IsAvailable &&
                     package.ParentPackageId is null &&
                     package.Id != distributionPackageId &&
                     (package.SourceKind == WccPackageSourceKinds.WccManagedLibrary ||
                      package.SourceKind == WccPackageSourceKinds.WccBundle ||
                      package.SourceKind == WccPackageSourceKinds.WccProject)))
        {
            var revision = FindNewestRevisionDependingOn(
                snapshot,
                package,
                distributionRevision.Id);
            if (revision is not null)
                roots.Add(revision.Id);
        }

        var visible = new HashSet<WccPackageRevisionId>();
        foreach (var root in roots)
        {
            visible.UnionWith(
                WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root));
        }

        return visible;
    }

    /// <summary>
    /// Builds a profile view around an exact retained Core revision. Compatible
    /// package heads are preferred; otherwise the newest retained compatible
    /// revision is selected. Missing or mismatched pins have no head fallback.
    /// </summary>
    public static IReadOnlySet<WccPackageRevisionId> GetVisibleRevisionIds(
        WccLibrarySnapshot snapshot,
        WccPackageId distributionPackageId,
        WccPackageRevisionId pinnedDistributionRevisionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.PackagesById.TryGetValue(distributionPackageId, out var distribution)
            || !distribution.IsAvailable || !IsPinnedDistributionPackage(distribution)
            || !snapshot.RevisionsById.TryGetValue(pinnedDistributionRevisionId, out var pinned)
            || pinned.PackageId != distributionPackageId
            || !HasCompatiblePinnedClosure(snapshot, pinned.Id, distributionPackageId, pinned.Id))
            return new HashSet<WccPackageRevisionId>();

        var roots = new HashSet<WccPackageRevisionId> { pinned.Id };
        foreach (var package in snapshot.PackagesById.Values.Where(package => package.IsAvailable
                     && package.Id != distributionPackageId
                     && (package.ParentPackageId == distributionPackageId
                         || package.ParentPackageId is null &&
                         (package.SourceKind == WccPackageSourceKinds.WccManagedLibrary
                          || package.SourceKind == WccPackageSourceKinds.WccBundle
                          || package.SourceKind == WccPackageSourceKinds.WccProject))))
        {
            var revision = snapshot.RevisionsById.Values.Where(revision => revision.PackageId == package.Id)
                .OrderByDescending(revision => package.CurrentRevisionId == revision.Id)
                .ThenByDescending(revision => revision.ImportedAtUtc)
                .ThenByDescending(revision => revision.Id.Value)
                .FirstOrDefault(revision => HasCompatiblePinnedClosure(snapshot, revision.Id, distributionPackageId, pinned.Id));
            if (revision is not null) roots.Add(revision.Id);
        }

        var visible = new HashSet<WccPackageRevisionId>();
        foreach (var root in roots)
            visible.UnionWith(WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root));
        return visible;
    }

    private static bool HasCompatiblePinnedClosure(WccLibrarySnapshot snapshot,
        WccPackageRevisionId root, WccPackageId distributionPackageId, WccPackageRevisionId pinned)
    {
        var closure = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root);
        if (!closure.Contains(pinned)) return false;
        foreach (var revisionId in closure)
        {
            var revision = snapshot.RevisionsById[revisionId];
            if (!snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable
                || revision.DependencyRevisionIds.Any(dependency => !snapshot.RevisionsById.ContainsKey(dependency))) return false;
            if (package.Id == distributionPackageId)
            {
                if (revisionId != pinned) return false;
            }
            else if (IsPinnedDistributionPackage(package)) return false;
        }
        return true;
    }

    private static bool IsPinnedDistributionPackage(WccPackageRecord package) =>
        package.SourceKind == WccPackageSourceKinds.WesnothCore
            && (package.ContentKind == WccPackageContentKinds.Core
                || package.ContentKind == WccPackageContentKinds.ResourcePack)
        || package.SourceKind == WccPackageSourceKinds.WesnothMainline
            && package.ParentPackageId is null
            && package.ContentKind == WccPackageContentKinds.ResourcePack;

    private static bool IsWesnothRuntimePackage(WccPackageRecord package) =>
        package.IsAvailable &&
        (package.SourceKind == WccPackageSourceKinds.WesnothCore ||
         package.SourceKind == WccPackageSourceKinds.WesnothMainline ||
         package.SourceKind == WccPackageSourceKinds.WesnothAddon);

    private static WccPackageRevisionRecord? FindSelectedRevision(
        WccLibrarySnapshot snapshot,
        WccPackageRecord package)
    {
        if (package.CurrentRevisionId is { } currentRevisionId &&
            snapshot.RevisionsById.TryGetValue(currentRevisionId, out var current) &&
            current.PackageId == package.Id)
        {
            return current;
        }

        // Compatibility fallback for manifests written before CurrentRevisionId existed.
        return snapshot.RevisionsById.Values
            .Where(revision => revision.PackageId == package.Id)
            .OrderByDescending(revision => revision.ImportedAtUtc)
            .ThenByDescending(revision => revision.Id.Value)
            .FirstOrDefault();
    }

    private static WccPackageRevisionRecord? FindNewestRevisionDependingOn(
        WccLibrarySnapshot snapshot,
        WccPackageRecord package,
        WccPackageRevisionId dependencyRevisionId)
    {
        var candidates = snapshot.RevisionsById.Values
            .Where(revision => revision.PackageId == package.Id)
            .OrderByDescending(revision =>
                package.CurrentRevisionId is { } currentRevisionId &&
                revision.Id == currentRevisionId)
            .ThenByDescending(revision => revision.ImportedAtUtc)
            .ThenByDescending(revision => revision.Id.Value);

        return candidates.FirstOrDefault(revision =>
            WccLibraryRevisionVisibility
                .GetVisibleRevisionIds(snapshot, revision.Id)
                .Contains(dependencyRevisionId));
    }
}
