using System.Globalization;

namespace Wcc.Core.Wml;

public enum WmlStaticActionBranchState { Unconditional, ProvenExcluded, NeedsRuntimeContext }

/// <summary>
/// Pure, bounded observations of evaluated action branches. This is not a runtime
/// evaluator or variable store: only an adjacent literal setter and a single string
/// equality condition can establish a branch. Hosts still verify source freshness.
/// </summary>
public sealed class WmlStaticActionBranchObservation
{
    private readonly WmlStaticEvaluationResult _evaluation;
    private readonly bool _structureKnown;
    private readonly HashSet<WmlStaticSource> _sources;
    private readonly Dictionary<WmlStaticScalarOwner, WmlStaticScalarOwner?> _previous = [];
    private readonly Dictionary<WmlStaticScalarOwner, bool?> _conditions = [];

    public WmlStaticActionBranchObservation(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(projection);
        cancellationToken.ThrowIfCancellationRequested();
        _evaluation = evaluation;
        _structureKnown = projection.Diagnostics.Count == 0;
        _sources = evaluation.Sources.ToHashSet();
        foreach (var parent in projection.Owners)
        {
            WmlStaticScalarOwner? previous = null;
            foreach (var child in parent.DirectChildren)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_previous.TryAdd(child, previous)) _structureKnown = false;
                previous = child;
            }
        }
    }

    public WmlStaticActionBranchState Observe(WmlStaticScalarOwner action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var actionScopeKnown = IsKnownActionScope(action, requirePairedPath: true);
        var uncertain = false;
        for (var child = action; child.Parent is { } parent; child = parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parent.TagName == "if")
            {
                if (!actionScopeKnown) { uncertain = true; continue; }
                if (child.TagName is not ("then" or "else")) { uncertain = true; continue; }
                var condition = Condition(parent, cancellationToken);
                if (condition is null) { uncertain = true; continue; }
                if (condition.Value != (child.TagName == "then")) return WmlStaticActionBranchState.ProvenExcluded;
            }
            else if (parent.TagName is "while" or "switch" or "foreach" or "repeat" or "elseif") uncertain = true;
        }
        return uncertain ? WmlStaticActionBranchState.NeedsRuntimeContext : WmlStaticActionBranchState.Unconditional;
    }

    private bool? Condition(WmlStaticScalarOwner owner, CancellationToken cancellationToken)
    {
        if (_conditions.TryGetValue(owner, out var cached)) return cached;
        var result = Inspect();
        _conditions.Add(owner, result);
        return result;

        bool? Inspect()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_evaluation.IsComplete || !_structureKnown || !Paired(owner) ||
                owner.Parent is not { } actionParent || !Paired(actionParent) || owner.Fields.Count != 0 ||
                owner.DirectChildren.Count(child => child.TagName == "variable") != 1 ||
                owner.DirectChildren.Count(child => child.TagName == "then") > 1 ||
                owner.DirectChildren.Count(child => child.TagName == "else") > 1 ||
                owner.DirectChildren.Any(child => child.TagName is not ("variable" or "then" or "else"))) return null;
            var variable = owner.DirectChildren.Single(child => child.TagName == "variable");
            var operation = variable.Fields.ContainsKey("equals") ? "equals" : "not_equals";
            if (!Paired(variable) || variable.DirectChildren.Count != 0 || variable.Fields.Count != 2 ||
                !Literal(variable, "name", out var name) || !SimpleName(name) ||
                !Literal(variable, operation, out var expected) || !PlainString(expected)) return null;
            if (!_previous.TryGetValue(owner, out var setter) || setter is null || setter.TagName != "set_variable" ||
                !Paired(setter) || setter.DirectChildren.Count != 0 || setter.Fields.Count != 2 ||
                !Literal(setter, "name", out var assignedName) || assignedName != name ||
                !Literal(setter, "value", out var assigned) || !PlainString(assigned)) return null;
            return operation == "equals" ? assigned == expected : assigned != expected;
        }
    }

    private bool Literal(WmlStaticScalarOwner owner, string key, out string value)
    {
        value = "";
        if (owner.ResolveField(key).State != WmlStaticScalarResolutionState.Known ||
            !owner.Fields.TryGetValue(key, out var field) || !WmlUnitReferenceProjection.Mapped(field) ||
            field.Contributions.LastOrDefault() is not { } contribution || field.ValueText is null ||
            contribution.ValueOutputOffset < 0 || contribution.ValueOutputLength != field.ValueText.Length ||
            contribution.ValueOutputOffset > _evaluation.Text.Length - contribution.ValueOutputLength ||
            !_evaluation.Text.AsSpan(contribution.ValueOutputOffset, contribution.ValueOutputLength).SequenceEqual(field.ValueText) ||
            contribution.ValueOrigins.Count == 0 || contribution.ValueOrigins.Any(origin => !_sources.Contains(origin.Source) ||
                origin.StartOffset < 0 || origin.Length <= 0 || origin.StartOffset > origin.Source.Text.Length - origin.Length)) return false;
        var decoded = WmlResourceValueTextDecoder.Decode(field.ValueText);
        if (!decoded.IsDecoded || !WmlUnitReferenceProjection.IsStaticName(decoded.ValueText)) return false;
        value = decoded.ValueText;
        return true;
    }

    private static bool Paired(WmlStaticScalarOwner owner) => !owner.IsAmended && !owner.HasUnresolvedContributions &&
        owner.OpeningOutputOffset.HasValue && owner.ClosingOutputOffset.HasValue;
    private static bool SimpleName(string name) => name.Length != 0 &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool PlainString(string value) => value.Length != 0 && value == value.Trim() &&
        !new[] { "yes", "no", "true", "false", "on", "off" }.Contains(value, StringComparer.OrdinalIgnoreCase) &&
        !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    // Scope only establishes that this is an action-bearing path. It does not
    // infer execution or schema validity from an arbitrary tag's name.
    internal static bool IsKnownActionScope(WmlStaticScalarOwner action, bool requirePairedPath = false)
    {
        var inEvent = false;
        for (var parent = action.Parent; parent is not null; parent = parent.Parent)
        {
            if (requirePairedPath && (parent.IsAmended || !parent.OpeningOutputOffset.HasValue || !parent.ClosingOutputOffset.HasValue)) return false;
            if (parent.TagName == "side") return action.Parent == parent && parent.Parent is { TagName: "scenario", Parent: null };
            if (parent.TagName == "scenario") return inEvent && parent.Parent is null;
            if (parent.TagName == "unit_type") return inEvent && parent.Parent is { TagName: "units", Parent: null };
            if (parent.TagName == "event") inEvent = true;
            if (parent.TagName is not ("event" or "then" or "else" or "elseif" or "if" or
                "command" or "option" or "message" or "do" or "while" or "repeat" or
                "foreach" or "switch" or "case")) return false;
        }
        return false;
    }
}
