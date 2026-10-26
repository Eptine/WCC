namespace Wcc.Core.Wml;

/// <summary>
/// Scans raw WML text for #define ... #enddef regions.
/// This is deliberately structural only for now: it records names and raw text,
/// but does not try to interpret macro bodies or arguments.
/// </summary>
public static class WmlMacroDefinitionScanner
{
    public static IReadOnlyList<WmlMacroDefinition> Scan(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var result = new List<WmlMacroDefinition>();
        var stack = new Stack<(string Name, int StartLineIndex)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith("#define", StringComparison.OrdinalIgnoreCase))
            {
                var name = ParseMacroName(trimmed);
                if (!string.IsNullOrWhiteSpace(name))
                    stack.Push((name, i));
            }

            // Wesnoth commonly places #enddef directly after a one-line macro body,
            // for example "units/dunefolk/rider/#enddef".
            var isCommentOnlyLine = trimmed.StartsWith('#') &&
                                    !trimmed.StartsWith("#enddef", StringComparison.OrdinalIgnoreCase) &&
                                    !trimmed.StartsWith("#define", StringComparison.OrdinalIgnoreCase);
            if (!isCommentOnlyLine &&
                trimmed.Contains("#enddef", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count == 0)
                    continue;

                var def = stack.Pop();

                var raw = string.Join(
                    newLine,
                    lines.Skip(def.StartLineIndex).Take(i - def.StartLineIndex + 1));

                result.Add(new WmlMacroDefinition(
                    def.Name,
                    def.StartLineIndex,
                    i + 1,
                    raw));
            }
        }

        return result;
    }

    private static string? ParseMacroName(string trimmedDefineLine)
    {
        // Example:
        // #define IMG_PATH_TEMP
        // #define SOME_MACRO ARG1 ARG2
        var rest = trimmedDefineLine["#define".Length..].Trim();
        if (rest.Length == 0)
            return null;

        int space = rest.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? rest : rest[..space].Trim();
    }
}
