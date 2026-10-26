using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class WccSideManagementWindow : Window
{
    public WccSideManagementWindow() => InitializeComponent();
    public WccSideManagementWindow(WccSideTopologyManagementVm viewModel) : this()
    { DataContext = viewModel; Closed += (_, _) => viewModel.Dispose(); }
    private void AddClicked(object? sender, RoutedEventArgs args) { if (DataContext is WccSideTopologyManagementVm vm) vm.Add(); }
    private async void AddPresetClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not WccSideTopologyManagementVm vm) return;
        await vm.AddFromPresetAsync(async picker =>
        {
            var window = new WccCampaignPresetPickerWindow(picker); await window.ShowDialog(this);
            return window.WasAccepted ? picker.GetPresetSelection() : null;
        });
    }
    private void RemoveClicked(object? sender, RoutedEventArgs args) { if (DataContext is WccSideTopologyManagementVm vm) vm.RemoveSelected(); }
    private void RestoreClicked(object? sender, RoutedEventArgs args) { if (DataContext is WccSideTopologyManagementVm vm) vm.RestoreSelected(); }
    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
}
