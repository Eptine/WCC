using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.Views;

public partial class WccFieldVisibilityWindow : Window
{
    public WccFieldVisibilityWindow() => InitializeComponent();

    public WccFieldVisibilityWindow(WccFieldVisibilityDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
}
