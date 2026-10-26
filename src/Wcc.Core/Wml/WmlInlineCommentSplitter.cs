namespace Wcc.Core.Wml;

public readonly record struct WmlValueWithInlineComment(
    string Value,
    string InlineCommentSuffix);

/// <summary>
/// Separates a WML inline comment from a key value without treating hash characters inside
/// quoted strings as comments. The suffix includes the original whitespace before '#', so a
/// later structured edit can preserve the author's comment verbatim.
/// </summary>
public static class WmlInlineCommentSplitter
{
    public static WmlValueWithInlineComment Split(string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
            return new WmlValueWithInlineComment(string.Empty, string.Empty);

        char? quote = null;
        bool escaped = false;

        for (int i = 0; i < rawValue.Length; i++)
        {
            var current = rawValue[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\' && quote is not null)
            {
                escaped = true;
                continue;
            }

            if (quote is not null)
            {
                if (current == quote)
                    quote = null;
                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current != '#')
                continue;

            var valueBeforeComment = rawValue[..i];
            var trimmedValue = valueBeforeComment.Trim();
            var trailingWhitespaceLength = valueBeforeComment.Length - valueBeforeComment.TrimEnd().Length;
            var suffixStart = i - trailingWhitespaceLength;
            return new WmlValueWithInlineComment(
                trimmedValue,
                rawValue[suffixStart..]);
        }

        return new WmlValueWithInlineComment(rawValue.Trim(), string.Empty);
    }
}
