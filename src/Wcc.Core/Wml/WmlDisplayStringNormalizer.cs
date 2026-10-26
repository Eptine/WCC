namespace Wcc.Core.Wml;

public static class WmlDisplayStringNormalizer
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();

        if (s.StartsWith("_", StringComparison.Ordinal))
            s = s[1..].Trim();

        // Wesnoth's normalized preprocessor output serializes string scalars
        // with raw-string delimiters (for example: id=<<scenario_id>>).
        if (s.Length >= 4 &&
            s.StartsWith("<<", StringComparison.Ordinal) &&
            s.EndsWith(">>", StringComparison.Ordinal) &&
            s.IndexOf(">>", 2, StringComparison.Ordinal) == s.Length - 2)
        {
            s = s[2..^2];
        }

        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s[1..^1];

        s = s.Replace("\\\"", "\"", StringComparison.Ordinal).Trim();

        var caretIndex = s.LastIndexOf('^');
        if (caretIndex >= 0 && caretIndex + 1 < s.Length)
            s = s[(caretIndex + 1)..].Trim();

        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static string HumanizeIdentifier(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var tokens = raw
            .Trim()
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
            return raw.Trim();

        return string.Join(" ", tokens.Select(CapitalizeToken));
    }

    private static string CapitalizeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return token;

        if (token.Length == 1)
            return token.ToUpperInvariant();

        return char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
    }
}
