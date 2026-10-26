using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.Campaigns;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private WccScenarioCreationSpec? _campaignScenarioCreationDraft;
    private string? _campaignScenarioCreationRelativePath;
    internal string? CampaignScenarioCreationRelativePath => _campaignScenarioCreationRelativePath;
    private WccScenarioInlineDraft? _campaignScenarioFirstDraft;
    private long _campaignScenarioCreationVersion;
    private bool _changingCampaignScenarioCreation;
    public bool HasCampaignScenarioCreationDraft => _campaignScenarioCreationDraft is not null;
    public bool CanCreateCampaignScenario => CampaignScenarioCreationProblem is null;
    public string? CampaignScenarioCreationProblem
    {
        get
        {
            lock (_campaignFieldGate)
            {
                if (_changingCampaignScenarioCreation || _changingCampaignPresets || IsSynchronizingUi ||
                    _publishingCampaignProjection || _publishingCampaignStructure || _changingCampaignStructure ||
                    _publishingCampaignCredits || _changingCampaignCredits)
                    return "Wait for the current campaign update before creating a scenario.";
                if (HasCampaignScenarioCreationDraft || HasCampaignUnitTypeCreationDraft || _changingCampaignUnitTypeCreation)
                    return "Save or discard the pending creation before creating another scenario or unit type.";
                if (_collectedSaveRecoveryPending) return "Finish source recovery before creating a scenario.";
                if (_campaignAuthoring is null) return "Scenario creation currently needs a WCC-created campaign with its canonical folders. Imported campaign structure remains available in RAW WML.";
                if (_campaignMenuOwner is not { } owner || _campaignProjection?.Source is not { } source || EffectiveCampaignPlan is not { } plan)
                    return "Wait for the campaign source and difficulties to resolve.";
                if (GetCampaignFieldBlockReason(GetCampaignFieldRoute(owner, "rank")) is { } problem) return problem;
                var snapshot = WccScenarioCreationPlanner.Read(source, _campaignAuthoring, plan, _campaignEntryCatalog?.Capture.Sources);
                return snapshot.CanCreate ? null : snapshot.Diagnostic;
            }
        }
    }
    public IReadOnlyList<PendingEditorChange> CampaignScenarioCreationPendingChanges => _campaignScenarioCreationDraft is { } spec
        ? [new("Campaign", "New scenario: " + spec.Id, _campaignScenarioCreationRelativePath +
            (spec.Mode == WccScenarioCreationMode.Guided ? " + map, " + (spec.Guided?.Sides.Count ?? 2) + " sides" : " (empty scaffold)"))] : [];

    public ScenarioCreationViewModel? BeginCampaignScenarioCreation() => BeginCampaignScenarioCreation(null);

    internal ScenarioCreationViewModel? BeginCampaignScenarioCreation(WccDependencyAudioScope? dependencyAudio)
    {
        lock (_campaignFieldGate)
        {
            if (CampaignScenarioCreationProblem is { } problem) { Status = problem; return null; }
            var binding = new CampaignDraftBinding(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
            var source = _campaignProjection!.Source!; var plan = EffectiveCampaignPlan!;
            var authoring = AuthoringKey(_campaignAuthoring);
            var capturedSources = _campaignPresetCapturedSources;
            var catalog = _campaignPresetCatalog;
            var install = _campaignPresetInstallPath;
            bool SameHostContext() => ReferenceEquals(binding.Project, _project) && !IsReadOnly && !_collectedSaveRecoveryPending &&
                _campaignFieldConfiguration is { } current && current.Session == binding.Configuration.Session &&
                current.ProjectIdentity == binding.Configuration.ProjectIdentity && current.RuntimeIdentity == binding.Configuration.RuntimeIdentity &&
                current.AddonsRoot == binding.Configuration.AddonsRoot && authoring == AuthoringKey(_campaignAuthoring);
            bool SameContext() => SameHostContext() && _campaignFieldConfiguration!.RootRevision == binding.Configuration.RootRevision;
            // Only the shell's successful checked Save may use this display guard:
            // a refresh may advance the working revision while retaining this chapter.
            bool SavedContext() => SameHostContext() && EffectiveCampaignPlan is { } selected &&
                selected.CampaignId == plan.CampaignId && selected.CampaignDefine == plan.CampaignDefine;
            bool Current() => SameContext() && IsCurrentCampaignBinding(binding) && ReferenceEquals(plan, EffectiveCampaignPlan) &&
                ReferenceEquals(capturedSources, _campaignPresetCapturedSources) && ReferenceEquals(catalog, _campaignPresetCatalog) &&
                ReferenceEquals(binding.Configuration.Snapshot, _campaignFieldConfiguration?.Snapshot) && install == _campaignPresetInstallPath &&
                source.Text == GetCurrentMainSourceRawWml() && !_campaignHasRawSourceChanges && !IsCampaignUiLoading &&
                !IsCampaignFieldSaveBusy && !IsSynchronizingUi && !HasCampaignScenarioCreationDraft;
            if (capturedSources is null) { Status = "Wait for the campaign source catalog before creating a scenario."; return null; }
            var setup = CreateCampaignScenarioWizardSetup(binding, source, plan, capturedSources, catalog, install, Current, dependencyAudio);
            return new(WccScenarioCreationPlanner.Read(source, _campaignAuthoring, plan, _campaignEntryCatalog?.Capture.Sources), CanSetNewScenarioAsFirst(), Current, SameContext, this, setup)
            { SavedContext = SavedContext, ScenarioDirectory = WccCampaignChapterAuthoringPlanner.ScenarioDirectoryFor(source, plan.CampaignDefine!, _campaignAuthoring, _campaignEntryCatalog?.Capture.Sources) ?? "scenarios" };
        }
    }
    private bool CanSetNewScenarioAsFirst()
    {
        if (_campaignMenuOwner is not { } owner || GetCampaignFieldRoute(owner, "first_scenario") is not { } route ||
            GetCampaignFieldBlockReason(route) is not null || !(route.CanSet || route.CanInsert) ||
            WccScenarioFieldValueCodec.InferInputKind(route.SourceValue) != WccScenarioFieldValueInputKind.Literal ||
            !string.IsNullOrEmpty(WccScenarioFieldValueCodec.DecodeLiteral(route.Value)) || FindCampaignFieldDraft(route) is not null)
            return false;
        return true;
    }
    public bool StageCampaignScenarioCreation(WccScenarioCreationSpec spec, bool setFirstScenario = false)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_campaignFieldGate)
        {
            if (CampaignScenarioCreationProblem is { } problem) { Status = problem; return false; }
            if (setFirstScenario && !CanSetNewScenarioAsFirst())
            { Status = "The campaign already has a first scenario or its source changed. No scenario was staged."; return false; }
            var binding = new CampaignDraftBinding(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
            var source = _campaignProjection!.Source!; var plan = EffectiveCampaignPlan!; var authoring = AuthoringKey(_campaignAuthoring);
            var before = GetPendingSourceChangeStamp();
            bool Current() => IsCurrentCampaignBinding(binding) && !IsReadOnly && !IsCampaignFieldSaveBusy &&
                !_campaignHasRawSourceChanges && !_collectedSaveRecoveryPending && !IsSynchronizingUi &&
                source.Text == GetCurrentMainSourceRawWml() && ReferenceEquals(plan, EffectiveCampaignPlan) &&
                authoring == AuthoringKey(_campaignAuthoring) && before == GetPendingSourceChangeStamp();
            WccScenarioCreationSpec capturedSpec;
            string relativePath;
            try
            {
                var edit = WccScenarioCreationPlanner.CreateEdit(WccScenarioCreationPlanner.Read(source, _campaignAuthoring, plan, _campaignEntryCatalog?.Capture.Sources), spec);
                _ = WccScenarioCreationPlanner.PlanSources(edit);
                capturedSpec = edit.Spec;
                relativePath = edit.ScenarioRelativePath;
            }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
            { Status = refusal.Message; return false; }
            if (capturedSpec.Guided is { } choices)
            {
                var selected = choices.Sides.Where(side => side.Preset is not null).Select(side => side.Preset!)
                    .Concat(choices.Sides.SelectMany(side => side.Leaders ?? []).Where(leader => leader.Preset is not null).Select(leader => leader.Preset!));
                foreach (var preset in selected)
                {
                    var matches = CampaignLocalPresets.Where(current => WccCampaignPresetSelectionBinding.MatchesCurrentEntry(current, preset, plan)).ToArray();
                    if (matches.Length != 1)
                    { Status = "A selected campaign preset changed or is unavailable in this campaign. Reopen the scenario wizard; no scenario was staged."; return false; }
                }
            }
            var firstRoute = setFirstScenario ? GetCampaignFieldRoute(_campaignMenuOwner!, "first_scenario") : null;
            _changingCampaignScenarioCreation = true;
            try
            {
                _campaignFieldSession.Invalidate(); CollectedSaveAuthorityChanging?.Invoke();
                if (!Current()) { Status = "The editing context changed. Current drafts remain; reopen the scenario wizard."; return false; }
                _campaignDraftBinding ??= binding;
                _campaignScenarioCreationDraft = capturedSpec;
                _campaignScenarioCreationRelativePath = relativePath; _campaignScenarioCreationVersion++;
                if (firstRoute is not null)
                {
                    SetCampaignFieldDraft(firstRoute, firstRoute.CanSet ? WmlFieldOperation.Set : WmlFieldOperation.Insert,
                        spec.Id, WccScenarioFieldValueInputKind.Literal);
                    _campaignScenarioFirstDraft = FindCampaignFieldDraft(firstRoute);
                }
            }
            finally { _changingCampaignScenarioCreation = false; }
            RaiseCampaignFieldsChanged();
            Status = "New scenario staged. Global Save checks and creates its files.";
            return HasCampaignScenarioCreationDraft;
        }
    }
    public void RevertCampaignScenarioCreation()
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignFieldSaveBusy || _changingCampaignScenarioCreation || _publishingCampaignProjection || IsSynchronizingUi) return;
            if (!HasCampaignScenarioCreationDraft) return;
            if (_campaignScenarioFirstDraft is { } first) _campaignDrafts.Remove(first);
            ClearCampaignScenarioCreationDraft(); CollectedSaveAuthorityChanging?.Invoke(); FinishCampaignDraftMutation();
        }
    }
    private void ClearCampaignScenarioCreationDraft()
    { _campaignScenarioCreationDraft = null; _campaignScenarioCreationRelativePath = null; _campaignScenarioFirstDraft = null; _campaignScenarioCreationVersion++; }
    private WccScenarioCreationEdit? CaptureCampaignScenarioCreation() => _campaignScenarioCreationDraft is { } spec
        ? WccScenarioCreationPlanner.CreateEdit(WccScenarioCreationPlanner.Read(_campaignProjection!.Source!, _campaignAuthoring, EffectiveCampaignPlan, _campaignEntryCatalog?.Capture.Sources), spec) : null;

    // Publication only: the checked transaction has already created and reread this exact file.
    internal ScenarioFileAsset? AttachCreatedScenario(WccProject project, string path, string text, Func<bool> current)
    {
        lock (_campaignFieldGate)
        {
            if (!ReferenceEquals(project, _project) || !current() || !CanEditProject || HasCampaignSourceDrafts ||
                !IsPathInsideCampaignRoot(path)) return null;
            if (project.ProjectScenarioFiles.FirstOrDefault(asset => string.Equals(asset.SourcePath, path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) is { } existing)
                return existing.RawTextSnapshot == text ? existing : null;
            var asset = WccProjectEditor.AddScenarioFileToProject(project, path, text);
            ScenarioFiles.Add(new ProjectScenarioFileVm(asset));
            return ReferenceEquals(project, _project) && current() ? asset : null;
        }
    }
}
