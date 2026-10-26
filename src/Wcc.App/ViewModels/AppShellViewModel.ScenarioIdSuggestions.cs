using System;
using System.Linq;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private void InitializeScenarioIdSuggestions()
    {
        ScenarioEditor.SetScenarioIdSuggestionProvider(() =>
            IsProjectScenarioWorkspace && ScenarioEditor.CurrentScenarioAsset is { } asset &&
            CampaignEditor.CurrentProject?.ProjectScenarioFiles.Any(current => ReferenceEquals(current, asset)) == true
                ? CampaignEditor.AttachedScenarioIdSuggestions : Array.Empty<string>());
        CampaignEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(CampaignWindowViewModel.AttachedScenarioIdSuggestions) or nameof(CampaignWindowViewModel.CurrentProject))
                ScenarioEditor.RefreshScenarioIdSuggestions();
        };
        ScenarioEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ScenarioWindowViewModel.CurrentScenarioAsset) or nameof(ScenarioWindowViewModel.SelectedScenario) or
                nameof(ScenarioWindowViewModel.SelectedEvaluationContext) or nameof(ScenarioWindowViewModel.LoadedPath))
                ScenarioEditor.RefreshScenarioIdSuggestions();
        };
        ScenarioEditor.ScenarioSourceSaved += (_, _) => CampaignEditor.RefreshAttachedScenarioIdSuggestions();
        CampaignEditor.CampaignFieldsSaved += (_, _) => CampaignEditor.RefreshAttachedScenarioIdSuggestions();
        PropertyChanged += (_, args) =>
        { if (args.PropertyName == nameof(IsProjectScenarioWorkspace)) ScenarioEditor.RefreshScenarioIdSuggestions(); };
    }
}
