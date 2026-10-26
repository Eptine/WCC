using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Wcc.App.ViewModels;
using Wcc.App.Views.Controls;

namespace Wcc.App.Views;

public partial class EventNodeGraphWindow
{
    private readonly Dictionary<EventDifficultyRegionVm, RegionVisual> _regionVisuals = [];
    private EventDifficultyRegionVm? _dragRegion;
    private bool _resizingRegion;
    private Rect _regionDragBounds;
    private void RebuildRegions(EventNodeGraphWindowViewModel model)
    {
        foreach (var region in model.DifficultyRegions)
        {
            var frame = new Border { Tag = region, Name = "EventGraphRegion_" + region.Key.ToString("N"),
                BorderThickness = new Thickness(2), BorderBrush = Brush("#668B95"), Background = Brush("#303D6269"), CornerRadius = new CornerRadius(5) };
            var grid = new Grid { RowDefinitions = new RowDefinitions("25,*") };
            var title = new TextBlock { Text = region.Label, FontWeight = FontWeight.SemiBold, FontSize = 11, Margin = new Thickness(7, 3), TextTrimming = TextTrimming.CharacterEllipsis };
            var titleGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,22") };
            var delete = new Button { Content = "×", Tag = region, Name = "EventGraphRegionDelete_" + region.Key.ToString("N"),
                Width = 20, MinWidth = 20, Height = 20, MinHeight = 20, Padding = new Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsVisible = region.IsSelected, IsEnabled = model.CanEdit };
            UseGraphIcon(delete, WccActionIcon.Close); ToolTip.SetTip(delete, "Remove this difficulty region; keep its actions.");
            delete.Click += DeleteRegionClicked; Grid.SetColumn(delete, 1); titleGrid.Children.Add(title); titleGrid.Children.Add(delete);
            var header = new Border { Tag = region, Name = "EventGraphRegionHeader_" + region.Key.ToString("N"), Background = Brush("#405E67"),
                Cursor = new Cursor(StandardCursorType.SizeAll), Child = titleGrid };
            header.PointerPressed += RegionHeaderPressed;
            var handle = new Border { Tag = region, Name = "EventGraphRegionResize_" + region.Key.ToString("N"), Width = 15, Height = 15,
                Background = Brush("#91ADB4"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Cursor = new Cursor(StandardCursorType.BottomRightCorner) };
            ToolTip.SetTip(handle, "Drag to resize this difficulty region around whole action cards.");
            handle.PointerPressed += RegionResizePressed; Grid.SetRow(handle, 1);
            grid.Children.Add(header); grid.Children.Add(handle); frame.Child = grid;
            frame.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(frame).Properties.IsLeftButtonPressed ||
                    args.Source is Visual source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any())) return;
                model.SelectedRegion = region; EventGraphCanvas.Focus(); args.Handled = true;
            };
            EventGraphCanvas.Children.Add(frame); _regionVisuals.Add(region, new(frame, title, delete));
        }
        UpdateRegions();
    }
    private void UpdateRegions()
    {
        // A containing rectangle has greater area. Recompute the layer after
        // every resize/move so a later-created parent cannot cover child handles.
        var layer = -_regionVisuals.Count;
        foreach (var (region, visual) in _regionVisuals.OrderByDescending(pair => pair.Key.Width * pair.Key.Height))
        {
            visual.Frame.ZIndex = layer++;
            Canvas.SetLeft(visual.Frame, region.X); Canvas.SetTop(visual.Frame, region.Y);
            visual.Frame.Width = region.Width; visual.Frame.Height = region.Height; visual.Title.Text = region.Label;
            visual.Frame.BorderBrush = Brush(region.IsSelected ? "#D9BF85" : "#668B95");
            visual.Delete.IsVisible = region.IsSelected; visual.Delete.IsEnabled = _model?.CanEdit == true;
        }
    }
    private void RegionHeaderPressed(object? sender, PointerPressedEventArgs args) => BeginRegionDrag(sender, args, false);
    private void RegionResizePressed(object? sender, PointerPressedEventArgs args) => BeginRegionDrag(sender, args, true);
    private void BeginRegionDrag(object? sender, PointerPressedEventArgs args, bool resize)
    {
        if (_model is not { CanEdit: true } model || sender is not Border { Tag: EventDifficultyRegionVm region } control ||
            !args.GetCurrentPoint(control).Properties.IsLeftButtonPressed ||
            args.Source is Visual source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any())) return;
        model.SelectedRegion = region; EventGraphCanvas.Focus(); _dragNode = null; _dragRegion = region; _resizingRegion = resize;
        _dragPointer = args.GetPosition(EventGraphCanvas); _regionDragBounds = new(region.X, region.Y, region.Width, region.Height);
        args.Pointer.Capture(EventGraphCanvas); args.Handled = true;
    }
    private bool MoveDraggedRegion(PointerEventArgs args)
    {
        if (_dragRegion is not { } region || _model is not { } model) return false;
        var delta = args.GetPosition(EventGraphCanvas) - _dragPointer;
        if (_resizingRegion) model.ResizeDifficultyRegion(region, _regionDragBounds.Width + delta.X, _regionDragBounds.Height + delta.Y);
        else model.MoveDifficultyRegion(region, _regionDragBounds.X + delta.X, _regionDragBounds.Y + delta.Y);
        args.Handled = true; return true;
    }
    private void AddDifficultyRegionClicked(object? sender, RoutedEventArgs args)
    {
        if (_model is not { CanAddDifficultyRegion: true } model) return;
        var x = (EventGraphScroll.Offset.X + EventGraphScroll.Viewport.Width / 2) / model.Zoom - 120;
        var y = (EventGraphScroll.Offset.Y + EventGraphScroll.Viewport.Height / 2) / model.Zoom - 80;
        model.AddDifficultyRegion(x, y);
    }
    private void DeleteRegionClicked(object? sender, RoutedEventArgs args)
    {
        if (_model is not { CanEdit: true } model || sender is not Button { Tag: EventDifficultyRegionVm region } ||
            !model.DifficultyRegions.Contains(region)) return;
        model.SelectedRegion = region; model.RemoveSelectedRegion(); args.Handled = true;
    }
    private sealed record RegionVisual(Border Frame, TextBlock Title, Button Delete);
}