namespace Wcc.Core.Wml;

/// <summary>
/// Tracks Wesnoth quoted and raw-string delimiters across physical WML lines.
/// Delimiters inside another string form or comments do not change the state.
/// </summary>
internal static class WmlRawStringScanner
{
    public static void UpdateState(
        ReadOnlySpan<char> line,
        ref WmlStringScanState state)
        => ScanLine(line, default, maskStringContent: false, ref state);

    /// <summary>
    /// Copies a physical line while replacing quoted/raw string delimiters and
    /// content, plus comments, with spaces. The result keeps the same length, so
    /// offsets still refer to the original source text.
    /// </summary>
    public static void CopyWithoutStringContent(
        ReadOnlySpan<char> line,
        Span<char> destination,
        ref WmlStringScanState state)
    {
        if (destination.Length < line.Length)
            throw new ArgumentException("The destination must fit the complete line.", nameof(destination));

        line.CopyTo(destination);
        ScanLine(line, destination, maskStringContent: true, ref state);
    }

    private static void ScanLine(
        ReadOnlySpan<char> line,
        Span<char> destination,
        bool maskStringContent,
        ref WmlStringScanState state)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (state.InsideRawString)
            {
                if (maskStringContent)
                    destination[index] = ' ';

                if (line[index] == '>' &&
                    index + 1 < line.Length &&
                    line[index + 1] == '>')
                {
                    if (maskStringContent)
                        destination[index + 1] = ' ';
                    state.InsideRawString = false;
                    index++;
                }

                continue;
            }

            var current = line[index];
            if (state.QuotedStringDelimiter is { } quote)
            {
                if (maskStringContent)
                    destination[index] = ' ';

                if (current == quote)
                {
                    // WML doubles quotes to include a literal quote. A backslash
                    // is ordinary text, including immediately before the closing
                    // quote in terrain codes and paths.
                    if (index + 1 < line.Length && line[index + 1] == quote)
                    {
                        if (maskStringContent)
                            destination[index + 1] = ' ';
                        index++;
                    }
                    else
                        state.QuotedStringDelimiter = null;
                }
                continue;
            }

            if (current == '#')
            {
                if (maskStringContent)
                    destination[index..line.Length].Fill(' ');
                return;
            }
            if (current == '"')
            {
                if (maskStringContent)
                    destination[index] = ' ';
                state.QuotedStringDelimiter = current;
                continue;
            }

            if (current == '<' &&
                index + 1 < line.Length &&
                line[index + 1] == '<')
            {
                if (maskStringContent)
                {
                    destination[index] = ' ';
                    destination[index + 1] = ' ';
                }
                state.InsideRawString = true;
                index++;
            }
        }
    }
}

internal struct WmlStringScanState
{
    public bool InsideRawString { get; set; }
    public char? QuotedStringDelimiter { get; set; }
    public readonly bool InsideQuotedString => QuotedStringDelimiter is not null;
    public readonly bool InsideMultilineString => InsideRawString || InsideQuotedString;
}
