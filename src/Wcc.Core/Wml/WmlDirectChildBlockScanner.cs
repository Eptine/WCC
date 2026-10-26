namespace Wcc.Core.Wml;

/// <summary>
/// Finds direct child blocks of a container block.
/// This is safer than scanning all matching tags anywhere in the line range,
/// because nested tags inside events/story/etc. are ignored unless they are
/// direct children of the current block.
/// </summary>
public static class WmlDirectChildBlockScanner
{
    public static IReadOnlyList<WmlLineBlockSpan> FindSpans(WmlEditableBlock block, string tagName)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (string.IsNullOrWhiteSpace(tagName))
            throw new ArgumentException("Tag name must be non-empty.", nameof(tagName));

        var spans = new List<WmlLineBlockSpan>();

        int start = block.OpeningTagLineIndex + 1;
        int endExclusive = block.ClosingTagLineIndex;

        int depth = 0;
        bool collecting = false;
        int collectStart = -1;
        var stringState = new WmlStringScanState();

        for (int i = start; i < endExclusive; i++)
        {
            var raw = block.Lines[i];
            var lineStartedInsideMultilineString = stringState.InsideMultilineString;
            WmlRawStringScanner.UpdateState(raw, ref stringState);
            if (lineStartedInsideMultilineString)
                continue;

            var t = raw.Trim();
            if (t.Length == 0)
                continue;

            if (IsAnyOpeningTagLine(t))
            {
                if (depth == 0 && IsOpeningTagLine(t, tagName))
                {
                    collecting = true;
                    collectStart = i;
                }

                depth++;
                continue;
            }

            if (IsAnyClosingTagLine(t))
            {
                depth = Math.Max(0, depth - 1);

                if (collecting && depth == 0)
                {
                    spans.Add(new WmlLineBlockSpan(tagName, collectStart, i + 1));
                    collecting = false;
                    collectStart = -1;
                }

                continue;
            }
        }

        return spans;
    }

    private static bool IsOpeningTagLine(string trimmedLine, string tagName)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        if (inner.StartsWith('/'))
            return false;

        var name = ReadNameUpToWhitespace(inner);
        return string.Equals(name, tagName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnyOpeningTagLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        return inner.Length > 0 && !inner.StartsWith('/');
    }

    private static bool IsAnyClosingTagLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        return inner.StartsWith('/');
    }

    private static string ReadNameUpToWhitespace(string s)
    {
        int k = 0;
        while (k < s.Length && !char.IsWhiteSpace(s[k])) k++;
        return s[..k].Trim();
    }
}
