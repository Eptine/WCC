using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.Views;

public partial class ScenarioMapCoordinatePickerWindow : Window
{
    private readonly ScenarioMapCoordinatePickerViewModel _viewModel;
    public ScenarioMapCoordinatePickerWindow(ScenarioMapCoordinatePickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        MainMap.HoveredCellChanged += (_, _) => viewModel.HoverCell(MainMap.HoveredCell);
        Closing += (_, _) => viewModel.Cancel();
        Opened += async (_, _) =>
        {
            await viewModel.Completion;
            if (IsVisible) Close();
        };
    }
    private void ConfirmClicked(object? sender, RoutedEventArgs args)
    { if (_viewModel.Confirm()) Close(); }
    private void CancelClicked(object? sender, RoutedEventArgs args)
    { _viewModel.Cancel(); Close(); }
}
