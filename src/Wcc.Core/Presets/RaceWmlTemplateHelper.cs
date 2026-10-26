using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class RaceWmlTemplateHelper
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

    public static string CreateNewRaceTemplate(string raceId)
    {
        var displayName = HumanizeId(raceId);

        return $$"""
                 [race]
                     id={{raceId}}
                     male_name= _ "{{displayName}}"
                     female_name= _ "female^{{displayName}}"
                     plural_name= _ "{{displayName}}s"
                     description= _ "TODO: describe this race."
                     num_traits=2
                 [/race]
                 """;
    }

    public static string ReplaceRaceId(string rawText, string newRaceId)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return rawText;

        var block = WmlTopLevelFileBlockScanner.FindBlocks(rawText, "race").FirstOrDefault()
                    ?? WmlAllBlockScanner.FindBlocks(rawText, "race").FirstOrDefault();

        if (block is null)
            return rawText;

        var editable = WmlEditableBlockParser.Parse(block, KnownKeys);
        editable.SetKnownValue("id", newRaceId.Trim());
        return editable.ToRawText();
    }

    private static string HumanizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "Custom Race";

        var text = id.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(text) ? "Custom Race" : text;
    }
}