using Wcc.Core.Wml;
using Wcc.Core.Library;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Proves the preview plan against the active captured campaign-menu expansion.
/// No macro name, difficulty spelling or incomplete-preview flag grants proof.
/// </summary>
internal static class WccScenarioFieldContextScope
{
    internal static string? Verify(WccStaticSourceCaptureResult capture, WmlCampaignEvaluationPlan? plan,
        WmlEvaluationContext selectedContext, WmlStaticSingleplayerEnvironment environment,
        CancellationToken cancellationToken)
        => VerifyWithDiagnostics(capture, plan, selectedContext, environment, cancellationToken, null);

    internal static string? VerifyWithDiagnostics(WccStaticSourceCaptureResult capture, WmlCampaignEvaluationPlan? plan,
        WmlEvaluationContext selectedContext, WmlStaticSingleplayerEnvironment environment,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var verificationMeasurement = diagnostics?.Measure(WccSourceSavePhase.ContextVerification);
        diagnostics?.Add(WccSourceSaveCount.ContextVerifications);
        if (plan is null) return selectedContext.CampaignDefine is null ? null
            : "A campaign edit requires its declared context plan.";
        var menuContext = new WmlEvaluationContext(targetCompatibility: selectedContext.TargetCompatibility);
        var undefined = plan.Difficulties.Select(difficulty => difficulty.Define)
            .Concat(plan.CampaignDefine is null ? [] : new[] { plan.CampaignDefine })
            .Concat(["EDITOR", "MULTIPLAYER"]).Distinct(StringComparer.Ordinal).ToArray();
        WmlStaticEvaluationResult evaluation;
        diagnostics?.Add(WccSourceSaveCount.PreprocessingEvaluations);
        using (diagnostics?.Measure(WccSourceSavePhase.Preprocessing))
            evaluation = new WmlStaticEvaluator().Evaluate(new(capture.EntrySource!, menuContext,
                new[] { environment.DefinitionsSource }.Concat(capture.PreludeSources).ToArray(),
                undefined, IsInitialEnvironmentComplete: true, IncludeResolver: capture.IncludeResolver,
                Limits: WmlStaticEvaluationLimits.Campaign), cancellationToken);
        diagnostics?.ObserveEvaluation(evaluation);
        WmlStaticScalarProjection projection;
        diagnostics?.Add(WccSourceSaveCount.ScalarProjections);
        using (diagnostics?.Measure(WccSourceSavePhase.ScalarProjection))
            projection = WmlStaticScalarProjection.Create(evaluation, cancellationToken);
        diagnostics?.ObserveProjection(projection);
        if (!evaluation.IsComplete || projection.Diagnostics.Count != 0)
            return "Difficulty discovery is incomplete: the active captured campaign menu contains unresolved or malformed WML.";
        var owners = projection.Owners.Where(owner => owner.TagName == "campaign"
            && owner.OpeningOutputOffset.HasValue
            && Unquote(owner.GetKnownValue("id")) == plan.CampaignId
            && Unquote(owner.GetKnownValue("define")) == plan.CampaignDefine).Take(2).ToArray();
        if (owners.Length != 1)
            return "Difficulty discovery is incomplete: the preview campaign has no unique exact captured menu owner.";
        var campaign = owners[0];
        var requiredKeys = new[] { "id", "define", "difficulties", "default_difficulty", "extra_defines" };
        if (requiredKeys.Any(key => campaign.ResolveField(key).State == WmlStaticScalarResolutionState.Unresolved))
            return "Difficulty discovery is incomplete: campaign context metadata remains unresolved.";
        var extras = Symbols(campaign.GetKnownValue("extra_defines"));
        if (extras is null)
            return "Difficulty discovery is incomplete: campaign extra defines are not literal symbols.";
        if (plan.CampaignDefine is not null && !IsSymbol(plan.CampaignDefine))
            return "The captured campaign define is not a supported literal symbol.";
        var difficultyOwners = campaign.DirectChildren.Where(child => child.TagName == "difficulty").ToArray();
        if (difficultyOwners.Any(owner => owner.OpeningOutputOffset is null
            || owner.ResolveField("define").State != WmlStaticScalarResolutionState.Known
            || owner.ResolveField("default").State == WmlStaticScalarResolutionState.Unresolved))
            return "Difficulty discovery is incomplete: an active difficulty entry is unresolved.";
        var legacy = Symbols(campaign.GetKnownValue("difficulties"));
        if (legacy is null || legacy.Length > 0 && difficultyOwners.Length > 0)
            return "Difficulty discovery is incomplete: legacy and child difficulty declarations cannot define one clear scope.";
        var definitions = difficultyOwners.Length == 0 ? legacy
            : difficultyOwners.Select(owner => Unquote(owner.GetKnownValue("define")) ?? "").ToArray();
        if (definitions.Any(symbol => !IsSymbol(symbol))
            || definitions.Distinct(StringComparer.Ordinal).Count() != definitions.Length
            || !definitions.SequenceEqual(plan.Difficulties.Select(difficulty => difficulty.Define), StringComparer.Ordinal))
            return "The active captured difficulty defines or ordering differ from the preview context plan.";
        var legacyDefault = Unquote(campaign.GetKnownValue("default_difficulty"));
        var defaults = difficultyOwners.Length == 0
            ? definitions.Where(define => define == legacyDefault).ToArray()
            : difficultyOwners.Where(owner => IsTrue(Unquote(owner.GetKnownValue("default"))))
                .Select(owner => Unquote(owner.GetKnownValue("define"))!).ToArray();
        if (defaults.Length > 1 || !defaults.SequenceEqual(
                plan.Difficulties.Where(difficulty => difficulty.IsDefault).Select(difficulty => difficulty.Define),
                StringComparer.Ordinal))
            return "The active captured difficulty default differs from the preview context plan.";

        var expectedContexts = definitions.Length == 0
            ? new[] { new WmlEvaluationContext(plan.CampaignDefine, additionalDefines: extras,
                targetCompatibility: selectedContext.TargetCompatibility) }
            : definitions.Select(define => new WmlEvaluationContext(plan.CampaignDefine, define, extras,
                selectedContext.TargetCompatibility)).ToArray();
        if (!expectedContexts.Select(context => context.SemanticKey).SequenceEqual(
                plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal)
            || !expectedContexts.Any(context => context.SemanticKey == selectedContext.SemanticKey))
            return "The declared campaign/difficulty/extra symbols do not match the captured menu contexts.";
        return null;
    }

    private static string[]? Symbols(string? value)
    {
        var literal = Unquote(value);
        if (literal is null) return [];
        var values = literal.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return values.All(IsSymbol) ? values : null;
    }

    private static bool IsSymbol(string value) => value.Length > 0
        && (value[0] == '_' || char.IsAsciiLetter(value[0]))
        && value.Skip(1).All(character => character == '_' || char.IsAsciiLetterOrDigit(character));
    private static bool IsTrue(string? value) => value is "yes" or "true" or "1";
    private static string? Unquote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }
}