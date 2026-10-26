using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Builds a read-only, context-specific spatial view of a scenario. Whole-line
/// macros are expanded through the shared bounded service; the authoritative
/// scenario block is never replaced or rewritten.
/// </summary>
public sealed class ScenarioSpatialProjectionBuilder(
    IWmlMacroExpansionService? macroExpansionService = null)
{
    private static readonly HashSet<string> SpatialKeys = new(
        [
            "x", "y", "x,y", "side", "id", "type", "name", "canrecruit",
            "placement", "image", "text", "terrain", "map_file", "map_data",
            "location_id"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> RuntimeConditionalTags = new(
        ["if", "then", "else", "elseif", "switch", "case"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MapChangeTags = new(
        ["terrain", "terrain_mask", "replace_map", "capture_village", "remove_item", "remove_label"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IWmlMacroExpansionService _macroExpansionService =
        macroExpansionService ?? new WmlMacroExpansionService();

    public ScenarioSpatialProjection Build(
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

    public ScenarioSpatialProjection Build(
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

    public ScenarioSpatialProjection Build(
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

    private static ScenarioSpatialProjection Build(
        ScenarioDocument document,
        WmlMacroExpansionResult expansion,
        WmlProjectionTree parsed,
        bool isWesnothPreprocessed)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        var scenario = parsed.Roots.FirstOrDefault(x =>
            string.Equals(x.TagName, "scenario", StringComparison.OrdinalIgnoreCase));
        if (scenario is null)
        {
            return new ScenarioSpatialProjection
            {
                MacroExpansion = expansion,
                Diagnostics = ["The expanded projection contains no readable [scenario] block."]
            };
        }

        var entries = new List<ScenarioSpatialEntry>();
        var nextId = 1;
        foreach (var node in scenario.DescendantsAndSelf())
        {
            if (ReferenceEquals(node, scenario))
                continue;

            if (string.Equals(node.TagName, "side", StringComparison.OrdinalIgnoreCase))
            {
                AddSideLeader(
                    node,
                    expansion,
                    entries,
                    ref nextId,
                    isWesnothPreprocessed,
                    isExplicitLeaderTag: false,
                    fallbackStartingSide: null);
                continue;
            }

            if (string.Equals(node.TagName, "leader", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.Parent?.TagName, "side", StringComparison.OrdinalIgnoreCase))
            {
                var parentValues = ReadValues(node.Parent!);
                AddSideLeader(
                    node,
                    expansion,
                    entries,
                    ref nextId,
                    isWesnothPreprocessed,
                    isExplicitLeaderTag: true,
                    fallbackStartingSide: ParseSingleInteger(ReadDisplay(parentValues, "side")));
                continue;
            }

            if (string.Equals(node.TagName, "unit", StringComparison.OrdinalIgnoreCase))
            {
                AddCoordinateEntry(
                    node,
                    ScenarioSpatialKind.Unit,
                    BuildUnitTitle,
                    expansion,
                    entries,
                    ref nextId,
                    isWesnothPreprocessed);
                continue;
            }

            if (string.Equals(node.TagName, "item", StringComparison.OrdinalIgnoreCase))
            {
                AddCoordinateEntry(
                    node,
                    ScenarioSpatialKind.Item,
                    BuildItemTitle,
                    expansion,
                    entries,
                    ref nextId,
                    isWesnothPreprocessed);
                continue;
            }

            if (string.Equals(node.TagName, "label", StringComparison.OrdinalIgnoreCase))
            {
                AddCoordinateEntry(
                    node,
                    ScenarioSpatialKind.Label,
                    BuildLabelTitle,
                    expansion,
                    entries,
                    ref nextId,
                    isWesnothPreprocessed);
                continue;
            }

            if (string.Equals(node.TagName, "event", StringComparison.OrdinalIgnoreCase))
            {
                AddEventTriggers(
                    node,
                    expansion,
                    entries,
                    ref nextId,
                    isWesnothPreprocessed);
                continue;
            }

            if (MapChangeTags.Contains(node.TagName))
            {
                AddCoordinateEntry(
                    node,
                    ScenarioSpatialKind.MapChange,
                    BuildMapChangeTitle,
                    expansion,
                    entries,
                    ref nextId,
                    isWesnothPreprocessed,
                    positionOptional: string.Equals(node.TagName, "replace_map", StringComparison.OrdinalIgnoreCase));
            }
        }

        var diagnostics = new List<string>();
        var issueCount = expansion.Issues
            .Select(x => x.Message)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (issueCount > 0)
        {
            diagnostics.Add(
                $"{issueCount} macro expansion issue(s) may hide additional spatial entries; known literal/generated entries remain visible.");
        }

        var conditionalCount = entries.Count(x => x.IsConditional);
        if (conditionalCount > 0)
        {
            diagnostics.Add(
                $"{conditionalCount} spatial entr{(conditionalCount == 1 ? "y is" : "ies are")} conditional and are shown as possible states, not applied map state.");
        }

        return new ScenarioSpatialProjection
        {
            Entries = entries,
            MacroExpansion = expansion,
            Diagnostics = diagnostics
        };
    }

    private static void AddSideLeader(
        WmlProjectionNode node,
        WmlMacroExpansionResult expansion,
        List<ScenarioSpatialEntry> entries,
        ref int nextId,
        bool isWesnothPreprocessed,
        bool isExplicitLeaderTag,
        int? fallbackStartingSide)
    {
        var values = ReadValues(node);
        var canRecruit = ReadDisplay(values, "canrecruit");
        var type = ReadDisplay(values, "type");
        var id = ReadDisplay(values, "id");
        if (!isExplicitLeaderTag &&
            !ReadsAsYes(canRecruit) &&
            string.IsNullOrWhiteSpace(type) &&
            string.IsNullOrWhiteSpace(id))
            return;

        var position = ReadPosition(values, positionOptional: false);
        var side = ParseSingleInteger(ReadDisplay(values, "side")) ?? fallbackStartingSide;
        if (position.State == ScenarioSpatialPositionState.Unresolved && side is not null &&
            string.IsNullOrWhiteSpace(position.RawX) && string.IsNullOrWhiteSpace(position.RawY))
        {
            position = position with
            {
                State = ScenarioSpatialPositionState.StartingPosition,
                StartingPosition = side
            };
        }

        var titleSubject = id ?? type ?? (side is null ? "Side leader" : $"Side {side} leader");
        AddEntry(
            node,
            ScenarioSpatialKind.SideLeader,
            $"Leader {titleSubject}",
            position,
            expansion,
            entries,
            ref nextId,
            isWesnothPreprocessed);
    }

    private static void AddEventTriggers(
        WmlProjectionNode eventNode,
        WmlMacroExpansionResult expansion,
        List<ScenarioSpatialEntry> entries,
        ref int nextId,
        bool isWesnothPreprocessed)
    {
        var eventValues = ReadValues(eventNode);
        var eventName = ReadDisplay(eventValues, "name") ?? "unnamed";
        var filters = eventNode.Children.Where(x =>
            string.Equals(x.TagName, "filter", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.TagName, "filter_second", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.TagName, "filter_location", StringComparison.OrdinalIgnoreCase)).ToArray();

        foreach (var filter in filters)
        {
            var values = ReadValues(filter);
            if (!HasPositionExpression(values))
                continue;

            var position = ReadPosition(values, positionOptional: false);
            AddEntry(
                filter,
                ScenarioSpatialKind.EventTrigger,
                $"Event {eventName}",
                position,
                expansion,
                entries,
                ref nextId,
                isWesnothPreprocessed,
                owningEvent: eventNode);
        }

        if (filters.Length == 0 && HasPositionExpression(eventValues))
        {
            AddEntry(
                eventNode,
                ScenarioSpatialKind.EventTrigger,
                $"Event {eventName}",
                ReadPosition(eventValues, positionOptional: false),
                expansion,
                entries,
                ref nextId,
                isWesnothPreprocessed,
                owningEvent: eventNode);
        }
    }

    private static void AddCoordinateEntry(
        WmlProjectionNode node,
        ScenarioSpatialKind kind,
        Func<IReadOnlyDictionary<string, string>, string> titleBuilder,
        WmlMacroExpansionResult expansion,
        List<ScenarioSpatialEntry> entries,
        ref int nextId,
        bool isWesnothPreprocessed,
        bool positionOptional = false)
    {
        var values = ReadValues(node);
        var position = kind == ScenarioSpatialKind.Unit && IsRecallUnit(values)
            ? new PositionReadResult(
                ScenarioSpatialPositionState.NotApplicable,
                [],
                values.GetValueOrDefault("x"),
                values.GetValueOrDefault("y"),
                null)
            : ReadPosition(values, positionOptional);
        AddEntry(
            node,
            kind,
            titleBuilder(values),
            position,
            expansion,
            entries,
            ref nextId,
            isWesnothPreprocessed);
    }

    private static bool IsRecallUnit(IReadOnlyDictionary<string, string> values)
    {
        var placement = ReadDisplay(values, "placement");
        if (string.Equals(placement, "recall", StringComparison.OrdinalIgnoreCase))
            return true;

        var x = ReadDisplay(values, "x");
        var y = ReadDisplay(values, "y");
        if (values.TryGetValue("x,y", out var combined))
        {
            var pair = WmlCoordinateValueText.SplitPair(combined);
            x = pair.ElementAtOrDefault(0);
            y = pair.ElementAtOrDefault(1);
        }

        return string.Equals(x, "recall", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(y, "recall", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddEntry(
        WmlProjectionNode node,
        ScenarioSpatialKind kind,
        string title,
        PositionReadResult position,
        WmlMacroExpansionResult expansion,
        List<ScenarioSpatialEntry> entries,
        ref int nextId,
        bool isWesnothPreprocessed,
        WmlProjectionNode? owningEvent = null)
    {
        owningEvent ??= node.Ancestors().FirstOrDefault(x =>
            string.Equals(x.TagName, "event", StringComparison.OrdinalIgnoreCase));
        var eventName = owningEvent is null
            ? null
            : ReadDisplay(ReadValues(owningEvent), "name") ?? "unnamed";
        var phase = eventName?.Trim().ToLowerInvariant() switch
        {
            "prestart" => ScenarioSpatialPhase.Prestart,
            "start" => ScenarioSpatialPhase.Start,
            null => ScenarioSpatialPhase.ScenarioSetup,
            _ => ScenarioSpatialPhase.Event
        };
        var provenanceId = WmlMacroExpansionService.ReadLeadingProvenanceMarker(node.Lines);
        var provenance = expansion.FindProvenance(provenanceId);
        var conditional = node.PreprocessorConditional || node.Ancestors().Any(x =>
            x.PreprocessorConditional || RuntimeConditionalTags.Contains(x.TagName));
        var sourceKind = isWesnothPreprocessed
            ? ScenarioSpatialSourceKind.WesnothPreprocessed
            : provenance is null
                ? ScenarioSpatialSourceKind.Literal
                : ScenarioSpatialSourceKind.MacroGenerated;
        var positionText = position.State switch
        {
            ScenarioSpatialPositionState.Exact =>
                string.Join(", ", position.Coordinates.Select(x => $"({x.X},{x.Y})")),
            ScenarioSpatialPositionState.StartingPosition =>
                $"starting position {position.StartingPosition}",
            ScenarioSpatialPositionState.NotApplicable => "whole map / no single coordinate",
            _ => $"unresolved position x={position.RawX ?? "?"}, y={position.RawY ?? "?"}"
        };
        var sourceText = isWesnothPreprocessed
            ? "Wesnoth-preprocessed selected-context output"
            : provenance is null
                ? "literal scenario WML"
                : $"{{{provenance.MacroName}}} from {FormatMacroSource(provenance)}";
        var conditionalText = conditional ? " · conditional" : string.Empty;
        var phaseText = phase switch
        {
            ScenarioSpatialPhase.ScenarioSetup => "scenario setup",
            ScenarioSpatialPhase.Prestart => "prestart",
            ScenarioSpatialPhase.Start => "start",
            _ => $"event '{eventName}'"
        };

        entries.Add(new ScenarioSpatialEntry
        {
            Id = $"spatial-{nextId++}",
            Kind = kind,
            Phase = phase,
            PositionState = position.State,
            SourceKind = sourceKind,
            Title = title,
            Description = $"{title} · {positionText} · {phaseText}{conditionalText} · {sourceText}",
            TagName = node.TagName,
            EventName = eventName,
            OwningEventId = owningEvent is null
                ? null
                : ScenarioEventProjection.CreateProjectedId(owningEvent.StartLine),
            RawX = position.RawX,
            RawY = position.RawY,
            StartingPosition = position.StartingPosition,
            ProjectedLine = node.StartLine + 1,
            IsConditional = conditional,
            CanEditDirectly = !isWesnothPreprocessed &&
                              sourceKind == ScenarioSpatialSourceKind.Literal &&
                              !conditional,
            Coordinates = position.Coordinates,
            MacroProvenance = provenance
        });
    }

    private static IReadOnlyDictionary<string, string> ReadValues(WmlProjectionNode node)
        => WmlKeyValueExtractor.Extract(
                new WmlBlock(node.TagName, node.RawText),
                SpatialKeys)
            .Known;

    private static bool HasPositionExpression(IReadOnlyDictionary<string, string> values)
        => values.ContainsKey("x,y") || values.ContainsKey("x") || values.ContainsKey("y");

    private static PositionReadResult ReadPosition(
        IReadOnlyDictionary<string, string> values,
        bool positionOptional)
    {
        if (values.TryGetValue("x,y", out var combined))
        {
            var pair = WmlCoordinateValueText.SplitPair(combined);
            if (pair.Count == 2 && TryParseInteger(pair[0], out var x) && TryParseInteger(pair[1], out var y))
            {
                return new PositionReadResult(
                    ScenarioSpatialPositionState.Exact,
                    [new ScenarioMapCoordinate(x, y)],
                    pair[0],
                    pair[1],
                    null);
            }

            return new PositionReadResult(
                ScenarioSpatialPositionState.Unresolved,
                [],
                pair.ElementAtOrDefault(0) ?? combined,
                pair.ElementAtOrDefault(1),
                null);
        }

        values.TryGetValue("x", out var rawX);
        values.TryGetValue("y", out var rawY);
        if (string.IsNullOrWhiteSpace(rawX) && string.IsNullOrWhiteSpace(rawY))
        {
            return new PositionReadResult(
                positionOptional
                    ? ScenarioSpatialPositionState.NotApplicable
                    : ScenarioSpatialPositionState.Unresolved,
                [],
                rawX,
                rawY,
                null);
        }

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
                return new PositionReadResult(
                    ScenarioSpatialPositionState.Exact,
                    coordinates,
                    rawX,
                    rawY,
                    null);
            }
        }

        return new PositionReadResult(
            ScenarioSpatialPositionState.Unresolved,
            [],
            rawX,
            rawY,
            null);
    }

    private static List<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var value = Unquote(raw.Trim());
        return value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    private static bool TryParseInteger(string raw, out int value)
        => int.TryParse(Unquote(raw.Trim()), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static int? ParseSingleInteger(string? raw)
        => raw is not null && TryParseInteger(raw, out var value) ? value : null;

    private static string? ReadDisplay(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var raw) ? WmlDisplayStringNormalizer.Normalize(raw) : null;

    private static bool ReadsAsYes(string? value)
        => string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string value)
        => value.Length >= 2 &&
           ((value[0] == '"' && value[^1] == '"') ||
            (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static string BuildUnitTitle(IReadOnlyDictionary<string, string> values)
    {
        var id = ReadDisplay(values, "id");
        var type = ReadDisplay(values, "type");
        return id is not null ? $"Unit {id}" : type is not null ? $"Unit {type}" : "Unit";
    }

    private static string BuildItemTitle(IReadOnlyDictionary<string, string> values)
    {
        var image = ReadDisplay(values, "image");
        return image is null ? "Item" : $"Item {Path.GetFileName(image)}";
    }

    private static string BuildLabelTitle(IReadOnlyDictionary<string, string> values)
    {
        var text = ReadDisplay(values, "text");
        return text is null ? "Label" : $"Label “{text}”";
    }

    private static string BuildMapChangeTitle(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("terrain", out var terrain))
            return $"Terrain → {WmlDisplayStringNormalizer.Normalize(terrain)}";
        if (values.ContainsKey("map_file") || values.ContainsKey("map_data"))
            return "Replace map";
        return "Map change";
    }

    private static string FormatMacroSource(WmlMacroExpansionProvenance provenance)
    {
        var package = string.IsNullOrWhiteSpace(provenance.PackageName)
            ? null
            : provenance.PackageName;
        var file = string.IsNullOrWhiteSpace(provenance.SourcePath)
            ? null
            : Path.GetFileName(provenance.SourcePath);
        var source = (package, file) switch
        {
            ({ } packageName, { } fileName) => $"{packageName} / {fileName}",
            ({ } packageName, null) => packageName,
            (null, { } fileName) => fileName,
            _ => "unknown source"
        };
        return provenance.DefinitionStartLine is { } line ? $"{source}:{line + 1}" : source;
    }

    private sealed record PositionReadResult(
        ScenarioSpatialPositionState State,
        IReadOnlyList<ScenarioMapCoordinate> Coordinates,
        string? RawX,
        string? RawY,
        int? StartingPosition);

}
