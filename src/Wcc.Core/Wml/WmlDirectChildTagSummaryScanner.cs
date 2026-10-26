namespace Wcc.Core.Wml;

/// <summary>
/// Lists distinct direct child tag names inside a single WML block.
/// Useful for structural summaries of preset-like blocks that do not have IDs.
/// </summary>
public static class WmlDirectChildTagSummaryScanner
{
    public static IReadOnlyList<string> GetDirectChildTagNames(WmlBlock block)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));

        var editable = WmlEditableBlockParser.Parse(
            block,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int start = editable.OpeningTagLineIndex + 1;
        int endExclusive = editable.ClosingTagLineIndex;

        int depth = 0;

        for (int i = start; i < endExclusive; i++)
        {
            var trimmed = editable.Lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            if (TryReadOpeningTagName(trimmed, out var tagName))
            {
                if (depth == 0)
                    result.Add(tagName);

                depth++;
                continue;
            }

            if (TryReadClosingTagName(trimmed, out _))
            {
                if (depth > 0)
                    depth--;

                continue;
            }
        }

        return result
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
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