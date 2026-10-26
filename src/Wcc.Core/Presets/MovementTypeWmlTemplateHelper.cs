namespace Wcc.Core.Presets;

public static class MovementTypeWmlTemplateHelper
{
    public static string CreateNewMovementTypeTemplate(string movementTypeName)
    {
        return $$"""
                 [movetype]
                     name={{movementTypeName}}

                     [movement_costs]
                         flat=1
                     [/movement_costs]

                     [defense]
                         flat=50
                     [/defense]

                     [resistance]
                         blade=100
                         pierce=100
                         impact=100
                         fire=100
                         cold=100
                         arcane=100
                     [/resistance]
                 [/movetype]
                 """;
    }

    public static string ReplaceMovementTypeName(string rawText, string newName)
    {
        return rawText.ReplaceFirstOrdinal("name=", $"name={newName}", lineScoped: true);
    }

    private static string ReplaceFirstOrdinal(this string text, string key, string replacement, bool lineScoped)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith(key, StringComparison.Ordinal))
                continue;

            var indentLength = lines[i].Length - trimmed.Length;
            var indent = lines[i][..indentLength];
            lines[i] = indent + replacement;
            break;
        }

        return string.Join(newline, lines);
    }
}