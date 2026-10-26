using System.Globalization;
using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Exact local initial schedule composition. Captured definitions describe macro
/// footprints; their names or effective slot values never establish a write route.
/// The returned source still requires the host's normal source/disk/save fences.
/// </summary>
public static class ScenarioInitialScheduleEditor
{
    public static ScenarioInitialScheduleProjection Project(string source,
        IEnumerable<WmlMacroExpansionDefinition> capturedDefinitions) => Project(source, capturedDefinitions, null);

    /// <summary>Textdomain is captured lexical source context, never inferred from a package or macro name.</summary>
    public static ScenarioInitialScheduleProjection Project(string source,
        IEnumerable<WmlMacroExpansionDefinition> capturedDefinitions, string? sourceTextDomain)
        => ProjectCore(source, capturedDefinitions, sourceTextDomain, null);

    /// <summary>Existing invocation footprints come from their real ordered full
    /// evaluation. The snapshot remains a proposal; Save reconstructs every context.</summary>
    public static ScenarioInitialScheduleProjection Project(WmlStaticSourceRange range,
        WmlStaticScalarProjection capturedProjection, IEnumerable<WmlMacroExpansionDefinition> capturedDefinitions,
        string? sourceTextDomain = null)
    {
        var context = ScenarioInitialSettingsCapturedContext.Create(range, capturedProjection);
        return ProjectCore(range.Text, capturedDefinitions, sourceTextDomain, context,
            context is null ? "The initial schedule needs a complete exact captured scenario evaluation." : null);
    }

    private static ScenarioInitialScheduleProjection ProjectCore(string source,
        IEnumerable<WmlMacroExpansionDefinition> capturedDefinitions, string? sourceTextDomain,
        ScenarioInitialSettingsCapturedContext? capturedContext, string? capturedProblem = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capturedDefinitions);
        var definitions = Array.AsReadOnly(capturedDefinitions.ToArray());
        var identity = DefinitionIdentity(definitions);
        var structure = WmlAuthoredStructureScanner.Scan(source);
        var tree = WmlProjectionTreeParser.Parse(structure.MaskedText);
        var scenario = tree.Roots.FirstOrDefault(node => node.TagName == "scenario");
        var fragments = new List<ScenarioInitialScheduleFragment>();
        var starts = new List<ScenarioInitialScheduleFragment>();
        var times = new List<ScenarioInitialTimeSlot>();
        string? customProblem = null;
        var refusal = capturedProblem ?? structure.Problem;
        if (scenario is null || tree.Roots.Count != 1)
            refusal ??= "One exact authored [scenario] block is required.";
        var closing = scenario is null ? source.Length : structure.Lines[scenario.EndLineExclusive - 1].StartOffset;
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : source.Contains('\r') ? "\r" : "\n";
        var indent = scenario is null ? "    " : IndentAt(source, structure.Lines[scenario.StartLine].StartOffset) + "    ";
        if (scenario is not null)
        {
            if (WmlSingleBlockValidator.Validate(source, "scenario") is { IsValid: false } invalid)
                refusal ??= invalid.Problem;
            var childRanges = scenario.Children.Select(node => (Start: structure.Lines[node.StartLine].StartOffset,
                End: structure.Lines[node.EndLineExclusive - 1].ContentEndOffset)).ToArray();
            foreach (var entry in ScenarioContentSourceInventory.Build(source))
            {
                if (entry.Kind == ScenarioContentSourceKind.ChildTag)
                {
                    if (entry.Name == "+time") { refusal ??= "Local [+time] amendments require scenario RAW editing."; continue; }
                    if (entry.Name != "time") continue;
                    var footprint = capturedContext is null ? Footprint(entry.Text, definitions, sourceTextDomain)
                        : CapturedFootprint(entry, capturedContext, definitions, sourceTextDomain);
                    if (!footprint.PureSchedule || footprint.Times.Count != 1 || footprint.StartScalars.Count != 0)
                        refusal ??= "An initial [time] block has unresolved or mixed expanded output; use scenario RAW.";
                    if (entry.IsConditional) refusal ??= "Conditional initial time sources require scenario RAW editing.";
                    var fragment = new ScenarioInitialScheduleFragment(entry.StartOffset,
                        entry.EndOffsetExclusive - entry.StartOffset, entry.Text,
                        footprint.Times, footprint.StartScalars, true);
                    fragments.Add(fragment);
                    AddTimes(times, footprint, footprint.Times.Count == 1, entry.Text);
                    continue;
                }
                var expanded = capturedContext is null ? Footprint(entry.Text, definitions, sourceTextDomain)
                    : CapturedFootprint(entry, capturedContext, definitions, sourceTextDomain);
                if (expanded.EngineMerge) refusal ??= "A root macro supplies include_file, whose engine merge is outside this source projection; use scenario RAW.";
                if (expanded.Times.Count > 0)
                {
                    if (!expanded.PureSchedule) refusal ??= "A schedule macro also changes other fields or child blocks; preserve its invocation in scenario RAW.";
                    if (entry.IsConditional) refusal ??= "Conditional schedule invocations require scenario RAW editing.";
                    fragments.Add(new(entry.StartOffset, entry.EndOffsetExclusive - entry.StartOffset,
                        entry.Text, expanded.Times, expanded.StartScalars, false, expanded.Definition));
                    AddTimes(times, expanded, false, null);
                    customProblem ??= expanded.MaterializationProblem;
                }
                else if (expanded.PureStart)
                {
                    if (entry.IsConditional) refusal ??= "Conditional starting-time overrides require scenario RAW editing.";
                    starts.Add(new(entry.StartOffset, entry.EndOffsetExclusive - entry.StartOffset,
                        entry.Text, [], expanded.StartScalars, false, expanded.Definition));
                }
                else if (!expanded.ExcludesSchedule)
                    refusal ??= "An unresolved root macro {" + entry.Name + "} may supply initial time slots or a starting-time override; " +
                        (expanded.ExclusionProblem is { } explanation ? explanation + " " : "") + "Use scenario RAW.";
            }
            // Capture only direct literal root starting attributes. Field-looking
            // text inside a child, quote, definition or macro argument is masked.
            for (var lineIndex = scenario.StartLine + 1; lineIndex < scenario.EndLineExclusive - 1; lineIndex++)
            {
                var line = structure.Lines[lineIndex];
                if (childRanges.Any(range => line.StartOffset >= range.Start && line.StartOffset < range.End)) continue;
                var masked = structure.MaskedText[line.StartOffset..line.ContentEndOffset].Trim();
                var equals = masked.IndexOf('=');
                if (capturedContext is null && HasStateMutation(source[line.StartOffset..line.ContentEndOffset]) ||
                    HasTextDomain(source[line.StartOffset..line.ContentEndOffset]))
                    refusal ??= "Root preprocessor definition changes require scenario RAW editing.";
                if (equals < 0 || masked[..equals].Trim() is not ("current_time" or "random_start_time" or "include_file")) continue;
                var rawLine = source[line.StartOffset..line.ContentEndOffset];
                var code = WmlInlineCommentSplitter.Split(rawLine).Value;
                // The splitter trims indentation. Its length describes the
                // authored code token, so the deletion must start at that token
                // rather than at the beginning of the physical source line.
                var codeStart = line.StartOffset + rawLine.Length - rawLine.TrimStart().Length;
                var codeEquals = code.IndexOf('=');
                var key = code[..codeEquals].Trim();
                var value = code[(codeEquals + 1)..].Trim();
                var stringState = new WmlStringScanState();
                WmlRawStringScanner.UpdateState(code, ref stringState);
                if (stringState.InsideMultilineString)
                    refusal ??= "Multiline starting-time or include_file assignments require scenario RAW editing.";
                if (key == "include_file")
                {
                    var includeOwner = Evaluate("[scenario]\n" + code + "\n[/scenario]", definitions).Projection.Owners.FirstOrDefault(owner => owner.TagName == "scenario");
                    if (structure.ConditionalLines[lineIndex] || includeOwner is null || WccScenarioFieldContentScope.CheckOwner(includeOwner) is { })
                        refusal ??= "The scenario's include_file merge cannot be excluded from this source projection; use scenario RAW.";
                    continue;
                }
                if (structure.ConditionalLines[lineIndex]) refusal ??= "Conditional starting-time attributes require scenario RAW editing.";
                starts.Add(new(codeStart, code.Length, code, [], [new(key, value, codeStart, code.Length)], true));
            }
            // Only exact root fragments participate in initial schedule composition.
            // Literal side/event/time_area bodies remain byte-identical and are not
            // misrepresented as a complete engine/runtime evaluation.
        }
        var state = new ScenarioInitialScheduleState(source, WccContentHash.ComputeText(source), identity,
            definitions, Array.AsReadOnly(fragments.OrderBy(fragment => fragment.Start).ToArray()),
            Array.AsReadOnly(starts.OrderBy(fragment => fragment.Start).ToArray()), closing, newline, indent, sourceTextDomain)
        { CapturedContext = capturedContext };
        if (capturedContext is not null)
        {
            var represented = fragments.Concat(starts).Select(fragment => capturedContext.Capture(new(
                fragment.IsLiteral ? ScenarioContentSourceKind.ChildTag : ScenarioContentSourceKind.MacroInvocation,
                fragment.Definition?.Name ?? "time", fragment.Start, fragment.Start + fragment.Length, fragment.Raw, 0)))
                .Where(fragment => fragment is not null).ToArray();
            if (capturedContext.Scenario.DirectChildren.Where(owner => owner.TagName == "time")
                .Any(owner => !represented.Any(fragment => fragment!.Owns(owner))))
                refusal ??= "An initial time has mixed, amended or nonlocal source outside the represented schedule.";
            foreach (var key in new[] { "current_time", "random_start_time" })
                if (capturedContext.Scenario.Fields.TryGetValue(key, out var field) && field.Contributions.Any(contribution =>
                    !represented.Any(fragment => fragment!.Contains(contribution.AssignmentOutputOffset, contribution.AssignmentOutputLength))))
                    refusal ??= "An initial starting-time assignment has no complete represented local source.";
        }
        var allScalars = fragments.Concat(starts).OrderBy(fragment => fragment.Start)
            .SelectMany(fragment => fragment.StartScalars).ToArray();
        var start = ResolveStart(allScalars, times.Count);
        var preset = fragments.Count == 1 && !fragments[0].IsLiteral ? fragments[0] : null;
        return new(state, times, preset?.Raw, preset?.Definition, refusal is null, refusal,
            start.Index, start.Random, start.Eligible, start.Known, customProblem)
        {
            ResolvedStartChoice = CaptureResolvedStartChoice(allScalars, times.Count,
                start.Index, start.Random, start.Known)
        };
    }

    public static ScenarioInitialScheduleComposeResult Compose(ScenarioInitialScheduleProjection projection,
        ScenarioInitialScheduleEdit edit)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(edit);
        if (!projection.CanCompose) return Fail(projection.Diagnostic ?? "The initial schedule has no exact local source route.");
        if (!Enum.IsDefined(edit.Kind)) return Fail("The schedule edit kind is unsupported.");
        var state = projection.State;
        var start = edit.Start ?? new(ScenarioInitialScheduleStartKind.Preserve);
        if (!Enum.IsDefined(start.Kind)) return Fail("The starting-time choice is unsupported.");
        var replacement = "";
        var newCount = projection.Times.Count;
        FootprintResult? preset = null;
        var intendedTimes = new List<ScenarioInitialTimeSlot>();
        if (edit.Kind == ScenarioInitialScheduleEditKind.Preset)
        {
            if (edit.PresetInvocation is null || !WmlMacroInvocationSyntaxScanner.TryParseSingle(edit.PresetInvocation, out _))
                return Fail("Choose one complete captured schedule macro invocation.");
            preset = Footprint(edit.PresetInvocation, state.Definitions, state.SourceTextDomain);
            if (!preset.PureSchedule) return Fail("The selected invocation is not a complete pure schedule footprint from the captured definitions.");
            newCount = preset.Times.Count;
            replacement = edit.PresetInvocation;
            AddTimes(intendedTimes, preset, false, null);
        }
        else if (edit.Kind == ScenarioInitialScheduleEditKind.Custom)
        {
            if (!projection.CanConvertToCustom) return Fail(projection.CustomConversionDiagnostic ?? "The preset cannot be materialized with its exact translation context.");
            var blocks = edit.TimeBlocks?.ToArray();
            if (blocks is null || blocks.Length == 0) return Fail("A custom initial cycle needs at least one [time] block.");
            foreach (var block in blocks)
            {
                if (block is null || WmlSingleBlockValidator.Validate(block, "time") is { IsValid: false })
                    return Fail("Each custom slot must contain one complete [time] block and no surrounding code.");
                var footprint = Footprint(block, state.Definitions, state.SourceTextDomain);
                if (!footprint.PureSchedule || footprint.Times.Count != 1 || footprint.StartScalars.Count != 0)
                    return Fail("A custom time block must expand to exactly one initial time without other output.");
                AddTimes(intendedTimes, footprint, true, block);
            }
            newCount = blocks.Length;
            replacement = string.Join(state.NewLine, blocks);
        }
        else intendedTimes.AddRange(projection.Times);
        if (newCount == 0 && start.Kind != ScenarioInitialScheduleStartKind.Preserve)
            return Fail("Choose an initial time cycle before setting its start.");
        if (start.Kind == ScenarioInitialScheduleStartKind.Slot && (start.SlotIndex is not { } slot || slot < 0 || slot >= newCount))
            return Fail("Choose a starting slot in the current cycle.");
        var eligible = start.RandomSlotIndices?.ToArray();
        if (start.Kind == ScenarioInitialScheduleStartKind.RandomSlots
            && (eligible is null || eligible.Length == 0 || eligible.Any(index => index < 0 || index >= newCount)))
            return Fail("Choose at least one random starting slot in the current cycle.");
        if (start.Kind is ScenarioInitialScheduleStartKind.RandomAll or ScenarioInitialScheduleStartKind.RandomSlots)
        {
            if (preset?.StartScalars.Any(scalar => scalar.Key == "current_time") == true)
                return Fail("This preset supplies current_time. Convert it to Custom before choosing a random start.");
            if (edit.Kind == ScenarioInitialScheduleEditKind.Preserve
                && state.Fragments.Any(fragment => fragment.StartScalars.Any(scalar => scalar.Key == "current_time")))
                return Fail("A schedule macro supplies current_time. Convert it to Custom before choosing a random start.");
        }

        var patches = new List<(int Start, int Length, string Text)>();
        if (edit.Kind != ScenarioInitialScheduleEditKind.Preserve)
        {
            var inserted = false;
            foreach (var fragment in state.Fragments)
            {
                var text = !inserted ? FormatFragment(replacement, state, fragment.IsLiteral) : "";
                inserted = true;
                // Converting shared/local presets to local times preserves every
                // macro-owned starting assignment at its original source order.
                if (edit.Kind == ScenarioInitialScheduleEditKind.Custom && start.Kind == ScenarioInitialScheduleStartKind.Preserve)
                    text += string.Concat(fragment.StartScalars.Select(scalar => state.NewLine + state.Indent + scalar.Key + "=" + scalar.Value));
                if (edit.Kind == ScenarioInitialScheduleEditKind.Custom && !fragment.IsLiteral && text.Length > 0)
                    text = SeparateLiteralReplacement(text, fragment, state);
                patches.Add((fragment.Start, fragment.Length, text));
            }
            if (!inserted) patches.Add((state.ClosingOffset, 0, FormatFragment(replacement, state, true) + state.NewLine));
        }
        if (start.Kind != ScenarioInitialScheduleStartKind.Preserve)
        {
            foreach (var fragment in state.StartFragments) patches.Add((fragment.Start, fragment.Length, ""));
            var value = start.Kind switch
            {
                ScenarioInitialScheduleStartKind.Slot => "current_time=" + start.SlotIndex!.Value.ToString(CultureInfo.InvariantCulture),
                ScenarioInitialScheduleStartKind.RandomAll => "random_start_time=yes",
                // The engine takes positive numbers modulo count without -1;
                // count denotes displayed index0, index1 denotes the second slot.
                ScenarioInitialScheduleStartKind.RandomSlots => "random_start_time=" + string.Join(",", eligible!.Select(index => index == 0 ? newCount : index)),
                _ => ""
            };
            patches.Add((state.ClosingOffset, 0, state.Indent + value + state.NewLine));
        }
        // Remove the physical line only when the removed fragment owns all its
        // non-whitespace text. Keep comments, adjacent calls and pre-existing
        // blank lines; replacing a long cycle must not manufacture empty rows.
        for (var index = 0; index < patches.Count; index++)
        {
            var patch = patches[index];
            if (patch.Length == 0 || patch.Text.Length != 0 || !state.Fragments.Any(fragment =>
                    fragment.Start == patch.Start && fragment.Length == patch.Length)) continue;
            var lineStart = patch.Start;
            while (lineStart > 0 && state.Source[lineStart - 1] is not ('\n' or '\r')) lineStart--;
            var end = patch.Start + patch.Length;
            var lineEnd = end;
            while (lineEnd < state.Source.Length && state.Source[lineEnd] is not ('\n' or '\r')) lineEnd++;
            if (!string.IsNullOrWhiteSpace(state.Source[lineStart..patch.Start]) ||
                !string.IsNullOrWhiteSpace(state.Source[end..lineEnd])) continue;
            if (lineEnd < state.Source.Length && state.Source[lineEnd++] == '\r' &&
                lineEnd < state.Source.Length && state.Source[lineEnd] == '\n') lineEnd++;
            if (patches.Where((_, other) => other != index).Any(other =>
                    other.Start < lineEnd && other.Start + other.Length > lineStart)) continue;
            patches[index] = (lineStart, lineEnd - lineStart, "");
        }
        var builder = new StringBuilder();
        var position = 0;
        foreach (var patch in patches.OrderBy(patch => patch.Start).ThenBy(patch => patch.Length))
        {
            if (patch.Start < position) return Fail("Schedule and starting-time source replacements overlap; use scenario RAW.");
            builder.Append(state.Source, position, patch.Start - position).Append(patch.Text);
            position = patch.Start + patch.Length;
        }
        builder.Append(state.Source, position, state.Source.Length - position);
        var candidate = builder.ToString();
        if (WmlSingleBlockValidator.Validate(candidate, "scenario") is { IsValid: false } malformed)
            return Fail(malformed.Problem ?? "The composed scenario structure is malformed.");
        ScenarioInitialScheduleProjection proposed;
        if (state.CapturedContext is null)
        {
            proposed = Project(candidate, state.Definitions, state.SourceTextDomain);
            if (!proposed.CanCompose || proposed.Times.Count != newCount)
                return Fail(proposed.Diagnostic ?? "The composed initial schedule does not preserve the requested slot footprint.");
            if ((start.Kind == ScenarioInitialScheduleStartKind.Slot && proposed.StartIndex != start.SlotIndex)
                || ((start.Kind is ScenarioInitialScheduleStartKind.RandomAll or ScenarioInitialScheduleStartKind.RandomSlots) && !proposed.IsRandomStart))
                return Fail("The composed source does not resolve to the requested starting-time choice.");
        }
        else proposed = ComposedCapturedPreview(projection, edit, start, preset, intendedTimes, candidate);
        return new(true, candidate)
        {
            Preview = proposed,
            SourcePatches = Array.AsReadOnly(patches.OrderBy(patch => patch.Start).ThenBy(patch => patch.Length)
                .Select(patch => new WccScenarioFieldSourcePatch(patch.Start, patch.Length, patch.Text)).ToArray())
        };
    }

    private static ScenarioInitialScheduleComposeResult Fail(string message) => new(false, Diagnostic: message);

    private static ScenarioInitialScheduleProjection ComposedCapturedPreview(ScenarioInitialScheduleProjection original,
        ScenarioInitialScheduleEdit edit, ScenarioInitialScheduleStartChoice start, FootprintResult? preset,
        IReadOnlyList<ScenarioInitialTimeSlot> times, string candidate)
    {
        var state = original.State;
        var ordered = new List<(int Position, ScenarioInitialScheduleScalar Scalar)>();
        if (edit.Kind == ScenarioInitialScheduleEditKind.Preserve || edit.Kind == ScenarioInitialScheduleEditKind.Custom &&
            start.Kind == ScenarioInitialScheduleStartKind.Preserve)
            ordered.AddRange(state.Fragments.SelectMany(fragment => fragment.StartScalars.Select(scalar => (fragment.Start, scalar))));
        else if (preset is not null) ordered.AddRange(preset.StartScalars.Select(scalar =>
            (state.Fragments.FirstOrDefault()?.Start ?? state.ClosingOffset, scalar)));
        if (start.Kind == ScenarioInitialScheduleStartKind.Preserve)
            ordered.AddRange(state.StartFragments.SelectMany(fragment => fragment.StartScalars.Select(scalar => (fragment.Start, scalar))));
        var scalars = ordered.OrderBy(item => item.Position).Select(item => item.Scalar).ToList();
        if (start.Kind != ScenarioInitialScheduleStartKind.Preserve)
            scalars.Add(new(start.Kind == ScenarioInitialScheduleStartKind.Slot ? "current_time" : "random_start_time",
            start.Kind == ScenarioInitialScheduleStartKind.Slot ? start.SlotIndex!.Value.ToString(CultureInfo.InvariantCulture)
                : start.Kind == ScenarioInitialScheduleStartKind.RandomAll ? "yes"
                : string.Join(",", start.RandomSlotIndices!.Select(index => index == 0 ? times.Count : index)), int.MaxValue, 0));
        var resolved = ResolveStart(scalars, times.Count);
        var previewState = new ScenarioInitialScheduleState(candidate, WccContentHash.ComputeText(candidate), state.DefinitionIdentity,
            state.Definitions, [], [], 0, state.NewLine, state.Indent, state.SourceTextDomain);
        return new(previewState, times,
            edit.Kind == ScenarioInitialScheduleEditKind.Preset ? edit.PresetInvocation :
                edit.Kind == ScenarioInitialScheduleEditKind.Preserve ? original.PresetInvocation : null,
            preset?.Definition ?? (edit.Kind == ScenarioInitialScheduleEditKind.Preserve ? original.PresetDefinition : null),
            false, "Pending initial schedule preview; compose from the original captured source snapshot.",
            resolved.Index, resolved.Random, resolved.Eligible, resolved.Known, null)
        { ResolvedStartChoice = CaptureResolvedStartChoice(scalars, times.Count, resolved.Index, resolved.Random, resolved.Known) };
    }

    private static FootprintResult CapturedFootprint(ScenarioContentSourceEntry entry,
        ScenarioInitialSettingsCapturedContext context, IReadOnlyList<WmlMacroExpansionDefinition> definitions,
        string? sourceTextDomain)
    {
        if (context.Capture(entry) is not { } fragment)
        {
            // An inactive branch has no observed invocation. Absence alone is
            // never proof: reuse the ordinary all-output negative footprint,
            // solely to exclude a fully known unrelated call from this editor.
            var negative = Footprint(entry.Text, definitions, sourceTextDomain);
            if (negative.ExcludesSchedule && !negative.EngineMerge)
                return new(false, false, true, [], [], [], null, false, null);
            return new(false, false, false, [], [], [], null, false, null, "The authored invocation has no unique captured occurrence.");
        }
        var times = context.Scenario.DirectChildren.Where(owner => owner.TagName == "time" && fragment.Touches(owner)).ToArray();
        var scalars = context.Scenario.Fields.Where(pair => pair.Key is "current_time" or "random_start_time")
            .SelectMany(pair => pair.Value.Contributions.Where(fragment.Touches).Select(contribution =>
                new ScenarioInitialScheduleScalar(pair.Key, context.Evaluation.Text.Substring(contribution.ValueOutputOffset,
                    contribution.ValueOutputLength), contribution.AssignmentOutputOffset, contribution.AssignmentOutputLength)))
            .OrderBy(scalar => scalar.Start).ToArray();
        var excludes = times.Length == 0 && scalars.Length == 0;
        var allowed = times.Select(ScenarioInitialSettingsCapturedContext.OwnerSpan).Where(span => span is not null)
            .Select(span => span!.Value).Concat(scalars.Select(scalar => (scalar.Start, scalar.Start + scalar.Length))).ToArray();
        var pure = !fragment.HasStatefulEffects && times.All(fragment.Owns) && fragment.OnlyEmits(allowed);
        var definition = fragment.Invocation?.Definition is { } active
            ? ScenarioInitialSettingsCapturedContext.FindActiveDefinition(definitions, entry.Name, active) : null;
        var rendered = times.Select(time => RenderOwner(time, "", definitions, sourceTextDomain, context.Evaluation)).ToArray();
        return new(pure && times.Length > 0 && scalars.All(scalar => scalar.Key == "current_time" && TryInteger(scalar.Value, out _)),
            pure && times.Length == 0 && scalars.Length > 0, excludes, rendered.Select(item => item.Text).ToArray(),
            scalars, times, definition, false, rendered.Select(item => item.Problem).FirstOrDefault(item => item is not null));
    }

    // Reuse the same start interpretation for an already evaluated display. These
    // merged values have no authored fragments and cannot enter composition.
    internal static ScenarioInitialScheduleProjection CapturedReadOnly(WmlStaticSourceRange range,
        WmlStaticScalarOwner scenario, IReadOnlyList<ScenarioInitialTimeSlot> times)
    {
        var scalars = scenario.Fields.Where(pair => pair.Key is "current_time" or "random_start_time")
            .Select(pair => new ScenarioInitialScheduleScalar(pair.Key,
                pair.Value.IsKnown ? pair.Value.ValueText ?? "$unresolved" : "$unresolved", 0, 0)).ToArray();
        var start = ResolveStart(scalars, times.Count);
        var state = new ScenarioInitialScheduleState(range.Text, WccContentHash.ComputeText(range.Text), "", [], [], [],
            0, "\n", "", null);
        return new(state, times, null, null, false,
            "Read-only initial schedule from the selected captured context. Edit its authored source to change this schedule.",
            start.Index, start.Random, start.Eligible, start.Known, null);
    }

    private static void AddTimes(List<ScenarioInitialTimeSlot> slots, FootprintResult footprint, bool literal, string? raw)
    {
        for (var index = 0; index < footprint.Times.Count; index++)
        {
            var owner = footprint.TimeOwners[index];
            string? Value(string key) => owner.Fields.TryGetValue(key, out var field) && field.ValueText is { } value
                ? WccScenarioFieldValueCodec.DecodeLiteral(value) : null;
            slots.Add(new(slots.Count, literal ? raw! : footprint.Times[index], Value("id"), Value("name"), Value("image"), Value("lawful_bonus"), literal)
            { Red = Value("red"), Green = Value("green"), Blue = Value("blue") });
        }
    }

    private static FootprintResult Footprint(string raw, IReadOnlyList<WmlMacroExpansionDefinition> definitions, string? sourceTextDomain = null)
    {
        var evaluated = Evaluate("[scenario]\n" + raw + "\n[/scenario]", definitions);
        var root = evaluated.Projection.Owners.FirstOrDefault(owner => owner.TagName == "scenario");
        if (root is null) return new(false, false, false, [], [], [], null, false, null);
        var times = root.DirectChildren.Where(owner => owner.TagName == "time").ToArray();
        var scalars = root.Fields.Where(pair => pair.Key is "current_time" or "random_start_time")
            .SelectMany(pair => pair.Value.Contributions.Select(contribution => new ScenarioInitialScheduleScalar(pair.Key,
                evaluated.Evaluation.Text.Substring(contribution.ValueOutputOffset, contribution.ValueOutputLength),
                contribution.AssignmentOutputOffset, contribution.AssignmentOutputLength)))
            .OrderBy(scalar => scalar.Start).ToArray();
        var allowed = root.Fields.Keys.All(key => key == "current_time") && root.DirectChildren.All(owner => owner.TagName == "time");
        var bodies = evaluated.Evaluation.Segments.SelectMany(segment => segment.ExpansionStack)
            .Select(frame => frame.Definition.Text).Distinct(StringComparer.Ordinal).ToArray();
        var stable = !HasStateMutation(raw) && !ContainsCapturedControl(raw, definitions, mutationOnly: true, 0)
            && bodies.All(body => !DefinitionHasControl(body, mutationOnly: true));
        if (HasTextDomain(raw) && LastTextDomain(raw) != sourceTextDomain) stable = false;
        var unconditional = !HasConditional(raw) && !ContainsCapturedControl(raw, definitions, mutationOnly: false, 0)
            && bodies.All(body => !DefinitionHasControl(body, mutationOnly: false));
        var pure = stable && unconditional && evaluated.Evaluation.IsComplete && evaluated.Projection.Diagnostics.Count == 0 && times.Length > 0
            && allowed && scalars.All(scalar => TryInteger(scalar.Value, out _));
        var pureStart = stable && unconditional && evaluated.Evaluation.IsComplete && evaluated.Projection.Diagnostics.Count == 0
            && times.Length == 0 && root.DirectChildren.Count == 0 && scalars.Length > 0
            && root.Fields.Count == root.Fields.Keys.Count(key => key is "current_time" or "random_start_time");
        var expandedStructure = WmlAuthoredStructureScanner.Scan(evaluated.Evaluation.Text);
        var expandedTree = WmlProjectionTreeParser.Parse(expandedStructure.MaskedText).Roots.FirstOrDefault(node => node.TagName == "scenario");
        var childRanges = expandedTree?.Children.Select(node => (Start: expandedStructure.Lines[node.StartLine].StartOffset,
            End: expandedStructure.Lines[node.EndLineExclusive - 1].ContentEndOffset)).ToArray() ?? [];
        var unknownRootMacro = expandedStructure.Macros.Any(macro => !childRanges.Any(range => macro.StartOffset >= range.Start && macro.StartOffset < range.End));
        // An unresolved conditional can hide time children or starting values
        // without exposing any winning contribution in this projection. Only
        // unconditional complete output, or the all-branch scalar proof below,
        // can establish that this root invocation supplies no schedule.
        var excludes = stable && unconditional && evaluated.Evaluation.IsComplete
            && times.Length == 0 && scalars.Length == 0 && root.OpeningOutputOffset is not null
            && !unknownRootMacro && evaluated.Projection.Diagnostics.Count == 0;
        if (!excludes && times.Length == 0 && scalars.Length == 0)
            excludes = ExcludesByScalarBranches(raw, definitions) || ExcludesByNumericVariants(raw, definitions);
        WmlMacroExpansionDefinition? definition = null;
        if (WmlMacroInvocationSyntaxScanner.TryParseSingle(raw, out var invocation) && invocation is not null)
        {
            var candidates = definitions.Where(candidate => candidate.Name == invocation.Name).ToArray();
            if (candidates.Length == 1) definition = candidates[0];
        }
        var engineMerge = root.Fields.ContainsKey("include_file") && WccScenarioFieldContentScope.CheckOwner(root) is not null;
        var materialized = times.Select(time => RenderOwner(time, "", definitions, sourceTextDomain, evaluated.Evaluation)).ToArray();
        return new(pure, pureStart, excludes, materialized.Select(rendered => rendered.Text).ToArray(), scalars, times, definition, engineMerge,
            materialized.Select(rendered => rendered.Problem).FirstOrDefault(problem => problem is not null),
            evaluated.Evaluation.Diagnostics.FirstOrDefault(diagnostic => diagnostic.IsBlocking)?.Message);
    }

    private static (WmlStaticEvaluationResult Evaluation, WmlStaticScalarProjection Projection) Evaluate(string source,
        IReadOnlyList<WmlMacroExpansionDefinition> definitions)
    {
        var unique = definitions.GroupBy(definition => definition.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Single());
        var prelude = new WmlStaticSource("wcc:schedule-captured-definitions", string.Join("\n", unique.Select(definition => definition.RawText)) + "\n", false);
        var input = new WmlStaticSource("wcc:schedule-source", source, false);
        var evaluation = new WmlStaticEvaluator().Evaluate(new(input, new(targetCompatibility: "1.20"), [prelude]));
        return (evaluation, WmlStaticScalarProjection.Create(evaluation));
    }

    // This union is only a negative footprint proof. It never supplies time
    // slots, starting values or replacement text: every possible branch must
    // reduce to bounded unrelated scalar assignments without changing symbols.
    private static bool ExcludesByScalarBranches(string raw, IReadOnlyList<WmlMacroExpansionDefinition> definitions)
    {
        var union = ExpandScalarBranches(raw, definitions, 0);
        if (union is null) return false;
        var evaluated = Evaluate("[scenario]\n" + union + "\n[/scenario]", definitions);
        var root = evaluated.Projection.Owners.FirstOrDefault(owner => owner.TagName == "scenario");
        return evaluated.Evaluation.IsComplete && evaluated.Projection.Diagnostics.Count == 0
            && root is not null && root.OpeningOutputOffset is not null && root.DirectChildren.Count == 0
            && root.Fields.Keys.All(key => key is not ("current_time" or "random_start_time" or "include_file"));
    }

    private static string? ExpandScalarBranches(string raw, IReadOnlyList<WmlMacroExpansionDefinition> definitions, int depth)
    {
        if (depth >= 32 || raw.Length > 1024 * 1024 || HasStateMutation(raw) || HasTextDomain(raw)) return null;
        var cleaned = WithoutConditionalControls(raw);
        var structure = WmlAuthoredStructureScanner.Scan(cleaned);
        if (structure.Problem is not null) return null;
        var builder = new StringBuilder();
        var position = 0;
        foreach (var macro in structure.Macros)
        {
            var invocationText = cleaned[macro.StartOffset..macro.EndOffsetExclusive];
            if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(invocationText, out var invocation) || invocation is null) return null;
            var candidates = definitions.Where(definition => definition.Name == invocation.Name).ToArray();
            if (candidates.Length != 1 || !WmlMacroDefinitionTemplate.TryParse(candidates[0], out var template, out _)
                || template is null || !template.TryResolveArguments(invocation.Arguments.Select(argument => argument.RawText).ToArray(), out var arguments, out _)) return null;
            var body = string.Join("\n", template.SubstituteBody(arguments));
            var expanded = ExpandScalarBranches(body, definitions, depth + 1);
            if (expanded is null) return null;
            builder.Append(cleaned, position, macro.StartOffset - position).Append(expanded);
            position = macro.EndOffsetExclusive;
            if (builder.Length > 1024 * 1024) return null;
        }
        return builder.Append(cleaned, position, cleaned.Length - position).ToString();
    }

    // Captured conditional definitions can return different scalar arguments.
    // For exclusion only, evaluate every safe numeric return variant in the
    // actual unchanged event/side footprint. No selected difficulty, projected
    // owner boundary or apparent placement of an unresolved call is assumed.
    private static bool ExcludesByNumericVariants(string raw, IReadOnlyList<WmlMacroExpansionDefinition> definitions)
    {
        if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(raw, out var invocation) || invocation is null) return false;
        var candidates = definitions.Where(definition => definition.Name == invocation.Name).ToArray();
        if (candidates.Length != 1 || !WmlMacroDefinitionTemplate.TryParse(candidates[0], out var template, out _)
            || template is null || !template.TryResolveArguments(invocation.Arguments.Select(argument => argument.RawText).ToArray(), out var arguments, out _)) return false;
        var body = string.Join("\n", template.SubstituteBody(arguments));
        var variants = ExpandNumericVariants(body, definitions, 0);
        if (variants is null || variants.Count == 0) return false;
        foreach (var variant in variants)
        {
            if (HasStateMutation(variant) || HasTextDomain(variant) || HasConditional(variant)
                || ContainsCapturedControl(variant, definitions, mutationOnly: true, 0)
                || ContainsCapturedControl(variant, definitions, mutationOnly: false, 0)) return false;
            var evaluated = Evaluate("[scenario]\n" + variant + "\n[/scenario]", definitions);
            var root = evaluated.Projection.Owners.FirstOrDefault(owner => owner.TagName == "scenario");
            if (!evaluated.Evaluation.IsComplete || evaluated.Projection.Diagnostics.Count != 0
                || root is null || root.OpeningOutputOffset is null || root.DirectChildren.Any(child => child.TagName == "time")
                || new[] { "current_time", "random_start_time", "include_file" }.Any(key =>
                    root.ResolveField(key).State != WmlStaticScalarResolutionState.ProvenAbsent)) return false;
        }
        return true;
    }

    private static IReadOnlyList<string>? ExpandNumericVariants(string raw, IReadOnlyList<WmlMacroExpansionDefinition> definitions, int depth)
    {
        if (depth >= 16 || raw.Length > 1024 * 1024) return null;
        var invocations = WmlMacroInvocationSyntaxScanner.Scan(raw);
        foreach (var invocation in invocations.OrderByDescending(invocation => invocation.NestingDepth))
        {
            var outputs = NumericOutputs(invocation, definitions, 0);
            if (outputs is null) continue;
            var variants = new List<string>();
            foreach (var output in outputs)
            {
                // Identical calls have identical returns in this mutation-free
                // footprint. Replacing their exact occurrences together bounds
                // repeated uses without picking one candidate definition.
                var matching = invocations.Where(item => item.RawText == invocation.RawText)
                    .OrderByDescending(item => item.Span.StartOffset).ToArray();
                var builder = new StringBuilder(raw);
                foreach (var match in matching) builder.Remove(match.Span.StartOffset, match.Span.Length).Insert(match.Span.StartOffset, output);
                var expanded = ExpandNumericVariants(builder.ToString(), definitions, depth + 1);
                if (expanded is null) return null;
                variants.AddRange(expanded);
                if (variants.Count > 32) return null;
            }
            return variants.Distinct(StringComparer.Ordinal).ToArray();
        }
        return [raw];
    }

    private static IReadOnlyList<string>? NumericOutputs(WmlMacroInvocationSyntax invocation,
        IReadOnlyList<WmlMacroExpansionDefinition> definitions, int depth)
    {
        if (depth >= 16) return null;
        var candidates = definitions.Where(definition => definition.Name == invocation.Name).ToArray();
        if (candidates.Length == 0 || candidates.Length > 32) return null;
        var outputs = new List<string>();
        foreach (var candidate in candidates)
        {
            if (DefinitionHasControl(candidate.RawText, mutationOnly: true) || DefinitionHasControl(candidate.RawText, mutationOnly: false)
                || !WmlMacroDefinitionTemplate.TryParse(candidate, out var template, out _) || template is null
                || !template.TryResolveArguments(invocation.Arguments.Select(argument => argument.RawText).ToArray(), out var arguments, out _)) return null;
            var body = string.Join("\n", template.SubstituteBody(arguments));
            if (HasStateMutation(body) || HasTextDomain(body) || HasConditional(body)) return null;
            var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
                .Select(line => WmlInlineCommentSplitter.Split(line).Value.Trim()).Where(line => line.Length > 0).ToArray();
            if (lines.Length != 1) return null;
            var value = lines[0];
            if (value.All(character => character is >= '0' and <= '9' or '+' or '-' or '.')
                && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
                outputs.Add(value);
            else if (WmlMacroInvocationSyntaxScanner.TryParseSingle(value, out var nested) && nested is not null
                     && NumericOutputs(nested, definitions, depth + 1) is { } nestedOutputs) outputs.AddRange(nestedOutputs);
            else return null;
            if (outputs.Count > 32) return null;
        }
        return outputs.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool DefinitionHasControl(string raw, bool mutationOnly)
    {
        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var header = Array.FindIndex(lines, line => line.TrimStart().StartsWith("#define", StringComparison.Ordinal));
        var body = string.Join("\n", header >= 0 ? lines.Skip(header + 1) : lines);
        return mutationOnly ? HasStateMutation(body) || HasTextDomain(body) : HasConditional(body);
    }

    private static bool ContainsCapturedControl(string raw, IReadOnlyList<WmlMacroExpansionDefinition> definitions, bool mutationOnly, int depth)
    {
        if (depth >= 32 || raw.Length > 1024 * 1024) return true;
        foreach (var macro in WmlAuthoredStructureScanner.Scan(raw).Macros)
        {
            if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(raw[macro.StartOffset..macro.EndOffsetExclusive], out var invocation) || invocation is null) continue;
            var candidates = definitions.Where(definition => definition.Name == invocation.Name).ToArray();
            if (candidates.Length != 1) continue;
            if (DefinitionHasControl(candidates[0].RawText, mutationOnly)) return true;
            if (!WmlMacroDefinitionTemplate.TryParse(candidates[0], out var template, out _) || template is null
                || !template.TryResolveArguments(invocation.Arguments.Select(argument => argument.RawText).ToArray(), out var arguments, out _)) continue;
            if (ContainsCapturedControl(string.Join("\n", template.SubstituteBody(arguments)), definitions, mutationOnly, depth + 1)) return true;
        }
        return false;
    }

    private static bool HasStateMutation(string raw) => ScanControl(raw, mutations: true);
    private static bool HasConditional(string raw) => ScanControl(raw, mutations: false);
    private static bool ScanControl(string raw, bool mutations)
    {
        var state = new WmlStringScanState();
        foreach (var line in raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!state.InsideMultilineString && (mutations
                    ? trimmed.StartsWith("#define", StringComparison.Ordinal) || trimmed.StartsWith("#undef", StringComparison.Ordinal)
                    : WmlPreprocessorScanner.GetDirectiveKind(trimmed) != WmlPreprocessorDirectiveKind.None)) return true;
            WmlRawStringScanner.UpdateState(line, ref state);
        }
        return false;
    }

    private static string WithoutConditionalControls(string raw)
    {
        var state = new WmlStringScanState();
        return string.Join("\n", raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(line =>
        {
            var control = !state.InsideMultilineString && WmlPreprocessorScanner.GetDirectiveKind(line.TrimStart()) != WmlPreprocessorDirectiveKind.None;
            WmlRawStringScanner.UpdateState(line, ref state);
            return control ? "" : line;
        }));
    }

    private static (string Text, string? Problem) RenderOwner(WmlStaticScalarOwner owner, string indent,
        IReadOnlyList<WmlMacroExpansionDefinition> definitions, string? sourceTextDomain, WmlStaticEvaluationResult evaluation)
    {
        var builder = new StringBuilder(indent + "[" + owner.TagName + "]\n");
        string? problem = null;
        var activeDomain = sourceTextDomain;
        var usedDomain = false;
        foreach (var pair in owner.Fields.OrderBy(pair => pair.Value.Contributions.LastOrDefault()?.AssignmentOutputOffset ?? int.MaxValue))
        {
            if (pair.Value.ValueText?.TrimStart().StartsWith('_') == true)
            {
                var contribution = pair.Value.WinningContribution;
                var domains = new List<string?>();
                if (contribution is not null)
                {
                    foreach (var segment in evaluation.Segments.Where(segment => segment.OutputOffset < contribution.ValueOutputOffset + contribution.ValueOutputLength
                                 && segment.OutputOffset + segment.Text.Length > contribution.ValueOutputOffset))
                    {
                        var begin = Math.Max(segment.OutputOffset, contribution.ValueOutputOffset);
                        var end = Math.Min(segment.OutputOffset + segment.Text.Length, contribution.ValueOutputOffset + contribution.ValueOutputLength);
                        if (string.IsNullOrWhiteSpace(evaluation.Text[begin..end])) continue;
                        var frame = segment.ExpansionStack.LastOrDefault();
                        if (frame is null) domains.Add(WmlTextDomainCapture.BeforeOffset(
                            segment.Origin.Source.Text, segment.Origin.StartOffset) ?? sourceTextDomain);
                        else
                        {
                            var exact = ScenarioInitialSettingsCapturedContext.FindActiveDefinition(definitions, frame.MacroName, frame.Definition);
                            if (exact is not null) domains.Add(exact.TextDomain);
                            else
                            {
                                // Standalone legacy previews have synthetic definition
                                // paths; retain their original unique-template rule.
                                var candidates = frame.Definition.Source.IsAuthored ? Array.Empty<WmlMacroExpansionDefinition>() : definitions.Where(definition => definition.Name == frame.MacroName
                                    && definition.RawText.Trim() == frame.Definition.Text.Trim()).ToArray();
                                domains.Add(candidates.Length == 1 ? candidates[0].TextDomain : null);
                            }
                        }
                    }
                }
                var distinctDomains = domains.Distinct(StringComparer.Ordinal).ToArray();
                var domain = distinctDomains.Length == 1 ? distinctDomains[0] : null;
                if (!ValidDomain(domain) || !ValidDomain(sourceTextDomain))
                    problem ??= "A translated time field lacks its captured definition or scenario textdomain; preserve the preset or use scenario RAW.";
                else if (domain != activeDomain)
                {
                    builder.Append(indent).Append("    #textdomain ").Append(domain).Append('\n');
                    activeDomain = domain;
                    usedDomain = true;
                }
            }
            builder.Append(indent).Append("    ").Append(pair.Key).Append('=').Append(pair.Value.ValueText).Append('\n');
        }
        if (usedDomain && activeDomain != sourceTextDomain)
            builder.Append(indent).Append("    #textdomain ").Append(sourceTextDomain).Append('\n');
        foreach (var child in owner.DirectChildren)
        {
            var rendered = RenderOwner(child, indent + "    ", definitions, sourceTextDomain, evaluation);
            builder.Append(rendered.Text);
            problem ??= rendered.Problem;
        }
        builder.Append(indent).Append("[/").Append(owner.TagName).Append(']');
        if (indent.Length > 0) builder.Append('\n');
        return (builder.ToString(), problem);
    }

    private static bool ValidDomain(string? domain) => !string.IsNullOrWhiteSpace(domain)
        && domain.All(character => !char.IsWhiteSpace(character) && character is not ('#' or '{' or '}' or '$' or '"' or '<' or '>'));
    private static bool HasTextDomain(string raw) => LastTextDomain(raw) is not null;
    private static string? LastTextDomain(string raw)
    {
        var state = new WmlStringScanState();
        string? domain = null;
        foreach (var line in raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!state.InsideMultilineString && trimmed.StartsWith("#textdomain", StringComparison.Ordinal)
                && trimmed.Length > 11 && char.IsWhiteSpace(trimmed[11])) domain = WmlInlineCommentSplitter.Split(trimmed[12..]).Value.Trim();
            WmlRawStringScanner.UpdateState(line, ref state);
        }
        return domain;
    }

    private static (int? Index, bool Random, int[] Eligible, bool Known) ResolveStart(
        IReadOnlyList<ScenarioInitialScheduleScalar> scalars, int count)
    {
        if (count == 0) return (null, false, [], false);
        var current = scalars.LastOrDefault(scalar => scalar.Key == "current_time");
        if (current is not null)
            return TryInteger(current.Value, out var value) && value != -17403
                ? (Modulo(value, count), false, [], true) : (null, false, [], false);
        var random = scalars.LastOrDefault(scalar => scalar.Key == "random_start_time");
        if (random is null) return (0, false, [], true);
        if (random.Value.TrimStart().StartsWith('_')) return (null, false, [], false);
        var raw = WccScenarioFieldValueCodec.DecodeLiteral(random.Value).Trim();
        if (raw is "yes" or "true") return (null, true, Enumerable.Range(0, count).ToArray(), true);
        if (raw is "no" or "false" or "") return (0, false, [], true);
        var eligible = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                return (null, false, [], false);
            if (index > 0) eligible.Add(Modulo(index, count));
        }
        return eligible.Count > 0 ? (null, true, eligible.Distinct().ToArray(), true) : (0, false, [], true);
    }

    private static bool TryInteger(string raw, out int value)
    {
        value = 0;
        return !raw.TrimStart().StartsWith('_') && int.TryParse(WccScenarioFieldValueCodec.DecodeLiteral(raw),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
    private static ScenarioInitialScheduleStartChoice? CaptureResolvedStartChoice(
        IReadOnlyList<ScenarioInitialScheduleScalar> scalars, int count, int? index, bool random, bool known)
    {
        if (!known || count == 0) return null;
        if (!random) return new(ScenarioInitialScheduleStartKind.Slot, index);
        var scalar = scalars.LastOrDefault(scalar => scalar.Key == "random_start_time");
        if (scalar is null) return null;
        var raw = WccScenarioFieldValueCodec.DecodeLiteral(scalar.Value).Trim();
        if (raw is "yes" or "true") return new(ScenarioInitialScheduleStartKind.RandomAll);
        // ResolveStart has already proved the complete numeric list. Keep every
        // positive entry here; its duplicates encode weighted random choices.
        var eligible = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return null;
            if (value > 0) eligible.Add(Modulo(value, count));
        }
        return new(ScenarioInitialScheduleStartKind.RandomSlots,
            RandomSlotIndices: Array.AsReadOnly(eligible.ToArray()));
    }
    private static int Modulo(int value, int count) => (value % count + count) % count;
    private static string IndentAt(string source, int offset)
    {
        var end = offset;
        while (end < source.Length && source[end] is ' ' or '\t') end++;
        return source[offset..end];
    }
    private static string FormatFragment(string raw, ScenarioInitialScheduleState state, bool prefixIndent) =>
        (prefixIndent ? state.Indent : "") + raw.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\n", state.NewLine + state.Indent, StringComparison.Ordinal);

    private static string SeparateLiteralReplacement(string text, ScenarioInitialScheduleFragment fragment, ScenarioInitialScheduleState state)
    {
        var lineStart = fragment.Start;
        while (lineStart > 0 && state.Source[lineStart - 1] is not ('\n' or '\r')) lineStart--;
        if (!string.IsNullOrWhiteSpace(state.Source[lineStart..fragment.Start])) text = state.NewLine + state.Indent + text;
        var end = fragment.Start + fragment.Length;
        var lineEnd = end;
        while (lineEnd < state.Source.Length && state.Source[lineEnd] is not ('\n' or '\r')) lineEnd++;
        if (!string.IsNullOrWhiteSpace(WmlInlineCommentSplitter.Split(state.Source[end..lineEnd]).Value))
            text += state.NewLine + state.Indent;
        return text;
    }
    private static string DefinitionIdentity(IEnumerable<WmlMacroExpansionDefinition> definitions) =>
        WccContentHash.ComputeText(string.Join("\n", definitions.Select(definition =>
            definition.Name + "|" + definition.SourcePath + "|" + definition.PackageRevisionId + "|" + definition.SourceStartLine + "|" + definition.TextDomain + "|" + definition.RawText)));
    private sealed record FootprintResult(bool PureSchedule, bool PureStart, bool ExcludesSchedule,
        IReadOnlyList<string> Times, IReadOnlyList<ScenarioInitialScheduleScalar> StartScalars,
        IReadOnlyList<WmlStaticScalarOwner> TimeOwners, WmlMacroExpansionDefinition? Definition, bool EngineMerge, string? MaterializationProblem,
        string? ExclusionProblem = null);
}
