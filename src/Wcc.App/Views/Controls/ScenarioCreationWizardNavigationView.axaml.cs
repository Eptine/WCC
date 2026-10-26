using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views.Controls;

public partial class ScenarioCreationWizardNavigationView : UserControl
{
    public ScenarioCreationWizardNavigationView() => InitializeComponent();
    public event EventHandler? CancelRequested;
    public event EventHandler<ScenarioCreationRequest>? CreateRequested;
    private void CancelClicked(object? sender, RoutedEventArgs args) => CancelRequested?.Invoke(this, EventArgs.Empty);
    private void BackClicked(object? sender, RoutedEventArgs args) => (DataContext as ScenarioCreationViewModel)?.MoveBack();
    private void NextClicked(object? sender, RoutedEventArgs args) => (DataContext as ScenarioCreationViewModel)?.MoveNext();
    private void CreateClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is ScenarioCreationViewModel vm && vm.TryCreateRequest(out var request)) CreateRequested?.Invoke(this, request!); }
}
