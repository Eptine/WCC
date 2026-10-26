using System;
using System.Linq;
using Wcc.Core.Campaigns;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    internal string GetPendingSourceChangeStamp()
    {
        lock (_campaignFieldGate)
            return _campaignTextInputVersion + ":" + _campaignFieldGeneration + ":" + _campaignStructureDraftVersion + ":" + _campaignCreditsDraftVersion + ":" + _campaignPresetDraftVersion + ":" + _campaignScenarioCreationVersion + ":" + _campaignUnitTypeCreationVersion + ":" + _campaignPackageDraftVersion + ":" + IsReadOnly + ":" + GetCurrentMainSourceRawWml();
    }

    internal Func<bool>? PreparePendingChangesDiscard(bool restoreSource, out string? problem)
    {
        lock (_campaignFieldGate)
        {
            problem = null;
            if (IsCampaignDependencySourceRefreshing)
            { problem = "Wait for the dependency source refresh before discarding pending changes."; return null; }
            if (_publishingCampaignStructure || _changingCampaignStructure || _publishingCampaignCredits || _changingCampaignCredits || _changingCampaignPresets || (_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation))
            { problem = "Finish the current campaign presentation update before discarding."; return null; }
            if (IsCampaignFieldSaveBusy || _campaignFieldSession.IsBusy || _campaignFieldSession.Recovery is not null)
            { problem = "Finish the campaign save or recovery before discarding changes."; return null; }
            if (!restoreSource && !HasCampaignSourceDrafts) return () => true;
            var project = _project;
            var document = project?.Campaign;
            var generation = _campaignFieldGeneration;
            var structureVersion = _campaignStructureDraftVersion;
            var creditsVersion = _campaignCreditsDraftVersion;
            var presetVersion = _campaignPresetDraftVersion;
            var scenarioCreationVersion = _campaignScenarioCreationVersion;
            var unitTypeCreationVersion = _campaignUnitTypeCreationVersion;
            var packageVersion = _campaignPackageDraftVersion;
            var inputVersion = _campaignTextInputVersion;
            var drafts = _campaignDrafts.ToArray();
            var baseline = project?.CampaignRawTextSnapshot;
            CampaignDocument? restored = null;
            if (restoreSource)
            {
                if (project is null || document is null || baseline is null)
                { problem = "The campaign's saved source baseline is unavailable. Its edits remain open."; return null; }
                try { restored = CampaignImporter.ImportFromText(baseline, project.SourcePath); }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                { problem = "Could not restore the campaign source: " + exception.Message; return null; }
            }
            return () =>
            {
                lock (_campaignFieldGate)
                {
                    if (IsCampaignFieldSaveBusy || inputVersion != _campaignTextInputVersion || generation != _campaignFieldGeneration ||
                        _publishingCampaignStructure || _changingCampaignStructure || _publishingCampaignCredits || _changingCampaignCredits || _changingCampaignPresets || (_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation) ||
                        structureVersion != _campaignStructureDraftVersion || creditsVersion != _campaignCreditsDraftVersion || presetVersion != _campaignPresetDraftVersion || scenarioCreationVersion != _campaignScenarioCreationVersion || unitTypeCreationVersion != _campaignUnitTypeCreationVersion || packageVersion != _campaignPackageDraftVersion || !drafts.SequenceEqual(_campaignDrafts) ||
                        !ReferenceEquals(project, _project) || !ReferenceEquals(document, _project?.Campaign) ||
                        restoreSource && baseline != _project?.CampaignRawTextSnapshot) return false;
                    if (restoreSource) SourceChanging?.Invoke();
                    if (IsCampaignFieldSaveBusy || inputVersion != _campaignTextInputVersion || generation != _campaignFieldGeneration ||
                        _publishingCampaignStructure || _changingCampaignStructure || _publishingCampaignCredits || _changingCampaignCredits || _changingCampaignPresets || (_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation) ||
                        structureVersion != _campaignStructureDraftVersion || creditsVersion != _campaignCreditsDraftVersion || presetVersion != _campaignPresetDraftVersion || scenarioCreationVersion != _campaignScenarioCreationVersion || unitTypeCreationVersion != _campaignUnitTypeCreationVersion || packageVersion != _campaignPackageDraftVersion || !drafts.SequenceEqual(_campaignDrafts) ||
                        !ReferenceEquals(project, _project) || !ReferenceEquals(document, _project?.Campaign)) return false;
                    DiscardCampaignTextInputs();
                    foreach (var row in _campaignFields) row.ResetInputState();
                    _campaignDrafts.Clear();
                    ClearCampaignStructureDrafts();
                    ClearCampaignCreditsDrafts();
                    ClearCampaignPresetDrafts();
                    ClearCampaignScenarioCreationDraft();
                    ClearCampaignUnitTypeCreationDraft();
                    ClearCampaignPackageEditDraft();
                    _campaignDraftBinding = null;
                    _campaignFieldStatus = null;
                    if (restored is not null)
                    {
                        project!.Campaign = restored;
                        RunUiSync(RefreshLegacyCampaignFields);
                    }
                    if (restored is not null) CampaignSourceWasRebound();
                    else
                    {
                        // Discarding typed proposals changes no source bytes.
                        // Revoke their generation, but retain the exact package
                        // entry selection while the same source is reprojected.
                        // The global discard checks that identity after each host.
                        RevokeCampaignFieldAuthority();
                        StartCampaignFieldProjection();
                    }
                    RaiseCampaignFieldsChanged();
                    return ReferenceEquals(project, _project) && (restored is null || ReferenceEquals(restored, project?.Campaign));
                }
            };
        }
    }
}
