using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    private static bool HasCapturedStoryBoundaries(WmlStaticScalarOwner owner, WmlStaticEvaluationResult evaluation) =>
        owner.OpeningOutputOffset is { } start && owner.ClosingOutputOffset is { } end && end > start &&
        owner.OpeningOrigin is { } opening && owner.ClosingOrigin is { } closing && start >= 0 &&
        start <= evaluation.Text.Length - opening.Length && end <= evaluation.Text.Length - closing.Length &&
        evaluation.Text.AsSpan(start, opening.Length).SequenceEqual(opening.Text) &&
        evaluation.Text.AsSpan(end, closing.Length).SequenceEqual(closing.Text);

    private static string? CapturedStoryFieldValue(WmlStaticScalarOwner owner, string key, WmlStaticEvaluationResult evaluation)
    {
        if (!owner.Fields.TryGetValue(key, out var field) || field.ValueText is not { } raw ||
            field.Contributions.LastOrDefault() is not { } contribution || contribution.ValueOutputOffset < 0 ||
            contribution.ValueOutputLength != raw.Length || contribution.ValueOutputOffset > evaluation.Text.Length - raw.Length ||
            !evaluation.Text.AsSpan(contribution.ValueOutputOffset, raw.Length).SequenceEqual(raw)) return null;
        return raw;
    }

    /// <summary>Bounded source-English text observation for read-only Story previews.
    /// Raw expressions stay in RawValues. No translation lookup, interpolation, formula or Lua execution.</summary>
    private static bool TryObserveStoryDisplayText(string? raw, out string? value)
    {
        value = null;
        if (raw is null || raw.Length > 128 * 1024 ||
            !WccWmlPlainTextCodec.TryReadLiteral(raw, out var text, out _)) return false;
        value = text;
        return true;
    }
}