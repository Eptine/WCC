using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

public sealed class WccMusicTrackRowVm : ViewModelBase
{
    private readonly ScenarioWindowViewModel _editor;
    private string _name;

    internal WccMusicTrackRowVm(ScenarioWindowViewModel editor, ScenarioInitialMusicTrack track)
    {
        _editor = editor;
        _name = track.Name;
        SourceId = track.SourceId;
    }

    internal string? SourceId { get; }
    internal ScenarioInitialMusicTrack Track => new(Name, SourceId);
    public string Name
    {
        get => _name;
        set => _editor.SetWccMusicTrackName(this, value);
    }
    internal void SetName(string value) => SetField(ref _name, value, nameof(Name));
    public bool CanEdit => _editor.CanEditWccMusicPlaylist;
    public string PositionLabel => (_editor.WccMusicRows.IndexOf(this) + 1).ToString();
    public string RoleLabel => _editor.WccMakeFirstTrackIntro && _editor.WccMusicRows.IndexOf(this) == 0 ? "Intro" : "Playlist";
    internal void Refresh()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(PositionLabel));
        OnPropertyChanged(nameof(RoleLabel));
    }
}

public sealed class WccScheduleTimeRowVm : ViewModelBase
{
    private CatalogPickerOptionVm? _preview;
    internal WccScheduleTimeRowVm(ScenarioWindowViewModel editor, ScenarioInitialTimeSlot time, int index)
    {
        Editor = editor;
        Time = time;
        Index = index;
    }
    internal ScenarioWindowViewModel Editor { get; }
    internal ScenarioInitialTimeSlot Time { get; }
    public int Index { get; }
    public string PositionLabel => (Index + 1).ToString();
    public string Id => Time.Id ?? "";
    public string Name => Time.Name ?? Id;
    public string Image => Time.Image ?? "";
    public CatalogPickerOptionVm? Preview
    {
        get => _preview;
        internal set => SetField(ref _preview, value);
    }
    public string BonusLabel => "Lawful bonus: " + (Time.LawfulBonus?.ToString() ?? "unknown");
    public string RawText => Time.RawWml;
    public bool CanEdit => Editor.CanEditWccCustomSchedule;
    public bool CanReorder => Editor.CanChangeWccCustomScheduleStructure;
    public bool IsStart => Editor.WccScheduleStartIndex == Index;
    public string StartLabel => IsStart ? "Starting time" : Editor.IsWccScheduleRandomSlot(Index) ? "Random start candidate" : "";
    public bool HasStartLabel => StartLabel.Length != 0;
    internal void Refresh()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(IsStart));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(HasStartLabel));
    }
}
