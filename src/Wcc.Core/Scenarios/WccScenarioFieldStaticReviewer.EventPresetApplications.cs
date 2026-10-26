using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateAppliedEventPreset(WmlStaticScalarOwner owner, WccCampaignLocalPresetEntry entry,
        WmlStaticSourceRange insertion, WmlStaticEvaluationResult evaluation, WccScenarioFieldSourceChange change,
        WmlEvaluationContext context, WmlCampaignEvaluationPlan? plan, WmlStaticScalarProjection projection)
    {
        if (!entry.IsSupported || entry.Role != WccCampaignLocalPresetRole.Event || entry.Payload?.EventSeed is not { } requested)
            throw new InvalidOperationException("The new event invocation needs its exact supported saved Event preset.");
        var binding = BindProposedPreset(entry, change);
        requested = binding.Payload.EventSeed!;
        var definition = binding.Definition;
        var body = binding.Body;
        var calls = evaluation.Invocations.Where(call => call.Name == entry.Name && call.InvocationOrigins.Count != 0
            && call.InvocationOrigins.All(origin => InsideProposedScalar(origin, insertion))).ToArray();
        if (calls.Length != 1 || calls[0].Outcome != WmlStaticInvocationOutcome.Expanded || !SameRange(calls[0].Definition, definition)
            || calls[0].InvocationOrigins.Count != 1 || calls[0].InvocationOrigins[0].Text != "{" + entry.Name + "}")
            throw new InvalidOperationException("The new event does not invoke its exact selected shared definition once in every context.");
        var invocation = calls[0].InvocationOrigins[0];
        var hasUnits = requested.Graph.Actions.Any(action => action.Kind == WccScenarioEventActionKind.SpawnUnit);
        if (hasUnits)
        {
            if (plan is null) throw new InvalidOperationException("Event Spawn review needs its exact declared campaign plan.");
            _ = ValidateEventGraphUnits(owner, requested.Graph, body, evaluation, change, context, plan,
                (origin, frames) => origin is not null && InsideProposedScalar(origin, body) && Frames(frames), projection);
        }
        var pending = new Stack<WmlStaticScalarOwner>(); pending.Push(owner);
        while (pending.TryPop(out var current))
        {
            if (hasUnits && ReferenceEquals(current.Parent, owner) && current.TagName == "unit") continue;
            if (current.IsAmended || current.HasUnresolvedContributions || current.OpeningOrigin is not { } opening
                || current.ClosingOrigin is not { } closing || !InsideProposedScalar(opening, body) || !InsideProposedScalar(closing, body)
                || !Frames(current.OpeningExpansionStack) || !Frames(current.ClosingExpansionStack))
                throw new InvalidOperationException("An event preset owner has no exact boundary in the selected shared body.");
            foreach (var field in current.Fields.Values)
            {
                if (!field.IsKnown || field.Contributions.Count != 1)
                    throw new InvalidOperationException("An event preset field is unresolved or repeated.");
                var contribution = field.Contributions[0];
                // Event/action owners deliberately omit the optional value-stack cache.
                // Prove the complete assignment frame plus its exact mapped body values.
                if (contribution.AssignmentOrigin is not { } assignment || !InsideProposedScalar(assignment, body)
                    || contribution.AssignmentOrigins.Count != 1 || contribution.CompositeAssignmentKeys.Count != 0
                    || !contribution.ValueMappingIsExact || contribution.ValueOrigins.Count == 0
                    || contribution.ValueOrigins.Any(origin => !InsideProposedScalar(origin, body))
                    || !Frames(contribution.ExpansionStack))
                    throw new InvalidOperationException("An event preset field lacks its exact selected body contribution.");
            }
            foreach (var child in current.DirectChildren) pending.Push(child);
        }
        if (hasUnits) return;
        if (owner.OpeningOutputOffset is not { } start || owner.ClosingOutputOffset is not { } end || owner.ClosingOrigin is not { } close
            || start < 0 || end < start || end + close.Length > evaluation.Text.Length)
            throw new InvalidOperationException("The applied event has no complete captured output span.");
        var actual = WccScenarioEventGraphEditor.Capture(evaluation.Text[start..(end + close.Length)], null);
        if (plan is null) throw new InvalidOperationException("Event difficulty review needs its exact declared campaign plan.");
        var expected = WccScenarioEventGraphEditor.ProjectDifficulty(requested.Graph, context.DifficultyDefine,
            plan.Difficulties.Select(row => row.Define).ToArray());
        var expectedText = WccScenarioEventGraphEditor.Render(expected, indent: "", newline: "\n", unitPresets: null);
        var actualText = actual.Seed is { } seed ? WccScenarioEventGraphEditor.Render(seed.Graph, indent: "", newline: "\n", unitPresets: null) : null;
        if (!actual.CanEdit || !expectedText.IsSuccess || actualText?.IsSuccess != true || actualText.Text != expectedText.Text)
            throw new InvalidOperationException("The applied event does not preserve its whole requested trigger, filters and ordered action chain.");

        bool Frames(IReadOnlyList<WmlStaticExpansionFrame> frames)
        {
            var active = frames.Where(frame => frame.InvocationOrigins.Any(origin => InsideProposedScalar(origin, insertion))).ToArray();
            return active.Length == 1 && active[0].MacroName == entry.Name && SameRange(active[0].Definition, definition)
                && active[0].RequiredParameters is { Count: 0 } && active[0].OptionalParameters is { Count: 0 }
                && active[0].InvocationOrigins.Count == 1 && SameRange(active[0].InvocationOrigins[0], invocation);
        }
    }
}