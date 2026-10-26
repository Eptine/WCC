using System;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private Task? _inlineNavigationSaveTask;
    private Action? _pendingInlineNavigation;
    private Task<bool>? _globalSaveTask;

    public Task<bool> SavePendingEditorChangesAsync(CancellationToken cancellationToken = default)
    {
        if (CampaignEditor.IsCampaignDependencySourceRefreshing)
        { Status = "Wait for the dependency source refresh before saving."; return Task.FromResult(false); }
        if (_wmlDefinitionRecovery is { } recovery)
        { Status = DescribeWmlDefinitionResult(recovery); SetGlobalSaveError(true); return Task.FromResult(false); }
        if (_wmlDefinitionRefreshTask is { IsCompleted: false })
        { Status = "The shared definition was saved. Wait for its campaign previews to refresh."; return Task.FromResult(false); }
        if (_globalSaveTask is { IsCompleted: false } task) return task.WaitAsync(cancellationToken);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _globalSaveTask = completion.Task;
        _ = CompleteGlobalSaveAsync(completion, cancellationToken);
        return completion.Task;
    }

    private async Task CompleteGlobalSaveAsync(TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        // Register the shared task before busy notifications can reenter Save.
        var identity = CapturePendingEditorIdentity();
        var succeeded = false;
        var measurements = new WccSavePerformanceCollector();
        _savePerformanceCollector = measurements;
        var campaignDiagnostics = CampaignEditor.LastCampaignBatchSaveDiagnostics;
        var scenarioDiagnostics = ScenarioEditor.WccFieldEditSession.LastBatchSaveDiagnostics;
        var collectedDiagnostics = _collectedSaveCoordinator.LastDiagnostics;
        try
        {
            if (!CampaignEditor.CommitCampaignTextInputs(out var inputProblem))
            { Status = inputProblem; return; }
            if (!identity.IsCurrent(this)) return;
            _collectedSaveCompletionMessage = null;
            SetGlobalSaveError(false);
            SetGlobalSaveActivity(true, "Checking and saving pending changes…");
            await Task.Yield();
            if (!identity.IsCurrent(this)) return;
            var saved = await MeasureSavePhaseAsync("Check, write and publish", () => SavePendingEditorChangesCoreAsync(cancellationToken));
            if (saved && identity.IsCurrent(this))
            {
                SetGlobalSaveActivity(true, "Refreshing source and editor previews…");
                // A completed save must finish publishing its committed sources.
                // Cancelling now cannot turn saved files into reported pending edits.
                await MeasureSavePhaseAsync("Final preview wait", async () =>
                {
                    if (_libraryRefreshTask is { } refresh) await refresh;
                    await CampaignEditor.WaitForCampaignFieldProjectionAsync();
                    await ScenarioEditor.WaitForScenarioUiLoadingAsync();
                });
            }
            succeeded = saved && identity.IsCurrent(this) && !HasPendingEditorChanges;
            if (succeeded) Status = _collectedSaveCompletionMessage ?? "Pending editor changes saved.";
        }
        catch (OperationCanceledException)
        { Status = "Saving was cancelled. Pending edits remain open."; }
        catch (Exception exception)
        { Status = "Could not finish saving the pending edits: " + exception.Message; }
        finally
        {
            if (!ReferenceEquals(campaignDiagnostics, CampaignEditor.LastCampaignBatchSaveDiagnostics))
                foreach (var operation in CampaignEditor.LastCampaignBatchSaveDiagnostics) measurements.AddSourceOperation(operation);
            if (!ReferenceEquals(scenarioDiagnostics, ScenarioEditor.WccFieldEditSession.LastBatchSaveDiagnostics))
                foreach (var operation in ScenarioEditor.WccFieldEditSession.LastBatchSaveDiagnostics) measurements.AddSourceOperation(operation);
            if (!ReferenceEquals(collectedDiagnostics, _collectedSaveCoordinator.LastDiagnostics))
                measurements.AddSourceOperation(_collectedSaveCoordinator.LastDiagnostics);
            LastSavePerformance = measurements.Complete(succeeded);
            _savePerformanceCollector = null;
            OnPropertyChanged(nameof(LastSavePerformance));
            OnPropertyChanged(nameof(ShellStatusDetail));
            SetGlobalSaveError(!succeeded);
            SetGlobalSaveActivity(false, string.Empty);
            completion.TrySetResult(succeeded && identity.IsCurrent(this) && !HasPendingEditorChanges);
        }
    }

    private async Task<bool> SavePendingEditorChangesCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_collectedSaveCoordinator.Recovery is { } recovery) { Status = DescribeCollectedSaveResult(recovery); return false; }
            if (HasPackageScenarioRawDraft) return await SavePackageScenarioRawDraftAsync(cancellationToken);
            if (NeedsCombinedSourceSave) return await SaveCollectedSourceDraftsAsync(cancellationToken);
            if (CampaignEditor.IsCampaignFieldSaveBusy)
            {
                await CampaignEditor.WaitForCampaignFieldSaveAsync(cancellationToken);
                if (CampaignEditor.HasCampaignSourceDrafts)
                {
                    Status = CampaignEditor.Status;
                    return false;
                }
            }
            if (CampaignEditor.HasCampaignSourceDrafts)
            {
                var result = await CampaignEditor.SaveCampaignFieldDraftsAsync(cancellationToken);
                if (!result.IsSuccess)
                {
                    Status = result.Diagnostic;
                    return false;
                }
            }
            if (ScenarioEditor.HasWccMusicScheduleDrafts)
            {
                Status = "Music or schedule changes appeared during Save. Your drafts remain open.";
                return false;
            }
            if (ScenarioEditor.IsWccInlineSaveBusy)
            {
                await ScenarioEditor.WaitForWccInlineSaveAsync(cancellationToken);
                if (ScenarioEditor.HasWccInlineDrafts)
                {
                    Status = ScenarioEditor.Status;
                    return false;
                }
            }
            if (ScenarioEditor.HasWccInlineDrafts)
            {
                var result = await ScenarioEditor.SaveWccInlineDraftsAsync(cancellationToken);
                if (!result.IsSuccess)
                {
                    Status = result.Diagnostic;
                    return false;
                }
            }
            if (!SaveCurrentCampaignIfDirty() || !SaveCurrentScenarioIfDirty()) return false;
            if (!await SaveCurrentProjectUnitIfDirtyAsync(cancellationToken)) return false;
            // Publication can redraft an editor already visited by this Save.
            // Retain that draft instead of writing the earlier editor again.
            if (_campaignEditorDirty || _scenarioEditorDirty || CampaignEditor.HasCampaignSourceChanges ||
                CampaignEditor.HasCampaignSourceDrafts || CampaignEditor.IsCampaignFieldSaveBusy ||
                ScenarioEditor.HasWccInlineDrafts || ScenarioEditor.HasWccMusicScheduleDrafts || ScenarioEditor.IsWccInlineSaveBusy ||
                ScenarioEditor.HasWccSourceRecipeDrafts)
            { Status = "Campaign or scenario changes appeared during Save. Your new drafts remain open."; return false; }
            return SaveCurrentUnitIfDirty(refreshCatalogEntries: true) && SaveLibraryUnitIfDirty();
        }
        catch (OperationCanceledException)
        {
            Status = "Saving was cancelled. Pending edits remain open.";
            return false;
        }
        catch (Exception exception)
        {
            Status = "Could not save the pending edits: " + exception.Message;
            return false;
        }
    }

    public async Task SaveCurrentContextAsync(CancellationToken cancellationToken = default)
    {
        await SavePendingEditorChangesAsync(cancellationToken);
        OnPropertyChanged(nameof(CanSaveCurrentContext));
    }

    private bool TrySaveBeforeInlineNavigation(Action continuation)
    {
        if (_isDiscardingPendingEditorChanges) return false;
        if (!HasPendingEditorChanges && !IsGlobalSaveBusy && !CampaignEditor.IsCampaignFieldSaveBusy &&
            !ScenarioEditor.IsWccInlineSaveBusy) return true;

        // Keep the clicked destination, rather than reading a picker again after
        // an asynchronous save. A later click replaces this pending destination.
        _pendingInlineNavigation = continuation;
        if (_inlineNavigationSaveTask is null || _inlineNavigationSaveTask.IsCompleted)
            _inlineNavigationSaveTask = SaveThenNavigateAsync();
        return false;
    }

    private async Task SaveThenNavigateAsync()
    {
        var identity = CapturePendingEditorIdentity();
        // Let the binding restore its old selection before a fast failed review
        // or a continuation can run. Also establish the task before clearing it.
        await Task.Yield();
        try
        {
            if (!await ConfirmSourceNavigationAsync() || !identity.IsCurrent(this) || HasPendingEditorChanges ||
                IsGlobalSaveBusy || CampaignEditor.IsCampaignFieldSaveBusy || ScenarioEditor.IsWccInlineSaveBusy) return;
            var continuation = _pendingInlineNavigation;
            _pendingInlineNavigation = null;
            continuation?.Invoke();
        }
        catch (Exception exception)
        {
            Status = "Could not finish the requested navigation: " + exception.Message;
        }
        finally
        {
            _pendingInlineNavigation = null;
            _inlineNavigationSaveTask = null;
        }
    }
}
