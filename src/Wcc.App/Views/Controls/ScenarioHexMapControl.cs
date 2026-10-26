using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Wcc.App.Preview;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Maps;

namespace Wcc.App.Views.Controls;

public sealed class ScenarioHexMapControl : Control
{
    public static readonly StyledProperty<ScenarioMapPreviewVm?> PreviewProperty =
        AvaloniaProperty.Register<ScenarioHexMapControl, ScenarioMapPreviewVm?>(nameof(Preview));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ScenarioHexMapControl, double>(nameof(Zoom), 0.75);

    private const double SourceTileSize = ScenarioMapViewportGeometry.SourceTileSize;
    private const double DragThreshold = 3.0;
    private const double KeyboardPanStep = 48.0;
    private readonly Dictionary<string, Bitmap?> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private ScenarioMapPreviewVm? _subscribedPreview;
    private Vector _panOffset;
    private Point _pointerPressPosition;
    private Vector _pointerPressPan;
    private IPointer? _capturedPointer;
    private bool _isDragging;
    private Rect _publishedVisibleRegion;
    private Size _publishedMapSize;

    public event EventHandler? ViewportChanged;
    public event EventHandler? HoveredCellChanged;
    public event EventHandler<ScenarioMapCellContextEventArgs>? CellContextRequested;
    public WesnothMapCell? HoveredCell { get; private set; }
    public Size MapPixelSize => Preview is { } preview
        ? ScenarioMapViewportGeometry.MapSize(preview.Map.Width, preview.Map.Height, SourceTileSize) : default;
    public Rect VisibleMapRegion => ScenarioMapViewportGeometry.VisibleRegion(MapPixelSize, Bounds.Size,
        GetMapOrigin(Bounds.Size), Math.Clamp(Zoom, 0.25, 2));

    public void CenterMapPoint(Point sourcePoint)
    {
        if (Preview is null) return;
        var scale = Math.Clamp(Zoom, 0.25, 2);
        _panOffset = new Vector(Bounds.Width / 2 - sourcePoint.X * scale,
            Bounds.Height / 2 - sourcePoint.Y * scale);
        ClampPan(Bounds.Size);
        PublishViewportChanged();
        InvalidateVisual();
    }

    static ScenarioHexMapControl()
    {
        AffectsMeasure<ScenarioHexMapControl>(PreviewProperty, ZoomProperty);
        AffectsRender<ScenarioHexMapControl>(PreviewProperty, ZoomProperty);
        PreviewProperty.Changed.AddClassHandler<ScenarioHexMapControl>((control, _) =>
            control.OnPreviewChanged());
        ZoomProperty.Changed.AddClassHandler<ScenarioHexMapControl>((control, _) =>
            control.OnZoomChanged());
    }

    public ScenarioMapPreviewVm? Preview
    {
        get => GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    private double TileWidth => SourceTileSize * Math.Clamp(Zoom, 0.25, 2.0);
    private double ColumnStep => TileWidth * 0.75;

    public ScenarioHexMapControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 720,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 440);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ClampPan(finalSize);
        PublishViewportChanged(finalSize);
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Preview is null)
            return;

        var map = Preview.Map;
        var origin = GetMapOrigin(Bounds.Size);
        var viewport = new Rect(
                -origin.X,
                -origin.Y,
                Bounds.Width,
                Bounds.Height)
            .Inflate(TileWidth);
        var firstRow = Math.Max(0, (int)Math.Floor(viewport.Y / TileWidth) - 1);
        var lastRow = Math.Min(map.Height - 1, (int)Math.Ceiling(viewport.Bottom / TileWidth) + 1);
        var firstColumn = Math.Max(0, (int)Math.Floor(viewport.X / ColumnStep) - 1);
        var lastColumn = Math.Min(map.Width - 1, (int)Math.Ceiling(viewport.Right / ColumnStep) + 1);
        for (var rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
        {
            var row = map.Rows[rowIndex];
            for (var columnIndex = firstColumn;
                 columnIndex <= lastColumn && columnIndex < row.Count;
                columnIndex++)
            {
                var cell = row[columnIndex];
                var contentBounds = GetTileBounds(columnIndex, rowIndex);
                if (!contentBounds.Intersects(viewport))
                    continue;

                DrawCell(context, cell, Translate(contentBounds, origin));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Preview is { } preview && e.GetCurrentPoint(this).Properties.IsRightButtonPressed &&
            _capturedPointer is null && HitTestCell(e.GetPosition(this)) is { } contextCell && CellContextRequested is not null)
        {
            Focus();
            CellContextRequested.Invoke(this, new(preview, contextCell, e.GetPosition(this)));
            e.Handled = true;
            return;
        }
        if (Preview is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Focus();
        _capturedPointer = e.Pointer;
        _pointerPressPosition = e.GetPosition(this);
        _pointerPressPan = _panOffset;
        _isDragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHoveredCell(Preview is null ? null : HitTestCell(e.GetPosition(this)));
        if (_capturedPointer != e.Pointer ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - _pointerPressPosition;
        if (!_isDragging && Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y)) >= DragThreshold)
            _isDragging = true;

        if (_isDragging)
        {
            _panOffset = _pointerPressPan + delta;
            ClampPan(Bounds.Size);
            PublishViewportChanged();
            InvalidateVisual();
        }

        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHoveredCell(null);
    }

    private void SetHoveredCell(WesnothMapCell? cell)
    {
        if (ReferenceEquals(HoveredCell, cell)) return;
        HoveredCell = cell;
        HoveredCellChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_capturedPointer != e.Pointer)
            return;

        if (!_isDragging && Preview is not null)
        {
            var cell = HitTestCell(e.GetPosition(this));
            if (cell is not null)
                Preview.SelectCell(cell);
        }

        e.Pointer.Capture(null);
        _capturedPointer = null;
        _isDragging = false;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _capturedPointer = null;
        _isDragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        Zoom = Math.Clamp(Zoom + Math.Sign(e.Delta.Y) * 0.1, 0.25, 2.0);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Preview?.IsCoordinateSelectionActive == true)
        {
            if (e.Key == Key.Escape)
            {
                Preview.CancelCoordinateSelection();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && Preview.ConfirmCoordinateSelection())
            {
                e.Handled = true;
                return;
            }
        }

        var delta = e.Key switch
        {
            Key.Left => new Vector(KeyboardPanStep, 0),
            Key.Right => new Vector(-KeyboardPanStep, 0),
            Key.Up => new Vector(0, KeyboardPanStep),
            Key.Down => new Vector(0, -KeyboardPanStep),
            _ => default
        };
        if (delta != default)
        {
            _panOffset += delta;
            ClampPan(Bounds.Size);
            PublishViewportChanged();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnPreviewChanged()
    {
        SetHoveredCell(null);
        if (_subscribedPreview is not null)
            _subscribedPreview.PropertyChanged -= PreviewOnPropertyChanged;

        _subscribedPreview = Preview;
        if (_subscribedPreview is not null)
            _subscribedPreview.PropertyChanged += PreviewOnPropertyChanged;

        _panOffset = default;
        DisposeBitmaps();
        PublishViewportChanged(force: true);
        InvalidateVisual();
    }

    private void OnZoomChanged()
    {
        ClampPan(Bounds.Size);
        PublishViewportChanged();
        InvalidateVisual();
    }

    private void PreviewOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScenarioMapPreviewVm.SelectedCell) or
            nameof(ScenarioMapPreviewVm.OverlayRevision) or
            nameof(ScenarioMapPreviewVm.IsCoordinateSelectionActive) or
            nameof(ScenarioMapPreviewVm.CoordinateSelectionCandidateCell))
            InvalidateVisual();
        if (e.PropertyName == nameof(ScenarioMapPreviewVm.FocusRequestRevision))
            CenterRequestedCell();
    }

    private void DrawCell(DrawingContext context, WesnothMapCell cell, Rect bounds)
    {
        var visual = Preview!.GetVisual(cell.TerrainCode);
        var drewImage = false;
        foreach (var layer in visual.Layers)
        {
            var bitmap = GetBitmap(layer.ImagePath);
            if (bitmap is null)
                continue;

            context.DrawImage(bitmap, new Rect(bitmap.Size), bounds);
            drewImage = true;
        }

        var hex = CreateHexGeometry(bounds);
        if (!drewImage)
            context.DrawGeometry(Brushes.DarkMagenta, null, hex);
        if (!visual.IsFullyResolved)
        {
            context.DrawGeometry(
                new SolidColorBrush(Color.FromArgb(105, 150, 0, 150)),
                new Pen(Brushes.Magenta, Math.Max(1, Zoom * 2)),
                hex);
            DrawCenteredText(context, "?", bounds, Brushes.White, Math.Max(10, TileWidth * 0.27));
        }

        if (cell.IsBorder)
        {
            context.DrawGeometry(
                new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)),
                null,
                hex);
        }

        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), hex);

        var overlays = Preview.GetOverlays(cell.X, cell.Y);
        var badgeIndex = 0;
        foreach (var overlay in overlays)
        {
            switch (overlay.Kind)
            {
                case ScenarioMapOverlayKind.StartingPosition:
                    var radius = TileWidth * 0.19;
                    var center = bounds.Center;
                    context.DrawEllipse(
                        Brushes.DarkBlue,
                        new Pen(Brushes.White, Math.Max(1, Zoom * 2)),
                        center,
                        radius,
                        radius);
                    DrawCenteredText(
                        context,
                        overlay.Label,
                        bounds,
                        Brushes.White,
                        Math.Max(9, TileWidth * 0.23));
                    break;

                case ScenarioMapOverlayKind.NamedLocation:
                    var namedCenter = bounds.Center;
                    var namedRadius = Math.Max(3, TileWidth * 0.08);
                    context.DrawEllipse(
                        Brushes.Gold,
                        new Pen(Brushes.Black, 1),
                        namedCenter,
                        namedRadius,
                        namedRadius);
                    break;

                default:
                    DrawSpatialBadge(context, overlay, bounds, badgeIndex++);
                    break;
            }
        }

        if (ReferenceEquals(Preview.CoordinateSelectionCandidateCell, cell))
            context.DrawGeometry(null, new Pen(Brushes.Cyan, Math.Max(3, Zoom * 5)), hex);
        else if (ReferenceEquals(Preview.SelectedCell, cell))
            context.DrawGeometry(null, new Pen(Brushes.Yellow, Math.Max(2, Zoom * 4)), hex);
    }

    private void DrawSpatialBadge(
        DrawingContext context,
        ScenarioMapOverlayVm overlay,
        Rect bounds,
        int badgeIndex)
    {
        var positions = new[]
        {
            new Vector(-0.20, -0.21),
            new Vector(0.20, -0.21),
            new Vector(-0.25, 0.16),
            new Vector(0.25, 0.16),
            new Vector(0, 0.28),
            new Vector(0, -0.31)
        };
        var position = positions[badgeIndex % positions.Length];
        var center = new Point(
            bounds.Center.X + (position.X * bounds.Width),
            bounds.Center.Y + (position.Y * bounds.Height));
        var radius = Math.Max(5, TileWidth * 0.115);
        var fill = ScenarioMapMarkerStyle.Fill(overlay.Kind);
        var penBrush = overlay.Event is not null &&
                       string.Equals(overlay.Event.Id, Preview?.SelectedEvent?.Id, StringComparison.Ordinal)
            ? Brushes.Cyan
            : overlay.SpatialEntry?.IsConditional == true || overlay.Event?.IsConditional == true
                ? Brushes.Gold
                : Brushes.White;
        context.DrawEllipse(
            fill,
            new Pen(penBrush, Math.Max(1, Zoom * 1.7)),
            center,
            radius,
            radius);
        DrawCenteredText(
            context,
            overlay.Label,
            new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2),
            Brushes.White,
            Math.Max(8, TileWidth * 0.15));
    }

    private Bitmap? GetBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (_bitmaps.TryGetValue(path, out var bitmap))
            return bitmap;

        bitmap = BitmapFileLoader.TryLoad(path);
        _bitmaps[path] = bitmap;
        return bitmap;
    }

    private WesnothMapCell? HitTestCell(Point point)
    {
        var map = Preview!.Map;
        var origin = GetMapOrigin(Bounds.Size);
        var contentPoint = point - origin;
        var approximateColumn = (int)Math.Floor(contentPoint.X / ColumnStep);
        for (var column = Math.Max(0, approximateColumn - 1);
             column <= Math.Min(map.Width - 1, approximateColumn + 1);
             column++)
        {
            var offset = ScenarioMapViewportGeometry.ColumnOffset(column, TileWidth, map.BorderSize);
            var approximateRow = (int)Math.Floor((contentPoint.Y - offset) / TileWidth);
            for (var row = Math.Max(0, approximateRow - 1);
                 row <= Math.Min(map.Height - 1, approximateRow + 1);
                 row++)
            {
                var cell = map.GetCellByStorageIndex(column, row);
                if (cell is not null &&
                    CreateHexGeometry(GetTileBounds(column, row)).FillContains(contentPoint))
                    return cell;
            }
        }

        return null;
    }

    private Rect GetTileBounds(int column, int row)
        => ScenarioMapViewportGeometry.TileBounds(column, row, TileWidth, Preview?.Map.BorderSize ?? 0);

    private Size GetMapSize()
    {
        if (Preview?.Map is not { Width: > 0, Height: > 0 } map)
            return default;

        return ScenarioMapViewportGeometry.MapSize(map.Width, map.Height, TileWidth);
    }

    private void CenterRequestedCell()
    {
        if (Preview?.RequestedFocusCell is not { } cell)
            return;

        var bounds = GetTileBounds(cell.ColumnIndex, cell.RowIndex);
        _panOffset = new Vector(
            (Bounds.Width / 2.0) - bounds.Center.X,
            (Bounds.Height / 2.0) - bounds.Center.Y);
        ClampPan(Bounds.Size);
        PublishViewportChanged();
        InvalidateVisual();
    }

    private Vector GetMapOrigin(Size viewportSize)
    {
        var mapSize = GetMapSize();
        return ScenarioMapViewportGeometry.Origin(mapSize, viewportSize, _panOffset);
    }

    private void PublishViewportChanged(Size? viewport = null, bool force = false)
    {
        var size = viewport ?? Bounds.Size;
        var mapSize = MapPixelSize;
        var region = ScenarioMapViewportGeometry.VisibleRegion(mapSize, size,
            GetMapOrigin(size), Math.Clamp(Zoom, 0.25, 2));
        if (!force && _publishedVisibleRegion == region && _publishedMapSize == mapSize) return;
        _publishedVisibleRegion = region;
        _publishedMapSize = mapSize;
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClampPan(Size viewportSize)
    {
        var origin = GetMapOrigin(viewportSize);
        var mapSize = GetMapSize();
        _panOffset = new Vector(
            mapSize.Width > viewportSize.Width ? origin.X : 0,
            mapSize.Height > viewportSize.Height ? origin.Y : 0);
    }

    private static Rect Translate(Rect bounds, Vector offset)
        => new(
            bounds.X + offset.X,
            bounds.Y + offset.Y,
            bounds.Width,
            bounds.Height);

    private static StreamGeometry CreateHexGeometry(Rect bounds)
    {
        var x = bounds.X;
        var y = bounds.Y;
        var w = bounds.Width;
        var h = bounds.Height;
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(x + (w * 0.25), y), isFilled: true);
        g.LineTo(new Point(x + (w * 0.75), y));
        g.LineTo(new Point(x + w, y + (h * 0.5)));
        g.LineTo(new Point(x + (w * 0.75), y + h));
        g.LineTo(new Point(x + (w * 0.25), y + h));
        g.LineTo(new Point(x, y + (h * 0.5)));
        g.EndFigure(isClosed: true);
        return geometry;
    }

    private static void DrawCenteredText(
        DrawingContext context,
        string value,
        Rect bounds,
        IBrush brush,
        double size)
    {
        var text = new FormattedText(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            size,
            brush);
        context.DrawText(
            text,
            new Point(
                bounds.Center.X - (text.Width / 2.0),
                bounds.Center.Y - (text.Height / 2.0)));
    }

    private void DisposeBitmaps()
    {
        foreach (var bitmap in _bitmaps.Values)
            bitmap?.Dispose();
        _bitmaps.Clear();
    }
}

/// <summary>The exact right-clicked map capture; it does not select a tile or authorize a source edit.</summary>
public sealed class ScenarioMapCellContextEventArgs(ScenarioMapPreviewVm preview, WesnothMapCell cell, Point position) : EventArgs
{
    public ScenarioMapPreviewVm Preview { get; } = preview;
    public WesnothMapCell Cell { get; } = cell;
    public Point Position { get; } = position;
}
