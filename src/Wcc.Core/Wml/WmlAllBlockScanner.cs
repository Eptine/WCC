namespace Wcc.Core.Wml;

/// <summary>
/// Finds all blocks with the given tag name anywhere in the file, including nested ones.
/// Useful for action tags such as [item] that may appear in scenarios, events, or macros.
/// </summary>
public static class WmlAllBlockScanner
{
    public static IReadOnlyList<WmlBlock> FindBlocks(string text, string tagName)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (string.IsNullOrWhiteSpace(tagName))
            throw new ArgumentException("Tag name must be set.", nameof(tagName));

        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var result = new List<WmlBlock>();
        var stack = new Stack<(string TagName, int StartLineIndex)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            if (TryReadOpeningTagName(trimmed, out var openingTag))
            {
                stack.Push((openingTag, i));
                continue;
            }

            if (TryReadClosingTagName(trimmed, out var closingTag))
            {
                if (stack.Count == 0)
                    continue;

                var top = stack.Pop();

                if (!string.Equals(top.TagName, closingTag, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(closingTag, tagName, StringComparison.OrdinalIgnoreCase))
                {
                    var raw = string.Join(
                        newLine,
                        lines.Skip(top.StartLineIndex).Take(i - top.StartLineIndex + 1));

                    result.Add(new WmlBlock(tagName, raw));
                }
            }
        }

        return result;
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
