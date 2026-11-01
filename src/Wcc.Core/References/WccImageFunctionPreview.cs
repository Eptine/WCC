using System.Globalization;

namespace Wcc.Core.References;

public readonly record struct WccRgbaColor(byte Red, byte Green, byte Blue, byte Alpha = 255);

/// <summary>Straight, interleaved RGBA bytes. Rendering snapshots these bytes and never changes the input.</summary>
public sealed class WccRgbaImage
{
    public const int MaximumPixels = 16 * 1024 * 1024;
    public WccRgbaImage(int width, int height, byte[] pixels) : this(width, height, pixels, false) { }
    private WccRgbaImage(int width, int height, byte[] pixels, bool ownsPixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > MaximumPixels
            || pixels.LongLength != (long)width * height * 4) throw new ArgumentException("A bounded complete RGBA image is required.");
        Width = width; Height = height; Pixels = ownsPixels ? pixels : (byte[])pixels.Clone();
    }
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    internal static WccRgbaImage Owned(int width, int height, byte[] pixels) => new(width, height, pixels, true);
    internal WccRgbaImage Copy() => new(Width, Height, Pixels);
}

public sealed class WccImageFunctionPreviewContext
{
    public string? SourcePath { get; init; }
    public string? SourceSha256 { get; init; }
    public Func<string, IReadOnlyList<WccRgbaColor>?>? PaletteResolver { get; init; }
    public Func<string, IReadOnlyList<WccRgbaColor>?>? ColorRangeResolver { get; init; }
    public Func<int, string?>? TeamColorResolver { get; init; }
}

public sealed record WccImageFunctionPreviewResult(WccRgbaImage Image, bool IsComplete,
    IReadOnlyList<string> Notes, int AppliedFunctions)
{
    /// <summary>Whether output bounds are known despite any approximate or unavailable pixel effects.</summary>
    public bool HasKnownGeometry { get; init; } = true;
}

/// <summary>Bounded independent local preview, with no files, engine process or source-write authority.</summary>
public static partial class WccImageFunctionPreview
{
    public static WccImageFunctionPreviewResult Render(WccRgbaImage image, WccImageFunctionDocument document,
        Func<string, WccRgbaImage?>? imageResolver = null, CancellationToken cancellationToken = default,
        WccImageFunctionPreviewContext? context = null) => RenderCore(image, document, imageResolver, cancellationToken, context, 0);

    private static WccImageFunctionPreviewResult RenderCore(WccRgbaImage image, WccImageFunctionDocument document,
        Func<string, WccRgbaImage?>? resolver, CancellationToken cancellationToken,
        WccImageFunctionPreviewContext? context, int depth)
    {
        ArgumentNullException.ThrowIfNull(image); ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var result = image.Copy(); var notes = new List<string>(); var complete = true; var knownGeometry = true; var applied = 0;
        // Raw source text, rather than mutable UI metadata, decides the preview.
        var parsed = WccImageFunctionDocument.Parse(document.BuildExpression());
        if (!parsed.IsComplete) return new(result, false, Array.AsReadOnly(new[] { parsed.Diagnostic! }), 0) { HasKnownGeometry = false };
        var steps = parsed.Steps.Where(step => step.IsEnabled).ToArray();
        var ordered = steps.Where(step => step.Name is "RC" or "TC" or "PAL")
            .Concat(steps.Where(step => step.Name is not ("RC" or "TC" or "PAL")));
        foreach (var step in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var arguments = WccImageFunctionDocument.SplitArguments(step.Arguments);
                var empty = string.IsNullOrWhiteSpace(step.Arguments);
                WccRgbaImage? nested = null;
                if (step.Name is "BLIT" or "MASK" or "L")
                {
                    if (depth >= 8) throw new ArgumentException("Nested image preview exceeds eight levels.");
                    var nestedDocument = WccImageFunctionDocument.Parse(arguments[0]);
                    var source = resolver?.Invoke(nestedDocument.BaseReference);
                    if (source is null) throw new ArgumentException("The referenced image is not resolved for local preview.");
                    var nestedResult = RenderCore(source, nestedDocument, resolver, cancellationToken, context, depth + 1);
                    nested = nestedResult.Image;
                    if (!nestedResult.IsComplete) complete = false;
                    notes.AddRange(nestedResult.Notes.Select(note => step.Name + " image: " + note));
                }
                switch (step.Name)
                {
                    case "NOP": RequireCount(arguments, empty ? 0 : 1, empty, 0); break;
                    case "RIGHT": case "NO_TOD_SHIFT":
                        RequireCount(arguments, empty ? 0 : 1, empty, 0);
                        notes.Add(step.Name + " is a context marker and does not change these preview pixels."); break;
                    case "DARKEN": case "BRIGHTEN":
                        throw new ArgumentException("This function was removed upstream; use the corresponding BLIT overlay.");
                    case "RC": case "PAL": case "TC": ApplyPalette(result, step, arguments, context, cancellationToken); break;
                    case "FL": result = Flip(result, empty || step.Arguments.Contains("horiz", StringComparison.Ordinal),
                        step.Arguments.Contains("vert", StringComparison.Ordinal), cancellationToken); break;
                    case "SCALE": case "SCALE_SHARP": case "SCALE_INTO": case "SCALE_INTO_SHARP":
                        RequireCount(arguments, 2); var width = Integer(arguments[0]); var height = Integer(arguments[1]);
                        if (width <= 0) width = result.Width; if (height <= 0) height = result.Height;
                        if (step.Name.StartsWith("SCALE_INTO", StringComparison.Ordinal))
                        {
                            var factor = Math.Min((double)width / result.Width, (double)height / result.Height);
                            width = Math.Max(1, (int)(result.Width * factor)); height = Math.Max(1, (int)(result.Height * factor));
                        }
                        result = Scale(result, width, height, step.Name.EndsWith("SHARP", StringComparison.Ordinal), cancellationToken);
                        if (!step.Name.EndsWith("SHARP", StringComparison.Ordinal))
                        { complete = false; notes.Add(step.Name + " uses approximate local smooth sampling; check the engine's pixels and alpha."); } break;
                    case "XBRZ":
                        RequireCount(arguments, 1); var multiplier = Integer(arguments[0]);
                        if (multiplier is < 2 or > 5) throw new ArgumentException("xBRZ scale must be 2, 3, 4 or 5.");
                        result = Scale(result, checked(result.Width * multiplier), checked(result.Height * multiplier), true, cancellationToken);
                        complete = false; notes.Add("XBRZ uses nearest-neighbor local approximation; check the engine's pixel-art result."); break;
                    case "ROTATE":
                        var degrees = empty ? 90 : Number(arguments[0]); if (!empty) RequireCount(arguments, 1);
                        result = Rotate(result, degrees, cancellationToken);
                        if (degrees % 90 != 0) { complete = false; knownGeometry = false; notes.Add("Arbitrary-angle rotation uses local sampling; check the engine's edges and bounds."); } break;
                    case "CROP":
                        RequireCount(arguments, 4);
                        result = Crop(result, Integer(arguments[0]), Integer(arguments[1]), Integer(arguments[2]), Integer(arguments[3]), cancellationToken); break;
                    case "CROP_TRANSPARENCY": RequireCount(arguments, empty ? 0 : 1, empty, 0); result = Trim(result, cancellationToken); break;
                    case "PAD": result = Pad(result, arguments, cancellationToken); break;
                    case "BLIT": case "MASK":
                        if (arguments.Count is < 1 or > 3) throw new ArgumentException("Use image[,x,y].");
                        var compositeX = arguments.Count > 1 ? Integer(arguments[1]) : 0;
                        var compositeY = arguments.Count > 2 ? Integer(arguments[2]) : 0;
                        if (step.Name == "MASK" && (compositeX < 0 || compositeY < 0))
                            throw new ArgumentException("Mask positions must be nonnegative; the local stage is retained.");
                        result = Composite(result, nested!, compositeX, compositeY, step.Name == "MASK", cancellationToken);
                        if (step.Name == "BLIT")
                        { complete = false; notes.Add("BLIT uses approximate local alpha blending; confirm the engine's composite pixels."); } break;
                    case "L":
                        RequireCount(arguments, 1);
                        var resizeLightmap = nested!.Width != result.Width || nested.Height != result.Height;
                        var lightmap = resizeLightmap ? Scale(nested, result.Width, result.Height, false, cancellationToken) : nested;
                        Transform(result, (r, g, b, a, offset) => new(Clamp(r + (lightmap.Pixels[offset] - 128) * 2),
                            Clamp(g + (lightmap.Pixels[offset + 1] - 128) * 2), Clamp(b + (lightmap.Pixels[offset + 2] - 128) * 2), a), cancellationToken);
                        if (resizeLightmap)
                        { complete = false; notes.Add("L uses approximate local lightmap resizing; confirm the engine's lighting pixels."); } break;
                    case "BL":
                        RequireCount(arguments, 1); var radius = Integer(arguments[0]);
                        if (radius is < 0 or > 64) throw new ArgumentException("Local blur radius must be between 0 and 64.");
                        result = Blur(result, radius, cancellationToken); complete = false;
                        notes.Add("BL uses a local box-blur approximation; confirm the engine's blur."); break;
                    case "CHAN": case "ADJUST_ALPHA": ApplyFormulas(result, step.Name, arguments, cancellationToken); break;
                    default:
                        if (!ApplyColor(result, step.Name, arguments, empty, cancellationToken))
                        {
                            knownGeometry = false;
                            throw new ArgumentException("This function has no local pixel implementation. Its source is retained for engine review.");
                        }
                        if (step.Name == "O" && Math.Clamp(Factor(arguments[0]), 0, 1) is > 0 and < 1)
                        { complete = false; notes.Add("O uses approximate local fractional alpha rounding; confirm the engine's alpha bytes."); }
                        break;
                }
                applied++;
            }
            catch (Exception exception) when (exception is ArgumentException or ArithmeticException or InvalidOperationException)
            {
                complete = false; notes.Add(step.Name + ": " + exception.Message);
                if (step.Name is "SCALE" or "SCALE_SHARP" or "SCALE_INTO" or "SCALE_INTO_SHARP"
                    or "XBRZ" or "ROTATE" or "CROP" or "CROP_TRANSPARENCY" or "PAD") knownGeometry = false;
            }
        }
        return new(result, complete, Array.AsReadOnly(notes.Distinct(StringComparer.Ordinal).ToArray()), applied) { HasKnownGeometry = knownGeometry };
    }

    private static void RequireCount(IReadOnlyList<string> arguments, int count, bool empty = false, int? expected = null)
    {
        if ((empty ? 0 : arguments.Count) != (expected ?? count)) throw new ArgumentException("Unexpected number of function arguments.");
    }
    private static int Integer(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new ArgumentException("A literal integer is required for local preview.");
    private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new ArgumentException("A finite literal number is required for local preview.");
    private static double Factor(string text) => text.TrimEnd().EndsWith('%') ? Number(text.TrimEnd()[..^1]) / 100 : Number(text);
    private static byte Clamp(double value) => (byte)Math.Clamp(value, 0, 255);
    private static byte[] Buffer(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > WccRgbaImage.MaximumPixels)
            throw new ArgumentException("The resulting image exceeds the bounded preview dimensions.");
        return new byte[checked(width * height * 4)];
    }
}