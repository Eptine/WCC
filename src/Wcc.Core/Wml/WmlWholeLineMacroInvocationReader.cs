using System.Text;

namespace Wcc.Core.Wml;

/// <summary>
/// Shared reader for a macro invocation that owns one or more complete source
/// lines. A complete leading invocation followed by other code is rejected on
/// that same line; it must never trigger a scan through the remainder of a
/// large file.
/// </summary>
internal static class WmlWholeLineMacroInvocationReader
{
    public static bool TryRead(
        IReadOnlyList<string> lines,
        int startLineIndex,
        out string sourceInvocation,
        out WmlMacroInvocationInfo? invocation,
        out int consumedLineCount)
    {
        sourceInvocation = string.Empty;
        invocation = null;
        consumedLineCount = 0;
        if (startLineIndex < 0 || startLineIndex >= lines.Count)
            return false;

        var firstValue = WmlInlineCommentSplitter.Split(lines[startLineIndex]).Value;
        if (!firstValue.TrimStart().StartsWith('{'))
            return false;

        var source = new StringBuilder();
        var parse = new StringBuilder();
        for (var lineIndex = startLineIndex; lineIndex < lines.Count; lineIndex++)
        {
            if (source.Length > 0)
            {
                source.Append('\n');
                parse.Append('\n');
            }
            source.Append(lines[lineIndex]);
            parse.Append(WmlInlineCommentSplitter.Split(lines[lineIndex]).Value);

            var parseText = parse.ToString();
            if (!WmlMacroInvocationParser.TryParseLeadingInvocation(
                    parseText,
                    out var leading,
                    out var consumedLength))
            {
                continue;
            }

            var trimmed = parseText.Trim();
            if (consumedLength != trimmed.Length)
            {
                // This is the important bounded case: `{A} {B}`, `{IF}
                // {LUA(...)}`, or `{A} key=value` is not one whole-line
                // invocation. Appending later lines can never make it one.
                return false;
            }

            sourceInvocation = source.ToString();
            invocation = leading;
            consumedLineCount = lineIndex - startLineIndex + 1;
            return true;
        }

        return false;
    }
}
