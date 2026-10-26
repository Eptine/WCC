using System;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    internal event Action? CollectedSaveAuthorityChanging;
    private bool _collectedSaveRecoveryPending;
    internal void BlockCollectedSaveForRecovery()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            _collectedSaveRecoveryPending = true;
            InvalidateSourceEditingSession();
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(CanEditDocument));
            RaiseInitialScalarFieldsChanged();
        }
    }

    internal WccCollectedSourceEditor? CaptureCollectedScenarioSave(out string? problem)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            problem = null;
            if (!HasWccInlineDrafts && !HasWccMusicScheduleDrafts && !HasWccSideFlagDrafts && !HasWccEventDrafts && !HasWccSideTopologyDrafts && !HasWccLeaderAdditionDrafts && !HasWccInitialUnitDrafts && !HasWccStoryDraft) return null;
            if (_changingWccDifficultyDrafts)
            { problem = "The pending difficulty fields are changing; finish the current input before saving."; return null; }
            if (IsWccInlineSaveBusy || WccFieldEditSession.IsBusy)
            { problem = "Finish the current scenario operation before saving."; return null; }
            problem = _wccInlineDrafts.FirstOrDefault(draft => draft.ValidationError is not null)?.ValidationError
                ?? GetWccInlineBindingProblem();
            if (problem is not null) return null;
            WccMusicScheduleDraftContribution? music = null;
            if (HasWccMusicScheduleDrafts && !TryCaptureWccMusicScheduleContribution(out music, out problem)) return null;
            WccSideFlagDraftContribution? flags = null;
            if (HasWccSideFlagDrafts && !TryCaptureWccSideFlagContribution(out flags, out problem)) return null;
            WccEventDraftContribution? events = null;
            if (HasWccEventDrafts && !TryCaptureWccEventContribution(out events, out problem)) return null;
            WccSideTopologyDraftContribution? topology = null;
            WccLeaderAdditionDraftContribution? leaders = null;
            InitialUnitContribution? units = null;
            var unitsCompleted = false;
            WccScenarioStoryDraftContribution? story = null;
            var settings = MergeCollectedInitialSettings(music?.InitialSettings, flags?.InitialSettings, out problem);
            if (problem is not null) return null;
            settings = MergeCollectedInitialSettings(settings, events?.InitialSettings, out problem);
            if (problem is not null) return null;
            // Merging independent flags/events preserves the music/schedule
            // planner's exact full-evaluation evidence for that same source only.
            if (settings is not null && music is not null && NeedsCapturedMusicScheduleSettings &&
                _musicScheduleCapturedProjection is { } capturedSettings)
                settings = settings.WithCapturedProjection(capturedSettings);
            if (_currentScenarioAsset is not { } asset || SelectedScenario is not { } selected || IsReadOnly && !CanUseSelectedScenarioDrafts ||
                !_sourceHostSession.CanWrite || _sourceEvaluationPlan is null || _macroLibrarySnapshot is null ||
                _staticRootRevisionSelection is not { } root || !ReferenceEquals(root.Snapshot, _macroLibrarySnapshot) ||
                _pristineLoadedSourceText is not { } sourceText || _hasAuthoredSourceChanges || _externalSourceChangesPending ||
                !IsPathInsideRoot(_currentSourcePath ?? LoadedPath, _sourceHostSession.RootPath) ||
                IsProtectedWesnothInstallPath(_currentSourcePath ?? LoadedPath))
            { problem = "The scenario needs a current writable source and campaign binding before combined Save."; return null; }
            var source = new WmlStaticSource((_currentSourcePath ?? LoadedPath)!, sourceText);
            if (_storyEdit is { } pendingStory)
            {
                if (_storySourceBinding is not { } storyBinding || StoryBindingProblem(storyBinding) is not null ||
                    !SameAuthoredFlagSource(source, pendingStory.Source) || source.Text != pendingStory.Source.Text)
                { problem = "The pending Story no longer belongs to this exact current scenario source. Its draft is retained."; return null; }
                // All categories share this exact immutable publication. The Story's
                // upload receipts remain bound to their original snapshot.
                source = pendingStory.Source;
            }
            if (HasWccSideTopologyDrafts && !TryCaptureWccSideTopologyContribution(source, out topology, out problem)) return null;
            if (HasWccLeaderAdditionDrafts && !TryCaptureWccLeaderAdditionContribution(source, out leaders, out problem)) return null;
            if (HasWccInitialUnitDrafts && !TryCaptureInitialUnitContribution(source, out units, out problem)) return null;
            if (HasWccStoryDraft && !TryCaptureWccStoryContribution(source, out story, out problem)) return null;
            if (!TryCaptureWccDifficultyFields(source, out var difficultyFields, out problem)) return null;
            var state = _wccInlineDraftState;
            if (state is null || settings is not null || difficultyFields.Count != 0 || topology is not null || leaders is not null || units is not null || story is not null)
            {
                if (!HasCurrentStaticScalarProjection || _staticScenarioOwner is not { } owner)
                { problem = "The scenario has no unique captured initial owner for combined Save. Reload its source."; return null; }
                state = new(CurrentWccFieldProjectIdentity, _sourceHostSession.RootPath!, root.RootRevisionId.Value.ToString(),
                    true, _macroLibrarySnapshot, root.RootRevisionId, source, _sourceEvaluationPlan, _evaluationContext,
                    owner, _wesnothSourceAddonsPath, _wesnothSourceProfileIdentity)
                { EntryReview = CurrentCampaignEntryReview };
            }
            var drafts = _wccInlineDrafts.ToArray();
            var fields = drafts.Where(draft => draft.Difficulty is null).Select(draft => new WccScenarioFieldEditProposal(draft.Route, draft.Operation,
                draft.Route.Key == "turns" ? NormalizeWccInlineRequestedValue(draft.Text) : draft.Text)
                { ValueInputKind = draft.ValueInputKind }).ToArray();
            var document = selected.Document;
            var host = _sourceHostSession;
            var snapshot = _macroLibrarySnapshot;
            var plan = _sourceEvaluationPlan;
            var profile = _wesnothSourceProfileIdentity;
            var scenarioIndex = Scenarios.IndexOf(selected);
            var sideIndex = GetAuthoredSideIndex(SelectedSide);
            var selectedOwner = SelectedSide?.IsGenerated == true ? SelectedStaticSideOwner : null;
            var selectedLeader = SelectedWccInitialLeader;
            var pendingLeader = selectedLeader is null ? null : FindPendingLeader(selectedLeader.Owner);
            var pendingLeaderIndex = pendingLeader is null ? -1 : _pendingLeaders.Where(leader => leader.Snapshot.SameOwner(pendingLeader.Snapshot.SideOwner)).ToList().IndexOf(pendingLeader);
            bool IsSourceCurrent()
            {
                lock (_wccInitialFieldRefreshGate)
                    return ReferenceEquals(asset, _currentScenarioAsset) && ReferenceEquals(document, SelectedScenario?.Document) &&
                        ReferenceEquals(host, _sourceHostSession) && ReferenceEquals(snapshot, _macroLibrarySnapshot) &&
                        ReferenceEquals(plan, _sourceEvaluationPlan) && _staticRootRevisionSelection?.RootRevisionId == root.RootRevisionId &&
                        (!IsReadOnly || CanUseSelectedScenarioDrafts) && _sourceHostSession.CanWrite && !_hasAuthoredSourceChanges && !_externalSourceChangesPending &&
                        profile == _wesnothSourceProfileIdentity && drafts.SequenceEqual(_wccInlineDrafts) &&
                        source.Text == _pristineLoadedSourceText && source.Text == asset.RawTextSnapshot &&
                        (drafts.Length == 0 || GetWccInlineBindingProblem() is null) &&
                        (units is null || (unitsCompleted ? !HasWccInitialUnitDrafts : IsCurrentInitialUnitContribution(units)));
            }
            bool IsCurrent() => IsSourceCurrent() &&
                (music is null || IsCurrentWccMusicScheduleContribution(music)) &&
                (flags is null || IsCurrentWccSideFlagContribution(flags)) &&
                (events is null || IsCurrentWccEventContribution(events)) &&
                (topology is null || IsCurrentWccSideTopologyContribution(topology)) &&
                (leaders is null || IsCurrentWccLeaderAdditionContribution(leaders)) &&
                (story is null || IsCurrentWccStoryContribution(story));
            void Begin(string status)
            {
                using var displayRefresh = DeferFieldDisplayRefresh();
                lock (_wccInitialFieldRefreshGate)
                {
                    _isWccInlineSaveBusy = true;
                    _wccInlineSaveProgressVersion++;
                    _wccInlinePreviewProgress = null;
                    _wccInlineSaveProgressStatus = _wccInlineDraftStatus = Status = status;
                    RaiseWccInlineDraftsChanged();
                    RaiseInitialScalarFieldsChanged();
                }
            }
            bool Publish(string text)
            {
                lock (_wccInitialFieldRefreshGate)
                {
                    if (!IsCurrent()) return false;
                    if (music is not null && !CompleteWccMusicScheduleContribution(music)) return false;
                    if (!IsSourceCurrent() || (music is not null && HasWccMusicScheduleDrafts) ||
                        (flags is not null && !IsCurrentWccSideFlagContribution(flags)) ||
                        (events is not null && !IsCurrentWccEventContribution(events)) ||
                        (topology is not null && !IsCurrentWccSideTopologyContribution(topology)) ||
                        (leaders is not null && !IsCurrentWccLeaderAdditionContribution(leaders)) ||
                        (story is not null && !IsCurrentWccStoryContribution(story))) return false;
                    if (flags is not null && !CompleteWccSideFlagContribution(flags)) return false;
                    // Completing submitted versions raises UI callbacks. A
                    // source rebind or newly entered draft belongs to the new editor.
                    if (!IsSourceCurrent() || (music is not null && HasWccMusicScheduleDrafts) ||
                        (flags is not null && HasWccSideFlagDrafts) ||
                        (events is not null && !IsCurrentWccEventContribution(events)) ||
                        (topology is not null && !IsCurrentWccSideTopologyContribution(topology)) ||
                        (leaders is not null && !IsCurrentWccLeaderAdditionContribution(leaders)) ||
                        (story is not null && !IsCurrentWccStoryContribution(story))) return false;
                    if (events is not null && !CompleteWccEventContribution(events)) return false;
                    if (!IsSourceCurrent() || (music is not null && HasWccMusicScheduleDrafts) ||
                        (flags is not null && HasWccSideFlagDrafts) ||
                        (events is not null && HasWccEventDrafts) ||
                        (topology is not null && !IsCurrentWccSideTopologyContribution(topology)) ||
                        (leaders is not null && !IsCurrentWccLeaderAdditionContribution(leaders)) ||
                        (story is not null && !IsCurrentWccStoryContribution(story))) return false;
                    if (topology is not null && !CompleteWccSideTopologyContribution(topology)) return false;
                    if (!IsSourceCurrent() || (music is not null && HasWccMusicScheduleDrafts) ||
                        (flags is not null && HasWccSideFlagDrafts) || (events is not null && HasWccEventDrafts) ||
                        (topology is not null && HasWccSideTopologyDrafts) ||
                        (leaders is not null && !IsCurrentWccLeaderAdditionContribution(leaders)) ||
                        (story is not null && !IsCurrentWccStoryContribution(story))) return false;
                    if (leaders is not null && !CompleteWccLeaderAdditionContribution(leaders)) return false;
                    if (!IsSourceCurrent() || (music is not null && HasWccMusicScheduleDrafts) ||
                        (flags is not null && HasWccSideFlagDrafts) || (events is not null && HasWccEventDrafts) ||
                        (topology is not null && HasWccSideTopologyDrafts) || (leaders is not null && HasWccLeaderAdditionDrafts) ||
                        (story is not null && !IsCurrentWccStoryContribution(story))) return false;
                    if (story is not null && !CompleteWccStoryContribution(story)) return false;
                    if (units is not null)
                    {
                        if (!CompleteInitialUnitContribution(units)) return false;
                        unitsCompleted = true;
                    }
                    if (!IsSourceCurrent() || (music is not null && HasWccMusicScheduleDrafts) ||
                        (flags is not null && HasWccSideFlagDrafts) || (events is not null && HasWccEventDrafts) ||
                        (topology is not null && HasWccSideTopologyDrafts) || (leaders is not null && HasWccLeaderAdditionDrafts) ||
                        (story is not null && HasWccStoryDraft)) return false;
                    _wccInlineDrafts.Clear();
                    _wccInlineDraftState = null;
                    _wccInlineDraftAsset = null;
                    _wccInlineInstallPath = null;
                    _wccInlineDraftStatus = null;
                    var reviewLines = drafts.Select(draft => draft.Difficulty is not null ? draft.Summary : draft.Label + ": " +
                        (draft.Route.Value ?? draft.Route.SourceValue ?? "absent") + " → " +
                        (draft.Operation == WmlFieldOperation.Remove ? "remove local override" : draft.Text)).ToList();
                    if (music?.InitialSettings.Music is { } playlist)
                        reviewLines.Add("Music: " + string.Join(", ", playlist.Tracks.Select(track => track.Name)));
                    if (music?.InitialSettings.Schedule is { } schedule)
                        reviewLines.Add("Schedule: " + schedule.Kind + (schedule.PresetInvocation is { } preset ? " " + preset : ""));
                    if (flags is not null)
                        reviewLines.AddRange(flags.InitialSettings.SideFlags.Select(flag => "Flag preset: " + flag.InvocationText));
                    if (events is not null)
                        reviewLines.AddRange(events.InitialSettings.Events.Select(edit => "Event: " + edit.Kind));
                    if (topology is not null)
                        reviewLines.AddRange(topology.Summaries);
                    if (leaders is not null)
                        reviewLines.AddRange(leaders.Summaries);
                    if (story is not null)
                        reviewLines.Add("Story: introduction slides and appearance");
                    if (units is not null)
                        reviewLines.AddRange(units.Edits.Select(edit => "Initial unit: " + edit.Kind));
                    _wccInlineReviewSummary = "All collected changes checked in every declared campaign context." +
                        Environment.NewLine + string.Join(Environment.NewLine, reviewLines);
                    ReloadSavedScenarioSource(asset, text, scenarioIndex, sideIndex);
                    RaiseWccInlineDraftsChanged();
                    ScenarioSourceSaved?.Invoke(this, EventArgs.Empty);
                    return ReferenceEquals(asset, _currentScenarioAsset) && asset.RawTextSnapshot == text;
                }
            }
            void RememberSelection(WccProjectSourceEditCandidate candidate)
            {
                lock (_wccInitialFieldRefreshGate)
                {
                    var file = candidate.Files.Single(file => PathsEqual(file.Source.Path, source.Path));
                    if (!ReferenceEquals(asset, _currentScenarioAsset) || asset.RawTextSnapshot != file.CandidateSource.Text) return;
                    if (selectedOwner is not null)
                    {
                        _pendingWccSideSelection = owner => SameCollectedOwner(selectedOwner, owner, candidate);
                        _pendingWccSideSelectionPath = file.CandidateSource.Path;
                    }
                    if (selectedLeader is not null)
                    {
                        _pendingWccInitialLeaderSelection = pendingLeader is null
                            ? owner => SameCollectedOwner(selectedLeader.Owner, owner, candidate)
                            : owner => owner.Parent is { } parent && SameCollectedOwner(pendingLeader.Snapshot.SideOwner, parent, candidate)
                                && parent.DirectChildren.Where(child => child.TagName == "leader").ToList().IndexOf(owner) == pendingLeader.Snapshot.ExistingLeaderCount + pendingLeaderIndex;
                        _pendingWccInitialLeaderPath = file.CandidateSource.Path;
                        _pendingWccInitialLeaderHash = file.CandidateSource.ContentHash;
                        _pendingWccInitialLeaderAsset = asset;
                        _pendingWccInitialLeaderIsInline = selectedLeader.IsLegacyInline;
                    }
                }
            }
            void End()
            {
                lock (_wccInitialFieldRefreshGate)
                {
                    _isWccInlineSaveBusy = false;
                    RaiseWccInlineDraftsChanged();
                    RaiseEditingStateChanged();
                    RaiseInitialScalarFieldsChanged();
                }
            }
            void RevokeCompetingAuthority()
            {
                lock (_wccInitialFieldRefreshGate)
                {
                    WccFieldEditSession.Invalidate();
                }
            }
            return new(state, new(source, fields, settings, difficultyFields: difficultyFields, sideTopology: topology?.Edit, leaderAdditions: leaders?.Edits) { StoryEdit = story?.Edit, UnitInstances = units?.Edits ?? [] }, IsCurrent, RevokeCompetingAuthority,
                Begin, Publish, RememberSelection, End);
        }
    }

    private static WccScenarioInitialSettingsEdit? MergeCollectedInitialSettings(
        WccScenarioInitialSettingsEdit? music, WccScenarioInitialSettingsEdit? flags, out string? problem)
    {
        problem = null;
        if (music is null) return flags;
        if (flags is null) return music;
        if (!SameAuthoredFlagSource(music.ScenarioRange.Source, flags.ScenarioRange.Source) ||
            music.ScenarioRange.StartOffset != flags.ScenarioRange.StartOffset || music.ScenarioRange.Length != flags.ScenarioRange.Length ||
            music.SourceTextDomain != flags.SourceTextDomain)
        {
            problem = "The pending settings and event drafts no longer refer to the same exact scenario source. Their drafts are retained.";
            return null;
        }
        return new(music.ScenarioRange, music.Definitions.Concat(flags.Definitions).Distinct(), music.SourceTextDomain,
            music.Music ?? flags.Music, music.Schedule ?? flags.Schedule, music.SideFlags.Concat(flags.SideFlags),
            music.Events.Concat(flags.Events));
    }

    private CollectedSourcePreviewDeferral? _collectedSourcePreviewDeferral;

    internal IDisposable DeferCollectedSourcePreview(string savedText)
    {
        if (_collectedSourcePreviewDeferral is not null)
            throw new InvalidOperationException("A collected source preview is already deferred.");
        CancelBackgroundOperations();
        return _collectedSourcePreviewDeferral = new(this, savedText);
    }

    private sealed class CollectedSourcePreviewDeferral(ScenarioWindowViewModel owner, string savedText) : IDisposable
    {
        private readonly object? _asset = owner._currentScenarioAsset;
        private readonly string? _path = owner._currentSourcePath ?? owner.LoadedPath;
        private readonly string? _baseline = owner._pristineLoadedSourceText;
        private readonly string _context = owner._evaluationContext.SemanticKey;
        public bool RefreshPending { get; set; }
        public bool IsCurrent => ReferenceEquals(_asset, owner._currentScenarioAsset) &&
            PathsEqual(_path, owner._currentSourcePath ?? owner.LoadedPath) &&
            _context == owner._evaluationContext.SemanticKey &&
            (owner._pristineLoadedSourceText == _baseline || owner._pristineLoadedSourceText == savedText) &&
            !owner._hasAuthoredSourceChanges && !owner._externalSourceChangesPending;

        public void Dispose()
        {
            if (!ReferenceEquals(owner._collectedSourcePreviewDeferral, this)) return;
            owner._collectedSourcePreviewDeferral = null;
            if (RefreshPending && IsCurrent) owner.RefreshMapPreview();
        }
    }

    private static bool SameCollectedOwner(WmlStaticScalarOwner before, WmlStaticScalarOwner after,
        WccProjectSourceEditCandidate candidate)
    {
        bool SameRange(WmlStaticSourceRange? old, WmlStaticSourceRange? fresh)
        {
            if (old is null || fresh is null) return false;
            var mapped = candidate.MapRange(old);
            return mapped is not null && PathsEqual(mapped.Source.Path, fresh.Source.Path) &&
                mapped.StartOffset == fresh.StartOffset && mapped.Length == fresh.Length;
        }
        if (!SameRange(before.OpeningOrigin, after.OpeningOrigin) ||
            before.OpeningExpansionStack.Count != after.OpeningExpansionStack.Count) return false;
        for (var index = 0; index < before.OpeningExpansionStack.Count; index++)
        {
            var old = before.OpeningExpansionStack[index];
            var fresh = after.OpeningExpansionStack[index];
            if (old.MacroName != fresh.MacroName || !SameRange(old.Definition, fresh.Definition) ||
                old.InvocationOrigins.Count != fresh.InvocationOrigins.Count) return false;
            for (var origin = 0; origin < old.InvocationOrigins.Count; origin++)
                if (!SameRange(old.InvocationOrigins[origin], fresh.InvocationOrigins[origin])) return false;
        }
        return true;
    }
}
