using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Library;
using Wcc.Core.Campaigns;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private WeakReference<WccDependencyAudioScope>? _scenarioCreationAudioScope;

    private WccDependencyAudioScope? FindScenarioCreationAudioScope()
    {
        if (LibrarySnapshot is not { } snapshot || FindCurrentProjectRevisionId(snapshot) is not { } root ||
            CampaignEditor.SelectedCampaignEntry?.Plan is not { } plan) return null;
        var active = ScenarioEditor.CaptureCurrentDependencyAudioScope();
        if (active?.Matches(snapshot, root, plan) == true) return active;
        if (_scenarioCreationAudioScope?.TryGetTarget(out var cached) == true && cached.Matches(snapshot, root, plan)) return cached;
        _scenarioCreationAudioScope = null;
        if (CampaignEditor.CampaignEntryCatalog is not { } catalog || CampaignEditor.SelectedCampaignEntry is not { } entry ||
            !_campaignEntryMemberships.TryGetValue(entry.Identity, out var membership) || !ReferenceEquals(membership.Entry, entry)) return null;
        var selected = membership.Contexts.FirstOrDefault(item => item.IsComplete &&
            item.Context.SemanticKey == ScenarioEditor.EvaluationContext.SemanticKey) ?? membership.Contexts.FirstOrDefault(item => item.IsComplete);
        if (selected is null) return null;
        bool Current() => ReferenceEquals(snapshot, LibrarySnapshot) && ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) &&
            ReferenceEquals(entry, CampaignEditor.SelectedCampaignEntry);
        var scope = new WccDependencyAudioScope(snapshot, WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root), root,
            catalog.Capture, WccCapturedImageResourceContext.Create(catalog.Capture, selected.Evaluation, selected.Projection), plan, Current);
        _scenarioCreationAudioScope = new(scope);
        return scope;
    }

    /// <summary>A new empty chapter has no selected scenario to lend a gameplay
    /// receipt. Prepare one preview context through the ordinary captured evaluator,
    /// off the UI thread; Save still performs its normal fresh source review.</summary>
    public async Task<ScenarioCreationViewModel?> CreateScenarioWizardModelAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCreateScenario) return null;
        if (FindScenarioCreationAudioScope() is not null) return CreateScenarioWizardModel();
        var snapshot = LibrarySnapshot;
        var catalog = CampaignEditor.CampaignEntryCatalog;
        var entry = CampaignEditor.SelectedCampaignEntry;
        var project = CampaignEditor.CurrentProject;
        if (snapshot is null || catalog is null || entry?.Plan is not { } plan ||
            FindCurrentProjectRevisionId(snapshot) is not { } root || catalog.Capture.EntrySource is not { } main ||
            !catalog.Capture.IsCaptureComplete || !WmlStaticSingleplayerEnvironment.TryCreate(catalog.Capture.CoreVersion, out var environment))
            return CreateScenarioWizardModel();
        var context = plan.Contexts.FirstOrDefault(item => item.SemanticKey == ScenarioEditor.EvaluationContext.SemanticKey)
            ?? plan.Contexts.FirstOrDefault(item => plan.Difficulties.Any(row => row.IsDefault && row.Define == item.DifficultyDefine))
            ?? plan.Contexts.FirstOrDefault();
        if (context is null) return CreateScenarioWizardModel();
        bool Current() => ReferenceEquals(snapshot, LibrarySnapshot) && ReferenceEquals(project, CampaignEditor.CurrentProject) &&
            ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) && ReferenceEquals(entry, CampaignEditor.SelectedCampaignEntry) &&
            FindCurrentProjectRevisionId(snapshot) == root;
        WccDependencyAudioScope? preparedScope = null;
        _isScenarioCreationBusy = true;
        OnPropertyChanged(nameof(IsScenarioCreationBusy)); RaiseShellPresentation();
        Status = "Preparing campaign audio choices...";
        try
        {
            var resources = await Task.Run(() =>
            {
                var projected = WccCampaignScenarioCatalog.ProjectContext(catalog.Capture, entry, context, cancellationToken);
                return WccCapturedImageResourceContext.Create(catalog.Capture, projected.Evaluation, projected.Projection);
            }, cancellationToken);
            if (!Current() || cancellationToken.IsCancellationRequested) return null;
            preparedScope = new(snapshot, WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root), root,
                catalog.Capture, resources, plan, Current);
            _scenarioCreationAudioScope = new(preparedScope);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { if (Current()) Status = "Campaign audio choices are unavailable: " + error.Message; }
        finally
        {
            _isScenarioCreationBusy = false;
            OnPropertyChanged(nameof(IsScenarioCreationBusy)); RaiseShellPresentation();
        }
        return Current() && CanCreateScenario ? CampaignEditor.BeginCampaignScenarioCreation(preparedScope) : null;
    }
}