using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.Views;

public partial class UnitTypeCreationWindow : Window
{
    private bool _closed;
    public UnitTypeCreationViewModel ViewModel { get; }

    public UnitTypeCreationWindow(UnitTypeCreationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent(); DataContext = ViewModel = viewModel;
        Closed += (_, _) => { _closed = true; ViewModel.Dispose(); };
    }

    private void CancelClicked(object? sender, RoutedEventArgs args) => Close((UnitTypeCreationRequest?)null);
    private void BackClicked(object? sender, RoutedEventArgs args) => ViewModel.MoveBack();
    private void NextClicked(object? sender, RoutedEventArgs args) => ViewModel.MoveNext();
    private void CreateClicked(object? sender, RoutedEventArgs args)
    { if (!_closed && ViewModel.TryCreateRequest(out var request)) Close(request); }
    private async void PickBaseClicked(object? sender, RoutedEventArgs args) => await ViewModel.PickBaseAsync(PresentPicker);
    private async void PickCopyClicked(object? sender, RoutedEventArgs args) => await ViewModel.PickCopySourceAsync(PresentPicker);
    private async void PickRaceClicked(object? sender, RoutedEventArgs args) => await ViewModel.PickRaceAsync(PresentPicker);
    private async void PickMovementTypeClicked(object? sender, RoutedEventArgs args) => await ViewModel.PickMovementTypeAsync(PresentPicker);
    private async Task<CatalogPickerSelection?> PresentPicker(CatalogPickerWindowViewModel viewModel)
    {
        if (_closed) return null;
        var picker = new CatalogPickerWindow(viewModel);
        await picker.ShowDialog(this);
        return !_closed && picker.WasAccepted ? picker.SelectedSelections.SingleOrDefault() : null;
    }
}
