using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MediaColor = Avalonia.Media.Color;

namespace Wcc.App.Views.Controls;

/// <summary>HSV selection surface; changes are proposed only by explicit pointer or keyboard input.</summary>
public sealed class ImageEditorColorPicker : Control
{
    private MediaColor _color = Colors.Red;
    private double _hue, _saturation = 1, _value = 1;
    private bool _dragging, _dragHue;
    public ImageEditorColorPicker()
    {
        Focusable = true; MinHeight = 112; Height = 132;
        ToolTip.SetTip(this, "Choose saturation and brightness in the square, hue in the strip. Arrow keys adjust the color; Ctrl+Left/Right changes hue.");
    }
    public event EventHandler? ColorChanged;
    public MediaColor Color
    {
        get => _color;
        set
        {
            var opaque = MediaColor.FromRgb(value.R, value.G, value.B);
            if (_color == opaque) return;
            _color = opaque;
            var hsv = ToHsv(opaque);
            if (hsv.Saturation > 0) _hue = hsv.Hue;
            _saturation = hsv.Saturation; _value = hsv.Value; InvalidateVisual();
        }
    }
    private Rect ColorSquare => new(2, 2, Math.Max(1, Bounds.Width - 36), Math.Max(1, Bounds.Height - 4));
    private Rect HueStrip => new(Math.Max(1, Bounds.Width - 24), 2, 20, Math.Max(1, Bounds.Height - 4));
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var square = ColorSquare; var strip = HueStrip;
        var whiteToHue = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(Colors.White, 0), new(FromHsv(_hue, 1, 1), 1) }
        };
        var brightness = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(MediaColor.FromArgb(0, 0, 0, 0), 0), new(Colors.Black, 1) }
        };
        var hue = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(Colors.Red, 0), new(Colors.Yellow, 1d / 6), new(Colors.Lime, 2d / 6),
                new(Colors.Cyan, 3d / 6), new(Colors.Blue, 4d / 6), new(Colors.Magenta, 5d / 6), new(Colors.Red, 1) }
        };
        context.FillRectangle(whiteToHue, square); context.FillRectangle(brightness, square);
        context.DrawRectangle(null, new Pen(Brushes.Gray), square);
        context.DrawRectangle(hue, new Pen(Brushes.Gray), strip);
        var point = new Point(square.Left + square.Width * _saturation, square.Top + square.Height * (1 - _value));
        context.DrawEllipse(null, new Pen(Brushes.Black, 3), point, 4, 4);
        context.DrawEllipse(null, new Pen(Brushes.White, 1), point, 4, 4);
        var markerY = strip.Top + strip.Height * _hue / 360;
        context.DrawLine(new Pen(Brushes.Black, 4), new Point(strip.Left - 2, markerY), new Point(strip.Right + 2, markerY));
        context.DrawLine(new Pen(Brushes.White, 2), new Point(strip.Left - 2, markerY), new Point(strip.Right + 2, markerY));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        if (!ColorSquare.Contains(point) && !HueStrip.Contains(point)) return;
        Focus(); _dragging = true; _dragHue = HueStrip.Contains(point);
        e.Pointer.Capture(this); Pick(point); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    { base.OnPointerMoved(e); if (_dragging) { Pick(e.GetPosition(this)); e.Handled = true; } }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); if (_dragging) { _dragging = false; e.Pointer.Capture(null); e.Handled = true; } }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); _dragging = false; }
    private void Pick(Point point)
    {
        if (_dragHue) _hue = Math.Clamp((point.Y - HueStrip.Top) / HueStrip.Height, 0, 1) * 360;
        else
        {
            _saturation = Math.Clamp((point.X - ColorSquare.Left) / ColorSquare.Width, 0, 1);
            _value = 1 - Math.Clamp((point.Y - ColorSquare.Top) / ColorSquare.Height, 0, 1);
        }
        Publish();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Left or Key.Right)
            _hue = (_hue + (e.Key == Key.Left ? -3 : 3) + 360) % 360;
        else if (e.Key is Key.Left or Key.Right) _saturation = Math.Clamp(_saturation + (e.Key == Key.Left ? -.01 : .01), 0, 1);
        else _value = Math.Clamp(_value + (e.Key == Key.Down ? -.01 : .01), 0, 1);
        Publish(); e.Handled = true;
    }
    private void Publish()
    { _color = FromHsv(_hue, _saturation, _value); InvalidateVisual(); ColorChanged?.Invoke(this, EventArgs.Empty); }
    public static MediaColor FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360; saturation = Math.Clamp(saturation, 0, 1); value = Math.Clamp(value, 0, 1);
        var chroma = value * saturation; var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1)); var offset = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        byte Channel(double channel) => (byte)Math.Clamp(Math.Round((channel + offset) * 255), 0, 255);
        return MediaColor.FromRgb(Channel(red), Channel(green), Channel(blue));
    }
    public static (double Hue, double Saturation, double Value) ToHsv(MediaColor color)
    {
        var red = color.R / 255d; var green = color.G / 255d; var blue = color.B / 255d;
        var max = Math.Max(red, Math.Max(green, blue)); var min = Math.Min(red, Math.Min(green, blue)); var delta = max - min;
        var hue = delta == 0 ? 0 : max == red ? (green - blue) / delta % 6 : max == green ? (blue - red) / delta + 2 : (red - green) / delta + 4;
        return ((hue * 60 + 360) % 360, max == 0 ? 0 : delta / max, max);
    }
}
