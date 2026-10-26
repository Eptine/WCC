namespace Wcc.Core;

public static class WccOwnedAssetIdFactory
{
    public static string CreateCloneId(string? baseId, IEnumerable<string> existingIds)
    {
        var used = existingIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stem = NormalizeToken(baseId);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "wcc_unit";

        var candidate = $"{stem}_Copy";
        if (!used.Contains(candidate))
            return candidate;

        for (int i = 2; ; i++)
        {
            candidate = $"{stem}_Copy_{i}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    public static string CreateCfgFileName(string? preferredId, string fallbackStem)
    {
        var stem = NormalizeToken(preferredId);
        if (string.IsNullOrWhiteSpace(stem))
            stem = NormalizeToken(fallbackStem);

        if (string.IsNullOrWhiteSpace(stem))
            stem = "wcc_asset";

        return stem + ".cfg";
    }

    private static string NormalizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var chars = raw.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'
                ? ch
                : '_')
            .ToArray();

        var normalized = new string(chars);

        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);

        return normalized.Trim('_');
    }
    public static string CreateUniqueId(string? baseId, IEnumerable<string> existingIds, string fallbackStem = "wcc_asset")
    {
        var used = existingIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stem = NormalizeToken(baseId);
        if (string.IsNullOrWhiteSpace(stem))
            stem = NormalizeToken(fallbackStem);

        if (string.IsNullOrWhiteSpace(stem))
            stem = "wcc_asset";

        if (!used.Contains(stem))
            return stem;

        for (int i = 2; ; i++)
        {
            var candidate = $"{stem}_{i}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }
}