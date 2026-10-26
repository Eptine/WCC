using System;
using System.Globalization;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    internal WmlDefinitionSourcePublication? PrepareSharedMacroSourcePublication(
        string sourcePath, string expectedText, string savedText, out string? problem)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            problem = null;
            if (_currentScenarioAsset is not { } asset ||
                !WmlDefinitionSourcePublication.SamePath(sourcePath, _currentSourcePath ?? LoadedPath)) return null;
            var documents = asset.Documents.ToArray();
            var selected = SelectedScenario?.Document;
            var generation = _sourceHostGeneration;
            var version = EditVersion;
            var host = _sourceHostSession;
            var snapshot = _macroLibrarySnapshot;
            var plan = _sourceEvaluationPlan;
            var context = _evaluationContext.SemanticKey;
            var environment = CaptureRawEditorEnvironmentGuard();
            var scenarioIndex = SelectedScenario is null ? 0 : Scenarios.IndexOf(SelectedScenario);
            var sideIndex = GetAuthoredSideIndex(SelectedSide);
            var leaderIndex = SelectedLeader is null ? 0 : Leaders.IndexOf(SelectedLeader);
            var selectedOwner = SelectedSide?.IsGenerated == true ? SelectedStaticSideOwner : null;
            var selectedLeader = SelectedWccInitialLeader;
            bool NoPending() => !HasWccInlineDrafts && !HasWccMusicScheduleDrafts && !HasWccSideFlagDrafts &&
                !HasWccEventDrafts && !HasWccSideTopologyDrafts && !HasWccLeaderAdditionDrafts && !HasWccInitialUnitDrafts && !HasWccStoryDraft && !_hasAuthoredSourceChanges && !_externalSourceChangesPending &&
                !IsWccInlineSaveBusy && !WccFieldEditSession.IsBusy;
            bool ModelCurrent() => ReferenceEquals(asset, _currentScenarioAsset) &&
                WmlDefinitionSourcePublication.SamePath(sourcePath, _currentSourcePath ?? LoadedPath) &&
                ReferenceEquals(host, _sourceHostSession) && ReferenceEquals(snapshot, _macroLibrarySnapshot) &&
                ReferenceEquals(plan, _sourceEvaluationPlan) && context == _evaluationContext.SemanticKey && environment() &&
                version == EditVersion && !IsReadOnly && NoPending();
            bool IsCurrent()
            {
                lock (_wccInitialFieldRefreshGate)
                    return ModelCurrent() && generation == _sourceHostGeneration &&
                        ReferenceEquals(selected, SelectedScenario?.Document) && documents.SequenceEqual(asset.Documents) &&
                        documents.All(document => document.OriginalText == expectedText) &&
                        expectedText == asset.RawTextSnapshot && expectedText == _pristineLoadedSourceText;
            }
            if (!IsCurrent())
            { problem = "The scenario source has changed or has pending edits. Reopen the definition after saving or discarding them."; return null; }
            ScenarioDocument[] candidates;
            WmlMacroDefinition[] macros;
            try
            {
                candidates = ScenarioImporter.ImportAllFromText(savedText).ToArray();
                macros = WmlMacroDefinitionScanner.Scan(savedText).ToArray();
                if (candidates.Length != documents.Length)
                { problem = "The definition candidate must preserve the containing file's authored scenario blocks."; return null; }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { problem = "The saved definition candidate cannot populate the scenario editor: " + error.Message; return null; }
            bool PublishedCurrent() => ModelCurrent() && savedText == asset.RawTextSnapshot &&
                savedText == _pristineLoadedSourceText && asset.Documents.All(document => document.OriginalText == savedText);
            bool Publish()
            {
                lock (_wccInitialFieldRefreshGate)
                {
                    if (!IsCurrent()) return false;
                    InvalidateSourceEditingSession();
                    if (!ModelCurrent() || _sourceHostGeneration != generation + 1 ||
                        !ReferenceEquals(selected, SelectedScenario?.Document) || !documents.SequenceEqual(asset.Documents) ||
                        asset.RawTextSnapshot != expectedText) return false;
                    if (!ReloadSharedDefinitionScenarioSource(asset, savedText, candidates, macros,
                            scenarioIndex, sideIndex, leaderIndex, ModelCurrent)) return false;
                    if (!PublishedCurrent()) return false;
                    if (selectedOwner is not null)
                    {
                        _pendingWccSideSelection = owner => WmlDefinitionSourcePublication.SameMappedOwner(
                            selectedOwner, owner, sourcePath, expectedText, savedText);
                        _pendingWccSideSelectionPath = sourcePath;
                    }
                    if (selectedLeader is not null)
                    {
                        _pendingWccInitialLeaderSelection = owner => WmlDefinitionSourcePublication.SameMappedOwner(
                            selectedLeader.Owner, owner, sourcePath, expectedText, savedText);
                        _pendingWccInitialLeaderPath = sourcePath;
                        _pendingWccInitialLeaderHash = WccContentHash.ComputeText(savedText);
                        _pendingWccInitialLeaderAsset = asset;
                        _pendingWccInitialLeaderIsInline = selectedLeader.IsLegacyInline;
                    }
                    ScenarioSourceSaved?.Invoke(this, EventArgs.Empty);
                    return PublishedCurrent();
                }
            }
            return new(IsCurrent, Publish) { AssetIdentity = asset };
        }
    }

    private bool ReloadSharedDefinitionScenarioSource(ScenarioFileAsset asset, string savedText,
        ScenarioDocument[] documents, WmlMacroDefinition[] macros, int scenarioIndex, int sideIndex,
        int leaderIndex, Func<bool> modelCurrent)
    {
        var generation = _sourceHostGeneration;
        var selectedScenario = _selectedScenario;
        var selectedSide = _selectedSide;
        var selectedLeader = _selectedLeader;
        var baseline = asset.RawTextSnapshot;
        var pristine = _pristineLoadedSourceText;
        bool Current() => modelCurrent() && generation == _sourceHostGeneration &&
            asset.RawTextSnapshot == baseline && _pristineLoadedSourceText == pristine &&
            ReferenceEquals(selectedScenario, _selectedScenario) && ReferenceEquals(selectedSide, _selectedSide) &&
            ReferenceEquals(selectedLeader, _selectedLeader);
        var succeeded = false;
        RunUiSync(() =>
        {
            if (!Current()) return;
            selectedScenario = null;
            base.SetField(ref _selectedScenario, null, nameof(SelectedScenario));
            if (!Current()) return;
            selectedSide = null;
            base.SetField(ref _selectedSide, null, nameof(SelectedSide));
            if (!Current()) return;
            selectedLeader = null;
            base.SetField(ref _selectedLeader, null, nameof(SelectedLeader));
            if (!Current()) return;

            // Plain model lists have no observer callbacks. Install the exact
            // pre-parsed source before notifying any new editor collections.
            asset.RawTextSnapshot = baseline = savedText;
            _pristineLoadedSourceText = pristine = savedText;
            asset.Documents.Clear();
            asset.Documents.AddRange(documents);
            asset.MacroDefinitions.Clear();
            asset.MacroDefinitions.AddRange(macros);
            _loadedAssetInternalId = asset.InternalId;
            _currentSourcePath = asset.SourcePath;
            _currentSourceMacroDefinitions.Clear();
            _currentSourceMacroDefinitions.AddRange(macros);
            RefreshMacroDefinitionResolver();
            if (!Current()) return;
            Action[] reset =
            [
                () => OnPropertyChanged(nameof(UsesSourceBackedFieldEditing)),
                () => LoadedPath = asset.SourcePath ?? asset.FileName,
                () => Status = "Loaded the saved shared definition source.",
                Scenarios.Clear, Sides.Clear, Leaders.Clear
            ];
            if (!WmlDefinitionSourcePublication.RunGuarded(Current, reset)) return;
            foreach (var document in documents)
            {
                if (!Current()) return;
                Scenarios.Add(new ScenarioDocVm(document));
                if (!Current()) return;
            }
            selectedScenario = Scenarios.ElementAtOrDefault(scenarioIndex) ?? Scenarios.FirstOrDefault();
            base.SetField(ref _selectedScenario, selectedScenario, nameof(SelectedScenario));
            if (!Current()) return;
            var scenario = selectedScenario?.Document.Scenario;
            Action[] scenarioFields =
            [
                () => ScenarioId = scenario?.Id,
                () => ScenarioNextScenario = scenario?.NextScenario,
                () => ScenarioMapFile = scenario?.MapFile,
                () => ScenarioMapData = scenario?.MapData,
                () => ScenarioTurns = scenario?.Turns?.ToString(CultureInfo.InvariantCulture),
                () => ScenarioTurnAt = scenario?.TurnAt?.ToString(CultureInfo.InvariantCulture),
                () => ScenarioRandomStartTime = scenario?.RandomStartTime,
                () => ScenarioMusic = scenario?.Music,
                () => ScenarioVictoryWhenEnemiesDefeated = scenario?.VictoryWhenEnemiesDefeated
            ];
            if (!WmlDefinitionSourcePublication.RunGuarded(Current, scenarioFields)) return;
            foreach (var document in selectedScenario?.Document.SideDocuments ?? [])
            {
                if (!Current()) return;
                Sides.Add(new ScenarioSideVm(document));
                if (!Current()) return;
            }
            selectedSide = Sides.FirstOrDefault(side => GetAuthoredSideIndex(side) == sideIndex) ?? Sides.FirstOrDefault();
            base.SetField(ref _selectedSide, selectedSide, nameof(SelectedSide));
            if (!Current()) return;
            var side = selectedSide?.Side;
            Action[] sideFields =
            [
                () => SideNumber = side?.SideNumber?.ToString(CultureInfo.InvariantCulture),
                () => SideGold = side?.Gold?.ToString(CultureInfo.InvariantCulture),
                () => SideController = side?.Controller,
                () => SideTeamName = side?.TeamName,
                () => SideUserTeamName = side?.UserTeamName,
                () => SideRecruit = side?.Recruit,
                () => SideDisplayName = side?.SideNameWml,
                () => SideColor = side?.Color,
                () => SideIncome = side?.Income?.ToString(CultureInfo.InvariantCulture),
                () => SideVillageGold = side?.VillageGold?.ToString(CultureInfo.InvariantCulture),
                () => SideVillageSupport = side?.VillageSupport?.ToString(CultureInfo.InvariantCulture),
                () => SideRecallCost = side?.RecallCost?.ToString(CultureInfo.InvariantCulture),
                () => SidePersistent = side?.Persistent,
                () => SideSaveId = side?.SaveId,
                () => SideFog = side?.Fog,
                () => SideShroud = side?.Shroud,
                () => SideShareVision = side?.ShareVision,
                () => SideHidden = side?.Hidden,
                () => SideScrollToLeader = side?.ScrollToLeader,
                () => SideDefeatCondition = side?.DefeatCondition,
                () => SideNoLeader = side?.NoLeader,
                () => SideRecruitmentPattern = side?.RecruitmentPattern
            ];
            if (!WmlDefinitionSourcePublication.RunGuarded(Current, sideFields)) return;
            foreach (var document in selectedSide?.Document.LeaderDocuments ?? [])
            {
                if (!Current()) return;
                Leaders.Add(new ScenarioLeaderVm(document));
                if (!Current()) return;
            }
            selectedLeader = Leaders.ElementAtOrDefault(leaderIndex) ?? Leaders.FirstOrDefault();
            base.SetField(ref _selectedLeader, selectedLeader, nameof(SelectedLeader));
            if (!Current()) return;
            var leader = selectedLeader?.Leader;
            Action[] leaderFields =
            [
                () => SideX = leader?.X?.ToString(CultureInfo.InvariantCulture),
                () => SideY = leader?.Y?.ToString(CultureInfo.InvariantCulture),
                () => SideCanRecruit = leader?.CanRecruit,
                () => SideType = leader?.Type,
                () => SideId = leader?.Id,
                () => SideName = leader?.NameWml,
                () => LeaderGender = leader?.Gender,
                () => LeaderVariation = leader?.Variation,
                () => LeaderFacing = leader?.Facing,
                () => LeaderUnrenamable = leader?.Unrenamable,
                () => LeaderRandomTraits = leader?.RandomTraits,
                () => LeaderRandomGender = leader?.RandomGender,
                () => LeaderPlacement = leader?.Placement,
                () => LeaderLocationId = leader?.LocationId,
                () => LeaderExtraRecruit = leader?.ExtraRecruit
            ];
            if (!WmlDefinitionSourcePublication.RunGuarded(Current, leaderFields)) return;
            foreach (var property in new[] { nameof(ScenarioIdIsEditable), nameof(ScenarioNextScenarioIsEditable),
                         nameof(ScenarioMapFileIsEditable), nameof(ScenarioMapDataIsEditable), nameof(ScenarioTurnsIsEditable),
                         nameof(ScenarioTurnAtIsEditable), nameof(ScenarioRandomStartTimeIsEditable), nameof(ScenarioMusicIsEditable),
                         nameof(ScenarioVictoryWhenEnemiesDefeatedIsEditable), nameof(ScenarioConditionalInfo),
                         nameof(CanPickSideCoordinate) })
            {
                if (!Current()) return;
                OnPropertyChanged(property);
                if (!Current()) return;
            }
            if (!WmlDefinitionSourcePublication.RunGuarded(Current,
                    [RaiseWccScenarioContentChanged, RaiseEditingStateChanged, RefreshSideLeaderPreview, () => RefreshMapPreview()])) return;
            succeeded = Current();
        });
        return succeeded && Current();
    }
}
