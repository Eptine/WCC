using Wcc.Core.Library;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateScenarioCreationBinding(WccScenarioCreationEdit edit,
        WccScenarioFieldEditSessionState state, WmlStaticScalarOwner anchor,
        WmlCampaignEvaluationPlan? plan, WccStaticSourceCaptureResult capture)
    {
        if (state.ReviewScope != WccScenarioFieldReviewScope.CampaignMenu || plan is null
            || !ReferenceEquals(edit.Source, state.Source) || !IsCampaignMenuSource(state, plan, anchor)
            || edit.Snapshot.Plan is not { } oldPlan || oldPlan.CampaignMainTextHash != plan.CampaignMainTextHash
            || oldPlan.CampaignDefine != plan.CampaignDefine
            || !oldPlan.Contexts.Select(context => context.SemanticKey).SequenceEqual(plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal))
            throw new InvalidOperationException("Scenario creation needs the exact current campaign main menu session and fixed declared contexts.");
        var fresh = WccScenarioCreationPlanner.Read(state.Source, edit.Snapshot.Structure.Authoring, plan, capture.Sources);
        if (!fresh.CanCreate) throw new InvalidOperationException(fresh.Diagnostic);
        ValidateCampaignStructureOwner(fresh.Structure, anchor);
        var freshEdit = WccScenarioCreationPlanner.CreateEdit(fresh, edit.Spec);
        var originalOwnership = WccScenarioCreationPlanner.PlanOwnershipCandidate(edit);
        var freshOwnership = WccScenarioCreationPlanner.PlanOwnershipCandidate(freshEdit);
        if ((originalOwnership is null) != (freshOwnership is null) || originalOwnership is not null && freshOwnership is not null &&
            (!Wcc.Core.Campaigns.WccCampaignPackageEdit.Same(originalOwnership.Source, freshOwnership.Source) || originalOwnership.Candidate != freshOwnership.Candidate))
            throw new InvalidOperationException("The new scenario's exact chapter ownership companion changed after capture.");
        if (edit.Spec.Guided is { } guided)
        {
            foreach (var definition in CreationDefinitions(guided)) RequireCapturedDefinition(definition, capture);
            var catalog = WccCampaignLocalPresetCatalog.Build(capture, plan);
            foreach (var selected in CreationPresets(guided))
            {
                var found = catalog.Entries.Where(entry => entry.Name == selected.Name || entry.Id == selected.Id).ToArray();
                if (found.Length != 1 || !found[0].IsSupported || found[0].Id != selected.Id || found[0].Name != selected.Name
                    || found[0].Role != selected.Role || !SameRange(found[0].DefinitionRange, selected.DefinitionRange)
                    || !SameRange(found[0].BodyRange, selected.BodyRange))
                    throw new InvalidOperationException("A guided preset is not its exact supported saved definition in this campaign.");
            }
        }
        _ = WccStaticSourceCapture.CreateCandidateCapture(capture, state.RootRevisionId, [], WccScenarioCreationPlanner.PlanSources(edit));
    }

    private static IReadOnlyList<WmlStaticSourceRange> ValidateProjectScenarioCreation(WccScenarioCreationEdit edit,
        PairedEvaluation baseline, PairedEvaluation proposed, WccStaticSourceCaptureResult candidateCapture,
        WmlEvaluationContext context, WccScenarioFieldSourceChange change)
    {
        var planned = WccScenarioCreationPlanner.PlanSources(edit); var source = planned[0];
        foreach (var file in planned)
        {
            var captured = candidateCapture.Sources.Where(current => PathComparer.Equals(current.Path, file.Path)).ToArray();
            if (captured.Length != 1 || captured[0].ContentHash != file.ContentHash || captured[0].Text != file.Text)
                throw new InvalidOperationException("A created source differs from its exact typed scenario recipe.");
        }
        if (baseline.Evaluation.Sources.Any(old => planned.Any(file => PathComparer.Equals(old.Path, file.Path))))
            throw new InvalidOperationException("A new scenario file already belongs to the baseline evaluation.");
        var roots = proposed.Projection.Owners.Where(owner => owner.TagName == "scenario" && owner.Parent is null
            && owner.OpeningOrigin is { } opening && PathComparer.Equals(opening.Source.Path, source.Path)).ToArray();
        var visits = proposed.Evaluation.Includes.SelectMany(include => include.Resolution.Sources)
            .Count(file => PathComparer.Equals(file.Path, source.Path));
        if (context.CampaignDefine is null)
        {
            if (roots.Length != 0 || visits != 0 || proposed.Evaluation.Sources.Any(file => PathComparer.Equals(file.Path, source.Path)))
                throw new InvalidOperationException("The new gameplay scenario unexpectedly becomes active in the campaign menu.");
            return [];
        }
        if (roots.Length != 1 || visits != 1 || roots[0].OpeningOrigin?.Source.ContentHash != source.ContentHash)
            throw new InvalidOperationException("The new scenario is not included exactly once as one literal root in every fixed gameplay context.");
        var root = roots[0]; var range = new WmlStaticSourceRange(candidateCapture.Sources.Single(file => PathComparer.Equals(file.Path, source.Path)), 0, source.Text.Length);
        if (edit.Spec.Mode == WccScenarioCreationMode.Guided)
        {
            var turnsSlots = WccScenarioCreationPlanner.CaptureTurnsDifficultyValues(edit.Spec.Guided?.TurnsDifficultyValues, edit.Snapshot.Plan!);
            var expectedTurns = turnsSlots is null ? edit.Spec.Turns.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : turnsSlots.Single(slot => slot.ContextSemanticKey == context.SemanticKey && slot.Define == context.DifficultyDefine).ValueText;
            if (root.GetKnownValue("turns") != expectedTurns)
                throw new InvalidOperationException("Created turns differ from their requested shared or fixed-context value.");
        }
        foreach (var scenario in proposed.Projection.Owners.Where(owner => owner.TagName == "scenario" && owner.Parent is null))
        {
            var id = scenario.ResolveField("id");
            if (id.State != WmlStaticScalarResolutionState.Known || id.ValueText is null || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(id.ValueText))
                throw new InvalidOperationException("Unique active scenario IDs require complete literal identity context.");
            if (!ReferenceEquals(scenario, root) && WccScenarioFieldValueCodec.DecodeLiteral(id.ValueText) == edit.Spec.Id)
                throw new InvalidOperationException("The new scenario ID already belongs to another active scenario in this campaign.");
        }
        if (edit.Spec.Guided is { } options)
            ValidateGuidedCreation(edit, options, baseline, proposed, root, range, context, change);
        else
        {
        // Only the direct typed literal scaffold is evaluated here, without Core
        // preprocessing. The actual schedule is proved separately from captured
        // invocation-time Core origins and its complete six ordered time slots.
        var literalText = source.Text.Replace("    {DEFAULT_SCHEDULE}\n", "", StringComparison.Ordinal);
        var literalSource = new WmlStaticSource(source.Path, literalText);
        var expectedEvaluation = new WmlStaticEvaluator().Evaluate(new(literalSource, context,
            KnownUndefinedSymbols: edit.Snapshot.Plan!.Difficulties.Select(row => row.Define).Where(define => define != context.DifficultyDefine).ToArray(),
            IsInitialEnvironmentComplete: true));
        var expectedProjection = WmlStaticScalarProjection.Create(expectedEvaluation);
        if (!expectedEvaluation.IsComplete || expectedProjection.Diagnostics.Count != 0 || expectedProjection.Owners.Count(owner => owner.TagName == "scenario" && owner.Parent is null) != 1)
            throw new InvalidOperationException("The typed literal scenario scaffold could not be independently projected.");
        var expected = expectedProjection.Owners.Single(owner => owner.TagName == "scenario" && owner.Parent is null);
        ValidateLiteralTree(root, expected, allowSchedule: edit.Spec.Mode == WccScenarioCreationMode.Guided);
        if (edit.Spec.Mode == WccScenarioCreationMode.Guided)
        {
            var times = root.DirectChildren.Where(child => child.TagName == "time").ToArray();
            if (times.Length != 6 || !times.Select(time => WccScenarioFieldValueCodec.DecodeLiteral(time.GetKnownValue("id")))
                    .SequenceEqual(["dawn", "morning", "afternoon", "dusk", "first_watch", "second_watch"], StringComparer.Ordinal)
                || !root.DirectChildren.Take(6).SequenceEqual(times))
                throw new InvalidOperationException("The Guided schedule differs from the complete ordered default Core time slots.");
            var call = proposed.Evaluation.Invocations.Where(call => call.Name == "DEFAULT_SCHEDULE" && call.InvocationOrigins.Any(origin => InsideProposedScalar(origin, range))).ToArray();
            if (call.Length != 1 || call[0].Outcome != WmlStaticInvocationOutcome.Expanded || call[0].InvocationOrigins.Count != 1
                || call[0].InvocationOrigins[0].Text != "{DEFAULT_SCHEDULE}" || call[0].Definition is not { } definition || !CoreOrigin(definition))
                throw new InvalidOperationException("Guided creation needs the actual captured Core DEFAULT_SCHEDULE definition, without campaign shadowing.");
            foreach (var time in times)
            {
                if (time.IsAmended || time.HasUnresolvedContributions || time.DirectChildren.Count != 0
                    || time.OpeningOrigin is not { } opening || time.ClosingOrigin is not { } closing
                    || !CoreOrigin(opening) || !CoreOrigin(closing) || !ScheduleFrames(time.OpeningExpansionStack) || !ScheduleFrames(time.ClosingExpansionStack))
                    throw new InvalidOperationException("A Guided time slot is not the complete exact captured Core subtree.");
                foreach (var field in time.Fields.Values)
                    if (!field.IsKnown || field.Contributions.Count != 1 || field.Contributions[0].AssignmentOrigin is not { } assignment
                        || !CoreOrigin(assignment) || !ScheduleFrames(field.Contributions[0].ExpansionStack)
                        || field.Contributions[0].ValueOrigins.Count == 0 || field.Contributions[0].ValueOrigins.Any(origin => !CoreOrigin(origin)))
                        throw new InvalidOperationException("A Guided time field differs from its sole captured Core contribution.");
            }
            var ids = root.DirectChildren.Where(child => child.TagName == "side").SelectMany(side => side.DirectChildren)
                .Where(child => child.TagName == "leader").Select(leader => WccScenarioFieldValueCodec.DecodeLiteral(leader.GetKnownValue("id"))).ToArray();
            if (ids.Length != 2 || ids.Distinct(StringComparer.Ordinal).Count() != 2)
                throw new InvalidOperationException("The Guided leaders need distinct initial unit IDs in their scenario.");
        }
        }
        if (root.OpeningOutputOffset is not { } start || root.ClosingOutputOffset is not { } end || root.ClosingOrigin is not { } rootClosing)
            throw new InvalidOperationException("The new scenario has no exact complete expanded boundaries.");
        var spans = proposed.GetProjectRegionSpans(range); var masked = WmlAuthoredStructureScanner.Scan(proposed.Evaluation.Text).MaskedText;
        bool Inside(int offset) => spans.Any(span => offset >= span.Start && offset < span.End);
        for (var offset = start; offset < end + rootClosing.Length; offset++)
            if (!Inside(offset) && !char.IsWhiteSpace(masked[offset])) throw new InvalidOperationException("New scenario output consumes an existing source contribution.");
        foreach (var span in spans)
            for (var offset = span.Start; offset < span.End; offset++)
                if ((offset < start || offset >= end + rootClosing.Length) && !char.IsWhiteSpace(masked[offset]))
                    throw new InvalidOperationException("A new scenario file produces output outside its one exact literal scenario.");
        return [range];

        bool CoreOrigin(WmlStaticSourceRange origin) => candidateCapture.PreludeSources.Any(core => core.IsAuthored
            && PathComparer.Equals(core.Path, origin.Source.Path) && core.ContentHash == origin.Source.ContentHash
            && origin.StartOffset >= 0 && origin.Length > 0 && origin.StartOffset <= core.Text.Length - origin.Length);
        bool ScheduleFrames(IReadOnlyList<WmlStaticExpansionFrame> frames)
        {
            var first = frames.Select((frame, index) => (frame, index)).FirstOrDefault(pair => pair.frame.InvocationOrigins.Any(origin => InsideProposedScalar(origin, range)));
            if (first.frame is null || first.frame.MacroName != "DEFAULT_SCHEDULE" || first.frame.InvocationOrigins.Count != 1
                || first.frame.InvocationOrigins[0].Text != "{DEFAULT_SCHEDULE}") return false;
            return frames.Skip(first.index).All(frame => CoreOrigin(frame.Definition) && frame.RequiredParameters is { Count: 0 }
                && frame.OptionalParameters is { Count: 0 } && frame.InvocationOrigins.Count == 1);
        }
        void ValidateLiteralTree(WmlStaticScalarOwner actual, WmlStaticScalarOwner desired, bool allowSchedule = false)
        {
            var children = actual.DirectChildren.Where(child => !allowSchedule || child.TagName != "time").ToArray();
            if (actual.IsAmended || actual.HasUnresolvedContributions || actual.TagName != desired.TagName || actual.Fields.Count != desired.Fields.Count
                || actual.OpeningOrigin is not { } opening || actual.ClosingOrigin is not { } closing
                || !InsideProposedScalar(opening, range) || !InsideProposedScalar(closing, range) || children.Length != desired.DirectChildren.Count)
                throw new InvalidOperationException("The new scenario differs from its exact whole literal owner and ordered child footprint.");
            foreach (var pair in desired.Fields)
            {
                if (!actual.Fields.TryGetValue(pair.Key, out var field) || !field.IsKnown || !pair.Value.IsKnown || field.Contributions.Count != 1
                    || field.ValueText != pair.Value.ValueText || field.Contributions[0].AssignmentOrigin is not { } assignment
                    || !InsideProposedScalar(assignment, range) || field.Contributions[0].AssignmentOrigins.Count != 1
                    || field.Contributions[0].CompositeAssignmentKeys.Count != 0 || !field.Contributions[0].ValueMappingIsExact
                    || field.Contributions[0].ValueOrigins.Count == 0 || field.Contributions[0].ValueOrigins.Any(origin => !InsideProposedScalar(origin, range)))
                    throw new InvalidOperationException("A created scenario field lacks its sole exact requested literal contribution: " + pair.Key);
            }
            for (var index = 0; index < children.Length; index++) ValidateLiteralTree(children[index], desired.DirectChildren[index]);
        }
    }

    private static IEnumerable<WccCampaignLocalPresetEntry> CreationPresets(WccScenarioGuidedCreationOptions options) =>
        options.Sides.Where(side => side.Preset is not null).Select(side => side.Preset!)
            .Concat(options.Sides.SelectMany(side => side.Leaders ?? []).Where(leader => leader.Preset is not null).Select(leader => leader.Preset!))
            .DistinctBy(entry => entry.Id);

    private static IReadOnlyList<WmlMacroExpansionDefinition> CreationDefinitions(WccScenarioGuidedCreationOptions options)
    {
        var supplied = (options.Music?.Definitions ?? []).Concat(options.Schedule?.Definitions ?? []).ToArray();
        var required = new Queue<string>();
        if (options.Schedule?.Edit.PresetInvocation is { } invocation && WmlMacroInvocationSyntaxScanner.TryParseSingle(invocation, out var schedule))
            required.Enqueue(schedule!.Name);
        if (options.Music is { Tracks.Count: > 0 } music && (music.MakeFirstIntro || music.LetIntroFinish))
        {
            required.Enqueue(music.MakeFirstIntro ? "INTRO_AND_SCENARIO_MUSIC" : "SCENARIO_MUSIC");
            if (music.Tracks.Count > (music.MakeFirstIntro ? 2 : 1)) required.Enqueue("EXTRA_SCENARIO_MUSIC");
            if (music.LetIntroFinish) required.Enqueue("LET_INTRO_MUSIC_FINISH");
        }
        var found = new List<WmlMacroExpansionDefinition>(); var visited = new HashSet<string>(StringComparer.Ordinal);
        while (required.TryDequeue(out var name))
        {
            if (!visited.Add(name)) continue;
            var matches = supplied.Where(definition => definition.Name == name)
                .DistinctBy(definition => (definition.SourcePath, definition.SourceStartLine, definition.TextDomain, NormalizeDefinition(definition.RawText))).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("The selected creation macro needs one exact captured definition: " + name);
            found.Add(matches[0]);
            foreach (var child in WmlMacroInvocationSyntaxScanner.Scan(matches[0].RawText))
                if (supplied.Any(definition => definition.Name == child.Name)) required.Enqueue(child.Name);
        }
        return Array.AsReadOnly(found.ToArray());
    }

    private static void ValidateGuidedCreation(WccScenarioCreationEdit edit, WccScenarioGuidedCreationOptions options,
        PairedEvaluation baseline, PairedEvaluation proposed, WmlStaticScalarOwner root, WmlStaticSourceRange range,
        WmlEvaluationContext context, WccScenarioFieldSourceChange change)
    {
        var definitions = CreationDefinitions(options);
        var allowed = new Dictionary<string, WmlStaticSourceRange>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var sources = baseline.Evaluation.Sources.Where(source => PathComparer.Equals(source.Path, definition.SourcePath)).DistinctBy(source => source.ContentHash).ToArray();
            if (sources.Length != 1 || definition.SourceStartLine is not { } line
                || definition.TextDomain != WmlTextDomainCapture.BeforeLine(sources[0].Text, line))
                throw new InvalidOperationException("The chosen creation definition has no exact active captured source.");
            var physical = WmlMacroDefinitionScanner.Scan(sources[0].Text).Where(candidate => candidate.Name == definition.Name
                && candidate.StartLineIndex == line && NormalizeDefinition(candidate.RawText) == NormalizeDefinition(definition.RawText)).ToArray();
            if (physical.Length != 1) throw new InvalidOperationException("The chosen creation macro is not its captured definition.");
            var lines = WmlAuthoredStructureScanner.Scan(sources[0].Text).Lines;
            var start = lines[line].StartOffset;
            while (start < lines[line].ContentEndOffset && sources[0].Text[start] is ' ' or '\t') start++;
            var original = new WmlStaticSourceRange(sources[0], start, lines[physical[0].EndLineIndexExclusive - 1].EndOffsetExclusive - start);
            var mapped = change.MapRange(original);
            if (mapped is null || mapped.Text != original.Text)
                throw new InvalidOperationException("A chosen shared creation macro changed in this Save batch.");
            allowed.Add(definition.Name, mapped);
        }
        var expectedText = range.Text;
        foreach (var entry in CreationPresets(options))
        {
            var mapped = MappedPresetDefinition(entry, change);
            if (!allowed.TryAdd(entry.Name, mapped))
                throw new InvalidOperationException("A selected preset conflicts with another creation macro name.");
            var body = change.MapRange(entry.BodyRange);
            if (body is null || body.Text != entry.BodyRange.Text)
                throw new InvalidOperationException("A selected preset body changed during scenario creation.");
            // Expected values come from the immutable typed payload, never from the candidate's expansion.
            expectedText = expectedText.Replace("{" + entry.Name + "}", WccScenarioCreationWriter.RenderPayload(entry.Payload!, edit.Snapshot.Plan!, ""), StringComparison.Ordinal);
        }
        var expectedSource = new WmlStaticSource("wcc-created-scenario-expectation:" + range.Source.ContentHash, expectedText, isAuthored: false);
        var prelude = new WmlStaticSource("wcc-created-definition-expectation", string.Join("\n", definitions.Select(definition =>
            (definition.TextDomain is { } domain ? "#textdomain " + domain + "\n" : "") + definition.RawText + "\n")), isAuthored: false);
        var expectedEvaluation = new WmlStaticEvaluator().Evaluate(new(expectedSource, context, [prelude],
            edit.Snapshot.Plan!.Difficulties.Select(row => row.Define).Concat(["EDITOR", "MULTIPLAYER"])
                .Where(symbol => !context.DefinedSymbols.Contains(symbol)).Distinct(StringComparer.Ordinal).ToArray(), true));
        var expectedProjection = WmlStaticScalarProjection.Create(expectedEvaluation);
        if (!expectedEvaluation.IsComplete || expectedProjection.Diagnostics.Count != 0
            || expectedProjection.Owners.Count(owner => owner.TagName == "scenario" && owner.Parent is null) != 1)
            throw new InvalidOperationException("The captured schedule/music and typed side payloads have no complete independent expected footprint.");
        var expected = expectedProjection.Owners.Single(owner => owner.TagName == "scenario" && owner.Parent is null);
        ValidateTree(root, expected);
        var syntax = WmlMacroInvocationSyntaxScanner.Scan(range.Text).Where(call => !call.IsInsideMacroDefinition).ToArray();
        foreach (var call in syntax)
        {
            var invocation = new WmlStaticSourceRange(range.Source, range.StartOffset + call.Span.StartOffset, call.Span.Length);
            var actual = proposed.Evaluation.Invocations.Where(current => current.Name == call.Name
                && current.InvocationOrigins.Count == 1 && SameRange(current.InvocationOrigins[0], invocation)).ToArray();
            if (!allowed.TryGetValue(call.Name, out var definition) || actual.Length != 1
                || actual[0].Outcome != WmlStaticInvocationOutcome.Expanded || !SameRange(actual[0].Definition, definition))
                throw new InvalidOperationException("A created invocation does not select its exact requested active definition: " + call.Name);
        }
        if (proposed.Evaluation.Invocations.Where(call => call.InvocationOrigins.Count != 0
                && call.InvocationOrigins.All(origin => InsideProposedScalar(origin, range))).Count() != syntax.Length)
            throw new InvalidOperationException("The new scenario has additional or repeated local macro invocations.");
        var sides = root.DirectChildren.Where(child => child.TagName == "side").ToArray();
        if (sides.Length != options.Sides.Count || sides.Where((side, index) => Literal(side, "side") != (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Any())
            throw new InvalidOperationException("The created side numbers differ from their exact requested declaration order.");
        for (var index = 0; index < sides.Length; index++)
        {
            var requested = options.Sides[index];
            if (requested.Preset is { } preset) RequireRequestedPreset(sides[index], preset);
            var leaders = sides[index].DirectChildren.Where(child => child.TagName == "leader").ToArray();
            var inheritedCount = (requested.Payload ?? requested.Preset!.Payload!).Leaders.Count;
            if (requested.PresetLeaderCoordinates is not null)
            {
                var body = change.MapRange(requested.Preset!.BodyRange);
                if (inheritedCount != 1 || leaders.Length != 1 + (requested.Leaders?.Count ?? 0)
                    || body is null || leaders[0].OpeningOrigin is not { } opening || !InsideProposedScalar(opening, body)
                    || leaders[0].OpeningExpansionStack.Count != 1
                    || !SameRange(leaders[0].OpeningExpansionStack[0].Definition, allowed[requested.Preset.Name]))
                    throw new InvalidOperationException("A combined preset location needs one exact inherited nested leader in every difficulty.");
            }
            for (var leaderIndex = 0; leaderIndex < (requested.Leaders?.Count ?? 0); leaderIndex++)
                if (requested.Leaders![leaderIndex].Preset is { } selected)
                    RequireRequestedPreset(leaders[inheritedCount + leaderIndex], selected);
        }
        var unitIds = sides.SelectMany(side => side.DirectChildren.Where(child => child.TagName is "leader" or "unit")).Select(owner => Literal(owner, "id"))
            .Concat(sides.Where(side => Literal(side, "no_leader") is not ("yes" or "true") && Literal(side, "type") is { Length: > 0 } type && type != "null")
                .Select(side => Literal(side, "id"))).Where(id => !string.IsNullOrEmpty(id)).ToArray();
        var saveIds = sides.Select(side => Literal(side, "save_id") is { Length: > 0 } save ? save : Literal(side, "id") is { Length: > 0 } id ? id
            : side.DirectChildren.FirstOrDefault(child => child.TagName == "leader") is { } leader ? Literal(leader, "id") : null)
            .Where(id => !string.IsNullOrEmpty(id)).ToArray();
        if (unitIds.Distinct(StringComparer.Ordinal).Count() != unitIds.Length || saveIds.Distinct(StringComparer.Ordinal).Count() != saveIds.Length)
            throw new InvalidOperationException("The created scenario has duplicate initial unit or carryover save_id identities. Choose distinct local overrides.");

        void RequireRequestedPreset(WmlStaticScalarOwner owner, WccCampaignLocalPresetEntry preset)
        {
            if (owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing
                || !InsideProposedScalar(opening, range) || !InsideProposedScalar(closing, range))
                throw new InvalidOperationException("A selected preset must use its requested literal Side or Leader wrapper.");
            var wrapper = new WmlStaticSourceRange(range.Source, opening.StartOffset, closing.StartOffset + closing.Length - opening.StartOffset);
            var calls = proposed.Evaluation.Invocations.Where(call => call.Name == preset.Name && call.InvocationOrigins.Count == 1
                && InsideProposedScalar(call.InvocationOrigins[0], wrapper)).ToArray();
            if (calls.Length != 1 || calls[0].InvocationOrigins[0].Text != "{" + preset.Name + "}"
                || calls[0].Outcome != WmlStaticInvocationOutcome.Expanded || !SameRange(calls[0].Definition, allowed[preset.Name]))
                throw new InvalidOperationException("A selected preset invocation was omitted, repeated, or replaced by copied instance fields.");
        }

        string? Literal(WmlStaticScalarOwner owner, string key)
        {
            var field = owner.ResolveField(key);
            if (field.State == WmlStaticScalarResolutionState.ProvenAbsent) return null;
            if (field.State != WmlStaticScalarResolutionState.Known || field.ValueText is null || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(field.ValueText))
                throw new InvalidOperationException("A created identity has no complete literal value.");
            return WccScenarioFieldValueCodec.DecodeLiteral(field.ValueText);
        }
        bool Allowed(WmlStaticSourceRange origin, IReadOnlyList<WmlStaticExpansionFrame> frames)
        {
            if (!InsideProposedScalar(origin, range) && !allowed.Values.Any(definition => InsideProposedScalar(origin, definition))) return false;
            if (frames.Count == 0) return InsideProposedScalar(origin, range);
            var first = frames.Select((frame, index) => (frame, index)).FirstOrDefault(pair => pair.frame.InvocationOrigins.Any(invocation => InsideProposedScalar(invocation, range)));
            if (first.frame is null) return false;
            return frames.Skip(first.index).All(frame => allowed.TryGetValue(frame.MacroName, out var definition)
                && SameRange(frame.Definition, definition) && frame.InvocationOrigins.Count != 0
                && frame.InvocationOrigins.All(invocation => InsideProposedScalar(invocation, range)
                    || allowed.Values.Any(parent => InsideProposedScalar(invocation, parent))));
        }
        void ValidateTree(WmlStaticScalarOwner actual, WmlStaticScalarOwner desired)
        {
            if (actual.TagName != desired.TagName || actual.IsAmended != desired.IsAmended || actual.HasUnresolvedContributions
                || actual.Fields.Count != desired.Fields.Count || actual.DirectChildren.Count != desired.DirectChildren.Count
                || actual.OpeningOrigin is not { } opening || actual.ClosingOrigin is not { } closing
                || !Allowed(opening, actual.OpeningExpansionStack) || !Allowed(closing, actual.ClosingExpansionStack))
                throw new InvalidOperationException("The created scenario differs from the complete requested ordered owner footprint: " + desired.TagName);
            foreach (var pair in desired.Fields)
            {
                if (!actual.Fields.TryGetValue(pair.Key, out var field) || !field.IsKnown || !pair.Value.IsKnown
                    || field.ValueText != pair.Value.ValueText || field.Contributions.Count != pair.Value.Contributions.Count)
                    throw new InvalidOperationException("A created field differs from its exact requested value or assignment history: " + pair.Key);
                for (var index = 0; index < field.Contributions.Count; index++)
                {
                    var contribution = field.Contributions[index]; var requested = pair.Value.Contributions[index];
                    // Parameter substitutions may split an assignment across definition and invocation
                    // origins. Projection intentionally retains value frames only for scalar editor
                    // owners; for [time]/[music]/[event], the complete assignment frames prove them.
                    var valueFrames = contribution.ValueExpansionStack.Count != 0
                        ? contribution.ValueExpansionStack : contribution.ExpansionStack;
                    if (contribution.AssignmentOrigins.Count == 0
                        || contribution.AssignmentOrigins.Any(origin => !Allowed(origin, contribution.ExpansionStack))
                        || contribution.AssignmentOrigin is { } assignment && !Allowed(assignment, contribution.ExpansionStack)
                        || contribution.AssignmentOrigins.Count != requested.AssignmentOrigins.Count
                        || contribution.ValueOrigins.Count == 0 || contribution.ValueOrigins.Any(origin => !Allowed(origin, valueFrames))
                        || contribution.ValueMappingIsExact != requested.ValueMappingIsExact
                        || !contribution.CompositeAssignmentKeys.SequenceEqual(requested.CompositeAssignmentKeys, StringComparer.Ordinal)
                        || proposed.Evaluation.Text.Substring(contribution.ValueOutputOffset, contribution.ValueOutputLength)
                            != expectedEvaluation.Text.Substring(requested.ValueOutputOffset, requested.ValueOutputLength))
                        throw new InvalidOperationException("A created field lacks its exact typed literal or selected-definition contribution: " + pair.Key);
                }
            }
            for (var index = 0; index < actual.DirectChildren.Count; index++) ValidateTree(actual.DirectChildren[index], desired.DirectChildren[index]);
        }
    }
}
