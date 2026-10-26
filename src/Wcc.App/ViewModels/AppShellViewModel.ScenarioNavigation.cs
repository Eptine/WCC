using System;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private ProjectScenarioFileVm? FindCurrentProjectScenarioRow(ScenarioFileAsset? asset)
    {
        return CampaignEditor.WithCampaignScenarioRows(rows =>
        {
            if (asset is null || CampaignEditor.CurrentProject is not { } project ||
                !project.ProjectScenarioFiles.Any(item => ReferenceEquals(item, asset))) return null;
            if (_activeCampaignScenarioRow is { } active && rows.Contains(active) &&
                ReferenceEquals(active.Asset, asset) && (active.IsUnresolvedCampaignMembership ||
                    active.CampaignEntry?.Identity == CampaignEditor.SelectedCampaignEntry?.Identity))
                return active;
            return rows.FirstOrDefault(item => ReferenceEquals(item.Asset, asset) &&
                       item.CampaignEntry?.Identity == CampaignEditor.SelectedCampaignEntry?.Identity &&
                       (item.ScenarioDocumentInternalId is null || item.ScenarioDocumentInternalId == ScenarioEditor.SelectedScenario?.Document.InternalId))
                ?? rows.FirstOrDefault(item => ReferenceEquals(item.Asset, asset) && item.CampaignEntry is null);
        });
    }

    private void SynchronizeProjectScenarioSelection(ScenarioFileAsset asset)
    {
        if (FindCurrentProjectScenarioRow(asset) is not { } row) return;
        CampaignEditor.SelectedScenarioFile = row;
        // A selection callback can replace the project or its row collection.
        if (ReferenceEquals(asset, ScenarioEditor.CurrentScenarioAsset) &&
            ReferenceEquals(row, FindCurrentProjectScenarioRow(asset)))
            SelectedCampaignScenarioFile = row;
    }

    private bool TryShowCurrentProjectScenario(ScenarioFileAsset asset)
    {
        if (ScenarioSession.Scope != WccEditorSessionScope.Project ||
            ScenarioSession.AccessMode != ProjectSession.AccessMode ||
            !PathsEqual(ScenarioSession.RootPath, ProjectSession.RootPath) ||
            (!PathsEqual(ScenarioSession.AssetRootPath, ProjectSession.AssetRootPath) &&
                !string.Equals(ScenarioSession.AssetRootPath, ProjectSession.AssetRootPath, StringComparison.Ordinal)) ||
            !ScenarioEditor.IsShowingCurrentScenarioSource(asset, ScenarioSession)) return false;
        _showLooseScenarioWorkspace = false;
        SynchronizeProjectScenarioSelection(asset);
        RaiseScenarioWorkspaceStateChanged();
        SelectedTabIndex = 1;
        return true;
    }
}
