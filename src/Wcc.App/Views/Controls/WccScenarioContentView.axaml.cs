using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views.Controls;

public partial class WccScenarioContentView : UserControl
{
    public WccScenarioContentView() => InitializeComponent();

    internal static async Task OpenScenarioRawAsync(Control control, ScenarioWindowViewModel vm)
    {
        if (vm.GetWccScenarioSourceRawWml() is not { } raw) return;
        var draft = vm.BeginWccScenarioSourceRawEdit();
        Window window = draft is not null
            ? new ValidatedRawCodeEditorWindow("Edit scenario source", draft.Text,
                "Edit the authored [scenario] source, including macros and custom children. Generated output and shared macro definitions are not edited. Save the scenario file after applying.",
                text => vm.ValidateWccInitialFieldRawEdit(draft, text),
                text => vm.ApplyWccInitialFieldRawEdit(draft, text))
            : new RawCodeEditorWindow("View scenario source", raw, isReadOnly: true,
                descriptionText: vm.WccScenarioContentGuidance);
        await ShowAsync(control, window);
    }

    private async void ScenarioRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm) await OpenScenarioRawAsync(this, vm);
    }

    private async void FileRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm) return;
        Window? window = vm.BeginWccScenarioFileRawEdit() is { } draft
            ? new ValidatedRawCodeEditorWindow("Edit scenario file source", draft.Text,
                "Manual source for this working file, including local definitions, includes and text outside [scenario]. Keep its scenario blocks. Apply changes the working draft; Save writes this file.",
                text => vm.ValidateWccScenarioFileRawEdit(draft, text),
                text => vm.ApplyWccScenarioFileRawEdit(draft, text))
            : vm.BuildSourceWmlView() is { } source
                ? new RawCodeEditorWindow(source.Title, source.RawText, isReadOnly: true,
                    descriptionText: vm.WccScenarioContentGuidance)
                : null;
        if (window is not null) await ShowAsync(this, window);
    }

    private async void SelectedRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm) return;
        if (vm.BeginSelectedWccScenarioContentRawEdit() is { } draft)
            await OpenFragmentAsync(vm, draft);
        else if (vm.SelectedWccScenarioContent is { } entry)
            await ShowAsync(this, new RawCodeEditorWindow("View authored " + entry.Name, entry.Text,
                isReadOnly: true, descriptionText: vm.WccScenarioContentGuidance));
    }

    private async void AddRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm &&
            vm.BeginAddWccScenarioContentRawEdit("# Add a scenario child block, macro call or file include here.\n") is { } draft)
            await OpenFragmentAsync(vm, draft);
    }

    private async void SchedulePresetClicked(object? sender, RoutedEventArgs e) => await PickMacroAsync(true);
    private async void MusicMacroClicked(object? sender, RoutedEventArgs e) => await PickMacroAsync(false);

    private async Task PickMacroAsync(bool schedule)
    {
        if (DataContext is not ScenarioWindowViewModel vm || !vm.CanEditWccScenarioContent) return;
        var picker = new CatalogPickerWindow(new CatalogPickerWindowViewModel(
            vm.GetWccScenarioMacroOptions(schedule), null, allowMultiple: false,
            schedule ? "Select schedule preset" : "Select music macro",
            "Select one visible Library definition. Review its whole invocation and fill required arguments in RAW; it is appended in source order."));
        await ShowAsync(this, picker);
        if (picker.WasAccepted && picker.SelectedSelections.SingleOrDefault() is { } selection &&
            await vm.BeginWccScenarioMacroRawEditAsync(selection, schedule) is { } draft)
            await OpenFragmentAsync(vm, draft);
    }

    private async void MusicFilePickerClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm || !vm.CanEditWccScenarioContent) return;
        var picker = new CatalogPickerWindow(new CatalogPickerWindowViewModel(
            vm.GetWccScenarioMusicFileOptions(), null, allowMultiple: false, "Select music file",
            "Music files from the project's visible Library revisions. You can also enter a custom music filename."));
        await ShowAsync(this, picker);
        if (picker.WasAccepted && picker.SelectedSelections.SingleOrDefault() is { } selection &&
            vm.BeginWccScenarioMusicFileRawEdit(selection.EmittedValue) is { } draft)
            await OpenFragmentAsync(vm, draft);
    }

    private Task OpenFragmentAsync(ScenarioWindowViewModel vm, WccScenarioContentRawEditDraft draft) =>
        ShowAsync(this, new ValidatedRawCodeEditorWindow("Edit authored scenario content", draft.Text,
            "Keep the authored macro or block representation. A macro can also add events, settings or amendments. Applying changes the working source; Save writes the file.",
            text => vm.ValidateWccScenarioContentRawEdit(draft, text),
            text => vm.ApplyWccScenarioContentRawEdit(draft, text)));

    private static async Task ShowAsync(Control control, Window window)
    {
        if (TopLevel.GetTopLevel(control) is Window owner) await window.ShowDialog(owner);
        else window.Show();
    }
}
