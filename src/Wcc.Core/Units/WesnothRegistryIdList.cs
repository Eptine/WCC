using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>
/// Lossless-at-rest helpers for the comma-separated identifiers used by
/// Wesnoth 1.20 abilities_list and specials_list. The parsed model preserves
/// order, duplicates, and unknown ids; source formatting is left untouched
/// until the user actually changes the list.
/// </summary>
public static class WesnothRegistryIdList
{
    public static IReadOnlyList<string> Parse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return Array.Empty<string>();

        var value = rawValue.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
    }

    public static string Format(IEnumerable<string?> ids)
    {
        if (ids is null) throw new ArgumentNullException(nameof(ids));
        return string.Join(",", ids
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    public static WmlFieldSafetyState AnalyzeField(WmlEditableBlock block, string key)
        => WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelField(block, key);

    public static void Apply(
        WmlEditableBlock block,
        IReadOnlyDictionary<string, WmlFieldSafetyState> fieldStates,
        string key,
        IEnumerable<string?> ids)
    {
        if (!WmlFieldSafetyWriteGuard.CanWrite(fieldStates, key))
            return;

        var normalized = ids
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
        if (fieldStates.TryGetValue(key, out var state) &&
            state.Kind == WmlFieldSafetyKind.PlainEditable &&
            Parse(state.PlainValue).SequenceEqual(normalized, StringComparer.Ordinal))
        {
            return;
        }

        if (normalized.Length == 0)
        {
            block.RemoveKnownValue(key);
            return;
        }

        block.SetKnownValue(key, Format(normalized));
    }

}
