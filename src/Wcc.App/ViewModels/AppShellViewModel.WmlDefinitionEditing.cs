using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private long _wmlDefinitionEditEpoch;
    private Task? _wmlDefinitionRefreshTask;
    private WccRawMacroDefinitionEditResult? _wmlDefinitionRecovery;

    /// <summary>Navigation evidence is inspected independently from this physical write capability.</summary>
    public WmlEditorDocumentViewModel? CreateCampaignMacroSourceDocument(
        WmlEditorSourceTarget target, WmlEditorDocumentViewModel rootDocument)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rootDocument);
        if (target.EvidenceKind == WmlEditorReferenceEvidenceKind.CapturedInclude) return null;
        WmlEditorDocumentViewModel ReadOnly(string reason) => new(target.Title, target.Text, isReadOnly: true,
            descriptionText: target.Description + "\nRead-only: " + reason);
        if (rootDocument.IsReadOnly) return ReadOnly("This editor window is in read mode.");
        if (_wmlDefinitionRecovery is { } recovery) return ReadOnly(DescribeWmlDefinitionResult(recovery));
        var project = CampaignEditor.CurrentProject;
        var snapshot = LibrarySnapshot;
        var session = ProjectSession;
        if (project?.Kind != WccProjectKind.Campaign || snapshot is null ||
            session.Scope != WccEditorSessionScope.Project || !session.CanWrite || string.IsNullOrWhiteSpace(session.RootPath))
            return ReadOnly("No writable campaign WorkingCopy owns this definition.");
        var sourcePath = target.Origin.Source.Path;
        if (ResolveEditorAccess(sourcePath) == WccEditorAccessMode.ReadOnly)
            return ReadOnly("This definition belongs to a protected Wesnoth installation.");
        // A physical file represented by different kinds of structured editor needs a
        // coordinated publication protocol. Keep that unusual mixed-file case in RAW.
        bool SameSource(string? path) => path is not null && PathsEqual(sourcePath, path);
        var modelKinds = (SameSource(project.SourcePath) ? 1 : 0) +
            (project.ProjectScenarioFiles.Any(file => SameSource(file.SourcePath)) || SameSource(ScenarioEditor.LoadedPath) ? 1 : 0) +
            (project.ProjectUnitFiles.Any(file => SameSource(file.SourcePath)) ||
                SameSource(UnitEditor.LoadedPath) || SameSource(LibraryUnitEditor.LoadedPath) ? 1 : 0);
        if (modelKinds > 1)
            return ReadOnly("This file combines different structured document kinds. Edit its complete source through the containing editor.");
        if (FindCurrentWorkingProjectRevisionId(snapshot) is not { } revision)
            return ReadOnly("The current campaign source revision could not be identified.");
        var physical = WccRawMacroDefinitionSourceCapture.TryCapture(snapshot, revision, session.RootPath,
            target.Title, target.Origin, target.SourceStartLine, target.SourceRevisionId,
            target.SourceContentHash, out var captureProblem);
        if (physical is null) return ReadOnly(captureProblem ?? "One current physical declaration could not be captured.");
        var identity = CapturePendingEditorIdentity();
        var stamp = CapturePendingEditorStamp();
        var campaignVersion = CampaignEditor.EditVersion;
        var scenarioVersion = ScenarioEditor.EditVersion;
        var unitVersion = UnitEditor.EditVersion;
        var libraryUnitVersion = LibraryUnitEditor.EditVersion;
        var campaignDocument = project.Campaign;
        var campaignBaseline = project.CampaignRawTextSnapshot;
        var scenarioDocument = ScenarioEditor.SelectedScenario?.Document;
        var scenarioBaseline = ScenarioEditor.CurrentScenarioAsset?.RawTextSnapshot;
        var scenarioContext = ScenarioEditor.EvaluationContext.SemanticKey;
        var unitDocument = UnitEditor.SelectedUnit?.Document;
        var unitBaseline = UnitEditor.CurrentUnitAsset?.RawTextSnapshot;
        var libraryUnitDocument = LibraryUnitEditor.SelectedUnit?.Document;
        var libraryUnitBaseline = LibraryUnitEditor.CurrentUnitAsset?.RawTextSnapshot;
        var epoch = _wmlDefinitionEditEpoch;
        var installation = _activeWesnothInstallation;
        var environmentIsCurrent = ScenarioEditor.CaptureRawEditorEnvironmentGuard();
        System.Collections.Generic.IReadOnlyList<WmlDefinitionSourcePublication>? publications = null;
        bool HostCurrent() => epoch == _wmlDefinitionEditEpoch && identity.IsCurrent(this) &&
            campaignVersion == CampaignEditor.EditVersion && scenarioVersion == ScenarioEditor.EditVersion &&
            unitVersion == UnitEditor.EditVersion && libraryUnitVersion == LibraryUnitEditor.EditVersion &&
            ReferenceEquals(campaignDocument, project.Campaign) && campaignBaseline == project.CampaignRawTextSnapshot &&
            ReferenceEquals(scenarioDocument, ScenarioEditor.SelectedScenario?.Document) &&
            scenarioBaseline == ScenarioEditor.CurrentScenarioAsset?.RawTextSnapshot &&
            scenarioContext == ScenarioEditor.EvaluationContext.SemanticKey &&
            ReferenceEquals(unitDocument, UnitEditor.SelectedUnit?.Document) && unitBaseline == UnitEditor.CurrentUnitAsset?.RawTextSnapshot &&
            ReferenceEquals(libraryUnitDocument, LibraryUnitEditor.SelectedUnit?.Document) &&
            libraryUnitBaseline == LibraryUnitEditor.CurrentUnitAsset?.RawTextSnapshot &&
            environmentIsCurrent() &&
            ReferenceEquals(snapshot, LibrarySnapshot) && ReferenceEquals(installation, _activeWesnothInstallation) &&
            session == ProjectSession && session.CanWrite &&
            FindCurrentWorkingProjectRevisionId(snapshot) == revision &&
            ResolveEditorAccess(sourcePath) == WccEditorAccessMode.Writable &&
            !IsShellBusy && !HasPendingEditorChanges && !rootDocument.IsModified &&
            !rootDocument.IsApplying && !rootDocument.IsCancelled && _wmlDefinitionRecovery is null;
        bool IsCurrent() => HostCurrent() && stamp == CapturePendingEditorStamp() &&
            (publications?.All(item => item.IsCurrent()) ?? true);
        if (!IsCurrent())
            return ReadOnly("Apply or discard the original draft and pending editor changes before editing a shared definition.");
        var edit = WccRawMacroDefinitionEditSession.TryCreate(snapshot, revision, session.RootPath,
            physical, IsCurrent, out var problem);
        if (edit is null) return ReadOnly(problem ?? "An exact writable physical declaration could not be captured.");
        return new(target.Title, physical.Text, text =>
        {
            if (!IsCurrent()) throw new InvalidOperationException("The shared definition context changed. Reopen its source before applying.");
            if (text == physical.Text)
            {
                var unchanged = edit.Apply(text);
                if (!unchanged.IsSuccess) throw new InvalidOperationException(DescribeWmlDefinitionResult(unchanged));
                return;
            }
            IDisposable? deferral = ScenarioEditor.DeferPreviewRefresh();
            try
            {
                // DeferPreviewRefresh revokes old preview authority itself. Rebase
                // that owned generation change only while all user/model intent
                // and the exact project environment still match the opening.
                if (!HostCurrent()) throw new InvalidOperationException("The editor changed while preparing the shared definition save.");
                stamp = CapturePendingEditorStamp();
                var savedText = physical.Source.Text[..physical.StartOffset] + text +
                    physical.Source.Text[(physical.StartOffset + physical.Length)..];
                publications = PrepareWmlDefinitionPublications(sourcePath, physical.Source.Text, savedText, out var publicationProblem);
                if (publications is null) throw new InvalidOperationException(publicationProblem);
                var result = edit.Apply(text);
                if (result.Status == WccRawMacroDefinitionEditStatus.RecoveryRequired) _wmlDefinitionRecovery = result;
                if (!result.IsSuccess) throw new InvalidOperationException(DescribeWmlDefinitionResult(result));
                if (result.Status == WccRawMacroDefinitionEditStatus.Unchanged) return;
                _wmlDefinitionEditEpoch++;
                var published = true;
                try
                {
                    foreach (var publication in publications)
                    {
                        if (!identity.IsCurrent(this) || HasPendingEditorChanges || !publication.Publish())
                        { published = false; break; }
                    }
                }
                catch (Exception failure)
                { published = false; Status = "The shared definition was saved, but the editor could not reload it: " + failure.Message; }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _wmlDefinitionRefreshTask = completion.Task;
                _ = RefreshAfterWmlDefinitionSaveAsync(completion, identity, result, deferral, published);
                deferral = null;
            }
            finally { deferral?.Dispose(); publications = null; }
        }, descriptionText: target.Description + "\nCurrent physical declaration captured for manual editing. " +
            "Shared campaign definition: changes affect every use. " +
            "Apply definition or close this tab to write its source file. The original draft uses its own Apply action. " +
            "Apply or discard other pending edits first.", validate: edit.Validate,
            closePolicy: WmlEditorClosePolicy.ApplyOnClose);
    }

    public Task WaitForWmlDefinitionRefreshAsync() => _wmlDefinitionRefreshTask ?? Task.CompletedTask;

    private async Task RefreshAfterWmlDefinitionSaveAsync(TaskCompletionSource completion,
        PendingEditorIdentity identity, WccRawMacroDefinitionEditResult result, IDisposable deferral, bool published)
    {
        var refreshed = false;
        try
        {
            SetGlobalSaveError(false);
            SetGlobalSaveActivity(true, "Refreshing the shared macro definition and campaign previews…");
            await Task.Yield();
            if (!published)
            { Status = "The shared definition was saved, but the editor changed during its reload. Reload campaign previews before editing again."; return; }
            if (!identity.IsCurrent(this) || HasPendingEditorChanges) return;
            if (ScenarioEditor.SourceLibraryRefreshAsync is { } refresh) await refresh();
            else await RefreshConfiguredLibraryIndexAsync();
            deferral.Dispose();
            if (!identity.IsCurrent(this) || HasPendingEditorChanges) return;
            await CampaignEditor.WaitForCampaignFieldProjectionAsync();
            await ScenarioEditor.WaitForScenarioUiLoadingAsync();
            refreshed = identity.IsCurrent(this) && !HasPendingEditorChanges;
            Status = DescribeWmlDefinitionResult(result);
        }
        catch (Exception failure)
        { Status = "The shared definition was saved, but its previews could not refresh: " + failure.Message; }
        finally
        {
            try
            {
                deferral.Dispose();
                if (!refreshed && !(Status?.StartsWith("The shared definition was saved", StringComparison.Ordinal) ?? false))
                    Status = "The shared definition was saved. Reload campaign previews before editing again.";
                SetGlobalSaveError(!refreshed || result.Save?.Status == WccTextAtomicReplaceStatus.SucceededWithCleanupWarning);
            }
            finally
            {
                // Completion must settle even if a UI callback throws while the
                // already committed source's preview/busy state is being released.
                try { SetGlobalSaveActivity(false, string.Empty); }
                finally { completion.TrySetResult(); }
            }
        }
    }

    private static string DescribeWmlDefinitionResult(WccRawMacroDefinitionEditResult result) =>
        result.Diagnostic + (result.Save?.RecoveryBackupPath is { } backup ? "\nRetained baseline: " + backup : "") +
        (result.Save?.RecoveryCandidatePath is { } candidate ? "\nRetained candidate: " + candidate : "");
}
