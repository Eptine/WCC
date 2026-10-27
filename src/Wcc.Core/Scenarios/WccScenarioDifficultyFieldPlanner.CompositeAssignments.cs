using System.Diagnostics.CodeAnalysis;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioDifficultyFieldPlanner
{
    internal static bool ExactInitialComponentContribution(WmlStaticScalarContribution contribution, string key,
        WmlStaticSource source, [NotNullWhen(true)] out WmlStaticSourceRange? assignment,
        [NotNullWhen(true)] out WmlStaticSourceRange? value)
    {
        if (ExactLocalContribution(contribution, source, out assignment, out value)) return true;
        assignment = null; value = null;
        if (!WccScenarioCompositeAssignment.TryCapture(contribution, key, source, out var composite)
            || contribution.AssignmentOrigin is not { } physical || contribution.AssignmentOrigins.Count != 1
            || !SameRange(physical, contribution.AssignmentOrigins[0]) || !SameRange(physical, composite.Assignment)
            || !OutsideLiteralExpansionDefinitions(physical, contribution.ExpansionStack)
            || !contribution.ValueMappingIsExact || physical.Length != contribution.AssignmentOutputLength)
            return false;
        var component = composite.Value(composite.IndexOf(key));
        var empty = component.Length == 0 && contribution.ValueOutputLength == 0 && contribution.ValueOrigins.Count == 0;
        if (!empty && (contribution.ValueOrigins.Count != 1 || !SameRange(component, contribution.ValueOrigins[0]))) return false;
        if (component.Length != contribution.ValueOutputLength
            || !OutsideLiteralExpansionDefinitions(component, contribution.ValueExpansionStack)
            || !empty && !SameLiteralExpansionFrames(contribution.ExpansionStack, contribution.ValueExpansionStack))
            return false;
        assignment = physical; value = component; return true;
    }

    private static WccScenarioCompositeAssignment? ComponentContract(WmlStaticScalarContribution contribution,
        string key, WmlStaticSource source) => contribution.CompositeAssignmentKeys.Count != 0
        && WccScenarioCompositeAssignment.TryCapture(contribution, key, source, out var composite) ? composite : null;

    internal static void RequireLiteralComposite(WmlStaticScalarOwner owner, WccScenarioCompositeAssignment composite)
    {
        var line = WholeStatementLine(composite.Source, composite.Assignment);
        var suffix = composite.Source.Text.AsSpan(composite.Assignment.StartOffset + composite.Assignment.Length,
            line.StartOffset + line.Length - composite.Assignment.StartOffset - composite.Assignment.Length).Trim();
        if (!IsOwnLine(composite.Source.Text, composite.Assignment.StartOffset)
            || !SameRange(line, composite.Region) || !suffix.IsEmpty && suffix[0] != '#')
            throw new InvalidOperationException("Composite normalization requires a standalone assignment and its own trailing comment.");
        foreach (var key in composite.Keys)
        {
            var component = composite.Value(composite.IndexOf(key));
            if (!WccScenarioFieldValueCodec.IsSupportedLiteralWml(component.Text)
                || component.Text.Length != 0 && (UnsupportedCompositeBoundary(component.Text[0]) || UnsupportedCompositeBoundary(component.Text[^1]))
                || !owner.Fields.TryGetValue(key, out var field))
                throw new InvalidOperationException("Every retained composite companion needs exact supported literal source syntax.");
            var matches = field.Contributions.Count(contribution =>
                ExactInitialComponentContribution(contribution, key, composite.Source, out var assignment, out var value)
                && SameRange(assignment, composite.Assignment) && SameRange(value, component));
            if (matches != 1)
                throw new InvalidOperationException("A composite companion has no unique exact physical contribution.");
        }
    }

    private static bool UnsupportedCompositeBoundary(char value) =>
        char.IsWhiteSpace(value) && value is not (' ' or '\t' or '\r' or '\n');

    private static bool SameDifficultyAssignments(IReadOnlyList<WccScenarioDifficultyFieldAssignment> left,
        IReadOnlyList<WccScenarioDifficultyFieldAssignment> right) =>
        left.Count == right.Count && left.Where((assignment, index) =>
            assignment.Define != right[index].Define || assignment.Literal != right[index].Literal
            || !SameRange(assignment.Assignment, right[index].Assignment)
            || !SameRange(assignment.Value, right[index].Value) || !SameRange(assignment.Region, right[index].Region)
            || !SameCompositeContract(assignment.CompositeAssignment, right[index].CompositeAssignment)).Any() == false;

    private static bool SameCompositeContract(WccScenarioCompositeAssignment? left, WccScenarioCompositeAssignment? right) =>
        left is null || right is null ? left is null && right is null : left.Same(right);

    private static IReadOnlyList<WccScenarioCompositeAssignment> SharedCompositeAssignments(
        IReadOnlyList<WccScenarioDifficultyFieldEdit> edits) => Array.AsReadOnly(edits
        .SelectMany(edit => edit.Snapshot.Layout!.Assignments).Where(assignment => assignment.Define is null)
        .Select(assignment => assignment.CompositeAssignment).OfType<WccScenarioCompositeAssignment>()
        .DistinctBy(composite => (composite.Assignment.StartOffset, composite.Assignment.Length))
        .OrderBy(composite => composite.Assignment.StartOffset).ToArray());
}
