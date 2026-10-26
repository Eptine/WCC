using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum ScenarioEventSourceKind
{
    Literal,
    MacroGenerated,
    WesnothPreprocessed,
    CapturedStaticEvaluation
}

public enum ScenarioEventTriggerFamily
{
    Startup,
    TurnAndSide,
    LocationAndVision,
    Combat,
    UnitLifecycle,
    ScenarioFlow,
    Custom,
    DynamicOrInvalid
}

public enum ScenarioEventEffectKind
{
    Units,
    Sides,
    TerrainAndMap,
    TurnsAndTime,
    ObjectivesAndFlow,
    VariablesAndConditions,
    Events,
    NarrativeAndUi,
    Audio,
    UnknownOrLua
}

public sealed record ScenarioEventTrigger(
    string RawName,
    string NormalizedName,
    ScenarioEventTriggerFamily Family,
    bool IsDynamic);

public sealed record ScenarioEventTriggerLocation(
    string Role,
    ScenarioSpatialPositionState PositionState,
    IReadOnlyList<ScenarioMapCoordinate> Coordinates,
    string? RawX,
    string? RawY)
{
    public string DisplayText => PositionState == ScenarioSpatialPositionState.Exact
        ? $"{Role}: {string.Join(", ", Coordinates.Select(x => $"({x.X},{x.Y})"))}"
        : $"{Role}: unresolved x={RawX ?? "?"}, y={RawY ?? "?"}";
}

public sealed class ScenarioEventProjectionEntry
{
    public required string Id { get; init; }
    public required string TriggerDisplay { get; init; }
    public required IReadOnlyList<ScenarioEventTrigger> Triggers { get; init; }
    public required ScenarioEventSourceKind SourceKind { get; init; }
    public required string SourceDisplay { get; init; }
    public required string EffectiveRawWml { get; init; }
    public string? WmlId { get; init; }
    public string? FirstTimeOnly { get; init; }
    public string? Priority { get; init; }
    public string? DelayedVariableSubstitution { get; init; }
    public int ProjectedLine { get; init; }
    public int? AuthoritativeLine { get; init; }
    public int? AuthoritativeDirectEventOrdinal { get; init; }
    public bool IsNested { get; init; }
    public bool IsConditional { get; init; }
    public bool IsAmendment { get; init; }
    public string? ParentEventId { get; init; }
    public string? ParentTriggerDisplay { get; init; }
    public bool CanEditRaw { get; init; }
    public string? EditabilityReason { get; init; }
    public WmlMacroExpansionProvenance? MacroProvenance { get; init; }
    public IReadOnlyList<ScenarioEventTriggerLocation> TriggerLocations { get; init; } = [];
    public IReadOnlyList<string> FilterSummaries { get; init; } = [];
    public IReadOnlyList<ScenarioEventEffectKind> EffectKinds { get; init; } = [];
    public IReadOnlyList<string> ActionTagNames { get; init; } = [];

    public bool IsRepeating =>
        string.Equals(NormalizeScalar(FirstTimeOnly), "no", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(NormalizeScalar(FirstTimeOnly), "false", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(NormalizeScalar(FirstTimeOnly), "0", StringComparison.OrdinalIgnoreCase);

    public string DisplayTitle => string.IsNullOrWhiteSpace(WmlId)
        ? SourceKind == ScenarioEventSourceKind.CapturedStaticEvaluation ? $"{TriggerDisplay} · captured preview"
            : $"{TriggerDisplay} · line {AuthoritativeLine ?? ProjectedLine}"
        : $"{TriggerDisplay} · id={WmlId}";

    public bool HasSpatialTrigger => TriggerLocations.Count > 0;
    public bool HasExactSpatialTrigger => TriggerLocations.Any(x =>
        x.PositionState == ScenarioSpatialPositionState.Exact && x.Coordinates.Count > 0);

    public string TriggerFamilyDisplay => string.Join(", ", Triggers
        .Select(x => FormatTriggerFamily(x.Family))
        .Distinct(StringComparer.Ordinal));

    public string EffectDisplay => EffectKinds.Count == 0
        ? "No statically classified actions"
        : string.Join(", ", EffectKinds.Select(FormatEffectKind));

    public string ActionDisplay => ActionTagNames.Count == 0
        ? "No direct action tags"
        : string.Join(", ", ActionTagNames.Select(x => $"[{x}]"));

    public string FilterDisplay => FilterSummaries.Count == 0
        ? "No explicit trigger filters"
        : string.Join(" | ", FilterSummaries);

    public string LocationDisplay => TriggerLocations.Count == 0
        ? "No statically known trigger coordinate"
        : string.Join(" | ", TriggerLocations.Select(x => x.DisplayText));

    public string RepeatDisplay => IsRepeating ? "Repeating" : "First match only";

    public string NestingDisplay => IsNested
        ? $"Registered when parent event '{ParentTriggerDisplay ?? "unnamed"}' runs"
        : "Registered with the scenario";

    private static string? NormalizeScalar(string? value)
        => value is null ? null : WmlDisplayStringNormalizer.Normalize(value)?.Trim();

    private static string FormatTriggerFamily(ScenarioEventTriggerFamily family) => family switch
    {
        ScenarioEventTriggerFamily.TurnAndSide => "Turn / side",
        ScenarioEventTriggerFamily.LocationAndVision => "Location / vision",
        ScenarioEventTriggerFamily.UnitLifecycle => "Unit lifecycle",
        ScenarioEventTriggerFamily.ScenarioFlow => "Scenario flow",
        ScenarioEventTriggerFamily.DynamicOrInvalid => "Dynamic / invalid",
        _ => family.ToString()
    };

    private static string FormatEffectKind(ScenarioEventEffectKind kind) => kind switch
    {
        ScenarioEventEffectKind.TerrainAndMap => "Terrain / map",
        ScenarioEventEffectKind.TurnsAndTime => "Turns / time",
        ScenarioEventEffectKind.ObjectivesAndFlow => "Objectives / flow",
        ScenarioEventEffectKind.VariablesAndConditions => "Variables / conditions",
        ScenarioEventEffectKind.NarrativeAndUi => "Narrative / UI",
        ScenarioEventEffectKind.UnknownOrLua => "Other / Lua",
        _ => kind.ToString()
    };
}

public sealed class ScenarioEventProjection
{
    public IReadOnlyList<ScenarioEventProjectionEntry> Events { get; init; } = [];
    public WmlMacroExpansionResult? MacroExpansion { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public static string CreateProjectedId(int zeroBasedStartLine)
        => $"event-{zeroBasedStartLine + 1}";
}

/// <summary>
/// Builds a read-only catalog of effective scenario event handlers after the
/// shared bounded macro expansion. Authoritative source ordinals are attached
/// only to literal direct-child events, where raw replacement is reversible.
/// </summary>
public sealed class ScenarioEventProjectionBuilder(
    IWmlMacroExpansionService? macroExpansionService = null)
{
    private static readonly HashSet<string> EventKeys = new(
        ["name", "id", "first_time_only", "priority", "delayed_variable_substitution", "x", "y", "x,y"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FilterKeys = new(
        ["id", "type", "side", "x", "y", "x,y", "terrain", "canrecruit", "race", "gender", "role", "location_id"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TriggerFilterTags = new(
        ["filter", "filter_second", "filter_location", "filter_attack", "filter_second_attack", "filter_condition", "filter_side"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> RuntimeConditionalTags = new(
        ["if", "then", "else", "elseif", "switch", "case", "while", "foreach"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IWmlMacroExpansionService _macroExpansionService =
        macroExpansionService is WmlStandaloneMacroPreviewExpansionService
            ? macroExpansionService
            : new WmlStandaloneMacroPreviewExpansionService(macroExpansionService);

    public ScenarioEventProjection Build(
        ScenarioDocument document,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var expansion = _macroExpansionService.Expand(
            document.EditableBlock.ToRawText(),
            localDefinitions,
            fallbackResolver,
            new WmlMacroExpansionOptions(MaxExpansionDepth: 24, MaxExpandedLines: 65536));
        return Build(document, expansion);
    }

    public ScenarioEventProjection Build(
        ScenarioDocument document,
        WmlMacroExpansionResult expansion)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (expansion is null) throw new ArgumentNullException(nameof(expansion));

        return Build(
            document,
            expansion,
            WmlProjectionTreeParser.Parse(expansion.ExpandedText),
            isWesnothPreprocessed: false);
    }

    public ScenarioEventProjection Build(
        ScenarioDocument document,
        WmlEffectiveDocumentProjection effectiveProjection)
    {
        if (effectiveProjection is null) throw new ArgumentNullException(nameof(effectiveProjection));
        return Build(
            document,
            effectiveProjection.MacroExpansion,
            effectiveProjection.ProjectionTree,
            effectiveProjection.Authority == WmlEvaluationAuthority.WesnothPreprocessor);
    }

    private static ScenarioEventProjection Build(
        ScenarioDocument document,
        WmlMacroExpansionResult expansion,
        WmlProjectionTree tree,
        bool isWesnothPreprocessed)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        var scenario = tree.Roots.FirstOrDefault(x =>
            string.Equals(x.TagName, "scenario", StringComparison.OrdinalIgnoreCase));
        if (scenario is null)
        {
            return new ScenarioEventProjection
            {
                MacroExpansion = expansion,
                Diagnostics = ["The expanded projection contains no readable [scenario] block."]
            };
        }

        IReadOnlyList<WmlLineBlockSpan> sourceSpans = isWesnothPreprocessed
            ? []
            : ScenarioEventEditor.FindDirectEventSpans(document);
        var scenarioFileStartLine = isWesnothPreprocessed
            ? 0
            : document.OriginalText
                .AsSpan(0, Math.Min(document.BlockSpan.StartIndex, document.OriginalText.Length))
                .Count('\n');
        var result = new List<ScenarioEventProjectionEntry>();
        var literalDirectOrdinal = 0;
        foreach (var node in scenario.DescendantsAndSelf().Where(IsEventNode))
        {
            var amendment = WmlInlineCommentSplitter.Split(node.SourceLines[node.StartLine]).Value.Trim()
                .StartsWith("[+event", StringComparison.OrdinalIgnoreCase);
            var provenance = FindProvenance(node, expansion);
            var sourceKind = isWesnothPreprocessed
                ? ScenarioEventSourceKind.WesnothPreprocessed
                : provenance is null
                    ? ScenarioEventSourceKind.Literal
                    : ScenarioEventSourceKind.MacroGenerated;
            var isDirect = ReferenceEquals(node.Parent, scenario);
            int? authoritativeOrdinal = null;
            WmlLineBlockSpan? authoritativeSpan = null;
            if (!isWesnothPreprocessed &&
                !amendment &&
                isDirect &&
                sourceKind == ScenarioEventSourceKind.Literal)
            {
                authoritativeOrdinal = literalDirectOrdinal++;
                if (authoritativeOrdinal.Value < sourceSpans.Count)
                    authoritativeSpan = sourceSpans[authoritativeOrdinal.Value];
            }

            var values = ReadValues(node, EventKeys);
            IReadOnlyList<ScenarioEventTrigger> triggers = amendment ? [] : ReadTriggers(values.GetValueOrDefault("name"));
            var parentEvent = node.Ancestors().FirstOrDefault(IsEventNode);
            var triggerDisplay = amendment ? "(amends previous event)" : triggers.Count == 0
                ? "(missing name)"
                : string.Join(", ", triggers.Select(x => x.NormalizedName));
            var locations = ReadTriggerLocations(node, values);
            var actionTags = new List<string>();
            var effects = new HashSet<ScenarioEventEffectKind>();
            foreach (var child in node.Children)
                AnalyzeActionNode(child, actionTags, effects);

            var conditional = node.PreprocessorConditional || node.Ancestors().Any(x =>
                x.PreprocessorConditional || RuntimeConditionalTags.Contains(x.TagName));
            var sourceDisplay = isWesnothPreprocessed
                ? "Wesnoth-preprocessed selected-context output"
                : BuildSourceDisplay(
                    provenance,
                    isDirect,
                    parentEvent,
                    scenarioFileStartLine,
                    authoritativeSpan,
                    node.StartLine);
            var canEdit = !isWesnothPreprocessed &&
                          sourceKind == ScenarioEventSourceKind.Literal &&
                          isDirect &&
                          authoritativeOrdinal is not null &&
                          authoritativeSpan is not null;
            var editabilityReason = canEdit
                ? null
                : amendment
                    ? "This [+event] amends an earlier event. Edit the containing scenario source; it has no standalone event owner."
                : isWesnothPreprocessed
                    ? "This event is selected-context Wesnoth-preprocessed output. Edit its authored WML or macro source instead."
                    : sourceKind == ScenarioEventSourceKind.MacroGenerated
                    ? "This event is generated by a macro. Use Edit RAW for an exact local invocation, or global RAW for other source changes; generated output stays read-only."
                    : parentEvent is not null
                        ? "Nested event editing is read-only in this first event slice."
                        : "The event could not be mapped exactly to one authoritative direct-child source block.";

            result.Add(new ScenarioEventProjectionEntry
            {
                Id = ScenarioEventProjection.CreateProjectedId(node.StartLine),
                TriggerDisplay = triggerDisplay,
                Triggers = triggers,
                SourceKind = sourceKind,
                SourceDisplay = sourceDisplay,
                EffectiveRawWml = !isWesnothPreprocessed &&
                                  provenance is null &&
                                  authoritativeSpan is not null
                    ? ReadSourceBlock(document.EditableBlock, authoritativeSpan)
                    : StripProjectionMarkers(node.RawText),
                WmlId = NormalizeValue(values.GetValueOrDefault("id")),
                FirstTimeOnly = values.GetValueOrDefault("first_time_only"),
                Priority = NormalizeValue(values.GetValueOrDefault("priority")),
                DelayedVariableSubstitution = values.GetValueOrDefault("delayed_variable_substitution"),
                ProjectedLine = node.StartLine + 1,
                AuthoritativeLine = authoritativeSpan is null
                    ? null
                    : scenarioFileStartLine + authoritativeSpan.StartLineIndex + 1,
                AuthoritativeDirectEventOrdinal = authoritativeOrdinal,
                IsNested = parentEvent is not null,
                IsConditional = conditional,
                IsAmendment = amendment,
                ParentEventId = parentEvent is null
                    ? null
                    : ScenarioEventProjection.CreateProjectedId(parentEvent.StartLine),
                ParentTriggerDisplay = parentEvent is null
                    ? null
                    : BuildTriggerDisplay(parentEvent),
                CanEditRaw = canEdit,
                EditabilityReason = editabilityReason,
                MacroProvenance = provenance,
                TriggerLocations = locations,
                FilterSummaries = ReadFilterSummaries(node),
                EffectKinds = effects.OrderBy(x => x).ToArray(),
                ActionTagNames = actionTags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            });
        }

        var diagnostics = new List<string>();
        var expansionIssueCount = expansion.Issues
            .Select(x => x.Message)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (expansionIssueCount > 0)
        {
            diagnostics.Add(
                $"{expansionIssueCount} macro expansion issue(s) may hide additional event handlers or actions.");
        }

        var missingTriggerCount = result.Count(x => !x.IsAmendment && x.Triggers.Count == 0);
        if (missingTriggerCount > 0)
            diagnostics.Add($"{missingTriggerCount} event handler(s) have no readable name trigger and cannot fire as written.");
        var dynamicCount = result.Count(x => x.Triggers.Any(t => t.IsDynamic));
        if (dynamicCount > 0)
            diagnostics.Add($"{dynamicCount} event handler(s) use dynamic trigger names; their runtime trigger cannot be predicted statically.");
        var nestedCount = result.Count(x => x.IsNested);
        if (nestedCount > 0)
            diagnostics.Add($"{nestedCount} nested event handler(s) are registered only after their parent event runs.");
        var dynamicRegistrationCount = result.Count(x => x.ActionTagNames.Any(tag =>
            string.Equals(tag, "insert_tag", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, "lua", StringComparison.OrdinalIgnoreCase)));
        if (dynamicRegistrationCount > 0)
        {
            diagnostics.Add(
                $"{dynamicRegistrationCount} event handler(s) use [insert_tag] or [lua]; runtime-generated handlers/effects may not be statically discoverable.");
        }

        return new ScenarioEventProjection
        {
            Events = result,
            MacroExpansion = expansion,
            Diagnostics = diagnostics
        };
    }

    private static bool IsEventNode(WmlProjectionNode node)
        => string.Equals(node.TagName, "event", StringComparison.OrdinalIgnoreCase);

    private static WmlMacroExpansionProvenance? FindProvenance(
        WmlProjectionNode node,
        WmlMacroExpansionResult expansion)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            var id = WmlMacroExpansionService.ReadLeadingProvenanceMarker(current.Lines);
            if (expansion.FindProvenance(id) is { } provenance)
                return provenance;
        }

        return null;
    }

    private static IReadOnlyList<ScenarioEventTrigger> ReadTriggers(string? rawName)
    {
        var normalized = NormalizeValue(rawName);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        return normalized.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(raw =>
            {
                var display = raw.Replace('_', ' ').Trim();
                var dynamic = display.Contains('$', StringComparison.Ordinal) ||
                              display.Contains('{', StringComparison.Ordinal) ||
                              display.Contains('}', StringComparison.Ordinal);
                return new ScenarioEventTrigger(raw, display, ClassifyTrigger(display, dynamic), dynamic);
            })
            .ToArray();
    }

    private static ScenarioEventTriggerFamily ClassifyTrigger(string value, bool dynamic)
    {
        if (dynamic || string.IsNullOrWhiteSpace(value))
            return ScenarioEventTriggerFamily.DynamicOrInvalid;

        var name = value.Trim().ToLowerInvariant();
        if (name is "preload" or "prestart" or "start")
            return ScenarioEventTriggerFamily.Startup;
        if (name is "victory" or "defeat" or "enemies defeated" or "time over" or "scenario end")
            return ScenarioEventTriggerFamily.ScenarioFlow;
        if (name.Contains("turn", StringComparison.Ordinal) ||
            name.StartsWith("side ", StringComparison.Ordinal) ||
            name is "new turn" or "turn refresh")
        {
            return ScenarioEventTriggerFamily.TurnAndSide;
        }
        if (name is "moveto" or "sighted" or "select" or "deselect" or "capture" or "exit hex")
            return ScenarioEventTriggerFamily.LocationAndVision;
        if (name.Contains("attack", StringComparison.Ordinal) ||
            name.Contains("hits", StringComparison.Ordinal) ||
            name.Contains("misses", StringComparison.Ordinal))
        {
            return ScenarioEventTriggerFamily.Combat;
        }
        if (name is "die" or "last breath" or "unit placed" or "recruit" or "prerecruit" or
            "recall" or "prerecall" or "advance" or "post advance")
        {
            return ScenarioEventTriggerFamily.UnitLifecycle;
        }

        return ScenarioEventTriggerFamily.Custom;
    }

    private static IReadOnlyList<ScenarioEventTriggerLocation> ReadTriggerLocations(
        WmlProjectionNode eventNode,
        IReadOnlyDictionary<string, string> eventValues)
    {
        var result = new List<ScenarioEventTriggerLocation>();
        foreach (var filter in eventNode.Children.Where(x => TriggerFilterTags.Contains(x.TagName)))
        {
            AddLocation(filter, $"[{filter.TagName}]", result);
            foreach (var location in filter.DescendantsAndSelf()
                         .Skip(1)
                         .Where(x => string.Equals(x.TagName, "filter_location", StringComparison.OrdinalIgnoreCase)))
            {
                AddLocation(location, "nested [filter_location]", result);
            }
        }

        if (result.Count == 0 && HasPosition(eventValues))
            AddLocation(eventValues, "event", result);

        return result
            .DistinctBy(x => (x.Role, x.RawX, x.RawY, x.PositionState))
            .ToArray();
    }

    private static void AddLocation(
        WmlProjectionNode node,
        string role,
        List<ScenarioEventTriggerLocation> result)
    {
        var values = ReadValues(node, FilterKeys);
        if (HasPosition(values))
            AddLocation(values, role, result);
    }

    private static void AddLocation(
        IReadOnlyDictionary<string, string> values,
        string role,
        List<ScenarioEventTriggerLocation> result)
    {
        var position = ReadPosition(values);
        result.Add(new ScenarioEventTriggerLocation(
            role,
            position.State,
            position.Coordinates,
            position.RawX,
            position.RawY));
    }

    private static IReadOnlyList<string> ReadFilterSummaries(WmlProjectionNode eventNode)
    {
        var summaries = new List<string>();
        foreach (var filter in eventNode.Children.Where(x => TriggerFilterTags.Contains(x.TagName)))
        {
            var values = ReadValues(filter, FilterKeys)
                .Select(x => $"{x.Key}={NormalizeValue(x.Value)}")
                .ToArray();
            summaries.Add(values.Length == 0
                ? $"[{filter.TagName}] (complex/empty)"
                : $"[{filter.TagName}] {string.Join(", ", values)}");
        }
        return summaries;
    }

    private static void AnalyzeActionNode(
        WmlProjectionNode node,
        List<string> actionTags,
        HashSet<ScenarioEventEffectKind> effects)
    {
        if (TriggerFilterTags.Contains(node.TagName))
            return;

        actionTags.Add(node.TagName);
        if (IsEventNode(node))
        {
            effects.Add(ScenarioEventEffectKind.Events);
            return;
        }

        if (RuntimeConditionalTags.Contains(node.TagName))
        {
            effects.Add(ScenarioEventEffectKind.VariablesAndConditions);
            foreach (var child in node.Children)
                AnalyzeActionNode(child, actionTags, effects);
            return;
        }

        effects.Add(ClassifyEffect(node.TagName));
    }

    private static ScenarioEventEffectKind ClassifyEffect(string tagName)
    {
        var tag = tagName.ToLowerInvariant();
        if (tag is "unit" or "recall" or "unstore_unit" or "move_unit" or "move_unit_fake" or
            "teleport" or "kill" or "harm_unit" or "heal_unit" or "modify_unit" or
            "transform_unit" or "put_to_recall_list" or "store_unit")
        {
            return ScenarioEventEffectKind.Units;
        }
        if (tag is "modify_side" or "store_side" or "gold" or "modify_ai" or "allow_recruit" or
            "disallow_recruit" or "set_recruit" or "modify_side_ai")
        {
            return ScenarioEventEffectKind.Sides;
        }
        if (tag is "terrain" or "terrain_mask" or "replace_map" or "capture_village" or
            "item" or "remove_item" or "label" or "remove_label" or "redraw")
        {
            return ScenarioEventEffectKind.TerrainAndMap;
        }
        if (tag is "modify_turns" or "replace_schedule" or "time_area")
            return ScenarioEventEffectKind.TurnsAndTime;
        if (tag is "objectives" or "endlevel" or "end_turn" or "disallow_end_turn")
            return ScenarioEventEffectKind.ObjectivesAndFlow;
        if (tag is "set_variable" or "clear_variable" or "set_variables" or "insert_tag" or
            "set_variable_to_random" or "set_variable_to_time_of_day")
        {
            return ScenarioEventEffectKind.VariablesAndConditions;
        }
        if (tag is "fire_event" or "remove_event" or "set_menu_item" or "clear_menu_item")
            return ScenarioEventEffectKind.Events;
        if (tag is "message" or "print" or "floating_text" or "scroll_to" or "scroll_to_unit" or
            "animate_unit" or "hide_unit" or "unhide_unit" or "delay")
        {
            return ScenarioEventEffectKind.NarrativeAndUi;
        }
        if (tag is "music" or "sound" or "sound_source" or "remove_sound_source")
            return ScenarioEventEffectKind.Audio;
        return ScenarioEventEffectKind.UnknownOrLua;
    }

    private static PositionResult ReadPosition(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("x,y", out var combined))
        {
            var pair = WmlCoordinateValueText.SplitPair(combined);
            if (pair.Count == 2 && TryParseInteger(pair[0], out var x) && TryParseInteger(pair[1], out var y))
            {
                return new PositionResult(
                    ScenarioSpatialPositionState.Exact,
                    [new ScenarioMapCoordinate(x, y)],
                    pair[0],
                    pair[1]);
            }

            return new PositionResult(
                ScenarioSpatialPositionState.Unresolved,
                [],
                pair.ElementAtOrDefault(0) ?? combined,
                pair.ElementAtOrDefault(1));
        }

        values.TryGetValue("x", out var rawX);
        values.TryGetValue("y", out var rawY);
        var xs = SplitList(rawX);
        var ys = SplitList(rawY);
        if (xs.Count > 0 && xs.Count == ys.Count)
        {
            var coordinates = new List<ScenarioMapCoordinate>();
            for (var i = 0; i < xs.Count; i++)
            {
                if (!TryParseInteger(xs[i], out var x) || !TryParseInteger(ys[i], out var y))
                {
                    coordinates.Clear();
                    break;
                }
                coordinates.Add(new ScenarioMapCoordinate(x, y));
            }
            if (coordinates.Count > 0)
            {
                return new PositionResult(
                    ScenarioSpatialPositionState.Exact,
                    coordinates,
                    rawX,
                    rawY);
            }
        }

        return new PositionResult(
            ScenarioSpatialPositionState.Unresolved,
            [],
            rawX,
            rawY);
    }

    private static bool HasPosition(IReadOnlyDictionary<string, string> values)
        => values.ContainsKey("x,y") || values.ContainsKey("x") || values.ContainsKey("y");

    private static List<string> SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : NormalizeValue(value)!
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();

    private static bool TryParseInteger(string value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static IReadOnlyDictionary<string, string> ReadValues(
        WmlProjectionNode node,
        ISet<string> knownKeys)
        => WmlKeyValueExtractor.Extract(new WmlBlock(node.TagName, node.RawText), knownKeys).Known;

    private static string BuildTriggerDisplay(WmlProjectionNode eventNode)
    {
        var values = ReadValues(eventNode, EventKeys);
        var triggers = ReadTriggers(values.GetValueOrDefault("name"));
        return triggers.Count == 0
            ? "missing name"
            : string.Join(", ", triggers.Select(x => x.NormalizedName));
    }

    private static string BuildSourceDisplay(
        WmlMacroExpansionProvenance? provenance,
        bool isDirect,
        WmlProjectionNode? parentEvent,
        int scenarioFileStartLine,
        WmlLineBlockSpan? authoritativeSpan,
        int projectedStartLine)
    {
        if (provenance is not null)
            return $"Macro {{{provenance.MacroName}}} · {provenance.SourceLabel} · " +
                   $"invoked at line {scenarioFileStartLine + provenance.InvocationLineIndex + 1}";
        if (parentEvent is not null)
            return $"Literal nested event · projected line {projectedStartLine + 1}";
        if (isDirect && authoritativeSpan is not null)
            return $"Literal scenario WML · line {scenarioFileStartLine + authoritativeSpan.StartLineIndex + 1}";
        return $"Literal effective WML · projected line {projectedStartLine + 1}";
    }

    private static string ReadSourceBlock(WmlEditableBlock block, WmlLineBlockSpan span)
        => string.Join(
            block.NewLine,
            block.Lines.Skip(span.StartLineIndex).Take(span.EndLineIndexExclusive - span.StartLineIndex));

    private static string StripProjectionMarkers(string rawText)
        => string.Join(
            "\n",
            rawText.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(line => !line.Trim().StartsWith(
                    WmlMacroExpansionService.ProvenanceMarkerPrefix,
                    StringComparison.Ordinal)));

    private static string? NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.StartsWith('_'))
            normalized = normalized[1..].TrimStart();
        if (normalized.Length >= 2 &&
            ((normalized[0] == '"' && normalized[^1] == '"') ||
             (normalized[0] == '\'' && normalized[^1] == '\'')))
        {
            normalized = normalized[1..^1];
        }
        return normalized.Replace("\\\"", "\"", StringComparison.Ordinal).Trim();
    }

    private sealed record PositionResult(
        ScenarioSpatialPositionState State,
        IReadOnlyList<ScenarioMapCoordinate> Coordinates,
        string? RawX,
        string? RawY);
}
