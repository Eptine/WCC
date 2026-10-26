using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class AbilityPresetWmlTemplateHelper
{
    public static string CreateNewAbilityTemplate(string macroName)
    {
        var innerId = macroName
            .Replace("ABILITY_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

        return $$"""
                 #define {{macroName}}
                     [dummy]
                         id={{innerId}}
                         name= _ "{{innerId.Replace('_', ' ')}}"
                         description= _ "TODO: describe this ability."
                     [/dummy]
                 #enddef
                 """;
    }

    public static string ReplaceMacroName(string rawText, string newMacroName)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return rawText;

        var lines = rawText.Replace("\r\n", "\n").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("#define", StringComparison.OrdinalIgnoreCase))
                continue;

            var indentLength = lines[i].Length - trimmed.Length;
            var indent = lines[i][..indentLength];
            var rest = trimmed["#define".Length..].Trim();

            int ws = rest.IndexOfAny(new[] { ' ', '\t' });
            var suffix = ws < 0 ? string.Empty : rest[ws..];

            lines[i] = $"{indent}#define {newMacroName}{suffix}";
            break;
        }

        return string.Join(rawText.Contains("\r\n") ? "\r\n" : "\n", lines);
    }
}