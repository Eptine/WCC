using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>An immutable authored-file proposal. Applying it stages text; it grants no write authority.</summary>
public sealed class WccPackageScenarioRawEditDraft
{
    internal WccPackageScenarioRawEditDraft(WccScenarioFieldEditSessionState state, ScenarioFileAsset asset,
        ScenarioDocument document, int scenarioIndex, Func<bool> isCurrent, string text, long inputVersion)
    {
        State = state; Asset = asset; Document = document; ScenarioIndex = scenarioIndex; IsCurrent = isCurrent;
        Text = text; InputVersion = inputVersion;
        InitialSelectionStart = text == state.Source.Text ? document.BlockSpan.StartIndex :
            ScenarioImporter.ImportAllFromText(text).ElementAtOrDefault(scenarioIndex)?.BlockSpan.StartIndex
            ?? document.BlockSpan.StartIndex;
    }
    internal WccScenarioFieldEditSessionState State { get; }
    internal ScenarioFileAsset Asset { get; }
    internal ScenarioDocument Document { get; }
    internal int ScenarioIndex { get; }
    internal Func<bool> IsCurrent { get; }
    internal long InputVersion { get; }
    public string SourcePath => State.Source.Path;
    public string Text { get; }
    public int InitialSelectionStart { get; }
    public bool AuthorizesPersistence => false;
}

public partial class AppShellViewModel
{
    private sealed record PendingPackageScenarioRaw(WccPackageScenarioRawEditDraft Draft, string CandidateText, long Version);
    private PendingPackageScenarioRaw? _packageScenarioRawDraft;
    private long _packageScenarioRawVersion;
    private long _packageRawNavigationPublicationVersion;
    private PackageRawNavigationPublication? _packageRawNavigationPublication;
    private sealed record PackageRawNavigationPublication(long Version,
        WccCampaignEntryCatalogProjection BeforeCatalog, WccCampaignEntryCatalogProjection AfterCatalog,
        WccLibrarySnapshot AfterSnapshot, WccProject Project, WccEditorSessionContext Session,
        ScenarioFileAsset EditedAsset, string[] BeforeDocumentIds, string? ProfileIdentity,
        long RequestVersion, long BeforeNavigationVersion, long AfterNavigationVersion);
    private sealed record PackageRawNavigationDestination(ScenarioFileAsset Asset, string SourcePath,
        int DocumentIndex, string? DocumentId, string? MembershipIdentity);
    private sealed record PackageScenarioSourceBinding(WccScenarioFieldEditSessionState State, ScenarioFileAsset Asset,
        ScenarioDocument Document, int ScenarioIndex, Func<bool> IsCurrent);
    // View publication only: our successful selected-campaign commit may hydrate
    // its exact current scenario, even when only menu fields changed. User
    // navigation remains blocked until the same Save completes.
    private sealed record CheckedPackageScenarioPublication(WccProject Project, WccEditorSessionContext Session,
        ScenarioFileAsset Asset, int DocumentIndex, string EntryIdentity, WmlStaticSource SavedSource,
        WmlStaticSource MainSource, string ContextSemanticKey, string? ProfileIdentity, long RequestVersion);
    private CheckedPackageScenarioPublication? _checkedPackageScenarioPublication;

    private ProjectScenarioFileVm? FindCheckedPackageScenarioPublication(
        System.Collections.Generic.IEnumerable<ProjectScenarioFileVm> rows)
    {
        if (_checkedPackageScenarioPublication is not { } publication || !IsGlobalSaveBusy ||
            HasPendingEditorChanges ||
            !ReferenceEquals(publication.Project, CampaignEditor.CurrentProject) || publication.Session != ProjectSession ||
            publication.RequestVersion != _campaignEntryNavigationRequestVersion ||
            CampaignEditor.ResolveCheckedCampaignEntryIdentity(publication.EntryIdentity) is not { } entryIdentity ||
            entryIdentity != CampaignEditor.SelectedCampaignEntry?.Identity ||
            publication.ContextSemanticKey != ScenarioEditor.EvaluationContext.SemanticKey ||
            publication.ProfileIdentity != BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile()) ||
            publication.Asset.RawTextSnapshot != publication.SavedSource.Text ||
            !PathsEqual(publication.Asset.SourcePath, publication.SavedSource.Path) ||
            publication.Asset.Documents.ElementAtOrDefault(publication.DocumentIndex) is not { } document ||
            document.OriginalText != publication.SavedSource.Text ||
            CampaignEditor.CampaignEntryCatalog is not { Capture.IsCaptureComplete: true } catalog ||
            catalog.Capture.EntrySource is not { } main || !PathsEqual(main.Path, publication.MainSource.Path) ||
            main.Text != publication.MainSource.Text || main.Text != CampaignEditor.GetCurrentMainSourceRawWml() ||
            catalog.Capture.Sources.Count(source => PathsEqual(source.Path, publication.SavedSource.Path) &&
                source.Text == publication.SavedSource.Text) != 1 ||
            !_campaignEntryMemberships.TryGetValue(entryIdentity, out var observation) || !observation.IsComplete) return null;
        var matches = rows.Where(row => row.CampaignEntry?.Identity == entryIdentity &&
            ReferenceEquals(row.Asset, publication.Asset) && row.ScenarioDocumentInternalId == document.InternalId &&
            row.Membership?.OwnerSource is { } source && PathsEqual(source.Path, publication.SavedSource.Path) &&
            source.Text == publication.SavedSource.Text).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public bool HasPackageScenarioRawDraft => _packageScenarioRawDraft is not null;
    public string? PendingPackageScenarioRawText => _packageScenarioRawDraft?.CandidateText;
    public bool IsPackageScenarioRawToolbarVisible => IsProjectWorkspace && SelectedTabIndex == 1 &&
        IsProjectScenarioWorkspace && CampaignEditor.RequiresSelectedCampaignAdapter;
    public bool CanEditPackageScenarioRaw => !IsShellBusy && !HasOtherPackageRawChanges &&
        (_packageScenarioRawDraft is { } pending ? pending.Draft.IsCurrent() : CapturePackageScenarioRawEdit() is not null);

    private bool HasOtherPackageRawChanges => _campaignEditorDirty || _scenarioEditorDirty || _unitEditorDirty ||
        _libraryUnitEditorDirty || CampaignEditor.HasCampaignSourceDrafts || CampaignEditor.HasCampaignSourceChanges ||
        ScenarioEditor.HasWccInlineDrafts || ScenarioEditor.HasWccMusicScheduleDrafts || ScenarioEditor.HasWccSourceRecipeDrafts;

    public WccPackageScenarioRawEditDraft? BeginPackageScenarioRawEdit()
    {
        if (IsShellBusy || HasOtherPackageRawChanges) return null;
        if (_packageScenarioRawDraft is { } pending)
            return pending.Draft.IsCurrent() ? new(pending.Draft.State, pending.Draft.Asset, pending.Draft.Document,
                pending.Draft.ScenarioIndex, pending.Draft.IsCurrent, pending.CandidateText, _packageScenarioRawVersion) : null;
        return CapturePackageScenarioRawEdit();
    }

    private WccPackageScenarioRawEditDraft? CapturePackageScenarioRawEdit()
    {
        var binding = CaptureSelectedScenarioSourceBinding(true);
        return binding is null ? null : new(binding.State, binding.Asset, binding.Document, binding.ScenarioIndex,
            binding.IsCurrent, binding.State.Source.Text, _packageScenarioRawVersion);
    }

    private PackageScenarioSourceBinding? CaptureSelectedScenarioSourceBinding(bool rawSource)
    {
        if (!CampaignEditor.RequiresSelectedCampaignAdapter || !CampaignEditor.HasSelectedCampaignEntryBinding || CampaignEditor.CurrentProject is not { } project ||
            ProjectSession is not { Scope: WccEditorSessionScope.Project, CanWrite: true } ||
            string.IsNullOrWhiteSpace(ProjectSession.RootPath) ||
            ScenarioSession is not { Scope: WccEditorSessionScope.Project, CanWrite: true } ||
            CampaignEditor.CampaignEntryCatalog is not { } catalog ||
            CampaignEditor.SelectedCampaignEntry is not { IsContextComplete: true, Plan: { } plan } entry ||
            LibrarySnapshot is not { } snapshot || FindCurrentProjectRevisionId(snapshot) is not { } root ||
            catalog.Capture is not { IsCaptureComplete: true, EntrySource: { } main } ||
            main.Text != CampaignEditor.GetCurrentMainSourceRawWml() ||
            ScenarioEditor.CurrentScenarioAsset is not { SourcePath: { } sourcePath, RawTextSnapshot: { } baseline } asset ||
            ScenarioEditor.SelectedScenario?.Document is not { } document ||
            FindCurrentProjectScenarioRow(asset) is not { Membership: { DisplayScenarioSpan: not null } membership } row ||
            row.CampaignEntry?.Identity != entry.Identity || row.ScenarioDocumentInternalId != document.InternalId ||
            !project.ProjectScenarioFiles.Any(item => ReferenceEquals(item, asset)) ||
            !asset.Documents.Any(item => ReferenceEquals(item, document)) || document.OriginalText != baseline ||
            string.IsNullOrWhiteSpace(sourcePath) ||
            !IsPathInsideRoot(ProjectSession.RootPath!, sourcePath) ||
            PathsEqual(sourcePath, main.Path) || catalog.Entries.Any(item => PathsEqual(item.OwnerSource?.Path, sourcePath)) ||
            membership.OwnerSource is not { } source || !PathsEqual(source.Path, sourcePath) || source.Text != baseline ||
            !_campaignEntryMemberships.TryGetValue(entry.Identity, out var observation) || !observation.IsComplete)
            return null;
        var context = ScenarioEditor.EvaluationContext;
        var occurrences = membership.Owners.Where(item => item.Context.SemanticKey == context.SemanticKey).Take(2).ToArray();
        if (occurrences.Length != 1 || occurrences[0].Owner.OpeningOrigin is not { } opening ||
            !PathsEqual(opening.Source.Path, source.Path) || opening.Source.Text != baseline ||
            opening.StartOffset != document.BlockSpan.StartIndex) return null;
        var projectSession = ProjectSession;
        var scenarioSession = ScenarioSession;
        var profile = ResolvePinnedProjectSourceResourceProfile();
        var profileIdentity = BuildWesnothSourceResourceProfileIdentity(profile);
        var state = new WccScenarioFieldEditSessionState(
            _currentProjectMetadata?.ProjectId.Value.ToString() ?? projectSession.RootPath!, projectSession.RootPath!,
            root.Value.ToString(), true, snapshot, root, source, plan, context, occurrences[0].Owner,
            profile?.AddonsPath, profileIdentity)
        { EntryReview = new WccCampaignEntryReviewIntent(main, entry.Identity) };
        bool Current() => ReferenceEquals(project, CampaignEditor.CurrentProject) && projectSession == ProjectSession &&
            scenarioSession == ScenarioSession && ProjectSession.CanWrite && ScenarioSession.CanWrite &&
            ReferenceEquals(snapshot, LibrarySnapshot) && FindCurrentProjectRevisionId(snapshot) == root &&
            ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) &&
            CampaignEditor.SelectedCampaignEntry?.Identity == entry.Identity &&
            ReferenceEquals(asset, ScenarioEditor.CurrentScenarioAsset) &&
            ReferenceEquals(document, ScenarioEditor.SelectedScenario?.Document) &&
            (rawSource ? ScenarioEditor.EvaluationContext.SemanticKey == context.SemanticKey :
                membership.Owners.Count(item => item.Context.SemanticKey == ScenarioEditor.EvaluationContext.SemanticKey) == 1) &&
            asset.RawTextSnapshot == baseline && document.OriginalText == baseline &&
            PathsEqual(asset.SourcePath, sourcePath) && main.Text == CampaignEditor.GetCurrentMainSourceRawWml() &&
            BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile()) == profileIdentity &&
            (!rawSource || !CampaignEditor.IsCampaignFieldSaveBusy && !ScenarioEditor.IsWccInlineSaveBusy && !HasOtherPackageRawChanges) &&
            (rawSource || !HasPackageScenarioRawDraft && !_campaignEditorDirty && !_scenarioEditorDirty);
        return new(state, asset, document, asset.Documents.IndexOf(document), Current);
    }

    public string? ValidatePackageScenarioRawEdit(WccPackageScenarioRawEditDraft draft, string text)
    {
        if (IsGlobalSaveBusy || _isDiscardingPendingEditorChanges || HasOtherPackageRawChanges ||
            draft.InputVersion != _packageScenarioRawVersion || !draft.IsCurrent())
            return "The scenario source or package context changed. Keep this text and reopen its RAW editor.";
        var validation = WmlSingleBlockValidator.ValidateSourceStructure(text);
        if (!validation.IsValid) return validation.Problem;
        try
        {
            if (ScenarioImporter.ImportAllFromText(text).Count != draft.Asset.Documents.Count)
                return "The RAW change must preserve the physical file's scenario blocks.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { return "The scenario source could not be imported: " + exception.Message; }
        return null;
    }

    public bool ApplyPackageScenarioRawEdit(WccPackageScenarioRawEditDraft draft, string text)
    {
        if (ValidatePackageScenarioRawEdit(draft, text) is { } problem) { Status = problem; return false; }
        _packageScenarioRawVersion++;
        _packageScenarioRawDraft = text == draft.State.Source.Text ? null : new(draft, text, _packageScenarioRawVersion);
        Status = _packageScenarioRawDraft is null ? "No package RAW changes remain." :
            "Scenario RAW text is pending. Save checks the selected campaign and its difficulties before writing.";
        RefreshPendingEditorChanges();
        OnPropertyChanged(nameof(CanSaveCurrentContext));
        return true;
    }

    private bool DiscardPackageScenarioRawDraft()
    {
        if (IsGlobalSaveBusy) return false;
        _packageScenarioRawDraft = null;
        _packageScenarioRawVersion++;
        RefreshPendingEditorChanges();
        OnPropertyChanged(nameof(CanSaveCurrentContext));
        return true;
    }

    private async Task<bool> SavePackageScenarioRawDraftAsync(CancellationToken cancellationToken)
    {
        var pending = _packageScenarioRawDraft;
        if (pending is null) return true;
        if (HasOtherPackageRawChanges || !pending.Draft.IsCurrent())
        { Status = "Package RAW Save needs its unchanged source and context without other pending edits. Your RAW text is retained."; return false; }
        var identity = CapturePendingEditorIdentity();
        var beforeCatalog = CampaignEditor.CampaignEntryCatalog!;
        var requestVersion = _campaignEntryNavigationRequestVersion;
        var beforeNavigationVersion = _campaignEntryNavigationVersion;
        var draft = pending.Draft;
        var state = draft.State;
        var request = new WccProjectSourceEditRequest(state.Source, rawPatches: [new(new WmlStaticSourceRange(state.Source, 0, state.Source.Text.Length), pending.CandidateText)]);
        bool Current() => ReferenceEquals(pending, _packageScenarioRawDraft) && pending.Version == _packageScenarioRawVersion &&
            identity.IsCurrent(this) && draft.IsCurrent();
        void Invalidate() => _collectedSaveCoordinator.InvalidateSession();
        _collectedSaveCoordinator.SetSession([state]);
        CampaignEditor.CollectedSaveAuthorityChanging += Invalidate;
        ScenarioEditor.CollectedSaveAuthorityChanging += Invalidate;
        try
        {
            var prepared = await MeasureSavePhaseAsync("Check selected campaign", () => Task.Run(async () =>
                await _collectedSaveCoordinator.PrepareAsync([request], cancellationToken), cancellationToken));
            if (!prepared.IsPrepared || prepared.Ticket is null) { Status = prepared.Diagnostic; return false; }
            if (!Current()) { Status = "The package context changed during review. Your RAW text is retained."; return false; }
            SetGlobalSaveActivity(true, "Writing the scenario checked in the selected campaign...");
            if (!Current()) { Status = "The package context changed before writing. Your RAW text is retained."; return false; }
            var result = await MeasureSavePhaseAsync("Commit checked scenario source", () => Task.Run(async () =>
                await _collectedSaveCoordinator.CommitAsync(prepared.Ticket, cancellationToken), cancellationToken));
            if (!result.IsSuccess)
            {
                Status = DescribeCollectedSaveResult(result);
                if (result.Status == WccProjectSourceEditStatus.RecoveryRequired)
                { CampaignEditor.BlockCollectedSaveForRecovery(); ScenarioEditor.BlockCollectedSaveForRecovery(); }
                return false;
            }
            // Once committed, caller cancellation cannot omit publication.
            var saved = await File.ReadAllTextAsync(state.Source.Path);
            if (saved != pending.CandidateText || !Current())
            { Status = "The scenario was saved, but its source or context changed before publication. Reload it; the RAW text is retained."; return false; }
            var beforeDocumentIds = draft.Asset.Documents.Select(document => document.InternalId).ToArray();
            if (!ScenarioEditor.TryPublishReviewedPackageRawScenario(draft.Asset, state.Source.Text,
                    saved, draft.ScenarioIndex, 0))
            { Status = "The scenario was saved, but its editor changed before publication. Reload it; the RAW text is retained."; return false; }
            _packageScenarioRawDraft = null;
            _packageScenarioRawVersion++;
            RefreshPendingEditorChanges();
            ClearCurrentScenarioDirty();
            await RefreshOwnedProjectPackageIndexAsync();
            if (!await MeasureSavePhaseAsync("Restore saved scenario selection", () => RestoreCheckedPackageScenarioSelectionAsync(identity.Project, identity.CampaignSession,
                    draft.Asset, draft.ScenarioIndex, state.EntryReview!.SelectedEntryIdentity))) return false;
            Status = DescribeCollectedSaveResult(result);
            RememberCheckedPackageScenarioPublication(beforeCatalog, identity.Project, identity.CampaignSession,
                draft.Asset, beforeDocumentIds, state.ProfileIdentity, requestVersion, beforeNavigationVersion);
            return !HasPendingEditorChanges;
        }
        finally
        {
            CampaignEditor.CollectedSaveAuthorityChanging -= Invalidate;
            ScenarioEditor.CollectedSaveAuthorityChanging -= Invalidate;
            _collectedSaveCoordinator.InvalidateSession();
            RefreshPendingEditorChanges();
        }
    }

    private async Task<bool> RestoreCheckedPackageScenarioSelectionAsync(WccProject? project, WccEditorSessionContext session,
        ScenarioFileAsset asset, int documentIndex, string entryIdentity)
    {
        await WaitForCampaignEntriesAsync();
        await ScenarioEditor.WaitForScenarioUiLoadingAsync();
        if (!ReferenceEquals(project, CampaignEditor.CurrentProject) || ProjectSession != session)
        { Status = "The scenario was saved; reopen its project to refresh the selected source."; return false; }
        var selectedDocument = asset.Documents.ElementAtOrDefault(documentIndex);
        var resolvedEntryIdentity = CampaignEditor.ResolveCheckedCampaignEntryIdentity(entryIdentity);
        var rows = CampaignEditor.ScenarioFiles.Where(row => row.CampaignEntry?.Identity == resolvedEntryIdentity &&
            ReferenceEquals(row.Asset, asset) && selectedDocument is not null &&
            row.ScenarioDocumentInternalId == selectedDocument.InternalId).Take(2).ToArray();
        if (rows.Length != 1)
        { Status = "The scenario was saved; its selected owner could not be uniquely rebound. Inspect the refreshed source."; return false; }
        if (!HasCurrentCheckedCampaignScenarioSelection(rows[0]))
            using (BeginCampaignEntrySelectionPublication())
                PublishCampaignEntrySelection(rows[0].CampaignEntry, rows[0]);
        if (!ReferenceEquals(asset, ScenarioEditor.CurrentScenarioAsset) ||
            ScenarioEditor.SelectedScenario?.Document.InternalId != selectedDocument!.InternalId)
        { Status = "The scenario was saved, but its selected editor changed during publication. Reload its source."; return false; }
        return true;
    }

    private void RememberCheckedPackageScenarioPublication(WccCampaignEntryCatalogProjection beforeCatalog,
        WccProject? project, WccEditorSessionContext session, ScenarioFileAsset asset, string[] beforeDocumentIds,
        string? profileIdentity, long requestVersion, long beforeNavigationVersion)
    {
        if (!HasPendingEditorChanges && CampaignEditor.CampaignEntryCatalog is { } afterCatalog &&
            LibrarySnapshot is { } afterSnapshot && project is not null && requestVersion == _campaignEntryNavigationRequestVersion)
            _packageRawNavigationPublication = new(++_packageRawNavigationPublicationVersion, beforeCatalog,
                afterCatalog, afterSnapshot, project, session, asset, beforeDocumentIds, profileIdentity,
                requestVersion, beforeNavigationVersion, _campaignEntryNavigationVersion);
    }

    private static PackageRawNavigationDestination? CapturePackageRawNavigationDestination(ProjectScenarioFileVm? row)
    {
        if (row?.Asset.SourcePath is not { } path) return null;
        return new(row.Asset, path, row.Asset.Documents.FindIndex(document =>
            document.InternalId == row.ScenarioDocumentInternalId), row.ScenarioDocumentInternalId,
            row.MembershipIdentity);
    }

    // Only a completed save of this exact request may replace its stale menu/row publication.
    // An independent refresh, later request or unrelated save keeps the ordinary stale refusal.
    private bool TryRebindNavigationAfterPackageRawSave(WccProject? project, WccEditorSessionContext session,
        WccCampaignEntryCatalogProjection? beforeCatalog, string entryIdentity,
        PackageRawNavigationDestination? destination, bool hadRow, long requestVersion,
        long beforeNavigationVersion, long previousPublicationVersion,
        out WccCampaignEntry? entry, out ProjectScenarioFileVm? row)
    {
        entry = null; row = null;
        if (_packageRawNavigationPublication is not { } publication || publication.Version <= previousPublicationVersion ||
            publication.RequestVersion != requestVersion || requestVersion != _campaignEntryNavigationRequestVersion ||
            publication.BeforeNavigationVersion != beforeNavigationVersion ||
            publication.AfterNavigationVersion != _campaignEntryNavigationVersion ||
            !ReferenceEquals(publication.BeforeCatalog, beforeCatalog) ||
            !ReferenceEquals(publication.AfterCatalog, CampaignEditor.CampaignEntryCatalog) ||
            !ReferenceEquals(publication.AfterSnapshot, LibrarySnapshot) ||
            !ReferenceEquals(publication.Project, project) || !ReferenceEquals(project, CampaignEditor.CurrentProject) ||
            publication.Session != session || session != ProjectSession || HasPendingEditorChanges ||
            publication.ProfileIdentity != BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile()))
            return false;
        var resolvedEntryIdentity = CampaignEditor.ResolveCheckedCampaignEntryIdentity(entryIdentity);
        var entries = CampaignEditor.CampaignEntries.Where(item => item.Identity == resolvedEntryIdentity).Take(2).ToArray();
        if (entries.Length != 1) return false;
        if (!hadRow) { entry = entries[0]; return true; }
        if (destination is null || !PathsEqual(destination.Asset.SourcePath, destination.SourcePath) ||
            project is null || !project.ProjectScenarioFiles.Any(asset => ReferenceEquals(asset, destination.Asset))) return false;
        string? documentId = destination.DocumentId;
        if (ReferenceEquals(destination.Asset, publication.EditedAsset) && destination.DocumentIndex >= 0)
        {
            if (destination.DocumentIndex >= publication.BeforeDocumentIds.Length ||
                publication.BeforeDocumentIds[destination.DocumentIndex] != destination.DocumentId ||
                destination.Asset.Documents.ElementAtOrDefault(destination.DocumentIndex) is not { } document) return false;
            documentId = document.InternalId;
        }
        var matches = CampaignEditor.ScenarioFiles.Where(item => item.CampaignEntry?.Identity == resolvedEntryIdentity &&
            ReferenceEquals(item.Asset, destination.Asset) && item.ScenarioDocumentInternalId == documentId &&
            (ReferenceEquals(destination.Asset, publication.EditedAsset) && destination.DocumentIndex >= 0 ||
                item.Membership is not null || item.Navigation is not null || item.NeedsCampaignMembershipRefresh && destination.DocumentIndex >= 0 &&
                    item.PendingScenarioDocumentIndex == destination.DocumentIndex)).Take(2).ToArray();
        if (matches.Length != 1) return false;
        entry = entries[0]; row = matches[0]; return true;
    }
}
