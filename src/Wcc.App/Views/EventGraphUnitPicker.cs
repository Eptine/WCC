using Wcc.Core.Scenarios;
using System;
using System.Globalization;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Maps;

namespace Wcc.App.Views;

internal static class EventGraphUnitPicker
{
    internal static void Attach(EventNodeGraphWindowViewModel model, EventNodeGraphWindow window,
        Func<Wcc.Core.Scenarios.WccScenarioEventAction, UnitPlacementWindowViewModel?> create, Func<bool> current) =>
        Attach(model, window, create, current, () => null);

    internal static void Attach(EventNodeGraphWindowViewModel model, EventNodeGraphWindow window,
        Func<Wcc.Core.Scenarios.WccScenarioEventAction, UnitPlacementWindowViewModel?> create, Func<bool> current,
        Func<ScenarioMapPreviewVm?> currentMap)
    {
        model.SpawnUnitEditor = async action =>
        {
            if (!current() || !ReferenceEquals(window.DataContext, model)) return null;
            using var unit = create(action);
            if (unit is null || !unit.CanEdit) return null;
            var picker = new UnitPlacementWindow(unit);
            var sourceMap = currentMap();
            var graphCoordinatePicker = model.CoordinatePicker;
            if (sourceMap is { Map: { HasErrors: false, PlayableWidth: > 0, PlayableHeight: > 0 } })
            {
                bool MapCurrent() => current() && ReferenceEquals(currentMap(), sourceMap) &&
                    ReferenceEquals(model.CoordinatePicker, graphCoordinatePicker) &&
                    ReferenceEquals(window.DataContext, model) && ReferenceEquals(picker.DataContext, unit);
                unit.CoordinatePicker = async (x, y) =>
                {
                    if (!MapCurrent()) return null;
                    ScenarioMapCoordinate? initial = int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var initialX) &&
                        int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var initialY) ? new(initialX, initialY) : null;
                    using var coordinate = new ScenarioMapCoordinatePickerViewModel(sourceMap,
                        new("Choose a playable hex for the spawned unit.", initial));
                    var coordinateWindow = new ScenarioMapCoordinatePickerWindow(coordinate);
                    await coordinateWindow.ShowDialog(picker);
                    var selected = await coordinate.Completion;
                    if (!selected.IsConfirmed || !MapCurrent()) return null;
                    var point = selected.Selection!.Coordinate;
                    return (point.X, point.Y);
                };
            }
            await picker.ShowDialog(window);
            if (!current() || !unit.HasCurrentContext || !ReferenceEquals(window.DataContext, model) || picker.ResultPlacement is not { } result ||
                !int.TryParse(result.Side.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var side) || side < 1 ||
                result.Spec.DifficultyDefine is not null) return null;
            return action with { Side = side, Unit = result.Spec };
        };
    }
}