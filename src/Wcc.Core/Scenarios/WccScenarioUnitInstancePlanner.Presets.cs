using System.Collections.ObjectModel;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioUnitInstancePlanner
{
    // A saved catalog row alone is not an instance binding. The existing captured
    // evaluation must select that exact definition at this exact local invocation.
    private static bool TryCapturePresetUnit(WmlStaticSourceRange unit, WmlStaticScalarOwner? owner,
        WmlCampaignEvaluationPlan? plan, WmlStaticScalarProjection? projection,
        IReadOnlyList<WccCampaignLocalPresetEntry> presets, out WccUnitInstanceLiteral? literal,
        out IReadOnlyDictionary<string, string> values, out WccCampaignLocalPresetEntry? selected)
    {
        literal = null; values = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()); selected = null;
        if (owner is null || owner.IsAmended || owner.HasUnresolvedContributions || projection?.SourceEvaluation is not { IsComplete: true } evaluation ||
            owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing || !Within(opening, unit) || !Within(closing, unit)) return false;
        var analysis = WmlEditorAnalysis.Build(unit.Text);
        if (analysis.Tokens.Any(token => token.Kind is WmlEditorTokenKind.Comment or WmlEditorTokenKind.Directive or WmlEditorTokenKind.RawCode)) return false;
        var macros = analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation).ToArray();
        if (macros.Length != 1) return false;
        var token = macros[0]; var rawCall = unit.Text.Substring(token.StartOffset, token.Length);
        var syntax = WmlMacroInvocationSyntaxScanner.Scan(rawCall).ToArray();
        if (syntax.Length != 1 || syntax[0].Arguments.Count != 0 || syntax[0].Span.StartOffset != 0 || syntax[0].Span.Length != token.Length) return false;
        var candidates = presets.Where(entry => entry.Name == syntax[0].Name && entry.Role == WccCampaignLocalPresetRole.Unit &&
            entry.IsSupported && entry.IsAvailableIn(plan) && entry.Payload?.UnitTemplate is not null).ToArray();
        if (candidates.Length != 1 || rawCall != "{" + candidates[0].Name + "}") return false;
        var preset = candidates[0]; var template = preset.Payload!.UnitTemplate!;
        var invocation = new WmlStaticSourceRange(unit.Source, unit.StartOffset + token.StartOffset, token.Length);
        var calls = evaluation.Invocations.Where(call => call.Name == preset.Name && call.InvocationOrigins.Count == 1 &&
            SamePresetRange(call.InvocationOrigins[0], invocation)).ToArray();
        if (calls.Length != 1 || calls[0].Outcome != WmlStaticInvocationOutcome.Expanded || !PresetDefinitionMatches(preset, calls[0].Definition)) return false;
        var mask = unit.Text.ToCharArray();
        for (var index = token.StartOffset; index < token.StartOffset + token.Length; index++)
            if (mask[index] is not ('\r' or '\n')) mask[index] = ' ';
        if (CaptureUnit(new string(mask), template, out literal, out values) is not null || literal is null ||
            literal.Fields.Values.Any(field => field.AssignmentStart <= token.StartOffset + token.Length) ||
            values.GetValueOrDefault("id") != template.Values["id"] || owner.Fields.Count != values.Count ||
            owner.DirectChildren.Count != (template.IsLoyal == true ? 1 : 0) || template.IsLoyal == true && !IsPresetLoyalty(owner)) return false;
        foreach (var pair in values)
        {
            if (!WccScenarioFieldValueCodec.TryGetDefinition("leader", pair.Key, out var definition) ||
                !owner.Fields.TryGetValue(pair.Key, out var field) || !field.IsKnown ||
                !WccScenarioFieldValueCodec.Equivalent(definition, field.ValueText, pair.Value) ||
                field.Contributions.Count != (template.Values.ContainsKey(pair.Key) ? 1 : 0) + (literal.Fields.ContainsKey(pair.Key) ? 1 : 0)) return false;
            var contributionIndex = 0;
            if (template.Values.ContainsKey(pair.Key) && !FromPreset(field.Contributions[contributionIndex++])) return false;
            if (literal.Fields.ContainsKey(pair.Key) && !Local(field.Contributions[contributionIndex])) return false;
        }
        selected = preset;
        return true;

        bool FromPreset(WmlStaticScalarContribution contribution) =>
            contribution.AssignmentOrigin is { } assignment && Within(assignment, preset.BodyRange) &&
            contribution.AssignmentOrigins.Count == 1 && contribution.CompositeAssignmentKeys.Count == 0 &&
            contribution.ValueMappingIsExact && contribution.ValueOrigins.Count == 1 && Within(contribution.ValueOrigins[0], preset.BodyRange) &&
            PresetFrames(contribution.ExpansionStack);
        bool PresetFrames(IReadOnlyList<WmlStaticExpansionFrame> frames)
        {
            var local = frames.Where(frame => frame.InvocationOrigins.Any(origin => Within(origin, unit))).ToArray();
            return local.Length == 1 && local[0].MacroName == preset.Name && PresetDefinitionMatches(preset, local[0].Definition) &&
                local[0].RequiredParameters is { Count: 0 } && local[0].OptionalParameters is { Count: 0 } &&
                local[0].InvocationOrigins.Count == 1 && SamePresetRange(local[0].InvocationOrigins[0], invocation);
        }
        bool Local(WmlStaticScalarContribution contribution) => contribution.AssignmentOrigin is { } assignment && Within(assignment, unit) &&
            contribution.AssignmentOrigins.Count == 1 && contribution.CompositeAssignmentKeys.Count == 0 && contribution.ValueMappingIsExact &&
            contribution.ValueOrigins.Count == 1 && Within(contribution.ValueOrigins[0], unit) &&
            !contribution.ExpansionStack.Concat(contribution.ValueExpansionStack).Any(frame => frame.InvocationOrigins.Any(origin => Within(origin, unit)));
    }

    // Bounded detached Spawn seed. This recognizes an explicit captured catalog
    // reference; the reviewer separately proves real activation and every frame.
    internal static string? CaptureEventUnit(string raw, IReadOnlyList<WccCampaignLocalPresetEntry>? presets,
        out WccScenarioUnitInstanceSpec? spec, out int side, out WccUnitInstanceLiteral? literal,
        out WccUnitInstanceField? sideField)
    {
        spec = null; side = 0; literal = null; sideField = null;
        var analysis = WmlEditorAnalysis.Build(raw);
        if (analysis.Tokens.Any(token => token.Kind is WmlEditorTokenKind.Directive or WmlEditorTokenKind.RawCode) ||
            analysis.TagOccurrences.Any(tag => !tag.IsComplete || tag.IsComputedName || tag.MatchingTagIndex is null) ||
            WccScenarioEventSourcePlanner.HasUnsafeControls(raw)) return "A Spawn Unit needs literal fields and one exact saved Unit preset; custom controls use RAW.";
        var calls = analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation).ToArray();
        if (calls.Length > 1 || analysis.References.Count != calls.Length || analysis.References.Any(reference =>
                !calls.Any(call => call.StartOffset == reference.StartOffset && call.Length == reference.Length)))
            return "A Spawn Unit accepts at most one exact saved Unit preset invocation.";
        WccCampaignLocalPresetEntry? selected = null;
        var mask = raw.ToCharArray();
        if (calls.Length == 1)
        {
            var call = calls[0]; var invocation = raw.Substring(call.StartOffset, call.Length);
            var lineStart = call.StartOffset; var lineEnd = call.StartOffset + call.Length;
            while (lineStart > 0 && raw[lineStart - 1] is not ('\r' or '\n')) lineStart--;
            while (lineEnd < raw.Length && raw[lineEnd] is not ('\r' or '\n')) lineEnd++;
            var suffix = raw.AsSpan(call.StartOffset + call.Length, lineEnd - call.StartOffset - call.Length).TrimStart();
            if (!raw.AsSpan(lineStart, call.StartOffset - lineStart).Trim().IsEmpty || !suffix.IsEmpty && suffix[0] != '#')
                return "A saved Unit preset needs its own executable line inside the Spawn unit; quoted and computed calls use RAW.";
            var choices = (presets ?? []).Where(preset => preset.Role == WccCampaignLocalPresetRole.Unit && preset.IsSupported &&
                preset.Payload?.UnitTemplate is not null && invocation == "{" + preset.Name + "}").ToArray();
            if (choices.Length != 1) return "Choose the exact supported saved Unit preset for this Spawn action; unknown or ambiguous helpers use RAW.";
            selected = choices[0];
            for (var index = call.StartOffset; index < call.StartOffset + call.Length; index++)
                if (mask[index] is not ('\r' or '\n')) mask[index] = ' ';
        }
        var masked = new string(mask);
        if (WmlSingleBlockValidator.Validate(masked, "unit") is { IsValid: false } invalid) return invalid.Problem;
        var source = new WmlStaticSource("wcc-event-unit-preview", masked, false);
        var evaluation = new WmlStaticEvaluationResult(masked, [new(0, masked, new(source, 0, masked.Length), [])], [], [source], [], true);
        var projection = WmlStaticScalarProjection.Create(evaluation);
        var roots = projection.Owners.Where(owner => owner.Parent is null).ToArray();
        if (projection.Diagnostics.Count != 0 || roots.Length != 1 || roots[0].TagName != "unit" ||
            !roots[0].Fields.TryGetValue("side", out var sideValue) || !sideValue.IsKnown || sideValue.Contributions.Count != 1 ||
            sideValue.Contributions[0] is not { ValueMappingIsExact: true, AssignmentOrigin: { } sideAssignment } contribution ||
            sideValue.ValueText is null || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(sideValue.ValueText) ||
            !int.TryParse(WccScenarioFieldValueCodec.DecodeLiteral(sideValue.ValueText), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out side) || side <= 0)
            return "A Spawn Unit needs one explicit positive literal side.";
        if (sideValue.ValueText.TrimStart().StartsWith('_') || contribution.CompositeAssignmentKeys.Count != 0)
            return "A Spawn Unit side must be an untranslated standalone literal.";
        sideField = new("side", sideValue.ValueText, sideAssignment.StartOffset, sideAssignment.Length,
            contribution.ValueOutputOffset, contribution.ValueOutputLength);
        if (calls.Length == 1 && (roots[0].Fields.Values.SelectMany(field => field.Contributions)
                .Any(field => field.AssignmentOutputOffset <= calls[0].StartOffset + calls[0].Length) ||
            roots[0].DirectChildren.Any(child => child.OpeningOrigin?.StartOffset <= calls[0].StartOffset + calls[0].Length)))
            return "The saved Unit preset invocation must precede every local Spawn field and child.";
        for (var index = sideField.AssignmentStart; index < sideField.AssignmentStart + sideField.AssignmentLength; index++)
            if (mask[index] is not ('\r' or '\n')) mask[index] = ' ';
        if (CaptureUnit(new string(mask), selected?.Payload?.UnitTemplate, out literal, out var values) is { } refusal) return refusal;
        if (literal is null || literal.Fields.Keys.Any(key => !IsEditableKey(key)) || literal.Loyalty.Diagnostic is not null ||
            literal.Owner.DirectChildren.Count != 0 && !IsPresetLoyalty(literal.Owner) &&
            !(literal.Owner.DirectChildren.Count == 1 && literal.Owner.DirectChildren[0].TagName == "modifications" &&
              literal.Owner.DirectChildren[0].Fields.Count == 0 && literal.Owner.DirectChildren[0].DirectChildren.Count == 0))
            return "A Spawn Unit supports the shared literal unit fields and standard loyal trait; custom children use RAW.";
        var template = selected?.Payload?.UnitTemplate;
        if (template is not null && values.GetValueOrDefault("id") != template.Values["id"])
            return "A preset Spawn keeps its exact saved unit ID. Choose Custom for another identity.";
        spec = new(values) { Preset = selected, IsLoyal = selected is null ? literal.Loyalty.IsLoyal : template!.IsLoyal };
        return null;
    }
    internal static bool SameEventUnitPreset(WccCampaignLocalPresetEntry? left, WccCampaignLocalPresetEntry? right) => SamePreset(left, right);

    private static bool SamePreset(WccCampaignLocalPresetEntry? left, WccCampaignLocalPresetEntry? right) =>
        left is null ? right is null : right is not null && left.Id == right.Id && left.Role == right.Role && left.Name == right.Name &&
        left.Scope == right.Scope && SamePresetRange(left.DefinitionRange, right.DefinitionRange) && SamePresetRange(left.BodyRange, right.BodyRange);

    private static bool PresetDefinitionMatches(WccCampaignLocalPresetEntry preset, WmlStaticSourceRange? actual)
    {
        var expected = preset.DefinitionRange; var leading = 0;
        while (leading < expected.Length && expected.Text[leading] is ' ' or '\t') leading++;
        return actual is not null && SamePresetRange(actual, new(expected.Source, expected.StartOffset + leading, expected.Length - leading));
    }

    private static bool SamePresetRange(WmlStaticSourceRange left, WmlStaticSourceRange right) =>
        WccScenarioFieldRouteAnalyzer.SameSource(left.Source, right.Source) && left.StartOffset == right.StartOffset && left.Length == right.Length && left.Text == right.Text;

    private static bool Within(WmlStaticSourceRange candidate, WmlStaticSourceRange container) =>
        WccScenarioFieldRouteAnalyzer.SameSource(candidate.Source, container.Source) && candidate.StartOffset >= container.StartOffset &&
        candidate.StartOffset + candidate.Length <= container.StartOffset + container.Length;
}