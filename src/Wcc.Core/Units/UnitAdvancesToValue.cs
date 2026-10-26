namespace Wcc.Core.Units;

public static class UnitAdvancesToValue
{
    public static IReadOnlyList<string> ParseIds(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return Array.Empty<string>();

        return rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public static string FormatIds(IEnumerable<string> ids)
    {
        if (ids is null)
            return string.Empty;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var id in ids)
        {
            var trimmed = id?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (seen.Add(trimmed))
                ordered.Add(trimmed);
        }

        return string.Join(",", ordered);
    }
}