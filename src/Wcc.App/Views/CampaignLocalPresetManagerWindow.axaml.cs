using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.Core.Scenarios;

namespace Wcc.App.Views;

public partial class CampaignLocalPresetManagerWindow : Window
{
    public CampaignLocalPresetManagerWindow(CampaignLocalPresetManagerViewModel vm)
    { InitializeComponent(); DataContext = vm; Closed += (_, _) => vm.Dispose(); }
    private async void CreatePresetClicked(object? sender, RoutedEventArgs args) => await CreatePresetAsync();
    private async System.Threading.Tasks.Task CreatePresetAsync(WccCampaignLocalPresetRole? preselectedRole = null)
    {
        if (DataContext is not CampaignLocalPresetManagerViewModel vm || vm.BeginCreateWizard(preselectedRole) is not { } wizard) return;
        var form = wizard.RequiresTypeSelection
            ? await new CampaignPresetTypeSelectionWindow(wizard).ShowDialog<CampaignPresetCreationForm?>(this)
            : wizard.BeginFields();
        if (form is null) return;
        await new CampaignPresetCreationWindow(form).ShowDialog(this);
        vm.Refresh();
    }
    private void RevertClicked(object? sender, RoutedEventArgs args) => (DataContext as CampaignLocalPresetManagerViewModel)?.Revert();
    private async void EditClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignLocalPresetManagerViewModel vm || vm.BeginEdit() is not { } form) return;
        await new CampaignPresetCreationWindow(form).ShowDialog(this); vm.Refresh();
    }
    private void DeleteClicked(object? sender, RoutedEventArgs args) => (DataContext as CampaignLocalPresetManagerViewModel)?.Delete();
    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    private async void SourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignLocalPresetManagerViewModel vm || vm.SelectedRow is not { } row) return;
        if (vm.BeginSelectedRaw() is { } draft)
            await new ValidatedRawCodeEditorWindow("Shared preset RAW - " + draft.SourcePath, draft.Text,
                "This is the shared definition. Applying changes updates the campaign working source; global Save writes it. Every invocation is affected.",
                text => vm.ValidateRaw(draft, text), text => vm.ApplyRaw(draft, text)).ShowDialog(this);
        else if (vm.BeginSelectedSharedRaw() is { } document)
            await new RawCodeEditorWindow(document).ShowDialog(this);
        else await new RawCodeEditorWindow("Preset source - " + row.Source, row.Text, isReadOnly: true,
            descriptionText: "Read-only captured definition. Save or discard pending changes before shared RAW editing.").ShowDialog(this);
    }
}
