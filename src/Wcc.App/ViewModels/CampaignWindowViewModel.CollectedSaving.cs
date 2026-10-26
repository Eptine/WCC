using System;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    internal event Action? CollectedSaveAuthorityChanging;
    private bool _collectedSaveRecoveryPending;
    internal void BlockCollectedSaveForRecovery()
    {
        lock (_campaignFieldGate)
        {
            _collectedSaveRecoveryPending = true;
            RevokeCampaignFieldAuthority();
            OnPropertyChanged(nameof(IsReadOnly));
            RaiseCampaignFieldsChanged();
        }
    }

    private bool HasCurrentCheckedCampaignProjection() =>
        _campaignProjectionTask.IsCompletedSuccessfully && HasExactCheckedCampaignProjectionBinding();

    private bool HasExactCheckedCampaignProjectionBinding() =>
        !_publishingCampaignProjection && !_campaignHasRawSourceChanges && !_collectedSaveRecoveryPending &&
        _campaignFieldSession.Recovery is null && !IsReadOnly && _campaignFieldConfiguration?.Session.CanWrite == true &&
        _campaignProjectionBinding is { } binding && IsCurrentCampaignBinding(binding) &&
        ReferenceEquals(_campaignProjectionDocument, _project?.Campaign) &&
        _campaignProjection is { IsComplete: true, Source: { } source } && IsCurrentCampaignMenuSource(source) &&
        HasSelectedCampaignEntryBinding && _campaignEntryCatalog is { } catalog &&
        _savedCampaignEntryPublication is { PublishedIdentity: { } published } receipt &&
        _selectedCampaignEntry?.Identity == published && IsCurrentCheckedCampaignEntryPublication(receipt, catalog);

    private bool TryRebindCheckedCampaignProjection(CampaignFieldConfiguration configuration)
    {
        if ((!_campaignSaveBusy && !HasActiveCheckedCampaignEntryDisplayPublication) || HasCampaignSourceDrafts || !HasExactCheckedCampaignProjectionBinding() ||
            _campaignFieldConfiguration is not { Plan: { } previousPlan } previous ||
            _selectedCampaignAdapter?.Plan is not { } projectedPlan || !ReferenceEquals(configuration.Plan, projectedPlan) ||
            !previous.IsEquivalent(configuration with { Plan = previous.Plan }) ||
            previousPlan.CampaignId != projectedPlan.CampaignId || previousPlan.CampaignDefine != projectedPlan.CampaignDefine ||
            !previousPlan.Contexts.Select(context => context.SemanticKey).SequenceEqual(projectedPlan.Contexts.Select(context => context.SemanticKey)))
            return false;
        // Membership publication adopts this exact already-projected plan after
        // our checked Save updated its authored menu span. Source, snapshot,
        // access, profile, selected owner and all declared contexts are unchanged.
        _campaignFieldConfiguration = configuration;
        _campaignProjectionBinding = _campaignProjectionBinding! with { Configuration = configuration };
        _campaignEntryCatalogConfiguration = configuration;
        return true;
    }
    internal WccCollectedSourceEditor? CaptureCollectedCampaignSave(out string? problem)
    {
        lock (_campaignFieldGate)
        {
            problem = null;
            if (IsCampaignDependencySourceRefreshing)
            { problem = "Wait for the dependency source refresh before saving pending changes."; return null; }
            if (_publishingCampaignStructure || _changingCampaignStructure || _publishingCampaignCredits || _changingCampaignCredits || _changingCampaignPresets || (_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation))
            { problem = "Finish the current campaign presentation update before saving."; return null; }
            if (!CommitCampaignTextInputs(out problem)) return null;
            if (!HasCampaignSourceDrafts) return null;
            if (HasCampaignPackageEditDraft) return CaptureCollectedCampaignPackageSave(out problem);
            problem = _campaignDrafts.FirstOrDefault(draft => draft.ValidationError is not null)?.ValidationError
                ?? _campaignStructureValidation ?? _campaignCreditsValidation ?? GetCampaignDraftBindingProblem() ??
                GetCampaignFieldBlockReason(_campaignDrafts.FirstOrDefault()?.Route ??
                    (_campaignMenuOwner is { } menuOwner ? GetCampaignFieldRoute(menuOwner, "rank") : null));
            if (problem is not null) return null;
            var binding = _campaignDraftBinding!;
            var configuration = binding.Configuration;
            var source = _campaignProjection!.Source!;
            var drafts = _campaignDrafts.ToArray();
            var structureVersion = _campaignStructureDraftVersion;
            var structure = CaptureCampaignStructureEdit();
            var creditsVersion = _campaignCreditsDraftVersion;
            var credits = CaptureCampaignCreditsEdit();
            var presetVersion = _campaignPresetDraftVersion;
            var scenarioCreationVersion = _campaignScenarioCreationVersion;
            var unitTypeCreationVersion = _campaignUnitTypeCreationVersion;
            WccCampaignLocalPresetStorageEdit? presets;
            WccScenarioCreationEdit? scenarioCreation;
            WccUnitTypeCreationEdit? unitTypeCreation;
            try { presets = CaptureCampaignPresetStorage(); scenarioCreation = CaptureCampaignScenarioCreation(); unitTypeCreation = CaptureCampaignUnitTypeCreation(); }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
            { problem = refusal.Message; return null; }
            var createdScenarioSource = scenarioCreation is null ? null : WccScenarioCreationPlanner.PlanSources(scenarioCreation)
                .Single(sourceFile => sourceFile.Path.EndsWith(".cfg", StringComparison.Ordinal));
            var createdUnitSource = unitTypeCreation is null ? null : WccUnitTypeCreationPlanner.PlanSources(unitTypeCreation).Single();
            var structureSummary = CampaignStructurePendingChanges.Select(change => change.Label + ": " + change.Detail).ToArray();
            var creditsSummary = CampaignCreditsPendingChanges.Select(change => change.Label + ": " + change.Detail).ToArray();
            var presetSummary = CampaignPresetPendingChanges.Select(change => change.Label + ": " + change.Detail).ToArray();
            var scenarioCreationSummary = CampaignScenarioCreationPendingChanges.Select(change => change.Label + ": " + change.Detail).ToArray();
            var unitTypeCreationSummary = CampaignUnitTypeCreationPendingChanges.Select(change => change.Label + ": " + change.Detail).ToArray();
            var state = new WccScenarioFieldEditSessionState(configuration.ProjectIdentity!, configuration.Session.RootPath!,
                configuration.RootRevision!.Value.Value.ToString(), true, configuration.Snapshot!, configuration.RootRevision.Value,
                source, EffectiveCampaignPlan, _campaignProjection.Context, _campaignMenuOwner!,
                configuration.AddonsRoot, configuration.RuntimeIdentity) { ReviewScope = WccScenarioFieldReviewScope.CampaignMenu, EntryReview = CurrentCampaignEntryReview };
            var fields = drafts.Select(BuildCampaignFieldProposal).ToArray();
            bool IsCurrent()
            {
                lock (_campaignFieldGate)
                    return IsCurrentCampaignBinding(binding) && !IsReadOnly && configuration.Session.CanWrite &&
                        !_campaignHasRawSourceChanges && drafts.SequenceEqual(_campaignDrafts) &&
                        structureVersion == _campaignStructureDraftVersion &&
                        creditsVersion == _campaignCreditsDraftVersion &&
                        presetVersion == _campaignPresetDraftVersion && !_changingCampaignPresets &&
                        scenarioCreationVersion == _campaignScenarioCreationVersion && !(_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation) &&
                        unitTypeCreationVersion == _campaignUnitTypeCreationVersion &&
                        IsCurrentCampaignMenuSource(source);
            }
            void Begin(string status)
            {
                lock (_campaignFieldGate)
                {
                    _campaignSaveBusy = true;
                    _campaignFieldStatus = Status = status;
                    RaiseCampaignFieldsChanged();
                }
            }
            bool Publish(string text)
            {

                lock (_campaignFieldGate)
                {
                    if (!IsCurrent()) return false;
                    SourceChanging?.Invoke();
                    if (!IsCurrent()) return false;
                    PublishSavedCampaignMenuSource(text, source.Path);
                    _campaignHasRawSourceChanges = false;
                    _campaignDrafts.Clear();
                    ClearCampaignStructureDrafts();
                    ClearCampaignCreditsDrafts();
                    ClearCampaignPresetDrafts();
                    ClearCampaignScenarioCreationDraft();
                    ClearCampaignUnitTypeCreationDraft();
                    _campaignDraftBinding = null;
                    _campaignProjection = null;
                    _campaignStructureInventory = null;
                    _campaignMenuOwner = null;
                    _campaignRoutes.Clear();
                    if (createdScenarioSource is not null && !binding.Project.ProjectScenarioFiles.Any(asset =>
                            string.Equals(asset.SourcePath, createdScenarioSource.Path,
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                    {
                        var createdAsset = WccProjectEditor.AddScenarioFileToProject(binding.Project,
                            createdScenarioSource.Path, createdScenarioSource.Text);
                        ScenarioFiles.Add(new ProjectScenarioFileVm(createdAsset));
                        if (!ReferenceEquals(binding.Project, _project)) return false;
                    }
                    _campaignReviewSummary = "Campaign menu and every declared play context checked in the combined Save." +
                        Environment.NewLine + string.Join(Environment.NewLine, drafts.Select(draft => draft.Label + ": " +
                            (draft.Route.Value ?? draft.Route.SourceValue ?? "absent") + " → " +
                            (draft.Operation == global::Wcc.Core.Wml.WmlFieldOperation.Remove ? "remove local override" : draft.Text)));
                    if (structureSummary.Length != 0) _campaignReviewSummary += Environment.NewLine + string.Join(Environment.NewLine, structureSummary);
                    if (creditsSummary.Length != 0) _campaignReviewSummary += Environment.NewLine + string.Join(Environment.NewLine, creditsSummary);
                    if (presetSummary.Length != 0) _campaignReviewSummary += Environment.NewLine + string.Join(Environment.NewLine, presetSummary);
                    if (scenarioCreationSummary.Length != 0) _campaignReviewSummary += Environment.NewLine + string.Join(Environment.NewLine, scenarioCreationSummary);
                    if (unitTypeCreationSummary.Length != 0) _campaignReviewSummary += Environment.NewLine + string.Join(Environment.NewLine, unitTypeCreationSummary);
                    if (createdUnitSource is not null && !binding.Project.ProjectUnitFiles.Any(asset =>
                            string.Equals(asset.SourcePath, createdUnitSource.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                    {
                        var createdAsset = WccProjectEditor.AddUnitFileToProject(binding.Project, createdUnitSource.Path, createdUnitSource.Text);
                        UnitFiles.Add(new ProjectUnitFileVm(createdAsset));
                        if (!ReferenceEquals(binding.Project, _project)) return false;
                    }
                    if (_project?.Campaign is not null) RunUiSync(RefreshLegacyCampaignFields);
                    _campaignFieldStatus = Status = "Refreshing the saved campaign preview…";
                    RaiseCampaignFieldsChanged();
                    CampaignFieldsSaved?.Invoke(this, EventArgs.Empty);
                    return ReferenceEquals(binding.Project, _project) &&
                        (!SamePresentationPath(source.Path, _project.SourcePath!) || _project.CampaignRawTextSnapshot == text && _project.Campaign?.OriginalText == text);
                }
            }
            void End()
            {
                lock (_campaignFieldGate)
                {
                    _campaignSaveBusy = false;
                    // The checked post-commit refresh already published this exact
                    // source/context. Keep its derived views when ending the busy scope.
                    if (!HasCampaignSourceDrafts && !HasCurrentCheckedCampaignProjection())
                    { RevokeCampaignFieldAuthority(); StartCampaignFieldProjection(); }
                    RaiseCampaignFieldsChanged();
                }
            }
            return new(state, new(source, fields, campaignStructure: structure, campaignCredits: credits)
                { ScenarioCreation = scenarioCreation, PresetStorage = presets, UnitTypeCreation = unitTypeCreation }, IsCurrent, _campaignFieldSession.Invalidate, Begin, Publish, _ => { }, End);
        }
    }
}
