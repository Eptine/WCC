namespace Wcc.Core.Wml;

/// <summary>
/// Scans a raw WML file for top-level blocks of a specific tag name, without
/// requiring an artificial root wrapper. Nested matching tags inside other tags
/// are ignored.
/// </summary>
public static class WmlTopLevelFileBlockScanner
{
    public static IReadOnlyList<WmlBlock> FindBlocks(string text, string tagName)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (string.IsNullOrWhiteSpace(tagName)) throw new ArgumentException("Tag name must be set.", nameof(tagName));

        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var blocks = new List<WmlBlock>();

        int depth = 0;
        int currentStart = -1;
        string? currentTopLevelTag = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            if (TryReadOpeningTagName(trimmed, out var openingTag))
            {
                if (depth == 0)
                {
                    currentTopLevelTag = openingTag;
                    if (string.Equals(openingTag, tagName, StringComparison.OrdinalIgnoreCase))
                        currentStart = i;
                }

                depth++;
                continue;
            }

            if (TryReadClosingTagName(trimmed, out var closingTag))
            {
                if (depth > 0)
                    depth--;

                if (depth == 0)
                {
                    if (currentStart >= 0 &&
                        string.Equals(currentTopLevelTag, tagName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(closingTag, tagName, StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = string.Join(
                            newLine,
                            lines.Skip(currentStart).Take(i - currentStart + 1));

                        blocks.Add(new WmlBlock(tagName, raw));
                    }

                    currentStart = -1;
                    currentTopLevelTag = null;
                }
            }
        }

        return blocks;
    }

    private static bool TryReadOpeningTagName(string trimmedLine, out string tagName)
    {
        tagName = string.Empty;

        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        if (inner.Length == 0 || inner.StartsWith('/'))
            return false;

        int stop = inner.IndexOfAny(new[] { ' ', '\t' });
        tagName = (stop < 0 ? inner : inner[..stop]).Trim();
        return tagName.Length > 0;
    }

    private static bool TryReadClosingTagName(string trimmedLine, out string tagName)
    {
        tagName = string.Empty;

        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        if (!inner.StartsWith('/'))
            return false;

        inner = inner[1..].Trim();
        int stop = inner.IndexOfAny(new[] { ' ', '\t' });
        tagName = (stop < 0 ? inner : inner[..stop]).Trim();
        return tagName.Length > 0;
    }
}
