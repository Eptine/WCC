using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.Core.Scenarios;

namespace Wcc.App.Views;

public partial class UnitPlacementWindow : Window
{
    public UnitPlacementResult? ResultPlacement { get; private set; }
    public WccScenarioUnitTemplate? ResultTemplate { get; private set; }
    public UnitPlacementResult? Result => ResultPlacement;
    public UnitPlacementWindow() { InitializeComponent(); Activated += (_, _) => (DataContext as UnitPlacementWindowViewModel)?.RefreshAvailability();
        Closed += (_, _) => (DataContext as UnitPlacementWindowViewModel)?.Dispose(); }
    public UnitPlacementWindow(UnitPlacementWindowViewModel model) : this() => DataContext = model;
    private async void PickTypeClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not UnitPlacementWindowViewModel model || model.BeginTypePick() is not { } capture) return;
        var picker = new CatalogPickerWindow(capture.Picker);
        await picker.ShowDialog(this);
        if (ReferenceEquals(DataContext, model) && picker.WasAccepted)
            model.TrySetPickedType(capture, picker.SelectedSelections.Count == 1 ? picker.SelectedSelections.Single() : null);
    }
    private async void PickCoordinatesClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is UnitPlacementWindowViewModel model) await model.PickCoordinatesAsync();
    }
    private void ApplyClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not UnitPlacementWindowViewModel model) return;
        if (model.IsTemplateMode)
        { if (model.TryCreateTemplate(out var template, out _)) { ResultTemplate = template; Close(); } }
        else if (model.TryCreateResult(out var result, out _)) { ResultPlacement = result; Close(result); }
    }
    private void DiscardClicked(object? sender, RoutedEventArgs args) { ResultPlacement = null; ResultTemplate = null; Close(null); }
}
