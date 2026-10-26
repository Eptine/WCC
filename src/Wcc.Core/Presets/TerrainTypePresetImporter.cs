using System;
using System.Collections.Generic;
using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class TerrainTypePresetImporter
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "name",
        "editor_name",
        "string",
        "editor_group",
        "symbol_image",
        "editor_image",
        "default_base",
        "description"
    };

    public static List<TerrainTypePreset> ImportAllFromText(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var result = new List<TerrainTypePreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in GetCandidateBlocks(text))
        {
            var preset = TryParseTerrainTypeBlock(block);
            if (preset is null)
                continue;

            if (!seen.Add(block.RawText))
                continue;

            result.Add(preset);
        }

        return result;
    }

    private static IEnumerable<WmlBlock> GetCandidateBlocks(string text)
    {
        foreach (var block in WmlTopLevelFileBlockScanner.FindBlocks(text, "terrain_type"))
            yield return block;
    }

    private static TerrainTypePreset? TryParseTerrainTypeBlock(WmlBlock block)
    {
        var editable = WmlEditableBlockParser.Parse(block, KnownKeys);

        if (!editable.Known.TryGetValue("id", out var idEntry))
            return null;

        var id = idEntry.Value?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return null;

        string? name = editable.Known.TryGetValue("name", out var nameEntry)
            ? nameEntry.Value?.Trim()
            : null;

        string? editorName = editable.Known.TryGetValue("editor_name", out var editorNameEntry)
            ? editorNameEntry.Value?.Trim()
            : null;

        string? stringCode = editable.Known.TryGetValue("string", out var stringEntry)
            ? stringEntry.Value?.Trim()
            : null;

        string? editorGroup = editable.Known.TryGetValue("editor_group", out var editorGroupEntry)
            ? editorGroupEntry.Value?.Trim()
            : null;

        string? symbolImage = editable.Known.TryGetValue("symbol_image", out var symbolImageEntry)
            ? symbolImageEntry.Value?.Trim()
            : null;

        string? editorImage = editable.Known.TryGetValue("editor_image", out var editorImageEntry)
            ? editorImageEntry.Value?.Trim()
            : null;

        string? defaultBase = editable.Known.TryGetValue("default_base", out var defaultBaseEntry)
            ? defaultBaseEntry.Value?.Trim()
            : null;

        string? description = editable.Known.TryGetValue("description", out var descriptionEntry)
            ? descriptionEntry.Value?.Trim()
            : null;

        return new TerrainTypePreset
        {
            Id = id,
            Name = name,
            EditorName = editorName,
            StringCode = stringCode,
            EditorGroup = editorGroup,
            SymbolImage = symbolImage,
            EditorImage = editorImage,
            DefaultBase = defaultBase,
            Description = description,
            RawText = block.RawText
        };
    }
}
