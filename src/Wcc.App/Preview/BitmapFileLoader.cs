using System.IO;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Wcc.Core.References;

namespace Wcc.App.Preview;

public static class BitmapFileLoader
{
    /// <summary>Reuses the ordinary decoder unless the complete expression has
    /// a supported, fully in-bounds literal crop chain. No engine process or files.</summary>
    public static Bitmap? TryLoad(string? absolutePath, string? imageExpression)
    {
        if (!WccImageExpressionCrop.TryParse(WccImageExpressionParser.TryParse(imageExpression), out var crops))
            return TryLoad(absolutePath);
        try
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath)) return null;
            using var codec = SKCodec.Create(absolutePath);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > 16 * 1024 * 1024 ||
                !WccImageExpressionCrop.TryResolve(crops, codec.Info.Width, codec.Info.Height, out var crop)) return TryLoad(absolutePath);
            using var source = SKBitmap.Decode(codec);
            using var subset = new SKBitmap();
            if (source is null || !source.ExtractSubset(subset, new SKRectI(crop.X, crop.Y, crop.X + crop.Width, crop.Y + crop.Height)))
                return TryLoad(absolutePath);
            using var encoded = subset.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null) return TryLoad(absolutePath);
            using var stream = encoded.AsStream();
            return new Bitmap(stream);
        }
        catch { return TryLoad(absolutePath); }
    }

    public static string ImageExpressionPreviewHint(string? imageExpression)
    {
        var expression = WccImageExpressionParser.TryParse(imageExpression);
        if (expression?.HasModifiers != true) return "";
        return WccImageExpressionCrop.TryParse(expression, out _)
            ? "\nCropped preview when the literal rectangles fit the image; otherwise the base image is shown."
            : "\nBase image preview; these image modifiers are not rendered here.";
    }

    public static Bitmap? TryLoad(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return null;

        if (!File.Exists(absolutePath))
            return null;

        try
        {
            return new Bitmap(absolutePath);
        }
        catch
        {
            return null;
        }
    }
}