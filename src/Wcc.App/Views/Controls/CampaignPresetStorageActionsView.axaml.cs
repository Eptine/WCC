using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views.Controls;

public partial class CampaignPresetStorageActionsView : UserControl
{
    public CampaignPresetStorageActionsView() => InitializeComponent();
    private void MoveClicked(object? sender, RoutedEventArgs args) => (DataContext as CampaignLocalPresetManagerViewModel)?.MoveMainPresets();
    private void RevertClicked(object? sender, RoutedEventArgs args) => (DataContext as CampaignLocalPresetManagerViewModel)?.RevertMove();
}
