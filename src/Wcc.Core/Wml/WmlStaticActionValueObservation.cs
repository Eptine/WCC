namespace Wcc.Core.Wml;

/// <summary>
/// A finite set of possible literal values, with captured navigation evidence. It
/// is neither a chosen runtime value nor permission to edit the variable's source.
/// Hosts must verify snapshot freshness before using this observation.
/// </summary>
public sealed class WmlStaticActionValueObservationResult
{
    internal WmlStaticActionValueObservationResult(string variableName, bool isRandom,
        IEnumerable<string> candidates, WmlStaticScalarOwner setter,
        WmlStaticScalarField setterValue, WmlStaticScalarContribution consumerContribution)
    {
        VariableName = variableName;
        IsRandom = isRandom;
        Candidates = Array.AsReadOnly(candidates.ToArray());
        Setter = setter;
        SetterValue = setterValue;
        ConsumerContribution = consumerContribution;
        SetterValueOrigins = Array.AsReadOnly(setterValue.Contributions[0].ValueOrigins.ToArray());
        SetterOrigins = Array.AsReadOnly(new[] { setter.OpeningOrigin!, setter.ClosingOrigin! });
    }

    public string VariableName { get; }
    public bool IsRandom { get; }
    public IReadOnlyList<string> Candidates { get; }
    public WmlStaticScalarOwner Setter { get; }
    public WmlStaticScalarField SetterValue { get; }
    public WmlStaticScalarContribution ConsumerContribution { get; }
    public IReadOnlyList<WmlStaticSourceRange> SetterValueOrigins { get; }
    public IReadOnlyList<WmlStaticSourceRange> SetterOrigins { get; }
}

/// <summary>
/// Pure bounded observation of an exact scalar-variable reference immediately
/// after a literal [set_variable]. It does not execute WML, track variable history,
/// choose random values or infer stored-unit/array members. Other expressions fail
/// closed. The existing scalar projection and write authority remain unchanged.
/// </summary>
public sealed class WmlStaticActionValueObservation
{
    private const int MaxValueCharacters = 16 * 1024;
    private const int MaxCandidates = 256;
    private const int MaxCandidateCharacters = 256;
    private const int MaxVariableCharacters = 128;
    private readonly WmlStaticEvaluationResult _evaluation;
    private readonly bool _structureKnown;
    private readonly HashSet<WmlStaticSource> _sources;
    private readonly HashSet<WmlStaticScalarOwner> _owners;
    private readonly Dictionary<WmlStaticScalarOwner, WmlStaticScalarOwner?> _previous = [];

    public WmlStaticActionValueObservation(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(projection);
        cancellationToken.ThrowIfCancellationRequested();
        _evaluation = evaluation;
        _structureKnown = projection.Diagnostics.Count == 0;
        _sources = evaluation.Sources.ToHashSet();
        _owners = projection.Owners.ToHashSet();
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

    public WmlStaticActionValueObservationResult? Observe(WmlStaticScalarOwner action,
        WmlStaticScalarField field, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(field);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_evaluation.IsComplete || !_structureKnown || !_owners.Contains(action) || !OriginalBoundaries(action) ||
            (action.IsAmended && action.TagName != "unit") ||
            !action.Fields.TryGetValue(field.Key, out var actualField) || !ReferenceEquals(actualField, field) ||
            !WmlStaticActionBranchObservation.IsKnownActionScope(action, requirePairedPath: true) ||
            action.Parent is not { } parent || parent.TagName == "side" || !Paired(parent) ||
            !Capture(field, cancellationToken, out var consumerValue, out var consumerContribution) ||
            !WithinOriginalBody(action, consumerContribution!) ||
            consumerValue.Length < 2 || consumerValue[0] != '$' || !SimpleName(consumerValue[1..])) return null;

        // The parent path must belong to the same evaluated projection. Merely
        // passing a same-named owner from another evaluation supplies no evidence.
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_owners.Contains(ancestor) || !Paired(ancestor) ||
                !AllowsDelayedSubstitution(ancestor, cancellationToken)) return null;
        }
        var variable = consumerValue[1..];
        if (!_previous.TryGetValue(action, out var setter) || setter is null || setter.Parent != parent ||
            setter.TagName != "set_variable" || !Paired(setter) || setter.Fields.Count != 2 ||
            setter.DirectChildren.Count != 0 || !setter.Fields.TryGetValue("name", out var nameField) ||
            !Capture(nameField, cancellationToken, out var name, out _) || !SimpleName(name) || name != variable) return null;
        var isRandom = setter.Fields.ContainsKey("rand");
        var valueKey = isRandom ? "rand" : "value";
        if (!setter.Fields.TryGetValue(valueKey, out var valueField) ||
            !Capture(valueField, cancellationToken, out var value, out _)) return null;
        var candidates = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        if (isRandom)
        {
            var start = 0;
            var entries = 0;
            for (var cursor = 0; cursor <= value.Length; cursor++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cursor < value.Length && value[cursor] != ',') continue;
                if (++entries > MaxCandidates) return null;
                var candidate = value[start..cursor].Trim();
                // Numeric rand items can describe ranges and require different
                // semantics. This observer only admits finite literal strings.
                if (!PlainCandidate(candidate) || !candidate.Any(char.IsAsciiLetter)) return null;
                if (unique.Add(candidate)) candidates.Add(candidate);
                start = cursor + 1;
            }
        }
        else
        {
            if (!PlainCandidate(value)) return null;
            candidates.Add(value);
        }
        return new(variable, isRandom, candidates, setter, valueField, consumerContribution!);
    }

    private bool AllowsDelayedSubstitution(WmlStaticScalarOwner owner, CancellationToken cancellationToken)
    {
        if (owner.TagName != "event" || owner.Parent is { TagName: "scenario", Parent: null } ||
            owner.Parent is { TagName: "unit_type", Parent: { TagName: "units", Parent: null } }) return true;

        // Nested delayed=no interpolates this event and every deeper generation
        // when the event is registered, before its own adjacent setter can run.
        // A deeper delayed=yes cannot undo that earlier substitution. Top-level
        // events ignore this flag. See EventWML / Multiple Nested Events.
        const string key = "delayed_variable_substitution";
        if (owner.ResolveField(key).State == WmlStaticScalarResolutionState.ProvenAbsent) return true;
        return owner.Fields.TryGetValue(key, out var field) &&
            Capture(field, cancellationToken, out var value, out _) && value is "yes" or "true";
    }

    private bool Capture(WmlStaticScalarField field, CancellationToken cancellationToken,
        out string value, out WmlStaticScalarContribution? contribution)
    {
        value = "";
        contribution = null;
        // A dynamic consumer and concatenated literal setter are deliberately
        // not promoted to Known in the strict scalar projection.
        if (field.ValueText is null || field.ValueText.Length > MaxValueCharacters || field.Contributions.Count != 1 ||
            field.Contributions[0] is not { ValueMappingIsExact: true, CompositeAssignmentKeys.Count: 0 } captured ||
            captured.ValueOutputOffset < 0 || captured.ValueOutputLength <= 0 || captured.ValueOutputLength != field.ValueText.Length ||
            captured.ValueOutputOffset > _evaluation.Text.Length - captured.ValueOutputLength ||
            !_evaluation.Text.AsSpan(captured.ValueOutputOffset, captured.ValueOutputLength).SequenceEqual(field.ValueText) ||
            captured.AssignmentOutputOffset < 0 || captured.AssignmentOutputLength <= 0 ||
            captured.AssignmentOutputOffset > captured.ValueOutputOffset ||
            captured.AssignmentOutputOffset > _evaluation.Text.Length - captured.AssignmentOutputLength ||
            captured.ValueOutputOffset + captured.ValueOutputLength > captured.AssignmentOutputOffset + captured.AssignmentOutputLength ||
            captured.ValueOrigins.Count == 0 || captured.AssignmentOrigins.Count == 0 ||
            captured.ValueOrigins.Any(origin => !Valid(origin)) || captured.AssignmentOrigins.Any(origin => !Valid(origin))) return false;
        var decoded = WmlResourceValueTextDecoder.Decode(field.ValueText, cancellationToken);
        if (!decoded.IsDecoded || decoded.ValueText.Length > MaxValueCharacters) return false;
        value = decoded.ValueText;
        contribution = captured;
        return true;
    }

    private bool Paired(WmlStaticScalarOwner owner) => !owner.IsAmended && OriginalBoundaries(owner);

    private bool OriginalBoundaries(WmlStaticScalarOwner owner) =>
        owner.OpeningOutputOffset is { } opening && owner.ClosingOutputOffset is { } closing && opening < closing &&
        owner.OpeningOrigin is { } openingOrigin && owner.ClosingOrigin is { } closingOrigin &&
        Boundary(opening, openingOrigin) && Boundary(closing, closingOrigin);

    private static bool WithinOriginalBody(WmlStaticScalarOwner owner, WmlStaticScalarContribution contribution) =>
        // [+unit] may add unrelated fields while retaining the original action
        // occurrence. Observe only a single untouched assignment in that original
        // body. Rewritten history or a field first added in an amendment refuses;
        // these old boundaries never become an insertion or write route.
        contribution.AssignmentOutputOffset >= owner.OpeningOutputOffset!.Value + owner.OpeningOrigin!.Length &&
        contribution.AssignmentOutputOffset + contribution.AssignmentOutputLength <= owner.ClosingOutputOffset!.Value;

    private bool Boundary(int offset, WmlStaticSourceRange origin) => Valid(origin) && offset >= 0 &&
        offset <= _evaluation.Text.Length - origin.Length &&
        _evaluation.Text.AsSpan(offset, origin.Length).SequenceEqual(origin.Text);

    private bool Valid(WmlStaticSourceRange origin) => _sources.Contains(origin.Source) && origin.StartOffset >= 0 &&
        origin.Length > 0 && origin.StartOffset <= origin.Source.Text.Length - origin.Length;

    private static bool SimpleName(string name) => name.Length is > 0 and <= MaxVariableCharacters &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool PlainCandidate(string value) => value.Length is > 0 and <= MaxCandidateCharacters && value == value.Trim() &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '_' or '-' or '\'');
}
