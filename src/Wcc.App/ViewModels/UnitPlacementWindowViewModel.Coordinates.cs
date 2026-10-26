using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Wcc.App.ViewModels;

public sealed partial class UnitPlacementWindowViewModel
{
    private Func<string?, string?, Task<(int X, int Y)?>>? _coordinatePicker;
    private long _coordinatePickerVersion;
    private bool _pickingCoordinates;
    public Func<string?, string?, Task<(int X, int Y)?>>? CoordinatePicker
    {
        get => _coordinatePicker;
        set
        {
            if (Equals(_coordinatePicker, value)) return;
            _coordinatePicker = value; _coordinatePickerVersion++;
            OnPropertyChanged(nameof(CoordinatePicker)); OnPropertyChanged(nameof(CanPickCoordinates));
        }
    }
    public bool CanPickCoordinates => ShowsPlacement && CanEdit && !_pickingCoordinates && _coordinatePicker is not null;
    public async Task<bool> PickCoordinatesAsync()
    {
        if (!CanPickCoordinates || _coordinatePicker is not { } picker) return false;
        var version = _version; var providerVersion = _coordinatePickerVersion;
        _pickingCoordinates = true; OnPropertyChanged(nameof(CanPickCoordinates));
        try
        {
            var selected = await picker(X, Y);
            if (selected is not { X: > 0, Y: > 0 } coordinate || !CanEdit || version != _version ||
                providerVersion != _coordinatePickerVersion || !ReferenceEquals(picker, _coordinatePicker)) return false;
            _values["x"] = coordinate.X.ToString(CultureInfo.InvariantCulture);
            _values["y"] = coordinate.Y.ToString(CultureInfo.InvariantCulture);
            Changed(); return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { _pickingCoordinates = false; OnPropertyChanged(nameof(CanPickCoordinates)); }
    }
}