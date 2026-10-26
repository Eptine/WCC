using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public enum CampaignSourceKind { RootTag, CampaignChildTag, MacroDefinition, MacroInvocation, Include, Preprocessor, Raw }

/// <summary>Exact display/RAW-navigation slice of the original main file; never a write capability.</summary>
public sealed record CampaignSourceEntry(CampaignSourceKind Kind, string Name, int StartOffset,
    int EndOffsetExclusive, string Text, int Line)
{
    public bool IsConditional { get; init; }
    public int? ParentCampaignStartOffset { get; init; }
}

/// <summary>Ordered authored inventory. Definitions are opaque; includes and directives retain their original chronology.</summary>
public static class CampaignSourceInventory
{
    public static IReadOnlyList<CampaignSourceEntry> Build(string mainRaw)
        => BuildInventory(mainRaw, groupConditionals: true);

    /// <summary>
    /// Individual physical entries for source navigation, including references
    /// contained in a grouped Additional code block. Still no write capability.
    /// </summary>
    public static IReadOnlyList<CampaignSourceEntry> BuildForNavigation(string mainRaw)
        => BuildInventory(mainRaw, groupConditionals: false);

    private static IReadOnlyList<CampaignSourceEntry> BuildInventory(string mainRaw, bool groupConditionals)
    {
        ArgumentNullException.ThrowIfNull(mainRaw);
        var structure = WmlAuthoredStructureScanner.Scan(mainRaw);
        var analysis = WmlEditorAnalysis.Build(mainRaw);
        var tree = WmlProjectionTreeParser.Parse(structure.MaskedText);
        var entries = new List<CampaignSourceEntry>();
        var rootRanges = new List<(int Start, int End)>();
        var campaignRanges = new List<(int Start, int End, (int Start, int End)[] Children)>();
        foreach (var root in tree.Roots)
        {
            if (!TryRange(root, structure, out var start, out var end)) continue;
            var name = TagName(root, structure);
            entries.Add(Entry(CampaignSourceKind.RootTag, name, start, end, root.StartLine));
            rootRanges.Add((start, end));
            if (name != "campaign") continue;
            var children = new List<(int Start, int End)>();
            foreach (var child in root.Children)
            {
                if (!TryRange(child, structure, out var childStart, out var childEnd)) continue;
                children.Add((childStart, childEnd));
                entries.Add(Entry(CampaignSourceKind.CampaignChildTag, TagName(child, structure), childStart, childEnd, child.StartLine, start));
            }
            campaignRanges.Add((start, end, children.ToArray()));
        }

        var definitions = new List<(int Start, int End)>();
        var definitionStart = -1;
        foreach (var directive in structure.Directives)
        {
            if (directive.Name == "define" && directive.DefinitionDepth == 0) definitionStart = directive.Line;
            else if (directive.Name == "enddef" && directive.DefinitionDepth == 1 && definitionStart >= 0)
            {
                var start = structure.Lines[definitionStart].StartOffset;
                var end = structure.Lines[directive.Line].ContentEndOffset;
                var header = mainRaw[start..structure.Lines[definitionStart].ContentEndOffset].TrimStart();
                var name = (header.Length > 7 ? header[7..] : "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "#define";
                definitions.Add((start, end));
                var parent = campaignRanges.FirstOrDefault(range => start >= range.Start && end <= range.End);
                entries.Add(Entry(CampaignSourceKind.MacroDefinition, name, start, end, definitionStart,
                    parent.End > parent.Start ? parent.Start : null));
                definitionStart = -1;
            }
        }

        foreach (var macro in structure.Macros)
        {
            if (definitions.Any(range => macro.StartOffset >= range.Start && macro.EndOffsetExclusive <= range.End)) continue;
            var parent = campaignRanges.FirstOrDefault(range => macro.StartOffset >= range.Start && macro.EndOffsetExclusive <= range.End);
            if (rootRanges.Any(range => macro.StartOffset >= range.Start && macro.EndOffsetExclusive <= range.End)
                && parent.End <= parent.Start) continue;
            if (parent.Children?.Any(range => macro.StartOffset >= range.Start && macro.EndOffsetExclusive <= range.End) == true) continue;
            var firstLine = FindLine(macro.StartOffset);
            var lastLine = FindLine(macro.EndOffsetExclusive - 1);
            if (!structure.MaskedText.AsSpan(structure.Lines[firstLine].StartOffset,
                    structure.Lines[lastLine].ContentEndOffset - structure.Lines[firstLine].StartOffset).Trim().IsEmpty) continue;
            var raw = mainRaw[macro.StartOffset..macro.EndOffsetExclusive];
            if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(raw, out var syntax) || syntax is null) continue;
            var kind = syntax.Name.Contains('/') || syntax.Name.StartsWith('.') || syntax.Name.StartsWith('~')
                ? CampaignSourceKind.Include : CampaignSourceKind.MacroInvocation;
            entries.Add(Entry(kind, syntax.Name, macro.StartOffset, macro.EndOffsetExclusive, firstLine,
                parent.End > parent.Start ? parent.Start : null));
        }

        foreach (var token in analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive))
        {
            var lineIndex = FindLine(token.StartOffset);
            var line = structure.Lines[lineIndex];
            if (!definitions.Any(range => line.StartOffset >= range.Start && line.StartOffset < range.End)
                && !rootRanges.Any(range => line.StartOffset >= range.Start && line.StartOffset < range.End))
            {
                var name = mainRaw.Substring(token.StartOffset, token.Length)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (name is "#ifdef" or "#ifndef" or "#ifhave" or "#ifnhave" or "#ifver" or "#ifnver" or "#else" or "#endif"
                    or "#undef" or "#textdomain" or "#error" or "#warning" or "#deprecated")
                    entries.Add(Entry(CampaignSourceKind.Preprocessor, name, line.StartOffset, line.ContentEndOffset, lineIndex));
            }
        }

        // A balanced authored conditional is one navigation slice, including its
        // comments, branches, nested helpers and original line endings. This is
        // presentation only: Paths and captured activation use their own inventory.
        foreach (var group in groupConditionals ? ConditionalGroups(mainRaw, analysis) : [])
        {
            var firstLine = FindLine(group.StartOffset);
            var start = structure.Lines[firstLine].StartOffset;
            var end = group.EndOffsetExclusive;
            if (analysis.TagOccurrences.Any(tag => tag.StartOffset >= start && tag.StartOffset < end &&
                (!tag.IsComplete || tag.IsComputedName || tag.MatchingTagIndex is not { } partner ||
                 analysis.TagOccurrences[partner].StartOffset < start || analysis.TagOccurrences[partner].StartOffset >= end))) continue;
            var overlaps = entries.Where(entry => entry.StartOffset < end && entry.EndOffsetExclusive > start).ToArray();
            var parent = overlaps.FirstOrDefault(entry => entry.Kind == CampaignSourceKind.RootTag &&
                entry.Name == "campaign" && entry.StartOffset < start && entry.EndOffsetExclusive > end);
            // Keep the actual menu owner and difficulty/helper navigation separate,
            // and never infer a RAW span across a partially enclosed literal tag.
            if (overlaps.Any(entry => !ReferenceEquals(entry, parent) &&
                (entry.StartOffset < start || entry.EndOffsetExclusive > end ||
                 entry.Kind == CampaignSourceKind.RootTag && entry.Name is "campaign" or "+campaign" ||
                 entry.Kind == CampaignSourceKind.CampaignChildTag && entry.Name is "difficulty" or "+difficulty" ||
                 parent is not null && entry.Kind == CampaignSourceKind.MacroInvocation))) continue;
            entries.RemoveAll(entry => entry.StartOffset >= start && entry.EndOffsetExclusive <= end);
            entries.Add(Entry(CampaignSourceKind.Preprocessor, group.Label, start, end, firstLine, parent?.StartOffset));
        }
        // The remaining outer bytes stay reachable, including comments and
        // unsupported constructs. Never reconstruct them from projected WML.
        var position = 0;
        foreach (var entry in entries.Where(entry => entry.ParentCampaignStartOffset is null).OrderBy(entry => entry.StartOffset).ThenByDescending(entry => entry.EndOffsetExclusive).ToArray())
        {
            if (entry.StartOffset > position && !string.IsNullOrWhiteSpace(mainRaw[position..entry.StartOffset]))
                entries.Add(Entry(CampaignSourceKind.Raw, "RAW source", position, entry.StartOffset, FindLine(position)));
            position = Math.Max(position, entry.EndOffsetExclusive);
        }
        if (position < mainRaw.Length && !string.IsNullOrWhiteSpace(mainRaw[position..]))
            entries.Add(Entry(CampaignSourceKind.Raw, "RAW source", position, mainRaw.Length, FindLine(position)));
        return Array.AsReadOnly(entries.OrderBy(entry => entry.StartOffset).ThenBy(entry => entry.ParentCampaignStartOffset.HasValue).ToArray());

        CampaignSourceEntry Entry(CampaignSourceKind kind, string name, int start, int end, int line, int? parent = null) =>
            new(kind, name, start, end, mainRaw[start..end], line + 1)
            { IsConditional = structure.ConditionalLines[Math.Clamp(line, 0, structure.ConditionalLines.Count - 1)], ParentCampaignStartOffset = parent };
        int FindLine(int offset)
        {
            var low = 0;
            var high = structure.Lines.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (structure.Lines[middle].StartOffset <= offset) low = middle + 1;
                else high = middle;
            }
            return Math.Max(0, low - 1);
        }
    }

    // Reuse the shared lexer, rather than treating # text inside a WML string,
    // Lua payload, comment or macro argument as a control. The bounded stack is
    // deliberately stricter than a fold: malformed nested controls invalidate
    // the complete enclosing slice instead of borrowing a later closing line.
    private static IReadOnlyList<WmlEditorFold> ConditionalGroups(string text, WmlEditorAnalysis analysis)
    {
        const int maximumDepth = 128;
        const int maximumGroups = 2048;
        var regions = new List<InventoryControl>();
        var groups = new List<WmlEditorFold>();
        foreach (var token in analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive))
        {
            var raw = text.Substring(token.StartOffset, token.Length);
            var nameEnd = 1;
            while (nameEnd < raw.Length && char.IsAsciiLetter(raw[nameEnd])) nameEnd++;
            var name = raw[1..nameEnd];
            var payload = raw[nameEnd..].Trim();
            if (WmlAuthoredStructureScanner.IsConditionalStart(name) || name is "define" or "arg")
            {
                if (regions.Count >= maximumDepth) return [];
                var valid = payload.Length > 0 && !payload.StartsWith('#');
                if (name is "define" or "arg")
                {
                    var literalName = payload.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    valid &= WmlPreprocessorToken.IsLiteralName(literalName);
                    if (name == "define" && regions.Any(region => region.Kind == "define")) valid = false;
                    if (name == "arg" && !regions.Any(region => region.Kind == "define")) valid = false;
                }
                regions.Add(new(name is "define" or "arg" ? name : "conditional", token, raw.Trim(), valid));
            }
            else if (name == "else")
            {
                if (regions.Count == 0 || regions[^1].Kind != "conditional") Invalidate();
                else
                {
                    var region = regions[^1];
                    region.Valid &= !region.HasElse && EmptyOrComment(payload);
                    region.HasElse = true;
                }
            }
            else if (name is "endif" or "enddef" or "endarg")
            {
                var expected = name == "endif" ? "conditional" : name == "enddef" ? "define" : "arg";
                var match = regions.FindLastIndex(region => region.Kind == expected);
                if (match < 0) { Invalidate(); continue; }
                if (match != regions.Count - 1) Invalidate();
                var opening = regions[match];
                opening.Valid &= EmptyOrComment(payload);
                regions.RemoveRange(match, regions.Count - match);
                if (!opening.Valid) Invalidate();
                else if (expected == "conditional" && regions.Count == 0)
                {
                    if (groups.Count >= maximumGroups) return [];
                    groups.Add(new(opening.Token.StartOffset, token.StartOffset + token.Length, opening.Label));
                }
            }
        }
        return groups;

        void Invalidate() { foreach (var region in regions) region.Valid = false; }
        static bool EmptyOrComment(string payload) => payload.Length == 0 || payload.StartsWith('#');
    }

    private sealed class InventoryControl(string kind, WmlEditorToken token, string label, bool valid)
    {
        public string Kind { get; } = kind;
        public WmlEditorToken Token { get; } = token;
        public string Label { get; } = label;
        public bool Valid { get; set; } = valid;
        public bool HasElse { get; set; }
    }

    private static string TagName(WmlProjectionNode node, WmlAuthoredStructure structure) =>
        structure.MaskedText[structure.Lines[node.StartLine].StartOffset..structure.Lines[node.StartLine].ContentEndOffset].Trim()[1..^1].Trim();
    private static bool TryRange(WmlProjectionNode node, WmlAuthoredStructure structure, out int start, out int end)
    {
        start = end = 0;
        if (node.EndLineExclusive <= node.StartLine || node.EndLineExclusive > structure.Lines.Count) return false;
        var last = structure.Lines[node.EndLineExclusive - 1];
        if (!structure.MaskedText.AsSpan(last.StartOffset, last.ContentEndOffset - last.StartOffset).Trim()
                .Equals($"[/{node.TagName}]", StringComparison.OrdinalIgnoreCase)) return false;
        start = structure.Lines[node.StartLine].StartOffset; end = last.ContentEndOffset; return true;
    }
}
