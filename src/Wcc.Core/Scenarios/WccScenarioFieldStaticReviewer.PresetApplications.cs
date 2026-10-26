using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static IEnumerable<WccCampaignLocalPresetEntry> AppliedPresets(WccProjectSourceEditRequest request) =>
        (request.SideTopology?.Additions.Where(row => row.Preset is not null).Select(row => row.Preset!) ?? [])
        .Concat(request.LeaderAdditions.SelectMany(edit => edit.Leaders).Where(row => row.Preset is not null).Select(row => row.Preset!))
        .Concat(request.UnitInstances.SelectMany(edit => new[] { edit.Spec?.Preset, edit.Entry?.Preset }).OfType<WccCampaignLocalPresetEntry>())
        .Concat((request.InitialSettings?.Events ?? []).Where(edit => edit.Preset is not null).Select(edit => edit.Preset!))
        .Concat((request.InitialSettings?.Events ?? []).SelectMany(edit =>
            (edit.GraphSeed?.Graph.Actions ?? []).Concat(edit.PreviousGraphSeed?.Graph.Actions ?? [])
                .Where(action => action.Unit?.Preset is not null).Select(action => action.Unit!.Preset!)
                .Concat(edit.Preset?.Payload?.EventUnitPresets ?? [])));

    private static void ValidatePresetApplicationBindings(WccProjectSourceEditRequest request,
        WccScenarioFieldEditSessionState state, WmlCampaignEvaluationPlan? plan, WccStaticSourceCaptureResult capture)
    {
        var selected = AppliedPresets(request).ToArray(); if (selected.Length == 0) return;
        if (state.ReviewScope != WccScenarioFieldReviewScope.Scenario || plan is null)
            throw new InvalidOperationException("Preset instances require the current scenario and all fixed campaign contexts.");
        var catalog = WccCampaignLocalPresetCatalog.Build(capture, plan);
        foreach (var entry in selected)
        {
            var matches = catalog.Entries.Where(current => current.Id == entry.Id && current.Name == entry.Name && current.Scope == entry.Scope).ToArray();
            if (matches.Length != 1 || !matches[0].IsSupported || !matches[0].IsAvailableIn(plan) || matches[0].Id != entry.Id || matches[0].Name != entry.Name || matches[0].Role != entry.Role
                || !SameRange(matches[0].DefinitionRange, entry.DefinitionRange) || !SameRange(matches[0].BodyRange, entry.BodyRange)
                || matches[0].DefinitionRange.Text != entry.DefinitionRange.Text)
                throw new InvalidOperationException("The selected preset is no longer one exact supported saved definition in this campaign capture.");
        }
    }

    private static WccScenarioFieldSourceChange.PresetBinding BindProposedPreset(WccCampaignLocalPresetEntry entry,
        WccScenarioFieldSourceChange change)
    {
        if (change.PresetBindings.TryGetValue(entry, out var cached)) return cached;
        var mapped = change.MapRange(entry.DefinitionRange);
        if (mapped is null) throw new InvalidOperationException("The selected shared preset lost its exact candidate mapping.");
        var payload = entry.Payload!;
        WmlStaticSourceRange? body;
        var mutation = change.PresetStorage?.Mutations.SingleOrDefault(row => row.Target.Id == entry.Id);
        if (mutation is not null)
        {
            var target = mutation.Target;
            if (mutation.IsDeletion || target.Name != entry.Name || target.Role != entry.Role || target.Scope != entry.Scope
                || !SameRange(target.DefinitionRange, entry.DefinitionRange) || !SameRange(target.BodyRange, entry.BodyRange)
                || mapped.Text != WccCampaignLocalPresetStoragePlanner.RenderMutation(mutation, change.PresetStorage!.Snapshot.Main.Plan))
                throw new InvalidOperationException("The selected preset does not match its exact collected shared replacement.");
            var current = WccCampaignLocalPresetStoragePlanner.ReadMappedDefinition(entry, mapped, change.PresetStorage!.Snapshot);
            if (current is not { IsSupported: true } || current.Id != entry.Id || current.Name != entry.Name
                || current.Role != entry.Role || current.Scope != entry.Scope || current.Definition.TextDomain != entry.Definition.TextDomain)
                throw new InvalidOperationException("The collected shared replacement lost its supported identity, scope or translation domain.");
            payload = current.Payload!; body = current.BodyRange;
        }
        else
        {
            body = change.MapRange(entry.BodyRange);
            if (mapped.Text != entry.DefinitionRange.Text || body is null || body.Text != entry.BodyRange.Text)
                throw new InvalidOperationException("The selected shared preset changed outside its exact collected replacement.");
        }
        var leading = 0; while (leading < mapped.Length && mapped.Text[leading] is ' ' or '\t') leading++;
        var binding = new WccScenarioFieldSourceChange.PresetBinding(payload,
            new(mapped.Source, mapped.StartOffset + leading, mapped.Length - leading), body!);
        if (change.PresetBindings.Count < 256) change.PresetBindings.Add(entry, binding);
        return binding;
    }

    private static WmlStaticSourceRange MappedPresetDefinition(WccCampaignLocalPresetEntry entry, WccScenarioFieldSourceChange change) =>
        BindProposedPreset(entry, change).Definition;
    private static bool IsExactExistingUnitPresetFrame(WccProjectAuthorizedRegion region, WmlStaticExpansionFrame frame)
    {
        var preset = region.UnitInstance?.Entry?.Preset;
        if (preset is not { IsSupported: true, Role: WccCampaignLocalPresetRole.Unit }) return false;
        var definition = preset.DefinitionRange; var leading = 0;
        while (leading < definition.Length && definition.Text[leading] is ' ' or '\t') leading++;
        var exact = new WmlStaticSourceRange(definition.Source, definition.StartOffset + leading, definition.Length - leading);
        return frame.RequiredParameters is { Count: 0 } && frame.OptionalParameters is { Count: 0 } &&
            frame.MacroName == preset.Name && SameRange(frame.Definition, exact) &&
            frame.InvocationOrigins.Count == 1 && frame.InvocationOrigins[0].Text == "{" + preset.Name + "}" &&
            InsideProposedScalar(frame.InvocationOrigins[0], region.Before);
    }

    private static bool IsExactPresetApplicationFrame(WccProjectAuthorizedRegion region, WmlStaticExpansionFrame frame,
        WccScenarioFieldSourceChange change)
    {
        var entries = region.SideTopology?.Additions.Where(row => row.Preset is not null).Select(row => row.Preset!)
            ?? region.LeaderAddition?.Leaders.Where(row => row.Preset is not null).Select(row => row.Preset!)
            ?? (region.UnitInstance?.Spec?.Preset is { } unitPreset ? new[] { unitPreset } :
                region.EventEdit?.Preset is { } eventPreset ? new[] { eventPreset } :
                ExactReplacedEventPresets(region, change));
        if (frame.RequiredParameters is not { Count: 0 } || frame.OptionalParameters is not { Count: 0 }
            || frame.InvocationOrigins.Count != 1 || !frame.InvocationOrigins[0].Source.IsAuthored) return false;
        var selected = entries.ToArray();
        if (InsideProposedScalar(frame.InvocationOrigins[0], region.After) &&
            (selected.Any(entry => frame.MacroName == entry.Name && frame.InvocationOrigins[0].Text == "{" + entry.Name + "}"
                && SameRange(frame.Definition, MappedPresetDefinition(entry, change)))
             || IsExactGraphUnitPresetApplicationFrame(region, frame, change))) return true;
        if (region.Kind != "Event" || region.EventEdit is not
            { Kind: WccScenarioEventSourceEditKind.AddMacroInvocation or WccScenarioEventSourceEditKind.ReplaceMacroInvocation } eventEdit) return false;
        foreach (var eventEntry in selected.Where(entry => entry is
                     { IsSupported: true, Role: WccCampaignLocalPresetRole.Event }))
        {
            if (region.After.Text.Trim() != "{" + eventEntry.Name + "}" ||
                eventEdit.EventText?.Trim() != "{" + eventEntry.Name + "}") continue;
            var eventBinding = BindProposedPreset(eventEntry, change);
            if (!InsideProposedScalar(frame.InvocationOrigins[0], eventBinding.Body)) continue;
            foreach (var unit in eventBinding.Payload.EventUnitPresets.Where(entry => entry is
                         { IsSupported: true, Role: WccCampaignLocalPresetRole.Unit }))
                if (frame.MacroName == unit.Name && frame.InvocationOrigins[0].Text == "{" + unit.Name + "}" &&
                    SameRange(frame.Definition, MappedPresetDefinition(unit, change))) return true;
        }
        return false;
    }

    private static bool IsExactGraphUnitPresetApplicationFrame(WccProjectAuthorizedRegion region,
        WmlStaticExpansionFrame frame, WccScenarioFieldSourceChange change)
    {
        if (region.Kind != "Event" ||
            region.EventEdit is not { GraphSeed: { } seed, EventText: { } text } edit ||
            edit.Kind is not (WccScenarioEventSourceEditKind.AddLiteral or WccScenarioEventSourceEditKind.ReplaceLiteral) ||
            seed.OriginalText != text ||
            region.After.Text.TrimEnd('\r', '\n') != seed.OriginalText.TrimEnd('\r', '\n') ||
            frame.InvocationOrigins.Count != 1 || !frame.InvocationOrigins[0].Source.IsAuthored)
            return false;
        var invocation = frame.InvocationOrigins[0];
        foreach (var action in seed.Graph.Actions)
        {
            if (action.Kind != WccScenarioEventActionKind.SpawnUnit ||
                action.Unit?.Preset is not { IsSupported: true, Role: WccCampaignLocalPresetRole.Unit } preset ||
                frame.MacroName != preset.Name || invocation.Text != "{" + preset.Name + "}" ||
                !seed.MessageSources.TryGetValue(action.Key, out var node)) continue;
            // Bind this exact captured action slot, rather than every call to the
            // selected name somewhere in an authorized event. The later common
            // graph review proves the active wrapper and every contribution.
            var calls = WmlEditorAnalysis.Build(seed.OriginalText[node.Start..node.End]).Tokens
                .Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation).ToArray();
            if (calls.Length != 1) continue;
            var expected = new WmlStaticSourceRange(region.After.Source,
                region.After.StartOffset + node.Start + calls[0].StartOffset, calls[0].Length);
            if (SameRange(invocation, expected) && SameRange(frame.Definition, MappedPresetDefinition(preset, change)))
                return true;
        }
        return false;
    }
    private static IEnumerable<WccCampaignLocalPresetEntry> ExactReplacedEventPresets(
        WccProjectAuthorizedRegion region, WccScenarioFieldSourceChange change) =>
        region.EventEdit is { Kind: WccScenarioEventSourceEditKind.ReplaceMacroInvocation, EventText: { } text }
            ? change.PresetStorage?.Snapshot.Entries.Where(entry => entry.Role == WccCampaignLocalPresetRole.Event
                && entry.IsSupported && text.Trim() == "{" + entry.Name + "}") ?? [] : [];

    private static void ValidateAppliedPresetOwner(WmlStaticScalarOwner owner, WccCampaignLocalPresetEntry entry,
        IReadOnlyDictionary<string, string> overrides, WmlEvaluationContext context, WmlStaticSourceRange insertion,
        WmlStaticEvaluationResult evaluation, WccScenarioFieldSourceChange change)
    {
        if (!entry.IsSupported || entry.Payload is not { } payload || owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing
            || !InsideProposedScalar(opening, insertion) || !InsideProposedScalar(closing, insertion))
            throw new InvalidOperationException("A preset instance has no exact new literal wrapper.");
        var wrapper = new WmlStaticSourceRange(opening.Source, opening.StartOffset, closing.StartOffset + closing.Length - opening.StartOffset);
        var binding = BindProposedPreset(entry, change);
        payload = binding.Payload;
        var definition = binding.Definition;
        var body = binding.Body;
        var calls = evaluation.Invocations.Where(call => call.Name == entry.Name && call.InvocationOrigins.Count != 0
            && call.InvocationOrigins.All(origin => InsideProposedScalar(origin, wrapper))).ToArray();
        if (calls.Length != 1 || calls[0].Outcome != WmlStaticInvocationOutcome.Expanded || !SameRange(calls[0].Definition, definition)
            || calls[0].InvocationOrigins.Count != 1 || calls[0].InvocationOrigins[0].Text != "{" + entry.Name + "}")
            throw new InvalidOperationException("The instance does not invoke its exact selected preset occurrence in every declared difficulty.");
        var invocation = calls[0].InvocationOrigins[0];
        ValidateFields(owner, payload.Role, PresetValues(payload.Values, payload.DifficultyValues, context), overrides, payload.Leaders.Count);
        for (var index = 0; index < payload.Leaders.Count; index++)
        {
            var child = owner.DirectChildren[index]; var expected = payload.Leaders[index];
            if (child.OpeningOrigin is not { } start || child.ClosingOrigin is not { } end || !InsideProposedScalar(start, body) || !InsideProposedScalar(end, body)
                || !PresetFrames(child.OpeningExpansionStack) || !PresetFrames(child.ClosingExpansionStack))
                throw new InvalidOperationException("A side preset leader is outside its exact selected macro body.");
            ValidateFields(child, WccCampaignLocalPresetRole.Leader, PresetValues(expected.Values, expected.DifficultyValues, context),
                new Dictionary<string, string>(), 0);
        }

        bool PresetFrames(IReadOnlyList<WmlStaticExpansionFrame> frames)
        {
            var tail = frames.Where(frame => frame.InvocationOrigins.Any(origin => InsideProposedScalar(origin, wrapper))).ToArray();
            return tail.Length == 1 && tail[0].MacroName == entry.Name && SameRange(tail[0].Definition, definition)
                && tail[0].RequiredParameters is { Count: 0 } && tail[0].OptionalParameters is { Count: 0 }
                && tail[0].InvocationOrigins.Count == 1 && SameRange(tail[0].InvocationOrigins[0], invocation);
        }
        void ValidateFields(WmlStaticScalarOwner target, WccCampaignLocalPresetRole role,
            IReadOnlyDictionary<string, string> presetValues, IReadOnlyDictionary<string, string> local, int childCount)
        {
            var keys = presetValues.Keys.Concat(local.Keys).Distinct(StringComparer.Ordinal).ToArray();
            if (target.TagName != (role == WccCampaignLocalPresetRole.Leader ? "leader" : "side") || target.IsAmended || target.HasUnresolvedContributions
                || target.DirectChildren.Count != childCount || target.Fields.Count != keys.Length)
                throw new InvalidOperationException("A preset instance differs from its whole requested field and ordered leader footprint.");
            foreach (var key in keys)
            {
                if (!WccScenarioFieldValueCodec.TryGetDefinition(target.TagName, key, out var fieldDefinition) || !target.Fields.TryGetValue(key, out var field)
                    || !field.IsKnown || !WccScenarioFieldValueCodec.Equivalent(fieldDefinition, field.ValueText, local.GetValueOrDefault(key) ?? presetValues[key])
                    || field.Contributions.Count != (presetValues.ContainsKey(key) ? 1 : 0) + (local.ContainsKey(key) ? 1 : 0))
                    throw new InvalidOperationException("A preset instance field does not resolve with its requested ordered overrides: " + key);
                var index = 0;
                if (presetValues.ContainsKey(key))
                {
                    var contribution = field.Contributions[index++];
                    if (contribution.AssignmentOrigin is not { } assignment || !InsideProposedScalar(assignment, body) || contribution.AssignmentOrigins.Count != 1
                        || contribution.CompositeAssignmentKeys.Count != 0 || !contribution.ValueMappingIsExact || !PresetFrames(contribution.ExpansionStack)
                        || !PresetFrames(contribution.ValueExpansionStack) || contribution.ValueOrigins.Count != 1
                        || !InsideProposedScalar(contribution.ValueOrigins[0], body)
                        || !WccScenarioFieldValueCodec.Equivalent(fieldDefinition, contribution.ValueOrigins[0].Text, presetValues[key]))
                        throw new InvalidOperationException("A preset field lacks its exact selected body contribution: " + key);
                }
                if (local.TryGetValue(key, out var requested))
                {
                    var contribution = field.Contributions[index];
                    var encoded = WccScenarioFieldValueCodec.EncodeLiteral(fieldDefinition, requested);
                    if (contribution.AssignmentOrigin is not { } assignment || !InsideProposedScalar(assignment, wrapper)
                        || !IsProjectLiteralOrigin(assignment, contribution.ExpansionStack, wrapper) || contribution.AssignmentOrigins.Count != 1
                        || contribution.CompositeAssignmentKeys.Count != 0 || !contribution.ValueMappingIsExact || contribution.ValueOrigins.Count != 1
                        || !InsideProposedScalar(contribution.ValueOrigins[0], wrapper) || contribution.ValueOrigins[0].Text != encoded
                        || presetValues.ContainsKey(key) && contribution.AssignmentOutputOffset <= field.Contributions[0].AssignmentOutputOffset)
                        throw new InvalidOperationException("A preset override lacks its exact later local literal contribution: " + key);
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, string> PresetValues(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> separate, WmlEvaluationContext context) =>
        values.Concat(separate.Select(pair => new KeyValuePair<string, string>(pair.Key,
            pair.Value.Single(slot => slot.Define == context.DifficultyDefine).ValueText!))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static void ValidatePresetApplicationIdentities(WccProjectComposedFile file, WmlStaticScalarOwner scenario)
    {
        if (!AppliedPresets(file.Request).Any(entry => entry.Role != WccCampaignLocalPresetRole.Event)) return;
        var regions = file.Regions.Where(region => region.Kind == "SideTopology" && region.Before.Length == 0 || region.Kind == "LeaderAddition").ToArray();
        bool New(WmlStaticScalarOwner owner) => regions.Any(region => owner.OpeningOrigin is { } opening && InsideProposedScalar(opening, region.After)
            || owner.OpeningExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin => InsideProposedScalar(origin, region.After))));
        var units = new List<(WmlStaticScalarOwner Owner, string Id)>(); var saves = new List<(WmlStaticScalarOwner Owner, string Id, bool IsNew)>();
        foreach (var side in scenario.DirectChildren.Where(child => child.TagName == "side"))
        {
            var leaders = side.DirectChildren.Where(child => child.TagName == "leader").ToArray();
            var legacy = Literal(side, "no_leader") is not ("yes" or "true") && Literal(side, "type") is { Length: > 0 } type && type != "null";
            if (legacy && Literal(side, "id") is { Length: > 0 } legacyId) units.Add((side, legacyId));
            foreach (var child in side.DirectChildren.Where(child => child.TagName is "leader" or "unit"))
                if (Literal(child, "id") is { Length: > 0 } id) units.Add((child, id));
            // saved_game::set_defaults uses nonempty save_id, then the direct
            // side id, then the first nested leader id before legacy conversion.
            var explicitSave = Literal(side, "save_id"); var sideId = Literal(side, "id");
            var save = !string.IsNullOrEmpty(explicitSave) ? explicitSave : !string.IsNullOrEmpty(sideId) ? sideId
                : leaders.FirstOrDefault() is { } first ? Literal(first, "id") : null;
            if (!string.IsNullOrEmpty(save)) saves.Add((side, save, New(side) || string.IsNullOrEmpty(explicitSave) && string.IsNullOrEmpty(sideId)
                && leaders.FirstOrDefault() is { } firstLeader && New(firstLeader)));
        }
        foreach (var unit in scenario.DirectChildren.Where(child => child.TagName == "unit"))
            if (Literal(unit, "id") is { Length: > 0 } id) units.Add((unit, id));
        if (units.GroupBy(row => row.Id, StringComparer.Ordinal).Any(group => group.Count() > 1 && group.Any(row => New(row.Owner))))
            throw new InvalidOperationException("A new preset leader duplicates an initial unit id in this scenario. Use a deliberate distinct local id override or another preset.");
        if (saves.GroupBy(row => row.Id, StringComparer.Ordinal).Any(group => group.Count() > 1 && group.Any(row => row.IsNew)))
            throw new InvalidOperationException("A new preset side duplicates an effective carryover save_id in this scenario. Use a distinct local save_id override.");

        static string? Literal(WmlStaticScalarOwner owner, string key)
        {
            var field = owner.ResolveField(key);
            if (field.State == WmlStaticScalarResolutionState.ProvenAbsent) return null;
            if (field.State != WmlStaticScalarResolutionState.Known || field.ValueText is null || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(field.ValueText))
                throw new InvalidOperationException("Initial unit/carryover identity needs runtime context; preset application cannot prove that its ids remain distinct.");
            return WccScenarioFieldValueCodec.DecodeLiteral(field.ValueText);
        }
    }
}
