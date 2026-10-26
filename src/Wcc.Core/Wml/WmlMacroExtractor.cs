namespace Wcc.Core.Wml;

public static class WmlMacroExtractor
{
    /// <summary>
    /// Extract one-line macro invocations like:
    ///   {ABILITY_SUBMERGE}
    ///   {WEAPON_SPECIAL_DRAIN}
    /// Conservative on purpose: v0.1 only.
    /// </summary>
    public static List<string> ExtractOneLineMacros(IReadOnlyList<string> lines, int startLineIndex, int endLineIndexExclusive)
    {
        var macros = new List<string>();
        int depth = 0;

        for (int i = startLineIndex; i < endLineIndexExclusive; i++)
        {
            var raw = lines[i];
            var t = raw.Trim();

            if (t.Length == 0) continue;

            // Track nested tags to only take "top-level" macro lines inside the container.
            if (IsAnyOpeningTagLine(t))
            {
                depth++;
                continue;
            }
            if (IsAnyClosingTagLine(t))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0) continue;

            // One-line macro invocation
            if (t.StartsWith('{') && t.EndsWith('}'))
                macros.Add(t);
        }

        return macros;
    }

    private static bool IsAnyOpeningTagLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']')) return false;
        var inner = trimmedLine[1..^1].Trim();
        return inner.Length > 0 && !inner.StartsWith('/');
    }

    private static bool IsAnyClosingTagLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']')) return false;
        var inner = trimmedLine[1..^1].Trim();
        return inner.StartsWith('/');
    }
}