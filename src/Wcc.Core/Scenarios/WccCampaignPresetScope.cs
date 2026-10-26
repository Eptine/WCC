using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum WccCampaignPresetScopeKind { Package, Campaign }

/// <summary>Immutable activation intent. Actual captured include and definition evidence remains mandatory.</summary>
public sealed record WccCampaignPresetScope(WccCampaignPresetScopeKind Kind,
    string? CampaignId = null, string? CampaignDefine = null, string? ManagedChapterId = null)
{
    public static WccCampaignPresetScope Package { get; } = new(WccCampaignPresetScopeKind.Package);
    public bool IsPackage => Kind == WccCampaignPresetScopeKind.Package;
    public bool Targets(WmlCampaignEvaluationPlan? plan) => IsPackage || plan is not null
        && CampaignId == plan.CampaignId && CampaignDefine == plan.CampaignDefine;
    public string DisplayName => IsPackage ? "Whole package" : ManagedChapterId ?? CampaignId ?? "Unknown campaign";
}

/// <summary>One preprocessor namespace across preset kinds; scope is proved by the managed layout, never labels.</summary>
internal static class WccCampaignPresetScopePolicy
{
    internal static bool Overlaps(WccCampaignPresetScope left, WccCampaignPresetScope right,
        IReadOnlyList<WccCampaignPresetScope> verifiedScopes) => left.IsPackage || right.IsPackage || left == right
        || !verifiedScopes.Contains(left) || !verifiedScopes.Contains(right)
        || left.CampaignId == right.CampaignId || left.CampaignDefine == right.CampaignDefine;

    internal static void ValidateReservedSymbols(IEnumerable<string> names, IReadOnlyList<WmlCampaignEvaluationPlan> plans)
    {
        var requested = names.ToHashSet(StringComparer.Ordinal);
        var reserved = plans.SelectMany(plan => plan.Contexts.SelectMany(context => context.DefinedSymbols))
            .Concat(plans.SelectMany(plan => plan.Difficulties.Select(difficulty => difficulty.Define)))
            .Concat(plans.Where(plan => plan.CampaignDefine is not null).Select(plan => plan.CampaignDefine!))
            .Concat(["EDITOR", "MULTIPLAYER"]);
        if (reserved.Any(requested.Contains))
            throw new InvalidOperationException("A preset macro name conflicts with a captured campaign, difficulty or environment symbol.");
    }

    internal static void Validate(IEnumerable<(Guid Id, string Symbol, WccCampaignPresetScope Scope)> rows,
        IReadOnlyList<WccCampaignPresetScope> verifiedScopes)
    {
        var entries = rows.ToArray();
        if (entries.Any(row => row.Id == Guid.Empty || !WccCampaignLocalPresetCatalog.ValidName(row.Symbol)
                || !row.Scope.IsPackage && !verifiedScopes.Contains(row.Scope))
            || entries.Select(row => row.Id).Distinct().Count() != entries.Length)
            throw new InvalidOperationException("Preset identities and activation scopes must be exact and package-unique.");
        for (var first = 0; first < entries.Length; first++)
            for (var second = first + 1; second < entries.Length; second++)
                if (entries[first].Symbol == entries[second].Symbol && Overlaps(entries[first].Scope, entries[second].Scope, verifiedScopes))
                    throw new InvalidOperationException("Preset macro names share one namespace and cannot overlap: " + entries[first].Symbol);
    }
}
