namespace Wcc.Core.Wml;

/// <summary>
/// Scans a list of lines for nested blocks like:
///   [attack] ... [/attack]
/// using line indices (safe for WML because tags are usually full lines).
/// </summary>
public static class WmlLineBlockScanner
{
    public static IReadOnlyList<WmlLineBlockSpan> FindSpans(IReadOnlyList<string> lines, string tagName)
    {
        if (lines is null) throw new ArgumentNullException(nameof(lines));
        if (string.IsNullOrWhiteSpace(tagName)) throw new ArgumentException("Tag name must be non-empty.", nameof(tagName));

        var spans = new List<WmlLineBlockSpan>();

        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();

            if (!IsOpeningTagLine(t, tagName))
                continue;

            int start = i;
            int depth = 1;

            for (int j = i + 1; j < lines.Count; j++)
            {
                var tj = lines[j].Trim();

                if (IsOpeningTagLine(tj, tagName)) depth++;
                else if (IsClosingTagLine(tj, tagName)) depth--;

                if (depth == 0)
                {
                    int endExclusive = j + 1;
                    spans.Add(new WmlLineBlockSpan(tagName, start, endExclusive));
                    i = j; // continue scanning after this block
                    break;
                }
            }
        }

        return spans;
    }

    private static bool IsOpeningTagLine(string trimmedLine, string tagName)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']')) return false;
        var inner = trimmedLine[1..^1].Trim();
        if (inner.StartsWith('/')) return false;
        var name = ReadNameUpToWhitespace(inner);
        return string.Equals(name, tagName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClosingTagLine(string trimmedLine, string tagName)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']')) return false;
        var inner = trimmedLine[1..^1].Trim();
        if (!inner.StartsWith('/')) return false;
        var name = ReadNameUpToWhitespace(inner[1..].Trim());
        return string.Equals(name, tagName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadNameUpToWhitespace(string s)
    {
        int k = 0;
        while (k < s.Length && !char.IsWhiteSpace(s[k])) k++;
        return s[..k].Trim();
    }
}