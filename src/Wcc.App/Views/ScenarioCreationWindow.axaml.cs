using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.Core.Scenarios;

namespace Wcc.App.Views;

public partial class ScenarioCreationWindow : Window
{
    public ScenarioCreationViewModel ViewModel { get; }
    public ScenarioCreationWindow(ScenarioCreationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent(); DataContext = ViewModel = viewModel;
        Closed += (_, _) => ViewModel.Dispose();
    }
    private void CancelClicked(object? sender, EventArgs args) => Close((ScenarioCreationRequest?)null);
    private void CreateRequested(object? sender, ScenarioCreationRequest request) => Close(request);
    private async void PickMapClicked(object? sender, RoutedEventArgs args) => await ViewModel.PickMapAsync(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Choose a Wesnoth map", AllowMultiple = false, FileTypeFilter = [new("Wesnoth maps") { Patterns = ["*.map"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return null;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("The map exceeds the 4 MiB source limit.");
        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length > 4 * 1024 * 1024) throw new IOException("The map changed beyond the 4 MiB source limit.");
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        return new WccScenarioCreationMap(Path.GetFileName(path), text, ViewModel.RenameMapToScenario);
    });
    private void RemoveMapClicked(object? sender, RoutedEventArgs args) => ViewModel.RemoveMap();
    private async void PickMusicClicked(object? sender, RoutedEventArgs args) => await ViewModel.PickMusicAsync(async vm =>
    { var picker = new CatalogPickerWindow(vm); await picker.ShowDialog(this); return picker.WasAccepted ? picker.SelectedSelections : null; });
    private void MusicAddClicked(object? sender, RoutedEventArgs args) => ViewModel.AddMusicName("");
    private void MusicUpClicked(object? sender, RoutedEventArgs args) { if ((sender as Control)?.DataContext is ScenarioCreationMusicRow row) ViewModel.MoveMusic(row, -1); }
    private void MusicDownClicked(object? sender, RoutedEventArgs args) { if ((sender as Control)?.DataContext is ScenarioCreationMusicRow row) ViewModel.MoveMusic(row, 1); }
    private void MusicRemoveClicked(object? sender, RoutedEventArgs args) { if ((sender as Control)?.DataContext is ScenarioCreationMusicRow row) ViewModel.RemoveMusic(row); }
    private async void PickScheduleClicked(object? sender, RoutedEventArgs args) => await ViewModel.PickScheduleAsync(async vm =>
    { var picker = new CatalogPickerWindow(vm); await picker.ShowDialog(this); return picker.WasAccepted ? picker.SelectedSelections.SingleOrDefault() : null; });
    private void CustomScheduleClicked(object? sender, RoutedEventArgs args) => ViewModel.UseCustomSchedule();
    private void NoScheduleClicked(object? sender, RoutedEventArgs args) => ViewModel.ClearSchedule();
    private async void TimeAddClicked(object? sender, RoutedEventArgs args) => await EditTime(null);
    private async void TimeEditClicked(object? sender, RoutedEventArgs args) { if (ViewModel.SelectedTime is { } row) await EditTime(row); }
    private async System.Threading.Tasks.Task EditTime(ScenarioCreationTimeRow? row)
    {
        if (!ViewModel.CanEditCustomSchedule) return;
        var dialog = new ValidatedRawCodeEditorWindow("Custom time", row?.RawText ?? "[time]\n    id=day\n    name=_ \"Day\"\n    lawful_bonus=0\n[/time]\n",
            "One literal [time] block. Applying changes only this wizard; Create writes the scenario.", ViewModel.ValidateCustomTime,
            text => { if (!(row is null ? ViewModel.AddCustomTime(text) : ViewModel.EditCustomTime(row, text))) throw new InvalidOperationException("The wizard or selected time changed."); });
        // This detached RAW form must never acquire shared-definition persistence.
        dialog.Opened += (_, _) => { if (dialog.FindControl<WmlEditorWorkspaceView>("CodeView") is { } view) view.SourceDocumentFactory = (_, _) => null; };
        await dialog.ShowDialog(this);
    }
    private void TimeUpClicked(object? sender, RoutedEventArgs args) { if (ViewModel.SelectedTime is { } row) ViewModel.MoveTime(row, -1); }
    private void TimeDownClicked(object? sender, RoutedEventArgs args) { if (ViewModel.SelectedTime is { } row) ViewModel.MoveTime(row, 1); }
    private void TimeRemoveClicked(object? sender, RoutedEventArgs args) { if (ViewModel.SelectedTime is { } row) ViewModel.RemoveTime(row); }
    private async void SidePresetClicked(object? sender, RoutedEventArgs args) => await ViewModel.PickSidePresetAsync(async vm =>
    { var picker = new WccCampaignPresetPickerWindow(vm); await picker.ShowDialog(this); return picker.WasAccepted ? vm.GetPresetSelection() : null; });
    private async void LeaderPresetClicked(object? sender, RoutedEventArgs args) => await ViewModel.PickLeaderPresetAsync(async vm =>
    { var picker = new WccCampaignPresetPickerWindow(vm); await picker.ShowDialog(this); return picker.WasAccepted ? vm.GetPresetSelection() : null; });
    private void CustomSideClicked(object? sender, RoutedEventArgs args) => ViewModel.ActiveSide?.UseCustomSide();
    private void CustomLeaderClicked(object? sender, RoutedEventArgs args) => ViewModel.ActiveSide?.UseCustomLeader();
    private void AddLeaderClicked(object? sender, RoutedEventArgs args) => ViewModel.ActiveSide?.AddLeader();
    private void RemoveLeaderClicked(object? sender, RoutedEventArgs args) => ViewModel.ActiveSide?.RemoveLeader();
    private async void VisibilityClicked(object? sender, RoutedEventArgs args)
    { if (ViewModel.ActiveSide is { } side) await new WccFieldVisibilityWindow(side.CreateVisibilityDialog()).ShowDialog(this); }
}
