using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static bool SameUnrequestedCompositeHistory(WmlStaticScalarOwner oldOwner,
        WmlStaticScalarOwner newOwner, string key, WmlStaticScalarField? oldField, WmlStaticScalarField? newField,
        WccScenarioFieldSourceChange change, string beforeText, string afterText,
        IReadOnlyList<ReviewedFieldTarget> targets)
    {
        var hasComposite = oldField?.Contributions.Any(contribution => contribution.CompositeAssignmentKeys.Count != 0) == true
            || newField?.Contributions.Any(contribution => contribution.CompositeAssignmentKeys.Count != 0) == true;
        if (!hasComposite) return true;
        if (oldField is null || newField is null || oldField.Contributions.Count != newField.Contributions.Count) return false;
        for (var index = 0; index < oldField.Contributions.Count; index++)
        {
            var before = oldField.Contributions[index]; var after = newField.Contributions[index];
            if (before.CompositeAssignmentKeys.Count == 0 && after.CompositeAssignmentKeys.Count == 0) continue;
            // Reading a native tuple does not authorize an incidental event
            // consumer, including a write hidden by a later winning assignment.
            // Other single-assignment event entries retain their existing rules.
            if (before.ValueMappingIsExact != after.ValueMappingIsExact
                || !before.CompositeAssignmentKeys.SequenceEqual(after.CompositeAssignmentKeys, StringComparer.Ordinal)
                || !SameAssignmentOutput(before, after, beforeText, afterText)
                    && !SameCompositeCompanionAssignment(oldOwner, newOwner, before, after, beforeText, afterText, targets)
                || !SameMappedOrigins(before.AssignmentOrigins, after.AssignmentOrigins, change)
                    && !SameCompositeCompanionInsertionOrigins(oldOwner, newOwner, key, before, after, change,
                        beforeText, afterText, targets)
                || !SameMappedOrigins(before.ValueOrigins, after.ValueOrigins, change)
                || !SameMappedFrames(before.ExpansionStack, after.ExpansionStack, change)
                || !SameMappedFrames(before.ValueExpansionStack, after.ValueExpansionStack, change)) return false;
        }
        return true;
    }
    private static bool SameCompositeCompanionAssignment(WmlStaticScalarOwner oldOwner,
        WmlStaticScalarOwner newOwner, WmlStaticScalarContribution before, WmlStaticScalarContribution after,
        string beforeText, string afterText, IReadOnlyList<ReviewedFieldTarget> targets)
    {
        if (before.CompositeAssignmentKeys.Count < 2
            || before.CompositeAssignmentKeys.Distinct(StringComparer.Ordinal).Count() != before.CompositeAssignmentKeys.Count
            || !before.CompositeAssignmentKeys.SequenceEqual(after.CompositeAssignmentKeys, StringComparer.Ordinal)
            || before.AssignmentOutputOffset < 0 || before.AssignmentOutputLength < 0
            || before.AssignmentOutputOffset > beforeText.Length - before.AssignmentOutputLength
            || after.AssignmentOutputOffset < 0 || after.AssignmentOutputLength < 0
            || after.AssignmentOutputOffset > afterText.Length - after.AssignmentOutputLength)
            return false;
        var oldText = beforeText.Substring(before.AssignmentOutputOffset, before.AssignmentOutputLength);
        var newText = afterText.Substring(after.AssignmentOutputOffset, after.AssignmentOutputLength);
        if (!WmlMultipleAssignmentSyntax.TryRead(oldText, out var oldSyntax, out _)
            || !WmlMultipleAssignmentSyntax.TryRead(newText, out var newSyntax, out _)
            || !oldSyntax.Keys.SequenceEqual(before.CompositeAssignmentKeys, StringComparer.Ordinal)
            || !oldSyntax.Keys.SequenceEqual(newSyntax.Keys, StringComparer.Ordinal)) return false;
        var changed = targets.Where(target => ReferenceEquals(target.OldOwner, oldOwner)
                && ReferenceEquals(target.NewOwner, newOwner)
                && oldOwner.Fields.TryGetValue(target.Route.Key, out var oldField)
                && newOwner.Fields.TryGetValue(target.Route.Key, out var newField)
                && oldField.Contributions.Any(contribution => SameStatement(contribution, before))
                && newField.Contributions.Any(contribution => SameStatement(contribution, after)))
            .Select(target => target.Route.Key).ToHashSet(StringComparer.Ordinal);
        if (changed.Count == 0) return false;
        for (var index = 0; index < oldSyntax.Components.Count; index++)
        {
            if (changed.Contains(oldSyntax.Keys[index])) continue;
            var old = oldSyntax.Components[index]; var current = newSyntax.Components[index];
            if (!oldText.AsSpan(old.ValueOffset, old.ValueLength)
                .SequenceEqual(newText.AsSpan(current.ValueOffset, current.ValueLength))) return false;
        }
        // Only the assignment-output comparison is adapted. Exact value origins,
        // mapped assignment origins, ordered histories and both frame chains
        // remain independently required by the caller for this companion.
        return true;
    }

    private static bool SameCompositeCompanionInsertionOrigins(WmlStaticScalarOwner oldOwner,
        WmlStaticScalarOwner newOwner, string key, WmlStaticScalarContribution before,
        WmlStaticScalarContribution after, WccScenarioFieldSourceChange change,
        string beforeText, string afterText, IReadOnlyList<ReviewedFieldTarget> targets)
    {
        if (before.AssignmentOrigins.Count != 1 || after.AssignmentOrigins.Count != 1
            || !SameCompositeCompanionAssignment(oldOwner, newOwner, before, after, beforeText, afterText, targets)) return false;
        var old = before.AssignmentOrigins[0]; var current = after.AssignmentOrigins[0];
        if (!WccScenarioCompositeAssignment.TryCapture(before, key, old.Source, out var physical)
            || !WccScenarioDifficultyFieldPlanner.SameRange(old, physical.Assignment)
            || old.Text != beforeText.Substring(before.AssignmentOutputOffset, before.AssignmentOutputLength)
            || current.Text != afterText.Substring(after.AssignmentOutputOffset, after.AssignmentOutputLength)) return false;
        try { WccScenarioDifficultyFieldPlanner.RequireLiteralComposite(oldOwner, physical); }
        catch (InvalidOperationException) { return false; }
        var seam = old.StartOffset + old.Length;
        var insertions = targets.Where(target => ReferenceEquals(target.OldOwner, oldOwner)
                && ReferenceEquals(target.NewOwner, newOwner) && target.Edit is { } edit
                && edit.Operation == WmlFieldOperation.Set && edit.Route.Kind == WccScenarioFieldRouteKind.DirectToken
                && edit.ReplacementStart == seam && edit.ReplacementLength == 0 && edit.RequestedSourceValue is not null)
            .Select(target => target.Edit!).GroupBy(edit => edit.Route.Key, StringComparer.Ordinal).ToArray();
        if (insertions.Length == 0) return false;
        var ordered = new List<(int Index, string Value)>();
        foreach (var group in insertions)
        {
            var index = physical.IndexOf(group.Key);
            if (index < 0 || physical.Value(index).Length != 0 || physical.Value(index).StartOffset != seam
                || group.Select(edit => edit.RequestedSourceValue).Distinct(StringComparer.Ordinal).Count() != 1
                || group.Any(edit => edit.Route.Target is not { } target
                    || !WccScenarioDifficultyFieldPlanner.SameRange(target, physical.Value(index)))) return false;
            ordered.Add((index, group.First().RequestedSourceValue!));
        }
        var cursor = physical.Syntax.Components.Select((component, index) => (component, index))
            .Where(item => !item.component.IsImplicitEmpty).Select(item => item.index).DefaultIfEmpty(-1).Max();
        var insertion = new System.Text.StringBuilder();
        foreach (var item in ordered.OrderBy(item => item.Index))
        {
            insertion.Append(',', Math.Max(0, item.Index - cursor)).Append(item.Value);
            cursor = item.Index;
        }
        // MapRange deliberately excludes a zero-length insertion exactly at a
        // range's closing boundary. Reconstruct only this requested native tuple
        // suffix; every other range, value origin, frame and history stays exact.
        var mapped = change.MapRange(old);
        return mapped is not null && WccScenarioFieldRouteAnalyzer.SameSource(mapped.Source, current.Source)
            && mapped.StartOffset == current.StartOffset && mapped.Length + insertion.Length == current.Length
            && current.Text == mapped.Text + insertion;
    }
    private static bool SameStatement(WmlStaticScalarContribution left, WmlStaticScalarContribution right) =>
        left.AssignmentOutputOffset == right.AssignmentOutputOffset && left.AssignmentOutputLength == right.AssignmentOutputLength;
    private static bool IsFreshKnownEmptyComposite(WmlStaticScalarOwner owner, string key, WmlStaticSource source)
    {
        if (owner.ResolveField(key).State != WmlStaticScalarResolutionState.Known
            || !owner.Fields.TryGetValue(key, out var field) || field.ValueText != "" || field.Contributions.Count != 1
            || field.Contributions[0].ValueOutputLength != 0
            || !WccScenarioCompositeAssignment.TryCapture(field.Contributions[0], key, source, out var composite)
            || composite.Value(composite.IndexOf(key)).Length != 0
            || !WccScenarioDifficultyFieldPlanner.ExactInitialComponentContribution(field.Contributions[0], key,
                source, out _, out _)) return false;
        try { WccScenarioDifficultyFieldPlanner.RequireLiteralComposite(owner, composite); return true; }
        catch (InvalidOperationException) { return false; }
    }
    private static void ValidateCompositeFieldContext(WccScenarioFieldCandidate edit,
        WmlStaticScalarOwner before, WmlStaticScalarOwner after)
    {
        if (edit.Route.CompositeAssignment is not { } physical)
        {
            if (edit.Route.Kind == WccScenarioFieldRouteKind.ScalarArgument
                && edit.Route.Owner.Fields.TryGetValue(edit.Route.Key, out var selected)
                && selected.WinningContribution is { CompositeAssignmentKeys.Count: > 1 }
                && edit.Route.Target is { } argument)
            {
                ValidateGeneratedCompositeArgument(before, edit.Route.Key, argument);
                if (edit.ProposedValueOrigin is { } proposedArgument)
                    ValidateGeneratedCompositeArgument(after, edit.Route.Key, proposedArgument);
            }
            return;
        }
        if (!before.Fields.TryGetValue(edit.Route.Key, out var oldField)) return;
        foreach (var contribution in oldField.Contributions.Where(contribution =>
                     contribution.AssignmentOrigins.Any(origin => CompositeRangesTouch(origin, physical.Assignment))))
            if (!WccScenarioCompositeAssignment.TryCapture(contribution, edit.Route.Key, edit.Route.Source, out var actual)
                || !actual.Same(physical) || !actual.HasExclusiveComponent(before, edit.Route.Key, contribution))
                throw new InvalidOperationException("The requested composite component has no complete physical field mapping in every active context.");
        if (edit.ProposedValueOrigin is not { } proposed || !after.Fields.TryGetValue(edit.Route.Key, out var newField)) return;
        foreach (var contribution in newField.Contributions.Where(contribution => contribution.CompositeAssignmentKeys.Count != 0
                     && (contribution.AssignmentOrigins.Any(origin => CompositeRangesTouch(origin, proposed))
                         || contribution.ValueOrigins.Any(origin => CompositeRangesTouch(origin, proposed))
                         || contribution.ValueExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin => CompositeRangesTouch(origin, proposed))))))
            if (!WccScenarioCompositeAssignment.TryCapture(contribution, edit.Route.Key, edit.CandidateSource, out var actual)
                || !actual.HasExclusiveComponent(after, edit.Route.Key, contribution))
                throw new InvalidOperationException("The proposed composite value changes native component arity or loses complete field ancestry.");
    }

    private static void ValidateGeneratedCompositeArgument(WmlStaticScalarOwner owner, string key, WmlStaticSourceRange argument)
    {
        if (!owner.Fields.TryGetValue(key, out var field)) return;
        foreach (var contribution in field.Contributions.Where(contribution => contribution.CompositeAssignmentKeys.Count > 1
                     && (contribution.ValueOrigins.Any(origin => CompositeRangesTouch(origin, argument))
                         || contribution.ValueExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin => CompositeRangesTouch(origin, argument))))))
        {
            if (!contribution.ValueMappingIsExact
                || contribution.CompositeAssignmentKeys.Distinct(StringComparer.Ordinal).Count() != contribution.CompositeAssignmentKeys.Count)
                throw new InvalidOperationException("The generated composite argument has no unique exact field component in every context.");
            // Literal required arguments must supply the complete native field,
            // including in contexts whose preceding macro output changes arity.
            if (WccScenarioFieldValueCodec.IsSupportedLiteralWml(argument.Text)
                && (contribution.ValueOrigins.Count != 1
                    || !WccScenarioDifficultyFieldPlanner.SameRange(contribution.ValueOrigins[0], argument)
                    || contribution.ValueOutputLength != argument.Length))
                throw new InvalidOperationException("A generated composite field consumes only part of its local argument or combines another component.");
        }
    }
    private static bool CompositeRangesTouch(WmlStaticSourceRange left, WmlStaticSourceRange right) =>
        WccScenarioFieldRouteAnalyzer.SameSource(left.Source, right.Source)
        && (left.Length == 0 || right.Length == 0
            ? left.StartOffset <= right.StartOffset + right.Length && right.StartOffset <= left.StartOffset + left.Length
            : left.StartOffset < right.StartOffset + right.Length && right.StartOffset < left.StartOffset + left.Length);
}
