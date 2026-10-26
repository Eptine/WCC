using System.Globalization;
using Wcc.Core.Library;
using Wcc.Core.Maps;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateUnitInstanceBinding(WccScenarioUnitInstanceEdit edit,
        WccScenarioFieldEditSessionState state,
        WmlStaticScalarOwner scenario, WmlStaticScalarOwner side, WmlStaticScalarProjection projection,
        WccStaticSourceCaptureResult capture)
    {
        var snapshot = edit.Snapshot;
        if (state.ReviewScope != WccScenarioFieldReviewScope.Scenario || state.Plan is null || snapshot.Plan is null ||
            !ReferenceEquals(edit.Source, state.Source) || !ReferenceEquals(side.Parent, scenario) || !snapshot.SameOwner(side) ||
            state.Plan.CampaignMainTextHash != snapshot.Plan.CampaignMainTextHash || state.Plan.CampaignDefine != snapshot.Plan.CampaignDefine ||
            !PathComparer.Equals(state.Plan.CampaignMainSourcePath, snapshot.Plan.CampaignMainSourcePath) ||
            !state.Plan.Contexts.Select(context => context.SemanticKey).SequenceEqual(snapshot.Plan.Contexts.Select(context => context.SemanticKey)))
            throw new InvalidOperationException("Initial unit placement no longer belongs to the exact side publication and declared contexts.");
        var currentPresets = WccCampaignLocalPresetCatalog.Build(capture, state.Plan).Entries;
        var fresh = WccScenarioUnitInstancePlanner.Read(state.Source, side, state.Plan, projection, currentPresets);
        if (!WccScenarioUnitInstancePlanner.SameLayout(snapshot, fresh))
            throw new InvalidOperationException("The initial unit source or exact side layout changed; reopen placement.");
        _ = WccScenarioUnitInstancePlanner.PlanPatch(edit);
    }

    private static void ValidateProjectUnitInstances(WccProjectComposedFile file, PairedEvaluation baseline, PairedEvaluation proposed,
        WmlStaticScalarOwner before, WmlStaticScalarOwner after, WmlEvaluationContext context, WccScenarioFieldSourceChange change,
        WccStaticSourceCaptureResult capture, WmlStaticSingleplayerEnvironment environment)
    {
        if (file.Request.UnitInstances.Count == 0) return;
        if (context.CampaignDefine is null) return;
        foreach (var edit in file.Request.UnitInstances)
        {
            var snapshot = edit.Snapshot;
            var oldSide = before.DirectChildren.SingleOrDefault(snapshot.SameOwner)
                ?? throw new InvalidOperationException("Initial unit placement has no exact side in a declared difficulty.");
            var fresh = WccScenarioUnitInstancePlanner.Read(file.Request.Source, oldSide, snapshot.Plan, baseline.Projection, snapshot.Presets);
            if (!WccScenarioUnitInstancePlanner.SameLayoutInContext(snapshot, fresh, edit.Entry, context))
                throw new InvalidOperationException("Initial unit ownership differs between difficulties; use RAW for generated layouts.");
            var key = OwnerKey(oldSide, change) ?? throw new InvalidOperationException("Initial unit placement loses its parent side identity.");
            var newSide = after.DirectChildren.SingleOrDefault(side => OwnerKey(side, null) == key)
                ?? throw new InvalidOperationException("Initial unit placement has no unique candidate side.");
            var region = file.Regions.Single(region => region.Kind == "UnitInstance" && ReferenceEquals(region.UnitInstance, edit));
            var expectedPatch = WccScenarioUnitInstancePlanner.PlanPatch(edit);
            if (region.Before.StartOffset != expectedPatch.Start || region.Before.Length != expectedPatch.Length || region.After.Text != expectedPatch.ReplacementText)
                throw new InvalidOperationException("The initial unit region differs from its exact immutable source recipe.");
            var oldActive = edit.Entry is not null && (edit.Entry.DifficultyDefine is null || edit.Entry.DifficultyDefine == context.DifficultyDefine);
            var newActive = edit.Spec is not null && (edit.Spec.DifficultyDefine is null || edit.Spec.DifficultyDefine == context.DifficultyDefine);
            var oldUnits = oldSide.DirectChildren.Where(unit => unit.TagName == "unit" && unit.OpeningOrigin is { } opening && InsideProposedScalar(opening, region.Before)).ToArray();
            var newUnits = newSide.DirectChildren.Where(unit => unit.TagName == "unit" && unit.OpeningOrigin is { } opening && InsideProposedScalar(opening, region.After)).ToArray();
            if (oldUnits.Length != (oldActive ? 1 : 0) || newUnits.Length != (newActive ? 1 : 0))
                throw new InvalidOperationException("The initial unit does not have its exact requested all/one-difficulty presence.");
            ValidateFootprint(baseline, region.Before, oldUnits);
            ValidateFootprint(proposed, region.After, newUnits);
            if (!newActive) continue;
            var expectedText = expectedPatch.ReplacementText;
            if (edit.Spec?.Preset is { } preset) expectedText = expectedText.Replace("{" + preset.Name + "}", BindProposedPreset(preset, change).Body.Text, StringComparison.Ordinal);
            var literal = new WmlStaticSource("wcc-unit-instance-expected", expectedText, isAuthored: false);
            var evaluated = new WmlStaticEvaluator().Evaluate(new(literal, context,
                KnownUndefinedSymbols: snapshot.Plan!.Difficulties.Select(row => row.Define).Where(define => define != context.DifficultyDefine).ToArray(),
                IsInitialEnvironmentComplete: true, Limits: new(MaxOutputCharacters: 2 * 1024 * 1024, MaxSourceCharacters: 2 * 1024 * 1024)), proposed.CancellationToken);
            var desired = WmlStaticScalarProjection.Create(evaluated, proposed.CancellationToken);
            var roots = desired.Owners.Where(owner => owner.Parent is null).ToArray();
            if (!evaluated.IsComplete || desired.Diagnostics.Count != 0 || roots.Length != 1 || roots[0].TagName != "unit")
                throw new InvalidOperationException("The initial unit literal recipe cannot be independently verified.");
            ValidateUnitTree(newUnits[0], roots[0], region.After, edit.Spec!.Preset, change);
            // Values equal to the captured preset default emit no local assignment;
            // their candidate value follows the shared replacement, as RenderUnit does.
            foreach (var pair in edit.Spec!.Values.Where(pair => edit.Spec.Preset?.Payload?.Values.GetValueOrDefault(pair.Key) != pair.Value))
            {
                WccScenarioFieldValueCodec.TryGetDefinition("leader", pair.Key, out var definition);
                if (!WccScenarioFieldValueCodec.Equivalent(definition!, newUnits[0].GetKnownValue(pair.Key), pair.Value))
                    throw new InvalidOperationException("The initial unit field differs from its requested literal: " + pair.Key);
            }
            ValidateMapPlacement(after, edit.Spec);
        }
        ValidateInitialUnitIds(after);
        var requestedTypes = file.Request.UnitInstances.Where(edit => edit.Spec is not null &&
            (edit.Spec.DifficultyDefine is null || edit.Spec.DifficultyDefine == context.DifficultyDefine)).Select(edit => ProposedUnitValue(edit.Spec!, "type", change)!).Distinct(StringComparer.Ordinal).ToArray();
        if (requestedTypes.Length != 0)
            ValidateUnitInstanceTypes(file, requestedTypes, context, change, capture, environment, proposed.CancellationToken);

        static void ValidateFootprint(PairedEvaluation view, WmlStaticSourceRange range, IReadOnlyList<WmlStaticScalarOwner> units)
        {
            var spans = view.GetProjectRegionSpans(range);
            var mask = WmlAuthoredStructureScanner.Scan(view.Evaluation.Text).MaskedText;
            bool Inside(int offset) => spans.Any(span => offset >= span.Start && offset < span.End);
            foreach (var unit in units)
            {
                if (!IsProjectLiteralOpening(unit, range) || unit.IsAmended || unit.HasUnresolvedContributions || unit.ClosingOrigin is not { } close ||
                    !InsideProposedScalar(close, range) || unit.OpeningOutputOffset is not { } start || unit.ClosingOutputOffset is not { } end)
                    throw new InvalidOperationException("An initial unit has no complete exact local subtree.");
                for (var offset = start; offset < end + close.Length; offset++)
                    if (!Inside(offset) && !char.IsWhiteSpace(mask[offset])) throw new InvalidOperationException("An initial unit consumes output outside its approved source region.");
            }
            foreach (var span in spans)
                for (var offset = span.Start; offset < span.End; offset++)
                    if (!char.IsWhiteSpace(mask[offset]) && !units.Any(unit => offset >= unit.OpeningOutputOffset!.Value && offset < unit.ClosingOutputOffset!.Value + unit.ClosingOrigin!.Length))
                        throw new InvalidOperationException("An initial unit recipe emits another side field or unrelated subtree.");
        }
    }

    private static void ValidateUnitTree(WmlStaticScalarOwner actual, WmlStaticScalarOwner expected, WmlStaticSourceRange range,
        WccCampaignLocalPresetEntry? preset, WccScenarioFieldSourceChange change)
    {
        if (actual.TagName != expected.TagName || actual.IsAmended || actual.HasUnresolvedContributions || actual.Fields.Count != expected.Fields.Count ||
            actual.DirectChildren.Count != expected.DirectChildren.Count || !Exact(actual.OpeningOrigin, actual.OpeningExpansionStack)
            || !Exact(actual.ClosingOrigin, actual.ClosingExpansionStack))
            throw new InvalidOperationException("An initial unit differs from its exact requested fields or children.");
        foreach (var pair in expected.Fields)
            if (!actual.Fields.TryGetValue(pair.Key, out var field) || !field.IsKnown || field.ValueText != pair.Value.ValueText
                || field.Contributions.Count != pair.Value.Contributions.Count || field.Contributions.Count == 0
                || field.Contributions.Any(contribution => !Exact(contribution.AssignmentOrigin, contribution.ExpansionStack)
                    || !contribution.ValueMappingIsExact || contribution.CompositeAssignmentKeys.Count != 0
                    || contribution.ValueOrigins.Any(origin => !Exact(origin, contribution.ExpansionStack))))
                throw new InvalidOperationException("An initial unit field has no exact requested local or selected-preset contribution: " + pair.Key);
        for (var index = 0; index < expected.DirectChildren.Count; index++) ValidateUnitTree(actual.DirectChildren[index], expected.DirectChildren[index], range, preset, change);
        bool Exact(WmlStaticSourceRange? origin, IReadOnlyList<WmlStaticExpansionFrame> stack)
        {
            if (origin is null) return false;
            if (InsideProposedScalar(origin, range) && IsProjectLiteralOrigin(origin, stack, range)) return true;
            var local = stack.Where(frame => frame.InvocationOrigins.Any(invocation => InsideProposedScalar(invocation, range))).ToArray();
            return preset is not null && local.Length == 1 && local[0].MacroName == preset.Name
                && SameRange(local[0].Definition, MappedPresetDefinition(preset, change)) && local[0].InvocationOrigins.Count == 1
                && local[0].InvocationOrigins[0].Text == "{" + preset.Name + "}" && InsideProposedScalar(local[0].InvocationOrigins[0], range)
                && InsideProposedScalar(origin, BindProposedPreset(preset, change).Body);
        }
    }
    private static void ValidateMapPlacement(WmlStaticScalarOwner scenario, WccScenarioUnitInstanceSpec spec)
    {
        if (!int.TryParse(spec.Values["x"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x) || x <= 0 ||
            !int.TryParse(spec.Values["y"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y) || y <= 0)
            throw new InvalidOperationException("Initial placement needs a positive numeric map coordinate pair.");
        // The unit's literal coordinates do not depend on how Wesnoth loads its
        // map. Keep available literal-map bounds checks without requiring a map
        // resource capture merely to author the already checked unit subtree.
        var map = scenario.GetKnownValue("map_data");
        if (map is null || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(map)) return;
        var text = WccScenarioFieldValueCodec.DecodeLiteral(map);
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > 8 * 1024 * 1024) throw new InvalidOperationException("The captured initial map exceeds the bounded placement size.");
        var document = WesnothMapParser.Parse(text, new() { DefaultBorderSize = 1, IgnoreLegacyBorderSize = true });
        if (document.Diagnostics.Count != 0 || document.Width <= 2 || document.Height <= 2 ||
            document.GetCell(x, y) is not { IsBorder: false } ||
            document.Rows.SelectMany(row => row).Any(cell => cell.TerrainCode.IndexOfAny(['{', '}', '$', '"', '<', '>', '=', '[', ']', '#']) >= 0))
            throw new InvalidOperationException("Choose one playable numeric map hex in every difficulty where this initial unit is present.");
    }

    private static void ValidateInitialUnitIds(WmlStaticScalarOwner scenario)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var side in scenario.DirectChildren.Where(side => side.TagName == "side"))
        foreach (var unit in new[] { side }.Where(owner => owner.GetKnownValue("type") is { } type &&
                     WccScenarioFieldValueCodec.DecodeLiteral(type) is not ("" or "null") &&
                     WccScenarioFieldValueCodec.DecodeLiteral(owner.GetKnownValue("no_leader")) is not ("yes" or "true"))
                 .Concat(side.DirectChildren.Where(child => child.TagName is "leader" or "unit")))
        {
            var identity = unit.ResolveField("id");
            if (identity.State == WmlStaticScalarResolutionState.ProvenAbsent) continue;
            if (identity.State != WmlStaticScalarResolutionState.Known || identity.ValueText is null || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(identity.ValueText))
                throw new InvalidOperationException("Unique initial unit and leader IDs need complete literal identity context.");
            var id = WccScenarioFieldValueCodec.DecodeLiteral(identity.ValueText);
            if (id.Length != 0 && !ids.Add(id)) throw new InvalidOperationException("Initial unit/leader ID '" + id + "' is already used in this difficulty.");
        }
    }

    private static void ValidateUnitInstanceTypes(WccProjectComposedFile file, IReadOnlyList<string> requested,
        WmlEvaluationContext context, WccScenarioFieldSourceChange change, WccStaticSourceCaptureResult capture, WmlStaticSingleplayerEnvironment environment, CancellationToken cancellationToken)
    {
        var plan = file.Request.UnitInstances[0].Snapshot.Plan!;
        var unitRequests = file.Request.UnitInstances.Where(edit => edit.Spec is not null)
            .Select(edit => (Spec: edit.Spec!, Entry: edit.Entry)).ToArray();
        ValidateRequestedUnitTypes(requested, context, change, capture, environment, cancellationToken, plan, unitRequests);
    }

    private static void ValidateRequestedUnitTypes(IReadOnlyList<string> requested, WmlEvaluationContext context,
        WccScenarioFieldSourceChange change, WccStaticSourceCaptureResult capture,
        WmlStaticSingleplayerEnvironment environment, CancellationToken cancellationToken, WmlCampaignEvaluationPlan plan,
        (WccScenarioUnitInstanceSpec Spec, WccScenarioUnitInstanceEntry? Entry)[] unitRequests)
    {
        if (!capture.HasCompleteCoreUnitPrelude) throw new InvalidOperationException("Initial unit placement needs the exact captured Core unit registry.");

        var undefined = plan.Difficulties.Select(row => row.Define).Concat(["EDITOR", "MULTIPLAYER"])
            .Where(symbol => !context.DefinedSymbols.Contains(symbol)).Distinct(StringComparer.Ordinal).ToArray();
        var registered = new WmlStaticEvaluator().Evaluate(new(change.Substitute(capture.EntrySource!), context,
            new[] { environment.DefinitionsSource }.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources).Select(change.Substitute).ToArray(),
            undefined, true, new CandidateResolver(capture.IncludeResolver, change),
            WmlStaticEvaluationLimits.Campaign), cancellationToken);
        var projection = WmlStaticScalarProjection.Create(registered, cancellationToken);
        var inventory = WmlUnitReferenceProjection.Create(registered, projection, cancellationToken);
        if (!inventory.HasCompleteDeclaredInventory) throw new InvalidOperationException("The captured Core, campaign and declared dependency type registration inventory is incomplete.");
        var core = capture.IncludeResolver.Resolve("core/units.cfg", capture.EntrySource!).Sources;
        if (core.Count != 1) throw new InvalidOperationException("The captured Core type root is ambiguous.");
        // The capture contains only the selected revision and its declared
        // dependency closure. Registration must also be active and unique here;
        // unrelated installed or indexed add-ons cannot contribute candidates.
        var captured = capture.Sources.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources)
            .DistinctBy(source => (source.Path, source.ContentHash)).ToArray();
        foreach (var id in requested)
        {
            var matches = inventory.Declarations.Where(type => type.Id == id).ToArray();
            if (matches.Length != 1 || matches[0].Owner.OpeningOrigin is not { } opening ||
                !opening.Source.IsAuthored || captured.Count(source => PathComparer.Equals(source.Path, opening.Source.Path)
                                                                       && source.ContentHash == opening.Source.ContentHash && source.Text == opening.Source.Text && source.IsAuthored) != 1)
                throw new InvalidOperationException("Unit type '" + id + "' needs one uniquely registered captured Core, campaign or declared dependency definition in this difficulty.");
            IReadOnlyList<string>? availableGenders = null;
            var gendersResolved = false;
            foreach (var edit in unitRequests.Where(edit => edit.Spec is { } spec && ProposedUnitValue(spec, "type", change) == id &&
                                                            (spec.DifficultyDefine is null || spec.DifficultyDefine == context.DifficultyDefine)))
            {
                var spec = edit.Spec!;
                var gender = ProposedUnitValue(spec, "gender", change);
                // Unrelated edits preserve imported literal data. A newly chosen
                // gender, type or broader scope needs a fresh all-context proof.
                if (gender is null || edit.Entry is { } entry && entry.Values.GetValueOrDefault("gender") == gender &&
                    entry.Values.GetValueOrDefault("type") == id && entry.DifficultyDefine == spec.DifficultyDefine) continue;
                if (!gendersResolved)
                {
                    availableGenders = WccUnitTypeGenderOptions.Read(WccUnitTypeEffectiveDisplay.Create(registered, projection, matches[0].Owner, inventory, cancellationToken));
                    gendersResolved = true;
                }
                if (availableGenders is null || !availableGenders.Contains(gender, StringComparer.Ordinal))
                    throw new InvalidOperationException("Gender '" + gender + "' is not available for unit type '" + id + "' in " + context.DisplayName + ".");
            }
        }
    }

    private static string? ProposedUnitValue(WccScenarioUnitInstanceSpec spec, string key, WccScenarioFieldSourceChange change)
    {
        if (spec.Values.TryGetValue(key, out var local) && spec.Preset?.Payload?.Values.GetValueOrDefault(key) != local) return local;
        return spec.Preset is { } preset ? BindProposedPreset(preset, change).Payload.Values.GetValueOrDefault(key) : local;
    }
}
