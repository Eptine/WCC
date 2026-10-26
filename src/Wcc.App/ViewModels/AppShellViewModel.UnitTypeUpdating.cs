using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private long _unitSourceSaveDraftVersion;
    private async Task<bool> SaveCurrentProjectUnitIfDirtyAsync(CancellationToken cancellationToken)
    {
        if (!_unitEditorDirty || UnitSession.Scope != WccEditorSessionScope.Project) return true;
        if (_collectedSaveCoordinator.Recovery is { } recovery)
        { Status = DescribeCollectedSaveResult(recovery); return false; }
        var asset = UnitEditor.CurrentUnitAsset;
        var project = CampaignEditor.CurrentProject;
        var session = UnitSession;
        if (asset is null || project is null || !session.CanWrite ||
            !project.ProjectUnitFiles.Any(file => ReferenceEquals(file, asset)))
        { Status = "The current unit must remain a file in the open writable campaign."; return false; }
        // Picker selections persist their receipts and rebind captured editors.
        // Let already queued publications finish before binding this Save.
        await _projectMetadataWriteLock.WaitAsync(cancellationToken);
        _projectMetadataWriteLock.Release();
        // A preceding campaign/scenario RAW save may be publishing a newer index.
        if (_libraryRefreshTask is { IsCompleted: false } refresh) await refresh.WaitAsync(cancellationToken);
        await CampaignEditor.WaitForCampaignFieldProjectionAsync(cancellationToken);
        await ScenarioEditor.WaitForScenarioUiLoadingAsync(cancellationToken);
        if (!ReferenceEquals(asset, UnitEditor.CurrentUnitAsset) || !ReferenceEquals(project, CampaignEditor.CurrentProject) || session != UnitSession)
        { Status = "The editing context changed before checking this unit. Your edits remain open."; return false; }
        if (CampaignEditor.CaptureCampaignUnitTypeUpdateBinding(out var problem) is not { } binding)
        { Status = problem; return false; }
        if (!UnitEditor.TryCaptureWorkingCopyUnitSources(binding.TextDomain, out var original, out var candidate, out problem))
        { Status = problem; return false; }
        var snapshot = binding.State.Snapshot;
        var profileIdentity = BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile());
        var projectSession = ProjectSession;
        var visibleRevisions = _activeLibraryRevisionIds.ToHashSet();
        var editVersion = UnitEditor.EditVersion;
        var draftVersion = _unitSourceSaveDraftVersion;
        var selection = UnitEditor.SelectedUnit;
        var installation = _activeWesnothInstallation;
        bool Current() => _unitEditorDirty && session == UnitSession && session.CanWrite &&
            ReferenceEquals(asset, UnitEditor.CurrentUnitAsset) && ReferenceEquals(selection, UnitEditor.SelectedUnit) &&
            ReferenceEquals(project, CampaignEditor.CurrentProject) && projectSession == ProjectSession && ProjectSession.CanWrite &&
            visibleRevisions.SetEquals(_activeLibraryRevisionIds) && ReferenceEquals(snapshot, LibrarySnapshot) &&
            ReferenceEquals(installation, _activeWesnothInstallation) && UnitEditor.EditVersion == editVersion &&
            _unitSourceSaveDraftVersion == draftVersion &&
            asset.RawTextSnapshot == original!.Text && binding.IsCurrent() &&
            profileIdentity == BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile());
        if (!Current()) { Status = "The unit or source catalog changed before checking. Your edits remain open."; return false; }
        SetGlobalSaveActivity(true, "Reading the unit's campaign and Library dependencies…");
        var projectCapture = await WccStaticSourceCapture.CaptureAsync(snapshot, binding.State.RootRevisionId,
            binding.State.Plan!.CampaignMainSourcePath, cancellationToken: cancellationToken,
            configuredAddonsPath: binding.State.ConfiguredAddonsPath, includeCoreUnits: true);
        if (!Current()) { Status = "The unit changed while reading dependencies. Your edits remain open."; return false; }
        var managed = snapshot.RevisionsById.Values.Where(revision => _activeLibraryRevisionIds.Contains(revision.Id) &&
            snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) && package.IsAvailable &&
            package.StorageMode == WccPackageStorageMode.ManagedSnapshot && package.SourceKind == WccPackageSourceKinds.WccManagedLibrary &&
            package.SourceLocation is { } location && PathsEqual(location, _unitCatalogLibraryRoot)).ToArray();
        if (managed.Length > 1)
        { Status = "The WCC Library has more than one active revision. Refresh its catalog before saving this unit."; return false; }
        WccStaticSourceCaptureResult managedCapture;
        if (managed.Length == 1)
        {
            var entry = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == managed[0].Id &&
                    file.FileType == WccSourceFileTypes.Wml && file.StoragePath is not null)
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal).FirstOrDefault();
            if (entry?.StoragePath is not { } managedPath)
            { Status = "The selected WCC Library revision has no captured WML source."; return false; }
            managedCapture = await WccStaticSourceCapture.CaptureAsync(snapshot, managed[0].Id, managedPath,
                cancellationToken: cancellationToken, configuredAddonsPath: binding.State.ConfiguredAddonsPath, includeCoreUnits: true);
        }
        else
        {
            var core = FindUnitCreationCore(snapshot, binding.State.RootRevisionId);
            if (core is null || !snapshot.RevisionsById.TryGetValue(core.Value, out var revision) ||
                snapshot.PackagesById[revision.PackageId].SourceLocation is not { } coreRoot)
            { Status = "The campaign needs its exact available pinned Core revision."; return false; }
            managedCapture = await WccStaticSourceCapture.CaptureAsync(snapshot, core.Value, Path.Combine(coreRoot, "units.cfg"),
                cancellationToken: cancellationToken, configuredAddonsPath: binding.State.ConfiguredAddonsPath, includeCoreUnits: true);
        }
        if (!Current()) { Status = "The unit or Library sources changed while reading dependencies. Your edits remain open."; return false; }
        if (binding.Contexts.Count == 0)
        { Status = "The campaign's complete declared difficulties are unavailable."; return false; }
        SetGlobalSaveActivity(true, "Planning shared unit dependencies for every difficulty…");
        var plans = await Task.Run(() => binding.Contexts.Select(context => WccUnitTypeUpdatePlanner.Plan(original!, candidate!,
            managedCapture, projectCapture, context, binding.TextDomain, cancellationToken)).ToArray(), cancellationToken);
        if (!Current()) { Status = "The unit changed during dependency planning. Your edits remain open."; return false; }
        if (plans.Select(plan => plan.Identity).Distinct(StringComparer.Ordinal).Count() != 1)
        { Status = "This unit needs different Library dependencies between difficulties. Keep one identical dependency plan for this Save."; return false; }
        SetGlobalSaveActivity(true, "Checking the unit and shared dependencies in every declared context…");
        void EditorChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!Current()) _collectedSaveCoordinator.InvalidateSession();
        }
        UnitEditor.PropertyChanged += EditorChanged;
        WccProjectSourceEditResult? saved;
        try
        {
            saved = await CampaignEditor.SaveCampaignUnitTypeUpdateAsync(_collectedSaveCoordinator, binding,
                plans[0], Current, cancellationToken);
        }
        finally { UnitEditor.PropertyChanged -= EditorChanged; }
        if (saved is null) { Status = CampaignEditor.Status; return false; }
        if (!saved.IsSuccess) { Status = DescribeCollectedSaveResult(saved); return false; }
        if (saved.Candidate is not { } reviewed || !Current())
        { Status = "The unit was saved, but its editing context changed. Reload the saved source before editing again."; return false; }
        // A completed commit is published even if the caller cancels now.
        foreach (var file in reviewed.Files)
            if (await File.ReadAllTextAsync(file.CandidateSource.Path) != file.CandidateSource.Text)
            { Status = "A saved source changed before publication. Reload it; your drafts remain open."; return false; }
        foreach (var created in reviewed.SourceCreates)
            if (await File.ReadAllTextAsync(created.Path) != created.Text)
            { Status = "A newly copied dependency changed before publication. Reload the unit; your drafts remain open."; return false; }
        if (!Current() || !UnitEditor.PublishCheckedWorkingCopyUnitSource(original!, candidate!))
        { Status = "The unit was saved, but its editor changed. Reload its source."; return false; }
        ClearCurrentUnitDirty();
        SetGlobalSaveActivity(true, "Refreshing the saved unit and shared campaign dependencies…");
        var refreshSaved = CampaignEditor.CampaignFieldLibraryRefreshAsync;
        if (refreshSaved is not null) await refreshSaved();
        else await RefreshOwnedProjectPackageIndexAsync();
        if (saved.Status == WccProjectSourceEditStatus.SucceededWithCleanupWarning)
            _collectedSaveCompletionMessage = DescribeCollectedSaveResult(saved);
        return ReferenceEquals(project, CampaignEditor.CurrentProject) && session == UnitSession &&
            ReferenceEquals(asset, UnitEditor.CurrentUnitAsset) && !_unitEditorDirty;
    }
}
