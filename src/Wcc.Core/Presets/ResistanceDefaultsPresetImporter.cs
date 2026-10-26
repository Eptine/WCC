using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class ResistanceDefaultsPresetImporter
{
    public static List<ResistanceDefaultsPreset> ImportAllFromText(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var result = new List<ResistanceDefaultsPreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        int ordinal = 0;

        foreach (var block in GetCandidateBlocks(text))
        {
            if (!seen.Add(block.RawText))
                continue;

            ordinal++;

            result.Add(new ResistanceDefaultsPreset
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
        foreach (var block in WmlTopLevelFileBlockScanner.FindBlocks(text, "resistance_defaults"))
            yield return block;

        foreach (var block in WmlContainerChildBlockScanner.FindDirectChildBlocks(text, "units", "resistance_defaults"))
            yield return block;
    }
}