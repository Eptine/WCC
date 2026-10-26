using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    // Retained across project switches: a partial filesystem operation must not be retried silently.
    private readonly WccProjectSourceEditCoordinator _collectedSaveCoordinator = new(collectDiagnostics: true);
    private string? _collectedSaveCompletionMessage;

    private bool NeedsCombinedSourceSave =>
        ScenarioEditor.HasSelectedCampaignFieldDrafts || CampaignEditor.HasSelectedCampaignEntryDrafts || CampaignEditor.HasCampaignPackageEditDraft ||
        (CampaignEditor.HasCampaignStructureDrafts || CampaignEditor.HasCampaignCreditsDrafts || CampaignEditor.HasCampaignPresetDrafts || CampaignEditor.HasCampaignScenarioCreationDraft || CampaignEditor.HasCampaignUnitTypeCreationDraft) ||
        ScenarioEditor.HasWccSourceRecipeDrafts ||
        ScenarioEditor.HasWccMusicScheduleDrafts ||
        ScenarioEditor.WccInlineDrafts.Any(draft => draft.Difficulty is not null) ||
        CampaignEditor.HasCampaignSourceDrafts && ScenarioEditor.HasWccInlineDrafts;

    private async Task<bool> SaveCollectedSourceDraftsAsync(CancellationToken cancellationToken)
    {
        if (_collectedSaveCoordinator.Recovery is { } recovery)
        { Status = DescribeCollectedSaveResult(recovery); return false; }
        if (_campaignEditorDirty || _scenarioEditorDirty)
        {
            Status = "The combined source Save needs pristine campaign and scenario baselines. Resolve applied RAW or structural changes first; all drafts remain open.";
            return false;
        }
        if (_unitEditorDirty || _libraryUnitEditorDirty)
        {
            Status = "Unit source edits use a separate save. Keep this campaign/scenario batch as drafts while resolving the unit edits.";
            return false;
        }
        // Selected and warmed publications settle before their source bindings are captured.
        await MeasureSavePhaseAsync("Wait for current previews", async () =>
        {
            await CampaignEditor.WaitForCampaignFieldProjectionAsync(cancellationToken);
            await ScenarioEditor.WaitForScenarioUiLoadingAsync(cancellationToken);
        });
        var campaign = CampaignEditor.CaptureCollectedCampaignSave(out var problem);
        if (problem is not null) { Status = problem; return false; }
        var scenario = ScenarioEditor.CaptureCollectedScenarioSave(out problem);
        if (problem is not null) { Status = problem; return false; }
        var editors = new[] { campaign, scenario }.Where(editor => editor is not null).Select(editor => editor!).ToArray();
        if (editors.Length == 0) { Status = "There are no collected source changes to save."; return false; }
        var identity = CapturePendingEditorIdentity();
        var packageReview = campaign?.Request.PackageEdit is not null ? null : scenario?.State.EntryReview ?? campaign?.State.EntryReview;
        var packageState = scenario?.State.EntryReview is not null ? scenario.State : campaign?.State;
        var packageScalar = packageReview is not null;
        var packageCatalog = packageScalar ? CampaignEditor.CampaignEntryCatalog : null;
        var packageAsset = packageScalar ? ScenarioEditor.CurrentScenarioAsset : null;
        var packageDocumentIndex = packageAsset?.Documents.FindIndex(document =>
            ReferenceEquals(document, ScenarioEditor.SelectedScenario?.Document)) ?? -1;
        var packageDocumentIds = packageAsset?.Documents.Select(document => document.InternalId).ToArray();
        var packageSource = packageAsset is { SourcePath: { } packagePath, RawTextSnapshot: { } packageBaseline } &&
            packageDocumentIndex >= 0 && packageAsset.Documents[packageDocumentIndex].OriginalText == packageBaseline
                ? new WmlStaticSource(packagePath, packageBaseline) : null;
        var packageContextSemanticKey = ScenarioEditor.EvaluationContext.SemanticKey;
        var packageRequestVersion = _campaignEntryNavigationRequestVersion;
        var packageNavigationVersion = _campaignEntryNavigationVersion;
        var pendingStamp = CapturePendingEditorStamp();
        foreach (var editor in editors) editor.RevokeCompetingAuthority();
        if (!identity.IsCurrent(this) || pendingStamp != CapturePendingEditorStamp() || editors.Any(editor => !editor.IsCurrent()))
        { Status = "The editing context changed before checking. Pending changes remain open."; return false; }
        _collectedSaveCoordinator.SetSession(editors.Select(editor => editor.State));
        void Invalidate() => _collectedSaveCoordinator.InvalidateSession();
        CampaignEditor.CollectedSaveAuthorityChanging += Invalidate;
        ScenarioEditor.CollectedSaveAuthorityChanging += Invalidate;
        var began = false;
        CheckedPackageScenarioPublication? checkedPublication = null;
        try
        {
            began = true;
            foreach (var editor in editors) editor.Begin("Checking the complete proposed changes in every declared context…");
            if (!identity.IsCurrent(this) || pendingStamp != CapturePendingEditorStamp() || editors.Any(editor => !editor.IsCurrent()))
            { Status = "The source or pending changes changed before checking. Your drafts are retained."; return false; }
            var prepared = await MeasureSavePhaseAsync("Source check", async () =>
            {
                var previous = _collectedSaveCoordinator.LastDiagnostics;
                try { return await Task.Run(async () => await _collectedSaveCoordinator.PrepareAsync(
                    editors.Select(editor => editor.Request), cancellationToken), cancellationToken); }
                finally
                {
                    if (!ReferenceEquals(previous, _collectedSaveCoordinator.LastDiagnostics))
                        _savePerformanceCollector?.AddSourceOperation(_collectedSaveCoordinator.LastDiagnostics);
                }
            });
            if (!prepared.IsPrepared || prepared.Ticket is null)
            { Status = prepared.Diagnostic; return false; }
            if (!identity.IsCurrent(this) || pendingStamp != CapturePendingEditorStamp() || editors.Any(editor => !editor.IsCurrent()))
            { Status = "The source or pending changes changed during checking. Your drafts are retained."; return false; }
            SetGlobalSaveActivity(true, "Writing the checked source changes…");
            foreach (var editor in editors) editor.Begin("Writing the checked source changes…");
            if (!identity.IsCurrent(this) || pendingStamp != CapturePendingEditorStamp() || editors.Any(editor => !editor.IsCurrent()))
            { Status = "The source or pending changes changed before writing. Your drafts are retained."; return false; }
            var result = await MeasureSavePhaseAsync("Source commit", async () =>
            {
                var previous = _collectedSaveCoordinator.LastDiagnostics;
                try { return await Task.Run(async () => await _collectedSaveCoordinator.CommitAsync(prepared.Ticket, cancellationToken),
                    cancellationToken); }
                finally
                {
                    if (!ReferenceEquals(previous, _collectedSaveCoordinator.LastDiagnostics))
                        _savePerformanceCollector?.AddSourceOperation(_collectedSaveCoordinator.LastDiagnostics);
                }
            });
            if (!result.IsSuccess)
            {
                Status = DescribeCollectedSaveResult(result);
                if (result.Status == WccProjectSourceEditStatus.RecoveryRequired)
                {
                    CampaignEditor.BlockCollectedSaveForRecovery();
                    ScenarioEditor.BlockCollectedSaveForRecovery();
                }
                return false;
            }
            if (result.Candidate is not { } candidate || !identity.IsCurrent(this) ||
                pendingStamp != CapturePendingEditorStamp() || editors.Any(editor => !editor.IsCurrent()))
            { Status = "The sources were saved, but the editing context changed. Reload them before further editing; pending drafts remain."; return false; }
            // Commit completed. A cancelled caller cannot omit publication of the saved files.
            foreach (var file in candidate.Files)
            {
                var text = await File.ReadAllTextAsync(file.CandidateSource.Path);
                if (!string.Equals(text, file.CandidateSource.Text, StringComparison.Ordinal))
                { Status = "A saved source changed before preview publication. Reload it; pending drafts remain open."; return false; }
            }
            foreach (var created in candidate.SourceCreates)
            {
                var text = await File.ReadAllTextAsync(created.Path);
                if (!string.Equals(text, created.Text, StringComparison.Ordinal))
                { Status = "A newly saved source changed before preview publication. Reload it; pending drafts remain open."; return false; }
            }
            // A compact recipe edit can change its manifest/loader without changing
            // the package main. Recheck that unchanged publication baseline too.
            if (campaign?.Request.PackageEdit is { } unchangedPackage &&
                !candidate.Files.Any(file => SourcePathEquals(file.Source.Path, unchangedPackage.Source.Path)) &&
                await File.ReadAllTextAsync(unchangedPackage.Source.Path) != unchangedPackage.Source.Text)
            { Status = "The files were saved, but the package main changed before publication. Reload the package."; return false; }
            using var campaignDisplayPublication = CampaignEditor.BeginCheckedCampaignEntryDisplayPublication(candidate);
            if (campaign?.Request.PackageEdit is { } checkedPackageEdit)
                RememberCheckedPackageWorkspacePublication(checkedPackageEdit, candidate);
            var scenarioText = scenario is null ? null : candidate.Files.Single(file =>
                SourcePathEquals(file.Source.Path, scenario.Request.Source.Path)).CandidateSource.Text;
            if (packageReview is not null && packageAsset is not null && packageSource is not null && packageState is not null && identity.Project is not null)
                _checkedPackageScenarioPublication = checkedPublication = new(identity.Project, identity.CampaignSession,
                    packageAsset, packageDocumentIndex, packageReview.SelectedEntryIdentity,
                    candidate.Files.FirstOrDefault(file => SourcePathEquals(file.Source.Path, packageSource.Path))?.CandidateSource ?? packageSource,
                    candidate.Files.FirstOrDefault(file => SourcePathEquals(file.Source.Path,
                        packageReview.CampaignMainSource.Path))?.CandidateSource ?? packageReview.CampaignMainSource,
                    packageContextSemanticKey,
                    packageState.ProfileIdentity, packageRequestVersion);
            using (scenarioText is null ? ScenarioEditor.DeferPreviewRefresh() :
                ScenarioEditor.DeferCollectedSourcePreview(scenarioText))
            {
                // Main publication revokes the old scenario plan, so publish its saved source first.
                if (scenario is not null && !MeasureSavePhase("Publish saved scenario source", () => scenario.Publish(candidate.Files.Single(file =>
                        SourcePathEquals(file.Source.Path, scenario.Request.Source.Path)).CandidateSource.Text)))
                { Status = "The scenario was saved but its editor changed. Reload its source."; return false; }
                var campaignText = campaign is null ? null : candidate.Files.SingleOrDefault(file =>
                    SourcePathEquals(file.Source.Path, campaign.Request.Source.Path))?.CandidateSource.Text
                    ?? (campaign.Request.PackageEdit is not null ? campaign.Request.Source.Text : null);
                if (campaign is not null && (campaignText is null || !MeasureSavePhase("Publish saved campaign source", () => campaign.Publish(campaignText))))
                { Status = "The campaign was saved but its editor changed. Reload its source."; return false; }
                SetGlobalSaveActivity(true, "Refreshing saved source and editor previews…");
                foreach (var editor in editors) editor.Begin("Refreshing saved source and editor previews…");
                if (!identity.IsCurrent(this))
                { Status = "The sources were saved, but the editing context changed before refreshing. Reload the saved files."; return false; }
                // Both hooks are the same host index refresh. Invoke exactly one for this source batch.
                var refresh = campaign is not null ? CampaignEditor.CampaignFieldLibraryRefreshAsync
                    : ScenarioEditor.SourceLibraryRefreshAsync;
                if (refresh is not null)
                {
                    try { await MeasureSavePhaseAsync("Library refresh", refresh); }
                    catch (Exception exception)
                    {
                        Status = "The sources were saved, but their preview refresh failed: " + exception.Message;
                        return false;
                    }
                }
                await MeasureSavePhaseAsync("Refresh committed campaign view", CampaignEditor.RefreshCheckedCampaignEntryProjectionAsync);
                if (campaign?.Request is { PackageEdit: not null } or { ScenarioCreation: not null })
                    await MeasureSavePhaseAsync("Refresh committed campaign workspace", () => WaitForCampaignEntriesAsync());
                // The refreshed host may clear old owner selections while binding
                // its new snapshot. Remember the mapped owners for the final preview.
                if (identity.IsCurrent(this))
                    foreach (var editor in editors) editor.RememberSelection(candidate);
            }
            if (packageReview is not null && packageAsset is not null && packageSource is not null && packageCatalog is not null && packageDocumentIds is not null && packageState is not null)
            {
                if (!await MeasureSavePhaseAsync("Restore saved scenario selection", () => RestoreCheckedPackageScenarioSelectionAsync(identity.Project, identity.CampaignSession,
                        packageAsset, packageDocumentIndex, packageReview.SelectedEntryIdentity))) return false;
                RememberCheckedPackageScenarioPublication(packageCatalog, identity.Project, identity.CampaignSession,
                    packageAsset, packageDocumentIds, packageState.ProfileIdentity, packageRequestVersion, packageNavigationVersion);
            }
            Status = DescribeCollectedSaveResult(result);
            if (result.Status == WccProjectSourceEditStatus.SucceededWithCleanupWarning)
                _collectedSaveCompletionMessage = Status;
            // All categories accepted at preflight are already committed. Local
            // busy flags stay set through publication, and callback-created edits
            // must remain drafts rather than entering a second legacy writer.
            return identity.IsCurrent(this) && !HasPendingEditorChanges;
        }
        finally
        {
            if (ReferenceEquals(_checkedPackageScenarioPublication, checkedPublication)) _checkedPackageScenarioPublication = null;
            CampaignEditor.CollectedSaveAuthorityChanging -= Invalidate;
            ScenarioEditor.CollectedSaveAuthorityChanging -= Invalidate;
            _collectedSaveCoordinator.InvalidateSession();
            if (began) foreach (var editor in editors) editor.End();
            if (campaign?.Request.PackageEdit is { } packageEdit) EndCheckedPackageWorkspacePublication(packageEdit.Identity);
            RefreshPendingEditorChanges();
        }
    }

    private static bool SourcePathEquals(string left, string right) => string.Equals(Path.GetFullPath(left),
        Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string DescribeCollectedSaveResult(WccProjectSourceEditResult result)
    {
        var retained = result.Files.Where(file => file.WasCreated || file.Save?.RecoveryBackupPath is not null ||
            file.Save?.RecoveryCandidatePath is not null).Select(file => file.SourcePath +
                (file.WasCreated ? " | Created destination retained" : string.Empty) +
                (file.Save?.RecoveryBackupPath is { } backup ? " | Backup: " + backup : string.Empty) +
                (file.Save?.RecoveryCandidatePath is { } stage ? " | Candidate: " + stage : string.Empty));
        return string.Join(Environment.NewLine, new[] { result.Diagnostic }.Concat(retained));
    }
}
