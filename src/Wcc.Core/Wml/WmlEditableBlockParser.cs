namespace Wcc.Core.Wml;

public static class WmlEditableBlockParser
{
    public static WmlEditableBlock Parse(WmlBlock block, ISet<string> knownKeys)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (knownKeys is null) throw new ArgumentNullException(nameof(knownKeys));

        var newLine = block.RawText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = block.RawText.Replace("\r\n", "\n").Split('\n').ToList();

        int opening = FindFirstNonEmptyLineIndex(lines);
        int closing = FindLastNonEmptyLineIndex(lines);

        var defaultIndent =
            opening >= 0 && opening < lines.Count
                ? TakeLeadingWhitespace(lines[opening]) + "    "
                : "    ";

        var editable = new WmlEditableBlock
        {
            TagName = block.TagName,
            Lines = lines,
            OpeningTagLineIndex = opening,
            ClosingTagLineIndex = closing,
            NewLine = newLine,
            DefaultNewKeyIndent = defaultIndent
        };

        int depth = 0;
        int preprocessorDepth = 0;
        int macroDefinitionDepth = 0;
        var stringState = new WmlStringScanState();
        var seenPlainKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var lineStartedInsideMultilineString = stringState.InsideMultilineString;
            WmlRawStringScanner.UpdateState(raw, ref stringState);
            if (lineStartedInsideMultilineString)
                continue;

            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
                continue;

            var tagCode = WmlInlineCommentSplitter.Split(raw).Value.Trim();

            if (i == opening && IsOpeningTagLine(tagCode, block.TagName))
                continue;
            if (i == closing && IsClosingTagLine(tagCode, block.TagName))
                continue;

            var codeKind = WmlTopLevelCodeScanner.ClassifyTrimmedLine(trimmed);
            if (codeKind != WmlTopLevelCodeKind.None)
            {
                switch (codeKind)
                {
                    case WmlTopLevelCodeKind.ConditionalIfStart:
                        preprocessorDepth++;
                        break;

                    case WmlTopLevelCodeKind.ConditionalEndIf:
                        preprocessorDepth = Math.Max(0, preprocessorDepth - 1);
                        break;

                    case WmlTopLevelCodeKind.MacroDefinitionStart:
                        macroDefinitionDepth++;
                        break;

                    case WmlTopLevelCodeKind.MacroDefinitionEnd:
                        macroDefinitionDepth = Math.Max(0, macroDefinitionDepth - 1);
                        break;
                }

                continue;
            }

            if (IsAnyOpeningTagLine(tagCode))
            {
                depth++;
                continue;
            }

            if (IsAnyClosingTagLine(tagCode))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0 || preprocessorDepth > 0 || macroDefinitionDepth > 0)
                continue;

            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
                continue;

            int rawEq = raw.IndexOf('=');
            if (rawEq <= 0)
                continue;

            var key = trimmed[..eq].Trim();
            if (key.Length == 0)
                continue;

            if (!knownKeys.Contains(key))
                continue;

            // If the same plain key appears more than once at top level, stop treating it as safely editable.
            if (seenPlainKeys.Contains(key))
            {
                editable.Known.Remove(key);
                continue;
            }
            seenPlainKeys.Add(key);

            var leadingWs = TakeLeadingWhitespace(raw);

            int keyStart = leadingWs.Length;
            int keyEnd = keyStart + key.Length;
            var preEqWs = rawEq > keyEnd ? raw.Substring(keyEnd, rawEq - keyEnd) : "";
            var postEqWs = TakeLeadingWhitespace(raw[(rawEq + 1)..]);
            var rawValue = raw[(rawEq + 1 + postEqWs.Length)..];
            var splitValue = WmlInlineCommentSplitter.Split(rawValue);

            editable.Known[key] = new WmlKeyValueEntry(
                key, splitValue.Value,
                LineIndex: i,
                LeadingWhitespace: leadingWs,
                PreEqualsWhitespace: preEqWs,
                PostEqualsWhitespace: postEqWs,
                InlineCommentSuffix: splitValue.InlineCommentSuffix
            );
        }

        return editable;
    }

    private static string TakeLeadingWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return s.Substring(0, i);
    }

    private static int FindFirstNonEmptyLineIndex(IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
            if (!string.IsNullOrWhiteSpace(lines[i]))
                return i;
        return 0;
    }

    private static int FindLastNonEmptyLineIndex(IReadOnlyList<string> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(lines[i]))
                return i;
        return lines.Count - 1;
    }

    private static bool IsOpeningTagLine(string trimmedLine, string tagName)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        if (inner.StartsWith('/')) return false;

        var name = ReadNameUpToWhitespace(inner);
        return string.Equals(name, tagName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClosingTagLine(string trimmedLine, string tagName)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        if (!inner.StartsWith('/')) return false;

        var name = ReadNameUpToWhitespace(inner[1..].Trim());
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
        return s.Substring(0, k).Trim();
    }
}
