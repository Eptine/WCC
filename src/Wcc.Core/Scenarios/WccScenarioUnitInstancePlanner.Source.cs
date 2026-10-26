using System.Collections.ObjectModel;
using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioUnitInstancePlanner
{
    internal static WccScenarioFieldSourcePatch PlanPatch(WccScenarioUnitInstanceEdit edit)
    {
        _ = Create(edit.Kind, edit.Snapshot, edit.Entry, edit.Spec);
        var newline = edit.Source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : edit.Source.Text.Contains('\n') ? "\n" : "\r";
        if (edit.Kind == WccScenarioUnitInstanceEditKind.Remove) return new(edit.Entry!.Range.StartOffset, edit.Entry.Range.Length, "");
        var spec = edit.Spec!;
        if (edit.Kind == WccScenarioUnitInstanceEditKind.Add)
        {
            return new(edit.Snapshot.InsertionOffset, 0,
                Wrap(RenderUnit(spec, edit.Snapshot.Indent, newline, spec.IsLoyal, null), spec.DifficultyDefine, newline) + newline);
        }
        var entry = edit.Entry!; var literal = entry.Literal!;
        var unitText = entry.UnitRange.Text;
        if (entry.Preset is null)
        {
            unitText = PatchLocalUnitFields(literal, spec, newline, unitText);
        }
        else unitText = RenderUnit(spec, literal.Indent[..^4], newline, spec.IsLoyal ?? entry.IsLoyal, null);
        var within = new WccScenarioFieldSourcePatch(entry.UnitRange.StartOffset - entry.Range.StartOffset, entry.UnitRange.Length, unitText);
        var groupPatches = new List<WccScenarioFieldSourcePatch> { within };
        if (entry.DifficultyDefine is not null && spec.DifficultyDefine != entry.DifficultyDefine)
        {
            var directives = WmlEditorAnalysis.Build(entry.Range.Text).Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive).ToArray();
            var first = directives.First(); var last = directives.Last();
            groupPatches.Add(new(first.StartOffset, first.Length, spec.DifficultyDefine is null ? "" : "#ifdef " + spec.DifficultyDefine));
            if (spec.DifficultyDefine is null)
                groupPatches.Add(new(last.StartOffset, "#endif".Length, "")); // Retain a trailing ordinary comment.
        }
        var rewritten = Apply(entry.Range.Text, groupPatches);
        if (entry.DifficultyDefine is null && spec.DifficultyDefine is not null) rewritten = Wrap(rewritten, spec.DifficultyDefine, newline);
        return new(entry.Range.StartOffset, entry.Range.Length, rewritten);
    }

    private static string PatchLocalUnitFields(WccUnitInstanceLiteral literal, WccScenarioUnitInstanceSpec spec,
        string newline, string unitText)
    {
        var patches = new List<WccScenarioFieldSourcePatch>();
        foreach (var pair in literal.Fields.Where(pair => IsEditableKey(pair.Key)))
        {
            if (!spec.Values.TryGetValue(pair.Key, out var desired))
                patches.Add(new(pair.Value.AssignmentStart, pair.Value.AssignmentLength, ""));
            else
            {
                WccScenarioFieldValueCodec.TryGetDefinition("leader", pair.Key, out var definition);
                if (!WccScenarioFieldValueCodec.Equivalent(definition!, pair.Value.RawValue, desired))
                    patches.Add(new(pair.Value.ValueStart, pair.Value.ValueLength, Encode(pair.Key, desired, pair.Value.RawValue)));
            }
        }
        foreach (var pair in spec.Values.Where(pair => !literal.Fields.ContainsKey(pair.Key)))
            patches.Add(new(literal.ClosingLineStart, 0, literal.Indent + pair.Key + "=" + Encode(pair.Key, pair.Value) + newline));
        AddLoyaltyPatches(literal, spec, patches, newline);
        unitText = Apply(unitText, patches);
        return unitText;
    }

    internal static bool SameLayout(WccScenarioUnitInstanceSnapshot left, WccScenarioUnitInstanceSnapshot right) =>
        SamePhysicalLayout(left, right) && left.Entries.Zip(right.Entries).All(pair => SameEntry(pair.First, pair.Second));

    // A different difficulty can have no effective owner for the same authored
    // wrapper. Its physical identity stays exact; only the active edited row is
    // required to reproduce the original edit binding. Output absence is checked
    // independently by the paired evaluator before an inactive row is removed.
    internal static bool SameLayoutInContext(WccScenarioUnitInstanceSnapshot left, WccScenarioUnitInstanceSnapshot right,
        WccScenarioUnitInstanceEntry? target, WmlEvaluationContext context)
    {
        if (!SamePhysicalLayout(left, right)) return false;
        if (target is null) return true;
        var index = left.Entries.ToList().FindIndex(entry => ReferenceEquals(entry, target));
        if (index < 0) return false;
        var fresh = right.Entries[index];
        return target.DifficultyDefine is not null && target.DifficultyDefine != context.DifficultyDefine
            ? fresh.Owner is null : SameEntry(target, fresh);
    }

    private static bool SamePhysicalLayout(WccScenarioUnitInstanceSnapshot left, WccScenarioUnitInstanceSnapshot right) =>
        left.CanAdd && right.CanAdd && left.InsertionOffset == right.InsertionOffset && left.SameOwner(right.SideOwner) &&
        WccScenarioFieldRouteAnalyzer.SameSource(left.Source, right.Source) && left.SideRange is { } firstSide && right.SideRange is { } secondSide &&
        SamePresetRange(firstSide, secondSide) && left.Entries.Count == right.Entries.Count && left.Entries.Zip(right.Entries).All(pair =>
            SamePresetRange(pair.First.Range, pair.Second.Range) && SamePresetRange(pair.First.UnitRange, pair.Second.UnitRange) &&
            pair.First.DifficultyDefine == pair.Second.DifficultyDefine);

    private static bool SameEntry(WccScenarioUnitInstanceEntry first, WccScenarioUnitInstanceEntry second) =>
        first.CanEdit == second.CanEdit && SamePreset(first.Preset, second.Preset) && first.IsLoyal == second.IsLoyal &&
        first.Values.Count == second.Values.Count && first.Values.All(value => second.Values.GetValueOrDefault(value.Key) == value.Value);

    // The event graph shares the initial-unit serializer and literal patcher.
    // Only its side belongs to the action instead of a containing [side].
    internal static string RenderEventUnit(WccScenarioUnitInstanceSpec spec, int side, string indent, string newline,
        string? originalRaw = null, WccScenarioUnitInstanceSpec? originalSpec = null)
    {
        var ordered = new WccScenarioUnitInstanceSpec(spec.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            { IsLoyal = spec.IsLoyal, Preset = spec.Preset };
        if (originalRaw is null || originalSpec is null)
            return RenderUnit(ordered, indent, newline, ordered.IsLoyal, side);
        var problem = CaptureEventUnit(originalRaw, originalSpec.Preset is { } preset ? [preset] : [],
            out _, out var originalSide, out var literal, out var sideField);
        if (problem is not null || literal is null || sideField is null)
            throw new InvalidOperationException(problem ?? "The captured Spawn Unit has no complete literal patch source.");
        if (spec.Preset is not null || originalSpec.Preset is not null)
        {
            // The exact unchanged wrapper remains lossless. A changed preset
            // instance uses the same canonical shared serializer, keeping the call.
            if (side == originalSide && SameEventUnitSpec(spec, originalSpec)) return originalRaw;
            return RenderUnit(ordered, indent, newline, ordered.IsLoyal ?? originalSpec.IsLoyal, side);
        }
        // Side stays outside the shared initial-unit field mask.
        var withoutSide = PatchLocalUnitFields(literal, ordered, newline, originalRaw);
        // The shared patcher may change earlier string lengths; locate the same
        // retained side again before editing its value.
        if (side != originalSide)
        {
            var refusal = CaptureEventUnit(withoutSide, [], out _, out _, out _, out var currentSide);
            if (refusal is not null || currentSide is null)
                throw new InvalidOperationException(refusal ?? "The retained Spawn Unit side lost its literal source.");
            withoutSide = Apply(withoutSide, [new(currentSide.ValueStart, currentSide.ValueLength,
                side.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        }
        return withoutSide;
    }

    internal static bool SameEventUnitSpec(WccScenarioUnitInstanceSpec? first, WccScenarioUnitInstanceSpec? second) =>
        first is null ? second is null : second is not null && first.DifficultyDefine == second.DifficultyDefine &&
        ((first.IsLoyal ?? first.Preset?.Payload?.UnitTemplate?.IsLoyal) == true) == ((second.IsLoyal ?? second.Preset?.Payload?.UnitTemplate?.IsLoyal) == true) && SamePreset(first.Preset, second.Preset) &&
        first.Values.Count == second.Values.Count && first.Values.All(pair => second.Values.GetValueOrDefault(pair.Key) == pair.Value);
    private static string RenderUnit(WccScenarioUnitInstanceSpec spec, string indent, string newline, bool? loyal,
        int? side)
    {
        var text = new StringBuilder().Append(indent).Append("[unit]").Append(newline);
        if (spec.Preset is { } preset) text.Append(indent).Append("    {").Append(preset.Name).Append('}').Append(newline);
        if (side is { } sideNumber) text.Append(indent).Append("    side=").Append(sideNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(newline);
        foreach (var pair in spec.Values.Where(pair => spec.Preset?.Payload?.Values.GetValueOrDefault(pair.Key) != pair.Value))
            text.Append(indent).Append("    ").Append(pair.Key).Append('=').Append(Encode(pair.Key, pair.Value)).Append(newline);
        if (spec.Preset is null && loyal == true) text.Append(RenderLoyalModifications(indent + "    ", newline));
        return text.Append(indent).Append("[/unit]").ToString();
    }

    private static string Encode(string key, string value, string? old = null)
    {
        WccScenarioFieldValueCodec.TryGetDefinition("leader", key, out var definition);
        return WccScenarioFieldValueCodec.EncodeLiteral(definition!, value, old);
    }
    private static string Wrap(string unit, string? define, string newline) => define is null ? unit : "#ifdef " + define + newline + unit + newline + "#endif";
    private static string Apply(string raw, IEnumerable<WccScenarioFieldSourcePatch> patches)
    {
        foreach (var patch in patches.OrderByDescending(patch => patch.Start).ThenBy(patch => patch.Length))
            raw = raw.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.ReplacementText);
        return raw;
    }

    private static void ValidateScope(WmlCampaignEvaluationPlan? plan, string? define)
    {
        if (plan is null || plan.Contexts.Count is < 1 or > 16 || plan.Contexts.Count != plan.Difficulties.Count ||
            string.IsNullOrWhiteSpace(plan.CampaignDefine) || plan.Contexts.Any(context => context.CampaignDefine != plan.CampaignDefine) ||
            plan.Contexts.Select(context => context.SemanticKey).Distinct(StringComparer.Ordinal).Count() != plan.Contexts.Count ||
            plan.Difficulties.Any(row => string.IsNullOrWhiteSpace(row.Define) || row.Define.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')) ||
            plan.Contexts.Where((context, index) => context.DifficultyDefine != plan.Difficulties[index].Define).Any() ||
            define is not null && !plan.Contexts.Any(context => context.DifficultyDefine == define))
            throw new InvalidOperationException("Initial units need all difficulties or one exact fixed declared difficulty.");
    }

    internal static string? CaptureUnit(string raw, out WccUnitInstanceLiteral? literal, out IReadOnlyDictionary<string, string> values) =>
        CaptureUnit(raw, null, out literal, out values);

    private static string? CaptureUnit(string raw, WccScenarioUnitTemplate? template,
        out WccUnitInstanceLiteral? literal, out IReadOnlyDictionary<string, string> values)
    {
        literal = null; values = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        if (raw.Length > 2 * 1024 * 1024) return "This initial unit exceeds the bounded edit size; use RAW.";
        var analysis = WmlEditorAnalysis.Build(raw);
        if (analysis.Tokens.Any(token => token.Kind is WmlEditorTokenKind.Directive or WmlEditorTokenKind.MacroInvocation or WmlEditorTokenKind.RawCode) ||
            analysis.References.Count != 0 || WccScenarioEventSourcePlanner.HasUnsafeControls(raw) || analysis.TagOccurrences.Count > 512 ||
            analysis.TagOccurrences.Any(tag => !tag.IsComplete || tag.IsComputedName || tag.MatchingTagIndex is null ||
                raw.AsSpan(tag.StartOffset + 1, tag.Length - 1).TrimStart().StartsWith("+", StringComparison.Ordinal) ||
                raw.AsSpan(tag.StartOffset + 1, tag.Length - 1).TrimStart().StartsWith("-", StringComparison.Ordinal)))
            return "Generated, amended, macro or control-based unit source retains RAW editing.";
        if (WmlSingleBlockValidator.Validate(raw, "unit") is { IsValid: false } invalid) return invalid.Problem;
        var source = new WmlStaticSource("wcc-unit-instance-preview", raw, isAuthored: false);
        var evaluation = new WmlStaticEvaluationResult(raw, [new WmlStaticOutputSegment(0, raw, new(source, 0, raw.Length), [])], [], [source], [], true);
        WmlStaticScalarProjection projection;
        try { projection = WmlStaticScalarProjection.Create(evaluation); }
        catch (InvalidOperationException exception) { return exception.Message; }
        var roots = projection.Owners.Where(owner => owner.Parent is null).ToArray();
        if (projection.Diagnostics.Count != 0 || roots.Length != 1 || roots[0].TagName != "unit" ||
            projection.Owners.Any(owner => owner.IsAmended || owner.HasUnresolvedContributions || owner.OpeningOrigin is null || owner.ClosingOrigin is null ||
                owner.Fields.Any(pair => !pair.Value.IsKnown || pair.Value.Contributions.Count != 1 || pair.Value.ValueText is null ||
                    !WccScenarioFieldValueCodec.IsSupportedLiteralWml(pair.Value.ValueText))))
            return "Initial unit fields and children must be complete retained literals; use RAW for dynamic source.";
        var unit = roots[0];
        if (template is not null && (unit.DirectChildren.Count != 0 || unit.Fields.Keys.Any(key => !IsEditableKey(key))))
            return "A saved unit preset wrapper needs only supported local fields; preserve custom children and fields with RAW.";
        if (unit.Fields.Keys.Any(key => key is "placement" or "location_id" or "parent_type" or "passable" or "overwrite" or "side"))
            return "This unit carries a side/type/placement override outside numeric initial placement; use RAW.";
        var fields = unit.Fields.ToDictionary(pair => pair.Key, pair =>
        {
            var contribution = pair.Value.Contributions[0];
            return new WccUnitInstanceField(pair.Key, pair.Value.ValueText!, contribution.AssignmentOutputOffset, contribution.AssignmentOutputLength,
                contribution.ValueOutputOffset, contribution.ValueOutputLength);
        }, StringComparer.Ordinal);
        var decoded = fields.Where(pair => IsEditableKey(pair.Key)).ToDictionary(pair => pair.Key,
            pair => WccScenarioFieldValueCodec.DecodeLiteral(pair.Value.RawValue), StringComparer.Ordinal);
        var effective = template is null ? decoded : template.Values.Concat(decoded).GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        values = new ReadOnlyDictionary<string, string>(effective);
        try { _ = new WccScenarioUnitInstanceSpec(effective); }
        catch (ArgumentException exception) { return exception.Message; }
        var close = unit.ClosingOrigin!.StartOffset;
        while (close > 0 && raw[close - 1] is not ('\r' or '\n')) close--;
        var opening = unit.OpeningOrigin!.StartOffset; var first = opening;
        while (first > 0 && raw[first - 1] is not ('\r' or '\n')) first--;
        literal = new(unit, new ReadOnlyDictionary<string, WccUnitInstanceField>(fields), close, raw[first..opening] + "    ")
            { Loyalty = CaptureLoyalty(unit, raw) };
        return null;
    }

    private static string? CaptureScope(WmlStaticSource source, WmlStaticSourceRange side, WmlStaticSourceRange unit,
        WmlCampaignEvaluationPlan? plan, out WmlStaticSourceRange target, out string? define)
    {
        target = unit; define = null;
        var stack = new Stack<(WmlEditorToken Start, string[] Words, bool HasElse)>();
        var intervals = new List<(WmlEditorToken Start, WmlEditorToken End, string[] Words, bool HasElse)>();
        foreach (var token in WmlEditorAnalysis.Build(source.Text).Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive))
        {
            var words = source.Text.Substring(token.StartOffset, token.Length).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            if (WmlAuthoredStructureScanner.IsConditionalStart(words[0].TrimStart('#'))) stack.Push((token, words, false));
            else if (words[0] == "#else" && stack.Count != 0) { var active = stack.Pop(); stack.Push((active.Start, active.Words, true)); }
            else if (words[0] == "#endif" && stack.Count != 0) { var active = stack.Pop(); intervals.Add((active.Start, token, active.Words, active.HasElse)); }
        }
        var enclosing = intervals.Where(interval => interval.Start.StartOffset < unit.StartOffset &&
            interval.End.StartOffset >= unit.StartOffset + unit.Length).ToArray();
        if (enclosing.Length == 0) return null;
        if (enclosing.Length != 1 || enclosing[0].HasElse || enclosing[0].Words.Length != 2 || enclosing[0].Words[0] != "#ifdef" ||
            enclosing[0].Start.StartOffset < side.StartOffset || enclosing[0].End.StartOffset + enclosing[0].End.Length > side.StartOffset + side.Length)
            return "Nested, shared or non-difficulty unit conditionals retain RAW editing.";
        var group = enclosing[0];
        try { ValidateScope(plan, group.Words[1]); }
        catch (InvalidOperationException exception) { return exception.Message; }
        var mask = WmlAuthoredStructureScanner.Scan(source.Text).MaskedText;
        if (!mask.AsSpan(group.Start.StartOffset + group.Start.Length, unit.StartOffset - group.Start.StartOffset - group.Start.Length).Trim().IsEmpty ||
            !mask.AsSpan(unit.StartOffset + unit.Length, group.End.StartOffset - unit.StartOffset - unit.Length).Trim().IsEmpty)
            return "A difficulty wrapper owns additional source; use RAW instead of moving or removing its unit.";
        var start = group.Start.StartOffset;
        while (start > 0 && source.Text[start - 1] is not ('\r' or '\n')) start--;
        target = new(source, start, group.End.StartOffset + group.End.Length - start); define = group.Words[1];
        return null;
    }

    private static WmlStaticSourceRange? FindDisplaySideRange(WmlStaticSource source, WmlStaticScalarOwner side)
    {
        if (side.TagName != "side" || side.OpeningOrigin is not { } opening ||
            !WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, source)) return null;
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        foreach (var root in WmlProjectionTreeParser.Parse(structure.MaskedText).Roots.Where(root => root.TagName == "scenario"))
        foreach (var candidate in root.Children.Where(candidate => candidate.TagName == "side"))
        {
            if (candidate.EndLineExclusive <= candidate.StartLine || candidate.EndLineExclusive > structure.Lines.Count) continue;
            var first = structure.Lines[candidate.StartLine]; var last = structure.Lines[candidate.EndLineExclusive - 1];
            var firstCode = structure.MaskedText.AsSpan(first.StartOffset, first.ContentEndOffset - first.StartOffset);
            var leading = firstCode.Length - firstCode.TrimStart().Length;
            if (first.StartOffset + leading != opening.StartOffset || !firstCode.Trim().SequenceEqual("[side]".AsSpan()) ||
                !structure.MaskedText.AsSpan(last.StartOffset, last.ContentEndOffset - last.StartOffset).Trim().SequenceEqual("[/side]".AsSpan())) continue;
            return new(source, first.StartOffset, last.ContentEndOffset - first.StartOffset);
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadUnitHints(string raw, WmlStaticScalarOwner? captured)
    {
        if (captured is null)
        {
            // Display-only known scalar hints from the shared parser. Unresolved
            // keys stay absent; this summary cannot create a spec or source route.
            var source = new WmlStaticSource("wcc-unit-instance-raw-hints", raw, isAuthored: false);
            try
            {
                var view = new WmlStaticEvaluationResult(raw, [new WmlStaticOutputSegment(0, raw, new(source, 0, raw.Length), [])], [], [source], [], true);
                captured = WmlStaticScalarProjection.Create(view).Owners.SingleOrDefault(owner => owner.Parent is null && owner.TagName == "unit");
            }
            catch (InvalidOperationException) { return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()); }
        }
        return new ReadOnlyDictionary<string, string>((captured?.Fields.Where(pair => IsEditableKey(pair.Key) && pair.Value.IsKnown &&
            pair.Value.ValueText is { } value && WccScenarioFieldValueCodec.IsSupportedLiteralWml(value)) ?? [])
            .ToDictionary(pair => pair.Key, pair => WccScenarioFieldValueCodec.DecodeLiteral(pair.Value.ValueText), StringComparer.Ordinal));
    }
}
