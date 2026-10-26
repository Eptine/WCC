using System.Globalization;
using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>One exact initial side and its proved separate amendments. Evidence is not write permission.</summary>
public sealed class WccScenarioSideTopologyEntry
{
    internal WccScenarioSideTopologyEntry(WmlStaticScalarOwner owner, int number, WmlStaticSourceRange? target,
        IEnumerable<WmlStaticSourceRange> amendments, bool canRemove, string diagnostic)
    {
        SideOwner = owner; Number = number; TargetRange = target;
        AmendmentRanges = Array.AsReadOnly(amendments.ToArray()); CanRemove = canRemove; Diagnostic = diagnostic;
    }
    public WmlStaticScalarOwner SideOwner { get; }
    public int Number { get; }
    public WmlStaticSourceRange? TargetRange { get; }
    public IReadOnlyList<WmlStaticSourceRange> AmendmentRanges { get; }
    public bool CanRemove { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    public bool SameOwner(WmlStaticScalarOwner owner) => WccScenarioSideTopologyPlanner.SameOwner(SideOwner, owner);
}

/// <summary>Immutable captured side list. Add/remove operations are checked again in every declared context.</summary>
public sealed class WccScenarioSideTopologySnapshot
{
    internal WccScenarioSideTopologySnapshot(WmlStaticSource source, WmlStaticScalarOwner owner,
        WmlCampaignEvaluationPlan? plan, WmlStaticSourceRange? scenarioRange,
        IEnumerable<WccScenarioSideTopologyEntry> entries, bool canAdd, string diagnostic, int insertionOffset, string indent)
    {
        Source = source; Owner = owner; Plan = plan; ScenarioRange = scenarioRange;
        Entries = Array.AsReadOnly(entries.ToArray()); CanAdd = canAdd; AddDiagnostic = diagnostic;
        InsertionOffset = insertionOffset; Indent = indent;
    }
    public WmlStaticSource Source { get; }
    public WmlStaticScalarOwner Owner { get; }
    public WmlStaticSourceRange? ScenarioRange { get; }
    public IReadOnlyList<WccScenarioSideTopologyEntry> Entries { get; }
    public bool CanAdd { get; }
    public string AddDiagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal WmlCampaignEvaluationPlan? Plan { get; }
    internal int InsertionOffset { get; }
    internal string Indent { get; }
}

/// <summary>Final append/removal intent at pristine source coordinates, never arbitrary source authority.</summary>
public sealed class WccScenarioSideTopologyEdit
{
    internal WccScenarioSideTopologyEdit(WccScenarioSideTopologySnapshot snapshot,
        IEnumerable<WccScenarioSideTopologyEntry> removals, int addedCount)
    { Snapshot = snapshot; Removals = Array.AsReadOnly(removals.ToArray()); AddedCount = addedCount;
      Additions = Array.AsReadOnly(Enumerable.Range(0, Math.Clamp(addedCount, 0, 256)).Select(_ => new WccScenarioSideAddition()).ToArray()); }
    public WccScenarioSideTopologySnapshot Snapshot { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public IReadOnlyList<WccScenarioSideTopologyEntry> Removals { get; }
    public int AddedCount { get; }
    public IReadOnlyList<WccScenarioSideAddition> Additions { get; internal init; }
    public bool AuthorizesPersistence => false;
}

/// <summary>Lossless bounded composition for literal initial sides and exact root amendment fragments.</summary>
public static class WccScenarioSideTopologyPlanner
{
    public static WccScenarioSideTopologySnapshot Read(WmlStaticSource source, WmlStaticScalarOwner scenario,
        WmlCampaignEvaluationPlan? plan, WmlStaticScalarProjection? capturedProjection = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(scenario);
        var entries = new List<WccScenarioSideTopologyEntry>();
        try
        {
            if (source.Text.Length > 4 * 1024 * 1024)
                throw new InvalidOperationException("This side source exceeds the bounded topology editor; use RAW.");
            var scan = WmlAuthoredStructureScanner.Scan(source.Text);
            // A discovery preview can supply display slots. Persistence still
            // requires the existing fresh captured-MENU/all-context proof.
            if (plan is null || plan.Contexts.Count == 0 ||
                scenario.TagName != "scenario" || scenario.IsAmended || scenario.HasUnresolvedContributions ||
                scenario.OpeningOrigin is not { } opening || scenario.ClosingOrigin is not { } closing ||
                !LiteralBoundary(opening, scenario.OpeningExpansionStack, source, "[scenario]", scan.MaskedText) ||
                !LiteralBoundary(closing, scenario.ClosingExpansionStack, source, "[/scenario]", scan.MaskedText) || closing.StartOffset <= opening.StartOffset)
                throw new InvalidOperationException("Side editing needs one exact unamended literal scenario and its complete fixed campaign contexts.");
            var scenarioRange = new WmlStaticSourceRange(source, opening.StartOffset, closing.StartOffset + closing.Length - opening.StartOffset);
            if (scan.Problem is not null || !WmlSingleBlockValidator.Validate(scenarioRange.Text, "scenario").IsValid ||
                scan.ConditionalLines[FindLine(scan, opening.StartOffset)] || !OwnTagLine(source, scan, opening) || !OwnTagLine(source, scan, closing))
                throw new InvalidOperationException("The scenario structure is incomplete or conditionally declared; use RAW for its topology.");
            var raw = ScenarioContentSourceInventory.Build(scenarioRange.Text);
            var sides = scenario.DirectChildren.Where(owner => owner.TagName == "side").ToArray();
            if (sides.Length > 4096 || raw.Count > 16384)
                throw new InvalidOperationException("This side source exceeds the bounded topology editor; use RAW.");
            var targets = new Dictionary<WmlStaticScalarOwner, WmlStaticSourceRange>();
            var amendments = sides.ToDictionary(owner => owner, _ => new List<WmlStaticSourceRange>());
            var problems = new Dictionary<WmlStaticScalarOwner, string>();
            for (var index = 0; index < sides.Length; index++)
            {
                var side = sides[index];
                var value = side.ResolveField("side");
                if (value.State != WmlStaticScalarResolutionState.ProvenAbsent &&
                    (value.State != WmlStaticScalarResolutionState.Known || !int.TryParse(WccScenarioFieldValueCodec.DecodeLiteral(value.ValueText ?? ""),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number != index + 1))
                    problems[side] = "Side numbers must match declaration order; changing side= alone cannot reorder sides.";
                if (side.OpeningOrigin is not { } start || side.ClosingOrigin is not { } end ||
                    !LiteralBoundary(start, side.OpeningExpansionStack, source, "[side]", scan.MaskedText) ||
                    !LiteralBoundary(end, side.ClosingExpansionStack, source, "[/side]", scan.MaskedText))
                { problems[side] = "This side is generated or has no exact paired local source block; use RAW."; continue; }
                var matching = raw.Where(entry => entry.Kind == ScenarioContentSourceKind.ChildTag && entry.Name == "side" &&
                    scenarioRange.StartOffset + entry.StartOffset <= start.StartOffset &&
                    scenarioRange.StartOffset + entry.EndOffsetExclusive >= end.StartOffset + end.Length &&
                    FindLine(scan, scenarioRange.StartOffset + entry.EndOffsetExclusive - 1) == FindLine(scan, end.StartOffset)).ToArray();
                if (matching.Length != 1 || matching[0].IsConditional || !OwnTagLine(source, scan, start) || !OwnTagLine(source, scan, end))
                { problems[side] = "This side is conditionally declared or has no unique local block; use RAW."; continue; }
                var targetStart = scenarioRange.StartOffset + matching[0].StartOffset;
                targets.Add(side, new(source, targetStart, end.StartOffset + end.Length - targetStart));
            }
            var evaluation = capturedProjection?.SourceEvaluation;
            var outputRoot = evaluation is not null && evaluation.IsComplete && scenario.OpeningOutputOffset is { } rootStart &&
                scenario.ClosingOutputOffset is { } rootEnd
                ? ScenarioContentSourceInventory.Build(evaluation.Text[rootStart..(rootEnd + closing.Length)])
                    .Select(entry => entry with { StartOffset = entry.StartOffset + rootStart, EndOffsetExclusive = entry.EndOffsetExclusive + rootStart }).ToArray()
                : [];
            var represented = new HashSet<WmlStaticScalarOwner>();
            foreach (var fragment in raw.Where(entry => entry.Name == "+side" || entry.Kind == ScenarioContentSourceKind.MacroInvocation))
            {
                var range = Range(fragment);
                if (evaluation is null)
                {
                    if (fragment.Name == "+side")
                    {
                        var previous = sides.LastOrDefault(side => targets.TryGetValue(side, out var target) && target.StartOffset < range.StartOffset);
                        if (previous is not null && !fragment.IsConditional) { amendments[previous].Add(range); represented.Add(previous); }
                    }
                    continue;
                }
                var spans = WccScenarioFieldStaticReviewer.ProjectRegionSpans(evaluation, range);
                bool Inside(int offset) => spans.Any(span => offset >= span.Start && offset < span.End);
                var emitted = outputRoot.Where(entry => Inside(entry.StartOffset + Leading(entry.Text))).ToArray();
                var modifiers = emitted.Where(entry => entry.Name == "+side").ToArray();
                if (modifiers.Length == 0) continue;
                var affected = modifiers.Select(modifier => sides.LastOrDefault(side => side.OpeningOutputOffset is { } offset && offset < modifier.StartOffset))
                    .Where(side => side is not null).Cast<WmlStaticScalarOwner>().Distinct().ToArray();
                var pure = !fragment.IsConditional && emitted.Length == modifiers.Length && affected.Length == 1 &&
                    spans.All(span => Enumerable.Range(span.Start, span.End - span.Start).All(offset =>
                        char.IsWhiteSpace(evaluation.Text[offset]) || modifiers.Any(modifier => offset >= modifier.StartOffset && offset < modifier.EndOffsetExclusive)));
                foreach (var side in affected)
                    if (pure) { amendments[side].Add(range); represented.Add(side); }
                    else problems[side] = "A side modifier is conditional or also emits unrelated root content; use RAW for that group.";
            }
            foreach (var side in sides)
                if (side.IsAmended && !represented.Contains(side))
                    problems[side] = "A side amendment has no complete proven local footprint; use RAW.";
            for (var index = 0; index < sides.Length; index++)
                entries.Add(new(sides[index], index + 1, targets.GetValueOrDefault(sides[index]), amendments[sides[index]],
                    !problems.ContainsKey(sides[index]) && targets.ContainsKey(sides[index]),
                    problems.GetValueOrDefault(sides[index]) ?? "Remove this exact side and its proved initial amendments. Runtime events remain unchanged."));
            var insertion = sides.Length == 0 ? opening.StartOffset + opening.Length :
                entries.Where(entry => entry.TargetRange is not null).SelectMany(entry => new[] { entry.TargetRange! }.Concat(entry.AmendmentRanges))
                    .Select(range => range.StartOffset + range.Length).DefaultIfEmpty(opening.StartOffset + opening.Length).Max();
            if (sides.Length == 0)
                insertion = scan.Lines[FindLine(scan, insertion)].EndOffsetExclusive;
            else
                insertion = scan.Lines[FindLine(scan, insertion - 1)].EndOffsetExclusive;
            var indent = source.Text[scan.Lines[FindLine(scan, opening.StartOffset)].StartOffset..opening.StartOffset] + "    ";
            var canAdd = entries.All(entry => entry.CanRemove);
            return new(source, scenario, plan, scenarioRange, entries, canAdd,
                canAdd ? "Add literal sides after the final proved initial side amendment. Save before editing their fields."
                    : "Side topology is generated, conditional, numbered inconsistently or has an unproved modifier; use RAW.", insertion, indent);

            WmlStaticSourceRange Range(ScenarioContentSourceEntry entry) =>
                entry.Kind == ScenarioContentSourceKind.MacroInvocation ?
                    new(source, scenarioRange.StartOffset + entry.StartOffset, entry.EndOffsetExclusive - entry.StartOffset) :
                    ChildRange(entry);
            WmlStaticSourceRange ChildRange(ScenarioContentSourceEntry entry)
            {
                var start = scenarioRange.StartOffset + entry.StartOffset;
                var line = scan.Lines[FindLine(scan, scenarioRange.StartOffset + entry.EndOffsetExclusive - 1)];
                var code = scan.MaskedText[line.StartOffset..line.ContentEndOffset];
                var end = line.StartOffset + code.IndexOf("[/side]", StringComparison.Ordinal) + "[/side]".Length;
                if (end < line.StartOffset + "[/side]".Length)
                    throw new InvalidOperationException("A side modifier has no separate paired literal closing line.");
                var closingRange = new WmlStaticSourceRange(source, end - "[/side]".Length, "[/side]".Length);
                if (!OwnTagLine(source, scan, closingRange))
                    throw new InvalidOperationException("A side modifier closing tag shares executable source; use RAW.");
                return new(source, start, end - start);
            }
        }
        catch (InvalidOperationException problem)
        { return new(source, scenario, plan, null, entries, false, problem.Message, 0, ""); }
    }

    public static WccScenarioSideTopologyEdit CreateEdit(WccScenarioSideTopologySnapshot snapshot,
        IEnumerable<WccScenarioSideTopologyEntry>? removals = null, int addedCount = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var removed = (removals ?? []).ToArray();
        if (addedCount is < 0 or > 256 || removed.Any(entry => !snapshot.Entries.Contains(entry) || !entry.CanRemove) ||
            removed.Distinct().Count() != removed.Length || snapshot.ScenarioRange is null ||
            addedCount > 0 && !snapshot.CanAdd ||
            !snapshot.Entries.Skip(snapshot.Entries.Count - removed.Length).SequenceEqual(removed.OrderBy(entry => entry.Number)))
            throw new InvalidOperationException("Only a proved final side suffix can be removed. Middle removal needs explicit reference and map-start remapping; use RAW.");
        return new(snapshot, removed.OrderBy(entry => entry.Number), addedCount);
    }

    public static WccScenarioSideTopologyEdit CreateEdit(WccScenarioSideTopologySnapshot snapshot,
        IEnumerable<WccScenarioSideTopologyEntry>? removals, IEnumerable<WccScenarioSideAddition> additions)
    {
        ArgumentNullException.ThrowIfNull(additions);
        var rows = additions.ToArray();
        if (rows.Any(row => row is null) || rows.Sum(row => (long)(row.EncodedSaveIdOverride?.Length ?? 0) + (row.Preset?.BodyRange.Length ?? 0)) > 2 * 1024 * 1024)
            throw new ArgumentException("Use a bounded ordered collection of new sides and literal identity overrides.", nameof(additions));
        var edit = CreateEdit(snapshot, removals, rows.Length);
        return new(snapshot, edit.Removals, rows.Length) { Additions = Array.AsReadOnly(rows) };
    }

    internal static IReadOnlyList<WccScenarioFieldSourcePatch> PlanPatches(WccScenarioSideTopologyEdit edit)
    {
        if (edit.Additions.Count != edit.AddedCount) throw new InvalidOperationException("The ordered side additions differ from their immutable count.");
        CreateEdit(edit.Snapshot, edit.Removals, edit.Additions);
        var patches = edit.Removals.SelectMany(entry => new[] { entry.TargetRange! }.Concat(entry.AmendmentRanges))
            .Select(range => new WccScenarioFieldSourcePatch(range.StartOffset, range.Length, "")).ToList();
        if (edit.AddedCount > 0)
        {
            var newline = edit.Source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : edit.Source.Text.Contains('\n') ? "\n" : "\r";
            var text = new StringBuilder();
            for (var index = 1; index <= edit.AddedCount; index++)
            {
                text.Append(edit.Snapshot.Indent).Append("[side]").Append(newline).Append(edit.Snapshot.Indent)
                    .Append("    side=").Append((edit.Snapshot.Entries.Count - edit.Removals.Count + index).ToString(CultureInfo.InvariantCulture))
                    .Append(newline);
                var addition = edit.Additions[index - 1];
                if (addition.Preset is { } preset)
                    text.Append(edit.Snapshot.Indent).Append("    {").Append(preset.Name).Append('}').Append(newline);
                if (addition.EncodedSaveIdOverride is { } saveId)
                    text.Append(edit.Snapshot.Indent).Append("    save_id=").Append(saveId).Append(newline);
                text.Append(edit.Snapshot.Indent).Append("[/side]").Append(newline);
            }
            patches.Add(new(edit.Snapshot.InsertionOffset, 0, text.ToString()));
        }
        return Array.AsReadOnly(patches.ToArray());
    }

    internal static bool SameLayout(WccScenarioSideTopologySnapshot left, WccScenarioSideTopologySnapshot right) =>
        left.ScenarioRange is not null && right.ScenarioRange is not null && left.CanAdd == right.CanAdd &&
        left.InsertionOffset == right.InsertionOffset && left.Entries.Count == right.Entries.Count &&
        left.Entries.Zip(right.Entries).All(pair => pair.First.Number == pair.Second.Number && pair.First.CanRemove == pair.Second.CanRemove &&
            SameRange(pair.First.TargetRange, pair.Second.TargetRange) && pair.First.AmendmentRanges.Count == pair.Second.AmendmentRanges.Count &&
            pair.First.AmendmentRanges.Zip(pair.Second.AmendmentRanges).All(ranges => SameRange(ranges.First, ranges.Second)));

    internal static bool SameOwner(WmlStaticScalarOwner left, WmlStaticScalarOwner right)
    {
        for (WmlStaticScalarOwner? a = left, b = right; a is not null || b is not null; a = a?.Parent, b = b?.Parent)
            if (a is null || b is null || a.TagName != b.TagName || a.IsAmended != b.IsAmended ||
                !SameRange(a.OpeningOrigin, b.OpeningOrigin) || !SameRange(a.ClosingOrigin, b.ClosingOrigin) ||
                !SameFrames(a.OpeningExpansionStack, b.OpeningExpansionStack) || !SameFrames(a.ClosingExpansionStack, b.ClosingExpansionStack)) return false;
        return true;
    }

    private static bool SameFrames(IReadOnlyList<WmlStaticExpansionFrame> left, IReadOnlyList<WmlStaticExpansionFrame> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.MacroName == pair.Second.MacroName &&
            SameRange(pair.First.Definition, pair.Second.Definition) && pair.First.InvocationOrigins.Count == pair.Second.InvocationOrigins.Count &&
            pair.First.InvocationOrigins.Zip(pair.Second.InvocationOrigins).All(ranges => SameRange(ranges.First, ranges.Second)) &&
            SameParameters(pair.First.RequiredParameters, pair.Second.RequiredParameters) && SameParameters(pair.First.OptionalParameters, pair.Second.OptionalParameters));
    private static bool SameParameters(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    private static bool SameRange(WmlStaticSourceRange? left, WmlStaticSourceRange? right) =>
        left is null ? right is null : right is not null && WccScenarioFieldRouteAnalyzer.SameSource(left.Source, right.Source) &&
            left.StartOffset == right.StartOffset && left.Length == right.Length;
    private static bool LiteralBoundary(WmlStaticSourceRange range, IReadOnlyList<WmlStaticExpansionFrame> frames,
        WmlStaticSource source, string expected, string maskedText) => range.Text == expected && WccScenarioFieldRouteAnalyzer.SameSource(range.Source, source) &&
            frames.All(frame => !WccScenarioFieldRouteAnalyzer.SameSource(frame.Definition.Source, source) ||
                range.StartOffset < frame.Definition.StartOffset || range.StartOffset + range.Length > frame.Definition.StartOffset + frame.Definition.Length) &&
            maskedText.Substring(range.StartOffset, range.Length) == expected;
    private static int Leading(string text) => text.Length - text.TrimStart().Length;
    private static int FindLine(WmlAuthoredStructure scan, int offset)
    {
        var low = 0; var high = scan.Lines.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (scan.Lines[middle].StartOffset <= offset) low = middle;
            else high = middle - 1;
        }
        return low;
    }
    private static bool OwnTagLine(WmlStaticSource source, WmlAuthoredStructure scan, WmlStaticSourceRange tag)
    {
        var line = scan.Lines[FindLine(scan, tag.StartOffset)];
        return source.Text.AsSpan(line.StartOffset, tag.StartOffset - line.StartOffset).Trim().IsEmpty &&
            WmlInlineCommentSplitter.Split(source.Text[(tag.StartOffset + tag.Length)..line.ContentEndOffset]).Value.Trim().Length == 0;
    }
}
