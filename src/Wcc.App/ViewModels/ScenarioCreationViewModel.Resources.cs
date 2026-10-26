using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;
using Wcc.Core.Maps;

namespace Wcc.App.ViewModels;

public sealed class ScenarioCreationMusicRow(string name) : ViewModelBase
{
    private string _name = name;
    public string Name { get => _name; set => SetField(ref _name, value ?? ""); }
}
public sealed class ScenarioCreationTimeRow(ScenarioInitialTimeSlot time) : ViewModelBase
{
    public ScenarioInitialTimeSlot Time { get; internal set; } = time;
    public string Name => Time.Name ?? Time.Id ?? "Time";
    public string RawText => Time.RawWml;
    public string BonusLabel => "Lawful bonus: " + (Time.LawfulBonus ?? "0");
}

public sealed partial class ScenarioCreationViewModel
{
    private WccScenarioCreationMap? _map;
    private bool _generatedMap;
    private ScenarioCreationScheduleChoice? _schedule;
    private bool _customSchedule;
    private bool _intro;
    private bool _finish;
    private string? _interactionProblem;
    private string? _coordinateMapText;
    private WccScenarioCreationMap? _coordinateMapSource;
    private ScenarioMapPreviewVm? _coordinateMap;
    private string CoordinateMapText => _generatedMap && TrySideCount(out var count)
        ? WccScenarioCreationWriter.RenderDefaultMap(count) : _map?.Text ?? WccScenarioCreationWriter.DefaultMap;
    private ScenarioMapPreviewVm? CaptureCoordinateMap()
    {
        if (_disposed || !_current() || _setup is null) return null;
        var text = CoordinateMapText;
        if (_coordinateMapText != text || !ReferenceEquals(_coordinateMapSource, _map))
        {
            _coordinateMapText = text; _coordinateMapSource = _map;
            var map = WesnothMapParser.Parse(text, new WesnothMapParserOptions { DefaultBorderSize = 1, IgnoreLegacyBorderSize = true });
            _coordinateMap = map.Diagnostics.Count != 0 ? null : new(map, _setup.TerrainCatalog,
                _generatedMap ? "Generated starter map" : _map?.FileName ?? "Scenario map", null);
        }
        return _coordinateMap;
    }
    public ObservableCollection<ScenarioCreationMusicRow> MusicTracks { get; } = [];
    public ObservableCollection<ScenarioCreationTimeRow> ScheduleTimes { get; } = [];
    public ScenarioCreationMusicRow? SelectedMusicTrack { get; set; }
    public ScenarioCreationTimeRow? SelectedTime { get; set; }
    public bool HasMap => _map is not null && !_generatedMap;
    public string MapSummary => _generatedMap ? "Generated starter map (one castle for each side)." : _map is null
        ? "Generated starter map will be used." : _map.FileName + " -> maps/" + MapOutputName;
    private string MapOutputName => _generatedMap ? EffectiveStem + ".map" : _map is { RenameToScenario: false } map ? map.FileName :
        WccScenarioCreationPlanner.MapFileNameForScenarioName(Name, EffectiveStem);
    public bool RenameMapToScenario
    {
        get => _map?.RenameToScenario == true;
        set { if (_map is { } map && map.RenameToScenario != value) { _map = map with { RenameToScenario = value }; DraftChanged(); } }
    }
    public bool MakeFirstTrackIntro { get => _intro; set { if (SetField(ref _intro, value)) DraftChanged(); } }
    public bool LetIntroMusicFinish { get => _finish; set { if (SetField(ref _finish, value)) DraftChanged(); } }
    public string ScheduleLabel => _customSchedule ? "Custom" : _schedule?.Invocation ?? "None (fixed neutral time)";
    public bool IsCustomSchedule => _customSchedule;
    public bool CanEditCustomSchedule => CanInteract && _customSchedule;
    public bool CanPickSidePreset => CanInteract && ActiveSide is not null && _setup?.Presets.Any(entry => entry.Role == WccCampaignLocalPresetRole.Side) == true;
    public bool CanPickLeaderPreset => CanInteract && ActiveSide is { CanEditLeaders: true } side && side.Fields.SelectedLeader is not null &&
        _setup?.Presets.Any(entry => entry.Role == WccCampaignLocalPresetRole.Leader) == true;
    public string ResourceGuidance => "Pick music already available in Core or this campaign. Asset copying is available after creation in the scenario editor. Custom schedule entries are literal [time] blocks.";

    private async Task<bool> Interact<T>(Func<Task<T>> choose, Func<T, bool> apply)
    {
        if (!CanInteract) return false;
        var step = _step; var version = _draftVersion; var side = ActiveSide; var leader = side?.Fields.SelectedLeader;
        _interactionProblem = null; _interactionBusy = true; RefreshState();
        try
        {
            var result = await choose();
            if (_disposed || !_current() || step != _step || version != _draftVersion || !ReferenceEquals(side, ActiveSide) ||
                !ReferenceEquals(leader, side?.Fields.SelectedLeader)) return false;
            return apply(result);
        }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { _interactionProblem = refusal.Message; return false; }
        finally { _interactionBusy = false; if (!_disposed) RefreshState(); }
    }
    public Task<bool> PickMapAsync(Func<Task<WccScenarioCreationMap?>> choose) => Interact(choose, map =>
    {
        if (map is null) return false;
        _map = map; _generatedMap = false; DraftChanged(); return true;
    });
    public void RemoveMap()
    {
        if (!CanInteract || !TrySideCount(out var count)) return;
        _generatedMap = true; _map = new("starter.map", WccScenarioCreationWriter.RenderDefaultMap(count), true); DraftChanged();
    }
    public Task<bool> PickMusicAsync(Func<CatalogPickerWindowViewModel, Task<IReadOnlyList<CatalogPickerSelection>?>> choose)
    {
        if (_setup is null) return Task.FromResult(false);
        var options = _setup.Music;
        var picker = new CatalogPickerWindowViewModel(options, string.Join(',', MusicTracks.Select(row => row.Name)), true,
            "Choose scenario music", "Core and current campaign music; this selection stages references and does not copy files.");
        return Interact(() => choose(picker), selections =>
        {
            if (selections is null) return false;
            var captured = selections.Select(selection => options.SingleOrDefault(option => option.ToSelection() == selection)
                ?? throw new InvalidOperationException("The music selection is outside the captured wizard choices.")).ToArray();
            foreach (var match in captured)
            {
                if (MusicTracks.Any(row => row.Name == match.ValueId)) continue;
                var row = new ScenarioCreationMusicRow(match.ValueId); row.PropertyChanged += MusicChanged; MusicTracks.Add(row);
            }
            DraftChanged(); return true;
        });
    }
    public void AddMusicName(string name)
    {
        if (!CanInteract) return;
        var row = new ScenarioCreationMusicRow(name); row.PropertyChanged += MusicChanged; MusicTracks.Add(row); DraftChanged();
    }
    private void MusicChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => DraftChanged();
    public void MoveMusic(ScenarioCreationMusicRow row, int delta)
    {
        if (!CanInteract) return;
        var index = MusicTracks.IndexOf(row); var target = index + delta;
        if (index >= 0 && target >= 0 && target < MusicTracks.Count) { MusicTracks.Move(index, target); DraftChanged(); }
    }
    public void RemoveMusic(ScenarioCreationMusicRow row)
    { if (CanInteract && MusicTracks.Remove(row)) { row.PropertyChanged -= MusicChanged; DraftChanged(); } }
    public Task<bool> PickScheduleAsync(Func<CatalogPickerWindowViewModel, Task<CatalogPickerSelection?>> choose)
    {
        if (_setup is null) return Task.FromResult(false);
        var choices = _setup.Schedules;
        var picker = new CatalogPickerWindowViewModel(choices.Select(choice => choice.Option), _schedule?.Option.ValueId, false,
            "Choose schedule", "Captured Core and campaign schedule macros. Custom times can be edited directly below.");
        return Interact(() => choose(picker), selection =>
        {
            if (selection is null) return false;
            var choice = choices.SingleOrDefault(choice => choice.Option.ToSelection() == selection);
            if (choice is null) throw new InvalidOperationException("The schedule selection is outside the captured wizard choices.");
            _schedule = choice; _customSchedule = false; ResetScheduleRows(); DraftChanged(); return true;
        });
    }
    public void UseCustomSchedule()
    {
        if (!CanInteract) return;
        _customSchedule = true;
        if (ScheduleTimes.Count == 0) AddCustomTime("[time]\n    id=day\n    name=_ \"Day\"\n    lawful_bonus=0\n[/time]\n");
        DraftChanged();
    }
    public void ClearSchedule()
    { if (CanInteract) { _customSchedule = false; _schedule = null; ScheduleTimes.Clear(); DraftChanged(); } }
    private void ResetScheduleRows()
    {
        ScheduleTimes.Clear();
        foreach (var time in _schedule?.Times ?? []) ScheduleTimes.Add(new(time));
        SelectedTime = ScheduleTimes.FirstOrDefault();
    }
    public string? ValidateCustomTime(string raw)
    {
        if (WmlSingleBlockValidator.ValidateLiteral(raw, "time") is { IsValid: false } invalid) return invalid.Problem;
        var projection = ScenarioInitialScheduleEditor.Project("[scenario]\n" + raw + "\n[/scenario]\n", _schedule?.Definitions ?? []);
        return projection.CanCompose && projection.Times.Count == 1 ? null : projection.Diagnostic ?? "One complete literal [time] block is required.";
    }
    public bool AddCustomTime(string raw)
    {
        if (!CanEditCustomSchedule || ValidateCustomTime(raw) is not null) return false;
        var projection = ScenarioInitialScheduleEditor.Project("[scenario]\n" + raw + "\n[/scenario]\n", _schedule?.Definitions ?? []);
        var row = new ScenarioCreationTimeRow(projection.Times[0] with { RawWml = raw }); ScheduleTimes.Add(row); SelectedTime = row; DraftChanged(); return true;
    }
    public bool EditCustomTime(ScenarioCreationTimeRow row, string raw)
    {
        if (!CanEditCustomSchedule || !ScheduleTimes.Contains(row) || ValidateCustomTime(raw) is not null) return false;
        var index = ScheduleTimes.IndexOf(row);
        var projection = ScenarioInitialScheduleEditor.Project("[scenario]\n" + raw + "\n[/scenario]\n", _schedule?.Definitions ?? []);
        var replacement = new ScenarioCreationTimeRow(projection.Times[0] with { RawWml = raw });
        ScheduleTimes[index] = replacement; SelectedTime = replacement; DraftChanged(); return true;
    }
    public void MoveTime(ScenarioCreationTimeRow row, int delta)
    {
        if (!CanEditCustomSchedule) return;
        var index = ScheduleTimes.IndexOf(row); var target = index + delta;
        if (index >= 0 && target >= 0 && target < ScheduleTimes.Count) { ScheduleTimes.Move(index, target); DraftChanged(); }
    }
    public void RemoveTime(ScenarioCreationTimeRow row)
    { if (CanEditCustomSchedule && ScheduleTimes.Remove(row)) { SelectedTime = ScheduleTimes.FirstOrDefault(); DraftChanged(); } }
    public Task<bool> PickSidePresetAsync(Func<WccCampaignPresetPickerVm, Task<WccCampaignPresetSelection?>> choose) =>
        PickPresetAsync(WccCampaignLocalPresetRole.Side, choose);
    public Task<bool> PickLeaderPresetAsync(Func<WccCampaignPresetPickerVm, Task<WccCampaignPresetSelection?>> choose) =>
        PickPresetAsync(WccCampaignLocalPresetRole.Leader, choose);
    private Task<bool> PickPresetAsync(WccCampaignLocalPresetRole role, Func<WccCampaignPresetPickerVm, Task<WccCampaignPresetSelection?>> choose)
    {
        var side = ActiveSide;
        if (_setup is null || side is null || !(role == WccCampaignLocalPresetRole.Side ? CanPickSidePreset : CanPickLeaderPreset)) return Task.FromResult(false);
        var binding = WccCampaignPresetSelectionBinding.CaptureAvailable(role, _setup.Presets, _setup.Plan, () => !_disposed && _current());
        var picker = new WccCampaignPresetPickerVm(binding);
        return Interact(() => choose(picker), selection =>
        {
            if (selection is null) return false;
            if (!ReferenceEquals(selection.Binding, binding) || !binding.ContainsCurrent(selection.Entry))
                throw new InvalidOperationException("The selected preset no longer belongs to this captured campaign catalog.");
            if (role == WccCampaignLocalPresetRole.Side) side.UseSidePreset(selection.Entry, selection.IdentityOverride);
            else side.UseLeaderPreset(selection.Entry, selection.IdentityOverride);
            return true;
        });
    }
    private WccScenarioCreationMusic? BuildMusic() => MusicTracks.Count == 0 && !_intro && !_finish ? null :
        new(Array.AsReadOnly(MusicTracks.Select(row => new ScenarioInitialMusicTrack(row.Name)).ToArray()), _intro, _finish, _setup?.MusicDefinitions);
    private WccScenarioCreationSchedule? BuildSchedule() => _customSchedule
        ? new(new(ScenarioInitialScheduleEditKind.Custom, TimeBlocks: Array.AsReadOnly(ScheduleTimes.Select(row => row.RawText).ToArray()),
            Start: new(ScenarioInitialScheduleStartKind.Slot, 0)), _schedule?.Definitions ?? [])
        : _schedule is { } schedule ? new(new(ScenarioInitialScheduleEditKind.Preset, schedule.Invocation), schedule.Definitions) : null;
}
