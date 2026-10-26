using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private Task<bool>? _scenarioCreationTask;
    private bool _isScenarioCreationBusy;
    public bool IsScenarioCreationBusy => _isScenarioCreationBusy;
    public bool CanCreateScenario => _scenarioCreationTask is not { IsCompleted: false } && CanInteractWithEditors &&
        IsProjectScenarioWorkspace && ProjectSession.CanWrite && CampaignEditor.CanCreateCampaignScenario;
    public string ScenarioCreationGuidance => !ProjectSession.CanWrite ? "Open a writable WCC-created campaign to create a scenario." :
        CampaignEditor.CampaignScenarioCreationProblem ?? "Create an empty scaffold or a guided starter scenario in this campaign.";
    public ScenarioCreationViewModel? CreateScenarioWizardModel() => CanCreateScenario
        ? CampaignEditor.BeginCampaignScenarioCreation(FindScenarioCreationAudioScope()) : null;

    public Task<bool> CreateScenarioAsync(ScenarioCreationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanCreateScenario || !request.IsCurrent())
        { Status = "The campaign source or context changed. Reopen the scenario wizard; current drafts remain."; return Task.FromResult(false); }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scenarioCreationTask = completion.Task;
        _ = CompleteScenarioCreationAsync(request, completion, cancellationToken);
        return completion.Task;
    }
    private async Task CompleteScenarioCreationAsync(ScenarioCreationRequest request, TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken)
    {
        var succeeded = false;
        var project = CampaignEditor.CurrentProject;
        var session = ProjectSession;
        var installation = _activeWesnothInstallation;
        var profileIdentity = BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile());
        bool CurrentHost() => ReferenceEquals(_scenarioCreationTask, completion.Task) && ReferenceEquals(project, CampaignEditor.CurrentProject) && session == ProjectSession;
        bool SavedContext() => CurrentHost() && profileIdentity == BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile()) &&
            (request.IsSavedContext?.Invoke() ?? request.IsSameContext());
        OnPropertyChanged(nameof(CanCreateScenario));
        try
        {
            if (!request.IsCurrent() || !await ConfirmSourceNavigationAsync(cancellationToken)) return;
            await CampaignEditor.WaitForCampaignFieldProjectionAsync(cancellationToken);
            // Discard rebinds both editors. Their independent publications must
            // settle before this operation evaluates the existing busy guard.
            await ScenarioEditor.WaitForScenarioUiLoadingAsync(cancellationToken);
            bool SameContext() => project is not null && ReferenceEquals(project, CampaignEditor.CurrentProject) &&
                session == ProjectSession && ReferenceEquals(installation, _activeWesnothInstallation) && request.IsSameContext();
            if (!SameContext() || HasPendingEditorChanges || !CanInteractWithEditors)
            { Status = "The editing context changed. Reopen the scenario wizard; current drafts remain."; return; }
            _isScenarioCreationBusy = true;
            OnPropertyChanged(nameof(IsScenarioCreationBusy)); RaiseShellPresentation();
            if (!SameContext() || HasPendingEditorChanges ||
                !CampaignEditor.StageCampaignScenarioCreation(request.Spec, request.SetFirstScenario))
            { Status = CampaignEditor.Status ?? "The new scenario could not be staged."; return; }
            var relativePath = CampaignEditor.CampaignScenarioCreationRelativePath
                ?? throw new InvalidOperationException("The staged scenario has no captured destination.");
            var path = Path.Combine(session.RootPath!, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!SameContext() || !await SavePendingEditorChangesAsync(cancellationToken)) return;
            // End-of-Save projections can start another membership publication.
            // Settle that existing work before locating the newly created document.
            await WaitForCampaignEntriesAsync();
            // Saved files remain on disk if a callback changes the host or introduces a new draft.
            if (!SavedContext()) { succeeded = true; return; }
            if (HasPendingEditorChanges)
            { Status = "Scenario saved at " + path + ". Open it after finishing the current edits."; succeeded = true; return; }
            var text = await File.ReadAllTextAsync(path);
            if (!SavedContext()) { succeeded = true; return; }
            if (HasPendingEditorChanges)
            { Status = "Scenario saved at " + path + ". Open it after finishing the current edits."; succeeded = true; return; }
            var asset = CampaignEditor.AttachCreatedScenario(project!, path, text, () => SavedContext() && !HasPendingEditorChanges);
            if (!SavedContext()) { succeeded = true; return; }
            if (asset is null || HasPendingEditorChanges)
            { if (SavedContext()) Status = "Scenario saved at " + path + ". Open its file to continue."; succeeded = true; return; }
            if (CampaignEditor.RequiresSelectedCampaignAdapter || CampaignEditor.RequiresManagedChapterScenarioNavigation)
            {
                // The ordinary current-row lookup intentionally matches the old
                // editor's document. A newly created file instead needs its exact
                // fresh membership, never the previously selected document ID.
                var document = asset.Documents.Count == 1 ? asset.Documents[0] : null;
                var matches = CampaignEditor.ScenarioFiles.Where(row => ReferenceEquals(row.Asset, asset) &&
                    row.CampaignEntry?.Identity == CampaignEditor.SelectedCampaignEntry?.Identity && document is not null &&
                    row.ScenarioDocumentInternalId == document.InternalId && row.Membership?.OwnerSource is { } source &&
                    PathsEqual(source.Path, path) && source.Text == text).Take(2).ToArray();
                if (matches.Length != 1 || !await OpenCampaignScenarioRowAsync(matches[0], cancellationToken))
                { if (SavedContext()) Status = "Scenario saved at " + path + ". Open its file to continue."; succeeded = true; return; }
            }
            else OpenScenarioAssetInProjectScenarioTab(asset);
            await ScenarioEditor.WaitForScenarioUiLoadingAsync();
            succeeded = true;
            if (SavedContext() && ReferenceEquals(asset, ScenarioEditor.CurrentScenarioAsset))
                Status = request.Spec.Mode == global::Wcc.Core.Scenarios.WccScenarioCreationMode.Empty
                    ? "Empty scenario created. Add a map and sides before playing it."
                    : "Scenario created. Its map, sides and initial settings are ready to edit.";
        }
        catch (OperationCanceledException) { if (CurrentHost()) Status = "Scenario creation cancelled. Any staged changes remain in global Save."; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (CurrentHost()) Status = "Could not finish scenario creation: " + failure.Message + ". Any staged changes remain in global Save."; }
        finally
        {
            _isScenarioCreationBusy = false;
            OnPropertyChanged(nameof(IsScenarioCreationBusy));
            completion.TrySetResult(succeeded);
            RaiseShellPresentation();
        }
    }
}
