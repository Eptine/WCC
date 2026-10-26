using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private long _unitNavigationVersion;

    private sealed record UnitNavigationRequest(long Version, PendingEditorIdentity Identity, string Profile);
    private sealed record IndexedUnitDestination(WccPackageId Package, string PackageRoot,
        WccPackageSourceKindId SourceKind, WccPackageStorageMode StorageMode, string RelativePath,
        string StoragePath, string? ExportSymbol, string Fingerprint, int Start, int End)
    {
        public WccProject? Project { get; init; }
    }

    private UnitNavigationRequest BeginUnitNavigation() => new(++_unitNavigationVersion,
        CapturePendingEditorIdentity(), BuildWesnothSourceResourceProfileIdentity(FindActiveWesnothInstallationProfile()));

    private bool IsCurrentUnitNavigation(UnitNavigationRequest request) => request.Version == _unitNavigationVersion &&
        request.Identity.IsCurrent(this) && request.Profile == BuildWesnothSourceResourceProfileIdentity(FindActiveWesnothInstallationProfile()) &&
        !HasPendingEditorChanges && !IsGlobalSaveBusy;

    private async Task<bool> ConfirmUnitNavigationAsync(UnitNavigationRequest request) =>
        await ConfirmSourceNavigationAsync() && IsCurrentUnitNavigation(request);

    private static bool IsShowingIndexedUnit(MainWindowViewModel editor, UnitTypeCatalogEntryVm entry) =>
        editor.CurrentUnitAsset?.SourcePath is { } path && entry.SourcePath is { } requested &&
        PathsEqual(path, requested) && editor.CurrentSelectedUnitTypeId == entry.UnitTypeId;

    private IndexedUnitDestination? CaptureIndexedUnitDestination(WccEntityId entityId, bool projectScope)
    {
        var project = projectScope ? CampaignEditor.CurrentProject : null;
        if (LibrarySnapshot is not { } snapshot || projectScope && project is null) return null;
        var visible = GetIndexedUnitVisibleRevisions(snapshot, project);
        if (!snapshot.EntitiesById.TryGetValue(entityId, out var entity) ||
            entity.EntityType != WccEntityTypes.UnitType || !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) ||
            !visible.Contains(file.PackageRevisionId) || file.StoragePath is not { } path ||
            entity.ContentFingerprint is not { Length: > 0 } fingerprint ||
            entity.SourceSpan?.StartOffset is not int start || entity.SourceSpan.EndOffsetExclusive is not int end ||
            !snapshot.RevisionsById.TryGetValue(file.PackageRevisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || package.SourceLocation is not { } root) return null;
        return new(package.Id, root, package.SourceKind, package.StorageMode,
            file.RelativePath, path, entity.ExportSymbol, fingerprint, start, end) { Project = project };
    }

    private WccEntityId? ResolveIndexedUnitDestination(IndexedUnitDestination receipt, out WccLibrarySnapshot? snapshot)
    {
        snapshot = LibrarySnapshot;
        if (snapshot is null || !snapshot.PackagesById.TryGetValue(receipt.Package, out var package) ||
            package.SourceKind != receipt.SourceKind || package.StorageMode != receipt.StorageMode ||
            !PathsEqual(package.SourceLocation, receipt.PackageRoot)) return null;
        var current = snapshot;
        var visible = GetIndexedUnitVisibleRevisions(current, receipt.Project);
        // A Save can replace the managed package revision, and with it every entity ID.
        // Follow only the same unchanged physical declaration, never a global ID match.
        var matches = current.EntitiesById.Values.Where(entity => entity.EntityType == WccEntityTypes.UnitType &&
            entity.ExportSymbol == receipt.ExportSymbol && entity.ContentFingerprint == receipt.Fingerprint &&
            entity.SourceSpan?.StartOffset == receipt.Start && entity.SourceSpan?.EndOffsetExclusive == receipt.End &&
            current.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) &&
            visible.Contains(file.PackageRevisionId) && file.RelativePath == receipt.RelativePath &&
            PathsEqual(file.StoragePath, receipt.StoragePath) &&
            current.RevisionsById.TryGetValue(file.PackageRevisionId, out var revision) && revision.PackageId == receipt.Package).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }

    private IReadOnlySet<WccPackageRevisionId> GetIndexedUnitVisibleRevisions(WccLibrarySnapshot snapshot, WccProject? project)
    {
        if (project is null) return _activeLibraryRevisionIds;
        return ReferenceEquals(project, CampaignEditor.CurrentProject) && FindCurrentProjectRevisionId(snapshot) is { } root
            ? WccProjectAuthoringRevisionScope.GetVisibleRevisionIds(snapshot, root)
            : new HashSet<WccPackageRevisionId>();
    }

    private bool LoadedIndexedUnitMatches(IndexedUnitDestination receipt, WccLoadedLibraryUnitEntity loaded) =>
        loaded.MatchesIndexedContentHash && PathsEqual(loaded.SourceFile.SourcePath, receipt.StoragePath) &&
        loaded.SourceFile.Documents.Count(document => document.Unit.Id == receipt.ExportSymbol &&
            document.BlockSpan.StartIndex == receipt.Start && document.BlockSpan.EndIndexExclusive == receipt.End &&
            WccContentHash.ComputeText(document.BlockSpan.RawText) == receipt.Fingerprint) == 1;

    private UnitTypeCatalogEntryVm? FindCurrentUnitCatalogRow(WccEntityId entity)
    {
        lock (_unitDisplaySync) return _allUnitCatalogEntryVms.SingleOrDefault(row => row.LibraryEntityId == entity);
    }

    private ProjectUnitWorkspaceEntryVm? FindCurrentProjectUnitNavigationRow(string key)
    {
        lock (_unitDisplaySync) return _allProjectOwnedUnitEntries.Concat(_allProjectReferencedUnitEntries).Concat(_allProjectUnresolvedUnitEntries)
            .SingleOrDefault(row => row.SelectionKey == key);
    }

    private void ReportChangedUnitDestination(UnitNavigationRequest request)
    {
        if (request.Version == _unitNavigationVersion)
            Status = "The requested unit or editing context changed. The current unit stays open.";
    }
}
