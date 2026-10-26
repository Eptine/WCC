namespace Wcc.Core.Wml;

/// <summary>
/// Computes insertion indices for child blocks using stable, readable rules:
/// - before the child currently at insertIndex, or
/// - after the last existing sibling of the same tag, or
/// - before the first fallback anchor tag, or
/// - before the parent's closing tag.
///
/// Preprocessor-aware behavior:
/// - if a target sibling is wrapped in a direct-child #ifdef/#else/#endif region,
///   inserting before it means before the whole region;
/// - if inserting after the last sibling and it is wrapped in such a region,
///   insert after the whole region.
/// </summary>
public static class WmlChildInsertionPlanner
{
    public static int GetInsertLineIndex(
        WmlEditableBlock parentBlock,
        string childTagName,
        int insertIndex,
        params string[] fallbackBeforeTags)
    {
        if (parentBlock is null) throw new ArgumentNullException(nameof(parentBlock));
        if (string.IsNullOrWhiteSpace(childTagName))
            throw new ArgumentException("Child tag name must be non-empty.", nameof(childTagName));
        if (insertIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        parentBlock.RefreshClosingTagLineIndex();

        var siblings = WmlDirectChildBlockScanner.FindSpans(parentBlock, childTagName);

        if (insertIndex < siblings.Count)
        {
            int lineIndex = siblings[insertIndex].StartLineIndex;
            var region = WmlPreprocessorScanner.FindEnclosingDirectChildRegion(parentBlock, lineIndex);
            return region?.StartLineIndex ?? lineIndex;
        }

        if (siblings.Count > 0)
        {
            var lastSibling = siblings[^1];
            var region = WmlPreprocessorScanner.FindEnclosingDirectChildRegion(parentBlock, lastSibling.StartLineIndex);
            return region?.EndLineIndexExclusive ?? lastSibling.EndLineIndexExclusive;
        }

        foreach (var fallbackTag in fallbackBeforeTags)
        {
            var fallbackSpans = WmlDirectChildBlockScanner.FindSpans(parentBlock, fallbackTag);
            if (fallbackSpans.Count > 0)
            {
                int lineIndex = fallbackSpans[0].StartLineIndex;
                var region = WmlPreprocessorScanner.FindEnclosingDirectChildRegion(parentBlock, lineIndex);
                return region?.StartLineIndex ?? lineIndex;
            }
        }

        return parentBlock.ClosingTagLineIndex;
    }
}
