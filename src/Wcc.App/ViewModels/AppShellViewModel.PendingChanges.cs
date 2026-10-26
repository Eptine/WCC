using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private Task<bool>? _pendingNavigationDecisionTask;
    private bool _isPendingNavigationDecision;
    private bool _isDiscardingPendingEditorChanges;
    private bool _isGlobalSaveBusy;
    private bool _hasGlobalSaveError;
    private string _globalSaveStatus = string.Empty;

    public Func<PendingNavigationRequest, Task<PendingNavigationDecision>>? PendingNavigationDecisionAsync { get; set; }
    public bool IsPendingNavigationDecision => _isPendingNavigationDecision;
    public bool IsGlobalSaveBusy => _isGlobalSaveBusy;
    public string GlobalSaveStatus => _globalSaveStatus;
    public bool HasGlobalSaveError => _hasGlobalSaveError;
    public bool HasPendingEditorChanges => CampaignEditor.IsCampaignDependencySourceRefreshing || HasPackageScenarioRawDraft || _campaignEditorDirty || _scenarioEditorDirty || _unitEditorDirty ||
        _libraryUnitEditorDirty || CampaignEditor.HasCampaignSourceDrafts || CampaignEditor.HasCampaignSourceChanges ||
        ScenarioEditor.HasWccInlineDrafts || ScenarioEditor.HasWccMusicScheduleDrafts || ScenarioEditor.HasWccSourceRecipeDrafts;

    public IReadOnlyList<PendingEditorChange> PendingEditorChanges
    {
        get
        {
            var changes = new List<PendingEditorChange>();
            if (_packageScenarioRawDraft is { } packageRaw)
                changes.Add(new("Scenario", "Package RAW source", packageRaw.Draft.SourcePath));
            changes.AddRange(CampaignEditor.CampaignFieldDrafts.Select(draft =>
                new PendingEditorChange("Campaign", draft.Label, DraftDetail(draft))));
            changes.AddRange(CampaignEditor.CampaignStructurePendingChanges);
            changes.AddRange(CampaignEditor.CampaignCreditsPendingChanges);
            changes.AddRange(CampaignEditor.CampaignPresetPendingChanges);
            changes.AddRange(CampaignEditor.CampaignScenarioCreationPendingChanges);
            changes.AddRange(CampaignEditor.CampaignUnitTypeCreationPendingChanges);
            changes.AddRange(CampaignEditor.CampaignPackagePendingChanges);
            if (_campaignEditorDirty || CampaignEditor.HasCampaignSourceChanges)
                changes.Add(new("Campaign", "Main source and structured changes", CampaignEditor.LoadedPath ?? ""));
            changes.AddRange(ScenarioEditor.WccInlineDrafts.Select(draft =>
                new PendingEditorChange("Scenario", draft.Label, DraftDetail(draft))));
            if (ScenarioEditor.HasWccMusicScheduleDrafts)
                changes.Add(new("Scenario", "Music and schedule", "Pending playlist or time-of-day changes"));
            changes.AddRange(ScenarioEditor.WccSideFlagDrafts.Select(draft =>
                new PendingEditorChange("Scenario", draft.Label, draft.Summary)));
            changes.AddRange(ScenarioEditor.WccEventDrafts.Select(draft =>
                new PendingEditorChange("Scenario", draft.Label, draft.Summary)));
            changes.AddRange(ScenarioEditor.WccSideTopologyDrafts.Select(draft =>
                new PendingEditorChange("Scenario", draft.Label, draft.Summary)));
            changes.AddRange(ScenarioEditor.WccLeaderAdditionDrafts.Select(draft =>
                new PendingEditorChange("Scenario", draft.Label, draft.Summary)));
            changes.AddRange(ScenarioEditor.WccInitialUnitRows.Where(row => row.Spec is not null || row.Removed).Select(row =>
                new PendingEditorChange("Scenario", "Initial unit / " + row.Title, row.Summary)));
            changes.AddRange(ScenarioEditor.WccStoryPendingChanges.Select(draft =>
                new PendingEditorChange("Scenario", draft.Label, draft.Summary)));
            if (_scenarioEditorDirty)
                changes.Add(new("Scenario", "Source and structured changes", ScenarioEditor.LoadedPath ?? ""));
            if (_unitEditorDirty) changes.Add(new("Unit", "Source and structured changes", UnitEditor.LoadedPath ?? ""));
            if (_libraryUnitEditorDirty) changes.Add(new("Library unit", "Source and structured changes", LibraryUnitEditor.LoadedPath ?? ""));
            return changes.AsReadOnly();
        }
    }

    internal static string DraftDetail(WccScenarioInlineDraft draft) =>
        draft.Difficulty is { } difficulty ? difficulty.Summary :
        (draft.Route.Value ?? draft.Route.SourceValue ?? "Absent") + " → " +
        (draft.Operation == global::Wcc.Core.Wml.WmlFieldOperation.Remove ? "Remove local override" : draft.Text ?? "") +
        (draft.ValidationError is null ? "" : " — " + draft.ValidationError);

    private void SetGlobalSaveError(bool error)
    {
        _hasGlobalSaveError = error;
        OnPropertyChanged(nameof(HasGlobalSaveError));
    }

    public void RefreshPendingEditorChanges()
    {
        ScenarioEditor.SetExternalContextDraftPending(HasPackageScenarioRawDraft);
        OnPropertyChanged(nameof(HasPackageScenarioRawDraft));
        OnPropertyChanged(nameof(PendingPackageScenarioRawText));
        OnPropertyChanged(nameof(CanEditPackageScenarioRaw));
        OnPropertyChanged(nameof(IsPackageScenarioRawToolbarVisible));
        OnPropertyChanged(nameof(HasPendingEditorChanges));
        OnPropertyChanged(nameof(PendingEditorChanges));
        OnPropertyChanged(nameof(PendingChanges));
        QueueCampaignDependencySourceAvailabilityNotification();
    }

    private void SetGlobalSaveActivity(bool busy, string status)
    {
        _isGlobalSaveBusy = busy;
        _globalSaveStatus = status;
        OnPropertyChanged(nameof(IsGlobalSaveBusy));
        OnPropertyChanged(nameof(GlobalSaveStatus));
        RefreshPendingEditorChanges();
        OnPropertyChanged(nameof(CanSaveCurrentContext));
    }

    public Task<bool> ConfirmSourceNavigationAsync(CancellationToken cancellationToken = default)
    {
        if (_isDiscardingPendingEditorChanges) return Task.FromResult(false);
        if (!CampaignEditor.CommitCampaignTextInputs(out var inputProblem))
        { Status = inputProblem; return Task.FromResult(false); }
        if (_pendingNavigationDecisionTask is { IsCompleted: false } pending)
            return pending.WaitAsync(cancellationToken);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingNavigationDecisionTask = completion.Task;
        _ = CompleteNavigationDecisionAsync(completion, cancellationToken);
        return completion.Task;
    }

    private async Task CompleteNavigationDecisionAsync(TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        var accepted = false;
        var identity = CapturePendingEditorIdentity();
        await Task.Yield();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_globalSaveTask is { IsCompleted: false } saving)
            {
                if (!await saving.WaitAsync(cancellationToken) || !identity.IsCurrent(this))
                { return; }
            }
            else if (CampaignEditor.IsCampaignFieldSaveBusy || ScenarioEditor.IsWccInlineSaveBusy)
            {
                if (!await SavePendingEditorChangesAsync(cancellationToken) || !identity.IsCurrent(this))
                { return; }
            }
            if (!HasPendingEditorChanges) { accepted = true; return; }
            var before = CapturePendingEditorStamp();
            _isPendingNavigationDecision = true;
            OnPropertyChanged(nameof(IsPendingNavigationDecision));
            // Programmatic hosts retain the former safe save barrier. The real
            // window installs the one shared Save/Discard/Cancel prompt.
            var decision = PendingNavigationDecisionAsync is { } decide
                ? await decide(new(PendingEditorChanges)).WaitAsync(cancellationToken)
                : PendingNavigationDecision.Save;
            if (!identity.IsCurrent(this) || before != CapturePendingEditorStamp())
            { Status = "The source or pending changes changed. The requested navigation was cancelled."; return; }
            accepted = decision switch
            {
                PendingNavigationDecision.Save => await SavePendingEditorChangesAsync(cancellationToken),
                PendingNavigationDecision.Discard => DiscardPendingEditorChanges(),
                _ => false
            };
            accepted = accepted && identity.IsCurrent(this) && !HasPendingEditorChanges;
        }
        catch (OperationCanceledException) { accepted = false; }
        catch (Exception exception)
        { Status = "Could not finish the requested navigation: " + exception.Message; accepted = false; }
        finally
        {
            _isPendingNavigationDecision = false;
            OnPropertyChanged(nameof(IsPendingNavigationDecision));
            RefreshPendingEditorChanges();
            // Enabling the UI can synchronously create a new edit. A close or
            // source switch must still retain that edit after the notification.
            completion.TrySetResult(accepted && identity.IsCurrent(this) && !HasPendingEditorChanges);
        }
    }

    public bool DiscardPendingEditorChanges()
    {
        if (IsGlobalSaveBusy || CampaignEditor.IsCampaignFieldSaveBusy || ScenarioEditor.IsWccInlineSaveBusy) return false;
        var identity = CapturePendingEditorIdentity();
        try
        {
            // Prepare all saved baselines before discarding any editor. A missing
            // baseline must never turn into simply clearing a dirty flag.
            var campaign = CampaignEditor.PreparePendingChangesDiscard(_campaignEditorDirty || CampaignEditor.HasCampaignSourceChanges, out var problem);
            if (campaign is null) { Status = problem; return false; }
            var scenario = ScenarioEditor.PreparePendingChangesDiscard(_scenarioEditorDirty, out problem);
            if (scenario is null) { Status = problem; return false; }
            var unit = UnitEditor.PreparePendingChangesDiscard(_unitEditorDirty, out problem);
            if (unit is null) { Status = problem; return false; }
            var libraryUnit = LibraryUnitEditor.PreparePendingChangesDiscard(_libraryUnitEditorDirty, out problem);
            if (libraryUnit is null) { Status = problem; return false; }
            _isDiscardingPendingEditorChanges = true;
            // Main SourceChanging revokes scenario evidence; restore the scenario
            // first while its captured preparation still identifies the old host.
            if (!identity.IsCurrent(this) || !scenario() || !identity.IsCurrent(this) || !campaign() ||
                !identity.IsCurrent(this) || !unit() || !identity.IsCurrent(this) || !libraryUnit() || !identity.IsCurrent(this))
            { Status = "The editing context changed. Remaining edits stay open."; return false; }
            ClearCurrentScenarioDirty();
            ClearCurrentCampaignDirty();
            ClearCurrentUnitDirty();
            ClearLibraryUnitDirty();
            if (!DiscardPackageScenarioRawDraft()) return false;
            if (CampaignEditor.CurrentProject is { } project) ConfigureScenarioEvaluationPlan(project);
            ScenarioEditor.RefreshMapPreview();
            Status = "Discarded pending editor changes. Saved files were not changed.";
            SetGlobalSaveError(false);
            return !HasPendingEditorChanges;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        { Status = "Could not discard pending changes: " + exception.Message; return false; }
        finally { _isDiscardingPendingEditorChanges = false; RefreshPendingEditorChanges(); }
    }

    private string CapturePendingEditorStamp() => string.Join("\u001e", _campaignEditorDirty, _scenarioEditorDirty,
        _unitEditorDirty, _libraryUnitEditorDirty, CampaignEditor.EditVersion, ScenarioEditor.EditVersion,
        UnitEditor.EditVersion, LibraryUnitEditor.EditVersion, CampaignEditor.GetPendingSourceChangeStamp(),
        string.Join("\u001f", CampaignEditor.CampaignFieldDrafts.Select(draft => draft.GroupIdentity + "=" + draft.Summary)),
        string.Join("\u001f", ScenarioEditor.WccInlineDrafts.Select(draft => draft.GroupIdentity + "=" + draft.Summary)),
        ScenarioEditor.GetPendingSourceChangeStamp(), _packageScenarioRawVersion);

    private PendingEditorIdentity CapturePendingEditorIdentity() => new(CampaignEditor.CurrentProject, ProjectSession,
        ScenarioEditor.CurrentScenarioAsset, ScenarioSession, UnitEditor.CurrentUnitAsset, UnitSession,
        LibraryUnitEditor.CurrentUnitAsset, LibraryUnitSession)
        {
            CampaignEntryIdentity = CampaignEditor.RequiresSelectedCampaignAdapter ? CampaignEditor.SelectedCampaignEntry?.Identity : null,
            PackageEditIdentity = CampaignEditor.CampaignPackageEditDraftIdentity
        };

    private sealed record PendingEditorIdentity(WccProject? Project, WccEditorSessionContext CampaignSession,
        ScenarioFileAsset? Scenario, WccEditorSessionContext ScenarioSession, UnitFileAsset? Unit,
        WccEditorSessionContext UnitSession, UnitFileAsset? LibraryUnit, WccEditorSessionContext LibraryUnitSession)
    {
        public string? CampaignEntryIdentity { get; init; }
        public string? PackageEditIdentity { get; init; }
        public bool IsCurrent(AppShellViewModel shell) => ReferenceEquals(Project, shell.CampaignEditor.CurrentProject) &&
            (CampaignEntryIdentity == (shell.CampaignEditor.RequiresSelectedCampaignAdapter ? shell.CampaignEditor.SelectedCampaignEntry?.Identity : null) ||
                CampaignEntryIdentity is not null && shell.CampaignEditor.ResolveCheckedCampaignEntryIdentity(CampaignEntryIdentity) is { } committedIdentity &&
                    committedIdentity == shell.CampaignEditor.SelectedCampaignEntry?.Identity ||
                PackageEditIdentity is not null && shell.CampaignEditor.MatchesCheckedPackageAuthoringPublication(PackageEditIdentity)) &&
            CampaignSession == shell.ProjectSession &&
            (PackageEditIdentity is null || !shell.IsCheckedPackageWorkspacePublicationRevoked(PackageEditIdentity)) &&
            (ReferenceEquals(Scenario, shell.ScenarioEditor.CurrentScenarioAsset) && ScenarioSession == shell.ScenarioSession ||
                PackageEditIdentity is not null && shell.MatchesCheckedPackageWorkspacePublication(PackageEditIdentity, Scenario, ScenarioSession)) &&
            ReferenceEquals(Unit, shell.UnitEditor.CurrentUnitAsset) &&
            UnitSession == shell.UnitSession && ReferenceEquals(LibraryUnit, shell.LibraryUnitEditor.CurrentUnitAsset) &&
            LibraryUnitSession == shell.LibraryUnitSession;
    }
}
