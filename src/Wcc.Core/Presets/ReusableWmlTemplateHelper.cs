using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

/// <summary>
/// Minimal valid fragments used by the raw-WML Library managers. These are intentionally
/// small; structured editors can replace the authoring surface without changing storage.
/// </summary>
public static class ReusableWmlTemplateHelper
{
    public static string CreateTrait(string id)
        => $$"""
             [trait]
                 id={{id}}
                 name= _ "{{Humanize(id)}}"
                 description= _ "TODO: describe this trait."
             [/trait]
             """;

    public static string CreateTerrainType(string id)
        => $$"""
             [terrain_type]
                 id={{id}}
                 name= _ "{{Humanize(id)}}"
                 editor_name= _ "{{Humanize(id)}}"
                 string={{id}}
             [/terrain_type]
             """;

    public static string CreateItem(string name)
        => $$"""
             [item]
                 name= _ "{{Humanize(name)}}"
                 image=items/ball-blue.png
             [/item]
             """;

    public static string CreateMacro(string name)
        => $$"""
             #define {{name}}
                 # TODO: add reusable WML here.
             #enddef
             """;

    public static string CreateSchedule(string name)
        => $$"""
             #define {{name}}
                 [time]
                     id={{name.ToLowerInvariant()}}
                     name= _ "{{Humanize(name)}}"
                     image=misc/time-schedules/default/schedule-dawn.png
                     lawful_bonus=0
                 [/time]
             #enddef
             """;

    public static string ReplaceTagId(string rawText, string tagName, string newId)
    {
        var block = WmlTopLevelFileBlockScanner.FindBlocks(rawText, tagName).FirstOrDefault()
                    ?? WmlAllBlockScanner.FindBlocks(rawText, tagName).FirstOrDefault();
        if (block is null)
            return rawText;

        var editable = WmlEditableBlockParser.Parse(
            block,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" });
        editable.SetKnownValue("id", newId.Trim());
        return editable.ToRawText();
    }

    public static string ReplaceItemName(string rawText, string newName)
    {
        var block = WmlTopLevelFileBlockScanner.FindBlocks(rawText, "item").FirstOrDefault()
                    ?? WmlAllBlockScanner.FindBlocks(rawText, "item").FirstOrDefault();
        if (block is null)
            return rawText;

        var editable = WmlEditableBlockParser.Parse(
            block,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" });
        editable.SetKnownValue("name", $"_ \"{Humanize(newName)}\"");
        return editable.ToRawText();
    }

    private static string Humanize(string value)
    {
        var text = value
            .Replace('_', ' ')
            .Trim();
        return string.IsNullOrWhiteSpace(text) ? "Custom" : text;
    }
}
