using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class TerrainDefaultsPresetImporter
{
    public static List<TerrainDefaultsPreset> ImportAllFromText(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var result = new List<TerrainDefaultsPreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        int ordinal = 0;

        foreach (var block in GetCandidateBlocks(text))
        {
            if (!seen.Add(block.RawText))
                continue;

            ordinal++;

            result.Add(new TerrainDefaultsPreset
            {
                OrdinalInFile = ordinal,
                DirectChildTagNames = WmlDirectChildTagSummaryScanner.GetDirectChildTagNames(block),
                RawText = block.RawText
            });
        }

        return result;
    }

    private static IEnumerable<WmlBlock> GetCandidateBlocks(string text)
    {
        foreach (var block in WmlTopLevelFileBlockScanner.FindBlocks(text, "terrain_defaults"))
            yield return block;

        foreach (var block in WmlContainerChildBlockScanner.FindDirectChildBlocks(text, "units", "terrain_defaults"))
            yield return block;
    }
}