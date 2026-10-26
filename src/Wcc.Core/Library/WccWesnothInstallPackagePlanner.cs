using Wcc.Core.Compatibility;
using Wcc.Core.Projects;

namespace Wcc.Core.Library;

public sealed class WccWesnothInstallPackagePlan
{
    public required WccLibraryManifest Manifest { get; init; }
    public required WccPackageId DistributionPackageId { get; init; }
    public required WccPackageRevisionId DistributionRevisionId { get; init; }
    public required IReadOnlyList<WccPackageRefreshRequest> RefreshRequests { get; init; }
    public required IReadOnlyList<WccSourceFileId> RemovedSourceFileIds { get; init; }
}

/// <summary>
/// Reconciles a configured Wesnoth installation with durable package records. The shared
/// core data is one containing package; shared resource trees are pinned dependencies,
/// and every mainline campaign directory keeps a separate WML symbol scope.
/// </summary>
public static class WccWesnothInstallPackagePlanner
{
    public static WccWesnothInstallPackagePlan Build(
        WccLibraryManifest manifest,
        string wesnothInstallPath)
        => BuildCore(
            manifest,
            wesnothInstallPath,
            WccCoreInstallPaths.GetCoreDataRoot(wesnothInstallPath),
            WccCoreInstallPaths.GetCampaignsRoot(wesnothInstallPath),
            versionMetadata: null);

    public static WccWesnothInstallPackagePlan Build(
        WccLibraryManifest manifest,
        WesnothInstallationIdentity installation)
    {
        if (installation is null) throw new ArgumentNullException(nameof(installation));

        return BuildCore(
            manifest,
            installation.InstallRoot,
            installation.CoreDataRoot,
            installation.CampaignsRoot,
            new WccPackageRevisionVersionMetadata(
                installation.ExactVersion,
                installation.CompatibilityTarget));
    }

    private static WccWesnothInstallPackagePlan BuildCore(
        WccLibraryManifest manifest,
        string wesnothInstallPath,
        string coreRoot,
        string campaignsRoot,
        WccPackageRevisionVersionMetadata? versionMetadata)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (string.IsNullOrWhiteSpace(wesnothInstallPath))
            throw new ArgumentException("Wesnoth install path must be set.", nameof(wesnothInstallPath));

        if (!Directory.Exists(coreRoot))
            throw new DirectoryNotFoundException(coreRoot);

        var editor = new WccPackagePlanEditor(manifest);
        var activePackageIds = new HashSet<WccPackageId>();
        // Establish the containing identity before its dependency revisions. Shared
        // resources do not depend back on Core: the distribution pins their inventory,
        // while actual macro visibility still follows authored includes in source order.
        var distributionPackage = editor.UpsertPackage(
            "Wesnoth Core",
            WccPackageSourceKinds.WesnothMainline,
            WccPackageContentKinds.ResourcePack,
            WccPackageStorageMode.ExternalReadOnly,
            coreRoot,
            parentPackageId: null);
        var requests = new List<WccPackageRefreshRequest>();
        var distributionDependencies = new List<WccPackageRevisionId>();
        var dataRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(coreRoot));
        foreach (var (identifier, name) in new[]
                 {
                     ("themes", "Wesnoth Themes"),
                     ("internal", "Wesnoth Internal Resources"),
                     ("ai", "Wesnoth AI Resources")
                 })
        {
            var resourceRoot = dataRoot is null ? null : Path.Combine(dataRoot, identifier);
            if (resourceRoot is null || !Directory.Exists(resourceRoot)) continue;
            var resources = editor.Upsert(
                name,
                WccPackageSourceKinds.WesnothMainline,
                WccPackageContentKinds.ResourcePack,
                WccPackageStorageMode.ExternalReadOnly,
                resourceRoot,
                distributionPackage.Id,
                dependencyRevisionIds: Array.Empty<WccPackageRevisionId>(),
                versionMetadata: versionMetadata,
                sourceIdentifier: identifier);
            activePackageIds.Add(resources.Package.Id);
            distributionDependencies.Add(resources.Revision.Id);
            requests.Add(new(resources.Revision.Id, resourceRoot, resources.Package.Name));
        }

        var distribution = editor.Upsert(
            distributionPackage.Name,
            distributionPackage.SourceKind,
            distributionPackage.ContentKind,
            distributionPackage.StorageMode,
            coreRoot,
            parentPackageId: null,
            dependencyRevisionIds: distributionDependencies,
            versionMetadata: versionMetadata);
        activePackageIds.Add(distribution.Package.Id);
        requests.Insert(0, new(distribution.Revision.Id, coreRoot, distribution.Package.Name));

        if (Directory.Exists(campaignsRoot))
        {
            foreach (var campaignRoot in Directory
                         .EnumerateDirectories(campaignsRoot)
                         .OrderBy(x => x, WccPackagePlanEditor.PathComparer))
            {
                var packageName = Path.GetFileName(campaignRoot);
                var campaign = editor.Upsert(
                    packageName,
                    WccPackageSourceKinds.WesnothMainline,
                    WccPackageContentKinds.Campaign,
                    WccPackageStorageMode.ExternalReadOnly,
                    campaignRoot,
                    distribution.Package.Id,
                    new[] { distribution.Revision.Id },
                    versionMetadata: versionMetadata);
                activePackageIds.Add(campaign.Package.Id);

                requests.Add(new WccPackageRefreshRequest(
                    campaign.Revision.Id,
                    campaignRoot,
                    packageName));
            }
        }

        // Only reconcile children of this installation. Other configured installations
        // share the manifest and must remain available for project revision pins.
        var staleCurrentInstallationPackageIds = manifest.Packages
            .Where(package =>
                package.SourceKind == WccPackageSourceKinds.WesnothMainline &&
                package.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
                package.ParentPackageId == distribution.Package.Id &&
                !activePackageIds.Contains(package.Id))
            .Select(package => package.Id)
            .ToHashSet();
        editor.RemovePackages(staleCurrentInstallationPackageIds);

        return new WccWesnothInstallPackagePlan
        {
            Manifest = editor.BuildManifest(),
            DistributionPackageId = distribution.Package.Id,
            DistributionRevisionId = distribution.Revision.Id,
            RefreshRequests = requests,
            RemovedSourceFileIds = editor.RemovedSourceFileIds
        };
    }
}
