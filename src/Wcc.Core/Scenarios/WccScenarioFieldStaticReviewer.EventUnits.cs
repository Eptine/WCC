using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    // A graph is intent, never evidence. Reuse its pure projections for the
    // expected values, then prove each real local wrapper and selected callee.
    private static WccScenarioUnitInstanceSpec[] ValidateEventGraphUnits(WmlStaticScalarOwner owner,
        WccScenarioEventGraph graph, WmlStaticSourceRange body, WmlStaticEvaluationResult evaluation,
        WccScenarioFieldSourceChange change, WmlEvaluationContext context, WmlCampaignEvaluationPlan plan,
        Func<WmlStaticSourceRange?, IReadOnlyList<WmlStaticExpansionFrame>, bool> literalOriginProof,
        WmlStaticScalarProjection projection)
    {
        if (!evaluation.IsComplete || owner.TagName != "event" || owner.IsAmended || owner.HasUnresolvedContributions ||
            owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing ||
            !InsideProposedScalar(opening, body) || !InsideProposedScalar(closing, body) ||
            !literalOriginProof(opening, owner.OpeningExpansionStack) ||
            !literalOriginProof(closing, owner.ClosingExpansionStack) ||
            owner.OpeningOutputOffset is not { } first || owner.ClosingOutputOffset is not { } last ||
            first < 0 || last < first || last + closing.Length > evaluation.Text.Length)
            throw new InvalidOperationException("A structured event has no complete exact source and output occurrence.");

        foreach (var preset in graph.Actions.Select(action => action.Unit?.Preset).OfType<WccCampaignLocalPresetEntry>())
            if (preset.Role != WccCampaignLocalPresetRole.Unit || !preset.IsSupported || !preset.IsAvailableIn(plan))
                throw new InvalidOperationException("A Spawn Unit preset is unavailable in this exact campaign scope.");

        var active = WccScenarioEventGraphEditor.ProjectDifficulty(graph, context.DifficultyDefine,
            plan.Difficulties.Select(row => row.Define).ToArray());
        var projected = WccScenarioEventGraphEditor.ProjectUnitPresets(active,
            preset => BindProposedPreset(preset, change).Payload.UnitTemplate
                ?? throw new InvalidOperationException("The selected Spawn preset lost its exact Unit template."));
        var actions = owner.DirectChildren.Where(child => child.TagName != "filter").ToArray();
        if (actions.Length != active.Actions.Count)
            throw new InvalidOperationException("The structured event has a different ordered action footprint.");
        for (var index = 0; index < active.Actions.Count; index++)
        {
            var requested = active.Actions[index];
            if (requested.Kind != WccScenarioEventActionKind.SpawnUnit) continue;
            ValidateEventUnit(actions[index], requested, body, evaluation, change, literalOriginProof);
        }
        // Every Unit contribution above is authoritative before the common pure
        // display view hides earlier defaults. Ordinary Capture stays strict;
        // no unproved duplicate field can be silently accepted by this review.
        if (!ReferenceEquals(projection.SourceEvaluation, evaluation) ||
            !projection.Owners.Any(current => ReferenceEquals(current, owner)))
            throw new InvalidOperationException("The Event graph review lost its exact evaluated projection.");
        var actual = WccScenarioEventGraphEditor.CaptureEvaluated(projection, owner);
        var expectedText = WccScenarioEventGraphEditor.Render(projected, indent: "", newline: "\n");
        var actualText = actual.Seed is { } seed ? WccScenarioEventGraphEditor.Render(seed.Graph, indent: "", newline: "\n") : null;
        if (!actual.CanEdit || !expectedText.IsSuccess || actualText?.IsSuccess != true || actualText.Text != expectedText.Text)
            throw new InvalidOperationException("The structured event does not preserve its complete active trigger, filters and ordered action values.");
        return projected.Actions.Where(action => action.Kind == WccScenarioEventActionKind.SpawnUnit).Select(action => action.Unit!).ToArray();
    }

    private static void ValidateEventUnit(WmlStaticScalarOwner unit, WccScenarioEventAction action,
        WmlStaticSourceRange eventBody, WmlStaticEvaluationResult evaluation, WccScenarioFieldSourceChange change,
        Func<WmlStaticSourceRange?, IReadOnlyList<WmlStaticExpansionFrame>, bool> literalOriginProof)
    {
        if (unit.TagName != "unit" || unit.IsAmended || unit.HasUnresolvedContributions ||
            unit.OpeningOrigin is not { } opening || unit.ClosingOrigin is not { } closing ||
            !InsideProposedScalar(opening, eventBody) || !InsideProposedScalar(closing, eventBody) ||
            !WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, closing.Source) ||
            closing.StartOffset < opening.StartOffset ||
            !literalOriginProof(opening, unit.OpeningExpansionStack) ||
            !literalOriginProof(closing, unit.ClosingExpansionStack) ||
            !SameEventFrameChain(unit.OpeningExpansionStack, unit.ClosingExpansionStack))
            throw new InvalidOperationException("A Spawn Unit has no exact local literal wrapper in its event.");

        var start = opening.StartOffset;
        while (start > eventBody.StartOffset && opening.Source.Text[start - 1] is not ('\r' or '\n')) start--;
        var wrapper = new WmlStaticSourceRange(opening.Source, start, closing.StartOffset + closing.Length - start);
        if (!InsideProposedScalar(wrapper, eventBody))
            throw new InvalidOperationException("A Spawn Unit wrapper extends beyond its exact event body.");
        var selected = action.Unit!.Preset;
        var problem = WccScenarioUnitInstancePlanner.CaptureEventUnit(wrapper.Text, selected is null ? [] : [selected],
            out var captured, out var side, out var literal, out var sideField);
        if (problem is not null || captured is null || literal is null || sideField is null ||
            side != action.Side || !WccScenarioUnitInstancePlanner.SameEventUnitSpec(captured, action.Unit))
            throw new InvalidOperationException(problem ?? "A Spawn Unit source differs from its exact requested unit form result.");

        WccScenarioFieldSourceChange.PresetBinding? binding = null;
        WmlStaticSourceRange? invocation = null;
        if (selected is not null)
        {
            binding = BindProposedPreset(selected, change);
            var lexical = WmlEditorAnalysis.Build(wrapper.Text).Tokens.Single(token => token.Kind == WmlEditorTokenKind.MacroInvocation);
            invocation = new(wrapper.Source, wrapper.StartOffset + lexical.StartOffset, lexical.Length);
            var calls = evaluation.Invocations.Where(call => call.Name == selected.Name &&
                call.InvocationOrigins.Count == 1 && SameRange(call.InvocationOrigins[0], invocation) &&
                SameEventFrameChain(call.ExpansionStack, unit.OpeningExpansionStack)).ToArray();
            if (calls.Length != 1 || calls[0].Outcome != WmlStaticInvocationOutcome.Expanded ||
                !SameRange(calls[0].Definition, binding.Definition) ||
                !literalOriginProof(invocation, calls[0].ExpansionStack))
                throw new InvalidOperationException("A Spawn Unit does not invoke its exact selected shared definition once at its own event occurrence.");
        }
        var defaults = binding?.Payload.UnitTemplate?.Values;
        var keys = (defaults?.Keys ?? []).Concat(literal.Fields.Keys).Append("side").Distinct(StringComparer.Ordinal).ToArray();
        if (unit.Fields.Count != keys.Length)
            throw new InvalidOperationException("A Spawn Unit has additional or missing fields outside its exact shared and local values.");
        foreach (var key in keys)
        {
            var fromPreset = defaults?.ContainsKey(key) == true;
            var local = key == "side" ? sideField : literal.Fields.GetValueOrDefault(key);
            var expectedCount = (fromPreset ? 1 : 0) + (local is null ? 0 : 1);
            if (!unit.Fields.TryGetValue(key, out var field) || !field.IsKnown ||
                field.Contributions.Count != expectedCount || expectedCount == 0)
                throw new InvalidOperationException("A Spawn Unit field does not have its exact ordered shared and local contributions: " + key);
            var position = 0;
            if (fromPreset)
            {
                var contribution = field.Contributions[position++];
                if (!ExactEventContribution(contribution, PresetOrigin) ||
                    contribution.ValueOrigins.Count != 1)
                    throw new InvalidOperationException("A Spawn Unit field lacks its exact selected Unit preset body contribution: " + key);
            }
            if (local is not null)
            {
                var contribution = field.Contributions[position];
                var assignment = new WmlStaticSourceRange(wrapper.Source, wrapper.StartOffset + local.AssignmentStart, local.AssignmentLength);
                var value = new WmlStaticSourceRange(wrapper.Source, wrapper.StartOffset + local.ValueStart, local.ValueLength);
                if (!ExactEventContribution(contribution, LocalOrigin) ||
                    !SameRange(contribution.AssignmentOrigin, assignment) ||
                    contribution.ValueOrigins.Count != 1 || !SameRange(contribution.ValueOrigins[0], value) ||
                    fromPreset && contribution.AssignmentOutputOffset <= field.Contributions[0].AssignmentOutputOffset)
                    throw new InvalidOperationException("A Spawn Unit override lacks its exact later literal source contribution: " + key);
            }
        }
        foreach (var child in unit.DirectChildren) ValidateChild(child);

        bool LocalOrigin(WmlStaticSourceRange? origin, IReadOnlyList<WmlStaticExpansionFrame> frames) =>
            origin is not null && InsideProposedScalar(origin, wrapper) &&
            SameEventFrameChain(frames, unit.OpeningExpansionStack) && literalOriginProof(origin, frames);

        bool PresetOrigin(WmlStaticSourceRange? origin, IReadOnlyList<WmlStaticExpansionFrame> frames)
        {
            if (origin is null || binding is null || selected is null || invocation is null ||
                !InsideProposedScalar(origin, binding.Body) || frames.Count != unit.OpeningExpansionStack.Count + 1 ||
                !SameEventFrameChain(frames.Take(frames.Count - 1).ToArray(), unit.OpeningExpansionStack)) return false;
            var frame = frames[^1];
            return frame.MacroName == selected.Name && SameRange(frame.Definition, binding.Definition) &&
                frame.RequiredParameters is { Count: 0 } && frame.OptionalParameters is { Count: 0 } &&
                frame.InvocationOrigins.Count == 1 && SameRange(frame.InvocationOrigins[0], invocation) &&
                literalOriginProof(invocation, unit.OpeningExpansionStack);
        }

        void ValidateChild(WmlStaticScalarOwner child)
        {
            var proof = selected is null ? (Func<WmlStaticSourceRange?, IReadOnlyList<WmlStaticExpansionFrame>, bool>)LocalOrigin : PresetOrigin;
            if (child.IsAmended || child.HasUnresolvedContributions ||
                !proof(child.OpeningOrigin, child.OpeningExpansionStack) || !proof(child.ClosingOrigin, child.ClosingExpansionStack))
                throw new InvalidOperationException("A Spawn Unit child has no exact local or selected shared loyal-trait ancestry.");
            foreach (var field in child.Fields.Values)
                if (!field.IsKnown || field.Contributions.Count != 1 || !ExactEventContribution(field.Contributions[0], proof))
                    throw new InvalidOperationException("A Spawn Unit loyal-trait field has no exact source contribution.");
            foreach (var descendant in child.DirectChildren) ValidateChild(descendant);
        }
    }

    private static bool ExactEventContribution(WmlStaticScalarContribution contribution,
        Func<WmlStaticSourceRange?, IReadOnlyList<WmlStaticExpansionFrame>, bool> originProof) =>
        contribution.AssignmentOrigin is { } assignment && contribution.AssignmentOrigins.Count == 1 &&
        SameRange(contribution.AssignmentOrigins[0], assignment) &&
        contribution.CompositeAssignmentKeys.Count == 0 && contribution.ValueMappingIsExact &&
        contribution.ValueOrigins.Count != 0 && originProof(assignment, contribution.ExpansionStack) &&
        contribution.ValueOrigins.All(origin => originProof(origin, contribution.ExpansionStack)) &&
        // Unit/event owners omit the optional value-stack cache. Their exact
        // literal value spans and full assignment ancestry remain mandatory.
        (contribution.ValueExpansionStack.Count == 0 ||
            SameEventFrameChain(contribution.ValueExpansionStack, contribution.ExpansionStack));

    private static bool SameEventFrameChain(IReadOnlyList<WmlStaticExpansionFrame> left,
        IReadOnlyList<WmlStaticExpansionFrame> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.MacroName == pair.Second.MacroName && SameRange(pair.First.Definition, pair.Second.Definition) &&
            pair.First.InvocationOrigins.Count == pair.Second.InvocationOrigins.Count &&
            pair.First.InvocationOrigins.Zip(pair.Second.InvocationOrigins).All(origins => SameRange(origins.First, origins.Second)) &&
            SameParameters(pair.First.RequiredParameters, pair.Second.RequiredParameters) &&
            SameParameters(pair.First.OptionalParameters, pair.Second.OptionalParameters));

    private static bool SameParameters(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);
}