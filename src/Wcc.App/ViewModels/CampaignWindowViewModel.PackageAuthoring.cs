using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private WccCampaignPackageEdit? _campaignPackageEditDraft;
    private long _campaignPackageDraftVersion;
    private string? _campaignPackageDraftSummary;
    public Func<Task<bool>>? PrepareCampaignPackageAuthoringAsync { get; set; }
    public bool HasCampaignPackageEditDraft => _campaignPackageEditDraft is not null;
    public IReadOnlyList<PendingEditorChange> CampaignPackagePendingChanges => _campaignDependencyBatch is { } batch ? DependencyPendingChanges(batch) : _campaignPackageEditDraft is { } edit
        ? [new("Campaign", _campaignPackageDraftSummary ?? "Package structure", edit.Source.Path)] : [];
    public bool CanStageCampaignPackageEdit => CampaignPackageAuthoringProblem is null;
    public string? CampaignPackageAuthoringProblem => GetCampaignPackageAuthoringProblem(false);
    private string? GetCampaignPackageAuthoringProblem(bool allowDependencyBatch)
    {
        lock (_campaignFieldGate)
        {
            if (IsReadOnly || _campaignFieldConfiguration is not { Session: { CanWrite: true, Scope: WccEditorSessionScope.Project }, Snapshot: not null, RootRevision: not null })
                return "Open a writable campaign with its current indexed source before changing package structure.";
            if (IsCampaignDependencySourceRefreshing)
                return "Wait for the dependency source refresh; pending recipes remain open.";
            if (IsCampaignUiLoading || IsCampaignSourceLoading || IsSynchronizingUi || _publishingCampaignProjection ||
                _publishingCampaignStructure || _changingCampaignStructure || _publishingCampaignCredits || _changingCampaignCredits ||
                _changingCampaignPresets || _changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation)
                return "Wait for the campaign to finish loading before changing package structure.";
            if (IsCampaignFieldSaveBusy || _campaignFieldSession.IsBusy || _campaignFieldSession.Recovery is not null || _collectedSaveRecoveryPending)
                return "Finish the current Save or source recovery before changing package structure.";
            if (HasNonPackageCampaignDrafts || _campaignHasRawSourceChanges ||
                HasCampaignPackageEditDraft && (!allowDependencyBatch || _campaignDependencyBatch is null))
                return "Save or discard pending changes before changing dependencies or chapters.";
            if (_project?.SourcePath is not { } projectMain || _campaignProjectionBinding is not { } binding || !IsCurrentCampaignBinding(binding) ||
                _campaignEntryCatalog is not { Capture: { IsCaptureComplete: true, EntrySource: { } main } } ||
                !SamePresentationPath(main.Path, projectMain) || main.Text != GetCurrentMainSourceRawWml())
                return "Package authoring needs the current complete captured main source.";
            if (allowDependencyBatch && GetCampaignPackageDraftBindingProblem() is { } problem) return problem;
            return null;
        }
    }
    private bool HasNonPackageCampaignDrafts => HasCampaignFieldDrafts || HasCampaignPresetDrafts ||
        HasCampaignScenarioCreationDraft || HasCampaignUnitTypeCreationDraft;

    internal bool StageCampaignPackageEdit(WccCampaignPackageEdit edit, string summary)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (_campaignFieldGate)
        {
            if (CampaignPackageAuthoringProblem is { } problem) { Status = problem; return false; }
            if (edit.SourceReplacements.Count == 0 && edit.SourceCreates.Count == 0)
            { Status = "No dependency or chapter changes to save."; return false; }
            var binding = _campaignProjectionBinding!;
            var main = _campaignEntryCatalog!.Capture.EntrySource!;
            if (!SamePresentationSource(edit.Source, main) || edit.Source.Text != main.Text)
            { Status = "The captured package source changed. Reopen the structural wizard."; return false; }
            var draftBinding = new CampaignDraftBinding(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
            _campaignFieldSession.Invalidate();
            CollectedSaveAuthorityChanging?.Invoke();
            if (!IsCurrentCampaignBinding(binding) || CampaignPackageAuthoringProblem is not null)
            { Status = "The campaign changed while staging package structure. Reopen the wizard."; return false; }
            _campaignDraftBinding = draftBinding;
            _campaignPackageEditDraft = edit;
            _campaignPackageDraftSummary = summary;
            _campaignPackageDraftVersion++;
            RaiseCampaignFieldsChanged();
            RaiseCampaignPackageAuthoringChanged();
            Status = summary + " staged. Global Save checks and writes the package files.";
            return true;
        }
    }
    internal string? GetCampaignPackageDraftBindingProblem()
    {
        if (IsCampaignDependencySourceRefreshing) return "Wait for the dependency source refresh before saving package changes.";
        if (_campaignPackageEditDraft is not { } edit) return null;
        if (_project?.SourcePath is not { } projectMain || _campaignDraftBinding is not { } binding || !IsCurrentCampaignBinding(binding) ||
            !SamePresentationPath(edit.Source.Path, projectMain) || edit.Source.Text != GetCurrentMainSourceRawWml())
            return "The package source binding changed. Structural drafts are retained; discard or reopen their source.";
        return null;
    }
    internal WccCampaignPackageEdit? CaptureCampaignPackageEdit(out string? problem)
    {
        lock (_campaignFieldGate)
        {
            problem = GetCampaignPackageDraftBindingProblem();
            return problem is null ? _campaignPackageEditDraft : null;
        }
    }
    public void RevertCampaignPackageEdit()
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignFieldSaveBusy || _campaignFieldSession.IsBusy || _campaignFieldSession.Recovery is not null || _collectedSaveRecoveryPending ||
                _publishingCampaignProjection || IsSynchronizingUi || IsCampaignDependencySourceRefreshing || !HasCampaignPackageEditDraft) return;
            ClearCampaignPackageEditDraft();
            CollectedSaveAuthorityChanging?.Invoke();
            FinishCampaignDraftMutation();
            RaiseCampaignPackageAuthoringChanged();
        }
    }
    internal void ClearCampaignPackageEditDraft()
    {
        _campaignPackageEditDraft = null; _campaignPackageDraftSummary = null; _campaignDependencyBatch = null;
        _dependencyAuthoringRowsVersion = -1; _campaignPackageDraftVersion++;
    }
    private void RaiseCampaignPackageAuthoringChanged()
    {
        RaiseCampaignChapterAuthoringChanged();
        RaiseDependencyAuthoringChanged();
        foreach (var name in new[] {nameof(HasCampaignPackageEditDraft), nameof(CampaignPackagePendingChanges), nameof(CanStageCampaignPackageEdit), nameof(CampaignPackageAuthoringProblem),
                     nameof(CanModifyCampaignDependencies), nameof(ManagedCampaignDependencies)}) OnPropertyChanged(name);
    }
}