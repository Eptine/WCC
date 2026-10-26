using System;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    // UI save routing only; each recipe still needs its own binding and fresh Core write proof.
    internal bool HasWccSourceRecipeDrafts => HasWccSideFlagDrafts || HasWccEventDrafts ||
        HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft;

    internal string GetPendingSourceChangeStamp()
    {
        lock (_wccInitialFieldRefreshGate)
            return string.Join("\u001e", _sourceHostGeneration, IsReadOnly, _evaluationContext.SemanticKey,
                _musicScheduleSourceDraft?.Text, _wccMusicChanged, _wccMakeFirstTrackIntro, _wccLetIntroFinish,
                string.Join("\u001f", _wccMusicRows.Select(row => row.SourceId + "=" + row.Name)),
                _wccScheduleEdit.Kind, _wccScheduleEdit.PresetInvocation,
                string.Join("\u001f", _wccScheduleEdit.TimeBlocks ?? []), _wccScheduleEdit.Start?.Kind,
                _wccScheduleEdit.Start?.SlotIndex, string.Join(",", _wccScheduleEdit.Start?.RandomSlotIndices ?? []),
                GetPendingWccSideFlagChangeStamp(), GetPendingWccEventChangeStamp(), GetPendingWccSideTopologyChangeStamp(), GetPendingWccLeaderAdditionStamp(), _initialUnitDraftVersion, GetPendingWccStoryChangeStamp());
    }

    internal Func<bool>? PreparePendingChangesDiscard(bool restoreSource, out string? problem)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            problem = null;
            if (_changingWccDifficultyDrafts)
            { problem = "The pending difficulty fields are changing; finish the current input before discarding."; return null; }
            if (IsWccInlineSaveBusy || WccFieldEditSession.IsBusy ||
                WccFieldEditSession.Recovery is not null)
            { problem = "Finish the scenario save or recovery before discarding changes."; return null; }
            var asset = _currentScenarioAsset;
            var document = SelectedScenario?.Document;
            var generation = _sourceHostGeneration;
            var baseline = asset?.RawTextSnapshot;
            var scenarioIndex = SelectedScenario is null ? 0 : Scenarios.IndexOf(SelectedScenario);
            var sideIndex = GetAuthoredSideIndex(SelectedSide);
            var restored = restoreSource && baseline is not null ? ScenarioImporter.ImportAllFromText(baseline) : null;
            if (restoreSource && (asset is null || baseline is null || restored is null))
            { problem = "The scenario's saved source baseline is unavailable. Its edits remain open."; return null; }
            return () =>
            {
                lock (_wccInitialFieldRefreshGate)
                {
                    if (_changingWccDifficultyDrafts || IsWccInlineSaveBusy || generation != _sourceHostGeneration || !ReferenceEquals(asset, _currentScenarioAsset) ||
                        !ReferenceEquals(document, SelectedScenario?.Document) || restoreSource && baseline != asset?.RawTextSnapshot) return false;
                    DiscardWccInlineDrafts();
                    DiscardWccMusicScheduleDrafts();
                    DiscardWccSideFlagDrafts();
                    DiscardWccEventDrafts();
                    DiscardWccSideTopologyDrafts();
                    DiscardWccLeaderAdditionDrafts();
                    DiscardWccInitialUnitDrafts();
                    DiscardWccStoryDraft();
                    if (restored is not null)
                    {
                        using var refresh = DeferPreviewRefresh();
                        InvalidateSourceEditingSession();
                        RunUiSync(() =>
                        {
                            SelectedScenario = null;
                            asset!.Documents.Clear();
                            asset.Documents.AddRange(restored);
                            asset.MacroDefinitions.Clear();
                            asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(baseline!));
                            LoadFromExistingScenarioAsset(asset);
                            SelectedScenario = Scenarios.ElementAtOrDefault(scenarioIndex) ?? Scenarios.FirstOrDefault();
                            SelectedSide = Sides.FirstOrDefault(side => GetAuthoredSideIndex(side) == sideIndex) ?? Sides.FirstOrDefault();
                        });
                        RefreshMapPreview();
                    }
                    return ReferenceEquals(asset, _currentScenarioAsset);
                }
            };
        }
    }
}
