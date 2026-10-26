namespace Wcc.Core.Wml;

/// <summary>
/// Extracts direct structural evidence from a single #define ... #enddef body:
/// - direct opening WML tags such as [time], [heals], [movement_costs], ...
/// - direct macro invocations, including embedded/adjacent calls such as {LONGDARK1}
///
/// This is intentionally structural only and does not expand macros.
/// </summary>
public static class WmlMacroDefinitionBodyAnalyzer
{
    public static WmlMacroDefinitionFingerprint Analyze(string rawMacroText)
    {
        if (rawMacroText is null) throw new ArgumentNullException(nameof(rawMacroText));

        var lines = rawMacroText.Replace("\r\n", "\n").Split('\n');

        var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invokedMacroNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith("#define", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#enddef", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.StartsWith('#') || trimmed.StartsWith('!'))
                continue;

            var tagName = TryReadOpeningTagName(trimmed);
            if (!string.IsNullOrWhiteSpace(tagName))
                tagNames.Add(tagName!);

        }

        foreach (var invocation in WmlMacroInvocationScanner.Scan(rawMacroText)
                     .Where(x => x.IsInsideMacroDefinition))
        {
            invokedMacroNames.Add(invocation.Name);
        }

        return new WmlMacroDefinitionFingerprint(
            tagNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            invokedMacroNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string? TryReadOpeningTagName(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return null;

        var inner = trimmedLine[1..^1].Trim();
        if (inner.Length == 0 || inner.StartsWith('/'))
            return null;

        var space = inner.IndexOfAny(new[] { ' ', '\t' });
        return (space < 0 ? inner : inner[..space]).Trim();
    }
}
