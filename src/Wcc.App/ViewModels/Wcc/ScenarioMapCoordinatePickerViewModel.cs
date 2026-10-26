using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Wcc.Core.Maps;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

public delegate Task<ScenarioMapCoordinateSelectionResult> ScenarioMapCoordinatePicker(
    ScenarioMapPreviewVm preview, ScenarioMapCoordinateSelectionRequest request);

/// <summary>A detached, read-only coordinate interaction over an already resolved map.</summary>
public sealed class ScenarioMapCoordinatePickerViewModel : ViewModelBase, IDisposable
{
    private WesnothMapCell? _hoveredCell;
    private double _zoom = 0.75;
    public ScenarioMapPreviewVm Preview { get; }
    public Task<ScenarioMapCoordinateSelectionResult> Completion { get; }
    public string Purpose { get; }
    public bool CanConfirm => Preview.CanConfirmCoordinateSelection;
    public bool HasFixedSelection => Preview.SelectedCell is not null;
    public string SelectionLabel => HasFixedSelection ? "Selected tile" : "Hovered tile";
    public double Zoom { get => _zoom; set => SetField(ref _zoom, Math.Clamp(value, 0.25, 2)); }
    public string TileSummary => (Preview.SelectedCell ?? _hoveredCell) is { } cell
        ? $"X: {cell.X}    Y: {cell.Y}\nTerrain: {cell.TerrainCode}" +
          (cell.StartingPosition is { } start ? $"\nStarting position: {start}" : "") +
          (cell.IsBorder ? "\nMap border" : "")
        : "Hover over a map hex to see its coordinates and terrain.";
    public string? Status => Preview.CoordinateSelectionStatus;

    public ScenarioMapCoordinatePickerViewModel(ScenarioMapPreviewVm source,
        ScenarioMapCoordinateSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        // Retain the exact map and terrain catalog, while keeping the caller's
        // selection, event workspace and overlays independent of the dialog.
        Preview = new(source.Map, source.TerrainCatalog, source.SourceDescription, source.SourcePath);
        Purpose = request.Purpose;
        Completion = Preview.BeginCoordinateSelectionAsync(request);
        Preview.PropertyChanged += PreviewChanged;
    }

    public void HoverCell(WesnothMapCell? cell)
    {
        if (cell is not null && !ReferenceEquals(Preview.Map.GetCellByStorageIndex(cell.ColumnIndex, cell.RowIndex), cell)) return;
        if (ReferenceEquals(_hoveredCell, cell)) return;
        _hoveredCell = cell;
        if (!HasFixedSelection) OnPropertyChanged(nameof(TileSummary));
    }

    public bool Confirm() => Preview.ConfirmCoordinateSelection();
    public void Cancel() => Preview.CancelCoordinateSelection();

    private void PreviewChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ScenarioMapPreviewVm.SelectedCell) or
            nameof(ScenarioMapPreviewVm.CoordinateSelectionCandidateCell) or
            nameof(ScenarioMapPreviewVm.CanConfirmCoordinateSelection) or
            nameof(ScenarioMapPreviewVm.CoordinateSelectionStatus))
            foreach (var property in new[] { nameof(CanConfirm), nameof(HasFixedSelection), nameof(SelectionLabel), nameof(TileSummary), nameof(Status) })
                OnPropertyChanged(property);
    }

    public void Dispose()
    {
        Cancel();
        Preview.PropertyChanged -= PreviewChanged;
    }
}
