using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class CampaignPresetTypeSelectionWindow : Window
{
    public CampaignPresetTypeSelectionWindow(CampaignPresetCreationWizardViewModel vm)
    { InitializeComponent(); DataContext = vm; }
    private void ContinueClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignPresetCreationWizardViewModel vm && vm.BeginFields() is { } form) Close(form);
    }
    private void CancelClicked(object? sender, RoutedEventArgs args) => Close();
}