using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private static IReadOnlyCollection<WccPackageRevisionId>? ResolveProjectDependenciesForCorePin(
        WccLibraryManifest manifest, WccWorkingProjectDescriptor? project,
        WccPackageRevisionId? coreRevisionId, WccProjectCoreRevisionPin? pin)
    {
        var resolution = pin is null ? null : WccProjectCorePinResolver.Resolve(manifest, pin);
        return resolution is { IsResolved: true, IsRebuiltIdentity: true }
            ? WccWorkingProjectDependencyResolver.ResolveForPinnedCore(manifest, project, resolution)
            : ResolveWorkingProjectDependenciesForPlan(manifest, project, coreRevisionId);
    }

    private async Task<WccLibraryRefreshResult> CompleteRebuiltProjectCoreIndexAsync(
        WccLibraryRefreshResult indexed, WccLibraryCoordinator coordinator, JsonWccLibraryStore store,
        WccWorkingProjectDescriptor? project, WccProjectCoreRevisionPin? pin,
        System.IProgress<WccLibraryProgress>? progress,
        IEnumerable<WccDiagnosticRecord> packageDiagnostics, CancellationToken cancellationToken)
    {
        if (project is null || pin is null) return indexed;
        var resolution = WccProjectCorePinResolver.Resolve(indexed.Snapshot.Manifest, pin);
        if (resolution is not { IsResolved: true, IsRebuiltIdentity: true }) return indexed;
        var dependencies = WccWorkingProjectDependencyResolver.ResolveForPinnedCore(
            indexed.Snapshot.Manifest, project, resolution);
        var plan = WccWorkingProjectPackagePlanner.Build(indexed.Snapshot.Manifest, project,
            resolution.Revision!.Id, dependencies);
        if (plan.RefreshRequest is not { } request ||
            indexed.Snapshot.PackagesById.TryGetValue(plan.ProjectPackageId!.Value, out var prior) &&
            prior.CurrentRevisionId == plan.ProjectRevisionId)
            return indexed;

        // Only the mutable working overlay is indexed again. Core and other package
        // captures remain exactly those already reviewed by the ordinary index pass.
        await store.SaveManifestAsync(plan.Manifest, cancellationToken);
        foreach (var sourceFileId in plan.RemovedSourceFileIds)
            await store.DeleteFileAnalysisAsync(sourceFileId, cancellationToken);
        var completed = await coordinator.RefreshPackagesAsync([request], progress, cancellationToken, packageDiagnostics);
        completed.ChangeSet.AddedSourceFileIds.AddRange(indexed.ChangeSet.AddedSourceFileIds);
        completed.ChangeSet.UpdatedSourceFileIds.AddRange(indexed.ChangeSet.UpdatedSourceFileIds);
        completed.ChangeSet.RemovedSourceFileIds.AddRange(indexed.ChangeSet.RemovedSourceFileIds);
        return new WccLibraryRefreshResult
        {
            Snapshot = completed.Snapshot,
            ChangeSet = completed.ChangeSet,
            CacheHitCount = indexed.CacheHitCount + completed.CacheHitCount,
            AnalyzedFileCount = indexed.AnalyzedFileCount + completed.AnalyzedFileCount
        };
    }
}
