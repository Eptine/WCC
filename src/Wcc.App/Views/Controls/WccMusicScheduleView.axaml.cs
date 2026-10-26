using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.References;
using Wcc.Core.Wml;
using Wcc.App.Views;

namespace Wcc.App.Views.Controls;

public partial class WccMusicScheduleView : UserControl
{
    private static readonly DataFormat<string> MusicDragFormat = DataFormat.CreateStringApplicationFormat("wcc.music-row");
    private readonly string _dragToken = Guid.NewGuid().ToString("N");
    private WccMusicTrackRowVm? _draggedMusicRow;

    public WccMusicScheduleView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, MusicDragOver);
        AddHandler(DragDrop.DropEvent, MusicDropped);
    }

    private async void PickMusicClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ScenarioWindowViewModel { CanEditWccMusicPlaylist: true } vm ||
            TopLevel.GetTopLevel(this) is not Window owner || vm.CaptureWccMusicSchedulePickerSource() is not { } source) return;
        var picker = new CatalogPickerWindow(new CatalogPickerWindowViewModel(vm.GetWccMusicPlaylistPickerOptions(),
            null, allowMultiple: true, "Pick playlist tracks", "Used playlist tracks are already selected. New selections are added; remove tracks in the playlist. Core Campaign and WCC music is copied into this campaign.",
            currentSelections: vm.GetWccMusicPlaylistCurrentSelections()));
        await picker.ShowDialog(owner);
        if (picker.WasAccepted && ReferenceEquals(DataContext, vm) && vm.IsCurrentWccMusicSchedulePickerSource(source))
            await vm.AddWccMusicSelectionsAsync(picker.SelectedSelections);
    }
    private async void AddMusicFileClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ScenarioWindowViewModel { CanEditWccMusicPlaylist: true } vm ||
            TopLevel.GetTopLevel(this) is not { } top || vm.CaptureWccMusicSchedulePickerSource() is not { } source) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add campaign music files", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Music") { Patterns = WccPackageMusicImporter.FilePatterns }]
        });
        if (!ReferenceEquals(DataContext, vm) || !vm.IsCurrentWccMusicSchedulePickerSource(source)) return;
        var paths = files.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Cast<string>().ToArray();
        if (paths.Length == 0) return;
        var names = await vm.ImportWccScenarioMusicFilesAsync(paths);
        if (ReferenceEquals(DataContext, vm) && vm.IsCurrentWccMusicSchedulePickerSource(source)) vm.AddWccMusicTracks(names);
    }
    private void RemoveMusicClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is ScenarioWindowViewModel vm &&
            (sender as Control)?.DataContext is WccMusicTrackRowVm row) vm.RemoveWccMusicTrack(row);
    }
    private void MusicUpClicked(object? sender, RoutedEventArgs args) => MoveMusic(sender, -1);
    private void MusicDownClicked(object? sender, RoutedEventArgs args) => MoveMusic(sender, 1);
    private void MoveMusic(object? sender, int direction)
    {
        if (DataContext is ScenarioWindowViewModel vm && sender is Control { DataContext: WccMusicTrackRowVm row })
            vm.MoveWccMusicTrack(row, vm.WccMusicRows.IndexOf(row) + direction);
    }
    private async void MusicDragPressed(object? sender, PointerPressedEventArgs args)
    {
        if (DataContext is not ScenarioWindowViewModel { CanEditWccMusicPlaylist: true } vm ||
            sender is not Control { DataContext: WccMusicTrackRowVm row } control ||
            !args.GetCurrentPoint(control).Properties.IsLeftButtonPressed || !vm.WccMusicRows.Contains(row)) return;
        _draggedMusicRow = row;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(MusicDragFormat, _dragToken));
        try { await DragDrop.DoDragDropAsync(args, transfer, DragDropEffects.Move); }
        finally { _draggedMusicRow = null; }
    }
    private void MusicDragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = _draggedMusicRow is not null && args.DataTransfer.TryGetValue(MusicDragFormat) == _dragToken &&
            DataContext is ScenarioWindowViewModel { CanEditWccMusicPlaylist: true } ? DragDropEffects.Move : DragDropEffects.None;
        args.Handled = true;
    }
    private void MusicDropped(object? sender, DragEventArgs args)
    {
        if (_draggedMusicRow is not { } row || args.DataTransfer.TryGetValue(MusicDragFormat) != _dragToken ||
            DataContext is not ScenarioWindowViewModel vm) return;
        var target = args.Source as Control;
        while (target is not null && target.DataContext is not WccMusicTrackRowVm) target = target.Parent as Control;
        if (target?.DataContext is WccMusicTrackRowVm destination) vm.MoveWccMusicTrack(row, vm.WccMusicRows.IndexOf(destination));
        args.Handled = true;
    }

    private async void PickScheduleClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ScenarioWindowViewModel { CanEditWccSchedule: true } vm ||
            TopLevel.GetTopLevel(this) is not Window owner || vm.CaptureWccMusicSchedulePickerSource() is not { } source) return;
        var picker = new CatalogPickerWindow(new CatalogPickerWindowViewModel(vm.GetWccInitialSchedulePickerOptions(),
            null, allowMultiple: false, "Pick schedule preset", "Select a schedule macro. Choose Custom to use literal times instead."));
        await picker.ShowDialog(owner);
        if (!picker.WasAccepted || !ReferenceEquals(DataContext, vm) || !vm.IsCurrentWccMusicSchedulePickerSource(source) ||
            picker.SelectedSelections.SingleOrDefault() is not { } selection ||
            await vm.BeginWccSchedulePresetChoiceAsync(selection) is not { } choice) return;
        if (!ReferenceEquals(DataContext, vm) || !vm.IsCurrentWccMusicSchedulePickerSource(source)) return;
        if (choice.HasRequiredArguments)
            await new ValidatedRawCodeEditorWindow("Schedule arguments", choice.Invocation,
                "Fill in the selected schedule's required arguments. This updates the collected source draft.",
                text => vm.ValidateWccSchedulePresetChoice(choice, text), text => vm.StageWccSchedulePresetChoice(choice, text)).ShowDialog(owner);
        else vm.StageWccSchedulePresetChoice(choice, choice.Invocation);
    }
    private void CustomScheduleClicked(object? sender, RoutedEventArgs args) => (DataContext as ScenarioWindowViewModel)?.MakeWccScheduleCustom();
    private async void EditTimeClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ScenarioWindowViewModel { CanEditWccScheduleTime: true, SelectedWccScheduleTime: { } row } vm ||
            TopLevel.GetTopLevel(this) is not Window owner || vm.CaptureWccMusicSchedulePickerSource() is not { } source) return;
        await new ValidatedRawCodeEditorWindow("Edit custom time", row.RawText,
            "Edit all attributes of this local [time] block. It stays in the schedule draft until Save.",
            text => vm.IsCurrentWccMusicSchedulePickerSource(source) ? vm.ValidateWccScheduleTimeRawEdit(row, text) : "The source or editing context changed.",
            text => vm.StageWccScheduleTimeRawEdit(row, text)).ShowDialog(owner);
    }
    private async void AddTimeClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ScenarioWindowViewModel { CanChangeWccCustomScheduleStructure: true } vm ||
            TopLevel.GetTopLevel(this) is not Window owner || vm.CaptureWccMusicSchedulePickerSource() is not { } source) return;
        await new ValidatedRawCodeEditorWindow("Add custom time", "[time]\nid=new_time\nname=_ \"New time\"\nlawful_bonus=0\n[/time]",
            "Add a complete local time block to the schedule draft.",
            text => vm.IsCurrentWccMusicSchedulePickerSource(source) ? vm.ValidateWccScheduleTimeAddition(text) : "The source or editing context changed.",
            text => vm.AddWccScheduleTime(text)).ShowDialog(owner);
    }
    private void RemoveTimeClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is ScenarioWindowViewModel vm &&
          (sender as Control)?.DataContext is WccScheduleTimeRowVm row) vm.RemoveWccScheduleTime(row); }
    private void StartTimeClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is ScenarioWindowViewModel { SelectedWccScheduleTime: { } row } vm) vm.SetWccScheduleStartingSlot(row); }
    private void TimeUpClicked(object? sender, RoutedEventArgs args) => MoveTime(sender, -1);
    private void TimeDownClicked(object? sender, RoutedEventArgs args) => MoveTime(sender, 1);
    private void MoveTime(object? sender, int direction)
    {
        if (DataContext is ScenarioWindowViewModel vm && sender is Control { DataContext: WccScheduleTimeRowVm row })
            vm.MoveWccScheduleTime(row, row.Index + direction);
    }
    private void DiscardClicked(object? sender, RoutedEventArgs args) => (DataContext as ScenarioWindowViewModel)?.DiscardWccMusicScheduleDrafts();
}
