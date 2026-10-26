namespace Wcc.Core.Wml;

/// <summary>
/// Produces one concise block-level notice while preserving the detailed reason
/// on every field for focused raw editors and tooltips.
/// </summary>
public static class WmlFieldSafetySummaryBuilder
{
    public static string? Build(
        IReadOnlyDictionary<string, WmlFieldSafetyState>? states,
        string subject = "This block")
    {
        if (states is null || states.Count == 0)
            return null;

        var locked = states.Values.Where(x => !x.IsEditable).ToArray();
        if (locked.Length == 0)
            return null;

        var messages = new List<string>();
        var conditional = locked
            .Where(x => x.Kind == WmlFieldSafetyKind.ConditionalOrMultipleReadOnly)
            .Select(x => x.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (conditional.Length > 0)
        {
            messages.Add(
                $"{FormatCount(subject, conditional.Length, "field is", "fields are")} conditional or repeated " +
                $"({FormatKeys(conditional)}); use the focused raw-source editor for those fields.");
        }

        var macroProtected = locked
            .Where(x => x.Kind == WmlFieldSafetyKind.UnsafeTopLevelCodeReadOnly)
            .Select(x => x.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (macroProtected.Length > 0)
        {
            messages.Add(
                $"Top-level macro or preprocessor source may provide {macroProtected.Length} " +
                $"field{(macroProtected.Length == 1 ? string.Empty : "s")} ({FormatKeys(macroProtected)}). " +
                "WCC preserves that source and does not synthesize competing values.");
        }

        var other = locked
            .Where(x => x.Kind is not (WmlFieldSafetyKind.ConditionalOrMultipleReadOnly or
                WmlFieldSafetyKind.UnsafeTopLevelCodeReadOnly))
            .Select(x => x.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (other.Length > 0)
            messages.Add($"{other.Length} additional field{(other.Length == 1 ? " is" : "s are")} read-only ({FormatKeys(other)}).");

        return messages.Count == 0 ? null : string.Join(" ", messages);
    }

    private static string FormatCount(string subject, int count, string singular, string plural)
        => $"{subject}: {count} {(count == 1 ? singular : plural)}";

    private static string FormatKeys(IReadOnlyList<string> keys)
    {
        const int visibleCount = 6;
        var visible = keys.Take(visibleCount).ToArray();
        var suffix = keys.Count > visible.Length
            ? $", +{keys.Count - visible.Length} more"
            : string.Empty;
        return string.Join(", ", visible) + suffix;
    }
}
