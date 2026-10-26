using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Campaigns;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateCampaignPresetStorageBinding(WccCampaignLocalPresetStorageEdit edit,
        WccScenarioFieldEditSessionState state, WmlStaticScalarOwner anchor, WmlCampaignEvaluationPlan? plan,
        WccStaticSourceCaptureResult capture, WmlStaticSingleplayerEnvironment environment)
    {
        if (state.ReviewScope != WccScenarioFieldReviewScope.CampaignMenu || plan is null || !capture.IsCaptureComplete
            || !ReferenceEquals(edit.Source, state.Source) || !IsCampaignMenuSource(state, plan, anchor)
            || edit.Snapshot.Main.Plan is not { } oldPlan || oldPlan.CampaignMainTextHash != plan.CampaignMainTextHash
            || oldPlan.CampaignDefine != plan.CampaignDefine || !oldPlan.Contexts.Select(context => context.SemanticKey)
                .SequenceEqual(plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal))
            throw new InvalidOperationException("Preset storage requires this exact current main menu session and fixed contexts.");
        var fresh = WccCampaignLocalPresetStoragePlanner.Read(capture, edit.Snapshot.PackageAuthoring, plan, edit.Snapshot.PackagePlans, state.Source);
        if (!fresh.CanStore || fresh.Locations.Count != edit.Snapshot.Locations.Count
            || fresh.Locations.Where((location, index) => location.Scope != edit.Snapshot.Locations[index].Scope
                || location.RelativePath != edit.Snapshot.Locations[index].RelativePath
                || !SameStorageSource(location.Source, edit.Snapshot.Locations[index].Source)).Any()
            || !SameStorageSource(fresh.Manifest?.Source, edit.Snapshot.Manifest?.Source)
            || !SameEntries(fresh.MainEntries, edit.Snapshot.MainEntries) || !SameEntries(fresh.StoredEntries, edit.Snapshot.StoredEntries))
            throw new InvalidOperationException("The captured preset storage or main definitions changed. Reload their source before saving.");
        WccCampaignLocalPresetStoragePlanner.ValidateStorageGuardNamespace(fresh,
            capture.Sources.Concat(capture.PreludeSources).Append(environment.DefinitionsSource).ToArray());
        ValidateCampaignStructureOwner(fresh.Main.Structure, anchor);
        _ = WccCampaignLocalPresetStoragePlanner.CreateCombinedEdit(fresh, edit.Presets, edit.Mutations,
            capture.Sources.Concat(capture.PreludeSources).Append(environment.DefinitionsSource).ToArray());
        var existing = fresh.MainEntries.Concat(fresh.StoredEntries).ToArray();
        var newNames = edit.Presets.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        var names = existing.Select(row => row.Name).Concat(newNames).ToHashSet(StringComparer.Ordinal);
        var ids = existing.Select(row => row.Id).Concat(edit.Presets.Select(row => row.Id)).ToHashSet();
        WccCampaignPresetScopePolicy.ValidateReservedSymbols(names, fresh.PackagePlans);
        foreach (var source in capture.Sources.Concat(capture.PreludeSources).Append(environment.DefinitionsSource)
                     .DistinctBy(source => (source.Path, source.ContentHash)))
        {
            var analysis = WmlEditorAnalysis.Build(source.Text);
            foreach (var token in analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive))
            {
                var words = WmlInlineCommentSplitter.Split(source.Text.Substring(token.StartOffset, token.Length).Trim().TrimStart('#')).Value
                    .Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2 || !names.Contains(words[1])) continue;
                if (words[0] == "define" && existing.Any(entry => PathComparer.Equals(entry.SourcePath, source.Path)
                        && entry.SourceHash == source.ContentHash && token.StartOffset >= entry.DefinitionRange.StartOffset
                        && token.StartOffset < entry.BodyRange.StartOffset)) continue;
                if (words[0] is "define" or "undef" || newNames.Contains(words[1]) && words[0] is "ifdef" or "ifndef")
                    throw new InvalidOperationException("A preset name has another declaration or preprocessor state in the captured closure: " + words[1]);
            }

            foreach (var entry in WccCampaignLocalPresetCatalog.ReadDefinitions(source, plan).Where(entry => ids.Contains(entry.Id)))
                if (!existing.Any(expected => expected.Id == entry.Id && expected.Name == entry.Name && SameRange(expected.DefinitionRange, entry.DefinitionRange)))
                    throw new InvalidOperationException("A stored preset durable ID belongs to another captured declaration.");
        }
        foreach (var preset in edit.Presets)
            if (WccCampaignLocalPresetStoragePlanner.FindScopedConsumers(preset.Name, preset.Scope, fresh,
                capture.Sources.Concat(capture.PreludeSources).Append(environment.DefinitionsSource).ToArray()).Count != 0)
                throw new InvalidOperationException("A new preset name already has an active or unproved consumer in its scope.");

        static bool SameStorageSource(WmlStaticSource? left, WmlStaticSource? right) => left is null ? right is null
            : right is not null && PathComparer.Equals(left.Path, right.Path) && left.ContentHash == right.ContentHash && left.Text == right.Text;
        static bool SameEntries(IReadOnlyList<WccCampaignLocalPresetEntry> left, IReadOnlyList<WccCampaignLocalPresetEntry> right) =>
            left.Count == right.Count && left.Select((entry, index) => entry.Id == right[index].Id && entry.Name == right[index].Name
                && entry.Role == right[index].Role && entry.Scope == right[index].Scope && entry.IsSupported && right[index].IsSupported
                && SameRange(entry.DefinitionRange, right[index].DefinitionRange) && SameRange(entry.BodyRange, right[index].BodyRange)).All(value => value);
    }

    private static void ValidateProjectCampaignPresetStorage(WccCampaignLocalPresetStorageEdit edit,
        PairedEvaluation baseline, PairedEvaluation proposed, WmlEvaluationContext context, WmlCampaignEvaluationPlan plan,
        WccStaticSourceCaptureResult candidateCapture, WmlStaticSingleplayerEnvironment environment,
        WccScenarioFieldSourceChange change, IReadOnlyList<WccProjectAuthorizedRegion> regions,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        var mutating = edit.Mutations.Count != 0;
        var expectedSources = WccCampaignLocalPresetStoragePlanner.PlanStorageCandidates(edit).Select(candidate => candidate.CandidateSource)
            .Concat(WccCampaignLocalPresetStoragePlanner.PlanSources(edit)).ToArray();
        foreach (var expected in expectedSources)
        {
            var matches = candidateCapture.Sources.Where(source => PathComparer.Equals(source.Path, expected.Path)).ToArray();
            if (matches.Length != 1 || matches[0].ContentHash != expected.ContentHash || matches[0].Text != expected.Text)
                throw new InvalidOperationException("A proposed preset utility or companion differs from the exact collected recipe.");
        }
        var main = change.Substitute(edit.Source);
        var candidatePlans = WmlCampaignEvaluationPlanDiscovery.Discover(main.Text, main.Path, context.TargetCompatibility);
        var candidatePlan = candidatePlans.SingleOrDefault(candidate => candidate.CampaignId == plan.CampaignId && candidate.CampaignDefine == plan.CampaignDefine);
        var candidateStorage = WccCampaignLocalPresetStoragePlanner.Read(main, edit.Snapshot.PackageAuthoring,
            candidatePlan, candidateCapture.Sources, candidatePlans);
        if (!candidateStorage.CanStore) throw new InvalidOperationException(candidateStorage.Diagnostic);
        var mainDefinitions = candidateStorage.MainEntries;
        if (!mutating && mainDefinitions.Count != 0)
            throw new InvalidOperationException("Managed main definitions were not completely moved by this storage recipe.");
        var definitions = candidateStorage.Entries.ToArray();
        var previous = edit.Snapshot.MainEntries.Concat(edit.Snapshot.StoredEntries).ToArray();
        if (definitions.Length != previous.Length + edit.Presets.Count - edit.Mutations.Count(row => row.IsDeletion))
            throw new InvalidOperationException("Preset storage lost or introduced an unrequested definition.");
        foreach (var entry in previous)
        {
            var matches = definitions.Where(current => current.Id == entry.Id).ToArray();
            var mutation = edit.Mutations.SingleOrDefault(row => row.Target.Id == entry.Id);
            if (mutation?.IsDeletion == true)
            {
                if (matches.Length != 0) throw new InvalidOperationException("The requested unused preset was not removed.");
                continue;
            }
            var mapped = change.MapRange(entry.DefinitionRange);
            if (matches.Length != 1 || mapped is null || !SameRange(mapped, matches[0].DefinitionRange)
                || matches[0].Role != entry.Role || matches[0].Scope != entry.Scope || matches[0].DefinitionRange.Text != (mutation is null ? entry.DefinitionRange.Text
                    : WccCampaignLocalPresetStoragePlanner.RenderMutation(mutation, plan))
                || matches[0].Definition.TextDomain != entry.Definition.TextDomain)
                throw new InvalidOperationException("A shared definition changed instead of retaining its exact bytes, identity and translation domain.");
        }
        foreach (var added in edit.Presets)
        {
            var matches = definitions.Where(current => current.Id == added.Id).ToArray();
            if (matches.Length != 1 || matches[0].Id != added.Id || matches[0].Name != added.Name || matches[0].Role != added.Payload.Role || matches[0].Scope != added.Scope)
                throw new InvalidOperationException("A new stored preset is not its exact unique requested definition.");
        }
        foreach (var location in candidateStorage.Locations.Where(location => location.Source is not null))
        {
            var includes = proposed.Evaluation.Includes.ToList();
            var visits = includes.Where(include => include.Resolution.Sources.Any(item => PathComparer.Equals(item.Path, location.Source!.Path))).ToArray();
            var active = context.CampaignDefine is not null && location.Scope.Targets(plan);
            if (visits.Length != (active ? 2 : 0) || visits.Sum(include => include.Resolution.Sources.Count(item => PathComparer.Equals(item.Path, location.Source!.Path))) != visits.Length)
                throw new InvalidOperationException("Preset storage needs only its exact early file and later directory visits in the applicable scope.");
            if (!active) continue;
            var addon = candidateStorage.PackageAuthoring!.AddonId;
            var utilityPath = location.RelativePath[..location.RelativePath.LastIndexOf('/')];
            var utility = candidateStorage.Main.Structure.Layout!.Paths.Single(row => row.Row.Kind == WccCampaignPathKind.CfgInclude && row.Row.Path == utilityPath);
            var direct = visits.SingleOrDefault(include => include.IncludePath == "~add-ons/" + addon + "/" + location.RelativePath
                && ExactCaller(include.InvocationOrigins, location.IncludeRange!));
            var directory = visits.SingleOrDefault(include => include.IncludePath == "~add-ons/" + addon + "/" + utilityPath
                && ExactCaller(include.InvocationOrigins, utility.Range));
            if (direct is null || directory is null || direct.Resolution.Sources.Count != 1 || includes.IndexOf(direct) >= includes.IndexOf(directory)
                || visits.Any(include => include.Resolution.Sources.Any(item => PathComparer.Equals(item.Path, location.Source!.Path)
                    && (item.ContentHash != location.Source!.ContentHash || item.Text != location.Source.Text))))
                throw new InvalidOperationException("Preset storage does not have the exact captured early-file and directory-include receipts.");
        }
        // Keep every old consumer visible to the existing owner/history/output
        // comparisons. Before allowing coordinate maps, also compare actual
        // invocation-time selection, not merely the final macro environment.
        foreach (var entry in previous)
        {
            var oldCalls = baseline.Evaluation.Invocations.Where(call => call.Name == entry.Name && SameRange(call.Definition, entry.DefinitionRange) && !IsTypedOldCall(call, entry)).ToArray();
            var newCalls = proposed.Evaluation.Invocations.Where(call => call.Name == entry.Name && definitions.Any(definition => definition.Id == entry.Id && SameRange(call.Definition, definition.DefinitionRange)) && !IsTypedNewCall(call, entry)).ToArray();
            var mutation = edit.Mutations.SingleOrDefault(row => row.Target.Id == entry.Id);
            if (mutation?.IsDeletion == true)
            {
                if (oldCalls.Length != 0 || newCalls.Length != 0) throw new InvalidOperationException("A removed preset still has an active consumer.");
                continue;
            }
            var currentDefinition = definitions.Single(row => row.Id == entry.Id).DefinitionRange;
            if (mutation is not null && oldCalls.Length != 0 && mutation.Replacement!.Leaders.Count != entry.Payload!.Leaders.Count)
                throw new InvalidOperationException("A used shared preset must retain its nested leader topology so caller amendments cannot retarget another leader.");
            if (oldCalls.Length != newCalls.Length)
                throw new InvalidOperationException("A preset consumer changes outside its exact typed application or removal recipe.");
            for (var index = 0; index < oldCalls.Length; index++)
            {
                if (mutation is not null)
                {
                    RequireAuthoredPresetCall(oldCalls[index]); RequireAuthoredPresetCall(newCalls[index]);
                }
                if (oldCalls[index].Outcome != WmlStaticInvocationOutcome.Expanded || newCalls[index].Outcome != oldCalls[index].Outcome
                    || oldCalls[index].Definition is not { } oldDefinition || newCalls[index].Definition is not { } newDefinition
                    || !SameRange(oldDefinition, entry.DefinitionRange) || !SameRange(newDefinition, currentDefinition)
                    || !SameMappedOrigins(oldCalls[index].InvocationOrigins, newCalls[index].InvocationOrigins, change))
                    throw new InvalidOperationException("An existing preset consumer changes its exact invocation-time definition or source occurrence.");
            }
        }

        static void RequireAuthoredPresetCall(WmlStaticInvocationTrace call)
        {
            if (call.InvocationOrigins.Count != 1 || !call.InvocationOrigins[0].Source.IsAuthored)
                throw new InvalidOperationException("Shared preset edits require one exact authored invocation occurrence. Use RAW for generated or split calls.");
            var origin = call.InvocationOrigins[0]; var syntax = WmlMacroInvocationSyntaxScanner.Scan(origin.Text).ToArray();
            if (syntax.Length != 1 || syntax[0].Name != call.Name || syntax[0].Arguments.Count != 0
                || syntax[0].Span.StartOffset != 0 || syntax[0].Span.Length != origin.Length)
                throw new InvalidOperationException("A generated preset name has no exact authored invocation footprint. Use RAW for this caller.");
        }

        bool IsTypedOldCall(WmlStaticInvocationTrace call, WccCampaignLocalPresetEntry entry)
        {
            if (!ExactAuthoredCall(call, entry)) return false;
            return regions.Any(region => InsideProposedScalar(call.InvocationOrigins[0], region.Before)
                && (region.UnitInstance?.Entry?.Preset is { } selected && SameSelected(selected, entry)
                    || region.EventEdit?.PreviousGraphSeed?.Graph.Actions.Any(action => action.Unit?.Preset is { } preset && SameSelected(preset, entry)) == true
                    || region.EventEdit?.Kind is WccScenarioEventSourceEditKind.RemoveMacroInvocation or WccScenarioEventSourceEditKind.ReplaceMacroInvocation
                    || region.SideTopology is not null && IsExactRemovedSideTopologyRegion(region)))
                || IsTypedNestedEventUnit(call, entry, false);
        }

        bool IsTypedNewCall(WmlStaticInvocationTrace call, WccCampaignLocalPresetEntry entry)
        {
            if (!ExactAuthoredCall(call, entry) || call.Definition is not { } definition
                || !SameMappedRange(entry.DefinitionRange, definition, change)) return false;
            return regions.Any(region => InsideProposedScalar(call.InvocationOrigins[0], region.After)
                && ((region.LeaderAddition?.Leaders.Where(leader => leader.Preset is not null).Select(leader => leader.Preset!)
                    ?? region.SideTopology?.Additions.Where(side => side.Preset is not null).Select(side => side.Preset!)
                    ?? (region.UnitInstance?.Spec?.Preset is { } unitPreset ? new[] { unitPreset } :
                        region.EventEdit?.Preset is { } eventPreset ? new[] { eventPreset } : ExactReplacedEventPresets(region, change)))
                    .Concat(region.EventEdit?.GraphSeed?.Graph.Actions.Where(action => action.Unit?.Preset is not null).Select(action => action.Unit!.Preset!) ?? [])
                    .Any(selected => SameSelected(selected, entry))))
                || IsTypedNestedEventUnit(call, entry, true);
        }

        bool IsTypedNestedEventUnit(WmlStaticInvocationTrace call, WccCampaignLocalPresetEntry unit, bool after)
        {
            if (unit.Role != WccCampaignLocalPresetRole.Unit || call.ExpansionStack.Count != 1) return false;
            var frame = call.ExpansionStack[0];
            if (frame.RequiredParameters is not { Count: 0 } || frame.OptionalParameters is not { Count: 0 }
                || frame.InvocationOrigins.Count != 1) return false;
            foreach (var savedEvent in previous.Where(row => row.Role == WccCampaignLocalPresetRole.Event))
            {
                var mutation = edit.Mutations.SingleOrDefault(row => row.Target.Id == savedEvent.Id);
                if (after && mutation?.IsDeletion == true) continue;
                var current = after ? definitions.Single(row => row.Id == savedEvent.Id) : savedEvent;
                var payload = after ? mutation?.Replacement ?? savedEvent.Payload : savedEvent.Payload;
                if (frame.MacroName != current.Name || !SameRange(frame.Definition, current.DefinitionRange)
                    || frame.InvocationOrigins[0].Text != "{" + current.Name + "}"
                    || !InsideProposedScalar(call.InvocationOrigins[0], current.BodyRange)
                    || payload?.EventUnitPresets.Any(selected => SameSelected(selected, unit)) != true) continue;
                // Replacing this complete Event body accounts for its exact old
                // and new nested uses; activation review proves the whole graph.
                if (mutation is not null) return true;
                if (regions.Any(region => InsideProposedScalar(frame.InvocationOrigins[0], after ? region.After : region.Before)
                    && (after ? region.EventEdit?.Preset is { } selected && SameSelected(selected, savedEvent)
                            || ExactReplacedEventPresets(region, change).Any(selected => SameSelected(selected, savedEvent))
                        : region.EventEdit?.Kind is WccScenarioEventSourceEditKind.RemoveMacroInvocation or WccScenarioEventSourceEditKind.ReplaceMacroInvocation)))
                    return true;
            }
            if (after)
                foreach (var added in edit.Presets.Where(row => row.Payload.Role == WccCampaignLocalPresetRole.Event))
                {
                    var current = definitions.Single(row => row.Id == added.Id);
                    if (frame.MacroName == current.Name && SameRange(frame.Definition, current.DefinitionRange)
                        && frame.InvocationOrigins[0].Text == "{" + current.Name + "}"
                        && InsideProposedScalar(call.InvocationOrigins[0], current.BodyRange)
                        && added.Payload.EventUnitPresets.Any(selected => SameSelected(selected, unit))) return true;
                }
            return false;
        }
        static bool ExactAuthoredCall(WmlStaticInvocationTrace call, WccCampaignLocalPresetEntry entry) =>
            call.Outcome == WmlStaticInvocationOutcome.Expanded && call.InvocationOrigins.Count == 1
            && call.InvocationOrigins[0].Source.IsAuthored && call.InvocationOrigins[0].Text == "{" + entry.Name + "}";

        static bool SameSelected(WccCampaignLocalPresetEntry selected, WccCampaignLocalPresetEntry entry) =>
            selected.Id == entry.Id && selected.Name == entry.Name && selected.Role == entry.Role && selected.Scope == entry.Scope
            && SameRange(selected.DefinitionRange, entry.DefinitionRange) && SameRange(selected.BodyRange, entry.BodyRange);
        // Reuse the same actual-main activation resolver and bounded evaluator
        // as managed creation; unused entries still need a surviving-definition
        // proof and complete field/nested-leader payload check in every context.
        var probeText = new StringBuilder("{./_main.cfg}\n");
        foreach (var definition in definitions.Where(definition => context.CampaignDefine is null || definition.Scope.Targets(plan)))
        {
            var tag = WccCampaignLocalPresetCatalog.Kind(definition.Role);
            probeText.Append("[wcc_preset_probe]\nprobe_id=").Append(definition.Id.ToString("N")).Append("\n#ifdef ")
                .Append(definition.Name).Append("\nactive=yes\n");
            if (definition.Role != WccCampaignLocalPresetRole.Event) probeText.Append('[').Append(tag).Append("]\n");
            probeText.Append('{').Append(definition.Name).Append("}\n");
            if (definition.Role != WccCampaignLocalPresetRole.Event) probeText.Append("[/").Append(tag).Append("]\n");
            probeText.Append("#else\nactive=no\n#endif\n[/wcc_preset_probe]\n");
        }        var probe = new WmlStaticSource("wcc-preset-storage-activation:" + main.ContentHash + ":" + context.SemanticKey, probeText.ToString(), isAuthored: false);
        var resolver = new PresetActivationResolver(probe, candidateCapture.EntrySource!, candidateCapture.IncludeResolver);
        var undefined = plan.Difficulties.Select(row => row.Define).Concat(["EDITOR", "MULTIPLAYER"])
            .Where(symbol => !context.DefinedSymbols.Contains(symbol)).Distinct(StringComparer.Ordinal).ToArray();
        WmlStaticEvaluationResult evaluation;
        diagnostics?.Add(WccSourceSaveCount.PreprocessingEvaluations);
        using (diagnostics?.Measure(WccSourceSavePhase.Preprocessing))
            evaluation = new WmlStaticEvaluator().Evaluate(new(probe, context,
                new[] { environment.DefinitionsSource }.Concat(candidateCapture.PreludeSources).ToArray(), undefined, true, resolver,
                WmlStaticEvaluationLimits.Campaign), cancellationToken);
        diagnostics?.ObserveEvaluation(evaluation); diagnostics?.Add(WccSourceSaveCount.ScalarProjections);
        WmlStaticScalarProjection projection;
        using (diagnostics?.Measure(WccSourceSavePhase.ScalarProjection)) projection = WmlStaticScalarProjection.Create(evaluation, cancellationToken);
        diagnostics?.ObserveProjection(projection); RequireComplete(new(evaluation, projection));
        foreach (var definition in definitions.Where(definition => context.CampaignDefine is null || definition.Scope.Targets(plan)))
        {
            var wrappers = projection.Owners.Where(owner => owner.TagName == "wcc_preset_probe" && owner.Parent is null
                && owner.OpeningOrigin is { } opening && ReferenceEquals(opening.Source, probe)
                && WccScenarioFieldValueCodec.DecodeLiteral(owner.ResolveField("probe_id").ValueText) == definition.Id.ToString("N")).ToArray();
            if (wrappers.Length != 1 || wrappers[0].IsAmended || wrappers[0].HasUnresolvedContributions)
                throw new InvalidOperationException("A stored preset activation probe has no exact unique wrapper.");
            var wrapper = wrappers[0];
            var calls = evaluation.Invocations.Where(call => call.Name == definition.Name && call.InvocationOrigins.Any(origin => ReferenceEquals(origin.Source, probe))).ToArray();
            if (context.CampaignDefine is null)
            {
                if (wrapper.GetKnownValue("active") != "no" || wrapper.DirectChildren.Count != 0 || calls.Length != 0)
                    throw new InvalidOperationException("A gameplay preset becomes active in the campaign menu.");
                continue;
            }
            if (wrapper.GetKnownValue("active") != "yes" || wrapper.DirectChildren.Count != 1 || calls.Length != 1
                || calls[0].Outcome != WmlStaticInvocationOutcome.Expanded || !SameRange(calls[0].Definition, definition.DefinitionRange))
                throw new InvalidOperationException("A stored preset is not the actual uniquely surviving requested definition.");
            var payload = edit.Mutations.SingleOrDefault(row => row.Target.Id == definition.Id)?.Replacement
                ?? edit.Presets.SingleOrDefault(preset => preset.Id == definition.Id)?.Payload
                ?? previous.Single(preset => preset.Id == definition.Id).Payload!;
            if (payload.Role is WccCampaignLocalPresetRole.Unit or WccCampaignLocalPresetRole.Event)
            {
                var tag = WccCampaignLocalPresetCatalog.Kind(payload.Role);
                var fragment = payload.Role == WccCampaignLocalPresetRole.Event ? payload.EventText!
                    : "[unit]\n" + string.Concat(payload.EncodedValues.Select(pair => pair.Key + "=" + pair.Value + "\n"))
                        + (payload.UnitTemplate!.IsLoyal == true ? WccScenarioUnitInstancePlanner.RenderPresetLoyalty("", "\n") : "") + "[/unit]\n";
                if (payload.EventSeed is { } eventSeed && eventSeed.Graph.Actions.Any(action => action.Kind == WccScenarioEventActionKind.SpawnUnit))
                {
                    ValidateEventGraphUnits(wrapper.DirectChildren[0], eventSeed.Graph, definition.BodyRange, evaluation,
                        change, context, plan, Exact, projection);
                    // Reuse the same bounded literal-body substitution as initial
                    // units; expanded output is review evidence, never edit source.
                    foreach (var unitPreset in payload.EventUnitPresets)
                        fragment = fragment.Replace("{" + unitPreset.Name + "}", BindProposedPreset(unitPreset, change).Body.Text, StringComparison.Ordinal);
                }
                var literal = new WmlStaticSource("wcc-preset-requested-body", fragment, false);
                // Conditional Event bodies need the same declared context as the
                // activation probe. Captured graph payloads contain only literal
                // actions and whitelisted, balanced difficulty guards.
                var literalEvaluation = payload.EventSeed?.Graph.DifficultyRegions.Count > 0 || payload.EventUnitPresets.Count > 0
                    ? new WmlStaticEvaluator().Evaluate(new(literal, context, IsInitialEnvironmentComplete: true))
                    : new WmlStaticEvaluationResult(fragment, [new(0, fragment, new(literal, 0, fragment.Length), [])], [], [literal], [], true);
                if (!literalEvaluation.IsComplete || literalEvaluation.Diagnostics.Count != 0)
                    throw new InvalidOperationException("The requested Event body is incomplete in a declared difficulty context.");
                var requested = WmlStaticScalarProjection.Create(literalEvaluation);
                var root = requested.Owners.Single(owner => owner.Parent is null);
                WholeTree(wrapper.DirectChildren[0], root, payload.Role == WccCampaignLocalPresetRole.Event);
                continue;
                void WholeTree(WmlStaticScalarOwner actual, WmlStaticScalarOwner expected, bool boundariesFromBody)
                {
                    if (actual.TagName != expected.TagName || actual.IsAmended || actual.HasUnresolvedContributions
                        || actual.Fields.Count != expected.Fields.Count || actual.DirectChildren.Count != expected.DirectChildren.Count)
                        throw new InvalidOperationException("The stored unit/event preset differs from its whole requested payload.");
                    if (boundariesFromBody && (!Exact(actual.OpeningOrigin, actual.OpeningExpansionStack) || !Exact(actual.ClosingOrigin, actual.ClosingExpansionStack)))
                        throw new InvalidOperationException("A preset child tag has no complete exact selected-body provenance.");
                    foreach (var field in expected.Fields)
                    {
                        if (!actual.Fields.TryGetValue(field.Key, out var found) || !found.IsKnown || found.ValueText != field.Value.ValueText
                            || found.Contributions.Count != 1 || !Exact(found.Contributions[0].AssignmentOrigin, found.Contributions[0].ExpansionStack)
                            || found.Contributions[0].ValueOrigins.Count == 0 || found.Contributions[0].ValueOrigins.Any(origin => !InsideProposedScalar(origin, definition.BodyRange)))
                            throw new InvalidOperationException("A stored unit/event field lost its sole requested body contribution: " + field.Key);
                    }
                    for (var child = 0; child < expected.DirectChildren.Count; child++)
                    {
                        // Exact nested Unit frames and every local/inherited field
                        // were proved by the common Event graph review above.
                        if (payload.EventSeed is { } seed && seed.Graph.Actions.Any(action => action.Kind == WccScenarioEventActionKind.SpawnUnit)
                            && actual.TagName == "event" && actual.DirectChildren[child].TagName == "unit") continue;
                        WholeTree(actual.DirectChildren[child], expected.DirectChildren[child], true);
                    }
                }
                bool Exact(WmlStaticSourceRange? origin, IReadOnlyList<WmlStaticExpansionFrame> stack) => origin is not null
                    && InsideProposedScalar(origin, definition.BodyRange) && stack.Count == 1 && SameRange(stack[0].Definition, definition.DefinitionRange);
            }
            Fields(wrapper.DirectChildren[0], payload.Role, payload.Values, payload.DifficultyValues, payload.Leaders.Count);
            for (var index = 0; index < payload.Leaders.Count; index++)
            {
                var leader = wrapper.DirectChildren[0].DirectChildren[index];
                if (leader.OpeningOrigin is not { } opening || leader.ClosingOrigin is not { } closing
                    || !InsideProposedScalar(opening, definition.BodyRange) || !InsideProposedScalar(closing, definition.BodyRange)
                    || leader.OpeningExpansionStack.Count != 1 || leader.ClosingExpansionStack.Count != 1
                    || !SameRange(leader.OpeningExpansionStack[0].Definition, definition.DefinitionRange)
                    || !SameRange(leader.ClosingExpansionStack[0].Definition, definition.DefinitionRange))
                    throw new InvalidOperationException("A stored nested leader lacks its exact complete selected-body boundaries.");
                Fields(wrapper.DirectChildren[0].DirectChildren[index], WccCampaignLocalPresetRole.Leader,
                    payload.Leaders[index].Values, payload.Leaders[index].DifficultyValues, 0);
            }
            void Fields(WmlStaticScalarOwner owner, WccCampaignLocalPresetRole role, IReadOnlyDictionary<string, string> values,
                IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> slots, int children)
            {
                if (owner.TagName != (role == WccCampaignLocalPresetRole.Leader ? "leader" : "side") || owner.IsAmended
                    || owner.HasUnresolvedContributions || owner.Fields.Count != values.Count + slots.Count || owner.DirectChildren.Count != children)
                    throw new InvalidOperationException("A stored preset differs from its whole requested literal payload.");
                foreach (var pair in values.Concat(slots.Select(pair => new KeyValuePair<string, string>(pair.Key,
                             pair.Value.Single(slot => slot.ContextSemanticKey == context.SemanticKey).ValueText!))))
                {
                    if (!WccCampaignLocalPresetPayload.TryField(role, pair.Key, out var fieldDefinition) || !owner.Fields.TryGetValue(pair.Key, out var field)
                        || !field.IsKnown || field.Contributions.Count != 1 || !WccScenarioFieldValueCodec.Equivalent(fieldDefinition!, field.ValueText, pair.Value)
                        || field.Contributions[0].AssignmentOrigin is not { } assignment || !InsideProposedScalar(assignment, definition.BodyRange)
                        || field.Contributions[0].ValueOrigins.Count == 0 || field.Contributions[0].ValueOrigins.Any(origin => !InsideProposedScalar(origin, definition.BodyRange))
                        || field.Contributions[0].ExpansionStack.Count != 1 || !SameRange(field.Contributions[0].ExpansionStack[0].Definition, definition.DefinitionRange))
                        throw new InvalidOperationException("A stored preset field lacks its sole exact requested active-body contribution: " + pair.Key);
                }
            }
        }
    }

    private static PairedEvaluation MaskPresetMutationFields(PairedEvaluation original, PairedEvaluation masked,
        WccCampaignLocalPresetStorageEdit? edit, WccScenarioFieldSourceChange change, bool after)
    {
        if (edit is null || edit.Mutations.Count == 0) return masked;
        var selected = edit.Mutations.Where(row => !row.IsDeletion).Select(row =>
        {
            var definition = after ? change.MapRange(row.Target.DefinitionRange) : row.Target.DefinitionRange;
            if (definition is null) throw new InvalidOperationException("The typed preset replacement lost its exact definition mapping.");
            var body = after ? WccCampaignLocalPresetStoragePlanner.ReadMappedDefinition(row.Target, definition, edit.Snapshot)?.BodyRange
                : row.Target.BodyRange;
            if (body is null) throw new InvalidOperationException("The typed preset replacement lost its exact pure body.");
            return (row.Target.Name, row.Target.Role, Definition: definition, Body: body);
        }).ToArray();
        char[]? text = null;
        // Complete literal Unit/Event payloads may change child topology. Mask only
        // segments emitted by their exact selected definition; invocation selection,
        // source closure and requested whole payload were independently proved above.
        foreach (var segment in original.Evaluation.Segments)
        {
            var producers = selected.Where(row => row.Role is WccCampaignLocalPresetRole.Unit or WccCampaignLocalPresetRole.Event
                && segment.ExpansionStack.Any(frame => frame.MacroName == row.Name && SameRange(frame.Definition, row.Definition))).ToArray();
            if (producers.Length == 0) continue;
            if (segment.Text != segment.Origin.Text || !ExactUnitEventSegment(segment, producers.Select(row => row.Name).ToArray()))
                throw new InvalidOperationException("A unit/event preset consumer has mixed or indirect output provenance; use RAW.");
            text ??= masked.Evaluation.Text.ToCharArray();
            for (var offset = segment.OutputOffset; offset < segment.OutputOffset + segment.Text.Length; offset++)
                if (text[offset] is not ('\r' or '\n')) text[offset] = ' ';
        }
        bool ExactUnitEventSegment(WmlStaticOutputSegment segment, IReadOnlyList<string> producers)
        {
            var frames = segment.ExpansionStack;
            if (frames.Count is < 1 or > 2 || frames.Any(frame => frame.RequiredParameters is not { Count: 0 }
                    || frame.OptionalParameters is not { Count: 0 } || frame.InvocationOrigins.Count != 1
                    || !frame.InvocationOrigins[0].Source.IsAuthored || frame.InvocationOrigins[0].Text != "{" + frame.MacroName + "}")) return false;
            if (frames.Count == 1)
                return producers.Count == 1 && selected.Any(row => row.Name == frames[0].MacroName && row.Name == producers[0]
                    && SameRange(frames[0].Definition, row.Definition) && InsideProposedScalar(segment.Origin, row.Body));
            // One supported Event may invoke one saved Unit template. No other
            // indirect or generated ancestry is accepted for mutation masking.
            var inventory = edit.Snapshot.Entries.Where(entry => entry.IsSupported).ToArray();
            foreach (var eventEntry in inventory.Where(entry => entry.Role == WccCampaignLocalPresetRole.Event && entry.Name == frames[0].MacroName))
            {
                var eventDefinition = after ? BindProposedPreset(eventEntry, change).Definition : eventEntry.DefinitionRange;
                var eventBody = after ? BindProposedPreset(eventEntry, change).Body : eventEntry.BodyRange;
                var eventPayload = after ? BindProposedPreset(eventEntry, change).Payload : eventEntry.Payload!;
                if (!SameRange(frames[0].Definition, eventDefinition) || !InsideProposedScalar(frames[1].InvocationOrigins[0], eventBody)) continue;
                foreach (var unit in eventPayload.EventUnitPresets.Where(unit => unit.Name == frames[1].MacroName))
                {
                    var unitDefinition = after ? BindProposedPreset(unit, change).Definition : unit.DefinitionRange;
                    var unitBody = after ? BindProposedPreset(unit, change).Body : unit.BodyRange;
                    if (SameRange(frames[1].Definition, unitDefinition) && InsideProposedScalar(segment.Origin, unitBody)
                        && producers.All(name => name == eventEntry.Name || name == unit.Name)) return true;
                }
            }
            return false;
        }
        foreach (var contribution in original.Projection.Owners.SelectMany(owner => owner.Fields.Values).SelectMany(field => field.Contributions))
        {
            original.CancellationToken.ThrowIfCancellationRequested();
            var producer = selected.Where(row => row.Role is WccCampaignLocalPresetRole.Side or WccCampaignLocalPresetRole.Leader && contribution.ExpansionStack.Any(frame => frame.MacroName == row.Name
                && SameRange(frame.Definition, row.Definition))).ToArray();
            if (producer.Length == 0) continue;
            var start = contribution.AssignmentOutputOffset; var length = contribution.AssignmentOutputLength;
            if (producer.Length != 1 || start < 0 || length <= 0 || start > original.Evaluation.Text.Length - length
                || contribution.AssignmentOrigins.Count != 1 || !InsideProposedScalar(contribution.AssignmentOrigins[0], producer[0].Body)
                || contribution.AssignmentOrigins[0].Length != length
                || contribution.AssignmentOrigins[0].Text != original.Evaluation.Text.Substring(start, length))
                throw new InvalidOperationException("A preset consumer lacks its exact sole literal assignment footprint.");
            text ??= masked.Evaluation.Text.ToCharArray();
            for (var offset = start; offset < start + length; offset++) if (text[offset] is not ('\r' or '\n')) text[offset] = ' ';
        }
        if (text is null) return masked;
        var evaluation = masked.Evaluation.WithMaskedTextPreservingMappings(new string(text));
        var projection = WmlStaticScalarProjection.CreateMasked(evaluation, masked.Projection, original.CancellationToken, out _, out _);
        return new(evaluation, projection) { Diagnostics = masked.Diagnostics, CancellationToken = masked.CancellationToken };
    }
}
