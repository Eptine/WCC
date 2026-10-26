namespace Wcc.Core.Wml;

/// <summary>
/// Utility methods for replacing line ranges inside a WmlEditableBlock while
/// keeping cached metadata (known line indices and closing tag index) consistent.
/// </summary>
public static class WmlEditableBlockLineEditor
{
    public static void ReplaceLines(
        WmlEditableBlock block,
        int start,
        int endExclusive,
        List<string> replacement)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (start < 0 || endExclusive < start || endExclusive > block.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(start));

        int removedCount = endExclusive - start;
        int insertedCount = replacement.Count;
        int delta = insertedCount - removedCount;

        block.Lines.RemoveRange(start, removedCount);
        block.Lines.InsertRange(start, replacement);

        foreach (var key in block.Known.Keys.ToList())
        {
            var entry = block.Known[key];

            // If a known key line was after the replaced region, shift it.
            if (entry.LineIndex >= endExclusive)
            {
                block.Known[key] = entry with { LineIndex = entry.LineIndex + delta };
            }
        }

        block.RefreshClosingTagLineIndex();
    }
}