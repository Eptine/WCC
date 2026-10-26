using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum ScenarioContentSourceKind { ChildTag, MacroInvocation }

/// <summary>
/// One complete authored fragment, with offsets into the unchanged input text.
/// This display-only span does not grant persistence or semantic authority.
/// </summary>
public sealed record ScenarioContentSourceEntry(
    ScenarioContentSourceKind Kind,
    string Name,
    int StartOffset,
    int EndOffsetExclusive,
    string Text,
    int Line)
{
    public bool IsConditional { get; init; }
}

/// <summary>
/// Lossless, tolerant inventory of literal direct scenario children and
/// standalone root macro calls. Unclassified source remains available through
/// full scenario RAW; it is never synthesized into a fragment.
/// </summary>
public static class ScenarioContentSourceInventory
{
    public static IReadOnlyList<ScenarioContentSourceEntry> Build(string scenarioRaw)
    {
        ArgumentNullException.ThrowIfNull(scenarioRaw);
        if (scenarioRaw.Length == 0) return [];
        var structure = WmlAuthoredStructureScanner.Scan(scenarioRaw);
        var tree = WmlProjectionTreeParser.Parse(structure.MaskedText);
        var scenario = tree.Roots.FirstOrDefault(node => node.TagName.Equals("scenario", StringComparison.OrdinalIgnoreCase));
        if (scenario is null || !HasClosingLine(scenario, structure)) return [];

        var entries = new List<ScenarioContentSourceEntry>();
        var childRanges = scenario.Children.Select(child => (
            Start: structure.Lines[child.StartLine].StartOffset,
            End: structure.Lines[Math.Clamp(child.EndLineExclusive - 1, 0, structure.Lines.Count - 1)].ContentEndOffset)).ToArray();
        foreach (var child in scenario.Children)
        {
            if (!HasClosingLine(child, structure)) continue;
            var first = structure.Lines[child.StartLine];
            var last = structure.Lines[child.EndLineExclusive - 1];
            var opening = structure.MaskedText.AsSpan(first.StartOffset, first.ContentEndOffset - first.StartOffset).Trim();
            var name = opening[1..^1].Trim().ToString();
            entries.Add(new ScenarioContentSourceEntry(ScenarioContentSourceKind.ChildTag, name,
                first.StartOffset, last.ContentEndOffset,
                scenarioRaw[first.StartOffset..last.ContentEndOffset], child.StartLine + 1)
            { IsConditional = structure.ConditionalLines[child.StartLine] });
        }

        var rootStart = structure.Lines[scenario.StartLine].EndOffsetExclusive;
        var rootEnd = structure.Lines[scenario.EndLineExclusive - 1].StartOffset;
        foreach (var macro in structure.Macros)
        {
            if (macro.StartOffset < rootStart || macro.EndOffsetExclusive > rootEnd ||
                childRanges.Any(range => macro.StartOffset >= range.Start && macro.StartOffset < range.End)) continue;
            var startLine = FindLine(structure.Lines, macro.StartOffset);
            var endLine = FindLine(structure.Lines, macro.EndOffsetExclusive - 1);
            var first = structure.Lines[startLine];
            var last = structure.Lines[endLine];
            if (!structure.MaskedText.AsSpan(first.StartOffset, last.ContentEndOffset - first.StartOffset).Trim().IsEmpty)
                continue; // An assignment or other literal code owns this brace expression.
            var raw = scenarioRaw[macro.StartOffset..macro.EndOffsetExclusive];
            if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(raw, out var syntax) || syntax is null) continue;
            entries.Add(new ScenarioContentSourceEntry(ScenarioContentSourceKind.MacroInvocation, syntax.Name,
                macro.StartOffset, macro.EndOffsetExclusive, raw, startLine + 1)
            { IsConditional = structure.ConditionalLines[startLine] });
        }
        return entries.OrderBy(entry => entry.StartOffset).ToArray();
    }

    private static bool HasClosingLine(WmlProjectionNode node, WmlAuthoredStructure structure)
    {
        if (node.EndLineExclusive <= node.StartLine || node.EndLineExclusive > structure.Lines.Count) return false;
        var line = structure.Lines[node.EndLineExclusive - 1];
        return structure.MaskedText.AsSpan(line.StartOffset, line.ContentEndOffset - line.StartOffset).Trim()
            .Equals($"[/{node.TagName}]", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindLine(IReadOnlyList<WmlPhysicalSourceLine> lines, int offset)
    {
        var low = 0;
        var high = lines.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (lines[middle].StartOffset <= offset) low = middle;
            else high = middle - 1;
        }
        return low;
    }
}
