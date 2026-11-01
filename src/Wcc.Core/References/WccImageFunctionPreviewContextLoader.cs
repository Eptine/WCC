using System.Globalization;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.References;

/// <summary>Optional editor-preview colors from one explicit Core file; never image-resource resolution authority.</summary>
public static class WccImageFunctionPreviewContextLoader
{
    public static WccImageFunctionPreviewContext? TryLoadCore(string coreDataRoot)
    {
        try
        {
            var file = new FileInfo(Path.Combine(Path.GetFullPath(coreDataRoot), "team-colors.cfg"));
            if (!file.Exists || file.Length > 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var snapshot = new MemoryStream();
            var chunk = new byte[4096]; int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) != 0)
            {
                if (snapshot.Length + read > 1024 * 1024) return null;
                snapshot.Write(chunk, 0, read);
            }
            var bytes = snapshot.ToArray();
            var parsed = ParseCore(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            return parsed is null ? null : new()
            {
                PaletteResolver = parsed.PaletteResolver, ColorRangeResolver = parsed.ColorRangeResolver,
                TeamColorResolver = parsed.TeamColorResolver, SourcePath = file.FullName,
                SourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    public static WccImageFunctionPreviewContext? ParseCore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 1024 * 1024) return null;
        var source = new WmlStaticSource("wcc-image-preview-core-colors.cfg", text, isAuthored: false);
        var evaluation = new WmlStaticEvaluator().Evaluate(new(source, new(), IsInitialEnvironmentComplete: true,
            Limits: new(MaxOutputCharacters: 1024 * 1024, MaxSourceCharacters: 1024 * 1024)));
        if (!evaluation.IsComplete || evaluation.Diagnostics.Any(item => item.IsBlocking)) return null;
        var projection = WmlStaticScalarProjection.Create(evaluation);
        if (projection.Diagnostics.Count != 0) return null;
        var palettes = new Dictionary<string, IReadOnlyList<WccRgbaColor>>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, IReadOnlyList<WccRgbaColor>>(StringComparer.Ordinal);
        var defaults = new List<string>();
        foreach (var owner in projection.Owners.Where(owner => owner.Parent is null))
        {
            if (owner.IsAmended || owner.HasUnresolvedContributions) return null;
            if (owner.TagName == "color_palette")
                foreach (var pair in owner.Fields)
                {
                    var colors = Colors(pair.Value.ValueText);
                    if (pair.Value.Contributions.Count != 1 || colors is null || !palettes.TryAdd(pair.Key, colors)) return null;
                }
            else if (owner.TagName == "color_range")
            {
                var id = WccScenarioFieldValueCodec.DecodeLiteral(owner.GetKnownValue("id"));
                var colors = Colors(owner.GetKnownValue("rgb"));
                if (string.IsNullOrWhiteSpace(id) || colors is null || colors.Count < 3 || !ranges.TryAdd(id, colors)) return null;
                if (WccScenarioFieldValueCodec.DecodeLiteral(owner.GetKnownValue("default")) is "yes" or "true") defaults.Add(id);
            }
        }
        return palettes.Count == 0 && ranges.Count == 0 ? null : new()
        {
            PaletteResolver = name => palettes.GetValueOrDefault(name),
            ColorRangeResolver = name => ranges.GetValueOrDefault(name),
            TeamColorResolver = number => number > 0 && number <= defaults.Count ? defaults[number - 1] : null
        };
    }
    private static IReadOnlyList<WccRgbaColor>? Colors(string? raw)
    {
        if (raw is null) return null;
        var parts = WccScenarioFieldValueCodec.DecodeLiteral(raw).Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4096) return null;
        var values = new List<WccRgbaColor>();
        foreach (var part in parts)
        {
            if (part.Length != 6 || !int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
            values.Add(new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
        return Array.AsReadOnly(values.ToArray());
    }
}
