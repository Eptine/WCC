using System.Diagnostics.CodeAnalysis;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Physical syntax captured from the exact source revision, never write authority.</summary>
internal sealed class WccScenarioCompositeAssignment
{
    private WccScenarioCompositeAssignment(WmlStaticSource source, WmlMultipleAssignment syntax)
    {
        Source = source;
        Syntax = syntax;
        Assignment = new(source, syntax.StartOffset, syntax.SuffixOffset);
        var start = syntax.StartOffset;
        while (start > 0 && source.Text[start - 1] is not ('\r' or '\n')) start--;
        var end = syntax.EndOffsetExclusive;
        if (end < source.Text.Length && source.Text[end] == '\r') end++;
        if (end < source.Text.Length && source.Text[end] == '\n') end++;
        Region = new(source, start, end - start);
    }

    internal WmlStaticSource Source { get; }
    internal WmlMultipleAssignment Syntax { get; }
    internal WmlStaticSourceRange Assignment { get; }
    internal WmlStaticSourceRange Region { get; }
    internal IReadOnlyList<string> Keys => Syntax.Keys;
    internal WmlStaticSourceRange Value(int index) =>
        new(Source, Syntax.StartOffset + Syntax.Components[index].ValueOffset, Syntax.Components[index].ValueLength);
    internal bool IsImplicit(int index) => Syntax.Components[index].IsImplicitEmpty;
    internal int IndexOf(string key)
    {
        for (var index = 0; index < Keys.Count; index++) if (Keys[index] == key) return index;
        return -1;
    }

    internal static bool TryCapture(WmlStaticScalarContribution contribution, string key, WmlStaticSource source,
        [NotNullWhen(true)] out WccScenarioCompositeAssignment? assignment)
    {
        assignment = null;
        if (contribution.CompositeAssignmentKeys.Count < 2
            || contribution.CompositeAssignmentKeys.Distinct(StringComparer.Ordinal).Count() != contribution.CompositeAssignmentKeys.Count)
            return false;
        var heads = contribution.AssignmentOrigins.Where(origin =>
                WccScenarioFieldRouteAnalyzer.SameSource(origin.Source, source)
                && contribution.ExpansionStack.All(frame => !Inside(origin, frame.Definition)))
            .OrderBy(origin => origin.StartOffset);
        foreach (var head in heads)
        {
            if (!WmlMultipleAssignmentSyntax.TryReadAt(source.Text, head.StartOffset, out var syntax, out _)
                || !syntax!.Keys.SequenceEqual(contribution.CompositeAssignmentKeys, StringComparer.Ordinal)) continue;
            var captured = new WccScenarioCompositeAssignment(source, syntax);
            var index = captured.IndexOf(key);
            if (index < 0) continue;
            var value = captured.Value(index);
            if (value.Text.Length != 0 && (UnsupportedBoundary(value.Text[0]) || UnsupportedBoundary(value.Text[^1]))) continue;
            // A preprocessor expansion can introduce commas before this physical
            // component. Its output may therefore belong to another native key.
            // Keep every top-level value invocation and every literal origin in
            // this component; an authored index alone supplies no field mapping.
            if (captured.IsImplicit(index) && contribution.ValueOutputLength != 0
                || contribution.ValueOutputLength != 0 && contribution.ValueOrigins.Count == 0
                || contribution.ValueExpansionStack.SelectMany(frame => frame.InvocationOrigins)
                    .Any(invocation => WccScenarioFieldRouteAnalyzer.SameSource(invocation.Source, source)
                        && contribution.ValueExpansionStack.All(parent => !Inside(invocation, parent.Definition))
                        && !Inside(invocation, value))
                || contribution.ValueOrigins.Any(origin => !Inside(origin, value)
                    && !contribution.ValueExpansionStack.Any(frame => Inside(origin, frame.Definition)
                        && contribution.ValueExpansionStack.Any(root => root.InvocationOrigins.Any(invocation => Inside(invocation, value))))))
                continue;
            assignment = captured;
            return true;
        }
        return false;
    }

    internal bool ContainsValue(string key, WmlStaticSourceRange range)
    {
        var index = IndexOf(key);
        return index >= 0 && Inside(range, Value(index));
    }

    internal bool HasExclusiveComponent(WmlStaticScalarOwner owner, string key, WmlStaticScalarContribution contribution)
    {
        var value = Value(IndexOf(key));
        if (value.Length == 0) return true;
        return !owner.Fields.Where(field => field.Key != key).SelectMany(field => field.Value.Contributions)
            .Where(other => other.AssignmentOutputOffset == contribution.AssignmentOutputOffset
                && other.AssignmentOutputLength == contribution.AssignmentOutputLength)
            .Any(other => other.ValueOrigins.Any(origin => Overlaps(origin, value))
                || other.ValueExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin => Overlaps(origin, value))));
    }

    private static bool Overlaps(WmlStaticSourceRange left, WmlStaticSourceRange right) =>
        WccScenarioFieldRouteAnalyzer.SameSource(left.Source, right.Source)
        && left.Length > 0 && right.Length > 0
        && left.StartOffset < right.StartOffset + right.Length && right.StartOffset < left.StartOffset + left.Length;
    internal bool Same(WccScenarioCompositeAssignment other) =>
        WccScenarioDifficultyFieldPlanner.SameSource(Source, other.Source)
        && WccScenarioDifficultyFieldPlanner.SameRange(Assignment, other.Assignment)
        && WccScenarioDifficultyFieldPlanner.SameRange(Region, other.Region)
        && Keys.SequenceEqual(other.Keys, StringComparer.Ordinal)
        && Syntax.Components.Select((component, index) =>
            component.IsImplicitEmpty == other.Syntax.Components[index].IsImplicitEmpty
            && WccScenarioDifficultyFieldPlanner.SameRange(Value(index), other.Value(index))).All(same => same);

    internal string MissingPrefix(int index)
    {
        if (!IsImplicit(index)) return "";
        var last = Syntax.Components.Select((component, position) => (component, position))
            .Where(item => !item.component.IsImplicitEmpty).Select(item => item.position).DefaultIfEmpty(-1).Max();
        return new string(',', index - last);
    }

    internal static bool ComposeEmptyInsertions(IReadOnlyList<WccScenarioFieldCandidate> edits,
        [NotNullWhen(true)] out string? replacement, out IReadOnlyDictionary<string, int> locations)
    {
        replacement = null;
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        locations = offsets;
        if (edits.Count == 0 || edits[0].Route.CompositeAssignment is not { } group
            || edits.Any(edit => edit.Operation != WmlFieldOperation.Set || edit.Route.Kind != WccScenarioFieldRouteKind.DirectToken
                || edit.ReplacementLength != 0 || edit.Route.CompositeAssignment is not { } other || !group.Same(other)
                || edit.RequestedSourceValue is null)) return false;
        var ordered = edits.OrderBy(edit => edit.Route.CompositeComponentIndex).ToArray();
        if (ordered.Select(edit => edit.Route.CompositeComponentIndex).Distinct().Count() != ordered.Length) return false;
        var cursor = group.Syntax.Components.Select((component, position) => (component, position))
            .Where(item => !item.component.IsImplicitEmpty).Select(item => item.position).DefaultIfEmpty(-1).Max();
        var builder = new System.Text.StringBuilder();
        foreach (var edit in ordered)
        {
            var index = edit.Route.CompositeComponentIndex;
            if (index < 0 || index >= group.Keys.Count || group.Value(index).Length != 0
                || group.Value(index).StartOffset != edit.ReplacementStart) return false;
            builder.Append(',', Math.Max(0, index - cursor));
            offsets[WccScenarioFieldBatchCandidatePlanner.GetSourceEditIdentity(edit.Route)] = builder.Length;
            builder.Append(edit.RequestedSourceValue);
            cursor = index;
        }
        replacement = builder.ToString();
        return true;
    }

    private static bool UnsupportedBoundary(char value) => char.IsWhiteSpace(value) && value is not (' ' or '\t' or '\r' or '\n');

    private static bool Inside(WmlStaticSourceRange range, WmlStaticSourceRange container) =>
        WccScenarioFieldRouteAnalyzer.SameSource(range.Source, container.Source)
        && range.StartOffset >= container.StartOffset && range.StartOffset + range.Length <= container.StartOffset + container.Length;
}
