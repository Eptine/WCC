namespace Wcc.Core.References;

public sealed record WccImageFunctionDefinition(string Name, string Label, string Category,
    string DefaultArguments, string ParameterHint, string PreviewNote = "");

/// <summary>Editor inventory from the official ImagePathFunctions reference; no source or render authority.</summary>
public static class WccImageFunctionCatalog
{
    public static IReadOnlyList<WccImageFunctionDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        D("BLEND", "Blend color", "Color", "255,0,0,50%", "red,green,blue,opacity (0..1 or percent)"),
        D("BW", "Black and white", "Color", "128", "threshold 0..255"),
        D("CS", "Color shift", "Color", "0,0,0", "red,green,blue channel additions; omitted channels are zero"),
        D("R", "Red shift", "Color", "0", "red channel addition"),
        D("G", "Green shift", "Color", "0", "green channel addition"),
        D("B", "Blue shift", "Color", "0", "blue channel addition"),
        D("GS", "Greyscale", "Color", "", "no parameters"),
        D("L", "Lightmap", "Color", "", "image expression", "Needs the selected lightmap image; engine review checks the exact resource context."),
        D("NEG", "Negative / solarize", "Color", "", "no parameters, one threshold, or red,green,blue thresholds (-1..255)"),
        D("PAL", "Replace palette", "Color", "magenta>ellipse_red", "source palette > destination palette (names or hexadecimal lists)", "Palette operations run before other functions. Named palettes need context."),
        D("RC", "Recolor", "Color", "magenta>red", "source palette > color range", "Palette operations run before other functions. Named colors need context."),
        D("SEPIA", "Sepia", "Color", "", "no parameters"),
        D("SWAP", "Swap channels", "Color", "blue,green,red", "red,green,blue[,alpha] channel names"),
        D("TC", "Team color", "Color", "1,magenta", "team number,source palette", "Needs team colors from the selected game context; engine review is authoritative."),
        D("FL", "Flip", "Transform", "horiz", "horiz, vert, horiz,vert, or empty for horizontal"),
        D("ROTATE", "Rotate", "Transform", "90", "clockwise degrees; omitted means 90"),
        D("SCALE", "Scale", "Transform", "72,72", "width,height; zero retains that source dimension"),
        D("SCALE_INTO", "Fit inside", "Transform", "72,72", "maximum width,height; preserve aspect ratio"),
        D("SCALE_SHARP", "Scale sharply", "Transform", "72,72", "width,height; nearest-neighbor pixels"),
        D("SCALE_INTO_SHARP", "Fit sharply", "Transform", "72,72", "maximum width,height; preserve aspect ratio"),
        D("XBRZ", "Scale pixel art (xBRZ)", "Transform", "2", "integer scale 2..5", "Local preview uses nearest-neighbor approximation. Confirm xBRZ with the engine."),
        D("BLIT", "Overlay image", "Canvas", ",0,0", "image expression,x,y", "Needs the selected overlay image."),
        D("CROP", "Crop", "Canvas", "0,0,72,72", "x,y,width,height"),
        D("CROP_TRANSPARENCY", "Trim transparent edges", "Canvas", "", "no parameters"),
        D("PAD", "Add transparent padding", "Canvas", "10", "one size, or top=,right=,bottom=,left= (short t/r/b/l accepted)"),
        D("MASK", "Alpha mask", "Canvas", ",0,0", "image expression,x,y", "Only mask alpha is used; regions outside the mask become transparent."),
        D("ADJUST_ALPHA", "Alpha formula", "Opacity", "alpha", "WFL pixel formula"),
        D("O", "Opacity", "Opacity", "50%", "factor 0..1 or percentage"),
        D("PLOT_ALPHA", "Show alpha", "Opacity", "", "no parameters; alpha becomes opaque greyscale"),
        D("WIPE_ALPHA", "Make opaque", "Opacity", "", "no parameters"),
        D("BG", "Background color", "Opacity", "0,0,0", "red,green,blue behind transparent pixels"),
        D("BL", "Blur", "Other", "1", "blur radius"),
        D("CHAN", "Channel formulas", "Other", "red,green,blue,alpha", "red formula,green formula,blue formula[,alpha formula]"),
        D("NOP", "No change", "Other", "", "no parameters"),
        D("NO_TOD_SHIFT", "Disable time-of-day shift", "Context", "", "no parameters", "Terrain/item context marker; no local pixel change."),
        D("RIGHT", "Portrait on right", "Context", "", "no parameters", "Message portrait placement marker; no local pixel change."),
        D("DARKEN", "Darken (removed)", "Legacy", "", "removed function", "Removed upstream; use BLIT(misc/tod-dark.png). Original text is preserved."),
        D("BRIGHTEN", "Brighten (removed)", "Legacy", "", "removed function", "Removed upstream; use BLIT(misc/tod-bright.png). Original text is preserved.")
    });
    public static WccImageFunctionDefinition? Find(string name) => Definitions.FirstOrDefault(definition =>
        string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase));
    private static WccImageFunctionDefinition D(string name, string label, string category, string arguments,
        string hint, string note = "") => new(name, label, category, arguments, hint, note);
}