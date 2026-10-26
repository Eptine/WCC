using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.Views.Controls;

/// <summary>Terrain-colored navigation overview; no event execution or texture resolution.</summary>
public sealed class ScenarioMapMinimapControl : Control
{
    public static readonly StyledProperty<ScenarioHexMapControl?> MapViewProperty =
        AvaloniaProperty.Register<ScenarioMapMinimapControl, ScenarioHexMapControl?>(nameof(MapView));
    private ScenarioHexMapControl? _subscribedMap;
    private ScenarioMapPreviewVm? _cachedPreview;
    private readonly List<(Rect Bounds, IBrush Brush)> _cells = [];
    private static readonly Dictionary<char, IBrush> TerrainBrushes = new()
    {
        ['G'] = Brush(93, 130, 63), ['F'] = Brush(40, 84, 46), ['H'] = Brush(140, 123, 76),
        ['M'] = Brush(124, 128, 135), ['W'] = Brush(53, 102, 153), ['S'] = Brush(73, 100, 87),
        ['D'] = Brush(195, 167, 102), ['A'] = Brush(203, 216, 220), ['U'] = Brush(93, 87, 78),
        ['C'] = Brush(147, 138, 119), ['K'] = Brush(173, 158, 118), ['R'] = Brush(159, 136, 99),
        ['Q'] = Brush(127, 63, 39), ['X'] = Brush(47, 49, 57), ['V'] = Brush(150, 118, 72)
    };
    private static readonly IBrush UnknownBrush = Brush(124, 58, 126);
    public ScenarioHexMapControl? MapView { get => GetValue(MapViewProperty); set => SetValue(MapViewProperty, value); }

    static ScenarioMapMinimapControl()
    {
        AffectsRender<ScenarioMapMinimapControl>(MapViewProperty);
        MapViewProperty.Changed.AddClassHandler<ScenarioMapMinimapControl>((control, _) => control.SubscribeMap());
    }

    public ScenarioMapMinimapControl() => ClipToBounds = true;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); SubscribeMap(); }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscribedMap is not null) _subscribedMap.ViewportChanged -= MapViewportChanged;
        _subscribedMap = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeMap()
    {
        if (_subscribedMap is not null) _subscribedMap.ViewportChanged -= MapViewportChanged;
        _subscribedMap = MapView;
        if (_subscribedMap is not null) _subscribedMap.ViewportChanged += MapViewportChanged;
        InvalidateVisual();
    }

    private void MapViewportChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (MapView?.Preview is not { } preview) return;
        if (!ReferenceEquals(preview, _cachedPreview))
        {
            _cachedPreview = preview;
            _cells.Clear();
            foreach (var row in preview.Map.Rows)
                foreach (var cell in row)
                {
                    var terrain = cell.TerrainCode;
                    var brush = terrain.Contains("^F", StringComparison.Ordinal) ? TerrainBrushes['F'] :
                        terrain.Length > 0 && TerrainBrushes.TryGetValue(char.ToUpperInvariant(terrain[0]), out var known) ? known : UnknownBrush;
                    _cells.Add((ScenarioMapViewportGeometry.TileBounds(cell.ColumnIndex, cell.RowIndex,
                        ScenarioMapViewportGeometry.SourceTileSize, preview.Map.BorderSize), brush));
                }
        }
        var mapSize = MapView.MapPixelSize;
        var overview = ScenarioMapViewportGeometry.FitOverview(mapSize, Bounds.Size);
        if (overview.Width <= 0) return;
        var scale = overview.Width / mapSize.Width;
        foreach (var cell in _cells)
            context.DrawRectangle(cell.Brush, null, new Rect(overview.X + cell.Bounds.X * scale,
                overview.Y + cell.Bounds.Y * scale, cell.Bounds.Width * scale, cell.Bounds.Height * scale));
        var visible = MapView.VisibleMapRegion;
        if (visible.Width > 0 && visible.Height > 0)
            context.DrawRectangle(null, new Pen(Brushes.White, 1.5), new Rect(overview.X + visible.X * scale,
                overview.Y + visible.Y * scale, visible.Width * scale, visible.Height * scale));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (MapView?.Preview is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var overview = ScenarioMapViewportGeometry.FitOverview(MapView.MapPixelSize, Bounds.Size);
        var point = e.GetPosition(this);
        if (!overview.Contains(point)) return;
        MapView.CenterMapPoint(ScenarioMapViewportGeometry.OverviewPointToMap(point, overview, MapView.MapPixelSize));
        e.Handled = true;
    }

    private static IBrush Brush(byte red, byte green, byte blue) => new SolidColorBrush(Color.FromRgb(red, green, blue));
}
