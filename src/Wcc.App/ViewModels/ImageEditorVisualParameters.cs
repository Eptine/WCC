using System;
using System.Globalization;
using System.Linq;

namespace Wcc.App.ViewModels;

public readonly record struct ImageEditorSlider(string Label, double Minimum, double Maximum, double Value, int Component);
public readonly record struct ImageEditorNumericComponent(string Label, double Minimum, double Maximum, int Component);

/// <summary>Visual edits for bounded literal arguments. Inspection never rewrites an argument.</summary>
public static class ImageEditorVisualParameters
{
    public static bool TryPaletteParts(ImageEditorModifierVm row, out string source, out string destination)
    {
        source = destination = "";
        if (row.Name is not ("RC" or "PAL")) return false;
        var parts = row.Arguments.Split('>');
        if (parts.Length != 2) return false;
        source = parts[0].Trim(); destination = parts[1].Trim(); return true;
    }
    public static bool SetPalettePart(ImageEditorModifierVm row, bool sourcePart, string value)
    {
        if (!TryPaletteParts(row, out var source, out var destination) || value.Contains('>') || value.Contains('\n') || value.Contains('\r')) return false;
        if (value == (sourcePart ? source : destination)) return true;
        var parts = row.Arguments.Split('>'); parts[sourcePart ? 0 : 1] = value;
        row.Arguments = string.Join(">", parts); return true;
    }
    public static bool TryTeamColor(ImageEditorModifierVm row, out int team, out string palette)
    {
        team = 0; palette = "";
        if (row.Name != "TC" || SplitRawArguments(row.Arguments) is not { Length: 2 } values ||
            !int.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out team) || team < 1) return false;
        palette = values[1].Trim(); return true;
    }
    public static bool SetTeamColorNumber(ImageEditorModifierVm row, int team)
    {
        if (team < 1 || !TryTeamColor(row, out var current, out _)) return false;
        if (team == current) return true;
        var values = SplitRawArguments(row.Arguments)!; values[0] = team.ToString(CultureInfo.InvariantCulture);
        row.Arguments = string.Join(",", values); return true;
    }
    public static bool SetTeamColorPalette(ImageEditorModifierVm row, string palette)
    {
        if (!TryTeamColor(row, out _, out var current) || palette.IndexOfAny([',', '\r', '\n', '(', ')']) >= 0) return false;
        if (palette == current) return true;
        var values = SplitRawArguments(row.Arguments)!; values[1] = palette; row.Arguments = string.Join(",", values); return true;
    }
    public static bool NeedsAdvanced(ImageEditorModifierVm row)
    {
        if (row.Name is "BG" or "BLEND") return !TryRgb(row, out _, out _, out _) || row.Name == "BLEND" && !TrySlider(row, out _);
        if (SliderShape(row.Name) is not null) return !TrySlider(row, out _);
        if (ComponentShapes(row.Name) is { Length: > 0 } shapes && row.Name is not ("BLIT" or "MASK"))
            return shapes.Any(shape => !TryComponent(row, shape.Component, out _));
        if (row.Name == "SWAP") return !TryChannels(row, out _);
        if (row.Name == "FL") return !TryFlip(row, out _, out _);
        if (row.Name == "XBRZ") return row.Arguments.Trim() is not ("2" or "3" or "4" or "5");
        if (row.Name == "TC") return !TryTeamColor(row, out _, out _);
        if (row.Name is "RC" or "PAL") return !TryPaletteParts(row, out _, out _);
        if (row.Name is "GS" or "SEPIA" or "PLOT_ALPHA" or "WIPE_ALPHA" or "NOP" or "NO_TOD_SHIFT" or "RIGHT") return row.Arguments.Trim().Length != 0;
        return true;
    }
    public static ImageEditorNumericComponent[] ComponentShapes(string name) => name switch
    {
        "CS" => [new("Red", -255, 255, 0), new("Green", -255, 255, 1), new("Blue", -255, 255, 2)],
        "R" => [new("Red", -255, 255, 0)],
        "G" => [new("Green", -255, 255, 0)],
        "B" => [new("Blue", -255, 255, 0)],
        "CROP" => [new("X", 0, 8192, 0), new("Y", 0, 8192, 1), new("Width", 1, 8192, 2), new("Height", 1, 8192, 3)],
        "SCALE" or "SCALE_SHARP" or "SCALE_INTO" or "SCALE_INTO_SHARP" => [new("Width", 0, 8192, 0), new("Height", 0, 8192, 1)],
        "BL" => [new("Radius", 0, 64, 0)],
        "PAD" => [new("Padding", 0, 8192, 0)],
        "BLIT" => [new("X", -8192, 8192, 1), new("Y", -8192, 8192, 2)],
        "MASK" => [new("X", 0, 8192, 1), new("Y", 0, 8192, 2)],
        _ => []
    };
    public static bool TryComponent(ImageEditorModifierVm row, int component, out double value)
    {
        value = 0;
        var shapes = ComponentShapes(row.Name);
        var shape = shapes.FirstOrDefault(item => item.Component == component);
        if (shape.Label is null || SplitRawArguments(row.Arguments) is not { } values) return false;
        var optional = row.Name is "CS" or "BLIT" or "MASK";
        var maximumCount = shapes.Max(item => item.Component) + 1;
        if (values.Length > maximumCount || values.Length == 0 || !optional && values.Length != maximumCount) return false;
        if (component >= values.Length) return optional;
        if (!double.TryParse(values[component].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value)) return false;
        return value >= shape.Minimum && value <= shape.Maximum;
    }
    public static bool SetComponent(ImageEditorModifierVm row, int component, double value)
    {
        var shape = ComponentShapes(row.Name).FirstOrDefault(item => item.Component == component);
        if (shape.Label is null || !double.IsFinite(value) || value != Math.Truncate(value) || value < shape.Minimum || value > shape.Maximum ||
            !TryComponent(row, component, out var old)) return false;
        if (old == value) return true;
        var values = SplitRawArguments(row.Arguments)!.ToList();
        while (values.Count <= component) values.Add("0");
        values[component] = value.ToString("0", CultureInfo.InvariantCulture);
        row.Arguments = string.Join(",", values); return true;
    }
    public static bool TryChannels(ImageEditorModifierVm row, out string[] channels)
    {
        channels = [];
        if (row.Name != "SWAP") return false;
        var values = row.Arguments.Split(',').Select(value => value.Trim()).ToArray();
        if (values.Length is not (3 or 4) || values.Any(value => value is not ("red" or "green" or "blue" or "alpha"))) return false;
        channels = values.Length == 4 ? values : [..values, "alpha"]; return true;
    }
    public static bool SetChannel(ImageEditorModifierVm row, int component, string channel)
    {
        if (component is < 0 or > 3 || channel is not ("red" or "green" or "blue" or "alpha") || !TryChannels(row, out var current)) return false;
        if (current[component] == channel) return true;
        var values = row.Arguments.Split(',').ToList();
        while (values.Count <= component) values.Add("alpha");
        values[component] = channel; row.Arguments = string.Join(",", values); return true;
    }
    // Preserve untouched component spelling, including nested image expressions.
    private static string[]? SplitRawArguments(string arguments)
    {
        var values = new System.Collections.Generic.List<string>();
        var start = 0; var depth = 0; var brackets = 0; char quote = '\0';
        for (var index = 0; index < arguments.Length; index++)
        {
            var character = arguments[index];
            if (quote != '\0')
            {
                if (character == '\\' && index + 1 < arguments.Length) index++;
                else if (character == quote)
                { if (index + 1 < arguments.Length && arguments[index + 1] == quote) index++; else quote = '\0'; }
                continue;
            }
            if (character is '\'' or '"') quote = character;
            else if (character == '(') depth++;
            else if (character == ')' && --depth < 0) return null;
            else if (character == '[') brackets++;
            else if (character == ']' && --brackets < 0) return null;
            else if (character == ',' && depth == 0 && brackets == 0) { values.Add(arguments[start..index]); start = index + 1; }
        }
        if (quote != '\0' || depth != 0 || brackets != 0) return null;
        values.Add(arguments[start..]); return values.ToArray();
    }
    public static ImageEditorSlider? SliderShape(string name) => name switch
    {
        "O" => new("Opacity", 0, 1, 0.5, 0),
        "BLEND" => new("Overlay strength", 0, 1, 0.5, 3),
        "ROTATE" => new("Clockwise angle", -360, 360, 90, 0),
        "BW" => new("Threshold", 0, 255, 128, 0),
        "NEG" => new("Threshold", -1, 255, 0, 0),
        _ => null
    };
    public static bool TrySlider(ImageEditorModifierVm row, out ImageEditorSlider slider)
    {
        slider = SliderShape(row.Name) ?? default;
        if (SliderShape(row.Name) is null) return false;
        var values = row.Arguments.Split(',');
        if (row.Name == "BLEND" && (values.Length != 4 || !TryRgb(row, out _, out _, out _))) return false;
        if (row.Name != "BLEND" && values.Length != 1) return false;
        var raw = values[slider.Component].Trim();
        var percent = raw.EndsWith('%');
        if (percent && row.Name is not ("O" or "BLEND")) return false;
        if (!double.TryParse(percent ? raw[..^1] : raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return false;
        if (percent) value /= 100;
        if (value < slider.Minimum || value > slider.Maximum) return false;
        slider = slider with { Value = value }; return true;
    }
    public static bool SetSlider(ImageEditorModifierVm row, double value)
    {
        if (!TrySlider(row, out var shape) || !double.IsFinite(value) || value < shape.Minimum || value > shape.Maximum) return false;
        if (Math.Abs(value - shape.Value) < 0.0000001) return true;
        var values = row.Arguments.Split(',');
        values[shape.Component] = value.ToString("0.###", CultureInfo.InvariantCulture);
        row.Arguments = string.Join(",", values); return true;
    }
    public static bool TryRgb(ImageEditorModifierVm row, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        var values = row.Arguments.Split(',');
        return (row.Name == "BLEND" && values.Length == 4 || row.Name == "BG" && values.Length == 3) &&
            byte.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out red) &&
            byte.TryParse(values[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out green) &&
            byte.TryParse(values[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out blue);
    }
    public static void SetRgb(ImageEditorModifierVm row, byte red, byte green, byte blue)
    {
        if (row.Name is not ("BLEND" or "BG")) return;
        var values = row.Arguments.Split(',').ToList(); var next = new[] { red, green, blue }; var changed = false;
        while (values.Count < 3) values.Add("");
        for (var index = 0; index < 3; index++)
            if (!byte.TryParse(values[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var old) || old != next[index])
            { values[index] = next[index].ToString(CultureInfo.InvariantCulture); changed = true; }
        if (!changed) return;
        if (row.Name == "BLEND" && values.Count < 4) values.Add("50%");
        row.Arguments = string.Join(",", values);
    }
    public static bool TryFlip(ImageEditorModifierVm row, out bool horizontal, out bool vertical)
    {
        horizontal = vertical = false;
        if (row.Name != "FL") return false;
        var values = row.Arguments.Split(',').Select(value => value.Trim()).ToArray();
        if (values.Any(value => value is not ("" or "horiz" or "vert"))) return false;
        if (!row.IsEnabled) return true;
        horizontal = row.Arguments.Trim().Length == 0 || values.Contains("horiz"); vertical = values.Contains("vert"); return true;
    }
    public static bool SetFlip(ImageEditorModifierVm row, bool horizontal, bool vertical)
    {
        if (!TryFlip(row, out var oldHorizontal, out var oldVertical)) return false;
        if (horizontal == oldHorizontal && vertical == oldVertical) return true;
        if (!horizontal && !vertical) { row.IsEnabled = false; return true; }
        row.Arguments = horizontal && vertical ? "horiz,vert" : horizontal ? "horiz" : "vert";
        row.IsEnabled = true; return true;
    }
}
