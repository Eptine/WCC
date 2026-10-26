namespace Wcc.Core.Wml;

/// <summary>
/// Very small parser for one-line macro invocations like:
/// {ABILITY_REGENERATES}
/// {SOME_MACRO ARG1 ARG2}
///
/// This is intentionally conservative and structural only.
/// </summary>
public static class WmlMacroInvocationParser
{
    public static bool TryParseWholeInvocation(string text, out WmlMacroInvocationInfo? invocation)
    {
        invocation = null;

        if (!TryParseLeadingInvocation(text, out var leading, out var consumedLength))
            return false;

        var trimmed = text.Trim();
        if (consumedLength != trimmed.Length)
            return false;

        invocation = leading;
        return true;
    }

    public static bool TryParseLeadingInvocation(
        string text,
        out WmlMacroInvocationInfo? invocation,
        out int consumedLength)
    {
        invocation = null;
        consumedLength = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (!s.StartsWith('{'))
            return false;

        var close = FindMatchingBrace(s);
        if (close <= 1)
            return false;

        var inner = s.Substring(1, close - 1).Trim();
        if (inner.Length == 0)
            return false;

        var tokens = SplitArguments(inner);

        if (tokens.Count == 0)
            return false;

        invocation = new WmlMacroInvocationInfo(
            s[..(close + 1)],
            tokens[0],
            tokens.Skip(1).ToList());

        consumedLength = close + 1;
        return true;
    }

    private static int FindMatchingBrace(string value)
    {
        var depth = 0;
        char? quote = null;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (quote is not null)
            {
                if (current == quote && (i == 0 || value[i - 1] != '\\'))
                    quote = null;
                continue;
            }

            if (current is '\"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current == '{')
                depth++;
            else if (current == '}' && --depth == 0)
                return i;
        }

        return -1;
    }

    private static List<string> SplitArguments(string value)
    {
        var result = new List<string>();
        var tokenStart = -1;
        var braceDepth = 0;
        var parenthesisDepth = 0;
        var squareDepth = 0;
        char? quote = null;

        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (quote is not null)
            {
                if (current == quote && (i == 0 || value[i - 1] != '\\'))
                    quote = null;
                continue;
            }

            if (current is '\"' or '\'')
            {
                quote = current;
                tokenStart = tokenStart < 0 ? i : tokenStart;
                continue;
            }

            switch (current)
            {
                case '{': braceDepth++; break;
                case '}': braceDepth = Math.Max(0, braceDepth - 1); break;
                case '(': parenthesisDepth++; break;
                case ')': parenthesisDepth = Math.Max(0, parenthesisDepth - 1); break;
                case '[': squareDepth++; break;
                case ']': squareDepth = Math.Max(0, squareDepth - 1); break;
            }

            if (char.IsWhiteSpace(current) &&
                braceDepth == 0 &&
                parenthesisDepth == 0 &&
                squareDepth == 0)
            {
                if (tokenStart >= 0)
                {
                    result.Add(value[tokenStart..i]);
                    tokenStart = -1;
                }
                continue;
            }

            tokenStart = tokenStart < 0 ? i : tokenStart;
        }

        if (tokenStart >= 0)
            result.Add(value[tokenStart..]);

        return result;
    }
}
