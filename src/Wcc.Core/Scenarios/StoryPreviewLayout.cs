namespace Wcc.Core.Scenarios;

public readonly record struct StoryPreviewSize(double Width, double Height);
public readonly record struct StoryPreviewPoint(double X, double Y);
public readonly record struct StoryPreviewRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}
public enum StoryPreviewTextLayout { Top, Middle, Bottom }
public sealed record StoryPreviewBackgroundOptions
{
    public bool ScaleHorizontally { get; init; } = true;
    public bool ScaleVertically { get; init; } = true;
    public bool? Scale { get; init; }
    public bool KeepAspectRatio { get; init; } = true;
    public bool TileHorizontally { get; init; }
    public bool TileVertically { get; init; }
    public bool? Tile { get; init; }
    public bool BaseLayer { get; init; }
}
public sealed record StoryPreviewBackgroundLayout(StoryPreviewSize? IntrinsicSize, StoryPreviewRect Bounds,
    bool TileHorizontally, bool TileVertically, bool BaseLayer)
{
    public bool HasImage => IntrinsicSize.HasValue;
    public bool IsTiled => TileHorizontally || TileVertically;
    public StoryPreviewBaseTransform BaseTransform => IntrinsicSize is { } size
        ? new(Bounds.X, Bounds.Y, Bounds.Width / size.Width, Bounds.Height / size.Height) : StoryPreviewBaseTransform.Identity;
}
public readonly record struct StoryPreviewBaseTransform(double OriginX, double OriginY, double ScaleX, double ScaleY)
{
    public static StoryPreviewBaseTransform Identity => new(0, 0, 1, 1);
    public StoryPreviewPoint ToCanvas(StoryPreviewPoint p)
    {
        StoryPreviewLayout.ValidateTransform(this); StoryPreviewLayout.ValidatePoint(p, nameof(p));
        var result = new StoryPreviewPoint(OriginX + p.X * ScaleX, OriginY + p.Y * ScaleY);
        StoryPreviewLayout.ValidatePoint(result, nameof(p)); return result;
    }
    public StoryPreviewPoint ToOriginal(StoryPreviewPoint p)
    {
        StoryPreviewLayout.ValidateTransform(this); StoryPreviewLayout.ValidatePoint(p, nameof(p));
        var result = new StoryPreviewPoint((p.X - OriginX) / ScaleX, (p.Y - OriginY) / ScaleY);
        StoryPreviewLayout.ValidatePoint(result, nameof(p)); return result;
    }
}
/// <summary>Pure static geometry from Wesnoth 1.19.28 story viewer; asset resolution and edit authority are external.</summary>
public static class StoryPreviewLayout
{
    public static StoryPreviewBackgroundLayout LayoutBackground(StoryPreviewSize viewport, StoryPreviewSize? intrinsicSize, StoryPreviewBackgroundOptions? options = null)
    {
        var canvas = PixelSize(viewport, nameof(viewport)); options ??= new();
        var tileH = options.Tile ?? options.TileHorizontally; var tileV = options.Tile ?? options.TileVertically;
        if (intrinsicSize is null) return new(null, default, tileH, tileV, options.BaseLayer);
        var intrinsic = PixelSize(intrinsicSize.Value, nameof(intrinsicSize));
        var scaleH = options.Scale ?? options.ScaleHorizontally; var scaleV = options.Scale ?? options.ScaleVertically;
        var width = intrinsic.Width; var height = intrinsic.Height;
        if (scaleH && options.KeepAspectRatio) height = Math.Min(intrinsic.Height * canvas.Width / intrinsic.Width, canvas.Height);
        else if (scaleV || tileV) height = canvas.Height;
        if (scaleV && options.KeepAspectRatio) width = Math.Min(intrinsic.Width * canvas.Height / intrinsic.Height, canvas.Width);
        else if (scaleH || tileH) width = canvas.Width;
        width = PixelDimensionOrOriginal(width, intrinsic.Width); height = PixelDimensionOrOriginal(height, intrinsic.Height);
        var x = tileH ? 0 : Math.Max(Math.Truncate(canvas.Width / 2) - Math.Truncate(width / 2), 0);
        var y = tileV ? 0 : Math.Max(Math.Truncate(canvas.Height / 2) - Math.Truncate(height / 2), 0);
        return new(intrinsic, new(x, y, width, height), tileH, tileV, options.BaseLayer);
    }
    public static StoryPreviewBaseTransform SelectBase(IReadOnlyList<StoryPreviewBackgroundLayout> layers)
    {
        ArgumentNullException.ThrowIfNull(layers); StoryPreviewBackgroundLayout? selected = null;
        foreach (var layer in layers) { ArgumentNullException.ThrowIfNull(layer); if (selected is null || layer.BaseLayer) selected = layer; }
        var transform = selected?.BaseTransform ?? StoryPreviewBaseTransform.Identity; ValidateTransform(transform); return transform;
    }
    public static StoryPreviewRect FirstTileBounds(StoryPreviewBackgroundLayout layer)
    {
        ArgumentNullException.ThrowIfNull(layer); if (layer.IntrinsicSize is not { } size) return default;
        if (!layer.IsTiled) return layer.Bounds;
        return new(layer.Bounds.X - Math.Truncate((layer.Bounds.Width - size.Width) / 2), layer.Bounds.Y - Math.Truncate((layer.Bounds.Height - size.Height) / 2), size.Width, size.Height);
    }
    public static StoryPreviewRect OverlayBounds(StoryPreviewBaseTransform transform, StoryPreviewSize intrinsicSize, double x, double y, bool centered = false, bool resizeWithBackground = false)
    {
        ValidateTransform(transform); ValidatePoint(new(x, y), nameof(x)); var intrinsic = PixelSize(intrinsicSize, nameof(intrinsicSize));
        var width = resizeWithBackground ? PixelDimensionOrOriginal(intrinsic.Width * transform.ScaleX, intrinsic.Width) : intrinsic.Width;
        var height = resizeWithBackground ? PixelDimensionOrOriginal(intrinsic.Height * transform.ScaleY, intrinsic.Height) : intrinsic.Height;
        var cx = Math.Truncate(x * transform.ScaleX) + transform.OriginX; var cy = Math.Truncate(y * transform.ScaleY) + transform.OriginY;
        if (centered) { cx -= Math.Truncate(width / 2); cy -= Math.Truncate(height / 2); }
        ValidatePoint(new(cx, cy), nameof(x)); return new(cx, cy, width, height);
    }
    public static StoryPreviewTextLayout DecodeTextLayout(string? value) => value switch { "top" => StoryPreviewTextLayout.Top, "middle" => StoryPreviewTextLayout.Middle, _ => StoryPreviewTextLayout.Bottom };
    public static StoryPreviewPoint DecodeTitlePosition(string? value, StoryPreviewTextLayout layout)
    {
        if (value == "centered") return new(50, 50); if (string.IsNullOrEmpty(value)) return default;
        var positions = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return positions.Length switch { 0 => default, 1 => new(Horizontal(positions[0]), Vertical(positions[0], layout)), _ => new(Horizontal(positions[0]), Vertical(positions[1], layout)) };
    }
    public static StoryPreviewRect TitleBounds(StoryPreviewSize viewport, StoryPreviewSize measuredTitle, string? position, StoryPreviewTextLayout layout)
    {
        var canvas = PixelSize(viewport, nameof(viewport)); ValidateMeasuredSize(measuredTitle, nameof(measuredTitle));
        var p = DecodeTitlePosition(position, layout);
        return new(Math.Max(Math.Truncate(p.X * (canvas.Width - 10 - measuredTitle.Width) / 100), 10), Math.Max(Math.Truncate(p.Y * (canvas.Height - 10 - measuredTitle.Height) / 100), 10), measuredTitle.Width, measuredTitle.Height);
    }
    public static double MinimumTextPanelHeight(StoryPreviewSize viewport) => Math.Max(150, Math.Truncate(PixelSize(viewport, nameof(viewport)).Height / 4));
    public static StoryPreviewRect TextContentBounds(StoryPreviewSize viewport, double measuredHeight, StoryPreviewTextLayout layout)
    {
        var canvas = PixelSize(viewport, nameof(viewport)); var column = Math.Min(Math.Truncate(canvas.Width / 1.5), 1000);
        var panel = TextPanelBounds(canvas, measuredHeight, layout);
        return new(Math.Truncate((canvas.Width - column) / 2) + 20, panel.Y + 20, Math.Max(1, column - 43), Math.Max(0, panel.Height - 113));
    }
    public static StoryPreviewRect SkipBounds(StoryPreviewSize viewport, StoryPreviewRect panel)
    {
        var canvas = PixelSize(viewport, nameof(viewport)); return new(Math.Truncate((canvas.Width - 250) / 2), panel.Bottom - 70, 250, 50);
    }
    public static StoryPreviewRect ArrowBounds(StoryPreviewSize viewport, StoryPreviewRect panel, bool right)
    {
        var canvas = PixelSize(viewport, nameof(viewport)); var column = Math.Min(Math.Truncate(canvas.Width / 1.5), 1000);
        var gutter = Math.Truncate((canvas.Width - column) / 2); var x = Math.Truncate((gutter - 128) / 2);
        return new(right ? canvas.Width - gutter + x : x, panel.Bottom - 148, 128, 128);
    }
    public static StoryPreviewRect TextPanelBounds(StoryPreviewSize viewport, double measuredHeight, StoryPreviewTextLayout layout)
    {
        var canvas = PixelSize(viewport, nameof(viewport)); if (!double.IsFinite(measuredHeight) || measuredHeight < 0) throw new ArgumentOutOfRangeException(nameof(measuredHeight));
        var h = Math.Min(measuredHeight, canvas.Height); var y = layout switch { StoryPreviewTextLayout.Top => 0, StoryPreviewTextLayout.Middle => Math.Truncate((canvas.Height - h) / 2), _ => canvas.Height - h }; return new(0, y, canvas.Width, h);
    }
    internal static void ValidateTransform(StoryPreviewBaseTransform t)
    {
        if (!double.IsFinite(t.OriginX) || !double.IsFinite(t.OriginY) || !double.IsFinite(t.ScaleX) || !double.IsFinite(t.ScaleY) || t.ScaleX <= 0 || t.ScaleY <= 0) throw new ArgumentOutOfRangeException(nameof(t));
    }
    internal static void ValidatePoint(StoryPreviewPoint p, string name) { if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) throw new ArgumentOutOfRangeException(name); }
    private static StoryPreviewSize PixelSize(StoryPreviewSize s, string name)
    {
        if (!double.IsFinite(s.Width) || !double.IsFinite(s.Height) || s.Width < 1 || s.Height < 1 || s.Width > int.MaxValue || s.Height > int.MaxValue) throw new ArgumentOutOfRangeException(name); return new(Math.Truncate(s.Width), Math.Truncate(s.Height));
    }
    private static void ValidateMeasuredSize(StoryPreviewSize s, string name) { if (!double.IsFinite(s.Width) || !double.IsFinite(s.Height) || s.Width < 0 || s.Height < 0) throw new ArgumentOutOfRangeException(name); }
    private static double PixelDimensionOrOriginal(double v, double original) { if (!double.IsFinite(v) || v < 0) throw new ArgumentOutOfRangeException(nameof(v)); var pixels = Math.Truncate(v); return pixels == 0 ? original : pixels; }
    private static double Horizontal(string v) => v switch { "center" => 50, "right" => 100, _ => 0 };
    private static double Vertical(string v, StoryPreviewTextLayout l) => v switch { "top" => l == StoryPreviewTextLayout.Top ? 50 : 0, "middle" => l == StoryPreviewTextLayout.Middle ? 0 : 50, "bottom" => l == StoryPreviewTextLayout.Bottom ? 50 : 100, _ => 0 };
}
