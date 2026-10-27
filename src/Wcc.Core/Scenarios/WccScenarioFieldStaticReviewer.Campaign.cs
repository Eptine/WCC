using System.Globalization;
using System.Text;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static Task<WccScenarioFieldBatchReviewResult> ReviewCampaignBatchAsync(
        WccScenarioFieldEditSessionState state, IReadOnlyList<WccScenarioFieldEditProposal> proposals,
        CancellationToken cancellationToken) => ReviewCampaignBatchWithDiagnosticsAsync(state, proposals, cancellationToken, null);

    private static async Task<WccScenarioFieldBatchReviewResult> ReviewCampaignBatchWithDiagnosticsAsync(
        WccScenarioFieldEditSessionState state, IReadOnlyList<WccScenarioFieldEditProposal> proposals,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        try
        {
            if (proposals.Count == 0 || proposals.Any(proposal => proposal.Route.Owner.TagName != "campaign"
                    || proposal.Route.ContextSemanticKey != state.Context.SemanticKey
                    || !WccScenarioFieldValueCodec.TryGetDefinition("campaign", proposal.Route.Key, out _)))
                return RefuseBatch("Campaign drafts require supported ordinary menu scalars; bootstrap and difficulty settings remain in RAW.");
            if (CheckScope(state) is { } scopeRefusal) return RefuseBatch(scopeRefusal);
            var capture = await CaptureBatchWithDiagnosticsAsync(state, state.Source.Path, cancellationToken, diagnostics).ConfigureAwait(false);
            if (CheckCapture(state, capture) is { } captureRefusal) return RefuseBatch(captureRefusal);
            if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
                return RefuseBatch("The exact pinned Core version cannot supply a known singleplayer environment.");
            var contexts = new[] { state.Context }.Concat(state.Plan!.Contexts).DistinctBy(context => context.SemanticKey).ToArray();
            diagnostics?.Add(WccSourceSaveCount.ReviewContexts, contexts.Length);
            var baselines = new Dictionary<string, PairedEvaluation>(StringComparer.Ordinal);
            foreach (var context in contexts)
            {
                var baseline = EvaluateWithDiagnostics(capture, context, state.Plan, environment!, null, cancellationToken, diagnostics);
                if (!baseline.Evaluation.IsComplete || baseline.Projection.Diagnostics.Count != 0)
                    return RefuseBatch("A campaign menu or declared play phase contains unresolved or malformed expanded WML; use RAW review.");
                baselines.Add(context.SemanticKey, baseline);
            }
            var menu = baselines[state.Context.SemanticKey];
            if (menu.Projection.Owners.Count(owner => owner.TagName == "campaign" && owner.Parent is null) != 1)
                return RefuseBatch("The menu has no unique campaign owner; multiple or generated campaign menus remain in RAW.");
            var anchor = FindCampaignBatchOwnerWithDiagnostics(menu.Projection, state.Owner, null, diagnostics);
            if (anchor is null) return RefuseBatch("The campaign cannot be rebound to one exact captured menu owner.");
            if (VerifyCampaignContextPlan(anchor, state.Plan, state.Source, true) is { } planRefusal) return RefuseBatch(planRefusal);
            var freshProposals = new List<WccScenarioFieldEditProposal>();
            foreach (var proposal in proposals)
            {
                using var fieldMeasurement = diagnostics?.Measure(WccSourceSavePhase.FieldValidation);
                diagnostics?.Add(WccSourceSaveCount.FieldValidations);
                var owner = FindCampaignBatchOwnerWithDiagnostics(menu.Projection, proposal.Route.Owner, null, diagnostics);
                if (owner is null || !ReferenceEquals(owner, anchor)) return RefuseBatch("A campaign draft belongs to a different captured menu owner.");
                var route = WccScenarioFieldRouteAnalyzer.Analyze(owner, proposal.Route.Key, state.Source, state.Context);
                if (!EquivalentRoute(proposal.Route, route)) return RefuseBatch("The campaign field's winning value or exact source route changed.");
                freshProposals.Add(proposal with { Route = route });
            }
            var candidate = PlanBatchWithDiagnostics(freshProposals, state.Source, diagnostics);
            var change = new WccScenarioFieldSourceChange(candidate);
            var fieldContexts = candidate.Edits.Select(_ => new List<WccScenarioFieldContextReview>()).ToArray();
            var contextResults = new List<WccScenarioFieldContextReview>();
            var closureIdentity = BatchClosureIdentityWithDiagnostics(capture, diagnostics);
            var identity = new StringBuilder();
            Add(identity, ScopeIdentity(state)); Add(identity, closureIdentity);
            foreach (var context in contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var baseline = baselines[context.SemanticKey];
                var proposed = EvaluateWithDiagnostics(capture, context, state.Plan, environment!, change, cancellationToken, diagnostics);
                if (!proposed.Evaluation.IsComplete || proposed.Projection.Diagnostics.Count != 0)
                    return RefuseBatch("The combined campaign draft has unresolved or malformed output in a menu or play phase.");
                var oldOwner = FindCampaignBatchOwnerWithDiagnostics(baseline.Projection, anchor, null, diagnostics);
                var newOwner = FindCampaignBatchOwnerWithDiagnostics(proposed.Projection, anchor, change, diagnostics);
                var isMenu = context.SemanticKey == state.Context.SemanticKey;
                if (oldOwner is null || newOwner is null)
                {
                    // An exact menu-only owner can be inactive during play. No
                    // other output or consumer is exempted in that phase.
                    if (isMenu || oldOwner is not null || newOwner is not null
                        || HasMatchingOwnerKey(baseline.Projection, anchor, null)
                        || HasMatchingOwnerKey(proposed.Projection, anchor, change))
                        return RefuseBatch("The campaign owner is absent, ambiguous or structurally uncertain in a reviewed phase.");
                }
                if (isMenu && VerifyCampaignContextPlan(newOwner!, state.Plan, state.Source, false) is { } changedPlan)
                    return RefuseBatch(changedPlan);
                var targets = new List<ReviewedFieldTarget>();
                for (var index = 0; index < candidate.Edits.Count; index++)
                {
                    using var fieldMeasurement = diagnostics?.Measure(WccSourceSavePhase.FieldValidation);
                    diagnostics?.Add(WccSourceSaveCount.FieldValidations);
                    var edit = candidate.Edits[index];
                    if (oldOwner is null || newOwner is null)
                    {
                        fieldContexts[index].Add(new(context.SemanticKey, "Campaign play: " + context.DisplayName, null, null, []));
                        continue;
                    }
                    WccScenarioFieldValueCodec.TryGetDefinition("campaign", edit.Route.Key, out var definition);
                    ValidateCompositeFieldContext(edit, oldOwner, newOwner);
                    var oldValue = oldOwner.ResolveField(edit.Route.Key);
                    var newValue = newOwner.ResolveField(edit.Route.Key);
                    if (oldValue.State == WmlStaticScalarResolutionState.Unresolved || newValue.State == WmlStaticScalarResolutionState.Unresolved
                        || oldValue.State == WmlStaticScalarResolutionState.Known && !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition!, oldValue.ValueText)
                            && !IsFreshKnownEmptyComposite(oldOwner, edit.Route.Key, state.Source)
                        || newValue.State == WmlStaticScalarResolutionState.Known && !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition!, newValue.ValueText))
                        return RefuseBatch("A campaign field is not a known supported scalar in a reviewed phase.");
                    if (edit.Route.Origin == WccScenarioFieldOrigin.Absent && oldValue.State != WmlStaticScalarResolutionState.ProvenAbsent)
                        return RefuseBatch("Campaign insertion requires proven absence in every phase where its owner exists.");
                    if (isMenu && edit.Operation != WmlFieldOperation.Remove
                        && (newValue.State != WmlStaticScalarResolutionState.Known
                            || !(edit.ValueInputKind == WccScenarioFieldValueInputKind.Literal
                                ? WccScenarioFieldValueCodec.Equivalent(definition!, newValue.ValueText, edit.RequestedValue)
                                : HasExactProposedScalarAncestry(newOwner, edit.Route.Key, edit, proposed.Evaluation))))
                        return RefuseBatch("The campaign proposal does not resolve to the requested menu field value.");
                    targets.Add(new(oldOwner, newOwner, edit.Route) { Edit = edit });
                    fieldContexts[index].Add(new(context.SemanticKey, isMenu ? "Campaign menu" : "Campaign play: " + context.DisplayName,
                        oldValue.ValueText, newValue.ValueText, []));
                }
                var effects = CompareBatchOwnersWithDiagnostics(baseline.Projection, proposed.Projection, targets, change,
                    baseline.Evaluation.Text, proposed.Evaluation.Text, out var effectRefusal, diagnostics);
                if (effectRefusal is not null) return RefuseBatch(effectRefusal);
                if (effects.Count != 0) return RefuseBatch("A campaign field changes another expanded consumer; only the requested campaign fields may change.");
                string? unsupportedRefusal;
                using (diagnostics?.Measure(WccSourceSavePhase.HistoryComparison))
                {
                    diagnostics?.Add(WccSourceSaveCount.HistoryComparisons);
                    unsupportedRefusal = CheckUnchangedCampaignFieldHistories(baseline.Projection, proposed.Projection, targets, change,
                        baseline.Evaluation.Text, proposed.Evaluation.Text);
                }
                if (unsupportedRefusal is not null)
                    return RefuseBatch(unsupportedRefusal);
                if (BatchHasUntrackedOutputChangeWithDiagnostics(baseline, proposed, diagnostics)) return RefuseBatch("The campaign draft changes untracked expanded output; use RAW review.");
                contextResults.Add(new(context.SemanticKey, isMenu ? "Campaign menu" : "Campaign play: " + context.DisplayName, null, null, effects));
                Add(identity, context.SemanticKey); Add(identity, WccContentHash.ComputeText(baseline.Evaluation.Text));
                Add(identity, WccContentHash.ComputeText(proposed.Evaluation.Text));
                foreach (var effect in effects) Add(identity, effect);
            }
            var finalCapture = await CaptureBatchWithDiagnosticsAsync(state, state.Source.Path, cancellationToken, diagnostics,
                WccSourceSavePhase.FinalCapture).ConfigureAwait(false);
            if (CheckCapture(state, finalCapture) is { } finalRefusal) return RefuseBatch(finalRefusal);
            if (BatchClosureIdentityWithDiagnostics(finalCapture, diagnostics) != closureIdentity) return RefuseBatch("The captured campaign closure or namespace changed during review.");
            Add(identity, candidate.CandidateSource.ContentHash);
            foreach (var patch in candidate.Patches)
            {
                Add(identity, patch.Start.ToString(CultureInfo.InvariantCulture)); Add(identity, patch.Length.ToString(CultureInfo.InvariantCulture));
                Add(identity, patch.ReplacementText);
            }
            var fields = candidate.Edits.Select((edit, index) => new WccScenarioFieldBatchFieldReview(edit.Route,
                edit.Operation, edit.RequestedValue, fieldContexts[index]));
            return new(true, "The campaign fields are checked in the menu and every declared play phase; context declarations remain unchanged.",
                candidate, WccContentHash.ComputeText(identity.ToString()), fields, contextResults,
                new(closureIdentity, ScopeIdentity(state), candidate));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return RefuseBatch("Campaign source review was refused: " + exception.Message); }
    }

    private static WmlStaticScalarOwner? FindCampaignBatchOwnerWithDiagnostics(WmlStaticScalarProjection projection,
        WmlStaticScalarOwner original, WccScenarioFieldSourceChange? candidate, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.OwnerLookup);
        diagnostics?.Add(WccSourceSaveCount.OwnerLookupQueries);
        return FindOwner(projection, original, candidate);
    }

    private static bool HasMatchingOwnerKey(WmlStaticScalarProjection projection, WmlStaticScalarOwner anchor, WccScenarioFieldSourceChange? change)
    {
        var key = OwnerKey(anchor, change);
        return key is not null && projection.Owners.Any(owner => OwnerKey(owner, null) == key);
    }

    private static string? CheckUnchangedCampaignFieldHistories(WmlStaticScalarProjection baseline,
        WmlStaticScalarProjection proposed, IReadOnlyList<ReviewedFieldTarget> targets, WccScenarioFieldSourceChange change,
        string baselineText, string proposedText)
    {
        // CompareOwners has already proved the ordered owner identities. For an
        // unselected scalar, its winner alone cannot prove unchanged
        // output: an earlier shadowed assignment is tracked and would otherwise
        // disappear from the final untracked-output comparison.
        for (var index = 0; index < baseline.Owners.Count; index++)
        {
            var before = baseline.Owners[index];
            var after = proposed.Owners[index];
            foreach (var key in before.Fields.Keys.Concat(after.Fields.Keys).Distinct(StringComparer.Ordinal))
            {
                if (targets.Any(target => ReferenceEquals(target.OldOwner, before)
                        && ReferenceEquals(target.NewOwner, after) && target.Route.Key == key)) continue;
                var oldValue = before.ResolveField(key);
                var newValue = after.ResolveField(key);
                if (!before.Fields.TryGetValue(key, out var oldField) || !after.Fields.TryGetValue(key, out var newField)
                    || oldValue.State != newValue.State || oldField.ValueText != newField.ValueText
                    || oldField.IsKnown != newField.IsKnown || oldField.Contributions.Count != newField.Contributions.Count
                    || !SameMappedOrigins(oldField.Origins, newField.Origins, change))
                    return "An unselected campaign-context field changes or loses its exact contribution evidence; use RAW review.";
                for (var contributionIndex = 0; contributionIndex < oldField.Contributions.Count; contributionIndex++)
                {
                    var oldContribution = oldField.Contributions[contributionIndex];
                    var newContribution = newField.Contributions[contributionIndex];
                    if (oldContribution.ValueMappingIsExact != newContribution.ValueMappingIsExact
                        || !oldContribution.CompositeAssignmentKeys.SequenceEqual(newContribution.CompositeAssignmentKeys, StringComparer.Ordinal)
                        || !SameAssignmentOutput(oldContribution, newContribution, baselineText, proposedText)
                            && !SameCompositeCompanionAssignment(before, after, oldContribution, newContribution,
                                baselineText, proposedText, targets)
                        || !SameMappedOrigins(oldContribution.AssignmentOrigins, newContribution.AssignmentOrigins, change)
                            && !SameCompositeCompanionInsertionOrigins(before, after, key, oldContribution, newContribution, change,
                                baselineText, proposedText, targets)
                        || !SameMappedOrigins(oldContribution.ValueOrigins, newContribution.ValueOrigins, change)
                        || !SameMappedFrames(oldContribution.ExpansionStack, newContribution.ExpansionStack, change)
                        || !SameMappedFrames(oldContribution.ValueExpansionStack, newContribution.ValueExpansionStack, change))
                        return "An unselected campaign-context field changes its assignment history; use RAW review.";
                }
            }
        }
        return null;
    }

    internal static string? VerifyCampaignContextPlan(WmlStaticScalarOwner owner, WmlCampaignEvaluationPlan plan,
        WmlStaticSource source, bool bindSource)
    {
        // Exact opening/closing capabilities are revoked for uncertain owner
        // structure. An unsupported scalar such as a translated concatenation
        // may retain those capabilities; its complete unchanged history is
        // proved separately without giving that scalar an editable route.
        if (owner.IsAmended || owner.OpeningOutputOffset is null || owner.ClosingOrigin is null)
            return "Campaign amendments or unresolved structure require RAW review.";
        if (bindSource)
        {
            var span = plan.AuthoredCampaignSpan;
            if (span is null || span.TagName != "campaign" || span.StartIndex < 0 || span.EndIndexExclusive > source.Text.Length
                || source.Text[span.StartIndex..span.EndIndexExclusive] != span.RawText
                || owner.OpeningExpansionStack.Count != 0 || owner.OpeningOrigin is not { } opening
                || !WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, source)
                || opening.StartOffset != span.StartIndex + span.RawText.Length - span.RawText.TrimStart().Length
                || opening.Text != "[campaign]" || !WccScenarioFieldRouteAnalyzer.SameSource(owner.ClosingOrigin.Source, source)
                || owner.ClosingOrigin.Text != "[/campaign]" || owner.ClosingOrigin.StartOffset < opening.StartOffset
                || owner.ClosingOrigin.StartOffset + owner.ClosingOrigin.Length > span.EndIndexExclusive)
                return "The declared play plan is not anchored to this exact authored campaign owner.";
        }
        if (plan.Contexts.Count == 0) return "The supplied campaign play plan has no evaluation contexts.";
        if (!WmlCapturedCampaignEvaluationPlan.TryObserveMetadata(owner, out var metadata, out var metadataProblem))
            return metadataProblem ?? "The campaign has no complete unambiguous declared difficulty scope.";
        var define = metadata!.CampaignDefine;
        var extras = metadata.ExtraDefines;
        var definitions = metadata.Difficulties.Select(difficulty => difficulty.Define).ToArray();
        var defaults = metadata.Difficulties.Where(difficulty => difficulty.IsDefault).Select(difficulty => difficulty.Define).ToArray();
        if (define != plan.CampaignDefine
            || !definitions.SequenceEqual(plan.Difficulties.Select(difficulty => difficulty.Define), StringComparer.Ordinal)
            || !defaults.SequenceEqual(plan.Difficulties.Where(difficulty => difficulty.IsDefault).Select(difficulty => difficulty.Define), StringComparer.Ordinal))
            return "The exact campaign's declared contexts differ from the supplied play plan.";
        var expected = definitions.Length == 0 ? new[] { new WmlEvaluationContext(define, additionalDefines: extras, targetCompatibility: plan.Contexts[0].TargetCompatibility) }
            : definitions.Select(value => new WmlEvaluationContext(define, value, extras, plan.Contexts[0].TargetCompatibility)).ToArray();
        return expected.Select(context => context.SemanticKey).SequenceEqual(plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal)
            ? null : "The exact campaign's extra defines or context ordering differ from the supplied play plan.";

    }
}
