using System.Text;

namespace Wcc.Core.Wml;

/// <summary>
/// Very small, tolerant block scanner for WML-like tags:
///   [tag] ... [/tag]
/// This is NOT a full parser. It's a bootstrap component for v0.1.
/// </summary>
public static class WmlBlockScanner
{
    public static IReadOnlyList<WmlBlockSpan> FindTopLevelBlockSpans(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var blocks = new List<WmlBlockSpan>();
        var structuralText = CreateStructuralText(text);

        int i = 0;
        while (i < text.Length)
        {
            int openBracket = structuralText.IndexOf('[', i);
            if (openBracket < 0) break;

            int closeBracket = structuralText.IndexOf(']', openBracket + 1);
            if (closeBracket < 0) break;

            var tagInside = structuralText.Substring(openBracket + 1, closeBracket - openBracket - 1).Trim();
            if (tagInside.Length == 0 || tagInside.StartsWith('/'))
            {
                i = closeBracket + 1;
                continue;
            }

            var tagName = ReadNameUpToWhitespace(tagInside);
            if (tagName.Length == 0)
            {
                i = closeBracket + 1;
                continue;
            }

            int depth = 1;
            int j = closeBracket + 1;

            while (j < text.Length && depth > 0)
            {
                int nextOpen = structuralText.IndexOf('[', j);
                if (nextOpen < 0) break;

                int nextClose = structuralText.IndexOf(']', nextOpen + 1);
                if (nextClose < 0) break;

                var inner = structuralText.Substring(nextOpen + 1, nextClose - nextOpen - 1).Trim();

                if (IsOpeningOf(inner, tagName)) depth++;
                else if (IsClosingOf(inner, tagName)) depth--;

                j = nextClose + 1;

                if (depth == 0)
                {
                    var raw = text.Substring(openBracket, j - openBracket);
                    blocks.Add(new WmlBlockSpan(tagName, raw, openBracket, j));
                    i = j;
                    break;
                }
            }

            if (depth != 0)
                i = closeBracket + 1;
        }

        return blocks;
    }
    
    public static IReadOnlyList<WmlBlock> FindTopLevelBlocks(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var blocks = new List<WmlBlock>();
        var structuralText = CreateStructuralText(text);

        int i = 0;
        while (i < text.Length)
        {
            // Find next opening bracket
            int openBracket = structuralText.IndexOf('[', i);
            if (openBracket < 0) break;

            // Try to read tag name: [tag_name]
            int closeBracket = structuralText.IndexOf(']', openBracket + 1);
            if (closeBracket < 0) break;

            var tagInside = structuralText.Substring(openBracket + 1, closeBracket - openBracket - 1).Trim();

            // Skip closing tags or empty tags
            if (tagInside.Length == 0 || tagInside.StartsWith('/'))
            {
                i = closeBracket + 1;
                continue;
            }

            // Tag name ends at first whitespace (WML can have attributes; we ignore for now)
            var tagName = ReadNameUpToWhitespace(tagInside);
            if (tagName.Length == 0)
            {
                i = closeBracket + 1;
                continue;
            }

            // Find matching closing tag at the same nesting level
            // We'll scan forward, tracking nested same-style tags.
            var closing = "[/" + tagName + "]";
            int searchFrom = closeBracket + 1;

            int depth = 1;
            int j = searchFrom;

            while (j < text.Length && depth > 0)
            {
                int nextOpen = structuralText.IndexOf('[', j);
                if (nextOpen < 0) break;

                int nextClose = structuralText.IndexOf(']', nextOpen + 1);
                if (nextClose < 0) break;

                var inner = structuralText.Substring(nextOpen + 1, nextClose - nextOpen - 1).Trim();

                // Opening of same tag increases depth; closing decreases depth
                if (IsOpeningOf(inner, tagName)) depth++;
                else if (IsClosingOf(inner, tagName)) depth--;

                j = nextClose + 1;

                if (depth == 0)
                {
                    // Include everything from the opening '[' to the end of closing ']'
                    var raw = text.Substring(openBracket, j - openBracket);
                    blocks.Add(new WmlBlock(tagName, raw));
                    i = j;
                    break;
                }
            }

            // If we didn't find a close tag, move past this opener to avoid infinite loops
            if (depth != 0)
                i = closeBracket + 1;
        }

        return blocks;
    }

    private static string CreateStructuralText(string text)
    {
        var structural = text.ToCharArray();
        var stringState = new WmlStringScanState();
        for (var lineStart = 0; lineStart < text.Length;)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? text.Length : newline;
            var length = lineEnd - lineStart;
            WmlRawStringScanner.CopyWithoutStringContent(
                text.AsSpan(lineStart, length),
                structural.AsSpan(lineStart, length),
                ref stringState);

            if (newline < 0)
                break;
            lineStart = newline + 1;
        }

        return new string(structural);
    }

    private static string ReadNameUpToWhitespace(string s)
    {
        int k = 0;
        while (k < s.Length && !char.IsWhiteSpace(s[k])) k++;
        return s.Substring(0, k).Trim();
    }

    private static bool IsOpeningOf(string inner, string tagName)
    {
        // Example: "unit_type" or "unit_type something=1"
        if (inner.StartsWith('/')) return false;
        var name = ReadNameUpToWhitespace(inner);
        return string.Equals(name, tagName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClosingOf(string inner, string tagName)
    {
        // Example: "/unit_type"
        if (!inner.StartsWith('/')) return false;
        var name = inner.Substring(1).Trim();
        // ignore anything after whitespace just in case
        name = ReadNameUpToWhitespace(name);
        return string.Equals(name, tagName, StringComparison.OrdinalIgnoreCase);
    }
}
