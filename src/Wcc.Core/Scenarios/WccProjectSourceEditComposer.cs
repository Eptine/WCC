using System.Text;
using Wcc.Core.Wml;
using Wcc.Core.Campaigns;

namespace Wcc.Core.Scenarios;

internal sealed record WccProjectAuthorizedRegion(WmlStaticSourceRange Before,
    WmlStaticSourceRange After, string Kind, WccScenarioSideFlagPresetEdit? SideFlag = null,
    WccScenarioEventSourceEdit? EventEdit = null)
{
    public WccScenarioSideTopologyEdit? SideTopology { get; init; }
    public WccScenarioLeaderAdditionEdit? LeaderAddition { get; init; }
    public WccScenarioStoryEdit? StoryEdit { get; init; }
    public WccScenarioUnitInstanceEdit? UnitInstance { get; init; }
}

internal sealed record WccProjectComposedFile(WccProjectSourceEditRequest Request,
    WccScenarioFieldBatchCandidate Candidate, IReadOnlyList<WccProjectAuthorizedRegion> Regions);

internal static class WccProjectSourceEditComposer
{
    internal static WccScenarioInitialSettingsComposeResult ComposeInitialSettings(WccScenarioInitialSettingsEdit settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var file = Compose(new(settings.ScenarioRange.Source, initialSettings: settings));
            var scenario = file.Candidate.MapRange(settings.ScenarioRange)
                ?? throw new InvalidOperationException("The composed initial settings have no exact containing scenario range.");
            return new(true, scenario.Text);
        }
        catch (InvalidOperationException exception) { return new(false, Diagnostic: exception.Message); }
    }

    internal static WccProjectComposedFile Compose(WccProjectSourceEditRequest request)
    {
        var source = request.Source;
        WccScenarioFieldBatchCandidate? scalar = request.Fields.Count == 0 ? null
            : WccScenarioFieldBatchCandidatePlanner.Plan(request.Fields, source);
        WccScenarioDifficultyFieldPlanner.GroupPatchPlan? difficultyPlan = null;
        var parts = new List<(WccScenarioFieldSourcePatch Patch, string? Kind, WccScenarioSideFlagPresetEdit? SideFlag, WccScenarioEventSourceEdit? EventEdit)>();
        parts.AddRange((scalar?.Patches ?? []).Select(patch => (patch, (string?)null, (WccScenarioSideFlagPresetEdit?)null, (WccScenarioEventSourceEdit?)null)));
        if (request.StoryEdit is { } story)
        {
            if (!ReferenceEquals(story.Source, source))
                throw new InvalidOperationException("The story belongs to another exact authored source publication.");
            foreach (var patch in WccScenarioStoryPlanner.PlanPatches(story)) parts.Add((patch, "Story", null, null));
        }
        if (request.PresetStorage is { } storage)
        {
            if (!ReferenceEquals(storage.Source, source))
                throw new InvalidOperationException("Preset storage requires its exact main publication and one definition recipe.");
            foreach (var patch in WccCampaignLocalPresetStoragePlanner.PlanMainPatches(storage))
                parts.Add((patch, "PresetStorage", null, null));
        }
        if (request.SideTopology is { } topology)
        {
            if (!ReferenceEquals(topology.Source, source))
                throw new InvalidOperationException("Side topology belongs to another immutable source publication.");
            foreach (var patch in WccScenarioSideTopologyPlanner.PlanPatches(topology))
                parts.Add((patch, "SideTopology", null, null));
        }
        if (request.LeaderAdditions.Count > 256 || request.LeaderAdditions.Any(edit => edit is null) ||
            request.LeaderAdditions.Sum(edit => edit.Leaders.Count) > 256 ||
            request.LeaderAdditions.Sum(edit => edit.Leaders.Sum(leader => leader.EncodedSize)) > 2 * 1024 * 1024)
            throw new InvalidOperationException("The collected new-leader payload exceeds its bounded file scope.");
        for (var index = 0; index < request.LeaderAdditions.Count; index++)
        {
            var edit = request.LeaderAdditions[index];
            if (edit is null || !ReferenceEquals(edit.Source, source))
                throw new InvalidOperationException("New leaders belong to another immutable source publication.");
            if (request.LeaderAdditions.Take(index).Any(previous => edit.Snapshot.SameOwner(previous.Snapshot.SideOwner)))
                throw new InvalidOperationException("Keep one collected new-leader recipe per side.");
            if (request.SideTopology?.Removals.Any(entry => entry.SameOwner(edit.Snapshot.SideOwner)) == true)
                throw new InvalidOperationException("A removed side cannot also receive new leaders; reset one of these drafts.");
            parts.Add((WccScenarioLeaderAdditionPlanner.PlanPatch(edit), "LeaderAddition", null, null));
        }
        if (request.UnitInstances.Count > 256 || request.UnitInstances.Any(edit => edit is null || !ReferenceEquals(edit.Source, source)))
            throw new InvalidOperationException("Initial unit recipes need one exact source publication and bounded payload count.");
        var unitPayloadSize = 0L;
        for (var index = 0; index < request.UnitInstances.Count; index++)
        {
            var edit = request.UnitInstances[index];
            if (request.SideTopology?.Removals.Any(entry => entry.SameOwner(edit.Snapshot.SideOwner)) == true)
                throw new InvalidOperationException("A removed side cannot also receive or edit an initial unit.");
            if (edit.Entry is { } target && request.UnitInstances.Take(index).Any(previous => previous.Entry is { } earlier &&
                earlier.Range.StartOffset == target.Range.StartOffset && earlier.Range.Length == target.Range.Length))
                throw new InvalidOperationException("Keep one final recipe per exact initial unit target.");
            var patch = WccScenarioUnitInstancePlanner.PlanPatch(edit);
            unitPayloadSize += patch.ReplacementText.Length;
            if (unitPayloadSize > 2 * 1024 * 1024) throw new InvalidOperationException("The initial unit payload exceeds its bounded collected file size.");
            parts.Add((patch, "UnitInstance", null, null));
        }
        if (request.CampaignStructure is { } structure)
        {
            if (!ReferenceEquals(structure.Source, source))
                throw new InvalidOperationException("Campaign structure belongs to another immutable source publication.");
            foreach (var patch in WccCampaignStructurePlanner.PlanPatches(structure))
                parts.Add((new(patch.Range.StartOffset, patch.Range.Length, patch.Replacement), patch.Kind, null, null));
        }
        if (request.CampaignCredits is { } credits)
        {
            if (!ReferenceEquals(credits.Source, source))
                throw new InvalidOperationException("Campaign credits belong to another immutable source publication.");
            foreach (var patch in WccCampaignCreditsPlanner.PlanPatches(credits))
                parts.Add((new(patch.Range.StartOffset, patch.Range.Length, patch.Replacement), "CampaignCredits", null, null));
        }
        for (var index = 0; index < request.DifficultyFields.Count; index++)
        {
            var edit = request.DifficultyFields[index];
            if (edit is null || !ReferenceEquals(edit.Source, source))
                throw new InvalidOperationException("Difficulty field intent belongs to another immutable source publication.");
            if (request.DifficultyFields.Take(index).Any(previous => previous.Snapshot.Key == edit.Snapshot.Key
                    && edit.Snapshot.SameOwner(previous.Snapshot.Owner))
                || request.Fields.Any(field => field.Route.Key == edit.Snapshot.Key && edit.Snapshot.SameOwner(field.Route.Owner)))
                throw new InvalidOperationException("Keep one typed final intent for the converted field; absorb its scalar drafts before Save.");
        }
        if (request.DifficultyFields.Count != 0)
        {
            difficultyPlan = WccScenarioDifficultyFieldPlanner.PlanGroupedPatches(request.DifficultyFields, scalar);
            parts.RemoveAll(part => part.Kind is null && difficultyPlan.AbsorbedScalarPatches.Contains(part.Patch));
            foreach (var patch in difficultyPlan.Patches) parts.Add((patch, "DifficultyField", null, null));
        }
        if (request.InitialSettings is { } settings)
        {
            RequireRange(settings.ScenarioRange, source);
            var raw = settings.ScenarioRange.Text;
            if (WmlSingleBlockValidator.Validate(raw, "scenario") is { IsValid: false } invalid)
                throw new InvalidOperationException(invalid.Problem);
            if (settings.Music is { } music)
            {
                var projection = settings.CapturedContext is { } captured && captured.Matches(settings.ScenarioRange)
                    ? ScenarioInitialMusicEditor.Project(settings.ScenarioRange, captured.Projection, settings.Definitions)
                    : ScenarioInitialMusicEditor.Project(raw, settings.Definitions);
                var result = ScenarioInitialMusicEditor.Compose(projection, music.Tracks,
                    music.MakeFirstIntro, music.LetIntroFinish, music.Shuffle);
                if (!result.IsSuccess) throw new InvalidOperationException(result.Problem);
                AddParts(result.SourcePatches, settings.ScenarioRange.StartOffset, "Music");
            }
            if (settings.Schedule is { } schedule)
            {
                var projection = settings.CapturedContext is { } captured && captured.Matches(settings.ScenarioRange)
                    ? ScenarioInitialScheduleEditor.Project(settings.ScenarioRange, captured.Projection, settings.Definitions, settings.SourceTextDomain)
                    : ScenarioInitialScheduleEditor.Project(raw, settings.Definitions, settings.SourceTextDomain);
                var result = ScenarioInitialScheduleEditor.Compose(projection, schedule);
                if (!result.IsSuccess) throw new InvalidOperationException(result.Diagnostic);
                AddParts(result.SourcePatches, settings.ScenarioRange.StartOffset, "Schedule");
            }
            for (var index = 0; index < settings.SideFlags.Count; index++)
                if (settings.SideFlags.Take(index).Any(previous => SameFlagOwner(previous.SideOwner, settings.SideFlags[index].SideOwner)))
                    throw new InvalidOperationException("Multiple flag preset recipes target the same side, including an explicit unchanged choice. Keep one intended preset per side.");
            foreach (var flag in settings.SideFlags)
            {
                var plan = WccSideFlagPresetSourcePlanner.Plan(source, flag);
                if (!plan.IsSuccess || plan.Patch is not { } patch) throw new InvalidOperationException(plan.Diagnostic);
                if (flag.SideOwner.OpeningOrigin is not { } opening || opening.StartOffset < settings.ScenarioRange.StartOffset ||
                    flag.SideOwner.ClosingOrigin is not { } closing || closing.StartOffset + closing.Length >
                    settings.ScenarioRange.StartOffset + settings.ScenarioRange.Length)
                    throw new InvalidOperationException("A flag preset is outside its exact containing scenario.");
                if (request.Fields.Any(field => field.Route.Key is "flag" or "flag_icon" &&
                    SameFlagOwner(field.Route.Owner, flag.SideOwner)))
                    throw new InvalidOperationException("A flag preset and scalar flag/flag_icon draft target the same side. Revert one before Save; both drafts are retained.");
                parts.Add((patch, "SideFlag", flag, null));
            }
            for (var index = 0; index < settings.Events.Count; index++)
                if (settings.Events[index].Target is { } target && settings.Events.Take(index).Any(previous =>
                        previous.Target is { } earlier && WccScenarioFieldRouteAnalyzer.SameSource(target.Source, earlier.Source) &&
                        target.StartOffset == earlier.StartOffset && target.Length == earlier.Length))
                    throw new InvalidOperationException("Multiple event drafts target the same pristine fragment, including an unchanged replacement. Keep one intended operation.");
            foreach (var eventEdit in settings.Events)
            {
                var plan = WccScenarioEventSourcePlanner.Plan(settings.ScenarioRange, eventEdit);
                if (!plan.IsSuccess || plan.Patch is not { } patch) throw new InvalidOperationException(plan.Diagnostic);
                parts.Add((patch, "Event", null, eventEdit));
            }
        }
        foreach (var raw in request.RawPatches)
        {
            RequireRange(raw.Range, source);
            if (raw.ReplacementText is null) throw new InvalidOperationException("A RAW replacement is missing.");
            parts.Add((new(raw.Range.StartOffset, raw.Range.Length, raw.ReplacementText), "RAW", null, null));
        }
        parts.RemoveAll(part => source.Text.Substring(part.Patch.Start, part.Patch.Length) == part.Patch.ReplacementText);
        var groups = parts.GroupBy(part => (part.Patch.Start, part.Patch.Length))
            .OrderBy(group => group.Key.Start).ThenBy(group => group.Key.Length).ToArray();
        var patches = new List<WccScenarioFieldSourcePatch>();
        var regionSpecs = new List<(int BeforeStart, int BeforeLength, int AfterStart, int AfterLength, string Kind,
            WccScenarioSideFlagPresetEdit? SideFlag, WccScenarioEventSourceEdit? EventEdit)>();
        var builder = new StringBuilder();
        var position = 0;
        foreach (var group in groups)
        {
            if (group.Key.Start < position)
                throw new InvalidOperationException("Field, music, schedule or RAW drafts overlap the same source. The drafts were retained.");
            var items = group.ToArray();
            if (group.Key.Length != 0 && items.Any(item => item.Patch.ReplacementText != items[0].Patch.ReplacementText))
                throw new InvalidOperationException("Different drafts replace the same authored source range.");
            builder.Append(source.Text, position, group.Key.Start - position);
            var afterStart = builder.Length;
            var replacement = group.Key.Length == 0 ? string.Concat(items.Select(item => item.Patch.ReplacementText))
                : items[0].Patch.ReplacementText;
            patches.Add(new(group.Key.Start, group.Key.Length, replacement));
            var prefix = 0;
            foreach (var item in items)
            {
                if (item.Kind is { } kind)
                    regionSpecs.Add((group.Key.Start, group.Key.Length, afterStart + prefix,
                        item.Patch.ReplacementText.Length, kind, item.SideFlag, item.EventEdit));
                if (group.Key.Length == 0) prefix += item.Patch.ReplacementText.Length;
            }
            builder.Append(replacement);
            position = group.Key.Start + group.Key.Length;
        }
        builder.Append(source.Text, position, source.Text.Length - position);
        var candidate = new WmlStaticSource(source.Path, builder.ToString());
        if (WmlSingleBlockValidator.ValidateSourceStructure(candidate.Text) is { IsValid: false } malformed)
            throw new InvalidOperationException(malformed.Problem);
        if (source.Text == candidate.Text && request.ScenarioCreation is null && request.PresetStorage is null && request.UnitTypeCreation is null && request.UnitTypeUpdate is null && request.StoryEdit?.DefinitionCandidate is null)
            throw new InvalidOperationException("No authored source changes are pending.");
        var edits = (scalar?.Edits ?? []).Select(edit =>
        {
            if (difficultyPlan is not null && difficultyPlan.ScalarLocations.TryGetValue(edit, out var grouped))
            {
                var groupPatch = patches.Single(patch => patch.Start == grouped.PatchStart);
                var groupDelta = patches.TakeWhile(patch => !ReferenceEquals(patch, groupPatch))
                    .Sum(patch => patch.ReplacementText.Length - patch.Length);
                return edit with { CandidateSource = candidate, ProposedValueOrigin = new(candidate,
                    grouped.PatchStart + groupDelta + grouped.Offset, grouped.Length) };
            }
            if (edit.ProposedValueOrigin is not { } origin) return edit with { CandidateSource = candidate };
            var scalarPatch = scalar!.Patches.Single(patch => patch.Start == edit.ReplacementStart && patch.Length == edit.ReplacementLength);
            var scalarDelta = scalar.Patches.TakeWhile(patch => !ReferenceEquals(patch, scalarPatch))
                .Sum(patch => patch.ReplacementText.Length - patch.Length);
            var finalPatch = patches.Single(patch => patch.Start == scalarPatch.Start && patch.Length == scalarPatch.Length);
            var finalDelta = patches.TakeWhile(patch => !ReferenceEquals(patch, finalPatch))
                .Sum(patch => patch.ReplacementText.Length - patch.Length);
            return edit with { CandidateSource = candidate, ProposedValueOrigin = new(candidate,
                origin.StartOffset - scalarDelta + finalDelta, origin.Length) };
        }).ToArray();
        var batch = new WccScenarioFieldBatchCandidate(source, candidate, edits, patches);
        return new(request, batch, Array.AsReadOnly(regionSpecs.Select(region => new WccProjectAuthorizedRegion(
            new(source, region.BeforeStart, region.BeforeLength),
            new(candidate, region.AfterStart, region.AfterLength), region.Kind, region.SideFlag, region.EventEdit)
            {
                SideTopology = region.Kind == "SideTopology" ? request.SideTopology : null,
                LeaderAddition = region.Kind == "LeaderAddition" ? request.LeaderAdditions.Single(edit =>
                    edit.Snapshot.InsertionOffset == region.BeforeStart) : null,
                StoryEdit = region.Kind == "Story" ? request.StoryEdit : null,
                UnitInstance = region.Kind == "UnitInstance" ? request.UnitInstances.Single(edit =>
                    WccScenarioUnitInstancePlanner.PlanPatch(edit) is { } patch && patch.Start == region.BeforeStart &&
                    patch.Length == region.BeforeLength && patch.ReplacementText == candidate.Text.Substring(region.AfterStart, region.AfterLength)) : null
            }).ToArray()));

        void AddParts(IEnumerable<WccScenarioFieldSourcePatch> local, int offset, string kind) =>
            parts.AddRange(local.Select(patch => (patch with { Start = patch.Start + offset }, (string?)kind,
                (WccScenarioSideFlagPresetEdit?)null, (WccScenarioEventSourceEdit?)null)));
    }

    private static bool SameFlagOwner(WmlStaticScalarOwner left, WmlStaticScalarOwner right) =>
        left.TagName == "side" && right.TagName == "side" && left.OpeningOrigin is { } a && right.OpeningOrigin is { } b &&
        WccScenarioFieldRouteAnalyzer.SameSource(a.Source, b.Source) && a.StartOffset == b.StartOffset && a.Length == b.Length;

    private static void RequireRange(WmlStaticSourceRange range, WmlStaticSource source)
    {
        if (!WccScenarioFieldRouteAnalyzer.SameSource(range.Source, source) || range.StartOffset < 0
            || range.Length < 0 || range.StartOffset > source.Text.Length - range.Length)
            throw new InvalidOperationException("A draft range does not belong to its exact pristine authored source.");
    }
}
