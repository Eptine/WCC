using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>Read-only captured menu projection; an owner or displayed value never authorizes persistence.</summary>
public sealed class CampaignInitialFieldProjection
{
    internal CampaignInitialFieldProjection(WmlStaticSource? source, WmlEvaluationContext context,
        WmlStaticScalarProjection? projection, IEnumerable<string> diagnostics, WmlCampaignEvaluationPlan? effectivePlan = null)
    {
        Source = source; EvaluationEntrySource = source; Context = context; Projection = projection; EffectivePlan = effectivePlan;
        Campaigns = Array.AsReadOnly((projection?.Owners.Where(owner => owner.TagName == "campaign" && owner.Parent is null) ?? []).ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }
    public WmlStaticSource? Source { get; }
    /// <summary>The real preprocessing entrypoint; Source may be an included selected menu file.</summary>
    public WmlStaticSource? EvaluationEntrySource { get; internal init; }
    public WmlEvaluationContext Context { get; }
    public WmlStaticScalarProjection? Projection { get; }
    /// <summary>Exact captured metadata when proven; otherwise the original preview. Not persistence authority.</summary>
    public WmlCampaignEvaluationPlan? EffectivePlan { get; }
    public IReadOnlyList<WmlStaticScalarOwner> Campaigns { get; }
    /// <summary>One exact entry over the full original menu. Save reauthenticates its source and plan.</summary>
    public WmlStaticScalarOwner? SelectedCampaignOwner { get; internal init; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool IsComplete => Source is not null && Projection is not null && Diagnostics.Count == 0;
    public string? Diagnostic => Diagnostics.Count == 0 ? null : string.Join(" ", Diagnostics);
    public bool AuthorizesPersistence => false;
}

public static class CampaignInitialFieldProjector
{
    /// <summary>Obtains complete context metadata from this exact captured menu evaluation.
    /// The shared discovery preview remains unchanged; this result is display/proposal evidence, not write authority.</summary>
    public static bool TryGetCapturedContextPlan(CampaignInitialFieldProjection projection, WmlCampaignEvaluationPlan? suppliedPlan,
        out WmlCampaignEvaluationPlan? completePlan, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(projection);
        completePlan = null;
        if (!projection.IsComplete || projection.Source is not { } source || projection.Projection is not { SourceEvaluation: { } evaluation } scalar)
        { problem = "The exact captured campaign menu projection is incomplete."; return false; }
        if (projection.SelectedCampaignOwner is { } selected)
        {
            completePlan = suppliedPlan ?? projection.EffectivePlan;
            problem = CheckContextPlan(projection, selected, completePlan);
            return problem is null && completePlan is { IsContextDiscoveryComplete: true };
        }
        return WmlCapturedCampaignEvaluationPlan.TryCreate(evaluation, scalar, source, suppliedPlan, out completePlan, out problem)
            && completePlan is { IsContextDiscoveryComplete: true };
    }

    public static CampaignInitialFieldProjection Project(WccStaticSourceCaptureResult capture,
        WmlCampaignEvaluationPlan? plan = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var context = new WmlEvaluationContext(targetCompatibility: plan?.Contexts.FirstOrDefault()?.TargetCompatibility ?? capture.CoreVersion);
        var problems = capture.Diagnostics.ToList();
        if (capture.EntrySource is null) return new(null, context, null, problems.Append("The campaign main source is unavailable."), plan);
        if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            return new(capture.EntrySource, context, null, problems.Append("The exact pinned Core version has no supported singleplayer environment."), plan);
        var undefined = (plan?.Difficulties.Select(difficulty => difficulty.Define) ?? [])
            .Concat(plan?.CampaignDefine is { } define ? [define] : []).Concat(["EDITOR", "MULTIPLAYER"]).Distinct(StringComparer.Ordinal).ToArray();
        var evaluated = new WmlStaticEvaluator().Evaluate(new(capture.EntrySource, context,
            new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).ToArray(), undefined,
            IsInitialEnvironmentComplete: true, IncludeResolver: capture.IncludeResolver,
            Limits: WmlStaticEvaluationLimits.Campaign), cancellationToken);
        var projection = WmlStaticScalarProjection.Create(evaluated, cancellationToken);
        problems.AddRange(evaluated.Diagnostics.Select(diagnostic => diagnostic.Message));
        problems.AddRange(projection.Diagnostics.Select(diagnostic => diagnostic.Message));
        if (!evaluated.IsComplete && problems.Count == 0) problems.Add("Campaign menu expansion is incomplete.");
        var effectivePlan = plan;
        if (problems.Count == 0 && WmlCapturedCampaignEvaluationPlan.TryCreate(evaluated, projection,
                capture.EntrySource, plan, out var resolvedPlan, out _))
        {
            // Keep the shared session plan when fresh captured metadata confirms its topology.
            // Menu labels and provenance are displayed from the projection, not this preview.
            if (plan is null || resolvedPlan is null || !SameTopology(plan, resolvedPlan)) effectivePlan = resolvedPlan;
        }
        return new(capture.EntrySource, context, projection, problems, effectivePlan);
    }

    private static bool SameTopology(WmlCampaignEvaluationPlan supplied, WmlCampaignEvaluationPlan captured)
        => supplied.CampaignId == captured.CampaignId && supplied.CampaignDefine == captured.CampaignDefine
            && supplied.Difficulties.Select(difficulty => (difficulty.Define, difficulty.IsDefault))
                .SequenceEqual(captured.Difficulties.Select(difficulty => (difficulty.Define, difficulty.IsDefault)))
            && supplied.Contexts.Select(context => context.SemanticKey)
                .SequenceEqual(captured.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal);

    public static WmlStaticScalarOwner? BindAuthoredCampaign(CampaignInitialFieldProjection projection, CampaignDocument document)
    {
        ArgumentNullException.ThrowIfNull(projection); ArgumentNullException.ThrowIfNull(document);
        if (projection.Source is not { } source || source.Text != document.OriginalText ||
            projection.SelectedCampaignOwner is null && projection.Campaigns.Count != 1) return null;
        var span = document.CampaignBlockSpan;
        var offset = span.StartIndex + span.RawText.Length - span.RawText.TrimStart().Length;
        var candidates = projection.SelectedCampaignOwner is { } selected ? new[] { selected } : projection.Campaigns;
        var matches = candidates.Where(owner => owner.OpeningOutputOffset.HasValue
            && owner.OpeningExpansionStack.Count == 0 && owner.OpeningOrigin is { } opening
            && opening.Source.Path == source.Path && opening.Source.ContentHash == source.ContentHash
            && opening.StartOffset == offset && opening.Text == "[campaign]").Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Checks the exact captured menu owner and its declared context topology for
    /// display/edit eligibility. This pure result is not persistence authority;
    /// collected review captures and proves every menu/play phase again.
    /// </summary>
    public static string? CheckContextPlan(CampaignInitialFieldProjection projection, WmlStaticScalarOwner owner,
        WmlCampaignEvaluationPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(projection); ArgumentNullException.ThrowIfNull(owner);
        if (!projection.IsComplete || projection.Source is not { } source)
            return "The captured campaign menu is incomplete; use RAW review.";
        if (projection.SelectedCampaignOwner is { } selected
                ? !ReferenceEquals(selected, owner) || projection.Campaigns.Count(item => ReferenceEquals(item, owner)) != 1
                : projection.Campaigns.Count != 1 || !ReferenceEquals(projection.Campaigns[0], owner))
            return "The captured campaign menu has no unique exact selected owner; use RAW review.";
        var root = projection.EvaluationEntrySource ?? source;
        if (plan is null || plan.Contexts.Count == 0
            || !string.Equals(root.Path, plan.CampaignMainSourcePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || root.ContentHash != plan.CampaignMainTextHash)
            return "The captured campaign menu and declared play plan do not share one exact main-source revision.";
        if (projection.SelectedCampaignOwner is not null)
        {
            if (projection.Projection?.SourceEvaluation is not { } evaluation ||
                WccCampaignCapturedOwner.FindAuthoredSpan(owner, evaluation) is null ||
                owner.OpeningOrigin is not { } opening || !WccScenarioFieldRouteAnalyzer.SameSource(source, opening.Source))
                return "The selected campaign has no exact physical menu source.";
            return WccScenarioFieldStaticReviewer.VerifyCampaignContextPlan(owner, plan, source, false);
        }
        return WccScenarioFieldStaticReviewer.VerifyCampaignContextPlan(owner, plan, source, true);
    }
}
