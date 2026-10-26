using System;
using System.Collections.Generic;
using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class RacePresetImporter
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "name",
        "male_name",
        "female_name",
        "plural_name",
        "description",
        "num_traits",
        "ignore_global_traits"
    };

    public static List<RacePreset> ImportAllFromText(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var result = new List<RacePreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in GetCandidateBlocks(text))
        {
            var preset = TryParseRaceBlock(block);
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
        foreach (var block in WmlTopLevelFileBlockScanner.FindBlocks(text, "race"))
            yield return block;

        foreach (var block in WmlContainerChildBlockScanner.FindDirectChildBlocks(text, "units", "race"))
            yield return block;
    }

    private static RacePreset? TryParseRaceBlock(WmlBlock block)
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

        string? maleName = editable.Known.TryGetValue("male_name", out var maleEntry)
            ? maleEntry.Value?.Trim()
            : null;

        string? femaleName = editable.Known.TryGetValue("female_name", out var femaleEntry)
            ? femaleEntry.Value?.Trim()
            : null;

        string? pluralName = editable.Known.TryGetValue("plural_name", out var pluralEntry)
            ? pluralEntry.Value?.Trim()
            : null;

        string? description = editable.Known.TryGetValue("description", out var descEntry)
            ? descEntry.Value?.Trim()
            : null;

        int? numTraits = null;
        if (editable.Known.TryGetValue("num_traits", out var numTraitsEntry) &&
            int.TryParse(numTraitsEntry.Value?.Trim(), out var parsedNumTraits))
        {
            numTraits = parsedNumTraits;
        }

        string? ignoreGlobalTraits = editable.Known.TryGetValue("ignore_global_traits", out var ignoreEntry)
            ? ignoreEntry.Value?.Trim()
            : null;

        return new RacePreset
        {
            Id = id,
            Name = name,
            MaleName = maleName,
            FemaleName = femaleName,
            PluralName = pluralName,
            Description = description,
            NumTraits = numTraits,
            IgnoreGlobalTraits = ignoreGlobalTraits,
            RawText = block.RawText
        };
    }
}