using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>An explicit ordinary literal insertion to promote into difficulty slots.
/// This recipe never grants persistence authority; fresh review re-proves insertion in every context.</summary>
public sealed class WccScenarioDifficultyFieldInsertionIntent
{
    private WccScenarioDifficultyFieldInsertionIntent(WccScenarioFieldRoute route, string initialValue)
    { Route = route; InitialValue = initialValue; }

    public WccScenarioFieldRoute Route { get; }
    public string InitialValue { get; }
    public bool AuthorizesPersistence => false;

    public static WccScenarioDifficultyFieldInsertionIntent Create(WccScenarioFieldEditProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Operation != WmlFieldOperation.Insert || proposal.ValueInputKind != WccScenarioFieldValueInputKind.Literal
            || proposal.RequestedValue is not { } value
            || !WccScenarioFieldValueCodec.TryGetDefinition(proposal.Route.Owner.TagName, proposal.Route.Key, out var field)
            || WccScenarioFieldValueCodec.InferInputKind(value) != WccScenarioFieldValueInputKind.Literal
            || WccScenarioFieldValueCodec.ValidateInput(field!, value) is not null
            || !IsAbsentInsertion(proposal.Route))
            throw new InvalidOperationException("Set a supported literal through the ordinary missing-field insertion before changing its difficulty dependence.");
        return new(proposal.Route, value);
    }

    internal static bool IsAbsentInsertion(WccScenarioFieldRoute route) =>
        route.Kind == WccScenarioFieldRouteKind.LocalAssignment && route.Origin == WccScenarioFieldOrigin.Absent
        && route.CanInsert && !route.CanSet && !route.CanRemove && route.Value is null && route.SourceValue is null
        && route.Target is null && route.Assignment is null && route.Invocation is null && route.Definition is null
        && route.ParameterName is null && route.ArgumentIndex is null && !route.IsOptionalArgumentSupplied;
}

public static partial class WccScenarioDifficultyFieldPlanner
{
    private static void ValidateInsertionIntent(WccScenarioDifficultyFieldInsertionIntent intent,
        WmlStaticSource source, WmlStaticScalarOwner owner, string key, WmlCampaignEvaluationPlan plan)
    {
        var route = intent.Route;
        var contexts = plan.Contexts.Where(context => context.SemanticKey == route.ContextSemanticKey).ToArray();
        if (contexts.Length != 1 || route.Key != key || !SameSource(route.Source, source)
            || !SameOwner(route.Owner, owner) || !WccScenarioDifficultyFieldInsertionIntent.IsAbsentInsertion(route)
            || !WccScenarioFieldValueCodec.TryGetDefinition(owner.TagName, key, out var field)
            || WccScenarioFieldValueCodec.InferInputKind(intent.InitialValue) != WccScenarioFieldValueInputKind.Literal
            || WccScenarioFieldValueCodec.ValidateInput(field!, intent.InitialValue) is not null)
            throw new InvalidOperationException("The pending missing-field insertion does not match this exact owner, source and declared context.");
        var fresh = RequireAbsentInsertionRoute(owner, key, source, contexts[0]);
        if ((route with { Owner = owner, Source = source }) != fresh)
            throw new InvalidOperationException("The ordinary missing-field insertion route changed; reload or reset its pending value.");
    }

    internal static WccScenarioFieldRoute RequireAbsentInsertionRoute(WmlStaticScalarOwner owner, string key,
        WmlStaticSource source, WmlEvaluationContext context)
    {
        RequireLiteralOwner(owner, source);
        var route = WccScenarioFieldRouteAnalyzer.Analyze(owner, key, source, context);
        if (!WccScenarioDifficultyFieldInsertionIntent.IsAbsentInsertion(route))
            throw new InvalidOperationException("Insertion requires proven field absence and an ordinary local insertion route in every declared context.");
        return route;
    }

    private static bool SameInsertionIntent(WccScenarioDifficultyFieldInsertionIntent? left,
        WccScenarioDifficultyFieldInsertionIntent? right) =>
        left is null || right is null ? left is null && right is null :
        left.InitialValue == right.InitialValue && SameSource(left.Route.Source, right.Route.Source)
        && SameOwner(left.Route.Owner, right.Route.Owner)
        && (left.Route with { Owner = right.Route.Owner, Source = right.Route.Source }) == right.Route;
}
