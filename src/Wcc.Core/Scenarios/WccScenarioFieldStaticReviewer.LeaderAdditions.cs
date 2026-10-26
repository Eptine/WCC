using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateLeaderAdditionBinding(WccScenarioLeaderAdditionEdit edit,
        WccScenarioFieldEditSessionState state, WmlStaticScalarOwner scenario,
        WmlStaticScalarOwner side, WmlStaticScalarProjection projection)
    {
        var snapshot = edit.Snapshot;
        if (state.ReviewScope != WccScenarioFieldReviewScope.Scenario || state.Plan is null || snapshot.Plan is null ||
            !ReferenceEquals(edit.Source, state.Source) || !ReferenceEquals(side.Parent, scenario) || !snapshot.SameOwner(side) ||
            state.Plan.CampaignMainTextHash != snapshot.Plan.CampaignMainTextHash ||
            state.Plan.CampaignDefine != snapshot.Plan.CampaignDefine ||
            !PathComparer.Equals(state.Plan.CampaignMainSourcePath, snapshot.Plan.CampaignMainSourcePath) ||
            !state.Plan.Contexts.Select(context => context.SemanticKey).SequenceEqual(snapshot.Plan.Contexts.Select(context => context.SemanticKey)))
            throw new InvalidOperationException("New leaders no longer belong to the exact side publication and fixed campaign contexts.");
        WccScenarioLeaderAdditionPlanner.CreateEdit(snapshot, edit.Leaders);
        var fresh = WccScenarioLeaderAdditionPlanner.Read(state.Source, side, state.Plan, projection);
        if (!WccScenarioLeaderAdditionPlanner.SameLayout(snapshot, fresh))
            throw new InvalidOperationException("The exact side or its existing nested leaders changed; reload its source.");
    }

    private static void ValidateProjectLeaderAdditions(WccProjectComposedFile file, PairedEvaluation baseline,
        PairedEvaluation proposed, WmlStaticScalarOwner before, WmlStaticScalarOwner after,
        WmlEvaluationContext context, WccScenarioFieldSourceChange change)
    {
        if (file.Request.LeaderAdditions.Count == 0 || context.CampaignDefine is null) return;
        var mask = WmlAuthoredStructureScanner.Scan(proposed.Evaluation.Text).MaskedText;
        foreach (var edit in file.Request.LeaderAdditions)
        {
            var snapshot = edit.Snapshot;
            var oldSide = before.DirectChildren.SingleOrDefault(side => snapshot.SameOwner(side))
                ?? throw new InvalidOperationException("A new leader's side differs between declared difficulties.");
            var fresh = WccScenarioLeaderAdditionPlanner.Read(file.Request.Source, oldSide, snapshot.Plan, baseline.Projection);
            if (!WccScenarioLeaderAdditionPlanner.SameLayout(snapshot, fresh))
                throw new InvalidOperationException("Existing leader topology differs between declared difficulties; use RAW.");
            var mappedKey = OwnerKey(oldSide, change)
                ?? throw new InvalidOperationException("A new leader's side loses its exact source identity.");
            var newSide = after.DirectChildren.SingleOrDefault(side => OwnerKey(side, null) == mappedKey)
                ?? throw new InvalidOperationException("A new leader's side has no unique candidate owner.");
            if (newSide.IsAmended || newSide.HasUnresolvedContributions)
                throw new InvalidOperationException("The candidate side becomes amended or unresolved.");
            var oldLeaders = oldSide.DirectChildren.Where(child => child.TagName == "leader").ToArray();
            var newLeaders = newSide.DirectChildren.Where(child => child.TagName == "leader").ToArray();
            if (newLeaders.Length != oldLeaders.Length + edit.Leaders.Count)
                throw new InvalidOperationException("The candidate does not produce the exact requested nested leader count.");
            for (var index = 0; index < oldLeaders.Length; index++)
                if (OwnerKey(oldLeaders[index], change) is not { } key || key != OwnerKey(newLeaders[index], null))
                    throw new InvalidOperationException("An existing leader lost its exact occurrence or order.");
            var region = file.Regions.SingleOrDefault(region => region.Kind == "LeaderAddition" && ReferenceEquals(region.LeaderAddition, edit))
                ?? throw new InvalidOperationException("New leaders have no exact immutable insertion region.");
            if (region.Before.Length != 0 || region.Before.StartOffset != snapshot.InsertionOffset ||
                baseline.GetProjectRegionSpans(region.Before).Count != 0 ||
                region.After.Text != WccScenarioLeaderAdditionPlanner.PlanPatch(edit).ReplacementText)
                throw new InvalidOperationException("A leader addition consumes existing source or differs from its literal payload.");
            var added = newLeaders.Skip(oldLeaders.Length).ToArray();
            for (var index = 0; index < added.Length; index++)
            {
                var leader = added[index];
                var payload = edit.Leaders[index];
                if (!ReferenceEquals(leader.Parent, newSide) || leader.IsAmended || leader.HasUnresolvedContributions ||
                    !IsProjectLiteralOpening(leader, region.After) || leader.ClosingOrigin is not { } closing ||
                    !InsideProposedScalar(closing, region.After) || leader.DirectChildren.Count != 0 || payload.Preset is null && leader.Fields.Count != payload.FieldCount)
                    throw new InvalidOperationException("A new leader is not its exact whole literal field-only subtree.");
                if (payload.Preset is { } preset)
                {
                    ValidateAppliedPresetOwner(leader, preset, payload.ValuesIn(context).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                        context, region.After, proposed.Evaluation, change);
                    continue;
                }
                foreach (var pair in payload.ValuesIn(context))
                {
                    if (!WccScenarioFieldValueCodec.TryGetDefinition("leader", pair.Key, out var definition) ||
                        !leader.Fields.TryGetValue(pair.Key, out var field) || !field.IsKnown || field.Contributions.Count != 1 ||
                        !WccScenarioFieldValueCodec.Equivalent(definition, field.ValueText, pair.Value))
                        throw new InvalidOperationException("A new leader field does not resolve to its requested literal: " + pair.Key);
                    var contribution = field.Contributions[0];
                    if (contribution.AssignmentOrigin is not { } assignment || !InsideProposedScalar(assignment, region.After) ||
                        !IsProjectLiteralOrigin(assignment, contribution.ExpansionStack, region.After) ||
                        contribution.ValueOrigins.Count == 0 || contribution.ValueOrigins.Any(origin => !InsideProposedScalar(origin, region.After)))
                        throw new InvalidOperationException("A new leader field lacks its exact local literal source contribution.");
                }
            }
            // Mapped output must be precisely the inserted leader subtrees. No
            // side field, extra tag, invocation or external subtree is masked.
            var spans = proposed.GetProjectRegionSpans(region.After);
            bool Inside(int offset) => spans.Any(span => offset >= span.Start && offset < span.End);
            foreach (var leader in added)
            {
                if (leader.OpeningOutputOffset is not { } start || leader.ClosingOutputOffset is not { } closing)
                    throw new InvalidOperationException("A new leader has no exact expanded boundaries.");
                for (var offset = start; offset < closing + leader.ClosingOrigin!.Length; offset++)
                    if (!Inside(offset) && !char.IsWhiteSpace(mask[offset]))
                        throw new InvalidOperationException("A new leader consumes output outside its literal insertion.");
            }
            foreach (var span in spans)
                for (var offset = span.Start; offset < span.End; offset++)
                    if (!char.IsWhiteSpace(mask[offset]) && !added.Any(leader =>
                        offset >= leader.OpeningOutputOffset!.Value && offset < leader.ClosingOutputOffset!.Value + leader.ClosingOrigin!.Length))
                        throw new InvalidOperationException("A new leader insertion emits output outside its exact nested subtrees.");
        }
    }
}
