using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Wcc.App.ViewModels;

namespace Wcc.App.Views.Controls;

public partial class WccInitialFieldGroupView : UserControl
{
    public WccInitialFieldGroupView()
    {
        InitializeComponent();
        CoordinateRow.LayoutUpdated += (_, _) => AlignCoordinatePicker();
    }
    private void AlignCoordinatePicker()
    {
        if (!CoordinateRow.IsVisible || !CoordinatePickerButton.IsVisible) return;
        var input = CoordinateXInput.CoordinateInput;
        if (input.Bounds.Height <= 0 || CoordinatePickerButton.Bounds.Height <= 0) return;
        var center = input.TranslatePoint(new Point(0, input.Bounds.Height / 2), CoordinateRow);
        if (center is null) return;
        var top = Math.Max(0, center.Value.Y - CoordinatePickerButton.Bounds.Height / 2);
        if (Math.Abs(CoordinatePickerButton.Margin.Top - top) > 0.5)
            CoordinatePickerButton.Margin = new Thickness(0, top, 0, 0);
    }
    private async void PickCoordinateClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WccInitialFieldEditorGroup { CoordinateX: { CanPickCoordinate: true } field } &&
            TopLevel.GetTopLevel(this) is Window owner)
            await WccInitialFieldEditorView.PresentCoordinatePickerAsync(field, owner);
    }
}
