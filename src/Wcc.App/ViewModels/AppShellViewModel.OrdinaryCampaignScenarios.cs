using System;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private void RestoreOrdinaryCampaignScenarioRows(WccProject project,
        WccCampaignEntryCatalogProjection catalog, Func<bool> isCurrent)
    {
        bool Current() => isCurrent() && ReferenceEquals(project, CampaignEditor.CurrentProject) &&
            ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) && catalog.IsComplete && catalog.Entries.Count == 1 &&
            !CampaignEditor.RequiresSelectedCampaignAdapter && !CampaignEditor.RequiresManagedChapterScenarioNavigation &&
            project.SourcePath is { } sourcePath && catalog.Capture.EntrySource is { } root && PathsEqual(root.Path, sourcePath) &&
            root.Text == CampaignEditor.GetCurrentMainSourceRawWml();
        if (!Current()) return;
        using var previewRefresh = ScenarioEditor.DeferPreviewRefresh();
        // An incomplete menu can temporarily require selected-entry inspection.
        // Once its dependencies resolve, return its physical rows and source plan
        // to the existing ordinary loader rather than retaining that inspection.
        if (!HasPendingEditorChanges)
        {
            ConfigureScenarioEvaluationPlan(project);
            if (!Current()) return;
        }
        var currentAsset = ScenarioEditor.CurrentScenarioAsset;
        var currentDocument = ScenarioEditor.SelectedScenario?.Document;
        var preferredAsset = currentAsset ?? CampaignEditor.SelectedScenarioFile?.Asset ??
            SelectedCampaignScenarioFile?.Asset ?? _activeCampaignScenarioRow?.Asset;
        var rows = project.ProjectScenarioFiles.Select(asset => new ProjectScenarioFileVm(asset)
        {
            ScenarioDocumentInternalId = ReferenceEquals(asset, currentAsset) && currentDocument is not null &&
                asset.Documents.Any(document => ReferenceEquals(document, currentDocument)) ? currentDocument.InternalId : null
        }).ToArray();
        var selected = rows.FirstOrDefault(row => ReferenceEquals(row.Asset, preferredAsset));
        if (selected is null && !IsGlobalSaveBusy)
        {
            var firstId = catalog.Entries[0].FirstScenarioId;
            selected = rows.FirstOrDefault(row => row.Asset.Documents.Any(document => document.Scenario.Id == firstId))
                ?? rows.FirstOrDefault();
        }
        var previousCampaignSelection = CampaignEditor.SelectedScenarioFile;
        var previousShellSelection = SelectedCampaignScenarioFile;
        using (BeginCampaignEntrySelectionPublication())
        {
            _activeCampaignScenarioRow = null;
            if (!CampaignEditor.ReplaceCampaignScenarioRows(rows, Current)) return;
            // A collection binding may briefly clear its selection. A different
            // nonnull row selected by a callback still owns the newer intent.
            if (!Current() || CampaignEditor.SelectedScenarioFile is { } campaignSelection &&
                    !ReferenceEquals(campaignSelection, previousCampaignSelection) && !ReferenceEquals(campaignSelection, selected) ||
                SelectedCampaignScenarioFile is { } shellSelection && !ReferenceEquals(shellSelection, previousShellSelection) &&
                    !ReferenceEquals(shellSelection, selected)) return;
            var shellSelectionAfterRows = SelectedCampaignScenarioFile;
            CampaignEditor.SelectedScenarioFile = selected;
            if (!Current() || !ReferenceEquals(CampaignEditor.SelectedScenarioFile, selected) ||
                !ReferenceEquals(SelectedCampaignScenarioFile, shellSelectionAfterRows)) return;

            SelectedCampaignScenarioFile = selected;
            bool CurrentSelection() => Current() && ReferenceEquals(CampaignEditor.SelectedScenarioFile, selected) &&
                ReferenceEquals(SelectedCampaignScenarioFile, selected);
            if (!CurrentSelection()) return;
            ConfigureCampaignEntryInspection();
            if (!CurrentSelection() || HasPendingEditorChanges || selected is null) return;
            LoadScenarioDocument(selected.Asset, CreateProjectEditorSession("Project scenario: " + selected.DisplayName));
            if (!CurrentSelection() || !ReferenceEquals(selected.Asset, ScenarioEditor.CurrentScenarioAsset)) return;
            RaiseScenarioWorkspaceStateChanged();
            if (!CurrentSelection()) return;
            RefreshProjectUnitWorkspaceEntries();
            if (CurrentSelection()) Status = "Campaign source context refreshed.";
        }
    }
}
