using System.Collections.ObjectModel;
using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Bounded literal event classification and lossless in-memory rendering. Source authority,
/// macro expansion and persistence remain with the existing checked event recipe workflow.
/// </summary>
public static class WccScenarioEventGraphEditor
{
    public const int MaximumEventCharacters = 256 * 1024;
    public const int MaximumMessages = 128;
    public const int MaximumDifficultyRegions = 32;
    public const int MaximumTextCharacters = 32 * 1024;
    private static readonly ScenarioInitialFieldDefinition TextDefinition =
        ScenarioInitialFieldCatalog.ScenarioFields.Single(field => field.Key == "name");
    private static readonly ScenarioInitialFieldDefinition SpeakerDefinition =
        ScenarioInitialFieldCatalog.LeaderFields.Single(field => field.Key == "id");

    public static WccScenarioDialogueCaptureResult Capture(string eventWml,
        IReadOnlyList<string>? declaredDifficultyDefines = null,
        IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets = null)
    {
        ArgumentNullException.ThrowIfNull(eventWml);
        if (eventWml.Length > MaximumEventCharacters) return RefusedCapture("This event exceeds the bounded graph size; use RAW.");
        var analysis = WmlEditorAnalysis.Build(eventWml);
        var macroCalls = analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation).ToArray();
        if (analysis.TagOccurrences.Any(tag => !tag.IsComplete || tag.IsComputedName || tag.MatchingTagIndex is null || HasAmendmentPrefix(eventWml, tag)) ||
            analysis.Tokens.Any(token => token.Kind == WmlEditorTokenKind.RawCode) ||
            analysis.References.Count != macroCalls.Length || analysis.References.Any(reference =>
                !macroCalls.Any(call => call.StartOffset == reference.StartOffset && call.Length == reference.Length)) ||
            WccScenarioEventSourcePlanner.HasUnsafeControls(eventWml))
            return RefusedCapture("Only exact saved Unit preset calls inside Spawn actions can use the graph; other helpers and raw code retain RAW editing.");
        if (ValidateDeclaredDifficulties(declaredDifficultyDefines) is { } declarationProblem)
            return RefusedCapture(declarationProblem);
        string literalView;
        IReadOnlyList<WccDialogueDifficultySource> difficultySources;
        try { (literalView, difficultySources) = ReadDifficultyControls(eventWml, analysis, declaredDifficultyDefines); }
        catch (InvalidOperationException exception) { return RefusedCapture(exception.Message); }
        // Mask only already-recognized literal difficulty directives, retaining
        // every UTF-16 offset. All action branches are parsed without evaluating
        // or inventing a campaign environment; Save proves each real context.
        if (macroCalls.Length != 0)
        {
            var mask = literalView.ToCharArray();
            foreach (var call in macroCalls)
                for (var index = call.StartOffset; index < call.StartOffset + call.Length; index++)
                    if (mask[index] is not ('\r' or '\n')) mask[index] = ' ';
            literalView = new string(mask);
        }
        if (difficultySources.Count != 0 || macroCalls.Length != 0) analysis = WmlEditorAnalysis.Build(literalView);
        if (analysis.TagOccurrences.Count > 4 + MaximumMessages * 12 ||
            analysis.TagOccurrences.Any(tag => !tag.IsComplete || tag.IsComputedName || tag.MatchingTagIndex is null ||
                HasAmendmentPrefix(eventWml, tag) || tag.Name is not ("event" or "message" or "filter" or "move_unit" or "endlevel" or "delay" or "scroll_to" or "gold" or "unit" or "modifications" or "trait" or "effect")))
            return RefusedCapture("Only supported literal actions and trigger filters can use the graph; additional WML retains RAW editing.");
        var validation = WmlSingleBlockValidator.Validate(literalView, "event");
        if (!validation.IsValid) return RefusedCapture(validation.Problem ?? "The event is not one complete literal block.");
        try
        {
            // Reuse the scalar parser over an opaque literal view. This creates no authored
            // source authority, disk capture, include resolver or campaign evaluation.
            var source = new WmlStaticSource("wcc-event-literal-preview", eventWml, isAuthored: false);
            var view = new WmlStaticEvaluationResult(literalView,
                [new WmlStaticOutputSegment(0, literalView, new(source, 0, eventWml.Length), [])], [], [source], [], true);
            var projection = WmlStaticScalarProjection.Create(view);
            var roots = projection.Owners.Where(owner => owner.Parent is null).ToArray();
            if (projection.Diagnostics.Count != 0 || roots.Length != 1 || roots[0].TagName != "event")
                return RefusedCapture("The event contains malformed or unsupported literal source; use RAW.");
            var owner = roots[0];
            var root = ReadNode(owner, eventWml, ["name", "first_time_only", "id"]);
            if (!root.Fields.TryGetValue("name", out var name) || !TryReadTrigger(ReadLiteral(name), out var trigger, out var turn))
                return RefusedCapture("This event trigger is outside the supported graph slice; use RAW.");
            var firstTimeOnly = true;
            if (root.Fields.TryGetValue("first_time_only", out var first))
            {
                var value = ReadLiteral(first);
                if (value is not ("yes" or "no" or "true" or "false")) return RefusedCapture("The event repeat setting is unsupported; use RAW.");
                firstTimeOnly = value is "yes" or "true";
            }
            var filters = owner.DirectChildren.Where(child => child.TagName == "filter").ToArray();
            if (filters.Length > 1 || filters.Any(filter => filter.DirectChildren.Count != 0))
                return RefusedCapture("Additional or nested trigger filters retain RAW editing.");
            WccDialogueLiteralNode? filterSource = filters.Length == 0 ? null : ReadNode(filters[0], eventWml, ["id", "x", "y"]);
            var children = owner.DirectChildren.Where(child => child.TagName != "filter").ToArray();
            if (children.Length > MaximumMessages) return RefusedCapture("This event has too many action nodes; use RAW.");
            var actions = new List<WccScenarioEventAction>();
            var sources = new Dictionary<Guid, WccDialogueLiteralNode>();
            foreach (var child in children)
            {
                if (child.DirectChildren.Count != 0 && child.TagName != "unit") return RefusedCapture("Nested action children, choices and branches retain RAW editing.");
                var (action, node) = child.TagName == "unit" ? ReadUnitAction(child, eventWml, unitPresets) : ReadAction(child, eventWml);
                if (ValidateAction(action) is { } problem) return RefusedCapture(problem + " Use RAW.");
                actions.Add(action);
                sources.Add(action.Key, node);
            }
            if (macroCalls.Length != actions.Count(action => action.Kind == WccScenarioEventActionKind.SpawnUnit && action.Unit?.Preset is not null))
                return RefusedCapture("A saved Unit preset call must belong to exactly one supported Spawn action; other helper uses retain RAW editing.");
            var regions = CaptureDifficultyMembership(difficultySources, actions, sources, root, filterSource);
            var graph = new WccScenarioEventGraph(trigger, actions, firstTimeOnly, regions)
            {
                EventId = OptionalLiteral(root, "id"), TurnNumber = turn,
                FilterUnitId = filterSource is null ? null : OptionalLiteral(filterSource, "id"),
                FilterX = filterSource is null ? null : OptionalLiteral(filterSource, "x"),
                FilterY = filterSource is null ? null : OptionalLiteral(filterSource, "y")
            };
            if (ValidateTrigger(graph) is { } triggerProblem) return RefusedCapture(triggerProblem + " Use RAW.");
            if ((trigger is WccScenarioEventTrigger.Die or WccScenarioEventTrigger.MoveTo) != (filterSource is not null))
                return RefusedCapture("This trigger needs its supported literal filter and no additional filter; use RAW.");
            if (ValidateDifficultyRegions(graph, declaredDifficultyDefines) is { } regionProblem) return RefusedCapture(regionProblem + " Use RAW.");
            var indent = LeadingIndent(eventWml, owner.OpeningOrigin!.StartOffset);
            var newline = eventWml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : eventWml.Contains('\n') ? "\n" : "\r";
            return new(new(eventWml, graph, root, sources, filterSource, indent, newline, difficultySources), null);
        }
        catch (InvalidOperationException exception) { return RefusedCapture(exception.Message); }
    }

    /// <summary>A display-only graph of one exact captured evaluated event.
    /// Earlier shared/default assignments are hidden only in this detached value
    /// view. This seed is not authored source and grants no persistence authority.</summary>
    public static WccScenarioDialogueCaptureResult CaptureEvaluated(WmlStaticScalarProjection projection,
        WmlStaticScalarOwner owner)
    {
        ArgumentNullException.ThrowIfNull(projection); ArgumentNullException.ThrowIfNull(owner);
        if (projection.SourceEvaluation is not { IsComplete: true } evaluation ||
            !projection.Owners.Any(current => ReferenceEquals(current, owner)) ||
            owner.TagName != "event" || owner.IsAmended || owner.HasUnresolvedContributions ||
            owner.DirectChildren.Count > MaximumMessages + 1 ||
            owner.OpeningOutputOffset is not { } first || owner.ClosingOutputOffset is not { } last ||
            owner.ClosingOrigin is not { } closing || first < 0 || last < first ||
            closing.Length <= 0 || last > evaluation.Text.Length - closing.Length ||
            last + closing.Length - first > MaximumEventCharacters)
            return RefusedCapture("The evaluated event needs its exact complete captured projection and bounded output occurrence.");
        var end = last + closing.Length;
        var masks = new List<(int Start, int Length)>();
        foreach (var unit in owner.DirectChildren.Where(child => child.TagName == "unit"))
        {
            if (unit.IsAmended || unit.HasUnresolvedContributions || unit.Fields.Count > 129 ||
                unit.OpeningOutputOffset is not { } unitStart || unit.ClosingOutputOffset is not { } unitClose ||
                unit.OpeningOrigin is not { } unitOpening || unit.ClosingOrigin is not { } unitClosing ||
                unitStart < first || unitClose < unitStart + unitOpening.Length ||
                unitClosing.Length <= 0 || unitClose > end - unitClosing.Length)
                return RefusedCapture("An evaluated Spawn Unit has no complete bounded action output.");
            foreach (var pair in unit.Fields.Where(pair => pair.Value.Contributions.Count > 1))
            {
                if (!WccScenarioUnitInstancePlanner.IsEditableKey(pair.Key) || !pair.Value.IsKnown ||
                    pair.Value.Contributions.Count != 2)
                    return RefusedCapture("Only one shared Unit default followed by one literal field can form a bounded winning-value display.");
                var previousEnd = unitStart + unitOpening.Length;
                for (var index = 0; index < pair.Value.Contributions.Count; index++)
                {
                    var contribution = pair.Value.Contributions[index];
                    var start = contribution.AssignmentOutputOffset; var length = contribution.AssignmentOutputLength;
                    if (contribution.AssignmentOrigin is not { } assignment || contribution.AssignmentOrigins.Count != 1 ||
                        contribution.CompositeAssignmentKeys.Count != 0 || !contribution.ValueMappingIsExact ||
                        length <= 0 || start < previousEnd || start > unitClose - length ||
                        assignment.Length != length || contribution.AssignmentOrigins[0].StartOffset != assignment.StartOffset ||
                        contribution.AssignmentOrigins[0].Length != assignment.Length ||
                        contribution.AssignmentOrigins[0].Source.ContentHash != assignment.Source.ContentHash ||
                        contribution.AssignmentOrigins[0].Source.Path != assignment.Source.Path ||
                        !evaluation.Text.AsSpan(start, length).SequenceEqual(assignment.Source.Text.AsSpan(assignment.StartOffset, assignment.Length)))
                        return RefusedCapture("An evaluated Unit override has no exact ordered assignment output span.");
                    if (index < pair.Value.Contributions.Count - 1) masks.Add((start, length));
                    previousEnd = start + length;
                }
            }
        }
        if (masks.Count == 0) return Capture(evaluation.Text[first..end]);
        var text = evaluation.Text[first..end].ToCharArray();
        var previousMaskEnd = first;
        foreach (var mask in masks.OrderBy(mask => mask.Start))
        {
            if (mask.Start < previousMaskEnd)
                return RefusedCapture("Evaluated Unit field display spans overlap.");
            for (var index = mask.Start; index < mask.Start + mask.Length; index++)
                if (text[index - first] is not ('\r' or '\n')) text[index - first] = ' ';
            previousMaskEnd = mask.Start + mask.Length;
        }
        return Capture(new string(text));
    }
    public static WccScenarioDialogueRenderResult Render(WccScenarioEventGraph graph,
        WccScenarioEventGraphSeed? seed = null, string indent = "    ", string newline = "\n",
        IReadOnlyList<string>? declaredDifficultyDefines = null,
        IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(indent);
        ArgumentNullException.ThrowIfNull(newline);
        if (ValidateTrigger(graph) is { } triggerProblem) return new(null, triggerProblem);
        if (graph.Actions.Count > MaximumMessages || graph.Actions.Any(action => action is null || action.Key == Guid.Empty) ||
            graph.Actions.Select(action => action.Key).Distinct().Count() != graph.Actions.Count)
            return new(null, "The event chain has too many actions or duplicate node identities.");
        if (ValidateDeclaredDifficulties(declaredDifficultyDefines) is { } declarationProblem) return new(null, declarationProblem);
        if (ValidateDifficultyRegions(graph, declaredDifficultyDefines) is { } regionProblem) return new(null, regionProblem);
        foreach (var action in graph.Actions)
            if (ValidateAction(action) is { } problem) return new(null, problem);
        if (ValidateUnitPresetReferences(graph, unitPresets) is { } presetProblem) return new(null, presetProblem);
        if (indent.Any(character => character is not (' ' or '\t')) || newline is not ("\n" or "\r\n" or "\r"))
            return new(null, "Use literal whitespace indentation and a supported line ending.");
        // Null loyal state retains a captured local unit's current canonical trait.
        // Resolve only that existing action identity; new units retain the shared
        // serializer's absent-trait default.
        if (seed is not null)
        {
            var retained = graph.Actions.Select(action =>
            {
                if (action.Kind != WccScenarioEventActionKind.SpawnUnit || action.Unit is not { IsLoyal: null, Preset: null } unit)
                    return action;
                var original = seed.Graph.Actions.SingleOrDefault(item => item.Key == action.Key);
                return original?.Kind == WccScenarioEventActionKind.SpawnUnit && original.Unit is { Preset: null } previous
                    ? action with { Unit = new WccScenarioUnitInstanceSpec(unit.Values) { IsLoyal = previous.IsLoyal } } : action;
            }).ToArray();
            graph = new(graph.Trigger, retained, graph.FirstTimeOnly, graph.DifficultyRegions)
            {
                EventId = graph.EventId, TurnNumber = graph.TurnNumber, FilterUnitId = graph.FilterUnitId,
                FilterX = graph.FilterX, FilterY = graph.FilterY
            };
        }
        if (seed is not null && SameGraph(graph, seed.Graph)) return new(seed.OriginalText, null);
        try
        {
            var rendered = seed is null ? RenderNew(graph, indent, newline) : RenderCaptured(graph, seed);
            if (rendered.Length > MaximumEventCharacters) return new(null, "This graph exceeds the bounded event size.");
            var captured = Capture(rendered, declaredDifficultyDefines, unitPresets);
            if (!captured.CanEdit || !SameGraphValues(graph, captured.Seed!.Graph))
                return new(null, captured.Diagnostic ?? "The rendered event did not preserve its requested trigger and action chain.");
            return new(rendered, null);
        }
        catch (ArgumentException exception) { return new(null, exception.Message); }
        catch (InvalidOperationException exception) { return new(null, exception.Message); }
    }

    private static (WccScenarioEventAction Action, WccDialogueLiteralNode Node) ReadUnitAction(
        WmlStaticScalarOwner owner, string raw, IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets)
    {
        var allowed = ScenarioInitialFieldCatalog.LeaderFields.Where(field => WccScenarioUnitInstancePlanner.IsEditableKey(field.Key))
            .Select(field => field.Key).Append("side").ToArray();
        var node = ReadNode(owner, raw, allowed);
        var problem = WccScenarioUnitInstancePlanner.CaptureEventUnit(raw[node.Start..node.End], unitPresets,
            out var unit, out var side, out _, out _);
        if (problem is not null || unit is null) throw new InvalidOperationException(problem ?? "The Spawn Unit is not one supported literal unit.");
        return (new(WccScenarioEventActionKind.SpawnUnit) { Unit = unit, Side = side }, node);
    }
    private static (WccScenarioEventAction Action, WccDialogueLiteralNode Node) ReadAction(WmlStaticScalarOwner owner, string raw)
    {
        if (owner.TagName == "message")
        {
            var node = ReadNode(owner, raw, ["speaker", "message", "caption"]);
            if (!node.Fields.TryGetValue("speaker", out var speaker) || !node.Fields.TryGetValue("message", out var text))
                throw new InvalidOperationException("Each message needs a literal speaker and text; other forms retain RAW editing.");
            var caption = node.Fields.GetValueOrDefault("caption");
            return (new(WccScenarioEventActionKind.Message)
            {
                Speaker = ReadLiteral(speaker), Text = DialogueText(text.RawValue),
                Caption = caption is null ? null : DialogueText(caption.RawValue),
                TranslateText = Translated(text.RawValue), TranslateCaption = caption is null || Translated(caption.RawValue)
            }, node);
        }
        if (owner.TagName == "move_unit")
        {
            // The native action accepts a unit filter inline, unlike an event's [filter].
            var node = ReadNode(owner, raw, ["id", "to_x", "to_y"]);
            if (!node.Fields.TryGetValue("id", out var id) || !node.Fields.TryGetValue("to_x", out var x) ||
                !node.Fields.TryGetValue("to_y", out var y) || !TryPositiveInteger(ReadLiteral(x), out var toX) ||
                !TryPositiveInteger(ReadLiteral(y), out var toY))
                throw new InvalidOperationException("A supported move action needs one literal unit ID and a positive destination pair.");
            return (new(WccScenarioEventActionKind.MoveUnit) { UnitId = ReadLiteral(id), ToX = toX, ToY = toY }, node);
        }
        if (owner.TagName == "endlevel")
        {
            var node = ReadNode(owner, raw, ["result"]);
            var result = OptionalLiteral(node, "result") ?? "victory";
            if (result is not ("victory" or "defeat")) throw new InvalidOperationException("The scenario result must be victory or defeat.");
            return (new(WccScenarioEventActionKind.EndLevel)
            { Result = result == "victory" ? WccScenarioEventResult.Victory : WccScenarioEventResult.Defeat }, node);
        }
        if (owner.TagName == "delay")
        {
            var node = ReadNode(owner, raw, ["time"]);
            if (!node.Fields.TryGetValue("time", out var time) || !TryPositiveInteger(ReadLiteral(time), out var duration))
                throw new InvalidOperationException("A wait action needs one positive literal duration in milliseconds.");
            return (new(WccScenarioEventActionKind.Wait) { DelayMilliseconds = duration }, node);
        }
        if (owner.TagName == "scroll_to")
        {
            var node = ReadNode(owner, raw, ["x", "y"]);
            if (!node.Fields.TryGetValue("x", out var x) || !node.Fields.TryGetValue("y", out var y) ||
                !TryPositiveInteger(ReadLiteral(x), out var toX) || !TryPositiveInteger(ReadLiteral(y), out var toY))
                throw new InvalidOperationException("A scroll action needs one positive literal coordinate pair.");
            return (new(WccScenarioEventActionKind.ScrollCamera) { ToX = toX, ToY = toY }, node);
        }
        if (owner.TagName == "gold")
        {
            var node = ReadNode(owner, raw, ["side", "amount"]);
            if (!node.Fields.TryGetValue("side", out var side) || !node.Fields.TryGetValue("amount", out var amount) ||
                !TryPositiveInteger(ReadLiteral(side), out var sideNumber) ||
                !int.TryParse(ReadLiteral(amount), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var adjustment))
                throw new InvalidOperationException("A gold action needs one explicit positive side and a signed integer amount.");
            return (new(WccScenarioEventActionKind.AdjustGold) { Side = sideNumber, Amount = adjustment }, node);
        }
        throw new InvalidOperationException("This action is outside the supported graph slice; use RAW.");
    }

    private static WccDialogueLiteralNode ReadNode(WmlStaticScalarOwner owner, string raw, string[] allowed)
    {
        if (owner.IsAmended || owner.HasUnresolvedContributions || owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing)
            throw new InvalidOperationException("Amended, unresolved or malformed event nodes retain RAW editing.");
        if (!raw.AsSpan(LineStart(raw, opening.StartOffset), opening.StartOffset - LineStart(raw, opening.StartOffset)).Trim().IsEmpty ||
            !raw.AsSpan(LineStart(raw, closing.StartOffset), closing.StartOffset - LineStart(raw, closing.StartOffset)).Trim().IsEmpty)
            throw new InvalidOperationException("An event node needs separate literal opening and closing lines; compact source retains RAW editing.");
        var fields = new Dictionary<string, WccDialogueLiteralField>(StringComparer.Ordinal);
        foreach (var (key, field) in owner.Fields)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal) || !field.IsKnown || field.Contributions.Count != 1 ||
                field.WinningContribution is not { AssignmentOrigin: { } assignment, ValueMappingIsExact: true } contribution ||
                contribution.CompositeAssignmentKeys.Count != 0 || field.ValueText is null ||
                !(owner.TagName == "message" && (key is "message" or "caption")
                    ? WccWmlPlainTextCodec.TryReadLiteral(field.ValueText, out _, out _)
                    : WccScenarioFieldValueCodec.IsSupportedLiteralWml(field.ValueText)))
                throw new InvalidOperationException("Additional, repeated, composite or dynamic field '" + key + "' retains RAW editing.");
            fields.Add(key, new(field.ValueText, contribution.ValueOutputOffset, contribution.ValueOutputLength, assignment.StartOffset, assignment.Length));
        }
        return new(LineStart(raw, opening.StartOffset), closing.StartOffset + closing.Length,
            LineStart(raw, closing.StartOffset), new ReadOnlyDictionary<string, WccDialogueLiteralField>(fields));
    }

    private static string RenderCaptured(WccScenarioEventGraph graph, WccScenarioEventGraphSeed seed)
    {
        var patches = new List<WccScenarioFieldSourcePatch>();
        if (graph.Trigger != seed.Graph.Trigger || graph.TurnNumber != seed.Graph.TurnNumber)
            SetField(patches, seed.Root, "name", EncodeSpeaker(TriggerText(graph)), seed.Indent + "    ", seed.Newline);
        if (graph.FirstTimeOnly != seed.Graph.FirstTimeOnly)
            SetField(patches, seed.Root, "first_time_only", graph.FirstTimeOnly ? "yes" : "no", seed.Indent + "    ", seed.Newline);
        if (graph.EventId != seed.Graph.EventId)
            SetOptionalField(patches, seed.Root, "id", graph.EventId, seed.Indent + "    ", seed.Newline);
        RenderCapturedFilter(graph, seed, patches);
        if ((graph.DifficultyRegions.Count != 0 || seed.Graph.DifficultyRegions.Count != 0) &&
            (!SameRegions(graph, seed.Graph) || !graph.Actions.Select(action => action.Key).SequenceEqual(seed.Graph.Actions.Select(action => action.Key))))
        {
            RenderCapturedRegionActions(graph, seed, patches);
            return Patch(seed.OriginalText, patches);
        }
        var sources = seed.Graph.Actions.Select(action => seed.MessageSources[action.Key]).ToArray();
        var rendered = graph.Actions.Select(action => seed.MessageSources.TryGetValue(action.Key, out var original)
            ? RenderCapturedAction(action, original, seed) : RenderAction(action, seed.Indent + "    ", seed.Newline)).ToArray();
        for (var index = 0; index < sources.Length; index++)
            patches.Add(new(sources[index].Start, sources[index].End - sources[index].Start, index < rendered.Length ? rendered[index] : ""));
        if (rendered.Length > sources.Length)
        {
            var extras = string.Join(seed.Newline, rendered.Skip(sources.Length));
            patches.Add(sources.Length == 0 ? new(seed.Root.ClosingLineStart, 0, extras + seed.Newline)
                : new(sources[^1].End, 0, seed.Newline + extras));
        }
        return Patch(seed.OriginalText, patches);
    }

    private static void RenderCapturedFilter(WccScenarioEventGraph graph, WccScenarioEventGraphSeed seed,
        ICollection<WccScenarioFieldSourcePatch> patches)
    {
        var needsFilter = graph.Trigger is WccScenarioEventTrigger.Die or WccScenarioEventTrigger.MoveTo;
        if (seed.FilterSource is { } filter)
        {
            if (!needsFilter) { patches.Add(new(filter.Start, filter.End - filter.Start, "")); return; }
            var indent = LeadingIndent(seed.OriginalText, filter.Start) + "    ";
            if (graph.FilterUnitId != seed.Graph.FilterUnitId) SetOptionalField(patches, filter, "id", graph.FilterUnitId, indent, seed.Newline);
            if (graph.FilterX != seed.Graph.FilterX) SetOptionalField(patches, filter, "x", graph.FilterX, indent, seed.Newline);
            if (graph.FilterY != seed.Graph.FilterY) SetOptionalField(patches, filter, "y", graph.FilterY, indent, seed.Newline);
        }
        else if (needsFilter)
        {
            var insertion = seed.Graph.Actions.Count == 0 ? seed.Root.ClosingLineStart : seed.MessageSources[seed.Graph.Actions[0].Key].Start;
            if (seed.DifficultySources.Count != 0) insertion = Math.Min(insertion, seed.DifficultySources.Min(source => source.OpenStart));
            patches.Add(new(insertion, 0, RenderFilter(graph, seed.Indent + "    ", seed.Newline) + seed.Newline));
        }
    }

    private static string RenderCapturedAction(WccScenarioEventAction action, WccDialogueLiteralNode node, WccScenarioEventGraphSeed seed)
    {
        var original = seed.Graph.Actions.Single(item => item.Key == action.Key);
        var nodeIndent = LeadingIndent(seed.OriginalText, node.Start);
        if (action.Kind != original.Kind) return RenderAction(action, nodeIndent, seed.Newline);
        if (action.Kind == WccScenarioEventActionKind.SpawnUnit)
            return WccScenarioUnitInstancePlanner.RenderEventUnit(action.Unit!, action.Side!.Value, nodeIndent,
                seed.Newline, seed.OriginalText[node.Start..node.End], original.Unit);
        var patches = new List<WccScenarioFieldSourcePatch>();
        var indent = nodeIndent + "    ";
        if (action.Kind == WccScenarioEventActionKind.Message)
        {
            if (action.Speaker != original.Speaker) SetField(patches, node, "speaker", EncodeSpeaker(action.Speaker!), indent, seed.Newline);
            if (action.Text != original.Text || action.TranslateText != original.TranslateText)
                SetField(patches, node, "message", EncodeText(action.Text!, action.TranslateText, node.Fields["message"].RawValue, seed.Newline), indent, seed.Newline);
            if (action.Caption != original.Caption || action.TranslateCaption != original.TranslateCaption && action.Caption is not null)
            {
                if (action.Caption is null) RemoveField(patches, node, "caption");
                else SetField(patches, node, "caption", EncodeText(action.Caption, action.TranslateCaption, node.Fields.GetValueOrDefault("caption")?.RawValue, seed.Newline), indent, seed.Newline);
            }
        }
        else if (action.Kind == WccScenarioEventActionKind.MoveUnit)
        {
            if (action.UnitId != original.UnitId) SetField(patches, node, "id", EncodeSpeaker(action.UnitId!), indent, seed.Newline);
            if (action.ToX != original.ToX) SetField(patches, node, "to_x", action.ToX!.Value.ToString(CultureInfo.InvariantCulture), indent, seed.Newline);
            if (action.ToY != original.ToY) SetField(patches, node, "to_y", action.ToY!.Value.ToString(CultureInfo.InvariantCulture), indent, seed.Newline);
        }
        else if (action.Kind == WccScenarioEventActionKind.Wait)
        {
            if (action.DelayMilliseconds != original.DelayMilliseconds)
                SetField(patches, node, "time", action.DelayMilliseconds!.Value.ToString(CultureInfo.InvariantCulture), indent, seed.Newline);
        }
        else if (action.Kind == WccScenarioEventActionKind.ScrollCamera)
        {
            if (action.ToX != original.ToX) SetField(patches, node, "x", action.ToX!.Value.ToString(CultureInfo.InvariantCulture), indent, seed.Newline);
            if (action.ToY != original.ToY) SetField(patches, node, "y", action.ToY!.Value.ToString(CultureInfo.InvariantCulture), indent, seed.Newline);
        }
        else if (action.Kind == WccScenarioEventActionKind.AdjustGold)
        {
            if (action.Side != original.Side) SetField(patches, node, "side", action.Side!.Value.ToString(CultureInfo.InvariantCulture), indent, seed.Newline);
            if (action.Amount != original.Amount) SetField(patches, node, "amount", action.Amount!.Value.ToString(CultureInfo.InvariantCulture), indent, seed.Newline);
        }
        else if (action.Result != original.Result)
            SetField(patches, node, "result", ResultText(action.Result), indent, seed.Newline);
        return Patch(seed.OriginalText[node.Start..node.End], patches.Select(patch => patch with { Start = patch.Start - node.Start }));
    }

    private static void SetField(ICollection<WccScenarioFieldSourcePatch> patches, WccDialogueLiteralNode node,
        string key, string encoded, string indent, string newline)
    {
        if (node.Fields.TryGetValue(key, out var field)) patches.Add(new(field.ValueStart, field.ValueLength, encoded));
        else patches.Add(new(node.ClosingLineStart, 0, indent + key + "=" + encoded + newline));
    }
    private static void SetOptionalField(ICollection<WccScenarioFieldSourcePatch> patches, WccDialogueLiteralNode node,
        string key, string? value, string indent, string newline)
    {
        if (value is null) RemoveField(patches, node, key);
        else SetField(patches, node, key, EncodeSpeaker(value), indent, newline);
    }
    private static void RemoveField(ICollection<WccScenarioFieldSourcePatch> patches, WccDialogueLiteralNode node, string key)
    {
        if (node.Fields.TryGetValue(key, out var field)) patches.Add(new(field.AssignmentStart, field.AssignmentLength, ""));
    }

    private static string RenderNew(WccScenarioEventGraph graph, string indent, string newline) =>
        indent + "[event]" + newline + indent + "    name=" + TriggerText(graph) + newline +
        (graph.EventId is null ? "" : indent + "    id=" + EncodeSpeaker(graph.EventId) + newline) +
        indent + "    first_time_only=" + (graph.FirstTimeOnly ? "yes" : "no") + newline +
        (graph.Trigger is WccScenarioEventTrigger.Die or WccScenarioEventTrigger.MoveTo ? RenderFilter(graph, indent + "    ", newline) + newline : "") +
        string.Concat(graph.Actions.Select((action, index) => RenderRegionOpenings(graph, index, indent + "    ", newline) +
            RenderAction(action, indent + "    ", newline) + newline + RenderRegionClosings(graph, index, indent + "    ", newline))) + indent + "[/event]";

    private static string RenderFilter(WccScenarioEventGraph graph, string indent, string newline) =>
        indent + "[filter]" + newline +
        (graph.FilterUnitId is null ? "" : indent + "    id=" + EncodeSpeaker(graph.FilterUnitId) + newline) +
        (graph.FilterX is null ? "" : indent + "    x=" + EncodeSpeaker(graph.FilterX) + newline) +
        (graph.FilterY is null ? "" : indent + "    y=" + EncodeSpeaker(graph.FilterY) + newline) + indent + "[/filter]";

    private static string RenderAction(WccScenarioEventAction action, string indent, string newline) => action.Kind switch
    {
        WccScenarioEventActionKind.SpawnUnit => WccScenarioUnitInstancePlanner.RenderEventUnit(action.Unit!, action.Side!.Value, indent, newline),
        WccScenarioEventActionKind.Message => indent + "[message]" + newline + indent + "    speaker=" + EncodeSpeaker(action.Speaker!) + newline +
            (action.Caption is null ? "" : indent + "    caption=" + EncodeText(action.Caption, action.TranslateCaption, null, newline) + newline) +
            indent + "    message=" + EncodeText(action.Text!, action.TranslateText, null, newline) + newline + indent + "[/message]",
        WccScenarioEventActionKind.MoveUnit => indent + "[move_unit]" + newline + indent + "    id=" + EncodeSpeaker(action.UnitId!) + newline +
            indent + "    to_x=" + action.ToX!.Value.ToString(CultureInfo.InvariantCulture) + newline +
            indent + "    to_y=" + action.ToY!.Value.ToString(CultureInfo.InvariantCulture) + newline + indent + "[/move_unit]",
        WccScenarioEventActionKind.EndLevel => indent + "[endlevel]" + newline + indent + "    result=" + ResultText(action.Result) + newline + indent + "[/endlevel]",
        WccScenarioEventActionKind.Wait => indent + "[delay]" + newline + indent + "    time=" +
            action.DelayMilliseconds!.Value.ToString(CultureInfo.InvariantCulture) + newline + indent + "[/delay]",
        WccScenarioEventActionKind.ScrollCamera => indent + "[scroll_to]" + newline + indent + "    x=" +
            action.ToX!.Value.ToString(CultureInfo.InvariantCulture) + newline + indent + "    y=" +
            action.ToY!.Value.ToString(CultureInfo.InvariantCulture) + newline + indent + "[/scroll_to]",
        WccScenarioEventActionKind.AdjustGold => indent + "[gold]" + newline + indent + "    side=" +
            action.Side!.Value.ToString(CultureInfo.InvariantCulture) + newline + indent + "    amount=" +
            action.Amount!.Value.ToString(CultureInfo.InvariantCulture) + newline + indent + "[/gold]",
        _ => throw new InvalidOperationException("Unsupported event action kind.")
    };

    /// <summary>Projects explicit region membership for one declared context. This
    /// performs no evaluation and supplies no source or persistence authority.</summary>
    public static WccScenarioEventGraph ProjectDifficulty(WccScenarioEventGraph graph, string? difficultyDefine,
        IReadOnlyList<string> declaredDifficultyDefines)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(declaredDifficultyDefines);
        var problem = ValidateDeclaredDifficulties(declaredDifficultyDefines) ?? ValidateTrigger(graph);
        if (problem is not null) throw new InvalidOperationException(problem);
        if (graph.Actions.Count > MaximumMessages || graph.Actions.Any(action => action is null || action.Key == Guid.Empty) ||
            graph.Actions.Select(action => action.Key).Distinct().Count() != graph.Actions.Count)
            throw new InvalidOperationException("The event chain has too many actions or duplicate node identities.");
        foreach (var action in graph.Actions)
            if (ValidateAction(action) is { } actionProblem) throw new InvalidOperationException(actionProblem);
        if (ValidateDifficultyRegions(graph, declaredDifficultyDefines) is { } regionProblem)
            throw new InvalidOperationException(regionProblem);
        if (difficultyDefine is not null && !declaredDifficultyDefines.Contains(difficultyDefine, StringComparer.Ordinal))
            throw new InvalidOperationException("Choose one exact declared difficulty context.");
        var excluded = graph.DifficultyRegions.Where(region => region.DifficultyDefine != difficultyDefine)
            .SelectMany(region => region.ActionKeys).ToHashSet();
        return new(graph.Trigger, graph.Actions.Where(action => !excluded.Contains(action.Key)), graph.FirstTimeOnly)
        {
            EventId = graph.EventId, TurnNumber = graph.TurnNumber, FilterUnitId = graph.FilterUnitId,
            FilterX = graph.FilterX, FilterY = graph.FilterY
        };
    }

    /// <summary>Materializes captured Unit preset defaults and explicit local deltas.
    /// This pure projection resolves no source and grants no persistence authority.</summary>
    public static WccScenarioEventGraph ProjectUnitPresets(WccScenarioEventGraph graph,
        Func<WccCampaignLocalPresetEntry, WccScenarioUnitTemplate>? resolveTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Actions.Count > MaximumMessages || graph.Actions.Any(action => action is null || action.Key == Guid.Empty) ||
            graph.Actions.Select(action => action.Key).Distinct().Count() != graph.Actions.Count)
            throw new InvalidOperationException("The event chain has too many actions or duplicate node identities.");
        var actions = graph.Actions.Select(action =>
        {
            if (ValidateAction(action) is { } problem) throw new InvalidOperationException(problem);
            if (action.Kind != WccScenarioEventActionKind.SpawnUnit || action.Unit?.Preset is not { } preset) return action;
            var original = preset.Payload!.UnitTemplate!;
            var proposed = resolveTemplate?.Invoke(preset) ?? original;
            if (proposed.Values.GetValueOrDefault("id") != original.Values.GetValueOrDefault("id"))
                throw new InvalidOperationException("A saved Unit preset keeps its exact native unit ID.");
            var fields = proposed.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var pair in action.Unit.Values)
                if (original.Values.GetValueOrDefault(pair.Key) != pair.Value) fields[pair.Key] = pair.Value;
            return action with
            {
                Unit = new WccScenarioUnitInstanceSpec(fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    { IsLoyal = proposed.IsLoyal }
            };
        }).ToArray();
        return new(graph.Trigger, actions, graph.FirstTimeOnly, graph.DifficultyRegions)
        {
            EventId = graph.EventId, TurnNumber = graph.TurnNumber, FilterUnitId = graph.FilterUnitId,
            FilterX = graph.FilterX, FilterY = graph.FilterY
        };
    }

    private static string? ValidateUnitPresetReferences(WccScenarioEventGraph graph,
        IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets)
    {
        foreach (var action in graph.Actions)
        {
            if (action.Kind != WccScenarioEventActionKind.SpawnUnit || action.Unit?.Preset is not { } selected) continue;
            var candidates = (unitPresets ?? []).Where(preset => preset.Name == selected.Name).ToArray();
            if (candidates.Length != 1 || !WccScenarioUnitInstancePlanner.SameEventUnitPreset(selected, candidates[0]))
                return "This Spawn Unit preset no longer matches one exact captured choice; reopen the graph from the current preset catalog.";
        }
        return null;
    }
    private static string? ValidateDeclaredDifficulties(IReadOnlyList<string>? declared)
    {
        if (declared is null) return null;
        return declared.Count > 16 || declared.Any(symbol => string.IsNullOrEmpty(symbol) || symbol.Length > 128 ||
            symbol.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')) ||
            declared.Distinct(StringComparer.Ordinal).Count() != declared.Count
            ? "Difficulty regions need one bounded list of unique literal declared difficulty symbols." : null;
    }

    private static (string LiteralView, IReadOnlyList<WccDialogueDifficultySource> Sources) ReadDifficultyControls(
        string raw, WmlEditorAnalysis analysis, IReadOnlyList<string>? declared)
    {
        var directives = analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive).ToArray();
        if (directives.Length == 0) return (raw, []);
        var mask = raw.ToCharArray();
        var stack = new Stack<(Guid Key, string Define, int Start, int End, Guid? Parent)>();
        var sources = new List<WccDialogueDifficultySource>();
        var openings = 0;
        foreach (var token in directives)
        {
            var words = raw.Substring(token.StartOffset, token.Length).Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 2 && words[0] == "#ifdef" && declared?.Contains(words[1], StringComparer.Ordinal) == true)
            {
                if (++openings > MaximumDifficultyRegions || stack.Count == 8)
                    throw new InvalidOperationException("This event exceeds the bounded difficulty-region count or nesting depth; use RAW.");
                stack.Push((Guid.NewGuid(), words[1], LineStart(raw, token.StartOffset),
                    AfterLine(raw, token.StartOffset + token.Length), stack.Count == 0 ? null : stack.Peek().Key));
            }
            else if (words.Length == 1 && words[0] == "#endif" && stack.TryPop(out var opened))
                sources.Add(new(opened.Key, opened.Define, opened.Start, opened.End,
                    LineStart(raw, token.StartOffset), AfterLine(raw, token.StartOffset + token.Length), opened.Parent));
            else throw new InvalidOperationException("Only balanced #ifdef regions for exact declared difficulties around whole actions can use the graph; other controls retain RAW editing.");
            for (var index = token.StartOffset; index < token.StartOffset + token.Length; index++)
                if (mask[index] is not ('\r' or '\n')) mask[index] = ' ';
        }
        if (stack.Count != 0) throw new InvalidOperationException("A difficulty region is missing its closing #endif; use RAW.");
        return (new string(mask), sources.OrderBy(source => source.OpenStart).ToArray());
    }

    private static IReadOnlyList<WccScenarioEventDifficultyRegion> CaptureDifficultyMembership(
        IReadOnlyList<WccDialogueDifficultySource> regions, IReadOnlyList<WccScenarioEventAction> actions,
        IReadOnlyDictionary<Guid, WccDialogueLiteralNode> nodes, WccDialogueLiteralNode root, WccDialogueLiteralNode? filter)
    {
        var captured = new List<WccScenarioEventDifficultyRegion>();
        foreach (var region in regions)
        {
            if (region.OpenStart <= root.Start || region.CloseEnd > root.ClosingLineStart ||
                root.Fields.Values.Any(field => Overlaps(region.OpenStart, region.CloseEnd, field.AssignmentStart,
                    field.AssignmentStart + field.AssignmentLength)) ||
                filter is not null && Overlaps(region.OpenStart, region.CloseEnd, filter.Start, filter.End))
                throw new InvalidOperationException("Difficulty regions may contain whole actions, not event metadata or trigger filters; use RAW.");
            var members = new List<Guid>();
            foreach (var action in actions)
            {
                var node = nodes[action.Key];
                if (!Overlaps(region.OpenStart, region.CloseEnd, node.Start, node.End)) continue;
                if (node.Start < region.OpenEnd || node.End > region.CloseStart)
                    throw new InvalidOperationException("Difficulty directives cannot split an action block or one of its fields; use RAW.");
                members.Add(action.Key);
            }
            captured.Add(new(region.Key, region.Define, members, region.ParentKey));
        }
        return captured;
    }

    private sealed record RegionInterval(WccScenarioEventDifficultyRegion Region, int Start, int End);
    private static IEnumerable<RegionInterval> RegionIntervals(WccScenarioEventGraph graph)
    {
        var positions = graph.Actions.Select((action, index) => (action.Key, index)).ToDictionary(pair => pair.Key, pair => pair.index);
        return graph.DifficultyRegions.Select(region => new RegionInterval(region,
            positions[region.ActionKeys[0]], positions[region.ActionKeys[^1]]));
    }

    private static string? ValidateDifficultyRegions(WccScenarioEventGraph graph, IReadOnlyList<string>? declared)
    {
        if (graph.DifficultyRegions.Count == 0) return null;
        if (declared is null || graph.DifficultyRegions.Count > MaximumDifficultyRegions ||
            graph.DifficultyRegions.Any(region => region is null || region.Key == Guid.Empty ||
                !declared.Contains(region.DifficultyDefine, StringComparer.Ordinal)) ||
            graph.DifficultyRegions.Select(region => region.Key).Distinct().Count() != graph.DifficultyRegions.Count)
            return "Each difficulty region needs a unique identity and one exact declared difficulty.";
        var positions = graph.Actions.Select((action, index) => (action.Key, index)).ToDictionary(pair => pair.Key, pair => pair.index);
        foreach (var region in graph.DifficultyRegions)
        {
            if (region.ActionKeys.Count == 0 || region.ActionKeys.Count > MaximumMessages ||
                region.ActionKeys.Distinct().Count() != region.ActionKeys.Count ||
                region.ActionKeys.Any(key => !positions.ContainsKey(key)))
                return "Each difficulty region needs explicit nonempty membership in this action chain.";
            var indices = region.ActionKeys.Select(key => positions[key]).ToArray();
            if (indices.Where((value, index) => value != indices[0] + index).Any())
                return "Connect the region's actions consecutively in the action chain, then move or resize the region to contain exactly that consecutive group.";
        }
        var intervals = RegionIntervals(graph).ToArray();
        foreach (var interval in intervals)
        {
            foreach (var other in intervals.Where(other => !ReferenceEquals(other, interval)))
                if (interval.Start <= other.End && other.Start <= interval.End &&
                    !StrictlyContains(interval, other) && !StrictlyContains(other, interval))
                    return "Difficulty regions must be disjoint or strictly nested; crossing and equal overlaps are unsupported.";
            var parent = intervals.Where(other => StrictlyContains(other, interval))
                .OrderBy(other => other.End - other.Start).FirstOrDefault();
            if (interval.Region.ParentRegionKey != parent?.Region.Key)
                return "A difficulty region must name its exact immediate enclosing region, or have no parent when disjoint.";
            if (parent is not null && parent.Region.DifficultyDefine != interval.Region.DifficultyDefine)
                return "Nested regions must select the same difficulty; different nested gates would make their actions unreachable.";
            var depth = intervals.Count(other => StrictlyContains(other, interval));
            if (depth >= 8) return "Difficulty regions exceed the bounded nesting depth.";
        }
        return null;
    }

    private static bool StrictlyContains(RegionInterval outer, RegionInterval inner) => outer.Start <= inner.Start &&
        outer.End >= inner.End && (outer.Start < inner.Start || outer.End > inner.End);
    private static bool Overlaps(int firstStart, int firstEnd, int secondStart, int secondEnd) => firstStart < secondEnd && secondStart < firstEnd;
    private static bool SameRegions(WccScenarioEventGraph first, WccScenarioEventGraph second) =>
        first.DifficultyRegions.Count == second.DifficultyRegions.Count && first.DifficultyRegions.All(region =>
            second.DifficultyRegions.SingleOrDefault(other => other.Key == region.Key) is { } other &&
            region.DifficultyDefine == other.DifficultyDefine && region.ParentRegionKey == other.ParentRegionKey &&
            region.ActionKeys.SequenceEqual(other.ActionKeys));
    private static IEnumerable<string> RegionShapes(WccScenarioEventGraph graph) => RegionIntervals(graph)
        .OrderBy(interval => interval.Start).ThenByDescending(interval => interval.End)
        .Select(interval => interval.Start.ToString(CultureInfo.InvariantCulture) + ":" + interval.End.ToString(CultureInfo.InvariantCulture) + ":" + interval.Region.DifficultyDefine);

    private static string RenderRegionOpenings(WccScenarioEventGraph graph, int actionIndex, string indent, string newline) =>
        graph.DifficultyRegions.Count == 0 ? "" : string.Concat(RegionIntervals(graph).Where(interval => interval.Start == actionIndex).OrderByDescending(interval => interval.End)
            .Select(interval => indent + "#ifdef " + interval.Region.DifficultyDefine + newline));
    private static string RenderRegionClosings(WccScenarioEventGraph graph, int actionIndex, string indent, string newline) =>
        graph.DifficultyRegions.Count == 0 ? "" : string.Concat(RegionIntervals(graph).Where(interval => interval.End == actionIndex).OrderByDescending(interval => interval.Start)
            .Select(_ => indent + "#endif" + newline));

    private static void RenderCapturedRegionActions(WccScenarioEventGraph graph, WccScenarioEventGraphSeed seed,
        ICollection<WccScenarioFieldSourcePatch> patches)
    {
        foreach (var source in seed.DifficultySources)
        {
            patches.Add(new(source.OpenStart, source.OpenEnd - source.OpenStart, ""));
            patches.Add(new(source.CloseStart, source.CloseEnd - source.CloseStart, ""));
        }
        var sources = seed.Graph.Actions.Select(action => seed.MessageSources[action.Key]).ToArray();
        var rendered = graph.Actions.Select(action => seed.MessageSources.TryGetValue(action.Key, out var original)
            ? RenderCapturedAction(action, original, seed) : RenderAction(action, seed.Indent + "    ", seed.Newline)).ToArray();
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index];
            if (index >= rendered.Length) { patches.Add(new(source.Start, source.End - source.Start, "")); continue; }
            var end = AfterLine(seed.OriginalText, source.End);
            var suffix = seed.OriginalText[source.End..end];
            var text = RenderRegionOpenings(graph, index, seed.Indent + "    ", seed.Newline) + rendered[index] + suffix +
                RenderRegionClosings(graph, index, seed.Indent + "    ", seed.Newline);
            patches.Add(new(source.Start, end - source.Start, text));
        }
        if (rendered.Length > sources.Length)
        {
            var extras = string.Concat(Enumerable.Range(sources.Length, rendered.Length - sources.Length).Select(index =>
                RenderRegionOpenings(graph, index, seed.Indent + "    ", seed.Newline) + rendered[index] + seed.Newline +
                RenderRegionClosings(graph, index, seed.Indent + "    ", seed.Newline)));
            patches.Add(new(sources.Length == 0 ? seed.Root.ClosingLineStart : AfterLine(seed.OriginalText, sources[^1].End), 0, extras));
        }
    }

    private static int AfterLine(string raw, int offset)
    {
        while (offset < raw.Length && raw[offset] is not ('\r' or '\n')) offset++;
        if (offset < raw.Length && raw[offset] == '\r') offset++;
        if (offset < raw.Length && raw[offset] == '\n') offset++;
        return offset;
    }
    private static string? ValidateTrigger(WccScenarioEventGraph graph)
    {
        if (!Enum.IsDefined(graph.Trigger) || graph.Trigger == WccScenarioEventTrigger.Unselected) return "Select a supported event trigger.";
        if (graph.EventId is not null && ValidateId(graph.EventId) is { } idProblem) return "The event ID is invalid: " + idProblem;
        if (graph.Trigger == WccScenarioEventTrigger.Turn ? graph.TurnNumber is null or <= 0 : graph.TurnNumber is not null)
            return "A turn trigger needs one positive turn number; other triggers cannot carry a turn number.";
        if (graph.Trigger == WccScenarioEventTrigger.Die)
        {
            if (ValidateId(graph.FilterUnitId) is { } problem) return "The death trigger needs one unit ID: " + problem;
            if (graph.FilterX is not null || graph.FilterY is not null) return "A death trigger cannot carry location fields in this graph slice.";
        }
        else if (graph.Trigger == WccScenarioEventTrigger.MoveTo)
        {
            if (graph.FilterUnitId is not null && ValidateId(graph.FilterUnitId) is { } problem) return "The movement trigger unit ID is invalid: " + problem;
            if (!ValidCoordinateFilter(graph.FilterX) || !ValidCoordinateFilter(graph.FilterY) ||
                graph.FilterX!.Split(',').Length != graph.FilterY!.Split(',').Length)
                return "A movement trigger needs positive X and Y coordinates, ranges or paired comma-separated lists with equal counts.";
        }
        else if (graph.FilterUnitId is not null || graph.FilterX is not null || graph.FilterY is not null)
            return "Only death and movement triggers can carry the supported unit/location filter.";
        return null;
    }

    private static string? ValidateAction(WccScenarioEventAction action)
    {
        if (!Enum.IsDefined(action.Kind) || !Enum.IsDefined(action.Result)) return "The action kind or scenario result is unsupported.";
        if (action.Kind != WccScenarioEventActionKind.Wait && action.DelayMilliseconds is not null ||
            action.Kind is not (WccScenarioEventActionKind.AdjustGold or WccScenarioEventActionKind.SpawnUnit) && action.Side is not null ||
            action.Kind != WccScenarioEventActionKind.AdjustGold && action.Amount is not null ||
            action.Kind != WccScenarioEventActionKind.SpawnUnit && action.Unit is not null)
            return "This action cannot silently discard another action's duration, side or gold settings.";
        if (action.Kind != WccScenarioEventActionKind.EndLevel && action.Result != WccScenarioEventResult.Victory)
            return "Only an end-level action can carry a scenario result.";
        if (action.Kind != WccScenarioEventActionKind.MoveUnit && action.UnitId is not null ||
            action.Kind is not (WccScenarioEventActionKind.MoveUnit or WccScenarioEventActionKind.ScrollCamera) &&
            (action.ToX is not null || action.ToY is not null))
            return "This action cannot silently discard unit movement or coordinate settings.";
        if (action.Kind == WccScenarioEventActionKind.SpawnUnit)
        {
            if (action.Unit is not { } unit || action.Side is null or <= 0)
                return "A Spawn Unit needs a shared unit form result and one explicit positive side.";
            if (unit.DifficultyDefine is not null)
                return "A Spawn Unit uses its graph difficulty region; clear the unit form's separate difficulty.";
            if (unit.Preset is { } preset)
            {
                if (preset.Role != WccCampaignLocalPresetRole.Unit || !preset.IsSupported || preset.Payload?.UnitTemplate is not { } template)
                    return "A Spawn Unit needs one supported saved Unit preset.";
                if (template.Values.Keys.Any(key => !unit.Values.ContainsKey(key)) ||
                    unit.Values.GetValueOrDefault("id") != template.Values.GetValueOrDefault("id"))
                    return "Keep the saved Unit preset fields and exact unit ID, or choose Custom for a different unit.";
                if (unit.IsLoyal is not null && unit.IsLoyal != template.IsLoyal)
                    return "Keep the saved Unit preset's loyal trait, or choose Custom for a local loyal trait.";
            }
        }
        if (action.Kind == WccScenarioEventActionKind.Message)
        {
            if (ValidateId(action.Speaker) is { } problem) return "Each message needs a literal speaker ID or narrator: " + problem;
            if (string.IsNullOrWhiteSpace(action.Text) || action.Text.Length > MaximumTextCharacters || action.Text.Contains('\0') ||
                action.Caption is { Length: > MaximumTextCharacters } || action.Caption?.Contains('\0') == true)
                return "Each message needs nonempty text within the bounded text size.";
            return WccScenarioFieldValueCodec.ValidateInput(TextDefinition, action.Text)
                ?? (action.Caption is null ? null : WccScenarioFieldValueCodec.ValidateInput(TextDefinition, action.Caption));
        }
        if (action.Speaker is not null || action.Text is not null || action.Caption is not null || !action.TranslateText || !action.TranslateCaption)
            return "This action cannot silently discard message settings.";
        if (action.Kind == WccScenarioEventActionKind.MoveUnit && ValidateId(action.UnitId) is { } idProblem)
            return "A move action needs one unit ID: " + idProblem;
        if (action.Kind is WccScenarioEventActionKind.MoveUnit or WccScenarioEventActionKind.ScrollCamera &&
            (action.ToX is null or <= 0 || action.ToY is null or <= 0))
            return "A move or scroll action needs one positive coordinate pair.";
        if (action.Kind == WccScenarioEventActionKind.Wait && action.DelayMilliseconds is null or <= 0)
            return "A wait action needs one positive duration in milliseconds.";
        if (action.Kind == WccScenarioEventActionKind.AdjustGold && (action.Side is null or <= 0 || action.Amount is null))
            return "A gold action needs one explicit positive side and a signed integer amount.";
        return null;
    }
    private static string? ValidateId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 512 || value.IndexOfAny(['\r', '\n', '\0', ',']) >= 0)
            return "Use one nonempty literal ID.";
        return WccScenarioFieldValueCodec.ValidateInput(SpeakerDefinition, value);
    }
    private static bool ValidCoordinateFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return false;
        var parts = value.Split(',');
        if (parts.Length > 64) return false;
        foreach (var part in parts)
        {
            var range = part.Trim().Split('-');
            if (range.Length is < 1 or > 2 || !TryPositiveInteger(range[0].Trim(), out var first) ||
                range.Length == 2 && (!TryPositiveInteger(range[1].Trim(), out var last) || last < first)) return false;
        }
        return true;
    }
    private static bool TryPositiveInteger(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    private static bool TryReadTrigger(string name, out WccScenarioEventTrigger trigger, out int? turn)
    {
        turn = null;
        trigger = name switch
        {
            "start" => WccScenarioEventTrigger.Start, "prestart" => WccScenarioEventTrigger.Prestart,
            "victory" => WccScenarioEventTrigger.Victory, "die" => WccScenarioEventTrigger.Die,
            "moveto" => WccScenarioEventTrigger.MoveTo, _ => WccScenarioEventTrigger.Unselected
        };
        if (trigger != WccScenarioEventTrigger.Unselected) return true;
        if (!name.StartsWith("turn ", StringComparison.Ordinal) || !TryPositiveInteger(name[5..], out var number)) return false;
        trigger = WccScenarioEventTrigger.Turn;
        turn = number;
        return true;
    }
    private static string TriggerText(WccScenarioEventGraph graph) => graph.Trigger switch
    {
        WccScenarioEventTrigger.Start => "start", WccScenarioEventTrigger.Prestart => "prestart",
        WccScenarioEventTrigger.Victory => "victory", WccScenarioEventTrigger.Turn => "turn " + graph.TurnNumber!.Value.ToString(CultureInfo.InvariantCulture),
        WccScenarioEventTrigger.Die => "die", WccScenarioEventTrigger.MoveTo => "moveto",
        _ => throw new InvalidOperationException("Select a supported event trigger.")
    };
    private static string ResultText(WccScenarioEventResult result) => result == WccScenarioEventResult.Victory ? "victory" : "defeat";
    private static string EncodeText(string text, bool translated, string? old, string newline) =>
        WccWmlPlainTextCodec.EncodePlainText(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", newline, StringComparison.Ordinal), translated, old);
    private static string EncodeSpeaker(string value) => WccScenarioFieldValueCodec.EncodeLiteral(SpeakerDefinition, value);
    private static string ReadLiteral(WccDialogueLiteralField field)
    {
        if (Translated(field.RawValue)) throw new InvalidOperationException("Identifiers, coordinates and trigger settings must be untranslated literals; use RAW.");
        return WccScenarioFieldValueCodec.DecodeLiteral(field.RawValue);
    }
    private static string? OptionalLiteral(WccDialogueLiteralNode node, string key) => node.Fields.TryGetValue(key, out var field) ? ReadLiteral(field) : null;
    private static string DialogueText(string raw) => WccWmlPlainTextCodec.TryReadLiteral(raw, out var text, out _)
        ? text : throw new InvalidOperationException("A message text value is not a complete bounded literal; use RAW.");
    private static bool Translated(string raw) => WccWmlPlainTextCodec.TryReadLiteral(raw, out _, out var translated)
        ? translated : raw.TrimStart().StartsWith('_') && raw.TrimStart()[1..].TrimStart().StartsWith('"');
    private static string Patch(string raw, IEnumerable<WccScenarioFieldSourcePatch> patches)
    {
        var all = patches.ToArray();
        // Replace an existing slot before inserting at its beginning; this also handles a new
        // trigger filter immediately before an edited first action without shifting its offsets.
        var combined = all.Where(patch => patch.Length != 0).Concat(all.Where(patch => patch.Length == 0)
            .GroupBy(patch => patch.Start).Select(group => new WccScenarioFieldSourcePatch(group.Key, 0,
                string.Concat(group.Select(patch => patch.ReplacementText)))));
        foreach (var patch in combined.OrderByDescending(patch => patch.Start).ThenByDescending(patch => patch.Length))
            raw = raw.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.ReplacementText);
        return raw;
    }
    private static bool SameMetadata(WccScenarioEventGraph left, WccScenarioEventGraph right) =>
        left.Trigger == right.Trigger && left.FirstTimeOnly == right.FirstTimeOnly && left.EventId == right.EventId &&
        left.TurnNumber == right.TurnNumber && left.FilterUnitId == right.FilterUnitId && left.FilterX == right.FilterX && left.FilterY == right.FilterY;
    private static bool SameGraph(WccScenarioEventGraph left, WccScenarioEventGraph right) =>
        SameMetadata(left, right) && left.Actions.Count == right.Actions.Count && left.Actions.Zip(right.Actions).All(pair =>
            (pair.First with { Unit = null }) == (pair.Second with { Unit = null }) &&
            WccScenarioUnitInstancePlanner.SameEventUnitSpec(pair.First.Unit, pair.Second.Unit)) && SameRegions(left, right);
    private static bool SameGraphValues(WccScenarioEventGraph left, WccScenarioEventGraph right) =>
        SameMetadata(left, right) && left.Actions.Count == right.Actions.Count && left.Actions.Zip(right.Actions).All(pair =>
            NormalizeAction(pair.First) == NormalizeAction(pair.Second) &&
            WccScenarioUnitInstancePlanner.SameEventUnitSpec(pair.First.Unit, pair.Second.Unit)) && RegionShapes(left).SequenceEqual(RegionShapes(right), StringComparer.Ordinal);
    private static WccScenarioEventAction NormalizeAction(WccScenarioEventAction action) => action with
    { Key = Guid.Empty, Unit = null, Text = NormalizeNewlines(action.Text), Caption = NormalizeNewlines(action.Caption),
        TranslateCaption = action.Caption is null || action.TranslateCaption };
    private static string? NormalizeNewlines(string? text) => text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static bool HasAmendmentPrefix(string raw, WmlEditorTagOccurrence tag)
    {
        var header = raw.AsSpan(tag.StartOffset + 1, tag.Length - 1).TrimStart();
        if (!header.IsEmpty && header[0] == '/') header = header[1..].TrimStart();
        return !header.IsEmpty && header[0] is '+' or '-';
    }
    private static int LineStart(string raw, int offset) { while (offset > 0 && raw[offset - 1] is not ('\r' or '\n')) offset--; return offset; }
    private static string LeadingIndent(string raw, int offset)
    { var start = LineStart(raw, offset); var end = start; while (end < raw.Length && raw[end] is ' ' or '\t') end++; return raw[start..end]; }
    private static WccScenarioDialogueCaptureResult RefusedCapture(string? diagnostic) => new(null, diagnostic);
}
