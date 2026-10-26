using System;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    // Structural recipes share the collected writer and its revocable source binding.
    // Keep them exclusive so scalar drafts never target coordinates shifted by a chapter/loader edit.
    private WccCollectedSourceEditor? CaptureCollectedCampaignPackageSave(out string? problem)
    {
        if (HasCampaignFieldDrafts || HasCampaignPresetDrafts || HasCampaignScenarioCreationDraft || HasCampaignUnitTypeCreationDraft)
        { problem = "Save or discard other campaign changes before its pending structural change."; return null; }
        var edit = CaptureCampaignPackageEdit(out problem);
        if (problem is not null || edit is null) return null;
        var binding = _campaignDraftBinding!;
        var configuration = binding.Configuration;
        if (_campaignMenuOwner is null || _campaignProjection is null || EffectiveCampaignPlan is null ||
            configuration.Snapshot is null || configuration.RootRevision is null || configuration.Session.RootPath is null)
        { problem = "Wait for the current package menu and context before saving its structural changes."; return null; }
        var version = _campaignPackageDraftVersion;
        var source = edit.Source;
        var state = new WccScenarioFieldEditSessionState(configuration.ProjectIdentity!, configuration.Session.RootPath,
            configuration.RootRevision.Value.Value.ToString(), true, configuration.Snapshot, configuration.RootRevision.Value,
            source, EffectiveCampaignPlan, _campaignProjection.Context, _campaignMenuOwner,
            configuration.AddonsRoot, configuration.RuntimeIdentity)
            { ReviewScope = WccScenarioFieldReviewScope.CampaignMenu, EntryReview = CurrentCampaignEntryReview };
        bool Current() => ReferenceEquals(edit, _campaignPackageEditDraft) && version == _campaignPackageDraftVersion &&
            IsCurrentCampaignBinding(binding) && !IsReadOnly && configuration.Session.CanWrite &&
            !_campaignHasRawSourceChanges && GetCampaignPackageDraftBindingProblem() is null;
        void Begin(string status)
        {
            lock (_campaignFieldGate)
            { _campaignSaveBusy = true; _campaignFieldStatus = Status = status; RaiseCampaignFieldsChanged(); }
        }
        bool Publish(string text)
        {
            lock (_campaignFieldGate)
            {
                if (!Current()) return false;
                SourceChanging?.Invoke();
                if (!Current()) return false;
                var parsed = WccProjectFactory.CreateFromCampaignMain(source.Path, text);
                binding.Project.Campaign = parsed.Campaign;
                binding.Project.CampaignRawTextSnapshot = text;
                foreach (var created in edit.SourceCreates.Where(created => created.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) &&
                             created.Path.Replace('\\', '/').Contains("/scenarios/", StringComparison.Ordinal)))
                {
                    if (ScenarioImporter.ImportAllFromText(created.Text).Count == 0 || binding.Project.ProjectScenarioFiles.Any(asset =>
                            asset.SourcePath is { } path && SamePresentationPath(path, created.Path))) continue;
                    var asset = WccProjectEditor.AddScenarioFileToProject(binding.Project, created.Path, created.Text);
                    ScenarioFiles.Add(new ProjectScenarioFileVm(asset));
                }
                _campaignHasRawSourceChanges = false;
                ClearCampaignPackageEditDraft();
                _campaignDraftBinding = null;
                _campaignProjection = null; _campaignStructureInventory = null; _campaignMenuOwner = null; _campaignRoutes.Clear();
                _campaignReviewSummary = "Managed package loading and chapter changes checked in the selected and newly added contexts.";
                if (_project?.Campaign is not null) RunUiSync(RefreshLegacyCampaignFields);
                _campaignFieldStatus = Status = "Refreshing the saved package…";
                RaiseCampaignFieldsChanged();
                CampaignFieldsSaved?.Invoke(this, EventArgs.Empty);
                return ReferenceEquals(binding.Project, _project) && _project.CampaignRawTextSnapshot == text;
            }
        }
        void End()
        {
            lock (_campaignFieldGate)
            {
                _campaignSaveBusy = false;
                if (!HasCampaignSourceDrafts) { RevokeCampaignFieldAuthority(); StartCampaignFieldProjection(); }
                RaiseCampaignFieldsChanged();
            }
        }
        return new(state, new(source) { PackageEdit = edit }, Current, _campaignFieldSession.Invalidate,
            Begin, Publish, _ => { }, End);
    }
}
