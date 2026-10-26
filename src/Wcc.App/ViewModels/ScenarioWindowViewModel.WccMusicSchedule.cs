using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed class WccSchedulePresetChoice
{
    internal WccSchedulePresetChoice(WccScenarioRawEditDraft source, CatalogPickerSelection selection,
        string invocation, int requiredArguments)
    { Source = source; Selection = selection; Invocation = invocation; RequiredArguments = requiredArguments; }
    internal WccScenarioRawEditDraft Source { get; }
    internal CatalogPickerSelection Selection { get; }
    internal int RequiredArguments { get; }
    public string Invocation { get; }
    public bool HasRequiredArguments => RequiredArguments != 0;
}

public partial class ScenarioWindowViewModel
{
    private readonly ObservableCollection<WccMusicTrackRowVm> _wccMusicRows = new();
    private readonly ObservableCollection<WccScheduleTimeRowVm> _wccScheduleTimes = new();
    private ReadOnlyObservableCollection<WccMusicTrackRowVm>? _readOnlyWccMusicRows;
    private ReadOnlyObservableCollection<WccScheduleTimeRowVm>? _readOnlyWccScheduleTimes;
    private ScenarioDocument? _musicScheduleDisplayedDocument;
    private string? _musicScheduleDisplayedSource;
    private IReadOnlyList<WmlMacroExpansionDefinition> _musicScheduleDefinitions = [];
    private ScenarioInitialMusicProjection? _wccMusicProjection;
    private ScenarioInitialScheduleProjection? _wccScheduleProjection;
    private ScenarioInitialScheduleProjection? _wccSchedulePreview;
    private WccScenarioRawEditDraft? _musicScheduleSourceDraft;
    private ScenarioInitialScheduleEdit _wccScheduleEdit = new(ScenarioInitialScheduleEditKind.Preserve);
    private bool _wccMusicChanged;
    private bool _refreshingWccMusicSchedule;
    private bool _raisingWccMusicSchedule;
    private bool _wccMakeFirstTrackIntro;
    private bool _wccLetIntroFinish;
    private string? _musicScheduleProblem;
    private string? _pickedScheduleMacroName;
    private WccMusicTrackRowVm? _selectedWccMusicRow;
    private WccScheduleTimeRowVm? _selectedWccScheduleTime;

    public event EventHandler? WccMusicScheduleDraftsChanged;
    public ReadOnlyObservableCollection<WccMusicTrackRowVm> WccMusicRows =>
        _readOnlyWccMusicRows ??= new(_wccMusicRows);
    public ReadOnlyObservableCollection<WccScheduleTimeRowVm> WccScheduleTimes =>
        _readOnlyWccScheduleTimes ??= new(_wccScheduleTimes);
    internal bool IsUsingWccMusicScheduleSourceDraft { get; private set; }
    public bool HasWccMusicScheduleDrafts => _musicScheduleSourceDraft is not null &&
        (_wccMusicChanged || _wccScheduleEdit.Kind != ScenarioInitialScheduleEditKind.Preserve ||
         _wccScheduleEdit.Start is { Kind: not ScenarioInitialScheduleStartKind.Preserve });
    public bool CanEditWccMusicPlaylist => !IsWccInlineSaveBusy && !WccFieldEditSession.IsBusy &&
        _wccMusicProjection?.CanEdit == true && IsCurrentWccMusicScheduleSource();
    public bool CanEditWccSchedule => !_usesCapturedScheduleDisplay && !IsWccInlineSaveBusy && !WccFieldEditSession.IsBusy &&
        _wccScheduleProjection?.CanCompose == true && IsCurrentWccMusicScheduleSource();
    public bool CanEditWccCustomSchedule => CanEditWccSchedule && !WccScheduleIsPreset &&
        _wccSchedulePreview is { CustomConversionDiagnostic: null };
    public bool CanChangeWccCustomScheduleStructure => CanEditWccCustomSchedule &&
        (_wccScheduleTimes.Count == 0 || _wccSchedulePreview?.StartIsKnown == true);
    public bool CanApplyWccMusicScheduleDrafts => HasWccMusicScheduleDrafts &&
        TryBuildWccMusicScheduleCandidate(fresh: false, out _, out _) && IsCurrentWccMusicScheduleSource();
    public bool CanDiscardWccMusicScheduleDrafts => HasWccMusicScheduleDrafts && !IsWccInlineSaveBusy;

    public WccMusicTrackRowVm? SelectedWccMusicRow
    {
        get => _selectedWccMusicRow;
        set { if (SetField(ref _selectedWccMusicRow, value)) OnPropertyChanged(nameof(CanRemoveWccMusicRow)); }
    }
    public WccScheduleTimeRowVm? SelectedWccScheduleTime
    {
        get => _selectedWccScheduleTime;
        set
        {
            if (!SetField(ref _selectedWccScheduleTime, value)) return;
            OnPropertyChanged(nameof(CanEditWccScheduleTime));
            OnPropertyChanged(nameof(CanRemoveWccScheduleTime));
            OnPropertyChanged(nameof(CanMoveWccScheduleTimeToStart));
        }
    }
    public bool CanRemoveWccMusicRow => CanEditWccMusicPlaylist && SelectedWccMusicRow is { } row && _wccMusicRows.Contains(row);
    public bool CanEditWccScheduleTime => CanEditWccCustomSchedule && SelectedWccScheduleTime is { } row && _wccScheduleTimes.Contains(row);
    public bool CanRemoveWccScheduleTime => CanChangeWccCustomScheduleStructure && _wccScheduleTimes.Count > 1 &&
        SelectedWccScheduleTime is { } row && _wccScheduleTimes.Contains(row);
    public bool CanMoveWccScheduleTimeToStart => CanChangeWccCustomScheduleStructure &&
        _wccSchedulePreview is { IsRandomStart: false } &&
        SelectedWccScheduleTime is { } row && _wccScheduleTimes.Contains(row);
    public bool CanMakeWccFirstTrackIntro => CanEditWccMusicPlaylist && _wccMusicRows.Count != 0;
    public bool CanLetWccIntroFinish => CanEditWccMusicPlaylist && _wccMusicRows.Count != 0;

    public bool WccMakeFirstTrackIntro
    {
        get => _wccMakeFirstTrackIntro;
        set
        {
            lock (_wccInitialFieldRefreshGate)
            {
                if (value == _wccMakeFirstTrackIntro || !CanEditWccMusicPlaylist ||
                    value && _wccMusicRows.Count == 0 || !EnsureWccMusicScheduleSourceDraft()) return;
                _wccMakeFirstTrackIntro = value;
                WccMusicChanged();
            }
        }
    }
    public bool WccLetIntroFinish
    {
        get => _wccLetIntroFinish;
        set
        {
            lock (_wccInitialFieldRefreshGate)
            {
                if (value == _wccLetIntroFinish || !CanLetWccIntroFinish || !EnsureWccMusicScheduleSourceDraft()) return;
                _wccLetIntroFinish = value;
                WccMusicChanged();
            }
        }
    }
    public string WccMusicGuidance => _wccMusicProjection?.Problem is { } problem ? problem
        : _wccMusicProjection?.Shuffle == true
            ? "Playlist source order is shown below. Normal playlist playback is shuffled."
            : "Playlist source order is shown below. Existing per-track playback settings are preserved.";
    public string? WccMusicDefaultIntroHint => !_wccMakeFirstTrackIntro && _wccMusicProjection?.DefaultIntroTrackName is { } name
        ? "The current music source lets the story screen choose " + name + ". It has no explicit intro track." : null;
    public bool HasWccMusicDefaultIntroHint => WccMusicDefaultIntroHint is not null;

    public bool WccScheduleIsPreset => !_usesCapturedScheduleDisplay && IsWccLibrarySchedulePreset(_wccSchedulePreview);
    public string WccSchedulePresetValue => _usesCapturedScheduleDisplay ? "Captured initial schedule" : _wccSchedulePreview is { PresetDefinition: not null, PresetInvocation: { } invocation }
        ? invocation
        : "Custom";
    public bool CanMakeWccScheduleCustom => CanEditWccSchedule && WccScheduleIsPreset &&
        _wccSchedulePreview is { CustomConversionDiagnostic: null };
    private bool HasDeterministicWccCustomStart => !_usesCapturedScheduleDisplay && !WccScheduleIsPreset &&
        _wccSchedulePreview is { StartIsKnown: true, IsRandomStart: false, StartIndex: not null };
    public int? WccScheduleStartIndex => HasDeterministicWccCustomStart ? 0 : DisplayedWccSchedule?.StartIndex;
    public string WccScheduleGuidance => _usesCapturedScheduleDisplay ? _capturedSchedulePreview!.Schedule.Diagnostic! : _wccSchedulePreview?.Diagnostic is { } diagnostic ? diagnostic
        : _wccSchedulePreview is { CanConvertToCustom: false, CustomConversionDiagnostic: { } conversionProblem } ? conversionProblem
        : WccScheduleIsPreset ? "Preset time slots are read-only. Choose Custom to edit local time blocks."
        : _wccScheduleTimes.Count > 0 && _wccSchedulePreview?.StartIsKnown == false
            ? "Starting time is unresolved. Time attributes can be edited in RAW; use scenario RAW to change the cycle's order or size."
        : _wccSchedulePreview?.IsRandomStart == true
            ? "Random starting candidates are marked. Changing the cycle keeps surviving candidates and their weights."
        : "The first Custom row is the starting time. Reorder the cycle or move a row to start; edit time attributes in RAW.";
    public string WccScheduleStartDescription => DisplayedWccSchedule switch
    {
        { StartIsKnown: false } => "Starting time is unresolved in this source context.",
        { IsRandomStart: true } => "Random starting time; eligible slots are marked below.",
        { StartIndex: not null } when !WccScheduleIsPreset && !_usesCapturedScheduleDisplay => "Starts at the first Custom row.",
        { StartIndex: { } index } => "Starts at slot " + (index + 1) + ".",
        _ => "No time slots are available."
    };
    public CatalogPickerOptionVm? WccSchedulePreviewImage => _wccScheduleTimes.ElementAtOrDefault(
        HasDeterministicWccCustomStart ? 0 : DisplayedWccSchedule is { } schedule
            ? WccScheduleImagePreviews.StartingIndex(schedule) : 0)?.Preview;
    public string WccSchedulePreviewDescription => DisplayedWccSchedule is { } schedule
        ? WccScheduleImagePreviews.Describe(schedule) : "No time slots are available.";
    public bool HasWccSchedulePreview => _wccScheduleTimes.Count > 0;
    public string WccMusicScheduleDraftStatus
    {
        get
        {
            if (HasWccMusicScheduleDrafts && !IsCurrentWccMusicScheduleSource())
                return "The source or editing context changed. These drafts are retained; discard them to reload the current music and schedule.";
            if (_musicScheduleProblem is { } retainedProblem) return retainedProblem;
            if (HasWccMusicScheduleDrafts)
                return TryBuildWccMusicScheduleCandidate(fresh: false, out _, out var composeProblem)
                    ? "Music and schedule changes are pending. Save applies them together and writes the scenario file."
                    : composeProblem ?? "This music or schedule draft cannot be applied.";
            return _hasAuthoredSourceChanges ? "Save source changes before editing music or schedule again."
                : IsWccInlineSaveBusy ? "The collected save is running."
                : "Changes stay in the working draft until Save.";
        }
    }

    internal void RefreshWccMusicScheduleEditors()
        => RequestMusicScheduleDisplayRefresh();

    private void RefreshWccMusicScheduleEditorsNow()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_refreshingWccMusicSchedule) return;
            _refreshingWccMusicSchedule = true;
            try
            {
                if (!HasWccMusicScheduleDrafts)
                {
                    if (_musicScheduleSourceDraft is not null && !IsCurrentWccMusicScheduleSource())
                    {
                        _musicScheduleSourceDraft = null;
                        _musicScheduleSourceBinding = null;
                        _musicScheduleDraftVersion++;
                    }
                    var document = SelectedScenario?.Document;
                    var raw = GetWccScenarioSourceRawWml();
                    var definitions = GetWccMusicScheduleDefinitions();
                    var captured = CurrentMusicScheduleAuthoringProjection;
                    // A late full evaluation supplies a fallback for chronology that
                    // cannot be projected in isolation. It does not replace a working
                    // ordinary planner or invalidate an already opened picker.
                    var capturedChanged = !ReferenceEquals(captured, _musicScheduleCapturedProjection);
                    var needsCapturedRefresh = capturedChanged && (_musicUsesCapturedAuthoring || _scheduleUsesCapturedAuthoring ||
                        _wccMusicProjection?.CanEdit != true || _wccScheduleProjection?.CanCompose != true ||
                        _wccMusicProjection is not null && PreferCapturedMusic(_wccMusicProjection, CapturedMusicAuthoring(captured, raw, definitions)) ||
                        CapturedScheduleDiffers(captured, raw, _wccScheduleProjection));
                    _musicScheduleCapturedProjection = captured;
                    if (!ReferenceEquals(document, _musicScheduleDisplayedDocument) || raw != _musicScheduleDisplayedSource ||
                        needsCapturedRefresh ||
                        !ReferenceEquals(definitions, _musicScheduleDefinitions) &&
                        DefinitionSignature(definitions) != DefinitionSignature(_musicScheduleDefinitions))
                    {
                        _musicScheduleDisplayedDocument = document;
                        _musicScheduleDisplayedSource = raw;
                        _musicScheduleDefinitions = definitions;
                        _musicScheduleSourceDraft = null;
                        _musicScheduleSourceBinding = null;
                        _musicScheduleDraftVersion++;
                        _wccMusicChanged = false;
                        _wccScheduleEdit = new(ScenarioInitialScheduleEditKind.Preserve);
                        _pickedScheduleMacroName = null;
                        _musicScheduleProblem = null;
                        _wccMusicProjection = raw is null ? null : ProjectWccMusicAuthoring(raw, definitions);
                        if (raw is null) _musicUsesCapturedAuthoring = false;
                        if (raw is null)
                        { _wccScheduleProjection = null; _scheduleUsesCapturedAuthoring = false; }
                        else
                            (_wccScheduleProjection, _scheduleUsesCapturedAuthoring) = SelectWccScheduleAuthoring(raw, definitions);
                        _wccSchedulePreview = _wccScheduleProjection;
                        _wccMakeFirstTrackIntro = _wccMusicProjection?.HasIntro == true;
                        _wccLetIntroFinish = _wccMusicProjection?.LetIntroFinish == true;
                        _wccMusicRows.Clear();
                        if (_wccMusicProjection is { } music)
                            foreach (var track in music.Tracks) _wccMusicRows.Add(new(this, track));
                        SelectedWccMusicRow = _wccMusicRows.FirstOrDefault();
                        RebuildWccScheduleTimeRows();
                    }
                }
                RefreshCapturedScheduleDisplay();
                RefreshWccScheduleImagePreviews();
                RaiseWccMusicScheduleChanged();
            }
            finally { _refreshingWccMusicSchedule = false; }
        }
    }

    private bool IsCurrentWccMusicScheduleSource()
    {
        return _musicScheduleSourceBinding is { } binding
            ? GetWccMusicScheduleBindingProblem(binding) is null : CanCaptureWccMusicScheduleSource();
    }

    private bool EnsureWccMusicScheduleSourceDraft()
    {
        if (_musicScheduleSourceDraft is not null) return IsCurrentWccMusicScheduleSource();
        var previous = IsUsingWccMusicScheduleSourceDraft;
        IsUsingWccMusicScheduleSourceDraft = true;
        try
        {
            if (!CanCaptureWccMusicScheduleSource()) return false;
            // This wrapper keeps existing picker APIs stable. Its RAW generations are
            // never used as music authority or passed directly to a writer.
            var draft = new WccScenarioRawEditDraft(SelectedScenario!.Document, _currentSourcePath ?? LoadedPath!,
                _pristineLoadedSourceText!, _sourceHostGeneration, _staticInitialValuesGeneration, null);
            if (!ReferenceEquals(draft.Document, _musicScheduleDisplayedDocument) ||
                draft.Text != _musicScheduleDisplayedSource) return false;
            var fresh = GetWccMusicScheduleDefinitions(fresh: true);
            if (DefinitionSignature(fresh) != DefinitionSignature(_musicScheduleDefinitions))
            {
                _musicScheduleProblem = "A music or schedule definition changed. Reload its indexed source before editing.";
                RaiseWccMusicScheduleChanged();
                return false;
            }
            _musicScheduleSourceDraft = draft;
            _musicScheduleSourceBinding = CaptureWccMusicScheduleBinding(draft);
            return true;
        }
        finally { IsUsingWccMusicScheduleSourceDraft = previous; }
    }

    public WccScenarioRawEditDraft? CaptureWccMusicSchedulePickerSource()
    {
        lock (_wccInitialFieldRefreshGate)
            return EnsureWccMusicScheduleSourceDraft() ? _musicScheduleSourceDraft : null;
    }
    public bool IsCurrentWccMusicSchedulePickerSource(WccScenarioRawEditDraft source) =>
        ReferenceEquals(source, _musicScheduleSourceDraft) && IsCurrentWccMusicScheduleSource();

    public bool AddWccMusicTracks(IEnumerable<string> names)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var requested = names.ToArray();
            if (requested.Length == 0 || !CanEditWccMusicPlaylist) return false;
            var mainNames = GetWccMusicPlaylistMainTrackNames().ToHashSet(StringComparer.Ordinal);
            var additions = requested.Where(mainNames.Add).ToArray();
            if (additions.Length == 0) return true;
            if (!EnsureWccMusicScheduleSourceDraft()) return false;
            foreach (var name in additions) _wccMusicRows.Add(new(this, new ScenarioInitialMusicTrack(name)));
            SelectedWccMusicRow = _wccMusicRows.LastOrDefault();
            WccMusicChanged();
            return true;
        }
    }
    public IReadOnlyList<string> GetWccMusicPlaylistMainTrackNames()
    {
        lock (_wccInitialFieldRefreshGate)
            return _wccMusicRows.Skip(_wccMakeFirstTrackIntro ? 1 : 0).Select(row => row.Name).ToArray();
    }
    public IReadOnlyList<CatalogPickerSelection> GetWccMusicPlaylistCurrentSelections()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var options = GetWccMusicPlaylistPickerOptions();
            return GetWccMusicPlaylistMainTrackNames().Distinct(StringComparer.Ordinal)
                .Select(name => options.FirstOrDefault(option => option.ValueId == name &&
                    option.ResourceSourceLabel is not "Core Campaign" and not "WCC"))
                .Where(option => option is not null).Select(option => option!.ToSelection()).ToArray();
        }
    }
    public bool AddWccMusicSelections(IEnumerable<CatalogPickerSelection> selections)
    {
        var requested = selections.ToArray();
        var options = GetWccMusicPlaylistPickerOptions();
        return requested.All(selection => CanReferenceWccMusicSelectionDirectly(selection) && options.Any(option => option.ValueId == selection.EmittedValue &&
                option.EmissionKind == selection.EmissionKind && option.LibraryEntityId == selection.LibraryEntityId &&
                option.LibraryPackageId == selection.LibraryPackageId)) &&
            AddWccMusicTracks(requested.Select(selection => selection.EmittedValue));
    }
    internal void SetWccMusicTrackName(WccMusicTrackRowVm row, string name)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!_wccMusicRows.Contains(row) || row.Name == name || !CanEditWccMusicPlaylist ||
                !EnsureWccMusicScheduleSourceDraft()) return;
            row.SetName(name);
            WccMusicChanged();
        }
    }
    public bool MoveWccMusicTrack(WccMusicTrackRowVm row, int destination)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var index = _wccMusicRows.IndexOf(row);
            if (index < 0 || destination < 0 || destination >= _wccMusicRows.Count || index == destination ||
                !CanEditWccMusicPlaylist || !EnsureWccMusicScheduleSourceDraft()) return false;
            _wccMusicRows.Move(index, destination);
            SelectedWccMusicRow = row;
            WccMusicChanged();
            return true;
        }
    }
    public bool RemoveWccMusicTrack(WccMusicTrackRowVm row)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var index = _wccMusicRows.IndexOf(row);
            if (index < 0 || !CanEditWccMusicPlaylist || !EnsureWccMusicScheduleSourceDraft()) return false;
            _wccMusicRows.RemoveAt(index);
            if (_wccMusicRows.Count == 0) _wccMakeFirstTrackIntro = _wccLetIntroFinish = false;
            SelectedWccMusicRow = _wccMusicRows.ElementAtOrDefault(Math.Min(index, _wccMusicRows.Count - 1));
            WccMusicChanged();
            return true;
        }
    }
    private void WccMusicChanged()
    {
        _musicScheduleDraftVersion++;
        _wccMusicChanged = _wccMusicProjection is { } original &&
            (original.HasIntro != _wccMakeFirstTrackIntro || original.LetIntroFinish != _wccLetIntroFinish ||
             !original.Tracks.SequenceEqual(_wccMusicRows.Select(row => row.Track)));
        if (!HasWccMusicScheduleDrafts)
        {
            _musicScheduleSourceDraft = null;
            _musicScheduleSourceBinding = null;
        }
        _musicScheduleProblem = null;
        RaiseWccMusicScheduleChanged();
    }

    private bool IsWccLibrarySchedulePreset(ScenarioInitialScheduleProjection? projection) =>
        projection?.PresetDefinition is { } definition && IsInitialCoreOrWccPresetDefinition(definition);

    public bool MakeWccScheduleCustom()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanMakeWccScheduleCustom || !EnsureWccMusicScheduleSourceDraft()) return false;
            var preview = _wccSchedulePreview!;
            var deterministic = preview is { StartIsKnown: true, IsRandomStart: false, StartIndex: not null };
            var selected = _selectedWccScheduleTime?.Index ?? 0;
            if (deterministic) selected = (selected - preview.StartIndex!.Value + preview.Times.Count) % preview.Times.Count;
            if (!TryGetWccScheduleAttributeStart(out var preservedStart, out var problem))
                return RefuseWccScheduleChange(problem!);
            return StageWccScheduleEditKeepingImages(new(ScenarioInitialScheduleEditKind.Custom,
                TimeBlocks: TimesStartingFirst(preview).Select(time => time.RawWml).ToArray(),
                Start: deterministic ? new(ScenarioInitialScheduleStartKind.Slot, SlotIndex: 0) : preservedStart),
                TimesStartingFirst(preview).Select(time => ScheduleImageAtSourceIndex(time.Index)).ToArray(), selected);
        }
    }
    public string? ValidateWccScheduleTimeRawEdit(WccScheduleTimeRowVm row, string text)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanEditWccScheduleTime || !ReferenceEquals(SelectedWccScheduleTime, row) || !_wccScheduleTimes.Contains(row))
                return "The selected custom schedule slot changed.";
            if (text == row.RawText) return null;
            var validation = WmlSingleBlockValidator.Validate(text, "time");
            if (!validation.IsValid) return validation.Problem;
            if (!TryGetWccScheduleAttributeStart(out var start, out var problem)) return problem;
            var blocks = _wccSchedulePreview!.Times.Select(time => time.RawWml).ToArray();
            blocks[row.Time.Index] = text;
            var result = ScenarioInitialScheduleEditor.Compose(_wccScheduleProjection!,
                new(ScenarioInitialScheduleEditKind.Custom, TimeBlocks: blocks, Start: start));
            return result.IsSuccess ? null : result.Diagnostic;
        }
    }
    public bool StageWccScheduleTimeRawEdit(WccScheduleTimeRowVm row, string text)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (ValidateWccScheduleTimeRawEdit(row, text) is not null) return false;
            if (text == row.RawText) return true;
            if (!EnsureWccMusicScheduleSourceDraft()) return false;
            if (!TryGetWccScheduleAttributeStart(out var start, out var problem)) return RefuseWccScheduleChange(problem!);
            var blocks = _wccSchedulePreview!.Times.Select(time => time.RawWml).ToArray();
            blocks[row.Time.Index] = text;
            return StageWccScheduleEditKeepingImages(new(ScenarioInitialScheduleEditKind.Custom, TimeBlocks: blocks, Start: start),
                _wccSchedulePreview.Times.Select(time => ScheduleImageAtSourceIndex(time.Index)).ToArray());
        }
    }
    public bool AddWccScheduleTime(string raw)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (ValidateWccScheduleTimeAddition(raw) is { } problem) return RefuseWccScheduleChange(problem);
            var positions = Enumerable.Range(0, _wccScheduleTimes.Count).Select(index => (int?)index).Append(null).ToArray();
            return StageWccScheduleStructure(_wccScheduleTimes.Select(time => time.RawText).Append(raw).ToArray(), positions,
                _wccScheduleTimes.Count);
        }
    }
    public string? ValidateWccScheduleTimeAddition(string raw)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanChangeWccCustomScheduleStructure) return WccScheduleStructureProblem();
            var validation = WmlSingleBlockValidator.Validate(raw, "time");
            if (!validation.IsValid) return validation.Problem;
            var positions = Enumerable.Range(0, _wccScheduleTimes.Count).Select(index => (int?)index).Append(null).ToArray();
            if (!TryGetWccScheduleStructuralStart(positions, out var start, out var problem)) return problem;
            var result = ScenarioInitialScheduleEditor.Compose(_wccScheduleProjection!,
                new(ScenarioInitialScheduleEditKind.Custom, TimeBlocks: _wccScheduleTimes.Select(time => time.RawText).Append(raw).ToArray(), Start: start));
            return result.IsSuccess ? null : result.Diagnostic;
        }
    }
    public bool RemoveWccScheduleTime(WccScheduleTimeRowVm row)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!_wccScheduleTimes.Contains(row)) return false;
            if (!CanChangeWccCustomScheduleStructure) return RefuseWccScheduleChange(WccScheduleStructureProblem());
            if (_wccScheduleTimes.Count <= 1) return RefuseWccScheduleChange("A Custom cycle needs at least one time slot.");
            var retained = _wccScheduleTimes.Where(time => !ReferenceEquals(time, row)).ToArray();
            var selected = Array.IndexOf(retained, _selectedWccScheduleTime);
            if (selected < 0) selected = Math.Min(row.Index, retained.Length - 1);
            return StageWccScheduleStructure(retained.Select(time => time.RawText).ToArray(),
                retained.Select(time => (int?)time.Index).ToArray(), selected);
        }
    }
    public bool MoveWccScheduleTime(WccScheduleTimeRowVm row, int destination)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!_wccScheduleTimes.Contains(row) || destination < 0 || destination >= _wccScheduleTimes.Count || destination == row.Index) return false;
            if (!CanChangeWccCustomScheduleStructure) return RefuseWccScheduleChange(WccScheduleStructureProblem());
            var ordered = _wccScheduleTimes.ToList();
            ordered.RemoveAt(row.Index);
            ordered.Insert(destination, row);
            return StageWccScheduleStructure(ordered.Select(time => time.RawText).ToArray(),
                ordered.Select(time => (int?)time.Index).ToArray(), ordered.IndexOf(_selectedWccScheduleTime!));
        }
    }
    public bool SetWccScheduleStartingSlot(WccScheduleTimeRowVm row)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!_wccScheduleTimes.Contains(row)) return false;
            if (!CanChangeWccCustomScheduleStructure) return RefuseWccScheduleChange(WccScheduleStructureProblem());
            if (_wccSchedulePreview?.IsRandomStart == true)
                return RefuseWccScheduleChange("This cycle uses a random starting time. Its candidates stay marked while rows are reordered.");
            var ordered = _wccScheduleTimes.Skip(row.Index).Concat(_wccScheduleTimes.Take(row.Index)).ToArray();
            return StageWccScheduleStructure(ordered.Select(time => time.RawText).ToArray(),
                ordered.Select(time => (int?)time.Index).ToArray(), Array.IndexOf(ordered, _selectedWccScheduleTime));
        }
    }
    public bool IsWccScheduleRandomSlot(int index) => DisplayedWccSchedule is { IsRandomStart: true } preview &&
        preview.RandomStartIndices.Contains(index);

    private bool TryGetWccScheduleAttributeStart(out ScenarioInitialScheduleStartChoice? start, out string? problem)
    {
        start = _wccScheduleEdit.Start;
        problem = null;
        if (start is { Kind: not ScenarioInitialScheduleStartKind.Preserve } ||
            _wccScheduleEdit.Kind != ScenarioInitialScheduleEditKind.Preset) return true;
        // A pending picker choice may supply a different start from the captured
        // original source. Preserve that chosen cycle when its macro is materialized.
        start = _wccSchedulePreview?.ResolvedStartChoice;
        if (start is not null) return true;
        problem = "The selected schedule's starting time is unresolved. Use scenario RAW to materialize it.";
        return false;
    }
    private bool StageWccScheduleStructure(IReadOnlyList<string> blocks, IReadOnlyList<int?> oldPositions, int selectedIndex)
    {
        if (!TryGetWccScheduleStructuralStart(oldPositions, out var start, out var problem)) return RefuseWccScheduleChange(problem!);
        if (!EnsureWccMusicScheduleSourceDraft()) return false;
        return StageWccScheduleEditKeepingImages(new(ScenarioInitialScheduleEditKind.Custom, TimeBlocks: blocks, Start: start),
            oldPositions.Select(index => index is { } position ? _wccScheduleTimes.ElementAtOrDefault(position)?.Preview : null).ToArray(),
            Math.Max(selectedIndex, 0));
    }
    private bool TryGetWccScheduleStructuralStart(IReadOnlyList<int?> oldPositions,
        out ScenarioInitialScheduleStartChoice? start, out string? problem)
    {
        start = null;
        problem = null;
        if (oldPositions.Count == 0) { problem = "A Custom cycle needs at least one time slot."; return false; }
        if (_wccScheduleTimes.Count == 0 || _wccSchedulePreview is { StartIsKnown: true, IsRandomStart: false })
        { start = new(ScenarioInitialScheduleStartKind.Slot, SlotIndex: 0); return true; }
        if (_wccSchedulePreview is not { StartIsKnown: true, ResolvedStartChoice: { } choice })
        { problem = "Starting time is unresolved. Use scenario RAW to change the cycle's order or size."; return false; }
        if (choice.Kind == ScenarioInitialScheduleStartKind.RandomAll) { start = choice; return true; }
        if (choice is not { Kind: ScenarioInitialScheduleStartKind.RandomSlots, RandomSlotIndices: { } weighted })
        { problem = "The random starting-time choice cannot be retained safely. Use scenario RAW."; return false; }
        var map = oldPositions.Select((oldIndex, newIndex) => (oldIndex, newIndex)).Where(pair => pair.oldIndex.HasValue)
            .ToDictionary(pair => pair.oldIndex!.Value, pair => pair.newIndex);
        var retained = weighted.Where(map.ContainsKey).Select(index => map[index]).ToArray();
        if (retained.Length == 0)
        { problem = "Removing this slot would leave no random starting candidates. Keep a candidate or change the random start in scenario RAW."; return false; }
        start = new(ScenarioInitialScheduleStartKind.RandomSlots, RandomSlotIndices: retained);
        return true;
    }
    private string WccScheduleStructureProblem() => _wccScheduleTimes.Count > 0 && _wccSchedulePreview?.StartIsKnown == false
        ? "Starting time is unresolved. Use scenario RAW to change the cycle's order or size."
        : "The current Custom schedule is unavailable for structural editing. Choose Custom for a preset and save other source changes first.";
    private bool RefuseWccScheduleChange(string problem)
    {
        _musicScheduleProblem = Status = problem;
        RaiseWccMusicScheduleChanged();
        return false;
    }
    private static IReadOnlyList<ScenarioInitialTimeSlot> TimesStartingFirst(ScenarioInitialScheduleProjection preview) =>
        preview is { StartIsKnown: true, IsRandomStart: false, StartIndex: { } start } && start > 0
            ? preview.Times.Skip(start).Concat(preview.Times.Take(start)).ToArray() : preview.Times;
    private bool StageWccScheduleEdit(ScenarioInitialScheduleEdit edit, int? selectedIndex = null)
    {
        var result = ScenarioInitialScheduleEditor.Compose(_wccScheduleProjection!, edit);
        if (!result.IsSuccess || result.CandidateSource is null)
        { _musicScheduleProblem = result.Diagnostic; RaiseWccMusicScheduleChanged(); return false; }
        _wccScheduleEdit = edit;
        _pendingScheduleImagePreviews = new Dictionary<int, CatalogPickerOptionVm>();
        _usesCapturedScheduleDisplay = false;
        _capturedSchedulePreview = null;
        _capturedScheduleDisplayContext = null;
        _musicScheduleDraftVersion++;
        _wccSchedulePreview = result.Preview ?? ScenarioInitialScheduleEditor.Project(result.CandidateSource, _musicScheduleDefinitions, GetWccScenarioTextDomain());
        _musicScheduleProblem = null;
        RebuildWccScheduleTimeRows(selectedIndex);
        RaiseWccMusicScheduleChanged();
        return true;
    }
    private void RebuildWccScheduleTimeRows(int? selectedIndex = null)
    {
        var selected = selectedIndex ?? _selectedWccScheduleTime?.Index ?? 0;
        _wccScheduleTimes.Clear();
        if (DisplayedWccSchedule is { } schedule)
        {
            foreach (var time in _usesCapturedScheduleDisplay || WccScheduleIsPreset ? schedule.Times : TimesStartingFirst(schedule))
                _wccScheduleTimes.Add(new(this, time, _wccScheduleTimes.Count));
        }
        _scheduleImagePreviewContext = null;
        RefreshWccScheduleImagePreviews();
        SelectedWccScheduleTime = _wccScheduleTimes.ElementAtOrDefault(selected) ?? _wccScheduleTimes.FirstOrDefault();
    }

    public async Task<WccSchedulePresetChoice?> BeginWccSchedulePresetChoiceAsync(CatalogPickerSelection selection)
    {
        WccScenarioRawEditDraft source;
        WccLibrarySnapshot snapshot;
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanEditWccSchedule)
                return RefuseWccSchedulePresetChoice("The current schedule is unavailable for editing. " + WccScheduleGuidance);
            if (selection.EmissionKind != CatalogPickerEmissionKind.MacroInvocation ||
                selection.LibraryEntityId is null || _macroLibrarySnapshot is not { } current ||
                !GetWccInitialSchedulePickerOptions().Any(option => option.LibraryEntityId == selection.LibraryEntityId &&
                    option.LibraryPackageId == selection.LibraryPackageId && option.ValueId == selection.EmittedValue))
                return RefuseWccSchedulePresetChoice("Choose a schedule from the current campaign or the Core/WCC library.");
            if (!EnsureWccMusicScheduleSourceDraft())
                return RefuseWccSchedulePresetChoice("The source or editing context changed. Reopen the schedule picker.");
            source = _musicScheduleSourceDraft!;
            snapshot = current;
        }
        try
        {
            var content = await WccLibraryEntityContentLoader.LoadAsync(snapshot, selection.LibraryEntityId!.Value);
            lock (_wccInitialFieldRefreshGate)
            {
                if (!ReferenceEquals(snapshot, _macroLibrarySnapshot) || !ReferenceEquals(source, _musicScheduleSourceDraft) ||
                    !IsCurrentWccMusicScheduleSource())
                    return RefuseWccSchedulePresetChoice("The source or editing context changed. Reopen the schedule picker.");
                if (!content.MatchesIndexedContentHash)
                    return RefuseWccSchedulePresetChoice("The selected schedule source changed. Refresh the Library and choose it again.");
                var definition = WmlMacroDefinitionScanner.Scan(content.EntityRawText).SingleOrDefault(item => item.Name == selection.EmittedValue);
                var fresh = GetWccMusicScheduleDefinitions([selection.EmittedValue], fresh: true);
                var resolved = fresh.Where(item => item.Name == selection.EmittedValue).ToArray();
                if (definition is null || resolved.Length != 1 || !PathsEqual(resolved[0].SourcePath, content.SourceFile.StoragePath) ||
                    resolved[0].RawText != definition.RawText)
                    return RefuseWccSchedulePresetChoice("The selected schedule is shadowed or no longer matches its captured source. Choose the current campaign definition or refresh the Library.");
                var header = definition.RawText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                    .First(line => line.TrimStart().StartsWith("#define ", StringComparison.Ordinal));
                var declaration = header.TrimStart()["#define".Length..].TrimStart();
                var parameters = WmlInlineCommentSplitter.Split(declaration).Value
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
                return new(source, selection, "{" + selection.EmittedValue +
                    (parameters.Length == 0 ? "" : " " + string.Join(" ", parameters.Select(parameter => "<" + parameter + ">"))) + "}", parameters.Length);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return RefuseWccSchedulePresetChoice("The selected schedule source is unavailable. Refresh the Library and try again."); }
    }
    private WccSchedulePresetChoice? RefuseWccSchedulePresetChoice(string problem)
    {
        _musicScheduleProblem = Status = problem;
        RaiseWccMusicScheduleChanged();
        return null;
    }
    public string? ValidateWccSchedulePresetChoice(WccSchedulePresetChoice choice, string invocation)
    {
        if (!ReferenceEquals(choice.Source, _musicScheduleSourceDraft) || !IsCurrentWccMusicScheduleSource())
            return "The source or editing context changed. Choose the schedule again.";
        if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(invocation.Trim(), out var parsed) || parsed is null ||
            parsed.Name != choice.Selection.EmittedValue || parsed.Arguments.Count < choice.RequiredArguments ||
            parsed.Arguments.Any(argument => argument.RawText.Contains('<') || argument.RawText.Contains('>')))
            return "Keep the selected schedule invocation and fill its required arguments.";
        var definitions = GetWccMusicScheduleDefinitions([choice.Selection.EmittedValue], fresh: true);
        var projection = ProjectWccScheduleAuthoring(_musicScheduleSourceDraft!.Text, definitions);
        var result = ScenarioInitialScheduleEditor.Compose(projection, new(ScenarioInitialScheduleEditKind.Preset, PresetInvocation: invocation));
        return result.IsSuccess ? null : result.Diagnostic;
    }
    public bool StageWccSchedulePresetChoice(WccSchedulePresetChoice choice, string invocation)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (ValidateWccSchedulePresetChoice(choice, invocation) is { } problem)
            { _musicScheduleProblem = problem; RaiseWccMusicScheduleChanged(); return false; }
            _pickedScheduleMacroName = choice.Selection.EmittedValue;
            _musicScheduleDefinitions = GetWccMusicScheduleDefinitions([_pickedScheduleMacroName!], fresh: true);
            (_wccScheduleProjection, _scheduleUsesCapturedAuthoring) =
                SelectWccScheduleAuthoring(_musicScheduleSourceDraft!.Text, _musicScheduleDefinitions);
            return StageWccScheduleEdit(new(ScenarioInitialScheduleEditKind.Preset, PresetInvocation: invocation));
        }
    }

    private bool TryBuildWccMusicScheduleCandidate(bool fresh, out string? candidate, out string? problem)
    {
        candidate = _musicScheduleSourceDraft?.Text;
        problem = null;
        if (candidate is null) { problem = "There is no music or schedule source draft."; return false; }
        var definitions = GetWccMusicScheduleDefinitions(_pickedScheduleMacroName is null ? null : [_pickedScheduleMacroName], fresh);
        if (DefinitionSignature(definitions) != DefinitionSignature(_musicScheduleDefinitions))
        { problem = "A music or schedule definition changed. The pending draft has been retained."; return false; }
        if (_musicScheduleSourceBinding is not { } binding)
        { problem = "The original music and schedule source is unavailable."; return false; }
        var result = CreateWccMusicScheduleSettings(binding.Range).ComposePreview();
        candidate = result.CandidateSource;
        problem = result.Diagnostic;
        return result.IsSuccess;
    }
    public bool ApplyWccMusicScheduleDrafts()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!HasWccMusicScheduleDrafts) return true;
            var previous = IsUsingWccMusicScheduleSourceDraft;
            IsUsingWccMusicScheduleSourceDraft = true;
            try
            {
                if (!IsCurrentWccMusicScheduleSource())
                { _musicScheduleProblem = Status = "The source or editing context changed. These music and schedule drafts are retained."; RaiseWccMusicScheduleChanged(); return false; }
                if (!TryBuildWccMusicScheduleCandidate(fresh: true, out var candidate, out var problem))
                { _musicScheduleProblem = Status = problem ?? "This music or schedule draft cannot be applied."; RaiseWccMusicScheduleChanged(); return false; }
                if (HasWccInlineDrafts || HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft)
                { _musicScheduleProblem = Status = "Save the collected field, flag, event, music and schedule changes together."; RaiseWccMusicScheduleChanged(); return false; }
                var rawDraft = BeginWccScenarioSourceRawEdit();
                if (rawDraft is null || rawDraft.Text != _musicScheduleSourceDraft!.Text ||
                    !ReferenceEquals(rawDraft.Document, _musicScheduleSourceDraft.Document) ||
                    !IsCurrentWccMusicScheduleSource() || !ApplyWccInitialFieldRawEdit(rawDraft, candidate!))
                { _musicScheduleProblem = Status; RaiseWccMusicScheduleChanged(); return false; }
                ClearWccMusicScheduleDraft();
                RefreshWccMusicScheduleEditors();
                return true;
            }
            finally { IsUsingWccMusicScheduleSourceDraft = previous; }
        }
    }
    public void DiscardWccMusicScheduleDrafts()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy) return;
            ClearWccMusicScheduleDraft();
            RefreshWccMusicScheduleEditors();
        }
    }
    private void ClearWccMusicScheduleDraft()
    {
        _musicScheduleSourceDraft = null;
        _musicScheduleSourceBinding = null;
        _musicScheduleDraftVersion++;
        _wccMusicChanged = false;
        _wccScheduleEdit = new(ScenarioInitialScheduleEditKind.Preserve);
        _pickedScheduleMacroName = null;
        _musicScheduleProblem = null;
        _musicScheduleDisplayedDocument = null;
        _musicScheduleDisplayedSource = null;
        _musicScheduleCapturedProjection = null;
        _musicUsesCapturedAuthoring = false;
        _scheduleUsesCapturedAuthoring = false;
        _pendingScheduleImagePreviews = new Dictionary<int, CatalogPickerOptionVm>();
    }
    private static string DefinitionSignature(IEnumerable<WmlMacroExpansionDefinition> definitions) =>
        string.Join("\u001e", definitions.OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ThenBy(definition => definition.SourcePath, StringComparer.Ordinal)
            .Select(definition => string.Join("\u001f", definition.Name, definition.SourcePath, definition.PackageRevisionId, definition.TextDomain, definition.RawText)));
    private void RaiseWccMusicScheduleChanged()
    {
        if (_raisingWccMusicSchedule) return;
        _raisingWccMusicSchedule = true;
        try
        {
            foreach (var row in _wccMusicRows) row.Refresh();
            foreach (var row in _wccScheduleTimes) row.Refresh();
            foreach (var property in new[]
            {
                nameof(HasWccMusicScheduleDrafts), nameof(CanEditWccMusicPlaylist), nameof(CanEditWccSchedule), nameof(CanEditWccCustomSchedule),
                nameof(CanChangeWccCustomScheduleStructure), nameof(CanRemoveWccScheduleTime), nameof(CanMoveWccScheduleTimeToStart),
                nameof(CanApplyWccMusicScheduleDrafts), nameof(CanDiscardWccMusicScheduleDrafts),
                nameof(CanRemoveWccMusicRow), nameof(CanEditWccScheduleTime), nameof(CanMakeWccFirstTrackIntro), nameof(CanLetWccIntroFinish),
                nameof(WccMakeFirstTrackIntro), nameof(WccLetIntroFinish), nameof(WccMusicGuidance),
                nameof(WccMusicDefaultIntroHint), nameof(HasWccMusicDefaultIntroHint), nameof(WccScheduleIsPreset),
                nameof(WccSchedulePresetValue), nameof(CanMakeWccScheduleCustom), nameof(WccScheduleGuidance),
                nameof(WccScheduleStartDescription), nameof(WccScheduleStartIndex), nameof(WccMusicScheduleDraftStatus),
                nameof(WccSchedulePreviewImage), nameof(WccSchedulePreviewDescription), nameof(HasWccSchedulePreview),
                nameof(CanEditDocument), nameof(CanEditWccScenarioContent), nameof(WccScenarioContentGuidance)
            }) OnPropertyChanged(property);
            RefreshWccInitialFieldEditors();
            WccMusicScheduleDraftsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _raisingWccMusicSchedule = false; }
    }
}
