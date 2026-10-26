using System;
using System.Collections.Generic;
using System.Linq;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    internal IReadOnlyList<PendingChangeRemovalProposal> CapturePendingChangeRemovals()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var proposals = new List<PendingChangeRemovalProposal>();
            foreach (var draft in _wccInlineDrafts.ToArray())
                Add(new("Scenario", draft.Label, AppShellViewModel.DraftDetail(draft)),
                    () => _wccInlineDrafts.Any(current => ReferenceEquals(current, draft)), GetWccInlineBindingProblem,
                    () =>
                    {
                        foreach (var row in WccScenarioFieldEditors.Concat(WccSideFieldEditors).Concat(WccLeaderFieldEditors))
                        {
                            var shown = FindWccInlineDraft(GetWccInitialFieldRoute(row.Owner, row.Key));
                            if (ReferenceEquals(shown, draft) || draft.Difficulty is { } difficulty &&
                                difficulty.Slots.Values.Any(slot => ReferenceEquals(shown, slot))) row.ResetInputState();
                        }
                        RevertWccInlineDraft(draft);
                    });
            if (HasWccMusicScheduleDrafts)
            {
                var version = _musicScheduleDraftVersion;
                Add(new("Scenario", "Music and schedule", "Pending playlist and time-of-day edits are one source proposal."),
                    () => HasWccMusicScheduleDrafts && version == _musicScheduleDraftVersion,
                    () => _musicScheduleSourceBinding is { } binding ? GetWccMusicScheduleBindingProblem(binding)
                        : "The music and schedule proposal has no captured source binding. Use global Discard to reload its saved baseline.",
                    DiscardWccMusicScheduleDrafts);
            }
            foreach (var draft in _sideFlagDrafts.ToArray())
                Add(new("Scenario", draft.Label, draft.Summary), () => _sideFlagDrafts.Contains(draft),
                    () => _sideFlagSourceBinding is { } binding ? GetSideFlagBindingProblem(binding)
                        : "This flag proposal has no captured source binding. Use global Discard to reload its saved baseline.",
                    () => RevertWccSideFlagDraft(draft.Identity));
            foreach (var draft in _eventDrafts.ToArray())
                Add(new("Scenario", draft.Label, draft.Summary), () => _eventDrafts.Contains(draft),
                    () => _eventSourceBinding is { } binding ? EventBindingProblem(binding)
                        : "This event proposal has no captured source binding. Use global Discard to reload its saved baseline.",
                    () => RevertWccEventDraft(draft.Identity));
            foreach (var draft in _sideTopologyDrafts.ToArray())
                Add(new("Scenario", draft.Label, draft.Summary), () => _sideTopologyDrafts.Contains(draft),
                    () => _sideTopologySourceBinding is { } binding ? SideTopologyBindingProblem(binding)
                        : "This side proposal has no captured source binding. Use global Discard to reload its saved baseline.",
                    () => RevertWccSideTopologyDraft(draft.Identity));
            foreach (var leader in _pendingLeaders.ToArray())
            {
                var summary = WccLeaderAdditionDrafts.Single(draft => draft.Identity == leader.Identity);
                Add(new("Scenario", summary.Label, summary.Summary), () => _pendingLeaders.Contains(leader),
                    LeaderAdditionBindingProblem, () => RevertWccLeaderAdditionDraft(leader.Identity));
            }
            foreach (var draft in _initialUnitDrafts.ToArray())
                Add(new("Scenario", "Initial unit", draft.Spec?.Values.GetValueOrDefault("id") ?? "Remove unit"),
                    () => _initialUnitDrafts.Contains(draft), InitialUnitBindingProblem, () => RevertWccInitialUnitDraft(draft.Identity));
            if (_storyEdit is { } story)
                Add(new("Scenario", "Introduction", "Story slides, shared helper edits and their deferred media are one proposal."),
                    () => ReferenceEquals(story, _storyEdit),
                    () => _storySourceBinding is { } binding ? StoryBindingProblem(binding)
                        : "The Introduction proposal has no captured source binding. Use global Discard to reload its saved baseline.",
                    DiscardWccStoryDraft);
            return proposals;

            void Add(PendingEditorChange change, Func<bool> exists, Func<string?> bindingProblem, Action reset)
            {
                string? Problem() => ScenarioPendingRemovalProblem() ?? bindingProblem() ??
                    (!exists() ? "This scenario draft changed. Select its current pending row." : null);
                proposals.Add(new(change, Problem, () =>
                {
                    lock (_wccInitialFieldRefreshGate)
                    {
                        if (Problem() is not null) return false;
                        reset();
                        return !exists();
                    }
                }));
            }
        }
    }

    private string? ScenarioPendingRemovalProblem()
    {
        if (IsWccInlineSaveBusy || WccFieldEditSession.IsBusy ||
            WccFieldEditSession.Recovery is not null || _collectedSaveRecoveryPending)
            return "Finish scenario Save or source recovery before removing a pending change.";
        if (IsSynchronizingUi || _changingWccDifficultyDrafts || _fieldDisplayDeferralDepth > 0 || _flushingFieldDisplay)
            return "Finish the current scenario update before removing a pending change.";
        return null;
    }
}
