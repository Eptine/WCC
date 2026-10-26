using System;
using System.Collections.Generic;
using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class MovementTypePresetImporter
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name"
    };

    public static List<MovementTypePreset> ImportAllFromText(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var result = new List<MovementTypePreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in GetCandidateBlocks(text))
        {
            var editable = WmlEditableBlockParser.Parse(block, KnownKeys);
            if (!editable.Known.TryGetValue("name", out var nameEntry))
                continue;

            var name = nameEntry.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!seen.Add(block.RawText))
                continue;

            result.Add(new MovementTypePreset
            {
                Name = name,
                RawText = block.RawText
            });
        }

        return result;
    }

    private static IEnumerable<WmlBlock> GetCandidateBlocks(string text)
    {
        foreach (var block in WmlTopLevelFileBlockScanner.FindBlocks(text, "movetype"))
            yield return block;

        foreach (var block in WmlContainerChildBlockScanner.FindDirectChildBlocks(text, "units", "movetype"))
            yield return block;
    }
}