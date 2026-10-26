using System.Text;
using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    // The draft's captured projection is only a planning input. Every selected
    // gameplay context rebuilds both the original and candidate recipes here.
    private static IReadOnlyList<(WmlStaticSourceRange Before, WmlStaticSourceRange After)>
        ValidateCapturedInitialSettings(WccProjectComposedFile file, PairedEvaluation before, PairedEvaluation after,
            WmlEvaluationContext context, WccScenarioFieldSourceChange change,
            IReadOnlyDictionary<WmlMacroExpansionDefinition, WmlStaticSourceRange> definitionRanges)
    {
        var settings = file.Request.InitialSettings;
        if (settings?.CapturedContext is null || context.CampaignDefine is null) return [];
        var original = settings.ScenarioRange;
        var candidate = file.Candidate.MapRange(original)
            ?? throw new InvalidOperationException("The initial settings lost their exact candidate scenario range.");
        var candidateDefinitions = MapCapturedSettingsDefinitions(settings.Definitions, definitionRanges, change);
        var preserved = new List<(WmlStaticSourceRange Before, WmlStaticSourceRange After)>();
        if (settings.Music is { } music)
        {
            var oldMusic = ScenarioInitialMusicEditor.Project(original, before.Projection, settings.Definitions);
            var newMusic = ScenarioInitialMusicEditor.Project(candidate, after.Projection, candidateDefinitions);
            if (!oldMusic.CanEdit || !newMusic.CanEdit)
                throw new InvalidOperationException(oldMusic.Problem ?? newMusic.Problem ?? "The captured playlist has no exact source recipe.");
            if (!newMusic.Tracks.Select(track => track.Name).SequenceEqual(music.Tracks.Select(track => track.Name), StringComparer.Ordinal) ||
                newMusic.HasIntro != music.MakeFirstIntro || newMusic.LetIntroFinish != music.LetIntroFinish ||
                music.Shuffle is { } shuffle && newMusic.Shuffle != shuffle)
                throw new InvalidOperationException("The candidate initial playlist differs from its requested tracks or playback intent.");
            preserved.AddRange(ValidatePreservedMusicCalls(oldMusic, newMusic, change));
        }
        if (settings.Schedule is { } schedule)
        {
            var oldSchedule = ScenarioInitialScheduleEditor.Project(original, before.Projection, settings.Definitions, settings.SourceTextDomain);
            var newSchedule = ScenarioInitialScheduleEditor.Project(candidate, after.Projection, candidateDefinitions, settings.SourceTextDomain);
            if (!oldSchedule.CanCompose || !newSchedule.CanCompose)
                throw new InvalidOperationException(oldSchedule.Diagnostic ?? newSchedule.Diagnostic ?? "The captured schedule has no exact source recipe.");
            var intended = ScenarioInitialScheduleEditor.Compose(oldSchedule, schedule);
            if (!intended.IsSuccess || intended.Preview is not { } preview)
                throw new InvalidOperationException(intended.Diagnostic ?? "The intended schedule cannot be reconstructed from its original source.");
            if (!preview.Times.Select(CapturedTimeShape).SequenceEqual(newSchedule.Times.Select(CapturedTimeShape), StringComparer.Ordinal) ||
                preview.StartIsKnown != newSchedule.StartIsKnown || preview.StartIndex != newSchedule.StartIndex ||
                preview.IsRandomStart != newSchedule.IsRandomStart || !preview.RandomStartIndices.SequenceEqual(newSchedule.RandomStartIndices) ||
                !SameCapturedStartChoice(preview.ResolvedStartChoice, newSchedule.ResolvedStartChoice))
                throw new InvalidOperationException("The candidate initial times or starting choice differ from the requested schedule.");
            if (schedule.Kind == ScenarioInitialScheduleEditKind.Preset &&
                newSchedule.PresetInvocation?.Trim() != schedule.PresetInvocation?.Trim())
                throw new InvalidOperationException("The candidate schedule does not retain the requested exact preset invocation.");
        }
        return preserved;
    }

    private static IReadOnlyList<WmlMacroExpansionDefinition> MapCapturedSettingsDefinitions(
        IReadOnlyList<WmlMacroExpansionDefinition> definitions,
        IReadOnlyDictionary<WmlMacroExpansionDefinition, WmlStaticSourceRange> verifiedRanges,
        WccScenarioFieldSourceChange change)
    {
        var result = new List<WmlMacroExpansionDefinition>(definitions.Count);
        foreach (var definition in definitions)
        {
            if (!verifiedRanges.TryGetValue(definition, out var original))
                throw new InvalidOperationException("A captured initial-settings definition has no verified physical source range.");
            var mapped = change.MapRange(original);
            if (mapped is null || mapped.Text != original.Text || !PathComparer.Equals(mapped.Source.Path, original.Source.Path))
                throw new InvalidOperationException("An initial-settings definition changed instead of retaining its exact mapped bytes.");
            var line = 0;
            for (var index = 0; index < mapped.StartOffset; index++)
                if (mapped.Source.Text[index] == '\n' || mapped.Source.Text[index] == '\r' &&
                    (index + 1 >= mapped.Source.Text.Length || mapped.Source.Text[index + 1] != '\n')) line++;
            var domain = WmlTextDomainCapture.BeforeOffset(mapped.Source.Text, mapped.StartOffset);
            // The original declaration was already scanned/authenticated and its
            // entire mapped bytes are unchanged. Candidate Project still matches
            // this updated line/path to the exact active definition frame.
            if (domain != definition.TextDomain)
                throw new InvalidOperationException("The mapped initial-settings definition changed its translation domain.");
            result.Add(definition with { SourcePath = mapped.Source.Path, SourceStartLine = line, TextDomain = domain });
        }
        return result;
    }

    private static IReadOnlyList<(WmlStaticSourceRange Before, WmlStaticSourceRange After)> ValidatePreservedMusicCalls(
        ScenarioInitialMusicProjection before, ScenarioInitialMusicProjection after, WccScenarioFieldSourceChange change)
    {
        if (before.PreservedMusicEntries.Count != after.PreservedMusicEntries.Count ||
            !ScenarioInitialMusicEditor.HasVerifiedPreservedMusicEntries(before) ||
            !ScenarioInitialMusicEditor.HasVerifiedPreservedMusicEntries(after))
            throw new InvalidOperationException("Unchanged music plumbing no longer targets the exact initial playback owner.");
        var result = new List<(WmlStaticSourceRange Before, WmlStaticSourceRange After)>();
        foreach (var entry in before.PreservedMusicEntries)
        {
            var oldContext = before.CapturedContext!;
            var newContext = after.CapturedContext!;
            var range = new WmlStaticSourceRange(oldContext.Range.Source,
                oldContext.Range.StartOffset + entry.StartOffset, entry.EndOffsetExclusive - entry.StartOffset);
            var mapped = change.MapRange(range);
            var matches = after.PreservedMusicEntries.Where(candidate => mapped is not null &&
                candidate.StartOffset + newContext.Range.StartOffset == mapped.StartOffset &&
                candidate.EndOffsetExclusive - candidate.StartOffset == mapped.Length && candidate.Text == entry.Text).Take(2).ToArray();
            if (mapped is null || mapped.Text != range.Text || matches.Length != 1 ||
                !ScenarioInitialSettingsCapturedContext.SameSource(mapped.Source, newContext.Range.Source) ||
                oldContext.Capture(entry)?.Invocation is not { Definition: { } oldDefinition } oldCall ||
                newContext.Capture(matches[0])?.Invocation is not { Definition: { } newDefinition } newCall ||
                oldCall.Name != newCall.Name || oldCall.Outcome != newCall.Outcome ||
                !SameMappedRange(oldDefinition, newDefinition, change) ||
                !SameMappedOrigins(oldCall.InvocationOrigins, newCall.InvocationOrigins, change) ||
                !SameMappedFrames(oldCall.ExpansionStack, newCall.ExpansionStack, change))
                throw new InvalidOperationException("Unchanged music plumbing changed its exact call, definition or invocation ancestry.");
            // These paired unchanged calls are comparison normalization only.
            // They are never writer patches or permission for an authored edit.
            result.Add((range, mapped));
        }
        return result;
    }

    private static bool SameCapturedStartChoice(ScenarioInitialScheduleStartChoice? left,
        ScenarioInitialScheduleStartChoice? right) => left is null || right is null ? left is null && right is null :
        left.Kind == right.Kind && left.SlotIndex == right.SlotIndex &&
        (left.RandomSlotIndices ?? []).SequenceEqual(right.RandomSlotIndices ?? []);

    // Only the already rendered literal [time] payload is evaluated here. This
    // independent shape comparison retains every scalar/child, not just UI keys.
    private static string CapturedTimeShape(ScenarioInitialTimeSlot time)
    {
        var source = new WmlStaticSource("wcc:requested-initial-time", time.RawWml, isAuthored: false);
        var evaluation = new WmlStaticEvaluator().Evaluate(new(source, new(), IsInitialEnvironmentComplete: true,
            Limits: new(MaxOutputCharacters: 2 * 1024 * 1024, MaxSourceCharacters: 2 * 1024 * 1024)));
        var projection = WmlStaticScalarProjection.Create(evaluation);
        var roots = projection.Owners.Where(owner => owner.Parent is null).Take(2).ToArray();
        if (!evaluation.IsComplete || projection.Diagnostics.Count != 0 || roots.Length != 1 || roots[0].TagName != "time")
            throw new InvalidOperationException("A requested initial time lacks a complete literal shape.");
        var text = new StringBuilder();
        var count = 0;
        Append(roots[0], 0);
        return text.ToString();

        void Append(WmlStaticScalarOwner owner, int depth)
        {
            if (++count > 4096 || depth > 128 || !owner.StructureKnown || owner.IsAmended)
                throw new InvalidOperationException("The initial time shape is unresolved or exceeds its bounded tree.");
            Add(text, owner.TagName);
            Add(text, owner.Fields.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var pair in owner.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!pair.Value.IsKnown || pair.Value.ValueText is not { } value)
                    throw new InvalidOperationException("A requested initial time has an unresolved value.");
                Add(text, pair.Key); Add(text, value);
            }
            Add(text, owner.DirectChildren.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var child in owner.DirectChildren) Append(child, depth + 1);
        }
    }
}
