using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Library;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private async Task<WccLibraryRefreshResult> CompleteCampaignDependencySourceIndexAsync(
        WccLibraryRefreshResult indexed, WccLibraryCoordinator coordinator, JsonWccLibraryStore store,
        WccWorkingProjectDescriptor? descriptor, WccPackageRevisionId? coreId, string? addonsPath,
        IProgress<WccLibraryProgress>? progress, IEnumerable<WccDiagnosticRecord> packageDiagnostics,
        Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        if (descriptor is null || coreId is null || CampaignEditor.CurrentProject is not { SourcePath: { } main } project)
            return indexed;
        var references = new HashSet<string>(StringComparer.Ordinal);
        for (var pass = 0; pass < 16; pass++)
        {
            if (!isCurrent()) return indexed;
            var manifest = indexed.Snapshot.Manifest;
            var working = manifest.Packages.SingleOrDefault(package => package.SourceKind == WccPackageSourceKinds.WccProject &&
                package.StorageMode == WccPackageStorageMode.WorkingCopy && PathsEqual(package.SourceLocation, descriptor.RootDirectory));
            if (working?.CurrentRevisionId is not { } root || !indexed.Snapshot.RevisionsById.TryGetValue(root, out var revision)) return indexed;
            if (manifest.Packages.Any(package => package.IsAvailable && package.SourceKind == WccPackageSourceKinds.WesnothAddon))
            {
                LibraryIndexStatus = "Resolving campaign references to add-on sources…";
                var capture = await WccStaticSourceCapture.CaptureAsync(indexed.Snapshot, root, main,
                    cancellationToken: cancellationToken, configuredAddonsPath: addonsPath);
                // A partial source capture is not a basis for extending a binding graph.
                if (capture.IsCaptureComplete)
                {
                    var discovered = await Task.Run(() =>
                        WccAddonSourceReferenceDiscovery.Discover(capture.Sources, cancellationToken), cancellationToken);
                    references.UnionWith(discovered.AddonIds);
                }
            }
            if (!isCurrent()) return indexed;
            var selected = WccWorkingProjectDependencyResolver.ResolveForSourceReferences(manifest, descriptor, coreId, references);
            var report = WccWorkingProjectDependencyResolver.Inspect(indexed.Snapshot, descriptor, coreId,
                selected ?? revision.DependencyRevisionIds, references);
            if (selected is null || revision.DependencyRevisionIds.SequenceEqual(selected))
            {
                _campaignDependencyEvidence = new(project, indexed.Snapshot, report);
                return indexed;
            }
            var plan = WccWorkingProjectPackagePlanner.Build(manifest, descriptor, coreId, selected);
            if (plan.RefreshRequest is not { } request) return indexed;
            if (!isCurrent()) return indexed;
            await store.SaveManifestAsync(plan.Manifest, cancellationToken);
            foreach (var file in plan.RemovedSourceFileIds) await store.DeleteFileAnalysisAsync(file, cancellationToken);
            var next = await coordinator.RefreshPackagesAsync([request], progress, cancellationToken, packageDiagnostics);
            next.ChangeSet.AddedSourceFileIds.AddRange(indexed.ChangeSet.AddedSourceFileIds);
            next.ChangeSet.UpdatedSourceFileIds.AddRange(indexed.ChangeSet.UpdatedSourceFileIds);
            next.ChangeSet.RemovedSourceFileIds.AddRange(indexed.ChangeSet.RemovedSourceFileIds);
            indexed = new WccLibraryRefreshResult
            {
                Snapshot = next.Snapshot, ChangeSet = next.ChangeSet,
                CacheHitCount = indexed.CacheHitCount + next.CacheHitCount,
                AnalyzedFileCount = indexed.AnalyzedFileCount + next.AnalyzedFileCount
            };
        }
        // Keep the bounded, explicitly captured graph; never guess another loader.
        _dependencySourceStatus = "Source discovery reached its limit of 16 graph passes. Review the remaining references in RAW WML.";
        return indexed;
    }
}
