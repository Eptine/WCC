using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class ItemPresetImporter
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "x",
        "y",
        "x,y",
        "image",
        "halo",
        "team_name",
        "visible_in_fog",
        "submerge",
        "z_order"
    };

    public static List<ItemPreset> ImportAllFromText(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var result = new List<ItemPreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in WmlAllBlockScanner.FindBlocks(text, "item"))
        {
            if (!seen.Add(block.RawText))
                continue;

            var preset = TryParseItemBlock(block);
            if (preset is not null)
                result.Add(preset);
        }

        return result;
    }

    private static ItemPreset? TryParseItemBlock(WmlBlock block)
    {
        var editable = WmlEditableBlockParser.Parse(block, KnownKeys);

        string? name = GetValue(editable, "name");
        string? image = GetValue(editable, "image");
        string? halo = GetValue(editable, "halo");

        // Require at least one primary identifying/display field.
        if (string.IsNullOrWhiteSpace(name) &&
            string.IsNullOrWhiteSpace(image) &&
            string.IsNullOrWhiteSpace(halo))
        {
            return null;
        }

        var (x, y) = ReadCoordinates(editable);

        return new ItemPreset
        {
            Name = name,
            X = x,
            Y = y,
            Image = image,
            Halo = halo,
            TeamName = GetValue(editable, "team_name"),
            VisibleInFog = GetValue(editable, "visible_in_fog"),
            Submerge = GetValue(editable, "submerge"),
            ZOrder = GetValue(editable, "z_order"),
            RawText = block.RawText
        };
    }

    private static string? GetValue(WmlEditableBlock editable, string key)
    {
        return editable.Known.TryGetValue(key, out var entry)
            ? entry.Value?.Trim()
            : null;
    }

    private static (string? X, string? Y) ReadCoordinates(WmlEditableBlock editable)
    {
        var x = GetValue(editable, "x");
        var y = GetValue(editable, "y");

        if (!string.IsNullOrWhiteSpace(x) || !string.IsNullOrWhiteSpace(y))
            return (x, y);

        var xy = GetValue(editable, "x,y");
        if (string.IsNullOrWhiteSpace(xy))
            return (null, null);

        var parts = xy.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
            return (parts[0], parts[1]);

        return (xy, null);
    }
}
