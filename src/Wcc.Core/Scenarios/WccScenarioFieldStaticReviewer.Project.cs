using Wcc.Core.Library;
using Wcc.Core.Wml;
using Wcc.Core.Campaigns;
using Wcc.Core.Units;

namespace Wcc.Core.Scenarios;

internal sealed record WccProjectPreparedReview(WccProjectSourceEditCandidate Candidate,
    IReadOnlyList<WccProjectComposedFile> Files, string ClosureIdentity,
    IReadOnlyList<string> ScopeIdentities)
{
    public bool IncludesCoreUnitData { get; init; }
    public WccCampaignPackageEdit? PackageEdit { get; init; }
    public WccLibrarySnapshot? PackageSnapshot { get; init; }
    public string? PackageRawReviewIdentity { get; init; }
    public string? PackageRawCatalogIdentity { get; init; }
}

internal static partial class WccScenarioFieldStaticReviewer
{
    internal static Task<WccProjectPreparedReview> ReviewProjectAsync(
        IReadOnlyList<WccScenarioFieldEditSessionState> states,
        IReadOnlyList<WccProjectSourceEditRequest> requests, CancellationToken cancellationToken)
        => ReviewProjectWithDiagnosticsAsync(states, requests, cancellationToken, null);

    internal static async Task<WccProjectPreparedReview> ReviewProjectWithDiagnosticsAsync(
        IReadOnlyList<WccScenarioFieldEditSessionState> states,
        IReadOnlyList<WccProjectSourceEditRequest> requests, CancellationToken cancellationToken,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        var primary = states[0];
        if (requests.Any(request => request.PackageEdit is not null))
            return await ReviewManagedPackageAsync(states, requests, cancellationToken, diagnostics).ConfigureAwait(false);
        if (states.Any(state => state.EntryReview is not null) && requests.All(IsPackageRawOnly))
            return await ReviewPackageRawProjectAsync(states, requests, cancellationToken, diagnostics).ConfigureAwait(false);
        var unitCreation = requests.Select(request => request.UnitTypeCreation).SingleOrDefault(edit => edit is not null);
        var unitUpdate = requests.Select(request => request.UnitTypeUpdate).SingleOrDefault(edit => edit is not null);
        var includesUnitData = unitCreation is not null || unitUpdate is not null || requests.Any(request => request.UnitInstances.Count != 0) || RequestedEventGraphs(requests, null).Any(graph => graph.Actions.Any(action => action.Kind == WccScenarioEventActionKind.SpawnUnit));
        foreach (var state in states)
            if (CheckScope(state) is { } scopeRefusal) throw new InvalidOperationException(scopeRefusal);
        var entry = primary.Plan?.CampaignMainSourcePath ?? primary.Source.Path;
        WccStaticSourceCaptureResult capture;
        diagnostics?.Add(WccSourceSaveCount.SourceCaptures);
        using (diagnostics?.Measure(WccSourceSavePhase.SourceCapture))
            capture = await WccStaticSourceCapture.CaptureAsync(primary.Snapshot, primary.RootRevisionId,
                entry, cancellationToken: cancellationToken, configuredAddonsPath: primary.ConfiguredAddonsPath,
                includeCoreUnits: includesUnitData).ConfigureAwait(false);
        diagnostics?.ObserveCapture(capture);
        foreach (var state in states)
            if (CheckCapture(state, capture) is { } refusal) throw new InvalidOperationException(refusal);
        if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            throw new InvalidOperationException("The exact pinned Core version cannot supply a known singleplayer environment.");
        var selectedEntry = primary.EntryReview is { } entryReview
            ? WccSelectedCampaignEntryAdapter.Create(capture, entryReview.SelectedEntryIdentity, primary.Plan, cancellationToken) : null;
        foreach (var state in states.Where(state => state.EntryReview is null && state.ReviewScope == WccScenarioFieldReviewScope.Scenario))
            if (WccScenarioFieldContextScope.VerifyWithDiagnostics(capture, state.Plan, state.Context, environment!, cancellationToken, diagnostics) is { } refusal)
                throw new InvalidOperationException(refusal);
        var contexts = (primary.Plan is null ? states.Select(state => state.Context)
            : new[] { new WmlEvaluationContext(targetCompatibility: primary.Context.TargetCompatibility) }
                .Concat(primary.Plan.Contexts)).DistinctBy(context => context.SemanticKey).ToArray();
        diagnostics?.Add(WccSourceSaveCount.ReviewContexts, contexts.Length);
        var baselines = contexts.ToDictionary(context => context.SemanticKey,
            context => EvaluateWithDiagnostics(capture, context, primary.Plan, environment!, null, cancellationToken, diagnostics), StringComparer.Ordinal);
        foreach (var context in contexts)
            if (unitUpdate is null) RequireComplete(baselines[context.SemanticKey], context, "Existing campaign");
            else ValidateUnitUpdateBaseline(unitUpdate, baselines[context.SemanticKey], context, capture, primary.Plan, environment!, cancellationToken);

        if (selectedEntry is not null)
            foreach (var context in contexts)
                baselines[context.SemanticKey] = ScopeSelectedCampaignMenu(baselines[context.SemanticKey], selectedEntry.Owner, null);

        WmlStaticScalarOwner? campaignAnchor = null;
        if (primary.Plan is { } plan)
        {
            var menu = baselines[contexts[0].SemanticKey];
            campaignAnchor = selectedEntry is null
                ? menu.Projection.Owners.Where(owner => owner.TagName == "campaign" && owner.Parent is null).SingleOrDefault()
                : FindOwner(menu.Projection, selectedEntry.Owner, null);
            if (campaignAnchor is null) throw new InvalidOperationException("The selected campaign has no unique complete captured menu owner.");
            if (VerifyCampaignContextPlan(campaignAnchor, plan, selectedEntry?.OwnerSource ?? capture.EntrySource!, selectedEntry is null) is { } refusal)
                throw new InvalidOperationException(refusal);
        }

        var requestedOwners = states.Select(state => state.Owner)
            .Concat(requests.SelectMany(request => request.Fields).Select(proposal => proposal.Route.Owner))
            .Concat(requests.SelectMany(request => request.DifficultyFields).Select(edit => edit.Snapshot.Owner))
            .Concat(requests.SelectMany(request => request.InitialSettings?.SideFlags ?? []).Select(flag => flag.SideOwner))
            .Concat(requests.Where(request => request.SideTopology is not null).Select(request => request.SideTopology!.Snapshot.Owner))
            .Concat(requests.SelectMany(request => request.LeaderAdditions).Select(edit => edit.Snapshot.SideOwner))
            .Concat(requests.SelectMany(request => request.UnitInstances).Select(edit => edit.Snapshot.SideOwner))
            .Concat(campaignAnchor is null ? [] : new[] { campaignAnchor }).ToArray();
        var baselineOwners = baselines.ToDictionary(pair => pair.Key,
            pair => CreateProjectOwnerLookup(pair.Value.Projection, requestedOwners, null, cancellationToken, diagnostics), StringComparer.Ordinal);

        var files = new List<WccProjectComposedFile>();
        var definitionRanges = new Dictionary<WmlMacroExpansionDefinition, WmlStaticSourceRange>();
        foreach (var request in requests)
        {
            using var compositionMeasurement = diagnostics?.Measure(WccSourceSavePhase.Composition);
            var state = states.Single(state => PathComparer.Equals(state.Source.Path, request.Source.Path));
            var anchor = LookupProjectOwner(baselineOwners[state.Context.SemanticKey], state.Owner, diagnostics);
            if (anchor is null) throw new InvalidOperationException("A source session owner cannot be rebound to one exact captured occurrence.");
            if (WccScenarioFieldContentScope.CheckOwner(anchor) is { } contentRefusal)
                throw new InvalidOperationException(contentRefusal);
            if (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu && !ReferenceEquals(anchor, campaignAnchor))
                throw new InvalidOperationException("The campaign source session is not the exact declared menu owner.");
            var fresh = new List<WccScenarioFieldEditProposal>();
            var initialSettings = request.InitialSettings;
            foreach (var proposal in request.Fields)
            {
                if (!baselines.ContainsKey(proposal.Route.ContextSemanticKey)
                    || !WccScenarioFieldValueCodec.TryGetDefinition(proposal.Route.Owner.TagName, proposal.Route.Key, out _)
                    || (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu
                        ? proposal.Route.Owner.TagName != "campaign" || proposal.Route.ContextSemanticKey != state.Context.SemanticKey
                        : proposal.Route.Owner.TagName == "campaign"))
                    throw new InvalidOperationException("A field draft is outside its explicit source and review scope.");
                var owner = LookupProjectOwner(baselineOwners[proposal.Route.ContextSemanticKey], proposal.Route.Owner, diagnostics);
                var contextAnchor = state.ReviewScope == WccScenarioFieldReviewScope.Scenario && selectedEntry is not null
                    ? LookupProjectOwner(baselineOwners[proposal.Route.ContextSemanticKey], state.Owner, diagnostics) : anchor;
                if (owner is null || (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu ? !ReferenceEquals(owner, anchor) :
                    selectedEntry is not null && (contextAnchor is null || !ReferenceEquals(owner, contextAnchor) && !IsOwnerWithin(owner, contextAnchor))))
                    throw new InvalidOperationException("A field draft has no unique exact current owner.");
                var context = contexts.Single(context => context.SemanticKey == proposal.Route.ContextSemanticKey);
                var route = WccScenarioFieldRouteAnalyzer.Analyze(owner, proposal.Route.Key, state.Source, context);
                if (!EquivalentRoute(proposal.Route, route))
                    throw new InvalidOperationException("A field's winning value, definition or exact source route changed.");
                fresh.Add(proposal with { Route = route });
            }
            if (request.InitialSettings is { } settings)
            {
                if (state.ReviewScope != WccScenarioFieldReviewScope.Scenario || anchor.TagName != "scenario"
                    || anchor.OpeningOrigin is not { } opening
                    || !WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, request.Source)
                    || !IsExactProjectScenarioRange(request.Source, opening, settings.ScenarioRange))
                    throw new InvalidOperationException("Initial settings require the exact authored scenario opening and source range.");
                foreach (var definition in settings.Definitions)
                {
                    RequireCapturedDefinition(definition, capture);
                    definitionRanges[definition] = ProjectDefinitionRange(definition, capture);
                }
                foreach (var flag in settings.SideFlags)
                    if (!settings.Definitions.Contains(flag.Definition) ||
                        LookupProjectOwner(baselineOwners[state.Context.SemanticKey], flag.SideOwner, diagnostics) is not { } flagOwner ||
                        !ReferenceEquals(flagOwner.Parent, anchor))
                        throw new InvalidOperationException("A flag recipe is not bound to its exact captured side and selected definition.");
                if (settings.SourceTextDomain != WmlTextDomainCapture.BeforeOffset(request.Source.Text, settings.ScenarioRange.StartOffset))
                    throw new InvalidOperationException("The scenario translation domain changed or is not captured exactly.");
                if (settings.CapturedContext is not null)
                    initialSettings = settings.WithCapturedProjection(baselines[state.Context.SemanticKey].Projection);
                ValidateEventDirectiveClosure(settings, capture);
            }
            if (request.RawPatches.Count != 0 && state.ReviewScope != WccScenarioFieldReviewScope.Scenario)
                throw new InvalidOperationException("Mixed campaign RAW editing requires a separate context-topology review.");
            if (request.CampaignStructure is { } structure)
                ValidateCampaignStructureBinding(structure, state, anchor, primary.Plan);
            if (request.CampaignCredits is { } credits)
                ValidateCampaignCreditsBinding(credits, state, anchor, primary.Plan);
            if (request.PresetStorage is { } storage)
                ValidateCampaignPresetStorageBinding(storage, state, anchor, primary.Plan, capture, environment!);
            if (request.ScenarioCreation is { } creation)
                ValidateScenarioCreationBinding(creation, state, anchor, primary.Plan, capture);
            if (request.UnitTypeCreation is { } unit)
                ValidateUnitTypeCreationBinding(unit, state, anchor, primary.Plan, capture);
            if (request.UnitTypeUpdate is { } update)
                ValidateUnitTypeUpdateBinding(update, state, capture);
            if (request.StoryEdit is { } story)
                ValidateStoryBinding(story, state, anchor, capture, baselines[contexts[0].SemanticKey], baselines);
            foreach (var edit in request.DifficultyFields)
            {
                var owner = LookupProjectOwner(baselineOwners[state.Context.SemanticKey], edit.Snapshot.Owner, diagnostics)
                    ?? throw new InvalidOperationException("A converted field has no unique exact current owner.");
                ValidateDifficultyFieldBinding(edit, state, anchor, owner, capture, campaignAnchor);
            }
            if (request.SideTopology is { } topology)
                ValidateSideTopologyBinding(topology, state, anchor, baselines[state.Context.SemanticKey].Projection, capture);
            foreach (var addition in request.LeaderAdditions)
            {
                var side = LookupProjectOwner(baselineOwners[state.Context.SemanticKey], addition.Snapshot.SideOwner, diagnostics)
                    ?? throw new InvalidOperationException("A new leader has no unique exact current side owner.");
                ValidateLeaderAdditionBinding(addition, state, anchor, side, baselines[state.Context.SemanticKey].Projection);
            }
            ValidatePresetApplicationBindings(request, state, primary.Plan, capture);
            foreach (var edit in request.UnitInstances)
            {
                var side = LookupProjectOwner(baselineOwners[state.Context.SemanticKey], edit.Snapshot.SideOwner, diagnostics)
                    ?? throw new InvalidOperationException("An initial unit has no exact current parent side.");
                ValidateUnitInstanceBinding(edit, state, anchor, side, baselines[state.Context.SemanticKey].Projection, capture);
            }
            var containing = ScenarioImporter.ImportAllFromText(request.Source.Text)
                .Where(document => anchor.OpeningOrigin is { } origin && origin.StartOffset >= document.BlockSpan.StartIndex
                    && origin.StartOffset < document.BlockSpan.EndIndexExclusive).ToArray();
            foreach (var raw in request.RawPatches)
                if (containing.Length != 1 || raw.Range.StartOffset < containing[0].BlockSpan.StartIndex
                    || raw.Range.StartOffset + raw.Range.Length > containing[0].BlockSpan.EndIndexExclusive)
                    throw new InvalidOperationException("Mixed RAW drafts must stay inside the exact selected authored scenario.");
            files.Add(WccProjectSourceEditComposer.Compose(new(request.Source, fresh, initialSettings, request.RawPatches, request.CampaignStructure, request.CampaignCredits, request.DifficultyFields, request.SideTopology, request.LeaderAdditions)
                { ScenarioCreation = request.ScenarioCreation, PresetStorage = request.PresetStorage, UnitTypeCreation = request.UnitTypeCreation, UnitTypeUpdate = request.UnitTypeUpdate, StoryEdit = request.StoryEdit, UnitInstances = request.UnitInstances }));
            if (request.StoryEdit is { DefinitionCandidate: not null, Snapshot.DefinitionBinding: { } storyDefinition } storyEdit)
                files[^1] = files[^1] with { Regions = files[^1].Regions.Concat(new[]
                    { new WccProjectAuthorizedRegion(storyDefinition.Invocation, files[^1].Candidate.MapRange(storyDefinition.Invocation)
                        ?? throw new InvalidOperationException("The Story definition caller has no exact combined mapping."), "StoryDefinition") { StoryEdit = storyEdit } }).ToArray() };
            diagnostics?.Add(WccSourceSaveCount.ComposedFiles);
            diagnostics?.Add(WccSourceSaveCount.SourcePatches, files[^1].Candidate.Patches.Count);
            diagnostics?.Add(WccSourceSaveCount.AuthorizedRegions, files[^1].Regions.Count);
        }
        var storageEdit = requests.Select(request => request.PresetStorage).SingleOrDefault(edit => edit is not null);
        if (storageEdit is not null)
            foreach (var attached in WccCampaignLocalPresetStoragePlanner.PlanStorageCandidates(storageEdit))
                files.Add(new(new(attached.Source), attached, Array.Empty<WccProjectAuthorizedRegion>()));
        if (unitUpdate is not null)
            files.Add(new(new(unitUpdate.OriginalSource),
                new(unitUpdate.OriginalSource, unitUpdate.CandidateSource, [],
                    [new(unitUpdate.OriginalRange.StartOffset, unitUpdate.OriginalRange.Length, unitUpdate.CandidateRange.Text)]), []));
        foreach (var attachedStory in WccScenarioStoryPlanner.PlanCombinedDefinitionCandidates(requests.Where(request => request.StoryEdit is not null).Select(request => request.StoryEdit!)))
        {
            files.Add(new(new(attachedStory.Source), attachedStory, []));
        }
        var creationEdit = requests.Select(request => request.ScenarioCreation).SingleOrDefault(edit => edit is not null);
        if (creationEdit is not null && WccScenarioCreationPlanner.PlanOwnershipCandidate(creationEdit) is { } ownership)
        {
            if (files.Any(file => PathComparer.Equals(file.Candidate.Source.Path, ownership.Source.Path)))
                throw new InvalidOperationException("Save preset definitions before creating a scenario when both update the package authoring companion. Both drafts are retained.");
            files.Add(new(new(ownership.Source), new(ownership.Source, ownership.CandidateSource, [],
                [new(0, ownership.Source.Text.Length, ownership.Candidate)]), []));
        }
        var change = new WccScenarioFieldSourceChange(files.Select(file => file.Candidate));
        if (storageEdit is not null)
            change = change.WithPresetRelocations(WccCampaignLocalPresetStoragePlanner.PlanRelocations(storageEdit)).WithPresetStorage(storageEdit);

        var sourceCreates = (creationEdit is null ? Array.Empty<WmlStaticSource>()
            : WccScenarioCreationPlanner.PlanSources(creationEdit))
            .Concat(unitCreation is null ? Array.Empty<WmlStaticSource>() : WccUnitTypeCreationPlanner.PlanSources(unitCreation))
            .Concat(unitCreation is null ? Array.Empty<WmlStaticSource>() : WccUnitTypeMaterializationPlanner.PlanMissingHelperSources(unitCreation, capture.Sources))
            .Concat(storageEdit is null ? Array.Empty<WmlStaticSource>()
                : WccCampaignLocalPresetStoragePlanner.PlanSources(storageEdit))
            .Concat(unitUpdate?.SourceCreates ?? []).ToArray();
        var binaryPlan = unitCreation is not null
            ? WccUnitTypeMaterializationPlanner.PlanBinaryDestinations(unitCreation.Spec, primary.ProjectRootPath)
            : unitUpdate is not null ? WccUnitTypeUpdatePlanner.PlanBinaryDestinations(unitUpdate) : null;
        var storyBinaryPlans = requests.Where(request => request.StoryEdit is not null)
            .Select(request => WccScenarioStoryPlanner.PlanBinaryDestinations(request.StoryEdit!)).ToArray();
        var binaryCreates = (binaryPlan?.Creates ?? []).Concat(storyBinaryPlans.SelectMany(plan => plan.Creates)).ToArray();
        var binaryReuses = (binaryPlan?.Reuses ?? []).Concat(storyBinaryPlans.SelectMany(plan => plan.Reuses)).ToArray();
        WccUnitTypeMaterializationPlanner.ValidateBinaryReuseMembership(binaryReuses, capture, primary.RootRevisionId);
        if (WccUnitTypeMaterializationPlanner.VerifyBinaryReuses(binaryReuses) is { } binaryReuseError)
            throw new InvalidOperationException(binaryReuseError);
        var directories = CaptureCreationDirectories(primary.ProjectRootPath,
            sourceCreates.Select(source => source.Path).Concat(binaryCreates.Select(binary => binary.DestinationPath)));
        var candidateCapture = creationEdit is null && storageEdit is null && unitCreation is null && unitUpdate is null && binaryCreates.Length == 0 ? null
            : WccStaticSourceCapture.CreateCandidateCapture(capture, primary.RootRevisionId,
                files.Select(file => file.Candidate.CandidateSource).ToArray(), sourceCreates,
                binaryCreates.Select(item => new WccCapturedBinaryCandidate(item.DestinationPath, item.SourceRevision)).ToArray(),
                directories.Select(path => new WccCapturedDirectoryCandidate(path)).ToArray());
        var regions = files.SelectMany(file => file.Regions).ToArray();
        var edits = files.SelectMany(file => file.Candidate.Edits).ToArray();
        var eventPresence = new Dictionary<WccProjectAuthorizedRegion, int>();
        var structureFile = files.SingleOrDefault(file => file.Request.CampaignStructure is not null);
        var expectedPlan = primary.Plan is null ? null : CampaignStructureExpectedPlan(primary.Plan, structureFile);
        WmlCampaignEvaluationPlan? candidateDifficultyPlan = null;
        foreach (var context in contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseline = baselines[context.SemanticKey];
            var eventContextPresence = new Dictionary<WccProjectAuthorizedRegion, int>();
            var proposed = candidateCapture is null
                ? EvaluateWithDiagnostics(capture, context, primary.Plan, environment!, change, cancellationToken, diagnostics)
                : EvaluateWithDiagnostics(candidateCapture, context, primary.Plan, environment!, null, cancellationToken, diagnostics);
            if (unitCreation?.Spec.Materialization is { } materialization &&
                WccUnitTypeMaterializationPlanner.FindHelperConflict(materialization,
                    WccUnitTypeCreationPlanner.PlanSources(unitCreation).Single(), proposed.Evaluation) is { } helperConflict)
                throw new InvalidOperationException(helperConflict.Diagnostic + " (" + context.DisplayName + ")");
            RequireComplete(proposed, context, "Proposed campaign");
            if (selectedEntry is not null) proposed = ScopeSelectedCampaignMenu(proposed, selectedEntry.Owner, change);
            var createdRanges = creationEdit is null ? Array.Empty<WmlStaticSourceRange>()
                : ValidateProjectScenarioCreation(creationEdit, baseline, proposed, candidateCapture!, context, change).ToArray();
            if (unitCreation is not null)
                createdRanges = createdRanges.Concat(ValidateProjectUnitTypeCreation(unitCreation, baseline, proposed,
                    capture, candidateCapture!, context, primary.Plan!, environment!, cancellationToken)).ToArray();
            if (unitUpdate is not null)
                createdRanges = createdRanges.Concat(ValidateProjectUnitTypeUpdate(unitUpdate, baseline, proposed,
                    capture, candidateCapture!, context, change)).ToArray();
            if (storageEdit is not null)
                ValidateProjectCampaignPresetStorage(storageEdit, baseline, proposed, context, primary.Plan!,
                    candidateCapture!, environment!, change, regions, cancellationToken, diagnostics);
            ValidateRequestedEventUnitTypes(requests, context, change, candidateCapture ?? capture, environment!, primary.Plan, cancellationToken);
            var oldOwners = baselineOwners[context.SemanticKey];
            var newOwners = CreateProjectOwnerLookup(proposed.Projection, requestedOwners, change, cancellationToken, diagnostics);
            if (context.CampaignDefine is null && requests.Any(request => request.DifficultyFields.Count != 0))
            {
                var candidateMain = change.Substitute(capture.EntrySource!);
                if (selectedEntry is not null)
                {
                    var candidateOwner = LookupProjectOwner(newOwners, campaignAnchor!, diagnostics)
                        ?? throw new InvalidOperationException("The converted candidate lost its exact selected menu owner.");
                    candidateDifficultyPlan = WccSelectedCampaignEntryAdapter.ObservePlan(proposed.Evaluation,
                        proposed.Projection, candidateMain, candidateOwner, primary.Plan);
                }
                else
                {
                    var preview = WmlCampaignEvaluationPlanDiscovery.Discover(candidateMain.Text, candidateMain.Path, context.TargetCompatibility)
                        .Where(plan => plan.CampaignDefine == primary.Plan!.CampaignDefine).ToArray();
                    if (preview.Length != 1)
                        throw new InvalidOperationException("The converted candidate has no unique campaign metadata anchor.");
                    if (!WmlCapturedCampaignEvaluationPlan.TryCreate(proposed.Evaluation,
                            proposed.Projection, candidateMain, preview[0], out candidateDifficultyPlan, out var problem))
                        throw new InvalidOperationException(problem);
                }
            }
            foreach (var file in files)
                ValidateProjectDifficultyFields(file, baseline, proposed, oldOwners, newOwners, context, change, capture.EntrySource!, candidateDifficultyPlan, diagnostics);
            if (campaignAnchor is not null && context.SemanticKey == contexts[0].SemanticKey)
            {
                var current = LookupProjectOwner(newOwners, campaignAnchor, diagnostics);
                if (current is null || VerifyCampaignContextPlan(current, expectedPlan!, capture.EntrySource!, false) is { })
                    throw new InvalidOperationException("The combined draft changes campaign ownership or its declared context topology.");
            }
            var fullTargets = new List<ReviewedFieldTarget>();
            var preservedInitialSettings = new List<(WmlStaticSourceRange Before, WmlStaticSourceRange After)>();
            foreach (var edit in edits)
            {
                var oldOwner = LookupProjectOwner(oldOwners, edit.Route.Owner, diagnostics);
                var newOwner = LookupProjectOwner(newOwners, edit.Route.Owner, diagnostics);
                var menu = edit.Route.Owner.TagName == "campaign";
                if (oldOwner is null || newOwner is null)
                {
                    // A literal menu-only campaign can be inactive in play, and
                    // a play-only scenario can be inactive in the menu. Ambiguous
                    // matching keys never acquire this unchanged-phase fallback.
                    var inactiveMenu = menu && context.CampaignDefine is not null;
                    var inactiveScenario = !menu && primary.Plan is not null && context.SemanticKey == contexts[0].SemanticKey;
                    if (oldOwner is null && newOwner is null && (inactiveMenu || inactiveScenario)
                        && !ProjectOwnerHasMatches(oldOwners, edit.Route.Owner, diagnostics)
                        && !ProjectOwnerHasMatches(newOwners, edit.Route.Owner, diagnostics)) continue;
                    throw new InvalidOperationException("An edited owner is absent, ambiguous or structurally uncertain in a reviewed context.");
                }
                diagnostics?.Add(WccSourceSaveCount.FieldValidations);
                using (diagnostics?.Measure(WccSourceSavePhase.FieldValidation))
                    ValidateProjectField(edit, oldOwner, newOwner, proposed.Evaluation, context);
                fullTargets.Add(new(oldOwner, newOwner, edit.Route) { Edit = edit });
            }
            foreach (var file in files.Where(file => file.Regions.Count != 0))
            {
                var state = states.Single(state => PathComparer.Equals(state.Source.Path, file.Request.Source.Path));
                var beforeRoot = LookupProjectOwner(oldOwners, state.Owner, diagnostics);
                var afterRoot = LookupProjectOwner(newOwners, state.Owner, diagnostics);
                if (file.Request.CampaignStructure is not null || file.Request.CampaignCredits is not null
                    || file.Request.PresetStorage is not null)
                {
                    var structure = file.Request.CampaignStructure;
                    var candidateStructure = structure is null ? null : CandidateCampaignStructure(file);
                    if (context.CampaignDefine is null)
                    {
                        if (beforeRoot is null || afterRoot is null)
                            throw new InvalidOperationException("A presentation draft lost its exact campaign menu owner.");
                        ValidatePresentation(beforeRoot, afterRoot);
                    }
                    else if (beforeRoot is not null && afterRoot is not null)
                    {
                        ValidatePresentation(beforeRoot, afterRoot);
                    }
                    else if (beforeRoot is not null || afterRoot is not null
                        || ProjectOwnerHasMatches(oldOwners, state.Owner, diagnostics)
                        || ProjectOwnerHasMatches(newOwners, state.Owner, diagnostics))
                        throw new InvalidOperationException("Campaign structure has ambiguous or changed activation in a gameplay context.");
                    continue;

                    void ValidatePresentation(WmlStaticScalarOwner before, WmlStaticScalarOwner after)
                    {
                        if (file.Request.PresetStorage is { } storage)
                        {
                            ValidateCampaignStructureOwner(storage.Snapshot.Main.Structure, before);
                            var candidate = WccCampaignStructurePlanner.Read(file.Candidate.CandidateSource, storage.Snapshot.Main.Structure.Authoring);
                            if (!candidate.CanEdit) throw new InvalidOperationException(candidate.Diagnostic);
                            ValidateCampaignStructureOwner(candidate, after);
                        }
                        if (structure is not null)
                        {
                            ValidateCampaignStructureOwner(structure.Snapshot, before);
                            ValidateCampaignStructureOwner(candidateStructure!, after);
                        }
                        if (file.Request.CampaignCredits is { } credits)
                        {
                            ValidateCampaignCreditsOwner(credits.Snapshot, before);
                            ValidateCandidateCampaignCredits(file, after);
                        }
                    }
                }
                if (beforeRoot is null && afterRoot is null && primary.Plan is not null
                    && context.SemanticKey == contexts[0].SemanticKey) continue;
                if (beforeRoot is null || afterRoot is null || beforeRoot.TagName != "scenario" || afterRoot.TagName != "scenario")
                    throw new InvalidOperationException("A structural draft has no exact scenario owner in a reviewed play context.");
                using var regionMeasurement = diagnostics?.Measure(WccSourceSavePhase.RegionValidation);
                diagnostics?.Add(WccSourceSaveCount.RegionValidations);
                preservedInitialSettings.AddRange(ValidateCapturedInitialSettings(file, baseline, proposed, context, change, definitionRanges));
                ValidateRepresentedInitialSource(baseline, beforeRoot, file.Request.InitialSettings);
                ValidateProjectStory(file, baseline, proposed, beforeRoot, afterRoot, context, change, candidateCapture ?? capture);
                ValidateProjectSideTopology(file, baseline, proposed, beforeRoot, afterRoot, context, change);
                ValidateProjectLeaderAdditions(file, baseline, proposed, beforeRoot, afterRoot, context, change);
                ValidateProjectUnitInstances(file, baseline, proposed, beforeRoot, afterRoot, context, change, candidateCapture ?? capture, environment!);
                ValidatePresetApplicationIdentities(file, afterRoot);
                ValidateProjectActiveDefinitionsWithDifficultyFields(baseline, file.Regions, false,
                    file.Request.InitialSettings, definitionRanges, change, file.Request.DifficultyFields, context);
                ValidateProjectActiveDefinitions(proposed, file.Regions, true, file.Request.InitialSettings, definitionRanges, change);
                ValidateProjectRegions(baseline, file.Regions, false, beforeRoot, file.Request.InitialSettings);
                ValidateProjectRegions(proposed, file.Regions, true, afterRoot, file.Request.InitialSettings);
                ValidateProjectSideFlagRegions(baseline, file, false, beforeRoot, oldOwners, definitionRanges, change);
                ValidateProjectSideFlagRegions(proposed, file, true, afterRoot, newOwners, definitionRanges, change);
                ValidateProjectEventRegions(baseline, file, false, beforeRoot, eventPresence, eventContextPresence, change, context, primary.Plan);
                ValidateProjectEventRegions(proposed, file, true, afterRoot, eventPresence, eventContextPresence, change, context, primary.Plan);
            }
            IReadOnlyList<WmlStaticSourceRange> oldIncludes = structureFile is null ? [] : CampaignStructureIncludeRanges(baseline, structureFile, false, context);
            IReadOnlyList<WmlStaticSourceRange> newIncludes = structureFile is null ? [] : CampaignStructureIncludeRanges(proposed, structureFile, true, context);
            // Moved definitions still supply existing consumer values. Their
            // exact relocation proof participates in ordinary origin/history
            // comparisons; masking the bodies would hide those consumers.
            var maskRegions = regions.Where(region => region.Kind != "PresetStorage").ToArray();
            var oldMasked = MaskProjectRegionsWithDiagnostics(baseline, maskRegions.Select(region => region.Before).Concat(oldIncludes)
                .Concat(preservedInitialSettings.Select(pair => pair.Before))
                .Concat(unitUpdate is null ? [] : new[] { unitUpdate.OriginalRange }).ToArray(), diagnostics);
            if (unitUpdate is not null) oldMasked = NormalizeUnitUpdateMaskedBaseline(unitUpdate, oldMasked);
            var newMasked = MaskProjectRegionsWithDiagnostics(proposed, maskRegions.Select(region => region.After).Concat(newIncludes).Concat(createdRanges)
                .Concat(preservedInitialSettings.Select(pair => pair.After)).ToArray(), diagnostics);
            // The independent typed payload and paired authored-call proof above
            // permits only these exact pure-preset scalar emissions. Retain every
            // owner/tag so caller [+leader] amendments keep their original target;
            // local overrides, siblings and other definitions remain visible.
            oldMasked = MaskPresetMutationFields(baseline, oldMasked, storageEdit, change, false);
            newMasked = MaskPresetMutationFields(proposed, newMasked, storageEdit, change, true);
            RequireComplete(oldMasked); RequireComplete(newMasked);
            var oldMaskedOwners = ReferenceEquals(oldMasked.Projection, baseline.Projection) ? oldOwners
                : CreateProjectOwnerLookup(oldMasked.Projection, requestedOwners, null, cancellationToken, diagnostics);
            var newMaskedOwners = ReferenceEquals(newMasked.Projection, proposed.Projection) ? newOwners
                : CreateProjectOwnerLookup(newMasked.Projection, requestedOwners, change, cancellationToken, diagnostics);
            var targets = fullTargets.Select(target => new ReviewedFieldTarget(
                LookupProjectOwner(oldMaskedOwners, target.Route.Owner, diagnostics)
                    ?? throw new InvalidOperationException("A structural draft obscures an edited scalar owner."),
                LookupProjectOwner(newMaskedOwners, target.Route.Owner, diagnostics)
                    ?? throw new InvalidOperationException("A structural draft obscures an edited scalar owner."),
                target.Route) { Edit = target.Edit }).ToArray();
            diagnostics?.Add(WccSourceSaveCount.OwnerComparisons);
            string? refusal;
            using (diagnostics?.Measure(WccSourceSavePhase.OwnerComparison))
                CompareOwnersWithDiagnostics(oldMasked.Projection, newMasked.Projection, targets, change,
                    oldMasked.Evaluation.Text, newMasked.Evaluation.Text, out refusal, diagnostics);
            if (refusal is not null) throw new InvalidOperationException(refusal);
            diagnostics?.Add(WccSourceSaveCount.HistoryComparisons);
            string? historyRefusal;
            using (diagnostics?.Measure(WccSourceSavePhase.HistoryComparison))
                historyRefusal = CheckProjectFieldHistories(oldMasked, newMasked, targets, change);
            if (historyRefusal is not null)
                throw new InvalidOperationException(historyRefusal);
            diagnostics?.Add(WccSourceSaveCount.OutputComparisons);
            bool changedOutput;
            using (diagnostics?.Measure(WccSourceSavePhase.OutputComparison))
                changedOutput = HasUntrackedOutputChange(oldMasked, newMasked);
            if (changedOutput)
                throw new InvalidOperationException("The combined draft changes expanded output outside its exact authorized source footprints.");
        }
        foreach (var region in regions.Where(region => region.Kind == "Event" &&
                     region.EventEdit!.Kind is not (WccScenarioEventSourceEditKind.AddLiteral or WccScenarioEventSourceEditKind.AddMacroInvocation)))
            if (eventPresence.GetValueOrDefault(region) == 0)
                throw new InvalidOperationException("The selected event source produces no exact direct event in any reviewed context.");
        var closure = includesUnitData ? UnitTypeCreationClosureIdentity(capture, diagnostics) : ProjectClosureIdentity(capture, diagnostics);
        WccStaticSourceCaptureResult finalCapture;
        diagnostics?.Add(WccSourceSaveCount.SourceCaptures);
        using (diagnostics?.Measure(WccSourceSavePhase.FinalCapture))
            finalCapture = await WccStaticSourceCapture.CaptureAsync(primary.Snapshot, primary.RootRevisionId,
                entry, cancellationToken: cancellationToken, configuredAddonsPath: primary.ConfiguredAddonsPath,
                includeCoreUnits: includesUnitData).ConfigureAwait(false);
        diagnostics?.ObserveCapture(finalCapture);
        using var finalMeasurement = diagnostics?.Measure(WccSourceSavePhase.FinalValidation);
        foreach (var state in states)
            if (CheckCapture(state, finalCapture) is { } refusal) throw new InvalidOperationException(refusal);
        if ((includesUnitData ? UnitTypeCreationClosureIdentity(finalCapture, diagnostics) : ProjectClosureIdentity(finalCapture, diagnostics)) != closure)
            throw new InvalidOperationException("The source closure changed during combined review.");
        WccUnitTypeMaterializationPlanner.ValidateBinaryReuseMembership(binaryReuses, finalCapture, primary.RootRevisionId);
        if (WccUnitTypeMaterializationPlanner.VerifyBinaryReuses(binaryReuses) is { } finalBinaryReuseError)
            throw new InvalidOperationException(finalBinaryReuseError);
        var candidate = new WccProjectSourceEditCandidate(files.Select(file => new WccProjectSourceFileCandidate(
            file.Candidate.Source, file.Candidate.CandidateSource, file.Candidate.Patches)))
            { SourceCreates = Array.AsReadOnly(sourceCreates), BinaryCreates = Array.AsReadOnly(binaryCreates),
                BinaryReuses = Array.AsReadOnly(binaryReuses), DirectoryCreates = directories };
        return new(candidate, Array.AsReadOnly(files.ToArray()), closure, Array.AsReadOnly(states.Select(ScopeIdentity).ToArray()))
            { IncludesCoreUnitData = includesUnitData };
    }

    internal static Task<string?> VerifyProjectPreparedAsync(IReadOnlyList<WccScenarioFieldEditSessionState> states,
        WccProjectPreparedReview proof, IReadOnlyList<Wcc.Core.Projects.WccTextDiskRevision> ownedStages,
        CancellationToken cancellationToken)
        => VerifyProjectPreparedWithDiagnosticsAsync(states, proof, ownedStages, cancellationToken, null);

    internal static async Task<string?> VerifyProjectPreparedWithDiagnosticsAsync(IReadOnlyList<WccScenarioFieldEditSessionState> states,
        WccProjectPreparedReview proof, IReadOnlyList<Wcc.Core.Projects.WccTextDiskRevision> ownedStages,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
        => await VerifyProjectPreparedWithResourcesAsync(states, proof, ownedStages, [], cancellationToken, diagnostics).ConfigureAwait(false);

    internal static async Task<string?> VerifyProjectPreparedWithResourcesAsync(IReadOnlyList<WccScenarioFieldEditSessionState> states,
        WccProjectPreparedReview proof, IReadOnlyList<Wcc.Core.Projects.WccTextDiskRevision> ownedStages,
        IReadOnlyList<Wcc.Core.Projects.WccBinaryDiskRevision> ownedBinaryStages,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (proof.PackageEdit is not null)
            return await VerifyManagedPackageAsync(states, proof, ownedStages, ownedBinaryStages, cancellationToken, diagnostics).ConfigureAwait(false);
        if (proof.PackageRawReviewIdentity is not null)
            return await VerifyPackageRawProjectPreparedAsync(states, proof, ownedStages, ownedBinaryStages,
                cancellationToken, diagnostics).ConfigureAwait(false);
        if (!states.Select(ScopeIdentity).SequenceEqual(proof.ScopeIdentities, StringComparer.Ordinal))
            return "The source sessions or declared contexts changed after review.";
        foreach (var state in states) if (CheckScope(state) is { } refusal) return refusal;
        if (WccUnitTypeMaterializationPlanner.VerifyBinaryReuses(proof.Candidate.BinaryReuses) is { } binaryReuseError) return binaryReuseError;
        var primary = states[0];
        WccStaticSourceCaptureResult capture;
        diagnostics?.Add(WccSourceSaveCount.SourceCaptures);
        using (diagnostics?.Measure(WccSourceSavePhase.FinalCapture))
            capture = proof.IncludesCoreUnitData
                ? await WccStaticSourceCapture.CaptureForStagedUnitTypeCreationAsync(primary.Snapshot, primary.RootRevisionId,
                    primary.Plan?.CampaignMainSourcePath ?? primary.Source.Path, ownedStages, ownedBinaryStages, cancellationToken,
                    primary.ConfiguredAddonsPath).ConfigureAwait(false)
                : proof.Candidate.BinaryCreates.Count != 0
                ? await WccStaticSourceCapture.CaptureForStagedStoryAsync(primary.Snapshot, primary.RootRevisionId,
                    primary.Plan?.CampaignMainSourcePath ?? primary.Source.Path, ownedStages, ownedBinaryStages, cancellationToken,
                    primary.ConfiguredAddonsPath).ConfigureAwait(false)
                : await WccStaticSourceCapture.CaptureForStagedCommitAsync(primary.Snapshot, primary.RootRevisionId,
                primary.Plan?.CampaignMainSourcePath ?? primary.Source.Path, ownedStages, cancellationToken,
                configuredAddonsPath: primary.ConfiguredAddonsPath).ConfigureAwait(false);
        diagnostics?.ObserveCapture(capture);
        foreach (var state in states) if (CheckCapture(state, capture) is { } refusal) return refusal;
        WccUnitTypeMaterializationPlanner.ValidateBinaryReuseMembership(proof.Candidate.BinaryReuses, capture, primary.RootRevisionId);
        if (WccUnitTypeMaterializationPlanner.VerifyBinaryReuses(proof.Candidate.BinaryReuses) is { } finalBinaryReuseError) return finalBinaryReuseError;
        return (proof.IncludesCoreUnitData ? UnitTypeCreationClosureIdentity(capture, diagnostics) : ProjectClosureIdentity(capture, diagnostics))
            == proof.ClosureIdentity ? null : "The pinned source closure or namespace changed after review.";
    }

    private static string ProjectClosureIdentity(WccStaticSourceCaptureResult capture, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.ClosureIdentity);
        return ClosureIdentity(capture);
    }

    private static string UnitTypeCreationClosureIdentity(WccStaticSourceCaptureResult capture, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (!capture.HasCompleteCoreUnitPrelude) throw new InvalidOperationException("The exact Core unit-data entry is no longer available.");
        return WccContentHash.ComputeText(ProjectClosureIdentity(capture, diagnostics) + "\n" +
            string.Join("\n", capture.CoreUnitPreludeSources.Select(source => source.Path + ":" + source.ContentHash)));
    }

    private static ProjectOwnerLookup CreateProjectOwnerLookup(WmlStaticScalarProjection projection,
        IReadOnlyList<WmlStaticScalarOwner> requestedOwners, WccScenarioFieldSourceChange? change,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.OwnerLookupBuild);
        diagnostics?.Add(WccSourceSaveCount.OwnerLookupBuilds);
        return new(projection, requestedOwners, change, cancellationToken);
    }

    private static WmlStaticScalarOwner? LookupProjectOwner(ProjectOwnerLookup lookup, WmlStaticScalarOwner original,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.OwnerLookup);
        diagnostics?.Add(WccSourceSaveCount.OwnerLookupQueries);
        return lookup.GetUnique(original);
    }

    private static bool ProjectOwnerHasMatches(ProjectOwnerLookup lookup, WmlStaticScalarOwner original,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.OwnerLookup);
        diagnostics?.Add(WccSourceSaveCount.OwnerLookupQueries);
        return lookup.HasMatches(original);
    }

    private static void RequireComplete(PairedEvaluation evaluated, WmlEvaluationContext? context = null, string? phase = null)
    {
        if (!evaluated.Evaluation.IsComplete || evaluated.Projection.Diagnostics.Count != 0)
        {
            var diagnostic = evaluated.Evaluation.Diagnostics.FirstOrDefault(item => item.IsBlocking)
                ?? evaluated.Projection.Diagnostics.FirstOrDefault(item => item.IsBlocking);
            var scope = phase ?? "";
            if (context is not null)
                scope += (scope.Length == 0 ? "" : " ") + "(" +
                    (context.CampaignDefine is null ? "Menu" : context.DisplayName) + ")";
            var detail = scope.Length == 0 ? "" : " " + scope + ":";
            if (diagnostic is not null)
            {
                detail += " " + diagnostic.Message;
                if (diagnostic.Origin is { } origin)
                {
                    var invocation = (evaluated.Evaluation.MaskedOriginalEvaluation ?? evaluated.Evaluation).Invocations
                        .FirstOrDefault(call => call.InvocationOrigins.Any(callOrigin =>
                            WccScenarioFieldRouteAnalyzer.SameSource(callOrigin.Source, origin.Source)
                            && origin.StartOffset >= callOrigin.StartOffset
                            && origin.StartOffset + origin.Length <= callOrigin.StartOffset + callOrigin.Length));
                    if (invocation is not null && !diagnostic.Message.Contains(invocation.Name, StringComparison.Ordinal))
                        detail += " {" + invocation.Name + "}";
                    var line = 1;
                    for (var offset = 0; offset < origin.StartOffset; offset++)
                        if (origin.Source.Text[offset] == '\n' || origin.Source.Text[offset] == '\r' &&
                            (offset + 1 >= origin.Source.Text.Length || origin.Source.Text[offset + 1] != '\n')) line++;
                    detail += " [" + origin.Source.Path + ":" + line + "]";
                }
            }
            throw new InvalidOperationException("A menu or declared play context has unresolved or malformed expanded WML; the combined Save needs RAW review." + detail);
        }
    }

    private static void RequireCapturedDefinition(WmlMacroExpansionDefinition definition, WccStaticSourceCaptureResult capture)
    {
        var sources = capture.Sources.Where(source => definition.SourcePath is not null
            && PathComparer.Equals(source.Path, definition.SourcePath)).ToArray();
        if (sources.Length != 1 || definition.SourceStartLine is not { } line
            || !WmlMacroDefinitionScanner.Scan(sources[0].Text).Any(actual => actual.Name == definition.Name
                && actual.StartLineIndex == line && NormalizeDefinition(actual.RawText) == NormalizeDefinition(definition.RawText))
            || definition.TextDomain != WmlTextDomainCapture.BeforeLine(sources[0].Text, line))
            throw new InvalidOperationException("An initial-settings definition is not the exact captured source and translation domain.");
    }

    private static string NormalizeDefinition(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool IsExactProjectScenarioRange(WmlStaticSource source, WmlStaticSourceRange opening,
        WmlStaticSourceRange requested)
    {
        if (!WccScenarioFieldRouteAnalyzer.SameSource(source, requested.Source)) return false;
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        if (structure.Problem is not null) return false;
        // Definition bodies, strings, comments and brace arguments are masked.
        // A literal file loaded through a macro include retains this same opener.
        return WmlBlockScanner.FindTopLevelBlockSpans(structure.MaskedText).Count(span => span.TagName == "scenario"
            && span.StartIndex == opening.StartOffset && span.StartIndex == requested.StartOffset
            && span.EndIndexExclusive == requested.StartOffset + requested.Length
            && structure.MaskedText.Substring(opening.StartOffset, opening.Length) == opening.Text) == 1;
    }

    private static WmlStaticSourceRange ProjectDefinitionRange(WmlMacroExpansionDefinition definition,
        WccStaticSourceCaptureResult capture)
    {
        var source = capture.Sources.Single(source => PathComparer.Equals(source.Path, definition.SourcePath));
        var declaration = WmlMacroDefinitionScanner.Scan(source.Text).Single(actual => actual.Name == definition.Name
            && actual.StartLineIndex == definition.SourceStartLine
            && NormalizeDefinition(actual.RawText) == NormalizeDefinition(definition.RawText));
        var lines = WmlAuthoredStructureScanner.Scan(source.Text).Lines;
        var start = lines[declaration.StartLineIndex].StartOffset;
        while (start < lines[declaration.StartLineIndex].ContentEndOffset && source.Text[start] is ' ' or '\t') start++;
        // The evaluator consumes the #enddef directive's terminal newline.
        // Bind the exact physical declaration, rather than trimming its body.
        var end = lines[declaration.EndLineIndexExclusive - 1].EndOffsetExclusive;
        return new(source, start, end - start);
    }

    private static void ValidateProjectActiveDefinitions(PairedEvaluation evaluated,
        IReadOnlyList<WccProjectAuthorizedRegion> regions, bool proposed,
        WccScenarioInitialSettingsEdit? settings,
        IReadOnlyDictionary<WmlMacroExpansionDefinition, WmlStaticSourceRange> definitions,
        WccScenarioFieldSourceChange change)
        => ValidateProjectActiveDefinitionsWithDifficultyFields(evaluated, regions, proposed,
            settings, definitions, change, [], null);

    private static void ValidateProjectActiveDefinitionsWithDifficultyFields(PairedEvaluation evaluated,
        IReadOnlyList<WccProjectAuthorizedRegion> regions, bool proposed,
        WccScenarioInitialSettingsEdit? settings,
        IReadOnlyDictionary<WmlMacroExpansionDefinition, WmlStaticSourceRange> definitions,
        WccScenarioFieldSourceChange change, IReadOnlyList<WccScenarioDifficultyFieldEdit> difficultyFields,
        WmlEvaluationContext? context)
    {
        var validation = new ProjectActiveDefinitionValidation(settings, definitions, change, proposed, evaluated.Diagnostics);
        foreach (var region in regions.Where(region => region.Kind != "RAW"))
        {
            var range = proposed ? region.After : region.Before;
            foreach (var segment in evaluated.GetProjectRegionSegments(range))
            {
                var first = ProjectInvokedFrame(segment, range);
                if (first < 0) continue;
                // Fresh per-field proof above has already matched the actual
                // sole contribution in this context. Only its removed original
                // invocation and exact definition ancestry get this permission.
                var convertedMacro = !proposed && region.Kind == "DifficultyField" && context is not null
                    && MatchesDifficultyMacroFrameChain(difficultyFields, range, segment, first, context);
                // Typed Story proof immediately above independently validates retained invocation
                // output and every supported Core travel body before any region is masked.
                var storyMacro = region.Kind is "Story" or "StoryDefinition" && region.StoryEdit is not null;
                // Outer include ancestry is already captured and remains in all-
                // context comparison. Only this invocation and its descendants
                // belong to the composer's authorized settings footprint.
                foreach (var frame in segment.ExpansionStack.Skip(first))
                    if (!validation.IsAllowed(frame) && !convertedMacro && !storyMacro && !(!proposed && IsExactRemovedSideTopologyRegion(region))
                        && !(!proposed && IsExactExistingUnitPresetFrame(region, frame))
                        && !(proposed && IsExactPresetApplicationFrame(region, frame, change)))
                        throw new InvalidOperationException("An active initial-settings definition or nested helper differs from its exact captured recipe.");
            }
        }
    }

    private static int ProjectInvokedFrame(WmlStaticOutputSegment segment, WmlStaticSourceRange region)
    {
        for (var index = 0; index < segment.ExpansionStack.Count; index++)
        {
            var origins = segment.ExpansionStack[index].InvocationOrigins;
            if (origins.Count != 0 && origins.All(origin => InsideProposedScalar(origin, region))) return index;
        }
        return -1;
    }

    private static bool IsProjectLiteralOrigin(WmlStaticOutputSegment segment, WmlStaticSourceRange region) =>
        IsProjectLiteralOrigin(segment.Origin, segment.ExpansionStack, region);

    private static bool IsProjectLiteralOrigin(WmlStaticSourceRange origin,
        IReadOnlyList<WmlStaticExpansionFrame> frames, WmlStaticSourceRange region) =>
        frames.All(frame => frame.InvocationOrigins.Count != 0 && frame.InvocationOrigins.All(invocation =>
            !WccScenarioFieldRouteAnalyzer.SameSource(invocation.Source, region.Source)))
        && frames.All(frame => !InsideProposedScalar(origin, frame.Definition));

    private static bool IsProjectLiteralOpening(WmlStaticScalarOwner owner, WmlStaticSourceRange region)
    {
        if (owner.OpeningOrigin is not { } opening || !InsideProposedScalar(opening, region)
            || !IsProjectLiteralOrigin(opening, owner.OpeningExpansionStack, region)) return false;
        var structure = WmlAuthoredStructureScanner.Scan(opening.Source.Text);
        return structure.Problem is null
            && structure.MaskedText.Substring(opening.StartOffset, opening.Length) == opening.Text;
    }

    private static void ValidateProjectField(WccScenarioFieldCandidate edit, WmlStaticScalarOwner before,
        WmlStaticScalarOwner after, WmlStaticEvaluationResult evaluation, WmlEvaluationContext context)
    {
        if ((WccScenarioFieldContentScope.CheckOwner(before) ?? WccScenarioFieldContentScope.CheckOwner(after)) is { } refusal)
            throw new InvalidOperationException(refusal);
        WccScenarioFieldValueCodec.TryGetDefinition(edit.Route.Owner.TagName, edit.Route.Key, out var definition);
        var oldValue = before.ResolveField(edit.Route.Key); var newValue = after.ResolveField(edit.Route.Key);
        if (edit.Route.Origin == WccScenarioFieldOrigin.Absent ? oldValue.State != WmlStaticScalarResolutionState.ProvenAbsent
            : oldValue.State != WmlStaticScalarResolutionState.Known || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition!, oldValue.ValueText))
            throw new InvalidOperationException("A field's baseline value or required absence is not proven in every reviewed context.");
        if (!(edit.Operation == WmlFieldOperation.Remove && edit.Route.Kind == WccScenarioFieldRouteKind.DirectToken
                && newValue.State == WmlStaticScalarResolutionState.ProvenAbsent)
            && (newValue.State != WmlStaticScalarResolutionState.Known || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition!, newValue.ValueText)))
            throw new InvalidOperationException("A combined field draft is not a supported known scalar in every reviewed context.");
        if (context.SemanticKey == edit.Route.ContextSemanticKey && edit.Operation != WmlFieldOperation.Remove
            && !(edit.ValueInputKind == WccScenarioFieldValueInputKind.Literal
                ? WccScenarioFieldValueCodec.Equivalent(definition!, newValue.ValueText, edit.RequestedValue)
                : HasExactProposedScalarAncestry(after, edit.Route.Key, edit, evaluation)))
            throw new InvalidOperationException("The combined candidate does not resolve to the requested field value and exact authored ancestry.");
    }

    private static PairedEvaluation MaskProjectRegions(PairedEvaluation evaluated, IReadOnlyList<WmlStaticSourceRange> regions)
        => MaskProjectRegionsWithDiagnostics(evaluated, regions, null);

    private static PairedEvaluation MaskProjectRegionsWithDiagnostics(PairedEvaluation evaluated,
        IReadOnlyList<WmlStaticSourceRange> regions, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.RegionMasking);
        diagnostics?.Add(WccSourceSaveCount.RegionMaskAttempts);
        if (regions.Count == 0)
        {
            foreach (var segment in evaluated.Evaluation.Segments)
            {
                evaluated.CancellationToken.ThrowIfCancellationRequested();
                if (segment.Origin.Length != segment.Text.Length)
                    throw new InvalidOperationException("Expanded output lacks exact captured segment evidence.");
            }
            return evaluated;
        }
        foreach (var segment in evaluated.Evaluation.Segments)
        {
            evaluated.CancellationToken.ThrowIfCancellationRequested();
            if (segment.Origin.Length != segment.Text.Length || segment.OutputOffset < 0
                || segment.OutputOffset > evaluated.Evaluation.Text.Length - segment.Text.Length)
                throw new InvalidOperationException("Expanded output lacks exact captured segment evidence.");
        }
        // The union of the unchanged per-range oracle spans is exactly the old
        // segment-by-region mask: any invoked range blanks that whole segment;
        // otherwise only the qualifying literal intersections are blanked.
        char[]? text = null;
        foreach (var region in regions)
            foreach (var span in evaluated.GetProjectRegionSpans(region))
                if (span.End > span.Start)
                {
                    text ??= evaluated.Evaluation.Text.ToCharArray();
                    Blank(span.Start, span.End - span.Start);
                }
        if (text is null)
        {
            diagnostics?.Add(WccSourceSaveCount.UnchangedRegionMasks);
            return evaluated;
        }
        var masked = new string(text);
        var evaluation = evaluated.Evaluation.WithMaskedTextPreservingMappings(masked);
        WmlStaticScalarProjection projection;
        diagnostics?.Add(WccSourceSaveCount.ScalarProjections);
        diagnostics?.Add(WccSourceSaveCount.MaskedProjections);
        using (diagnostics?.Measure(WccSourceSavePhase.MaskedProjection))
        {
            projection = WmlStaticScalarProjection.CreateMasked(evaluation, evaluated.Projection,
                evaluated.CancellationToken, out var analysisReused, out var reusedContributions);
            diagnostics?.Add(analysisReused ? WccSourceSaveCount.MaskedAnalysisReuses
                : WccSourceSaveCount.MaskedAnalysisFallbacks);
            diagnostics?.Add(WccSourceSaveCount.MaskedContributionReuses, reusedContributions);
        }
        diagnostics?.ObserveProjection(projection);
        return new(evaluation, projection) { Diagnostics = diagnostics, CancellationToken = evaluated.CancellationToken };

        void Blank(int start, int length)
        {
            for (var index = start; index < start + length; index++)
                if (text![index] is not ('\r' or '\n')) text[index] = ' ';
        }
    }

    private static void ValidateProjectRegions(PairedEvaluation evaluated,
        IReadOnlyList<WccProjectAuthorizedRegion> regions, bool proposed,
        WmlStaticScalarOwner scenario, WccScenarioInitialSettingsEdit? settings)
    {
        if (scenario.OpeningOutputOffset is not { } rootStart || scenario.ClosingOutputOffset is not { } rootEnd)
            throw new InvalidOperationException("A structural source draft has unresolved scenario boundaries.");
        foreach (var region in regions)
        {
            var range = proposed ? region.After : region.Before;
            var spans = evaluated.GetProjectRegionSpans(range);
            // An amended scenario retains its first closing offset, before later
            // literal children. These typed recipes have already proved their exact
            // child/field ownership and footprint in this context; the old closing
            // offset cannot bound those children. Captured music/schedule recipes
            // also pass independent current-context footprint validation first.
            // RAW and detached settings retain their stricter extent requirement.
            // All recipes still pass the owner
            // checks here and the final outside-output/history comparison.
            var checkedAmendedChild = scenario.IsAmended && (region.Kind == "DifficultyField" ||
                region.UnitInstance is not null || region.LeaderAddition is not null ||
                settings?.CapturedContext is not null && (region.Kind is "Music" or "Schedule"));
            if (region.Kind is not ("SideFlag" or "Event") && spans.Any(span =>
                    span.Start < rootStart + scenario.OpeningOrigin!.Length || span.End > rootEnd && !checkedAmendedChild))
                throw new InvalidOperationException("A structural draft emits output outside its exact scenario owner.");
            foreach (var owner in evaluated.Projection.Owners)
            {
                if (owner.OpeningOutputOffset is not { } start || !spans.Any(span => start >= span.Start && start < span.End)) continue;
                var top = owner;
                while (top.Parent is not null && !ReferenceEquals(top.Parent, scenario)) top = top.Parent;
                if (!ReferenceEquals(top.Parent, scenario) || region.Kind == "Schedule" && top.TagName != "time"
                    || region.Kind == "Event" && top.TagName != "event"
                    || region.Kind == "Music" && top.TagName is not ("music" or "event"))
                    throw new InvalidOperationException("An initial-settings or RAW draft changes another expanded owner outside its approved footprint.");
                if (region.Kind == "Music" && (owner.TagName is not ("music" or "event")
                    || owner.TagName == "event" && (owner.Fields.Keys.Any(key => key != "name")
                        || WccScenarioFieldValueCodec.DecodeLiteral(owner.GetKnownValue("name") ?? "") != "prestart")
                    || owner.TagName == "music" && owner.Fields.Keys.Any(key => key is not
                        ("name" or "title" or "append" or "play_once" or "immediate" or "shuffle" or "ms_before" or "ms_after"))))
                    throw new InvalidOperationException("A music footprint contains commands or attributes outside the exact initial-playlist templates.");
            }
            foreach (var (key, field) in scenario.Fields)
                foreach (var contribution in field.Contributions)
                    if (spans.Any(span => contribution.AssignmentOutputOffset >= span.Start
                            && contribution.AssignmentOutputOffset < span.End)
                        && (region.Kind == "Music" || region.Kind == "Schedule" && key is not ("current_time" or "random_start_time")))
                        throw new InvalidOperationException("An initial-settings macro changes another scenario scalar outside its approved footprint.");
        }
    }

    private static void ValidateRepresentedInitialSource(PairedEvaluation evaluated, WmlStaticScalarOwner scenario,
        WccScenarioInitialSettingsEdit? settings)
    {
        if (settings is null) return;
        if (settings.Music is not null)
        {
            var projection = settings.CapturedContext is null
                ? ScenarioInitialMusicEditor.Project(settings.ScenarioRange.Text, settings.Definitions)
                : ScenarioInitialMusicEditor.Project(settings.ScenarioRange, evaluated.Projection, settings.Definitions);
            if (!projection.CanEdit) throw new InvalidOperationException(projection.Problem);
            var ranges = projection.SourceEntries.Select(entry => new WmlStaticSourceRange(settings.ScenarioRange.Source,
                settings.ScenarioRange.StartOffset + entry.StartOffset, entry.EndOffsetExclusive - entry.StartOffset)).ToArray();
            foreach (var owner in scenario.DirectChildren.Where(owner => owner.TagName == "music"))
                if (owner.OpeningOutputOffset is not { } opening || !ranges.Any(range =>
                        evaluated.GetProjectRegionSpans(range).Any(span => opening >= span.Start && opening < span.End)))
                    throw new InvalidOperationException("Another captured root macro supplies initial music outside the represented playlist source.");
            foreach (var owner in scenario.DirectChildren.Where(owner => owner.TagName == "event"
                         && (owner.ResolveField("name").State != WmlStaticScalarResolutionState.Known
                             || owner.GetKnownValue("name") is not { } trigger
                             || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(trigger)
                             || WccScenarioFieldValueCodec.DecodeLiteral(trigger)
                                 .Split(',', StringSplitOptions.TrimEntries).Contains("prestart", StringComparer.Ordinal))
                         && HasImmediateProjectMusic(owner)
                         && !IsProjectLiteralOpening(owner, settings.ScenarioRange)))
                if (owner.OpeningOutputOffset is not { } opening || !ranges.Any(range =>
                        evaluated.GetProjectRegionSpans(range).Any(span => opening >= span.Start && opening < span.End)))
                    throw new InvalidOperationException("An unrepresented generated prestart handler supplies another initial playlist.");
        }
        if (settings.Schedule is not null)
        {
            var projection = settings.CapturedContext is null
                ? ScenarioInitialScheduleEditor.Project(settings.ScenarioRange.Text, settings.Definitions, settings.SourceTextDomain)
                : ScenarioInitialScheduleEditor.Project(settings.ScenarioRange, evaluated.Projection, settings.Definitions, settings.SourceTextDomain);
            if (!projection.CanCompose) throw new InvalidOperationException(projection.Diagnostic);
            var ranges = projection.State.Fragments.Select(fragment => new WmlStaticSourceRange(settings.ScenarioRange.Source,
                settings.ScenarioRange.StartOffset + fragment.Start, fragment.Length)).ToArray();
            foreach (var owner in scenario.DirectChildren.Where(owner => owner.TagName == "time"))
                if (owner.OpeningOutputOffset is not { } opening || !ranges.Any(range =>
                        evaluated.GetProjectRegionSpans(range).Any(span => opening >= span.Start && opening < span.End)))
                    throw new InvalidOperationException("Another captured root macro supplies global times outside the represented schedule source.");
            var startRanges = ranges.Concat(projection.State.StartFragments.Select(fragment => new WmlStaticSourceRange(
                settings.ScenarioRange.Source, settings.ScenarioRange.StartOffset + fragment.Start, fragment.Length))).ToArray();
            foreach (var key in new[] { "current_time", "random_start_time" })
                if (scenario.Fields.TryGetValue(key, out var field))
                    foreach (var contribution in field.Contributions.Where(contribution => contribution.ExpansionStack.Count != 0
                                 || contribution.AssignmentOrigins.Any(origin => !WccScenarioFieldRouteAnalyzer.SameSource(origin.Source, settings.ScenarioRange.Source))))
                        if (!startRanges.Any(range => evaluated.GetProjectRegionSpans(range).Any(span =>
                                contribution.AssignmentOutputOffset >= span.Start && contribution.AssignmentOutputOffset < span.End)))
                            throw new InvalidOperationException("An unrepresented root helper supplies another initial schedule start.");
        }
    }

    private static bool HasImmediateProjectMusic(WmlStaticScalarOwner owner) =>
        owner.DirectChildren.Any(child => child.TagName == "music" || child.TagName != "event" && HasImmediateProjectMusic(child));

    internal static IReadOnlyList<(int Start, int End)> ProjectRegionSpans(WmlStaticEvaluationResult evaluation,
        WmlStaticSourceRange range)
    {
        var spans = new List<(int Start, int End)>();
        foreach (var segment in evaluation.Segments)
            AppendProjectRegionSpan(segment, range, spans);
        return spans;
    }

    private static void AppendProjectRegionSpan(WmlStaticOutputSegment segment,
        WmlStaticSourceRange range, List<(int Start, int End)> spans)
    {
        if (ProjectInvokedFrame(segment, range) >= 0)
            spans.Add((segment.OutputOffset, segment.OutputOffset + segment.Text.Length));
        else if (WccScenarioFieldRouteAnalyzer.SameSource(segment.Origin.Source, range.Source)
            && IsProjectLiteralOrigin(segment, range))
        {
            var start = Math.Max(range.StartOffset, segment.Origin.StartOffset);
            var end = Math.Min(range.StartOffset + range.Length, segment.Origin.StartOffset + segment.Origin.Length);
            if (end > start) spans.Add((segment.OutputOffset + start - segment.Origin.StartOffset,
                segment.OutputOffset + end - segment.Origin.StartOffset));
        }
    }

    /// <summary>
    /// Bounded memoization of the legacy mapping oracle within one exact paired
    /// evaluation. Keys retain source object identity; no path/hash-only cache
    /// can reuse offsets from another source revision or evaluation.
    /// </summary>
    private sealed class ProjectRegionSpanCache(WmlStaticEvaluationResult evaluation,
        WccSourceSaveDiagnosticCollector? diagnostics, CancellationToken cancellationToken = default)
    {
        private const int MaximumRanges = 256;
        private const int MaximumRetainedSpans = 32_768;
        private readonly Dictionary<WmlStaticSourceRange, IReadOnlyList<(int Start, int End)>> _spans = [];
        private int _retainedSpans;
        private ProjectRegionSegmentIndex? _segmentIndex;

        public bool Matches(WmlStaticEvaluationResult candidate) => ReferenceEquals(evaluation, candidate);

        public IEnumerable<WmlStaticOutputSegment> GetSegments(WmlStaticSourceRange range) =>
            (_segmentIndex ??= new(evaluation, diagnostics, cancellationToken)).Get(range);

        public IReadOnlyList<(int Start, int End)> Get(WmlStaticSourceRange range)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics?.Add(WccSourceSaveCount.RegionSpanRequests);
            if (_spans.TryGetValue(range, out var cached))
            {
                diagnostics?.Add(WccSourceSaveCount.RegionSpanCacheHits);
                return cached;
            }
            diagnostics?.Add(WccSourceSaveCount.RegionSpanBuilds);
            var spans = new List<(int Start, int End)>();
            foreach (var segment in GetSegments(range))
                AppendProjectRegionSpan(segment, range, spans);
            if (_spans.Count >= MaximumRanges || spans.Count > MaximumRetainedSpans - _retainedSpans)
                return spans; // Exact legacy fallback; no eviction or broadened proof.
            var retained = Array.AsReadOnly(spans.ToArray());
            _spans.Add(range, retained);
            _retainedSpans += retained.Count;
            diagnostics?.Add(WccSourceSaveCount.RegionSpanCachedRanges);
            diagnostics?.Add(WccSourceSaveCount.RegionSpanRetainedSpans, retained.Count);
            return retained;
        }
    }

    private static string? CheckProjectFieldHistories(PairedEvaluation before, PairedEvaluation after,
        IReadOnlyList<ReviewedFieldTarget> targets, WccScenarioFieldSourceChange change)
    {
        var targetKeys = targets.Select(target => (target.OldOwner, target.NewOwner, target.Route.Key)).ToHashSet();
        for (var index = 0; index < before.Projection.Owners.Count; index++)
        {
            var oldOwner = before.Projection.Owners[index]; var newOwner = after.Projection.Owners[index];
            foreach (var key in oldOwner.Fields.Keys.Concat(newOwner.Fields.Keys).Distinct(StringComparer.Ordinal))
            {
                if (targetKeys.Contains((oldOwner, newOwner, key))) continue;
                var oldValue = oldOwner.ResolveField(key); var newValue = newOwner.ResolveField(key);
                // CompareOwners already verifies changed winner event consumers
                // through exact local scalar-argument invocation ancestry.
                if (oldValue.State != newValue.State || oldValue.ValueText != newValue.ValueText)
                {
                    if (!newOwner.Fields.TryGetValue(key, out var field) || ExactCurrentContribution(field) is not { } current
                        || !targets.Any(target => target.Route.Owner.TagName != "campaign"
                            && target.Route.Kind is WccScenarioFieldRouteKind.OptionalArgument or WccScenarioFieldRouteKind.ScalarArgument
                            && target.Route.Invocation is { } invocation && current.ExpansionStack.Any(frame =>
                                frame.InvocationOrigins.Any(origin => SameMappedRange(invocation, origin, change)))))
                        return "An unrequested consumer changes outside the verified local Scenario argument routes.";
                    continue;
                }
                if (!oldOwner.Fields.TryGetValue(key, out var oldField) || !newOwner.Fields.TryGetValue(key, out var newField)
                    || oldField.Contributions.Count != newField.Contributions.Count)
                    return "An unrequested assignment history changed in the combined candidate.";
                for (var contribution = 0; contribution < oldField.Contributions.Count; contribution++)
                {
                    var old = oldField.Contributions[contribution]; var current = newField.Contributions[contribution];
                    if (old.ValueMappingIsExact != current.ValueMappingIsExact
                        || !old.CompositeAssignmentKeys.SequenceEqual(current.CompositeAssignmentKeys, StringComparer.Ordinal)
                        || !SameAssignmentOutput(old, current, before.Evaluation.Text, after.Evaluation.Text)
                        || !SameMappedOrigins(old.AssignmentOrigins, current.AssignmentOrigins, change)
                        || !SameMappedOrigins(old.ValueOrigins, current.ValueOrigins, change)
                        || !SameMappedFrames(old.ExpansionStack, current.ExpansionStack, change)
                        || !SameMappedFrames(old.ValueExpansionStack, current.ValueExpansionStack, change))
                        return "An unrequested field changes its exact ordered assignment history in the combined candidate.";
                }
            }
        }
        return null;
    }
}
