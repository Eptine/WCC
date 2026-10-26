namespace Wcc.Core.Wml;

public static class WmlKeyValueExtractor
{
    /// <summary>
    /// Extracts simple "key=value" pairs that appear directly inside the block (depth 0).
    /// Everything else is preserved into UnknownLines: comments, macros, nested blocks, weird lines.
    ///
    /// This is deliberately conservative so that import/export won’t destroy custom WML.
    /// </summary>
    public static WmlSimpleBlock Extract(WmlBlock block, ISet<string> knownKeys)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (knownKeys is null) throw new ArgumentNullException(nameof(knownKeys));

        var result = new WmlSimpleBlock
        {
            TagName = block.TagName,
            RawText = block.RawText
        };

        var lines = block.RawText.Replace("\r\n", "\n").Split('\n');

        int first = FindFirstNonEmptyLineIndex(lines);
        int last  = FindLastNonEmptyLineIndex(lines);

        int depth = 0;

        for (int idx = 0; idx < lines.Length; idx++)
        {
            var lineRaw = lines[idx];
            var line = lineRaw.Trim();

            if (line.Length == 0)
                continue;

            // Skip ONLY the wrapper tags of THIS block (not nested tags!)
            if (idx == first && IsOpeningTagLine(line, block.TagName))
                continue;
            if (idx == last && IsClosingTagLine(line, block.TagName))
                continue;

            // Comments / preprocessor / macros: preserve as-is
            if (line.StartsWith('#') || line.StartsWith('{') || line.StartsWith('!'))
            {
                result.UnknownLines.Add(lineRaw);
                continue;
            }

            // Tag lines inside the block => nested structure, preserve and adjust depth
            if (IsAnyOpeningTagLine(line))
            {
                depth++;
                result.UnknownLines.Add(lineRaw);
                continue;
            }
            if (IsAnyClosingTagLine(line))
            {
                depth = Math.Max(0, depth - 1); // be tolerant
                result.UnknownLines.Add(lineRaw);
                continue;
            }

            // If we are inside a nested block, do NOT parse key=value (it might belong to child tags)
            if (depth > 0)
            {
                result.UnknownLines.Add(lineRaw);
                continue;
            }

            // Parse very simple key=value (top level only)
            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                result.UnknownLines.Add(lineRaw);
                continue;
            }

            var key = line[..eq].Trim();
            var value = WmlInlineCommentSplitter.Split(line[(eq + 1)..]).Value;

            if (key.Length == 0)
            {
                result.UnknownLines.Add(lineRaw);
                continue;
            }

            if (knownKeys.Contains(key))
                result.Known[key] = value;
            else
                result.UnknownLines.Add(lineRaw);
        }

        return result;
    }

    private static int FindFirstNonEmptyLineIndex(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
            if (!string.IsNullOrWhiteSpace(lines[i]))
                return i;
        return 0;
    }

    private static int FindLastNonEmptyLineIndex(string[] lines)
    {
        for (int i = lines.Length - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(lines[i]))
                return i;
        return lines.Length - 1;
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

    private static bool IsClosingTagLine(string trimmedLine, string tagName)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        if (!inner.StartsWith('/'))
            return false;

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
