using Wcc.Core.Scenarios;
using System;
using System.Globalization;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Maps;

namespace Wcc.App.Views;

/// <summary>Connects the shared event graph to one captured playable map. A changed
/// provider, graph window or draft cannot publish coordinates into another editor.</summary>
internal static class EventGraphCoordinatePicker
{
    internal static void Attach(EventNodeGraphWindowViewModel model, EventNodeGraphWindow window,
        Func<ScenarioMapPreviewVm?> currentMap, Func<bool> current)
    {
        if (currentMap() is not { Map: { HasErrors: false, PlayableWidth: > 0, PlayableHeight: > 0 } } sourceMap) return;
        model.CoordinatePicker = async (x, y) =>
        {
            if (!current() || !ReferenceEquals(currentMap(), sourceMap) || !ReferenceEquals(window.DataContext, model)) return null;
            ScenarioMapCoordinate? initial = int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var initialX) &&
                int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var initialY)
                ? new(initialX, initialY) : null;
            using var picker = new ScenarioMapCoordinatePickerViewModel(sourceMap,
                new("Choose a playable hex for this event.", initial));
            var coordinateWindow = new ScenarioMapCoordinatePickerWindow(picker);
            await coordinateWindow.ShowDialog(window);
            var selected = await picker.Completion;
            if (!selected.IsConfirmed || !current() || !ReferenceEquals(currentMap(), sourceMap) || !ReferenceEquals(window.DataContext, model)) return null;
            var coordinate = selected.Selection!.Coordinate;
            return (coordinate.X, coordinate.Y);
        };
    }
}