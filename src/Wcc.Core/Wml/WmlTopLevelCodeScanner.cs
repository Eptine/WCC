namespace Wcc.Core.Wml;

/// <summary>
/// Collects top-level custom / preprocessor / macro lines inside a block
/// and classifies what kind of construct each one is.
/// This is intentionally classification-only for now; it does not try to
/// expand macros or understand arguments.
/// </summary>
public static class WmlTopLevelCodeScanner
{
    public static IReadOnlyList<WmlTopLevelCodeLine> Collect(WmlEditableBlock block)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));

        var result = new List<WmlTopLevelCodeLine>();

        int start = block.OpeningTagLineIndex + 1;
        int endExclusive = block.ClosingTagLineIndex;

        int tagDepth = 0;

        for (int i = start; i < endExclusive; i++)
        {
            var raw = block.Lines[i];
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
                continue;

            if (IsAnyOpeningTagLine(trimmed))
            {
                tagDepth++;
                continue;
            }

            if (IsAnyClosingTagLine(trimmed))
            {
                tagDepth = Math.Max(0, tagDepth - 1);
                continue;
            }

            if (tagDepth > 0)
                continue;

            var kind = ClassifyTrimmedLine(trimmed);
            if (kind != WmlTopLevelCodeKind.None)
                result.Add(new WmlTopLevelCodeLine(i, raw, kind));
        }

        return result;
    }

    public static WmlTopLevelCodeKind ClassifyTrimmedLine(string trimmedLine)
    {
        if (string.IsNullOrWhiteSpace(trimmedLine))
            return WmlTopLevelCodeKind.None;

        switch (WmlPreprocessorScanner.GetDirectiveKind(trimmedLine))
        {
            case WmlPreprocessorDirectiveKind.IfStart:
                return WmlTopLevelCodeKind.ConditionalIfStart;
            case WmlPreprocessorDirectiveKind.Else:
                return WmlTopLevelCodeKind.ConditionalElse;
            case WmlPreprocessorDirectiveKind.EndIf:
                return WmlTopLevelCodeKind.ConditionalEndIf;
        }

        if (trimmedLine.StartsWith("#define", StringComparison.OrdinalIgnoreCase))
            return WmlTopLevelCodeKind.MacroDefinitionStart;

        if (trimmedLine.StartsWith("#enddef", StringComparison.OrdinalIgnoreCase))
            return WmlTopLevelCodeKind.MacroDefinitionEnd;

        if (trimmedLine.StartsWith('{'))
            return WmlTopLevelCodeKind.MacroInvocation;

        if (trimmedLine.StartsWith('!'))
            return WmlTopLevelCodeKind.BangCommand;

        if (trimmedLine.StartsWith('#'))
            return WmlTopLevelCodeKind.OtherHashDirective;

        return WmlTopLevelCodeKind.None;
    }

    public static bool IsConditional(WmlTopLevelCodeKind kind)
        => kind is WmlTopLevelCodeKind.ConditionalIfStart
            or WmlTopLevelCodeKind.ConditionalElse
            or WmlTopLevelCodeKind.ConditionalEndIf;

    public static bool IsMacroDefinition(WmlTopLevelCodeKind kind)
        => kind is WmlTopLevelCodeKind.MacroDefinitionStart
            or WmlTopLevelCodeKind.MacroDefinitionEnd;

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