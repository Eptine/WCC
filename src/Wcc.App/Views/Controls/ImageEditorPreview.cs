using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Wcc.App.Views.Controls;

public sealed class ImageEditorRectangleChangedEventArgs(Rect rectangle) : EventArgs
{
    public Rect Rectangle { get; } = rectangle;
}
public sealed class ImageEditorColorPickedEventArgs(Color color) : EventArgs
{
    public Color Color { get; } = color;
}

/// <summary>Detached pixel-space editing surface. Geometry drags propose values and defer expensive preview work.</summary>
public sealed class ImageEditorPreview : Control
{
    private Bitmap? _bitmap;
    private Rect? _selection;
    private string? _mode;
    private object? _interactionOwner;
    private Rect _imageRect;
    private Point? _origin;
    private Point? _dragStart;
    private Rect _dragRectangle;
    private int _handle = -1;
    private double _zoom = 1;
    private bool _fitPending;
    private bool _isPickingColor;
    private bool _spaceDown;
    private bool _interactionActive;
    private bool _canInteract;
    private bool _scalePreservesAspect;
    private Vector _pan;
    private Point? _panStart;
    private Vector _panAtStart;
    private StandardCursorType _cursorType = StandardCursorType.Arrow;
    public ImageEditorPreview() { Focusable = true; }
    public event EventHandler<ImageEditorRectangleChangedEventArgs>? RectangleChanged;
    public event EventHandler<ImageEditorColorPickedEventArgs>? ColorPicked;
    public event EventHandler? ZoomChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public Bitmap? Bitmap
    {
        get => _bitmap;
        set { if (ReferenceEquals(_bitmap, value)) return; _bitmap = value; InvalidateVisual(); }
    }
    public Rect? Selection { get => _selection; set { _selection = value; InvalidateVisual(); } }
    public string? InteractionMode
    {
        get => _mode;
        set { if (_mode == value) return; FinishInteraction(); _mode = value; InvalidateVisual(); }
    }
    public object? InteractionOwner
    {
        get => _interactionOwner;
        set { if (!ReferenceEquals(_interactionOwner, value)) { FinishInteraction(); _interactionOwner = value; IsPickingColor = false; } }
    }
    public bool CanInteract
    {
        get => _canInteract;
        set { if (_canInteract == value) return; _canInteract = value; if (!value) FinishInteraction(); }
    }
    public bool ScalePreservesAspect
    {
        get => _scalePreservesAspect;
        set { if (_scalePreservesAspect == value) return; _scalePreservesAspect = value; InvalidateVisual(); }
    }
    public bool IsPickingColor
    {
        get => _isPickingColor;
        set { if (_isPickingColor == value) return; _isPickingColor = value; FinishInteraction(); SetCursor(value ? StandardCursorType.Cross : StandardCursorType.Arrow); }
    }
    /// <summary>One source pixel occupies one device-independent pixel at 100%.</summary>
    public double Zoom { get => _zoom; set => SetZoom(value); }
    /// <summary>Explicitly recenter a newly loaded base image; normal preview replacements keep their viewport.</summary>
    public void ResetViewport()
    {
        FinishInteraction(); _origin = null; _pan = default; _fitPending = false;
        var changed = _zoom != 1; _zoom = 1;
        if (changed) ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    public void ResetZoom() { FinishInteraction(); _origin = null; _pan = default; SetZoom(1); UpdateImageGeometry(); InvalidateVisual(); }
    public void ZoomBy(double factor) => SetZoom(_zoom * factor);
    public void FitToViewport()
    {
        FinishInteraction(); _pan = default; _origin = null; _fitPending = true;
        UpdateImageGeometry(); InvalidateVisual();
    }
    private void SetZoom(double value, Point? anchorPoint = null)
    {
        if (!double.IsFinite(value)) return;
        FinishInteraction(); UpdateImageGeometry();
        _fitPending = false;
        var next = Math.Clamp(value, 0.01, 32);
        if (next == _zoom) return;
        var previous = _zoom; var oldOrigin = _imageRect.Position;
        _zoom = next;
        if (_bitmap is not null && _origin is not null)
        {
            var anchor = anchorPoint ?? new Point(Bounds.Width / 2, (Bounds.Height - 24) / 2);
            _origin = anchor - (anchor - oldOrigin) * (_zoom / previous) - _pan;
        }
        UpdateImageGeometry(); InvalidateVisual(); ZoomChanged?.Invoke(this, EventArgs.Empty);
    }
    private Size Extent()
    {
        if (_bitmap is null) return default;
        return new Size(Math.Max(_bitmap.PixelSize.Width, _mode == "SCALE" ? _selection?.Right ?? 0 : 0),
            Math.Max(_bitmap.PixelSize.Height, _mode == "SCALE" ? _selection?.Bottom ?? 0 : 0));
    }
    private void UpdateImageGeometry()
    {
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var extent = Extent();
        if (_fitPending && Bounds.Width > 32 && Bounds.Height > 56)
        {
            var next = Math.Clamp(Math.Min((Bounds.Width - 32) / extent.Width, (Bounds.Height - 56) / extent.Height), 0.01, 32);
            _fitPending = false; _origin = null;
            if (_zoom != next) { _zoom = next; ZoomChanged?.Invoke(this, EventArgs.Empty); }
        }
        if (_origin is null)
            _origin = new Point((Bounds.Width - extent.Width * _zoom) / 2, (Bounds.Height - 24 - extent.Height * _zoom) / 2);
        // Status text and tools may reflow while editing. A different viewport size must
        // not move the pixel origin; only explicit Fit/100% or a new base recenters it.
        _imageRect = new Rect(_origin.Value + _pan, new Size(_bitmap.PixelSize.Width * _zoom, _bitmap.PixelSize.Height * _zoom));
    }
    private Rect Target
    {
        get
        {
            if (_mode != "SCALE" || _selection is not { } scale) return _imageRect;
            if (ScalePreservesAspect && _bitmap is not null)
            {
                var factor = Math.Min(scale.Width / _bitmap.PixelSize.Width, scale.Height / _bitmap.PixelSize.Height);
                scale = new Rect(scale.Position, new Size(Math.Max(1, (int)(_bitmap.PixelSize.Width * factor)),
                    Math.Max(1, (int)(_bitmap.PixelSize.Height * factor))));
            }
            return Display(scale);
        }
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#14181D")), new Rect(Bounds.Size));
        using var clip = context.PushClip(new Rect(Bounds.Size));
        const int tile = 12;
        var light = new SolidColorBrush(Color.Parse("#62666B")); var dark = new SolidColorBrush(Color.Parse("#41454A"));
        for (var y = 0; y < Bounds.Height; y += tile)
            for (var x = 0; x < Bounds.Width; x += tile)
                context.FillRectangle(((x / tile + y / tile) & 1) == 0 ? light : dark, new Rect(x, y, tile, tile));
        if (_bitmap is null || Bounds.Width < 1 || Bounds.Height < 1) return;
        UpdateImageGeometry(); var target = Target;
        context.DrawImage(_bitmap, new Rect(_bitmap.Size), target);
        if (_mode == "CROP" && _selection is { } crop) DimOutside(context, Display(crop), target);
        // Transparent pixels still contribute to the image bounds.
        context.DrawRectangle(null, new Pen(Brushes.Black, 2), target);
        context.DrawRectangle(null, new Pen(Brushes.White, 1, DashStyle.Dash), target);
        var size = _mode == "CROP" && _selection is { } frame ? frame.Size
            : _mode == "SCALE" ? new Size(target.Width / _zoom, target.Height / _zoom)
            : new Size(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        var labelTarget = _mode == "CROP" && _selection is { } cropped ? Display(cropped) : target;
        var text = new FormattedText($"{size.Width:0} × {size.Height:0} px", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
        var label = new Rect(labelTarget.Right - text.Width - 10, labelTarget.Bottom + 5, text.Width + 10, text.Height + 4);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#D914181D")), label);
        context.DrawText(text, new Point(label.X + 5, label.Y + 2));
        if (_mode is not ("CROP" or "SCALE" or "MOVE") || _selection is not { } selected) return;
        var box = Display(selected);
        context.DrawRectangle(null, new Pen(Brushes.Gold, 2, _mode == "MOVE" ? DashStyle.Dash : null), box);
        if (_mode == "MOVE") return;
        foreach (var point in Handles(box))
            context.DrawRectangle(Brushes.Gold, new Pen(Brushes.Black, 1), new Rect(point.X - 5, point.Y - 5, 10, 10));
    }
    private static void DimOutside(DrawingContext context, Rect selection, Rect image)
    {
        var visible = selection.Intersect(image); var dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        if (visible.Width <= 0 || visible.Height <= 0) { context.FillRectangle(dim, image); return; }
        if (visible.Top > image.Top) context.FillRectangle(dim, new Rect(image.Left, image.Top, image.Width, visible.Top - image.Top));
        if (visible.Bottom < image.Bottom) context.FillRectangle(dim, new Rect(image.Left, visible.Bottom, image.Width, image.Bottom - visible.Bottom));
        if (visible.Left > image.Left) context.FillRectangle(dim, new Rect(image.Left, visible.Top, visible.Left - image.Left, visible.Height));
        if (visible.Right < image.Right) context.FillRectangle(dim, new Rect(visible.Right, visible.Top, image.Right - visible.Right, visible.Height));
    }
    private Rect Display(Rect rectangle) => new(_imageRect.X + rectangle.X * _zoom,
        _imageRect.Y + rectangle.Y * _zoom, rectangle.Width * _zoom, rectangle.Height * _zoom);
    private static Point[] Handles(Rect box) => [box.TopLeft, new(box.Center.X, box.Top), box.TopRight,
        new(box.Right, box.Center.Y), box.BottomRight, new(box.Center.X, box.Bottom), box.BottomLeft, new(box.Left, box.Center.Y)];
    private static int HitHandle(Rect box, Point point)
    {
        var found = Array.FindIndex(Handles(box), item => Math.Abs(item.X - point.X) <= 9 && Math.Abs(item.Y - point.Y) <= 9);
        if (found >= 0) return found;
        if (point.X >= box.Left && point.X <= box.Right)
        {
            if (Math.Abs(point.Y - box.Top) <= 7) return 1;
            if (Math.Abs(point.Y - box.Bottom) <= 7) return 5;
        }
        if (point.Y >= box.Top && point.Y <= box.Bottom)
        {
            if (Math.Abs(point.X - box.Right) <= 7) return 3;
            if (Math.Abs(point.X - box.Left) <= 7) return 7;
        }
        return -1;
    }
    private void SetCursor(StandardCursorType type)
    {
        if (_cursorType == type && Cursor is not null) return;
        _cursorType = type; Cursor = new Cursor(type);
    }
    private bool CanDragSelection(Point point, out int handle)
    {
        handle = -1;
        if (!CanInteract || _selection is not { } selected || _mode is not ("CROP" or "SCALE" or "MOVE")) return false;
        var box = Display(selected);
        if (_mode == "MOVE") return box.Contains(point);
        handle = HitHandle(box, point);
        return handle >= 0 || _mode == "CROP" && box.Contains(point);
    }
    private void UpdateCursor(Point point)
    {
        if (IsPickingColor) { SetCursor(StandardCursorType.Cross); return; }
        if (_panStart is not null || _spaceDown) { SetCursor(StandardCursorType.SizeAll); return; }
        if (_dragStart is not null && _mode == "MOVE") { SetCursor(StandardCursorType.SizeAll); return; }
        if (!CanDragSelection(point, out var handle)) { SetCursor(StandardCursorType.Hand); return; }
        if (_dragStart is not null) handle = _handle;
        SetCursor(handle switch
        {
            0 or 4 => StandardCursorType.TopLeftCorner,
            2 or 6 => StandardCursorType.TopRightCorner,
            1 or 5 => StandardCursorType.SizeNorthSouth,
            3 or 7 => StandardCursorType.SizeWestEast,
            _ => StandardCursorType.SizeAll
        });
    }
    public bool TryPickColor(Point canvasPoint, out Color color)
    {
        color = default;
        if (_bitmap is null) return false;
        UpdateImageGeometry(); var target = Target;
        if (!target.Contains(canvasPoint) || target.Width <= 0 || target.Height <= 0) return false;
        var x = Math.Clamp((int)((canvasPoint.X - target.Left) * _bitmap.PixelSize.Width / target.Width), 0, _bitmap.PixelSize.Width - 1);
        var y = Math.Clamp((int)((canvasPoint.Y - target.Top) * _bitmap.PixelSize.Height / target.Height), 0, _bitmap.PixelSize.Height - 1);
        using var stream = new MemoryStream(); _bitmap.Save(stream);
        using var data = SKData.CreateCopy(stream.ToArray()); using var codec = SKCodec.Create(data);
        if (codec is null) return false;
        using var decoded = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success) return false;
        var pixel = decoded.GetPixel(x, y); color = Color.FromArgb(pixel.Alpha, pixel.Red, pixel.Green, pixel.Blue); return true;
    }
    private void StartInteraction()
    {
        if (_interactionActive) return; _interactionActive = true; InteractionStarted?.Invoke(this, EventArgs.Empty);
    }
    private void FinishInteraction()
    {
        _dragStart = null; _panStart = null;
        if (_interactionActive) { _interactionActive = false; InteractionCompleted?.Invoke(this, EventArgs.Empty); }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); UpdateImageGeometry();
        var point = e.GetPosition(this); var properties = e.GetCurrentPoint(this).Properties;
        if (_bitmap is null) return; Focus();
        if (properties.IsMiddleButtonPressed || properties.IsLeftButtonPressed && _spaceDown)
        { _panStart = point; _panAtStart = _pan; e.Pointer.Capture(this); UpdateCursor(point); e.Handled = true; return; }
        if (!properties.IsLeftButtonPressed) return;
        if (IsPickingColor)
        {
            if (TryPickColor(point, out var color)) { IsPickingColor = false; ColorPicked?.Invoke(this, new(color)); }
            e.Handled = true; return;
        }
        if (!CanDragSelection(point, out var handle) || _selection is not { } rectangle)
        { _panStart = point; _panAtStart = _pan; e.Pointer.Capture(this); UpdateCursor(point); e.Handled = true; return; }
        _handle = handle; _dragStart = point; _dragRectangle = rectangle;
        e.Pointer.Capture(this); StartInteraction(); UpdateCursor(point); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); UpdateImageGeometry(); var point = e.GetPosition(this);
        if (_panStart is { } panStart)
        { _pan = _panAtStart + (point - panStart); UpdateImageGeometry(); InvalidateVisual(); e.Handled = true; return; }
        UpdateCursor(point);
        if (_dragStart is not { } start || !CanInteract || _bitmap is null) return;
        var dx = Math.Round((point.X - start.X) / _zoom); var dy = Math.Round((point.Y - start.Y) / _zoom);
        var left = _dragRectangle.Left; var top = _dragRectangle.Top; var right = _dragRectangle.Right; var bottom = _dragRectangle.Bottom;
        if (_mode == "MOVE") { left += dx; right += dx; top += dy; bottom += dy; }
        else if (_mode == "SCALE")
        {
            right = Math.Clamp(_dragRectangle.Width + (_handle is 0 or 6 or 7 ? -dx : _handle is 2 or 3 or 4 ? dx : 0), 1, 8192);
            bottom = Math.Clamp(_dragRectangle.Height + (_handle is 0 or 1 or 2 ? -dy : _handle is 4 or 5 or 6 ? dy : 0), 1, 8192);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) bottom = Math.Clamp(Math.Round(right * _dragRectangle.Height / _dragRectangle.Width), 1, 8192);
            left = top = 0;
        }
        else if (_handle < 0)
        {
            left = Math.Clamp(left + dx, 0, Math.Max(0, _bitmap.PixelSize.Width - _dragRectangle.Width));
            top = Math.Clamp(top + dy, 0, Math.Max(0, _bitmap.PixelSize.Height - _dragRectangle.Height));
            right = left + _dragRectangle.Width; bottom = top + _dragRectangle.Height;
        }
        else
        {
            if (_handle is 0 or 6 or 7) left = Math.Clamp(left + dx, 0, right - 1);
            if (_handle is 0 or 1 or 2) top = Math.Clamp(top + dy, 0, bottom - 1);
            if (_handle is 2 or 3 or 4) right = Math.Clamp(right + dx, left + 1, _bitmap.PixelSize.Width);
            if (_handle is 4 or 5 or 6) bottom = Math.Clamp(bottom + dy, top + 1, _bitmap.PixelSize.Height);
        }
        var next = new Rect(left, top, right - left, bottom - top);
        _selection = next; InvalidateVisual(); RectangleChanged?.Invoke(this, new(next)); e.Handled = true;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_bitmap is null || e.Delta.Y == 0) return;
        SetZoom(_zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && IsPickingColor) { IsPickingColor = false; e.Handled = true; }
        if (e.Key == Key.Space && !_spaceDown) { _spaceDown = true; SetCursor(StandardCursorType.SizeAll); e.Handled = true; }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) { _spaceDown = false; SetCursor(StandardCursorType.Hand); e.Handled = true; }
    }
    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    { base.OnLostFocus(e); _spaceDown = false; FinishInteraction(); }
    protected override void OnPointerExited(PointerEventArgs e)
    { base.OnPointerExited(e); if (_dragStart is null && _panStart is null && !IsPickingColor) SetCursor(StandardCursorType.Arrow); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); FinishInteraction(); e.Pointer.Capture(null); UpdateCursor(e.GetPosition(this)); InvalidateVisual(); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); FinishInteraction(); InvalidateVisual(); }
}
