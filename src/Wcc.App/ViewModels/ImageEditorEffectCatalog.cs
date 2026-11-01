using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

public sealed record ImageEditorEffectGroup(string Id, string Label, string IconFile,
    IReadOnlyList<WccImageFunctionDefinition> Flavors)
{
    public WccImageFunctionDefinition DefaultFlavor => Flavors[0];
}

/// <summary>Presentation groups only. The authored expression continues to use the actual Wesnoth functions.</summary>
public static class ImageEditorEffectCatalog
{
    public static IReadOnlyList<ImageEditorEffectGroup> Groups { get; } = Array.AsReadOnly(new[]
    {
        Group("resize", "Resize", "resize.png", "SCALE", "SCALE_INTO", "SCALE_SHARP", "SCALE_INTO_SHARP", "XBRZ"),
        Group("crop", "Crop", "crop.png", "CROP"),
        Group("rotate", "Rotate", "rotate_right.png", "ROTATE"),
        Group("flip", "Flip", "flip_horizontal.png", "FL"),
        Group("palette", "Replace colors", "Effect_ReplacePalet.png", "RC", "PAL", "SWAP", "TC"),
        Group("shift", "Color shift", "Effect_ColorShift.png", "CS", "R", "G", "B", "CHAN"),
        Group("blend", "Color overlay", "Effect_Overlay.png", "BLEND"),
        Group("background", "Background color", "Effect_background.png", "BG"),
        Group("grey", "Greyscale", "Effect_Grey.png", "GS"),
        Group("bw", "Black and white", "Effect_SW.png", "BW"),
        Group("sepia", "Sepia", "Effect_Sepia.png", "SEPIA"),
        Group("invert", "Invert / solarize", "Effect_Invert.png", "NEG"),
        Group("opacity", "Opacity", "Effect_Opacity.png", "O"),
        Group("alpha", "Alpha", "Effect_alpha.png", "ADJUST_ALPHA", "PLOT_ALPHA", "WIPE_ALPHA"),
        Group("blur", "Blur", "Effect_Bluur.png", "BL"),
        Group("light", "Lightmap", "Effect_Lightmap.png", "L"),
        Group("mask", "Alpha mask", "Effect_Mask.png", "MASK"),
        // BLIT is a separate image operation, rather than the solid-color BLEND effect.
        Group("image", "Overlay image", "Effect_Overlay.png", "BLIT")
    });

    public static IReadOnlyList<ImageEditorEffectGroup> CommonEffects { get; } = Array.AsReadOnly(
        Groups.Where(group => group.Id is not ("resize" or "crop" or "rotate" or "flip")).ToArray());

    public static IReadOnlyList<WccImageFunctionDefinition> OtherEffects { get; } = Array.AsReadOnly(
        WccImageFunctionCatalog.Definitions.Where(definition =>
            !Groups.Any(group => group.Flavors.Contains(definition))).ToArray());

    public static ImageEditorEffectGroup? Find(string function) => Groups.FirstOrDefault(group =>
        group.Flavors.Any(flavor => flavor.Name.Equals(function, StringComparison.OrdinalIgnoreCase)));

    private static ImageEditorEffectGroup Group(string id, string label, string icon, params string[] names) =>
        new(id, label, icon, Array.AsReadOnly(names.Select(name => WccImageFunctionCatalog.Find(name)!).ToArray()));

    // Only known functions participate. Unrecognized/custom text remains intact and ordered.
    public static string? PriorityKey(ImageEditorModifierVm row)
    {
        if (WccImageFunctionCatalog.Find(row.Name) is null || !row.ToStep().RawText.TrimEnd().EndsWith(')')) return null;
        if (Find(row.Name)?.Id == "resize") return "resize";
        if (row.Name == "FL")
        {
            var values = WccImageFunctionDocument.SplitArguments(row.Arguments);
            if (values.Any(value => value is not ("" or "horiz" or "vert"))) return null;
            var horizontal = row.Arguments.Trim().Length == 0 || values.Contains("horiz");
            return "FL:" + (horizontal ? "h" : "") + (values.Contains("vert") ? "v" : "");
        }
        return row.Name;
    }
}
