using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    public WccScenarioFieldEditSession WccFieldEditSession { get; } = new();
    private readonly Dictionary<(WmlStaticScalarOwner Owner, string Key), WccScenarioFieldRoute> _wccFieldRoutes = new();
    private WmlStaticSource? _wccFieldSource;
    private Func<WmlStaticScalarOwner, bool>? _pendingWccSideSelection;
    private string? _pendingWccSideSelectionPath;

    private void ClearPendingWccFieldSelection()
    {
        _pendingWccSideSelection = null;
        _pendingWccSideSelectionPath = null;
        ClearPendingWccInitialLeaderSelection();
    }

    private void InitializeWccFieldEditing()
    {
        WccFieldEditSession.Changed += () =>
        {
            if (WccFieldEditSession.Recovery is { } recovery)
            {
                SourceEditingStatus = "Saving requires source recovery. " + recovery.ErrorMessage +
                    " Source: " + recovery.SourcePath + " Backup: " + recovery.RecoveryBackupPath +
                    " Candidate: " + recovery.RecoveryCandidatePath;
                OnPropertyChanged(nameof(SourceEditingStatus));
            }
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(CanEditDocument));
            RaiseEditingStateChanged();
            RaiseInitialScalarFieldsChanged();
        };
    }

    private void InvalidateWccFieldEditing()
    {
        CollectedSaveAuthorityChanging?.Invoke();
        lock (_wccInitialFieldRefreshGate)
        {
            _wccFieldRoutes.Clear();
            _wccFieldSource = null;
            _wccDifficultySnapshots.Clear();
            _wccDifficultyInsertionSnapshots.Clear();
        }
        WccFieldEditSession.Invalidate();
        RaiseWccSideTopologyCapabilitiesChanged();
        RaiseWccStoryCapabilitiesChanged();
        RaiseWccInlineDraftsChanged();
    }

    public WccScenarioFieldRoute? GetWccInitialFieldRoute(string key)
    {
        var owner = key == "turns" ? HasCurrentStaticScalarProjection ? _staticScenarioOwner : null : SelectedStaticSideOwner;
        return owner is null ? null : GetWccInitialFieldRoute(owner, key);
    }

    public WccScenarioFieldRoute? GetWccInitialFieldRoute(WmlStaticScalarOwner owner, string key)
    {
        // Display getters and background preview publication share this cache.
        // Keep its source and routes consistent with the existing refresh gate.
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccPendingLeaderOwner(owner)) return GetPendingLeaderRoute(owner, key);
            var path = _currentSourcePath ?? LoadedPath;
            if (!IsCurrentWccInitialFieldOwner(owner) || _pristineLoadedSourceText is null || string.IsNullOrWhiteSpace(path) ||
                _hasAuthoredSourceChanges || _externalSourceChangesPending)
                return null;
            if (_wccFieldRoutes.TryGetValue((owner, key), out var cached) &&
                cached.ContextSemanticKey == _evaluationContext.SemanticKey &&
                string.Equals(cached.Source.Text, _pristineLoadedSourceText, StringComparison.Ordinal) && PathsEqual(cached.Source.Path, path))
                return cached;
            _wccFieldSource ??= new WmlStaticSource(path, _pristineLoadedSourceText);
            var route = WccScenarioFieldRouteAnalyzer.Analyze(owner, key, _wccFieldSource, _evaluationContext);
            _wccFieldRoutes[(owner, key)] = route;
            return route;
        }
    }

    internal bool IsCurrentWccInitialFieldOwner(WmlStaticScalarOwner owner)
    {
        if (IsWccPendingLeaderOwner(owner)) return LeaderAdditionBindingProblem() is null && HasCurrentStaticScalarProjection;
        if (!HasCurrentStaticScalarProjection) return false;
        // The projection may contain a campaign root above [scenario]. Match
        // the current scenario in the actual owner chain instead of any ids.
        for (var current = owner; current is not null; current = current.Parent)
            if (ReferenceEquals(current, _staticScenarioOwner)) return true;
        return false;
    }

    internal static ScenarioInitialFieldDefinition? WccInitialFieldDefinition(WccScenarioFieldRoute route) =>
        ScenarioInitialFieldCatalog.TryGet(route.Owner.TagName, route.Key, out var definition) ? definition :
        route.Owner.TagName == "side" && ScenarioInitialFieldCatalog.LegacyInlineKeys.Contains(route.Key) &&
        ScenarioInitialFieldCatalog.TryGet("leader", route.Key, out definition) ? definition : null;

    internal bool CanReviewWccInitialField(WmlStaticScalarOwner owner, string key) => IsWccPendingLeaderOwner(owner)
        ? PendingLeaderFieldProblem(owner) is null && ScenarioInitialFieldCatalog.TryGet("leader", key, out var definition) && definition.IsEditable
        : WccFieldEditBlockReason(key, GetWccInitialFieldRoute(owner, key)) is null;

    internal string GetWccInitialFieldEditStatus(WmlStaticScalarOwner owner, string key)
    {
        if (IsWccPendingLeaderOwner(owner)) return PendingLeaderFieldProblem(owner) ?? "This new leader and its fields are pending. Save checks them together in every campaign difficulty.";
        var route = GetWccInitialFieldRoute(owner, key);
        return WccFieldEditBlockReason(key, route) ?? route!.Diagnostic +
            " Save checks the exact source and every campaign difficulty together.";
    }

    public bool CanReviewWccInitialField(string key) => WccFieldEditBlockReason(key, GetWccInitialFieldRoute(key)) is null;
    public string GetWccInitialFieldEditStatus(string key)
    {
        var route = GetWccInitialFieldRoute(key);
        return WccFieldEditBlockReason(key, route) ?? route!.Diagnostic +
            " WCC checks the actual campaign contexts and exact source again before saving.";
    }

    private string? WccFieldEditBlockReason(string key, WccScenarioFieldRoute? route)
    {
        if (LeaderAdditionBindingProblem() is { } leaderProblem) return leaderProblem;
        if (_fieldDisplayDeferralDepth > 0)
            return "The selected scenario fields are being synchronized.";
        if (GetWccDifficultyDraftBindingProblem() is { } difficultyProblem) return difficultyProblem;
        if (HasWccEventDrafts && (_eventSourceBinding is not { } eventBinding || EventBindingProblem(eventBinding) is not null))
            return "The event source binding changed. Your drafts are retained; reload their source.";
        if (HasWccSideFlagDrafts && (_sideFlagSourceBinding is not { } flagBinding || GetSideFlagBindingProblem(flagBinding) is not null))
            return "The flag preset source binding changed. Your drafts are retained; reload their source.";
        if (HasWccMusicScheduleDrafts && !IsCurrentWccMusicScheduleSource())
            return "The music or schedule source binding changed. Your drafts are retained; reload their source.";
        if (HasWccStoryDraft && (_storySourceBinding is not { } storyBinding || StoryBindingProblem(storyBinding) is not null))
            return "The Story source binding changed. Your drafts are retained; reload their source.";
        if (WccFieldEditSession.Recovery is not null)
            return "A previous save requires source recovery before editing.";
        if (_hasAuthoredSourceChanges || _externalSourceChangesPending)
            return "Save or reload pending source changes before editing the resolved field.";
        if (route is null) return StaticInitialValuesStatus ?? "The field has no current resolved source. Use authored RAW source.";
        if (WccSideTopologyOwnerProblem(route.Owner) is { } topologyProblem) return topologyProblem;
        if (_isReadOnly || IsBoundedScenarioInspection || _collectedSaveRecoveryPending || !_sourceHostSession.CanWrite)
            return "This source is open read-only. Its resolved value and RAW source remain available for inspection.";
        if (_campaignEntryInspectionOnly && !CanUseSelectedScenarioField(route))
            return "Editing needs the selected campaign's complete current context and unchanged physical source. Inspect its authored source when that binding is unavailable.";
        if (IsReadOnly && !CanUseSelectedScenarioField(route) || !_sourceHostSession.CanWrite)
            return "This source is open read-only. Its resolved value and RAW source remain available for inspection.";
        if (_sourceHostSession.Scope != WccEditorSessionScope.Project || _currentScenarioAsset is null ||
            !IsPathInsideRoot(route.Source.Path, _sourceHostSession.RootPath) || IsProtectedWesnothInstallPath(route.Source.Path))
            return "A WCC field change needs a writable scenario source inside the open campaign project.";
        if (route.Kind == WccScenarioFieldRouteKind.RawOnly || !(route.CanSet || route.CanInsert)) return route.Diagnostic;
        if (key != "turns" && WccInitialFieldDefinition(route) is not { IsEditable: true })
            return "This field is available for inspection. Use authored RAW source for changes.";
        if (WccFieldEditSession.IsBusy || IsWccInlineSaveBusy) return "Finish the current field operation before starting another.";
        if (_macroLibrarySnapshot is null || _staticRootRevisionSelection is not { } root ||
            !ReferenceEquals(root.Snapshot, _macroLibrarySnapshot) || _sourceEvaluationPlan is null)
            return "The campaign needs a current indexed source and context binding before WCC can check a change.";
        if (!string.Equals(_currentScenarioAsset.RawTextSnapshot, route.Source.Text, StringComparison.Ordinal))
            return "Reload the exact scenario source before checking a field change.";
        return null;
    }

    public WccScenarioFieldEditDraft? BeginWccInitialFieldEdit(string key)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            // Selected-entry proposals join the ordinary collected coordinator;
            // this older direct scalar dialog has no selected-entry save scope.
            if (CurrentCampaignEntryReview is not null) return null;
            // Serialize normal input with background display publication. A
            // reentrant callback still sees the active scope and cannot edit it.
            if (_fieldDisplayDeferralDepth > 0 || _flushingFieldDisplay) return null;
            if (HasWccInlineDrafts || HasWccMusicScheduleDrafts || HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft || IsWccInlineSaveBusy)
            { Status = "Use the shared Save for the pending collected changes."; return null; }
            var route = GetWccInitialFieldRoute(key);
            if (WccFieldEditBlockReason(key, route) is { } reason)
            {
                Status = reason;
                return null;
            }
            var state = CaptureWccFieldSessionState(route!);
            WccFieldEditSession.Publish(state);
            return WccFieldEditSession.BeginEdit(route!);
        }
    }

    private WccScenarioFieldEditSessionState CaptureWccFieldSessionState(WccScenarioFieldRoute route)
    {
        var root = _staticRootRevisionSelection!;
        var package = CurrentCampaignEntryReview;
        return new WccScenarioFieldEditSessionState(
            CurrentWccFieldProjectIdentity,
            _sourceHostSession.RootPath!, root.RootRevisionId.Value.ToString(), true,
            _macroLibrarySnapshot!, root.RootRevisionId, route!.Source, _sourceEvaluationPlan,
            _evaluationContext, package is null ? route.Owner : _staticScenarioOwner!, _wesnothSourceAddonsPath, _wesnothSourceProfileIdentity)
        { EntryReview = package };
    }

    private string CurrentWccFieldProjectIdentity =>
        _projectMetadata?.ProjectId.Value.ToString() ?? _sourceHostSession.RootPath!;

    public async Task<WccScenarioFieldEditResult> CommitWccInitialFieldEditAsync(WccScenarioFieldEditDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (HasWccInlineDrafts || HasWccMusicScheduleDrafts || HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft || IsWccInlineSaveBusy)
        {
            WccFieldEditSession.Invalidate();
            return new(WccScenarioFieldEditStatus.InvalidRequest, "Use the shared Save for the pending collected changes.");
        }
        var generation = _sourceHostGeneration;
        var asset = _currentScenarioAsset;
        var scenarioIndex = SelectedScenario is null ? 0 : Scenarios.IndexOf(SelectedScenario);
        var authoredIndex = GetAuthoredSideIndex(SelectedSide);
        var selectedOwner = SelectedSide?.IsGenerated == true ? SelectedStaticSideOwner : null;
        var result = await WccFieldEditSession.CommitAsync(draft, cancellationToken);
        if (!result.IsSuccess || asset is null || generation != _sourceHostGeneration || !ReferenceEquals(asset, _currentScenarioAsset))
            return result;
        try
        {
            // The committed disk change must be reimported even if the spent
            // proposal's cancellation token is cancelled immediately afterwards.
            var text = await File.ReadAllTextAsync(draft.Route.Source.Path);
            if (generation != _sourceHostGeneration || !ReferenceEquals(asset, _currentScenarioAsset)) return result;
            if (selectedOwner is not null && result.Candidate is { } candidate)
            {
                _pendingWccSideSelection = owner => SameMappedOwner(selectedOwner, owner, candidate);
                _pendingWccSideSelectionPath = candidate.CandidateSource.Path;
            }
            await PublishSavedScenarioSourceAsync(asset, text, scenarioIndex, authoredIndex,
                result.Status == WccScenarioFieldEditStatus.SucceededWithCleanupWarning
                    ? "Field change saved. " + result.Diagnostic : "Field change saved; refreshing the WCC preview.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            InvalidateSourceEditingSession();
            Status = "The field change was saved, but the editor could not reload it: " + exception.Message;
        }
        return result;
    }

    private void RestoreWccFieldSelection()
    {
        if (_pendingWccSideSelection is null) return;
        if (!PathsEqual(_pendingWccSideSelectionPath, _currentSourcePath ?? LoadedPath))
        {
            _pendingWccSideSelection = null;
            _pendingWccSideSelectionPath = null;
            return;
        }
        var matches = Sides.Where(row => row.IsGenerated && row.EffectiveOwner is { } owner &&
            _pendingWccSideSelection(owner)).Take(2).ToArray();
        if (matches.Length == 1)
        {
            RunUiSync(() => SelectedSide = matches[0]);
        }
        // One settled publication consumes the restoration request. It must
        // never override a later explicit user selection on the same source.
        _pendingWccSideSelection = null;
        _pendingWccSideSelectionPath = null;
    }

    private static bool SameMappedOwner(WmlStaticScalarOwner old, WmlStaticScalarOwner fresh, WccScenarioFieldCandidate edit)
    {
        bool SameRange(WmlStaticSourceRange? before, WmlStaticSourceRange? after)
        {
            if (before is null || after is null || !PathsEqual(before.Source.Path, after.Source.Path)) return false;
            var start = before.StartOffset;
            var length = before.Length;
            if (PathsEqual(before.Source.Path, edit.Route.Source.Path))
            {
                var end = start + length;
                var changedEnd = edit.ReplacementStart + edit.ReplacementLength;
                var delta = edit.ReplacementText.Length - edit.ReplacementLength;
                if (end <= edit.ReplacementStart) { }
                else if (start >= changedEnd) start += delta;
                else if (start <= edit.ReplacementStart && end >= changedEnd) length += delta;
                else return false;
            }
            return start == after.StartOffset && length == after.Length;
        }
        if (!SameRange(old.OpeningOrigin, fresh.OpeningOrigin) || old.OpeningExpansionStack.Count != fresh.OpeningExpansionStack.Count)
            return false;
        for (var index = 0; index < old.OpeningExpansionStack.Count; index++)
        {
            var before = old.OpeningExpansionStack[index];
            var after = fresh.OpeningExpansionStack[index];
            if (before.MacroName != after.MacroName || !SameRange(before.Definition, after.Definition) ||
                before.InvocationOrigins.Count != after.InvocationOrigins.Count) return false;
            for (var origin = 0; origin < before.InvocationOrigins.Count; origin++)
                if (!SameRange(before.InvocationOrigins[origin], after.InvocationOrigins[origin])) return false;
        }
        return true;
    }
}
