using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static bool IsExactRemovedSideTopologyRegion(WccProjectAuthorizedRegion region) =>
        region.Kind == "SideTopology" && region.Before.Length > 0 && region.After.Length == 0 &&
        region.SideTopology is { } recipe && recipe.Removals.Any(entry => SameRange(entry.TargetRange, region.Before) ||
            entry.AmendmentRanges.Any(range => SameRange(range, region.Before)));

    private static void ValidateSideTopologyBinding(WccScenarioSideTopologyEdit edit,
        WccScenarioFieldEditSessionState state, WmlStaticScalarOwner anchor,
        WmlStaticScalarProjection projection, WccStaticSourceCaptureResult capture)
    {
        var snapshot = edit.Snapshot;
        if (state.ReviewScope != WccScenarioFieldReviewScope.Scenario || state.Plan is null || snapshot.Plan is null ||
            !ReferenceEquals(edit.Source, state.Source) || !WccScenarioSideTopologyPlanner.SameOwner(snapshot.Owner, anchor) ||
            state.Plan.CampaignMainTextHash != snapshot.Plan.CampaignMainTextHash ||
            state.Plan.CampaignDefine != snapshot.Plan.CampaignDefine ||
            !PathComparer.Equals(state.Plan.CampaignMainSourcePath, snapshot.Plan.CampaignMainSourcePath) ||
            !state.Plan.Contexts.Select(context => context.SemanticKey).SequenceEqual(snapshot.Plan.Contexts.Select(context => context.SemanticKey)))
            throw new InvalidOperationException("Side topology no longer belongs to the exact scenario publication and fixed campaign contexts.");
        if (edit.Additions.Count != edit.AddedCount) throw new InvalidOperationException("The ordered side additions changed.");
        WccScenarioSideTopologyPlanner.CreateEdit(snapshot, edit.Removals, edit.Additions);
        var fresh = WccScenarioSideTopologyPlanner.Read(state.Source, anchor, state.Plan, projection);
        if (!WccScenarioSideTopologyPlanner.SameLayout(snapshot, fresh))
            throw new InvalidOperationException("The exact initial side list or its amendment footprint changed; reload its source.");
        foreach (var removed in edit.Removals)
            if (!fresh.Entries.Any(entry => entry.Number == removed.Number && entry.SameOwner(removed.SideOwner)))
                throw new InvalidOperationException("A removed side has no unique exact captured literal owner.");
        // Reuse the existing bounded physical helper-closure inspection. Silent
        // definitions/undefinitions and forwarded arguments are inspected too;
        // output-free state changes cannot be hidden by removing a whole side.
        var inspection = new WccScenarioInitialSettingsEdit(snapshot.ScenarioRange!, events:
            edit.Removals.SelectMany(entry => new[] { entry.TargetRange! }.Concat(entry.AmendmentRanges))
                .Select(range => new WccScenarioEventSourceEdit(WccScenarioEventSourceEditKind.RemoveLiteral, range)));
        ValidateEventDirectiveClosure(inspection, capture);
    }

    private static void ValidateProjectSideTopology(WccProjectComposedFile file, PairedEvaluation baseline,
        PairedEvaluation proposed, WmlStaticScalarOwner before, WmlStaticScalarOwner after,
        WmlEvaluationContext context, WccScenarioFieldSourceChange change)
    {
        if (file.Request.SideTopology is not { } edit) return;
        var snapshot = edit.Snapshot;
        if (context.CampaignDefine is null) return; // The source is inactive in MENU; full outside comparison still runs.
        var fresh = WccScenarioSideTopologyPlanner.Read(file.Request.Source, before, snapshot.Plan, baseline.Projection);
        if (!WccScenarioSideTopologyPlanner.SameLayout(snapshot, fresh))
            throw new InvalidOperationException("Initial side order, numbering or modifier ownership differs between declared difficulties; use RAW.");
        var oldSides = before.DirectChildren.Where(owner => owner.TagName == "side").ToArray();
        var newSides = after.DirectChildren.Where(owner => owner.TagName == "side").ToArray();
        var retained = oldSides.Length - edit.Removals.Count;
        if (newSides.Length != retained + edit.AddedCount)
            throw new InvalidOperationException("The candidate does not produce the exact requested initial side count.");
        for (var index = 0; index < retained; index++)
            if (OwnerKey(oldSides[index], change) is not { } oldKey || oldKey != OwnerKey(newSides[index], null))
                throw new InvalidOperationException("A retained side lost its exact source occurrence or declaration order.");
        var oldRoot = RootEntries(baseline, before);
        var newRoot = RootEntries(proposed, after);
        var regions = file.Regions.Where(region => region.Kind == "SideTopology").ToArray();
        foreach (var region in regions)
        {
            if (region.Before.Length == 0)
            {
                if (baseline.GetProjectRegionSpans(region.Before).Count != 0)
                    throw new InvalidOperationException("A side insertion unexpectedly consumes existing expanded output.");
                var emitted = RequireWholeRootFragments(proposed, newRoot, region.After, "side");
                if (emitted.Length != edit.AddedCount)
                    throw new InvalidOperationException("The inserted region does not contain exactly the new literal sides.");
                for (var index = retained; index < newSides.Length; index++)
                {
                    var side = newSides[index];
                    if (side.IsAmended || !IsProjectLiteralOpening(side, region.After) || side.ClosingOrigin is not { } closing ||
                        !InsideProposedScalar(closing, region.After) || side.GetKnownValue("side") != (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                        throw new InvalidOperationException("An added side differs from its minimal exact numbered literal template.");
                    var addition = edit.Additions[index - retained];
                    if (addition.Preset is { } preset)
                    {
                        var overrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["side"] = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                        if (addition.SaveIdOverride is { } saveId) overrides.Add("save_id", saveId);
                        ValidateAppliedPresetOwner(side, preset, overrides, context, region.After, proposed.Evaluation, change);
                    }
                    else if (side.DirectChildren.Count != 0 || side.Fields.Count != 1)
                        throw new InvalidOperationException("An added minimal side also contains unrequested fields or children.");
                }
                continue;
            }
            if (region.After.Length != 0 || proposed.GetProjectRegionSpans(region.After).Count != 0)
                throw new InvalidOperationException("A side removal carries unexpected replacement or expanded output.");
            var removed = edit.Removals.SingleOrDefault(entry => SameRange(entry.TargetRange, region.Before) ||
                entry.AmendmentRanges.Any(range => SameRange(range, region.Before)))
                ?? throw new InvalidOperationException("A side removal has no exact immutable target recipe.");
            var initial = SameRange(removed.TargetRange, region.Before);
            var fragments = RequireWholeRootFragments(baseline, oldRoot, region.Before, initial ? "side" : "+side");
            if (initial && fragments.Length != 1 || !initial && fragments.Length == 0)
                throw new InvalidOperationException("A removal does not consume its exact side or amendment cardinality.");
            foreach (var fragment in fragments)
            {
                var target = initial ? oldSides.SingleOrDefault(side => side.OpeningOutputOffset == fragment.StartOffset + Leading(fragment.Text))
                    : oldSides.LastOrDefault(side => side.OpeningOutputOffset is { } opening && opening < fragment.StartOffset);
                if (target is null || !removed.SameOwner(target))
                    throw new InvalidOperationException("A removed amendment belongs to another side occurrence.");
            }
        }

        static ScenarioContentSourceEntry[] RootEntries(PairedEvaluation evaluated, WmlStaticScalarOwner scenario)
        {
            if (scenario.IsAmended || scenario.OpeningOutputOffset is not { } start || scenario.ClosingOutputOffset is not { } closing ||
                scenario.ClosingOrigin is not { } origin)
                throw new InvalidOperationException("Side topology needs exact unamended scenario output boundaries.");
            return ScenarioContentSourceInventory.Build(evaluated.Evaluation.Text[start..(closing + origin.Length)])
                .Select(entry => entry with { StartOffset = entry.StartOffset + start, EndOffsetExclusive = entry.EndOffsetExclusive + start }).ToArray();
        }

        static ScenarioContentSourceEntry[] RequireWholeRootFragments(PairedEvaluation evaluated,
            IReadOnlyList<ScenarioContentSourceEntry> root, WmlStaticSourceRange range, string kind)
        {
            var spans = evaluated.GetProjectRegionSpans(range);
            bool Inside(int offset) => spans.Any(span => offset >= span.Start && offset < span.End);
            var fragments = root.Where(entry => Inside(entry.StartOffset + Leading(entry.Text))).ToArray();
            // The inventory includes an authored closing-line comment. Its
            // exact tag/subtree is proved below; the preserved comment is not
            // part of the removal footprint and must not become authority.
            if (fragments.Any(entry => entry.Kind != ScenarioContentSourceKind.ChildTag || entry.Name != kind))
                throw new InvalidOperationException("A side footprint also produces another root owner or incomplete modifier.");
            var masked = WmlAuthoredStructureScanner.Scan(evaluated.Evaluation.Text).MaskedText;
            foreach (var span in spans)
                for (var offset = span.Start; offset < span.End; offset++)
                    if (!char.IsWhiteSpace(masked[offset]) && !fragments.Any(entry => offset >= entry.StartOffset && offset < entry.EndOffsetExclusive))
                        throw new InvalidOperationException("A side footprint changes output outside its exact initial side/amendment subtrees.");
            foreach (var fragment in fragments)
                for (var offset = fragment.StartOffset; offset < fragment.EndOffsetExclusive; offset++)
                    if (!Inside(offset) && !char.IsWhiteSpace(masked[offset]))
                        throw new InvalidOperationException("A side footprint consumes nonlocal source output.");
            return fragments;
        }
        static int Leading(string text) => text.Length - text.TrimStart().Length;
    }
}
