namespace Wcc.Core.Library;

public sealed record WccWorkingProjectDescriptor(
    string Name,
    string RootDirectory,
    WccPackageContentKindId ContentKind,
    Guid? StableProjectId = null);

public sealed class WccWorkingProjectPackagePlan
{
    public required WccLibraryManifest Manifest { get; init; }
    public WccPackageId? ProjectPackageId { get; init; }
    public WccPackageRevisionId? ProjectRevisionId { get; init; }
    public WccPackageRefreshRequest? RefreshRequest { get; init; }
    public required IReadOnlyList<WccSourceFileId> RemovedSourceFileIds { get; init; }
}

/// <summary>
/// Maintains at most one live WCC working-copy package. It is a view of the current project
/// directory, not a permanent library import; switching/closing projects removes the old
/// external overlay while leaving managed snapshots untouched.
/// </summary>
public static class WccWorkingProjectPackagePlanner
{
    public static WccWorkingProjectPackagePlan Build(
        WccLibraryManifest manifest,
        WccWorkingProjectDescriptor? project,
        WccPackageRevisionId? coreRevisionId,
        IReadOnlyCollection<WccPackageRevisionId>? resolvedDependencyRevisionIds = null)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        var editor = new WccPackagePlanEditor(manifest);
        var activePackageIds = new HashSet<WccPackageId>();
        WccPlannedPackage? plannedProject = null;

        if (project is not null && Directory.Exists(project.RootDirectory))
        {
            var preferredPackageId = project.StableProjectId is { } projectId
                ? WccLibraryScopedIdFactory.CreateWorkingPackageId(projectId)
                : (WccPackageId?)null;
            var preferredRevisionId = project.StableProjectId is { } stableProjectId
                ? WccLibraryScopedIdFactory.CreateWorkingRevisionId(stableProjectId)
                : (WccPackageRevisionId?)null;
            plannedProject = editor.Upsert(
                project.Name,
                WccPackageSourceKinds.WccProject,
                project.ContentKind,
                WccPackageStorageMode.WorkingCopy,
                project.RootDirectory,
                parentPackageId: null,
                dependencyRevisionIds: resolvedDependencyRevisionIds ??
                    (coreRevisionId is { } pinnedCoreRevisionId
                        ? new[] { pinnedCoreRevisionId }
                        : Array.Empty<WccPackageRevisionId>()),
                preferredPackageId,
                preferredRevisionId);
            activePackageIds.Add(plannedProject.Package.Id);

            // Configured installed sources keep their durable identity and availability
            // while the same folder is edited. Standalone visibility excludes the working
            // copy; campaign capture and authoring collapse only a verified physical pair.
            // Removing the installed source here also hid unrelated campaigns whose exact
            // dependency closures reached it through a shared music/resource package.
        }

        editor.RemoveInactivePackages(
            WccPackageSourceKinds.WccProject,
            WccPackageStorageMode.WorkingCopy,
            activePackageIds);

        return new WccWorkingProjectPackagePlan
        {
            Manifest = editor.BuildManifest(),
            ProjectPackageId = plannedProject?.Package.Id,
            ProjectRevisionId = plannedProject?.Revision.Id,
            RefreshRequest = plannedProject is null
                ? null
                : new WccPackageRefreshRequest(
                    plannedProject.Revision.Id,
                    project!.RootDirectory,
                    plannedProject.Package.Name),
            RemovedSourceFileIds = editor.RemovedSourceFileIds
        };
    }
}
