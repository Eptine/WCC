using System.Collections.ObjectModel;
using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Explicit new-leader literals. Defaults and inherited display values are not authored automatically.</summary>
public sealed class WccScenarioLeaderAddition
{
    public WccScenarioLeaderAddition(IEnumerable<KeyValuePair<string, string>> values,
        IEnumerable<KeyValuePair<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>>? difficultyValues = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var captured = values.ToArray();
        var independent = (difficultyValues ?? []).ToArray();
        if (captured.Length + independent.Length > 256 || captured.Any(pair => pair.Key is null || pair.Value is null) ||
            independent.Any(pair => pair.Key is null || pair.Value is null || pair.Value.Count is < 1 or > 16 ||
                pair.Value.Any(slot => slot is null || slot.ValueText is null)) ||
            captured.Select(pair => pair.Key).Concat(independent.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal).Count() != captured.Length + independent.Length)
            throw new ArgumentException("New leader fields must have unique supported keys and complete literal values.", nameof(values));
        var literals = new Dictionary<string, string>(StringComparer.Ordinal);
        var encoded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in captured)
        {
            literals.Add(pair.Key, pair.Value);
            encoded.Add(pair.Key, Encode(pair.Key, pair.Value));
        }
        Values = new ReadOnlyDictionary<string, string>(literals);
        EncodedValues = new ReadOnlyDictionary<string, string>(encoded);
        var slots = new Dictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(StringComparer.Ordinal);
        var encodedSlots = new Dictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(StringComparer.Ordinal);
        foreach (var pair in independent)
        {
            slots.Add(pair.Key, Array.AsReadOnly(pair.Value.ToArray()));
            encodedSlots.Add(pair.Key, Array.AsReadOnly(pair.Value.Select(slot => slot with { ValueText = Encode(pair.Key, slot.ValueText!) }).ToArray()));
        }
        DifficultyValues = new ReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(slots);
        EncodedDifficultyValues = new ReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(encodedSlots);

        static string Encode(string key, string text)
        {
            if (!WccScenarioFieldValueCodec.TryGetDefinition("leader", key, out var field))
                throw new ArgumentException("This new leader field is not supported for initial authoring: " + key);
            if (text.Length > 128 * 1024 || WccScenarioFieldValueCodec.InferInputKind(text) != WccScenarioFieldValueInputKind.Literal)
                throw new ArgumentException("New leader fields currently support literals; macro and runtime expressions need RAW after Save.");
            if (WccScenarioFieldValueCodec.ValidateInput(field, text) is { } problem) throw new ArgumentException(problem);
            return WccScenarioFieldValueCodec.EncodeLiteral(field, text);
        }
    }
    public IReadOnlyDictionary<string, string> Values { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> DifficultyValues { get; }
    public WccCampaignLocalPresetEntry? Preset { get; private init; }
    public static WccScenarioLeaderAddition FromPreset(WccCampaignLocalPresetEntry preset,
        IEnumerable<KeyValuePair<string, string>>? values = null,
        IEnumerable<KeyValuePair<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>>? difficultyValues = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!preset.IsSupported || preset.Role != WccCampaignLocalPresetRole.Leader || preset.Payload is null)
            throw new ArgumentException("Select one supported saved campaign-local Leader preset.", nameof(preset));
        var addition = new WccScenarioLeaderAddition(values ?? [], difficultyValues) { Preset = preset };
        if (addition.DifficultyValues.Keys.Any(key => key is "id" or "side" or "save_id"))
            throw new ArgumentException("A preset instance identity override must stay shared.");
        if (addition.Values.TryGetValue("id", out var id) && id.Length == 0)
            throw new ArgumentException("Use a deliberate nonempty local id override, or keep the preset identity.");
        return addition;
    }
    public bool AuthorizesPersistence => false;
    internal IReadOnlyDictionary<string, string> EncodedValues { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> EncodedDifficultyValues { get; }
    internal int FieldCount => Values.Count + DifficultyValues.Count;
    internal long EncodedSize => (Preset?.BodyRange.Length ?? 0L) + EncodedValues.Sum(pair => (long)pair.Key.Length + pair.Value.Length) +
        EncodedDifficultyValues.Sum(pair => pair.Value.Sum(slot => (long)pair.Key.Length + slot.ValueText!.Length));
    internal IEnumerable<KeyValuePair<string, string>> ValuesIn(WmlEvaluationContext context) => Values.Concat(
        DifficultyValues.Select(pair => new KeyValuePair<string, string>(pair.Key,
            pair.Value.Single(slot => slot.ContextSemanticKey == context.SemanticKey).ValueText!)));
}

/// <summary>Exact literal-side evidence for new nested leaders, never permission to mutate its baseline.</summary>
public sealed class WccScenarioLeaderAdditionSnapshot
{
    internal WccScenarioLeaderAdditionSnapshot(WmlStaticSource source, WmlStaticScalarOwner side,
        WmlCampaignEvaluationPlan? plan, WmlStaticSourceRange? sideRange, bool canAdd,
        string diagnostic, int insertion, string indent)
    {
        Source = source; SideOwner = side; Plan = plan; SideRange = sideRange;
        CanAdd = canAdd; Diagnostic = diagnostic; InsertionOffset = insertion; Indent = indent;
        ExistingLeaders = Array.AsReadOnly(side.DirectChildren.Where(child => child.TagName == "leader").ToArray());
    }
    public WmlStaticSource Source { get; }
    public WmlStaticScalarOwner SideOwner { get; }
    public WmlStaticSourceRange? SideRange { get; }
    public bool CanAdd { get; }
    public string Diagnostic { get; }
    public int ExistingLeaderCount => ExistingLeaders.Count;
    public bool AuthorizesPersistence => false;
    public bool SameOwner(WmlStaticScalarOwner owner) => WccScenarioSideTopologyPlanner.SameOwner(SideOwner, owner);
    internal WmlCampaignEvaluationPlan? Plan { get; }
    internal IReadOnlyList<WmlStaticScalarOwner> ExistingLeaders { get; }
    internal int InsertionOffset { get; }
    internal string Indent { get; }
}

/// <summary>Immutable new nested leader payloads at one captured side occurrence.</summary>
public sealed class WccScenarioLeaderAdditionEdit
{
    internal WccScenarioLeaderAdditionEdit(WccScenarioLeaderAdditionSnapshot snapshot,
        IEnumerable<WccScenarioLeaderAddition> leaders)
    { Snapshot = snapshot; Leaders = Array.AsReadOnly(leaders.ToArray()); }
    public WccScenarioLeaderAdditionSnapshot Snapshot { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public IReadOnlyList<WccScenarioLeaderAddition> Leaders { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>Bounded literal leader insertion using the existing exact side source inventory.</summary>
public static class WccScenarioLeaderAdditionPlanner
{
    public static WccScenarioLeaderAdditionSnapshot Read(WmlStaticSource source, WmlStaticScalarOwner side,
        WmlCampaignEvaluationPlan? plan, WmlStaticScalarProjection? capturedProjection = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(side);
        if (side.TagName != "side" || side.IsAmended || side.HasUnresolvedContributions || side.Parent is not { TagName: "scenario" } scenario)
            return Refused("Adding leaders needs an exact unamended initial side; generated, conditional or amended sides use RAW.");
        var topology = WccScenarioSideTopologyPlanner.Read(source, scenario, plan, capturedProjection);
        var matches = topology.Entries.Where(entry => entry.SameOwner(side)).Take(2).ToArray();
        var target = matches.Length == 1 && matches[0].CanRemove ? matches[0].TargetRange : null;
        // Inserting inside a complete local side does not remove or reorder its
        // amended ancestor. Keep destructive topology proof separate from this
        // bounded insertion, also used by initial unit placement.
        if (target is null && scenario.IsAmended)
            target = ReadAmendedScenarioSideRange(source, scenario, side, plan, capturedProjection);
        if (target is null || side.ClosingOrigin is not { } closing)
            return Refused(matches.FirstOrDefault()?.Diagnostic ?? topology.AddDiagnostic);
        var lineStart = closing.StartOffset;
        while (lineStart > 0 && source.Text[lineStart - 1] is not ('\r' or '\n')) lineStart--;
        var indent = source.Text[lineStart..closing.StartOffset] + "    ";
        return new(source, side, plan, target, true,
            "New nested leaders and their explicit fields join the same checked Save; existing leaders remain unchanged.", lineStart, indent);

        WccScenarioLeaderAdditionSnapshot Refused(string problem) => new(source, side, plan, null, false, problem, 0, "");
    }

    private static WmlStaticSourceRange? ReadAmendedScenarioSideRange(WmlStaticSource source,
        WmlStaticScalarOwner scenario, WmlStaticScalarOwner side, WmlCampaignEvaluationPlan? plan,
        WmlStaticScalarProjection? projection)
    {
        if (source.Text.Length > 4 * 1024 * 1024 || plan is not { IsContextDiscoveryComplete: true } || plan.Contexts.Count == 0 ||
            scenario.HasUnresolvedContributions || projection?.SourceEvaluation is not { IsComplete: true } evaluation ||
            evaluation.Diagnostics.Any(item => item.IsBlocking) || projection.Diagnostics.Any(item => item.IsBlocking) ||
            projection.Owners.Count(owner => ReferenceEquals(owner, scenario)) != 1 ||
            scenario.DirectChildren.Count(owner => ReferenceEquals(owner, side)) != 1 ||
            scenario.OpeningOrigin is not { } scenarioOpening || side.OpeningOrigin is not { } opening ||
            side.ClosingOrigin is not { } closing || closing.StartOffset <= opening.StartOffset) return null;
        var scan = WmlAuthoredStructureScanner.Scan(source.Text);
        if (scan.Problem is not null ||
            !TagLine(scenarioOpening, scenario.OpeningExpansionStack, "[scenario]", out _) ||
            !TagLine(opening, side.OpeningExpansionStack, "[side]", out _) ||
            !TagLine(closing, side.ClosingExpansionStack, "[/side]", out var closingLine)) return null;
        var scenarios = WmlBlockScanner.FindTopLevelBlockSpans(scan.MaskedText).Where(span => span.TagName == "scenario" &&
            span.StartIndex == scenarioOpening.StartOffset).Take(2).ToArray();
        if (scenarios.Length != 1) return null;
        var span = scenarios[0];
        var range = new WmlStaticSourceRange(source, span.StartIndex, span.EndIndexExclusive - span.StartIndex);
        if (!WmlSingleBlockValidator.Validate(range.Text, "scenario").IsValid ||
            !ReferenceEquals(WccCapturedScenarioPreview.FindLiteralOwner(range, projection), scenario)) return null;
        var matches = ScenarioContentSourceInventory.Build(range.Text).Where(entry =>
            entry.Kind == ScenarioContentSourceKind.ChildTag && entry.Name == "side" &&
            range.StartOffset + entry.StartOffset <= opening.StartOffset &&
            range.StartOffset + entry.EndOffsetExclusive >= closing.StartOffset + closing.Length &&
            range.StartOffset + entry.EndOffsetExclusive - 1 <= scan.Lines[closingLine].ContentEndOffset).Take(2).ToArray();
        if (matches.Length != 1 || matches[0].IsConditional) return null;
        var start = range.StartOffset + matches[0].StartOffset;
        return new(source, start, closing.StartOffset + closing.Length - start);

        bool TagLine(WmlStaticSourceRange tag, IReadOnlyList<WmlStaticExpansionFrame> frames, string expected, out int lineIndex)
        {
            lineIndex = -1;
            if (!WccScenarioFieldRouteAnalyzer.SameSource(tag.Source, source) || tag.Text != expected ||
                frames.Any(frame => WccScenarioFieldRouteAnalyzer.SameSource(frame.Definition.Source, source) &&
                    tag.StartOffset >= frame.Definition.StartOffset &&
                    tag.StartOffset + tag.Length <= frame.Definition.StartOffset + frame.Definition.Length) ||
                !scan.MaskedText.AsSpan(tag.StartOffset, tag.Length).SequenceEqual(expected)) return false;
            for (var index = 0; index < scan.Lines.Count; index++)
            {
                var line = scan.Lines[index];
                if (tag.StartOffset < line.StartOffset || tag.StartOffset + tag.Length > line.ContentEndOffset) continue;
                if (scan.ConditionalLines[index] || !source.Text.AsSpan(line.StartOffset, tag.StartOffset - line.StartOffset).Trim().IsEmpty ||
                    WmlInlineCommentSplitter.Split(source.Text[(tag.StartOffset + tag.Length)..line.ContentEndOffset]).Value.Trim().Length != 0)
                    return false;
                lineIndex = index;
                return true;
            }
            return false;
        }
    }

    public static WccScenarioLeaderAdditionEdit CreateEdit(WccScenarioLeaderAdditionSnapshot snapshot,
        IEnumerable<WccScenarioLeaderAddition> leaders)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(leaders);
        var captured = leaders.ToArray();
        if (!snapshot.CanAdd || snapshot.SideRange is null)
            throw new InvalidOperationException(snapshot.Diagnostic);
        if (captured.Length is < 1 or > 256 || captured.Any(leader => leader is null) ||
            captured.Sum(leader => leader.EncodedSize) > 2 * 1024 * 1024)
            throw new InvalidOperationException("Keep between one and 256 new leaders within the bounded field payload size.");
        if (captured.Any(leader => leader.DifficultyValues.Count != 0))
        {
            var plan = snapshot.Plan;
            if (plan is null || string.IsNullOrWhiteSpace(plan.CampaignDefine) || plan.Contexts.Count is < 1 or > 16 || plan.Contexts.Count != plan.Difficulties.Count ||
                plan.Difficulties.Any(row => string.IsNullOrWhiteSpace(row.Define) || row.Define.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')) ||
                plan.Difficulties.Select(row => row.Define).Distinct(StringComparer.Ordinal).Count() != plan.Contexts.Count ||
                plan.Contexts.Select(context => context.SemanticKey).Distinct(StringComparer.Ordinal).Count() != plan.Contexts.Count ||
                plan.Contexts.Where((context, index) => context.CampaignDefine != plan.CampaignDefine || context.DifficultyDefine != plan.Difficulties[index].Define).Any())
                throw new InvalidOperationException("Independent new-leader fields require one complete fixed campaign difficulty set.");
            foreach (var slots in captured.SelectMany(leader => leader.DifficultyValues.Values))
                if (slots.Count != plan.Contexts.Count || slots.Select(slot => slot.Define).Distinct(StringComparer.Ordinal).Count() != slots.Count ||
                    slots.Select(slot => slot.ContextSemanticKey).Distinct(StringComparer.Ordinal).Count() != slots.Count ||
                    !plan.Contexts.All(context => slots.Any(slot => slot.ContextSemanticKey == context.SemanticKey && slot.Define == context.DifficultyDefine)))
                    throw new InvalidOperationException("A new-leader field must supply one exact literal slot per fixed declared difficulty.");
        }
        return new(snapshot, captured);
    }

    internal static WccScenarioFieldSourcePatch PlanPatch(WccScenarioLeaderAdditionEdit edit)
    {
        CreateEdit(edit.Snapshot, edit.Leaders);
        var newline = edit.Source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : edit.Source.Text.Contains('\n') ? "\n" : "\r";
        var text = new StringBuilder();
        foreach (var leader in edit.Leaders)
        {
            text.Append(edit.Snapshot.Indent).Append("[leader]").Append(newline);
            if (leader.Preset is { } preset)
                text.Append(edit.Snapshot.Indent).Append("    {").Append(preset.Name).Append('}').Append(newline);
            foreach (var pair in leader.EncodedValues)
                text.Append(edit.Snapshot.Indent).Append("    ").Append(pair.Key).Append('=').Append(pair.Value).Append(newline);
            if (leader.EncodedDifficultyValues.Count != 0)
                foreach (var context in edit.Snapshot.Plan!.Contexts)
                {
                    text.Append("#ifdef ").Append(context.DifficultyDefine).Append(newline);
                    foreach (var pair in leader.EncodedDifficultyValues)
                        text.Append(edit.Snapshot.Indent).Append("    ").Append(pair.Key).Append('=')
                            .Append(pair.Value.Single(slot => slot.ContextSemanticKey == context.SemanticKey).ValueText).Append(newline);
                    text.Append("#endif").Append(newline);
                }
            text.Append(edit.Snapshot.Indent).Append("[/leader]").Append(newline);
        }
        return new(edit.Snapshot.InsertionOffset, 0, text.ToString());
    }

    internal static bool SameLayout(WccScenarioLeaderAdditionSnapshot left, WccScenarioLeaderAdditionSnapshot right) =>
        left.CanAdd && right.CanAdd && left.InsertionOffset == right.InsertionOffset &&
        left.ExistingLeaders.Count == right.ExistingLeaders.Count &&
        left.ExistingLeaders.Zip(right.ExistingLeaders).All(pair => WccScenarioSideTopologyPlanner.SameOwner(pair.First, pair.Second));
}
