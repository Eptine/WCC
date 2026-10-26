namespace Wcc.Core.Wml;

public static class WmlFieldSafetyRawEditor
{
    public static bool CanOpenRawEditor(WmlFieldSafetyState? state)
        => state?.HasRawEditor == true;

    public static string? GetRawText(WmlEditableBlock block, WmlFieldSafetyState? state)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (state?.RawEditRegion is null)
            return null;

        var region = state.RawEditRegion;

        return string.Join(
            block.NewLine,
            block.Lines
                .Skip(region.StartLineIndex)
                .Take(region.EndLineIndexExclusive - region.StartLineIndex));
    }

    public static void ApplyEditedRawText(WmlEditableBlock block, WmlFieldSafetyState state, string newText)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (state.RawEditRegion is null)
            throw new InvalidOperationException("This field has no raw edit region.");

        var region = state.RawEditRegion;

        var normalized = (newText ?? string.Empty).Replace("\r\n", "\n");
        var replacement = normalized.Length == 0
            ? new List<string>()
            : normalized.Split('\n').ToList();

        WmlEditableBlockLineEditor.ReplaceLines(
            block,
            region.StartLineIndex,
            region.EndLineIndexExclusive,
            replacement);

        region.EndLineIndexExclusive = region.StartLineIndex + replacement.Count;
        state.RawCodePreview = newText;
    }
}
