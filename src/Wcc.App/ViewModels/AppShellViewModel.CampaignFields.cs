using System;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private WmlCampaignEvaluationPlan? _campaignFieldEvaluationPlan;
    private WmlStaticSource? _scenarioDifficultyCampaignMain;

    public async Task<bool> PrepareCampaignProjectExportAsync(CancellationToken cancellationToken = default)
    {
        var project = CampaignEditor.CurrentProject;
        var projectSession = ProjectSession;
        var scenario = ScenarioEditor.CurrentScenarioAsset;
        var scenarioSession = ScenarioSession;
        var unit = UnitEditor.CurrentUnitAsset;
        var unitSession = UnitSession;
        if (project is null) return false;
        if (!await SavePendingEditorChangesAsync(cancellationToken))
        {
            // Global Save also refuses identity drift and reentrant new edits.
            // Those refusals must not bypass export's visible failure message.
            Status = (string.IsNullOrWhiteSpace(Status) ? "The pending edits could not be saved." : Status) +
                " Reopen project export after resolving the pending edits.";
            return false;
        }
        if (!ReferenceEquals(project, CampaignEditor.CurrentProject) || projectSession != ProjectSession ||
            !ReferenceEquals(scenario, ScenarioEditor.CurrentScenarioAsset) || scenarioSession != ScenarioSession ||
            !ReferenceEquals(unit, UnitEditor.CurrentUnitAsset) || unitSession != UnitSession ||
            CampaignEditor.HasCampaignSourceDrafts || CampaignEditor.IsCampaignFieldSaveBusy ||
            CampaignEditor.HasCampaignSourceChanges || ScenarioEditor.HasWccInlineDrafts ||
            ScenarioEditor.IsWccInlineSaveBusy || ScenarioEditor.HasWccMusicScheduleDrafts || ScenarioEditor.HasWccSourceRecipeDrafts ||
            _campaignEditorDirty || _scenarioEditorDirty || _unitEditorDirty || _libraryUnitEditorDirty)
        {
            Status = "The project or editing context changed. Reopen project export.";
            return false;
        }
        return CampaignEditor.CanExportCurrentCampaignSource();
    }

    private void ConfigureCampaignFieldHost()
    {
        using var projectionBatch = CampaignEditor.DeferCampaignFieldProjection();
        CampaignEditor.CampaignPresetSharedRawCapability = CanEditSavedCampaignPresetRaw;
        CampaignEditor.CampaignPresetSharedRawFactory = CreateSavedCampaignPresetRawDocument;
        ScenarioEditor.ConfigureWccCampaignPresetHost(CampaignEditor);
        var profile = ResolvePinnedProjectSourceResourceProfile();
        CampaignEditor.ConfigureCampaignPresetPreviews(profile?.InstallPath);
        CampaignEditor.ConfigureCampaignAuthoring(_currentProjectMetadata?.Authoring);
        var mainPath = CampaignEditor.CurrentProject?.SourcePath;
        var mainText = CampaignEditor.GetCurrentMainSourceRawWml();
        if (string.IsNullOrWhiteSpace(mainPath) || mainText is null)
            _scenarioDifficultyCampaignMain = null;
        else if (_scenarioDifficultyCampaignMain?.Path != System.IO.Path.GetFullPath(mainPath) ||
                 _scenarioDifficultyCampaignMain.Text != mainText)
            _scenarioDifficultyCampaignMain = new WmlStaticSource(mainPath, mainText);
        // Source text and the plan are checked together by the difficulty planner.
        // Retain the immutable main revision across row refreshes; never read disk here.
        var selectedAuthoring = _scenarioDifficultyCampaignMain is { } root
            ? global::Wcc.Core.Campaigns.WccCampaignChapterAuthoringPlanner.ResolveSelectedAuthoring(_currentProjectMetadata?.Authoring, root, _campaignFieldEvaluationPlan?.CampaignDefine, CampaignEditor.CampaignEntryCatalog?.Capture.Sources)
            : _currentProjectMetadata?.Authoring;
        ScenarioEditor.ConfigureWccDifficultyFields(_scenarioDifficultyCampaignMain, selectedAuthoring);
        CampaignEditor.ConfigureCampaignFieldEditing(
            ProjectSession,
            LibrarySnapshot,
            LibrarySnapshot is { } snapshot ? FindCurrentProjectRevisionId(snapshot) : null,
            _campaignFieldEvaluationPlan,
            _currentProjectMetadata?.ProjectId.Value.ToString() ?? ProjectSession.RootPath,
            profile?.AddonsPath,
            BuildWesnothSourceResourceProfileIdentity(profile));
        CampaignEditor.PrepareCampaignPackageAuthoringAsync = () => ConfirmSourceNavigationAsync();
        CampaignEditor.RefreshCampaignDependencyBatchSourceAsync = (token, folder) => ChangeCampaignDependencySourceAsync(folder, true, token);
        ConfigureCampaignDependencySourceHost();
    }

    private CurrentSourceWmlView? BuildCampaignSourceView()
    {
        var view = BuildEditorSourceView("Campaign source WML", CampaignEditor.LoadedPath,
            CampaignEditor.GetCurrentMainSourceRawWml());
        if (view is null) return null;
        var draftStatus = CampaignEditor.IsCampaignFieldSaveBusy
            ? " Save is in progress."
            : CampaignEditor.HasCampaignSourceDrafts
                ? " Pending campaign field changes remain in the draft review."
                : string.Empty;
        return view with
        {
            DescriptionText = "Read-only physical campaign source WML, including applied RAW edits." + draftStatus
        };
    }

}
