using System.Collections.Frozen;
using Wcc.Core.Scenarios;

namespace Wcc.Core.Wml;

public enum WmlDefinitionReferenceKind { Race, MovementType, Scenario }

/// <summary>An active declaration in one evaluated namespace; never an edit route.</summary>
public sealed record WmlObservedDefinition(WmlDefinitionReferenceKind Kind, string Id,
    WmlStaticScalarOwner Owner, WmlStaticScalarField Field);

/// <summary>An explicit direct field; absent or inherited defaults are not invented.</summary>
public sealed record WmlDefinitionReference(WmlDefinitionReferenceKind Kind,
    WmlStaticScalarOwner Owner, WmlStaticScalarField Field);

/// <summary>
/// Shared, read-only observation of active race, movement-type and single-player
/// scenario namespaces from an existing immutable preprocessing result. It performs
/// no filesystem access, runtime execution, inheritance merge or source editing.
/// </summary>
public sealed class WmlDefinitionReferenceProjection
{
    private readonly WmlStaticEvaluationResult _evaluation;
    private readonly bool _projectionMatches;
    private readonly HashSet<WmlStaticSource> _sources;
    private readonly FrozenDictionary<WmlDefinitionReferenceKind, IReadOnlySet<string>> _ids;
    private readonly FrozenDictionary<WmlDefinitionReferenceKind, bool> _complete;

    private WmlDefinitionReferenceProjection(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection scalar, CancellationToken cancellationToken)
    {
        _evaluation = evaluation;
        _projectionMatches = ReferenceEquals(scalar.SourceEvaluation, evaluation);
        _sources = evaluation.Sources.ToHashSet();
        var unitRoots = scalar.Owners.Where(owner => owner.TagName == "units" && owner.Parent is null).ToHashSet();
        var types = scalar.Owners.Where(owner => owner.TagName == "unit_type" &&
            owner.Parent is { } parent && unitRoots.Contains(parent)).ToHashSet();
        var global = _projectionMatches && evaluation.IsComplete && scalar.Diagnostics.Count == 0;
        var unitComplete = global && unitRoots.Count != 0 && unitRoots.All(HasCapturedPair);
        var complete = new Dictionary<WmlDefinitionReferenceKind, bool>
        {
            [WmlDefinitionReferenceKind.Race] = unitComplete,
            [WmlDefinitionReferenceKind.MovementType] = unitComplete,
            [WmlDefinitionReferenceKind.Scenario] = global
        };
        var declarations = new List<WmlObservedDefinition>();
        var references = new List<WmlDefinitionReference>();
        foreach (var owner in scalar.Owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.Parent is { } parent && unitRoots.Contains(parent))
            {
                if (owner.TagName == "race") Declare(owner, "id", WmlDefinitionReferenceKind.Race);
                if (owner.TagName == "movetype") Declare(owner, "name", WmlDefinitionReferenceKind.MovementType);
            }
            if (types.Contains(owner) || IsVariant(owner, types))
            {
                Reference(owner, "race", WmlDefinitionReferenceKind.Race);
                Reference(owner, "movement_type", WmlDefinitionReferenceKind.MovementType);
            }
            if (owner.TagName == "scenario" && owner.Parent is null)
            {
                Declare(owner, "id", WmlDefinitionReferenceKind.Scenario);
                Reference(owner, "next_scenario", WmlDefinitionReferenceKind.Scenario);
            }
            if (owner.TagName == "campaign" && owner.Parent is null)
                Reference(owner, "first_scenario", WmlDefinitionReferenceKind.Scenario);
        }
        Declarations = Array.AsReadOnly(declarations.ToArray());
        References = Array.AsReadOnly(references.ToArray());
        _ids = Enum.GetValues<WmlDefinitionReferenceKind>().ToFrozenDictionary(kind => kind,
            kind => (IReadOnlySet<string>)declarations.Where(item => item.Kind == kind)
                .Select(item => item.Id).ToFrozenSet(StringComparer.Ordinal));
        _complete = complete.ToFrozenDictionary();

        void Declare(WmlStaticScalarOwner owner, string key, WmlDefinitionReferenceKind kind)
        {
            if (owner.Fields.TryGetValue(key, out var field) && TryObserveField(owner, field, out var value) &&
                value.Length != 0 && WmlUnitReferenceProjection.IsStaticName(value))
                declarations.Add(new(kind, value, owner, field));
            else complete[kind] = false;
        }
        void Reference(WmlStaticScalarOwner owner, string key, WmlDefinitionReferenceKind kind)
        {
            if (owner.Fields.TryGetValue(key, out var field)) references.Add(new(kind, owner, field));
        }
    }

    public IReadOnlyList<WmlObservedDefinition> Declarations { get; }
    public IReadOnlyList<WmlDefinitionReference> References { get; }
    public IReadOnlySet<string> DeclaredIds(WmlDefinitionReferenceKind kind) => _ids[kind];
    /// <summary>Still requires the host's snapshot and actual engine Core-data evidence.</summary>
    public bool HasCompleteInventory(WmlDefinitionReferenceKind kind) => _complete[kind];

    /// <summary>
    /// Validates a supplied gameplay environment against one exact MENU owner.
    /// This does not discover, create or evaluate contexts; it only observes
    /// the captured literal symbol metadata required to correlate a launch link.
    /// </summary>
    public bool MatchesCampaignEnvironment(WmlStaticScalarOwner campaign, WmlEvaluationContext menu,
        WmlEvaluationContext gameplay)
    {
        if (campaign.TagName != "campaign" || campaign.Parent is not null || menu.CampaignDefine is not null ||
            gameplay.CampaignDefine is null || menu.TargetCompatibility != gameplay.TargetCompatibility) return false;
        if (campaign.Fields.TryGetValue("type", out var type) &&
            (!TryObserveField(campaign, type, out var kind) || kind != "sp")) return false;
        var define = Symbols(campaign, "define", required: true);
        var extras = Symbols(campaign, "extra_defines", required: false);
        if (define is not { Length: 1 } || define[0] != gameplay.CampaignDefine || extras is null ||
            !WmlCampaignDifficultyObserver.TryObserve(campaign, define[0], extras,
                (owner, key) => Symbols(owner, key, required: true), out var difficulties, out _)) return false;
        if (menu.DefinedSymbols.Contains(define[0]) || difficulties.Any(menu.DefinedSymbols.Contains)) return false;
        if (difficulties.Count == 0 ? gameplay.DifficultyDefine is not null :
            gameplay.DifficultyDefine is null || !difficulties.Contains(gameplay.DifficultyDefine, StringComparer.Ordinal)) return false;
        var expected = menu.DefinedSymbols.Concat(define).Concat(extras)
            .Concat(gameplay.DifficultyDefine is null ? [] : new[] { gameplay.DifficultyDefine });
        return gameplay.DefinedSymbols.SetEquals(expected);

        string[]? Symbols(WmlStaticScalarOwner owner, string key, bool required)
        {
            if (!owner.Fields.TryGetValue(key, out var field))
                return owner.ResolveField(key).State == WmlStaticScalarResolutionState.ProvenAbsent && !required ? [] : null;
            if (!TryObserveField(owner, field, out var value)) return null;
            if (value.Length == 0) return required ? null : [];
            var symbols = value.Split(',').Select(symbol => symbol.Trim()).ToArray();
            return symbols.All(symbol => WmlPreprocessorToken.IsLiteralName(symbol) && !symbol.Contains('$')) ? symbols : null;
        }
    }

    public static WmlDefinitionReferenceProjection Create(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection? scalar = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        return new(evaluation, scalar ?? WmlStaticScalarProjection.Create(evaluation, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Observes only exact captured final value text. A checker may provide an
    /// already proven composite component; this never changes strict scalar state.
    /// </summary>
    public bool TryObserveField(WmlStaticScalarOwner owner, WmlStaticScalarField field,
        out string value, bool allowComposite = false)
    {
        value = "";
        if (!TryObserveCapturedField(owner, field, out var observed, allowComposite)) return false;
        if (owner.TagName == "scenario" && owner.Parent is null && ScenarioHasExternalContent(owner)) return false;
        if (owner.TagName == "scenario" && field.Key == "next_scenario" && ScenarioHasGenerator(owner)) return false;
        value = observed;
        return true;
    }

    private bool TryObserveCapturedField(WmlStaticScalarOwner owner, WmlStaticScalarField field,
        out string value, bool allowComposite = false)
    {
        value = "";
        for (var containing = owner; containing is not null; containing = containing.Parent)
            if (!HasCapturedPair(containing)) return false;
        if (field.ValueText is not { } text || field.Contributions.LastOrDefault() is not
            { ValueMappingIsExact: true } contribution ||
            (!allowComposite && contribution.CompositeAssignmentKeys.Count != 0) ||
            contribution.ValueOutputOffset < 0 || contribution.ValueOutputLength != text.Length ||
            contribution.ValueOutputOffset > _evaluation.Text.Length - contribution.ValueOutputLength ||
            !_evaluation.Text.AsSpan(contribution.ValueOutputOffset, contribution.ValueOutputLength).SequenceEqual(text) ||
            (text.Length != 0 && contribution.ValueOrigins.Count == 0) ||
            contribution.ValueOrigins.Any(origin => !Captured(origin))) return false;
        if (!WmlResourceValueTextDecoder.TryDecode(text, out value, out _)) return false;
        value = value.Trim();
        return true;
    }

    private bool HasCapturedPair(WmlStaticScalarOwner owner) =>
        _projectionMatches && Boundary(owner.OpeningOutputOffset, owner.OpeningOrigin) && Boundary(owner.ClosingOutputOffset, owner.ClosingOrigin);
    private bool Boundary(int? outputOffset, WmlStaticSourceRange? origin) =>
        outputOffset is { } offset && offset >= 0 && origin is { Length: > 0 } && Captured(origin) &&
        offset <= _evaluation.Text.Length - origin.Length && _evaluation.Text.AsSpan(offset, origin.Length)
            .SequenceEqual(origin.Source.Text.AsSpan(origin.StartOffset, origin.Length));
    private bool Captured(WmlStaticSourceRange? origin) => origin is not null && _sources.Contains(origin.Source) &&
        origin.StartOffset >= 0 && origin.Length >= 0 && origin.StartOffset <= origin.Source.Text.Length - origin.Length;

    private bool ScenarioHasExternalContent(WmlStaticScalarOwner owner)
    {
        if (WccScenarioFieldContentScope.CheckOwner(owner) is not null) return true;
        if (owner.Fields.TryGetValue("include_file", out var include) &&
            (!TryObserveCapturedField(owner, include, out var included) || included.Length != 0)) return true;
        // A .cfg map_file may be merged by the engine when there is no map_data.
        // This is outside preprocessing; an ordinary literal .map is unaffected.
        if (owner.Fields.TryGetValue("map_data", out var data))
        {
            if (!TryObserveCapturedField(owner, data, out var map)) return true;
            if (map.Length != 0) return false;
        }
        if (!owner.Fields.TryGetValue("map_file", out var file))
            return owner.ResolveField("map_file").State != WmlStaticScalarResolutionState.ProvenAbsent;
        return !TryObserveCapturedField(owner, file, out var name) ||
            name.Length != 0 && (!WmlUnitReferenceProjection.IsStaticName(name) || name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase));
    }

    private bool ScenarioHasGenerator(WmlStaticScalarOwner owner) =>
        owner.DirectChildren.Any(child => child.TagName == "generator") ||
        owner.Fields.TryGetValue("scenario_generation", out var field) &&
            (!TryObserveCapturedField(owner, field, out var value) || value.Length != 0);

    private static bool IsVariant(WmlStaticScalarOwner owner, HashSet<WmlStaticScalarOwner> types)
    {
        if (owner.TagName is not ("male" or "female" or "variation")) return false;
        for (var parent = owner.Parent; parent is not null; parent = parent.Parent)
        {
            if (types.Contains(parent)) return true;
            if (parent.TagName is not ("male" or "female" or "variation")) return false;
        }
        return false;
    }
}
