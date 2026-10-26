using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Wcc.App.Views.Controls;

/// <summary>The sidebar entry is reusable and binding-testable without a native window.</summary>
public partial class ScenarioCreationLauncherView : UserControl
{
    public event EventHandler<RoutedEventArgs>? CreationRequested;
    public ScenarioCreationLauncherView() => InitializeComponent();
    private void CreateClicked(object? sender, RoutedEventArgs args) => CreationRequested?.Invoke(this, args);
}
