using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    // The same queue owns broad discovery and refreshes after a known owned-package mutation.
    // Raw cached analyses are rebuilt together so incoming references and local shadowing update.
    internal Task RefreshOwnedProjectPackageIndexAsync(CancellationToken cancellationToken = default) =>
        StartOwnedPackageRefreshAsync(managedLibrary: false, cancellationToken);

    internal Task RefreshOwnedLibraryPackageIndexAsync(CancellationToken cancellationToken = default) =>
        StartOwnedPackageRefreshAsync(managedLibrary: true, cancellationToken);

    private Task StartOwnedPackageRefreshAsync(bool managedLibrary, CancellationToken cancellationToken) =>
        StartLibraryRefreshAsync(managedLibrary, cancellationToken);

    private async Task CompleteOwnedPackageRefreshAsync(bool managedLibrary, TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        var firstPass = true;
        var completed = false;
        try
        {
            while (true)
            {
                lock (_libraryRefreshRequestGate) _libraryRefreshRequestedAgain = false;
                if (firstPass)
                {
                    firstPass = false;
                    if (!await TryRefreshOwnedPackageIndexAsync(managedLibrary, cancellationToken))
                        RequestLibraryRefreshAgain();
                }
                else await RefreshConfiguredLibraryIndexCoreAsync(cancellationToken);

                lock (_libraryRefreshRequestGate)
                    if (_libraryRefreshRequestedAgain && !cancellationToken.IsCancellationRequested) continue;
                IsLibraryIndexRefreshing = false;
                ConfigureCampaignDependencySourceHost();
                lock (_libraryRefreshRequestGate)
                {
                    // Cleanup can invoke a host callback requesting another pass.
                    // Finish only after that callback, before admitting a new worker.
                    if (_libraryRefreshRequestedAgain && !cancellationToken.IsCancellationRequested) continue;
                    completed = true;
                    completion.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        { LibraryIndexStatus = "Library refresh cancelled; the previous snapshot remains available."; }
        catch (Exception failure)
        { LibraryIndexStatus = "Library refresh failed: " + failure.Message; }
        finally
        {
            if (!completed)
            {
                try
                {
                    IsLibraryIndexRefreshing = false;
                    ConfigureCampaignDependencySourceHost();
                }
                finally
                {
                    // Owned refresh keeps its existing status-based error policy.
                    lock (_libraryRefreshRequestGate) completion.TrySetResult();
                }
            }
        }
    }
    private async Task<bool> TryRefreshOwnedPackageIndexAsync(bool managedLibrary, CancellationToken cancellationToken)
    {
        var before = LibrarySnapshot;
        var installation = _activeWesnothInstallation;
        if (before is null || installation is null || !TryFindActiveCoreRevision(before, out var corePackage, out var coreRevision))
            return false;
        if (corePackage.SourceLocation is not { } coreRoot) return false;
        var project = CampaignEditor.CurrentProject;
        var session = ProjectSession;
        var descriptor = managedLibrary ? null : CreateCurrentWorkingProjectDescriptor();
        if (!managedLibrary && descriptor is null) return false;
        bool Current() => ReferenceEquals(before, LibrarySnapshot) && ReferenceEquals(installation, _activeWesnothInstallation) &&
            (managedLibrary || ReferenceEquals(project, CampaignEditor.CurrentProject) && session == ProjectSession);
        var store = _libraryIndexStore;
        var manifest = await store.LoadManifestAsync(cancellationToken);
        var storedCorePackage = manifest.Packages.SingleOrDefault(package => package.Id == corePackage.Id);
        var storedCoreRevision = manifest.PackageRevisions.SingleOrDefault(revision => revision.Id == coreRevision.Id);
        if (!Current() || storedCorePackage is null || storedCoreRevision is null ||
            storedCoreRevision.PackageId != corePackage.Id || storedCorePackage.CurrentRevisionId != corePackage.CurrentRevisionId ||
            storedCorePackage.SourceKind != corePackage.SourceKind || storedCorePackage.ContentKind != corePackage.ContentKind ||
            storedCorePackage.StorageMode != corePackage.StorageMode || storedCorePackage.IsAvailable != corePackage.IsAvailable ||
            !PathsEqual(storedCorePackage.SourceLocation, coreRoot) ||
            storedCoreRevision.Version != coreRevision.Version || storedCoreRevision.TargetWesnothVersion != coreRevision.TargetWesnothVersion ||
            storedCoreRevision.ContentFingerprint != coreRevision.ContentFingerprint ||
            !storedCoreRevision.DependencyRevisionIds.SequenceEqual(coreRevision.DependencyRevisionIds))
            return false;
        WccPackageRefreshRequest request;
        if (managedLibrary)
        {
            // The first saved reusable CFG needs its ordinary managed package identity.
            // This planner touches only the owned library root, not the installed trees.
            var plan = await Task.Run(() => WccManagedLibraryPackagePlanner.Build(manifest, _unitCatalogLibraryRoot, coreRevision.Id),
                cancellationToken);
            if (!Current() || plan.RefreshRequest is null || plan.RemovedSourceFileIds.Count != 0) return false;
            request = plan.RefreshRequest;
            await store.SaveManifestAsync(plan.Manifest, cancellationToken);
        }
        else
        {
            var revisionId = FindCurrentProjectRevisionId(before);
            var revision = manifest.PackageRevisions.SingleOrDefault(row => row.Id == revisionId);
            var package = revision is null ? null : manifest.Packages.SingleOrDefault(row => row.Id == revision.PackageId);
            if (revision is null || package is null || package.SourceKind != WccPackageSourceKinds.WccProject ||
                package.StorageMode != WccPackageStorageMode.WorkingCopy || !package.IsAvailable ||
                !PathsEqual(package.SourceLocation, descriptor!.RootDirectory)) return false;
            // Configured campaigns keep their durable read-only Library identity
            // while the working view edits the same files. Replan both views and
            // their dependency cycles together after a save; refreshing only the
            // working package would leave the persistent Library snapshot stale.
            if (manifest.Packages.Any(other => other.Id != package.Id &&
                    other.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
                    (other.SourceKind == WccPackageSourceKinds.WesnothAddon ||
                     other.SourceKind == WccPackageSourceKinds.WesnothMainline) &&
                    PathsEqual(other.SourceLocation, descriptor.RootDirectory)))
                return false;
            request = new(revision.Id, descriptor.RootDirectory, package.Name);
        }
        IsLibraryIndexRefreshing = true;
        LibraryIndexStatus = managedLibrary ? "Updating the saved WCC Library package..." : "Updating the saved campaign package...";
        using var progress = new WccLibraryProgressRelay(action => Dispatcher.UIThread.Post(action),
            value => LibraryIndexStatus = FormatLibraryProgress(value));
        // Package metadata planning is unchanged by this owned-file mutation. Keep its
        // diagnostics while cache/build/reference diagnostics are recomputed from raw shards.
        var packageDiagnostics = before.Diagnostics.Where(diagnostic => diagnostic.Code.StartsWith("addon.", StringComparison.Ordinal)).ToArray();
        var result = await new WccLibraryCoordinator(store).RefreshPackagesAsync([request], progress,
            cancellationToken, packageDiagnostics);
        if (!managedLibrary && descriptor is not null && Current())
        {
            var projectCoreId = ResolveCurrentProjectCoreRevisionForPlan(result.Snapshot.Manifest, coreRevision.Id,
                _currentProjectMetadata?.Compatibility?.CoreRevision);
            result = await CompleteCampaignDependencySourceIndexAsync(result, new WccLibraryCoordinator(store), store,
                descriptor, projectCoreId, CampaignDependencyProfile()?.AddonsPath, progress, packageDiagnostics, Current, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested(); progress.Complete();
        // A format upgrade, absent shard, or stale cache must receive ordinary recovery.
        // Never publish a fast snapshot which silently drops unrelated indexed definitions.
        if (result.Snapshot.Diagnostics.Any(diagnostic => diagnostic.Code is
                "library.unsupported_file_analysis_version" or "library.stale_file_analysis" or
                "library.missing_file_analysis" or "library.file_analysis_load_failed"))
        {
            LibraryIndexStatus = "Refreshing the complete package cache after a cache change...";
            return false;
        }
        if (!Current()) return false;
        LibraryIndexStatus = "Applying the updated package and editor catalogs...";
        var activeRevisionIds = WccStandaloneLibraryRevisionScope.GetVisibleRevisionIds(result.Snapshot, corePackage.Id,
            ActiveWesnothInstallationProfile?.AddonsPath);
        var assetRegistry = await Task.Run(() => WccLibraryAssetRegistryProjection.Build(result.Snapshot, activeRevisionIds), cancellationToken);
        if (!Current()) return false;
        using var scenarioPreviewRefresh = ScenarioEditor.DeferPreviewRefresh();
        BeginLibraryPickerProjectionDeferral();
        try
        {
            LibrarySnapshot = result.Snapshot;
            ResetUnitDisplayScopes();
            ApplyActiveLibraryView(activeRevisionIds, assetRegistry);
            ConfigureEditorsForActiveWesnothInstallation();
            RestoreLibraryPickerProjection();
            await ReconcileCurrentProjectBindingsAsync(result.Snapshot, cancellationToken);
            RaiseProjectCompatibilityPropertiesChanged();
            RefreshUnitCatalogEntries(); RefreshProjectUnitWorkspaceEntries();
        }
        finally { EndLibraryPickerProjectionDeferral(); }
        OnPropertyChanged(nameof(LibrarySnapshot));
        LibrarySnapshotChanged?.Invoke(this, EventArgs.Empty);
        LibraryIndexStatus = $"Library ready: updated {request.ProgressLabel ?? "saved package"}; " +
            $"{result.AnalyzedFileCount} changed file(s), {result.CacheHitCount} cache hit(s).";
        return true;
    }
}
