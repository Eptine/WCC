using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.Views.Controls;

/// <summary>Map-only workspace reusable in the scenario page or a coordinate picker host.</summary>
public partial class WccScenarioMapWorkspaceView : UserControl
{
    private ContextMenu? _unitContextMenu;
    public static readonly StyledProperty<double> MapViewportHeightProperty =
        AvaloniaProperty.Register<WccScenarioMapWorkspaceView, double>(nameof(MapViewportHeight), 440);
    public double MapViewportHeight { get => GetValue(MapViewportHeightProperty); private set => SetValue(MapViewportHeightProperty, value); }

    public WccScenarioMapWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => MapViewportHeight = Math.Clamp(args.NewSize.Width * 0.4, 340, 660);
        MainMap.CellContextRequested += MapCellContextRequested;
        DataContextChanged += (_, _) => { _unitContextMenu?.Close(); _unitContextMenu = null; };
        DetachedFromVisualTree += (_, _) => { _unitContextMenu?.Close(); _unitContextMenu = null; };
    }

    private void ReloadMapClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is ScenarioWindowViewModel vm) vm.RefreshMapPreview(); }
    private void ConfirmCoordinateClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is ScenarioWindowViewModel vm) vm.MapPreview?.ConfirmCoordinateSelection(); }
    private void CancelCoordinateClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is ScenarioWindowViewModel vm) vm.MapPreview?.CancelCoordinateSelection(); }

    private void MapCellContextRequested(object? sender, ScenarioMapCellContextEventArgs args)
    {
        if (DataContext is not ScenarioWindowViewModel vm || !ReferenceEquals(vm.MapPreview, args.Preview) || args.Preview.IsCoordinateSelectionActive) return;
        _unitContextMenu?.Close();
        var add = new MenuItem { Name = "AddInitialUnitContextAction", Header = $"Add initial unit here ({args.Cell.X}, {args.Cell.Y})",
            IsEnabled = !args.Cell.IsBorder && vm.CanAddInitialUnits };
        add.Click += async (_, _) =>
        {
            if (!ReferenceEquals(DataContext, vm) || !ReferenceEquals(MainMap.Preview, args.Preview)) return;
            if (vm.BeginInitialUnitAdd(args.Preview, args.Cell) is { } draft) await OpenUnitAsync(vm, draft);
        };
        _unitContextMenu = new ContextMenu { ItemsSource = new[] { add } };
        _unitContextMenu.Open(MainMap);
    }
    private async void EditInitialUnitClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is ScenarioWindowViewModel vm && sender is Control { DataContext: WccInitialUnitRowVm row } &&
            vm.BeginInitialUnitEdit(row) is { } draft) await OpenUnitAsync(vm, draft);
    }
    private void RemoveInitialUnitClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is ScenarioWindowViewModel vm && sender is Control { DataContext: WccInitialUnitRowVm row } &&
            !vm.TryRemoveInitialUnit(row, out var problem)) vm.Status = problem ?? "The initial unit could not be removed in the current context.";
    }
    private void FocusInitialUnitClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is ScenarioWindowViewModel vm && sender is Control { DataContext: WccInitialUnitRowVm row }) vm.FocusInitialUnit(row); }
    private async Task OpenUnitAsync(ScenarioWindowViewModel vm, WccInitialUnitEditDraft draft)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            using var model = new UnitPlacementWindowViewModel(draft);

            var window = new UnitPlacementWindow(model);
            await window.ShowDialog(owner);
            if (window.ResultPlacement is { } result && !draft.TryStage(result, out var problem))
                vm.Status = problem ?? "The initial unit changed before it could be queued. Reopen its editor.";
        }
        finally { draft.Cancel(); }
    }
}
