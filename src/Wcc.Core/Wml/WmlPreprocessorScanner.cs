namespace Wcc.Core.Wml;

public static class WmlPreprocessorScanner
{
    public static bool TryGetDirectiveKind(string trimmedLine, out WmlPreprocessorDirectiveKind kind)
    {
        kind = GetDirectiveKind(trimmedLine);
        return kind != WmlPreprocessorDirectiveKind.None;
    }

    public static WmlPreprocessorDirectiveKind GetDirectiveKind(string trimmedLine)
    {
        if (string.IsNullOrWhiteSpace(trimmedLine))
            return WmlPreprocessorDirectiveKind.None;

        if (!trimmedLine.StartsWith('#'))
            return WmlPreprocessorDirectiveKind.None;

        // Wesnoth recognizes these six conditional commands. Bare #if is
        // an ordinary comment, including prose such as "#if Kaleh moves...".
        if (trimmedLine.StartsWith("#endif", StringComparison.OrdinalIgnoreCase))
            return WmlPreprocessorDirectiveKind.EndIf;

        if (trimmedLine.StartsWith("#else", StringComparison.OrdinalIgnoreCase))
            return WmlPreprocessorDirectiveKind.Else;

        if (trimmedLine.StartsWith("#ifdef", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("#ifndef", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("#ifhave", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("#ifnhave", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("#ifver", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("#ifnver", StringComparison.OrdinalIgnoreCase))
        {
            return WmlPreprocessorDirectiveKind.IfStart;
        }

        return WmlPreprocessorDirectiveKind.None;
    }

    public static IReadOnlyList<WmlPreprocessorRegionSpan> FindDirectChildRegions(WmlEditableBlock block)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));

        var regions = new List<WmlPreprocessorRegionSpan>();

        int start = block.OpeningTagLineIndex + 1;
        int endExclusive = block.ClosingTagLineIndex;

        int tagDepth = 0;
        int ppDepth = 0;
        int regionStart = -1;

        for (int i = start; i < endExclusive; i++)
        {
            var t = block.Lines[i].Trim();
            if (t.Length == 0)
                continue;

            var directiveKind = GetDirectiveKind(t);
            if (directiveKind != WmlPreprocessorDirectiveKind.None && tagDepth == 0)
            {
                switch (directiveKind)
                {
                    case WmlPreprocessorDirectiveKind.IfStart:
                        if (ppDepth == 0)
                            regionStart = i;
                        ppDepth++;
                        break;

                    case WmlPreprocessorDirectiveKind.Else:
                        break;

                    case WmlPreprocessorDirectiveKind.EndIf:
                        ppDepth = Math.Max(0, ppDepth - 1);
                        if (ppDepth == 0 && regionStart >= 0)
                        {
                            regions.Add(new WmlPreprocessorRegionSpan(regionStart, i + 1));
                            regionStart = -1;
                        }
                        break;
                }

                continue;
            }

            if (IsAnyOpeningTagLine(t))
            {
                tagDepth++;
                continue;
            }

            if (IsAnyClosingTagLine(t))
            {
                tagDepth = Math.Max(0, tagDepth - 1);
                continue;
            }
        }

        return regions;
    }

    public static WmlPreprocessorRegionSpan? FindEnclosingDirectChildRegion(WmlEditableBlock block, int lineIndex)
    {
        return FindDirectChildRegions(block)
            .FirstOrDefault(r => r.StartLineIndex <= lineIndex && lineIndex < r.EndLineIndexExclusive);
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
}
