namespace Wcc.Core.Library;

/// <summary>
/// Selects durable sources for the standalone Library and its authoring tools.
/// The complete snapshot still retains working projects and attached campaign
/// sources for their exact project bindings; those are not standalone sources.
/// </summary>
public static class WccStandaloneLibraryRevisionScope
{
    public static IReadOnlySet<WccPackageRevisionId> GetVisibleRevisionIds(
        WccLibrarySnapshot snapshot,
        WccPackageId distributionPackageId,
        string? configuredAddonsPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.PackagesById.TryGetValue(distributionPackageId, out var distribution)
            || FindSelectedRevision(snapshot, distribution) is not { } selected)
            return new HashSet<WccPackageRevisionId>();

        return GetVisibleRevisionIds(snapshot, distributionPackageId, selected.Id, configuredAddonsPath);
    }

    /// <summary>
    /// Uses an exact retained Core revision without substituting the current Core.
    /// A selected source whose closure is not durable is omitted in its entirety,
    /// rather than exposing it with hidden temporary dependencies to its previews.
    /// </summary>
    public static IReadOnlySet<WccPackageRevisionId> GetVisibleRevisionIds(
        WccLibrarySnapshot snapshot,
        WccPackageId distributionPackageId,
        WccPackageRevisionId pinnedDistributionRevisionId,
        string? configuredAddonsPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.PackagesById.TryGetValue(distributionPackageId, out var distribution)
            || !distribution.IsAvailable || !IsDistributionPackage(distribution)
            || !snapshot.RevisionsById.TryGetValue(pinnedDistributionRevisionId, out var pinned)
            || pinned.PackageId != distributionPackageId)
            return new HashSet<WccPackageRevisionId>();

        var addonsRoot = NormalizeLocation(configuredAddonsPath);
        // The planner accepts either an add-ons directory or one direct add-on.
        // Match that same discovery boundary without scanning any source tree.
        var directAddonRoot = addonsRoot is not null && File.Exists(Path.Combine(addonsRoot, "_main.cfg"));
        bool IsStandalonePackage(WccPackageRecord package)
        {
            if (!package.IsAvailable) return false;
            if (package.Id == distributionPackageId) return true;
            if (package.ParentPackageId == distributionPackageId)
            {
                if (package.SourceKind == WccPackageSourceKinds.WesnothMainline
                    || package.SourceKind == WccPackageSourceKinds.WesnothCore)
                    return true;
                return package.SourceKind == WccPackageSourceKinds.WesnothAddon
                    && package.StorageMode == WccPackageStorageMode.ExternalReadOnly
                    && IsAutomaticAddonRoot(package.SourceLocation, addonsRoot, directAddonRoot);
            }
            return package.ParentPackageId is null
                && (package.SourceKind == WccPackageSourceKinds.WccManagedLibrary
                    || package.SourceKind == WccPackageSourceKinds.WccBundle);
        }

        HashSet<WccPackageRevisionId>? CaptureDurableClosure(WccPackageRevisionId root)
        {
            var closureRevisionIds = new HashSet<WccPackageRevisionId>();
            var owners = new Dictionary<WccPackageId, WccPackageRevisionId>();
            var pending = new Queue<WccPackageRevisionId>();
            pending.Enqueue(root);
            while (pending.TryDequeue(out var revisionId))
            {
                if (!closureRevisionIds.Add(revisionId)) continue;
                if (!snapshot.RevisionsById.TryGetValue(revisionId, out var revision)
                    || !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package)
                    || !IsStandalonePackage(package)
                    || owners.TryGetValue(package.Id, out var other) && other != revisionId
                    || package.Id == distributionPackageId && revisionId != pinned.Id
                    || package.Id != distributionPackageId && IsDistributionPackage(package))
                    return null;
                owners[package.Id] = revisionId;
                foreach (var dependency in revision.DependencyRevisionIds) pending.Enqueue(dependency);
            }
            return closureRevisionIds.Contains(pinned.Id) ? closureRevisionIds : null;
        }

        if (CaptureDurableClosure(pinned.Id) is not { } visible)
            return new HashSet<WccPackageRevisionId>();
        foreach (var package in snapshot.PackagesById.Values.Where(package =>
                     package.Id != distributionPackageId && IsStandalonePackage(package)))
        {
            // Compatibility may choose a retained revision from this exact Core.
            // Once selected, a forbidden dependency must not cause a fallback to
            // older content that merely happens to avoid that dependency.
            var selected = FindCompatibleRevision(snapshot, package, pinned.Id);
            if (selected is not null && CaptureDurableClosure(selected.Id) is { } closure)
                visible.UnionWith(closure);
        }
        return visible;
    }

    private static WccPackageRevisionRecord? FindSelectedRevision(
        WccLibrarySnapshot snapshot, WccPackageRecord package)
    {
        if (package.CurrentRevisionId is { } id && snapshot.RevisionsById.TryGetValue(id, out var current)
            && current.PackageId == package.Id)
            return current;
        return snapshot.RevisionsById.Values.Where(revision => revision.PackageId == package.Id)
            .OrderByDescending(revision => revision.ImportedAtUtc)
            .ThenByDescending(revision => revision.Id.Value).FirstOrDefault();
    }

    private static WccPackageRevisionRecord? FindCompatibleRevision(
        WccLibrarySnapshot snapshot, WccPackageRecord package, WccPackageRevisionId coreRevisionId)
        => snapshot.RevisionsById.Values.Where(revision => revision.PackageId == package.Id)
            .OrderByDescending(revision => package.CurrentRevisionId == revision.Id)
            .ThenByDescending(revision => revision.ImportedAtUtc)
            .ThenByDescending(revision => revision.Id.Value)
            .FirstOrDefault(revision => WccLibraryRevisionVisibility
                .GetVisibleRevisionIds(snapshot, revision.Id).Contains(coreRevisionId));

    private static bool IsAutomaticAddonRoot(string? sourceLocation, string? addonsRoot, bool directAddonRoot)
    {
        var sourceRoot = NormalizeLocation(sourceLocation);
        if (sourceRoot is null || addonsRoot is null) return false;
        return WccPackagePlanEditor.LocationsEqual(sourceRoot, addonsRoot)
            ? directAddonRoot
            : !directAddonRoot && Path.GetDirectoryName(sourceRoot) is { } parent
                && WccPackagePlanEditor.LocationsEqual(parent, addonsRoot);
    }

    private static string? NormalizeLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return WccPackagePlanEditor.NormalizeLocation(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return null; }
    }

    private static bool IsDistributionPackage(WccPackageRecord package)
        => package.SourceKind == WccPackageSourceKinds.WesnothCore
            && (package.ContentKind == WccPackageContentKinds.Core
                || package.ContentKind == WccPackageContentKinds.ResourcePack)
            || package.SourceKind == WccPackageSourceKinds.WesnothMainline
                && package.ParentPackageId is null
                && package.ContentKind == WccPackageContentKinds.ResourcePack;
}
