using System;
using System.Collections.Generic;
using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class TraitPresetImporter
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "name",
        "male_name",
        "female_name",
        "description",
        "help_text"
    };

    private static readonly HashSet<string> ForbiddenTraitMacroTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "event",
        "object",
        "item",
        "advancement",
        "movetype",
        "time",
        "unit",
        "unit_type",
        "attack",
        "attack_anim",
        "abilities",
        "specials"
    };

    public static List<TraitPreset> ImportAllFromText(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var result = new List<TraitPreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in GetCandidateBlocks(text))
        {
            var preset = TryParseTraitBlock(block);
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
        foreach (var block in WmlTopLevelFileBlockScanner.FindBlocks(text, "trait"))
            yield return block;

        foreach (var block in WmlContainerChildBlockScanner.FindDirectChildBlocks(text, "units", "trait"))
            yield return block;

        foreach (var def in WmlMacroDefinitionScanner.Scan(text))
        {
            var fingerprint = WmlMacroDefinitionBodyAnalyzer.Analyze(def.RawText);
            if (!LooksLikePureTraitMacro(fingerprint))
                continue;

            foreach (var block in WmlTopLevelFileBlockScanner.FindBlocks(def.RawText, "trait"))
                yield return block;
        }
    }

    private static TraitPreset? TryParseTraitBlock(WmlBlock block)
    {
        var editable = WmlEditableBlockParser.Parse(block, KnownKeys);

        if (!editable.Known.TryGetValue("id", out var idEntry))
            return null;

        var id = idEntry.Value?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var displayName =
            editable.Known.TryGetValue("name", out var nameEntry) ? nameEntry.Value?.Trim() :
            editable.Known.TryGetValue("male_name", out var maleEntry) ? maleEntry.Value?.Trim() :
            editable.Known.TryGetValue("female_name", out var femaleEntry) ? femaleEntry.Value?.Trim() :
            null;

        return new TraitPreset
        {
            Id = id,
            DisplayName = displayName,
            RawText = block.RawText
        };
    }

    private static bool LooksLikePureTraitMacro(WmlMacroDefinitionFingerprint fingerprint)
    {
        if (!fingerprint.DirectTagNames.Contains("trait", StringComparer.OrdinalIgnoreCase))
            return false;

        foreach (var tag in fingerprint.DirectTagNames)
        {
            if (ForbiddenTraitMacroTags.Contains(tag))
                return false;
        }

        return true;
    }
}
