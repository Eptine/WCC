namespace Wcc.Core.Wml;

/// <summary>
/// Identifies lines which begin inside a quoted or &lt;&lt;verbatim&gt;&gt; WML value.
/// Leading whitespace on those continuation lines is value content and must not be
/// treated as structural indentation by projections or formatters.
/// </summary>
internal static class WmlMultilineTextScanner
{
    public static IReadOnlyList<bool> FindContinuationLines(IReadOnlyList<string> lines)
    {
        if (lines is null) throw new ArgumentNullException(nameof(lines));

        var result = new bool[lines.Count];
        char? quote = null;
        var verbatim = false;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            result[lineIndex] = quote is not null || verbatim;
            ScanLine(lines[lineIndex], ref quote, ref verbatim);
        }

        return result;
    }

    private static void ScanLine(string line, ref char? quote, ref bool verbatim)
    {
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            var next = index + 1 < line.Length ? line[index + 1] : '\0';

            if (verbatim)
            {
                if (current == '>' && next == '>')
                {
                    verbatim = false;
                    index++;
                }
                continue;
            }

            if (quote is not null)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (current == quote)
                    quote = null;
                continue;
            }

            if (current == '#')
                break;
            if (current == '<' && next == '<')
            {
                verbatim = true;
                index++;
                continue;
            }
            if (current is '"' or '\'')
                quote = current;
        }
    }
}
