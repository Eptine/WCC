namespace Wcc.Core.References;

public static partial class WccImageFunctionPreview
{
    private static bool ApplyColor(WccRgbaImage image, string name, IReadOnlyList<string> args, bool empty, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "GS": RequireCount(args, 0, empty); Transform(image, (r, g, b, a, _) => { var grey = Clamp(r * .299 + g * .587 + b * .114); return new(grey, grey, grey, a); }, cancellationToken); return true;
            case "SEPIA": RequireCount(args, 0, empty); Transform(image, (r, g, b, a, _) => new(Clamp(r * .393 + g * .769 + b * .189), Clamp(r * .349 + g * .686 + b * .168), Clamp(r * .272 + g * .534 + b * .131), a), cancellationToken); return true;
            case "BW": RequireCount(args, 1); var threshold = Integer(args[0]); if (threshold is < 0 or > 255) throw new ArgumentException("Threshold must be 0..255.");
                Transform(image, (r, g, b, a, _) => { var grey = (byte)(Clamp(r * .299 + g * .587 + b * .114) > threshold ? 255 : 0); return new(grey, grey, grey, a); }, cancellationToken); return true;
            case "CS": case "R": case "G": case "B":
                if (empty || args.Count > (name == "CS" ? 3 : 1)) throw new ArgumentException("Supply one to three literal channel shifts.");
                var red = name is "CS" or "R" ? Integer(args[0]) : 0;
                var green = name == "G" ? Integer(args[0]) : name == "CS" && args.Count > 1 ? Integer(args[1]) : 0;
                var blue = name == "B" ? Integer(args[0]) : name == "CS" && args.Count > 2 ? Integer(args[2]) : 0;
                Transform(image, (r, g, b, a, _) => new(Clamp((double)r + red), Clamp((double)g + green), Clamp((double)b + blue), a), cancellationToken); return true;
            case "NEG":
                if (!empty && args.Count is not (1 or 3)) throw new ArgumentException("Supply no threshold, one threshold, or three thresholds.");
                var limits = empty ? new[] { -1, -1, -1 } : args.Count == 1 ? Enumerable.Repeat(Integer(args[0]), 3).ToArray() : args.Select(Integer).ToArray();
                if (limits.Any(value => value is < -1 or > 255)) throw new ArgumentException("Negative thresholds must be -1..255.");
                Transform(image, (r, g, b, a, _) => new((byte)(r > limits[0] ? 255 - r : r), (byte)(g > limits[1] ? 255 - g : g), (byte)(b > limits[2] ? 255 - b : b), a), cancellationToken); return true;
            case "BLEND": RequireCount(args, 4); var color = Rgb(args); var mix = Math.Clamp(Factor(args[3]), 0, 1);
                Transform(image, (r, g, b, a, _) => new(Clamp(r * (1 - mix) + color.Red * mix), Clamp(g * (1 - mix) + color.Green * mix), Clamp(b * (1 - mix) + color.Blue * mix), a), cancellationToken); return true;
            case "BG": RequireCount(args, 3); var background = Rgb(args);
                Transform(image, (r, g, b, a, _) => new(Clamp((r * a + background.Red * (255 - a)) / 255d), Clamp((g * a + background.Green * (255 - a)) / 255d), Clamp((b * a + background.Blue * (255 - a)) / 255d)), cancellationToken); return true;
            case "O": RequireCount(args, 1); var opacity = Math.Clamp(Factor(args[0]), 0, 1);
                Transform(image, (r, g, b, a, _) => new(r, g, b, Clamp(Math.Round(a * opacity, MidpointRounding.AwayFromZero))), cancellationToken); return true;
            case "WIPE_ALPHA": RequireCount(args, 0, empty); Transform(image, (r, g, b, _, _) => new(r, g, b), cancellationToken); return true;
            case "PLOT_ALPHA": RequireCount(args, 0, empty); Transform(image, (_, _, _, a, _) => new(a, a, a), cancellationToken); return true;
            case "SWAP":
                if (args.Count is not (3 or 4)) throw new ArgumentException("Supply three or four channel names.");
                var channels = args.Select(value => value switch { "red" => 0, "green" => 1, "blue" => 2, "alpha" => 3, _ => -1 }).ToArray();
                if (channels.Contains(-1)) throw new ArgumentException("Use red, green, blue or alpha.");
                Transform(image, (r, g, b, a, _) => new(Channel(channels[0], r, g, b, a), Channel(channels[1], r, g, b, a),
                    Channel(channels[2], r, g, b, a), channels.Length == 4 ? Channel(channels[3], r, g, b, a) : a), cancellationToken); return true;
            default: return false;
        }
    }
    private static byte Channel(int channel, byte r, byte g, byte b, byte a) => channel switch { 0 => r, 1 => g, 2 => b, _ => a };
    private static WccRgbaColor Rgb(IReadOnlyList<string> args)
    {
        var values = args.Take(3).Select(Integer).ToArray();
        if (values.Any(value => value is < 0 or > 255)) throw new ArgumentException("Color channels must be 0..255.");
        return new((byte)values[0], (byte)values[1], (byte)values[2]);
    }
    private static void Transform(WccRgbaImage image, Func<byte, byte, byte, byte, int, WccRgbaColor> transform, CancellationToken cancellationToken)
    {
        var pixels = image.Pixels;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
            var color = transform(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3], offset);
            pixels[offset] = color.Red; pixels[offset + 1] = color.Green; pixels[offset + 2] = color.Blue; pixels[offset + 3] = color.Alpha;
        }
    }
    private static void ApplyPalette(WccRgbaImage image, WccImageFunctionStep step, IReadOnlyList<string> arguments,
        WccImageFunctionPreviewContext? context, CancellationToken cancellationToken)
    {
        string sourceName; string targetName; var paletteSwitch = step.Name == "PAL";
        if (step.Name == "TC")
        {
            RequireCount(arguments, 2); var team = Integer(arguments[0]);
            targetName = context?.TeamColorResolver?.Invoke(team) ?? throw new ArgumentException("Team color is unavailable in this local preview context.");
            sourceName = arguments[1];
        }
        else
        {
            var parts = step.Arguments.Split('>');
            if (parts.Length != 2) throw new ArgumentException("Use source palette > target palette or range.");
            sourceName = parts[0].Trim(); targetName = parts[1].Trim();
        }
        var source = Palette(sourceName, context?.PaletteResolver);
        var target = Palette(targetName, paletteSwitch ? context?.PaletteResolver : context?.ColorRangeResolver);
        if (source.Count == 0 || target.Count == 0 || paletteSwitch && source.Count != target.Count || !paletteSwitch && target.Count < 3)
            throw new ArgumentException("The palette/range is unavailable or has incompatible dimensions. Named colors require the captured game context.");
        var map = new Dictionary<int, WccRgbaColor>();
        // Native palette interpolation uses integer RGB averages before forming the ratio.
        var midpoint = (source[0].Red + source[0].Green + source[0].Blue) / 3;
        for (var index = 0; index < source.Count; index++)
        {
            var old = source[index]; WccRgbaColor color;
            if (paletteSwitch) color = target[index];
            else
            {
                var level = (old.Red + old.Green + old.Blue) / 3;
                var low = level <= midpoint; var start = low ? target[2] : target[0]; var end = low ? target[0] : target[1];
                var ratio = low ? midpoint == 0 ? 1 : level / (double)midpoint : midpoint == 255 ? 0 : (level - midpoint) / (double)(255 - midpoint);
                color = new(Clamp(start.Red + (end.Red - start.Red) * ratio), Clamp(start.Green + (end.Green - start.Green) * ratio), Clamp(start.Blue + (end.Blue - start.Blue) * ratio));
            }
            map.TryAdd(old.Red << 16 | old.Green << 8 | old.Blue, color);
        }
        Transform(image, (r, g, b, a, _) => map.TryGetValue(r << 16 | g << 8 | b, out var color) ? color with { Alpha = a } : new(r, g, b, a), cancellationToken);
    }
    private static IReadOnlyList<WccRgbaColor> Palette(string text, Func<string, IReadOnlyList<WccRgbaColor>?>? resolver)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var colors = new List<WccRgbaColor>();
        foreach (var part in parts)
        {
            var hex = part.StartsWith('#') ? part[1..] : part;
            if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
                return resolver?.Invoke(text) ?? [];
            colors.Add(new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
        return colors;
    }
    private static void ApplyFormulas(WccRgbaImage image, string name, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (name == "ADJUST_ALPHA" ? arguments.Count != 1 : arguments.Count is < 1 or > 4) throw new ArgumentException("Supply one alpha formula or up to four channel formulas.");
        var formulas = new WccImagePixelFormula?[4];
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Length == 0) continue;
            if (!WccImageFormulaCompiler.TryCompile(arguments[index], out var formula, out var problem))
                throw new ArgumentException(problem ?? "This formula needs the engine preview.");
            formulas[name == "ADJUST_ALPHA" ? 3 : index] = formula;
        }
        var original = (byte[])image.Pixels.Clone(); var output = new byte[original.Length];
        for (var offset = 0; offset < original.Length; offset += 4)
        {
            if ((offset & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
            var pixel = new WccImageFormulaPixel(offset / 4 % image.Width, offset / 4 / image.Width, image.Width, image.Height,
                original[offset], original[offset + 1], original[offset + 2], original[offset + 3]);
            for (var channel = 0; channel < 4; channel++)
            {
                var value = formulas[channel]?.Invoke(pixel) ?? original[offset + channel];
                if (!double.IsFinite(value)) throw new ArgumentException("A pixel formula produced a nonfinite value.");
                output[offset + channel] = Clamp(value);
            }
        }
        output.CopyTo(image.Pixels, 0);
    }
}