using System.Globalization;

namespace Wcc.Core.References;

/// <summary>A literal pixel rectangle, never a resource lookup or write route.</summary>
public readonly record struct WccImageCrop(int X, int Y, int Width, int Height);

/// <summary>Bounded local-preview subset of IPF. Other functions, omitted/default
/// arguments and clipping retain the existing base-image or engine-renderer route.</summary>
public static class WccImageExpressionCrop
{
    public static bool TryParse(WccImageExpression? expression, out IReadOnlyList<WccImageCrop> crops)
    {
        crops = [];
        if (expression is null || expression.ModifierSuffix.Length is 0 or > 4096) return false;
        var remaining = expression.ModifierSuffix.AsSpan();
        var result = new List<WccImageCrop>();
        Span<int> values = stackalloc int[4];
        while (!remaining.IsEmpty)
        {
            if (result.Count >= 16 || !remaining.StartsWith("~CROP(", StringComparison.Ordinal)) return false;
            var end = remaining.IndexOf(')');
            if (end < 0) return false;
            var arguments = remaining[6..end];
            for (var index = 0; index < 4; index++)
            {
                var comma = arguments.IndexOf(',');
                var part = comma >= 0 ? arguments[..comma] : arguments;
                if ((index < 3) != (comma >= 0) || !int.TryParse(part,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                        CultureInfo.InvariantCulture, out values[index])) return false;
                arguments = comma >= 0 ? arguments[(comma + 1)..] : [];
            }
            if (values[0] < 0 || values[1] < 0 || values[2] <= 0 || values[3] <= 0) return false;
            result.Add(new(values[0], values[1], values[2], values[3]));
            remaining = remaining[(end + 1)..];
        }
        crops = Array.AsReadOnly(result.ToArray());
        return crops.Count > 0;
    }

    /// <summary>Composes left-to-right crops only when every rectangle is wholly
    /// within its input. No inferred clipping, padding or default dimensions.</summary>
    public static bool TryResolve(IReadOnlyList<WccImageCrop> crops, int imageWidth, int imageHeight, out WccImageCrop crop)
    {
        crop = default;
        if (crops.Count is 0 or > 16 || imageWidth <= 0 || imageHeight <= 0) return false;
        var x = 0; var y = 0; var width = imageWidth; var height = imageHeight;
        foreach (var next in crops)
        {
            if (next.X < 0 || next.Y < 0 || next.Width <= 0 || next.Height <= 0 ||
                next.Width > width || next.Height > height || next.X > width - next.Width || next.Y > height - next.Height) return false;
            x += next.X; y += next.Y; width = next.Width; height = next.Height;
        }
        crop = new(x, y, width, height);
        return true;
    }
}
