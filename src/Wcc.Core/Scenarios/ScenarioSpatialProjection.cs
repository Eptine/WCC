using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum ScenarioSpatialKind
{
    SideLeader,
    Unit,
    Item,
    Label,
    EventTrigger,
    MapChange
}

public enum ScenarioSpatialPhase
{
    ScenarioSetup,
    Prestart,
    Start,
    Event
}

public enum ScenarioSpatialPositionState
{
    Exact,
    StartingPosition,
    Unresolved,
    NotApplicable
}

public enum ScenarioSpatialSourceKind
{
    Literal,
    MacroGenerated,
    WesnothPreprocessed,
    CapturedStaticEvaluation
}

public sealed class ScenarioSpatialEntry
{
    public required string Id { get; init; }
    public required ScenarioSpatialKind Kind { get; init; }
    public required ScenarioSpatialPhase Phase { get; init; }
    public required ScenarioSpatialPositionState PositionState { get; init; }
    public required ScenarioSpatialSourceKind SourceKind { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string TagName { get; init; }
    public string? EventName { get; init; }
    public string? OwningEventId { get; init; }
    public string? RawX { get; init; }
    public string? RawY { get; init; }
    public int? StartingPosition { get; init; }
    public int? ProjectedLine { get; init; }
    public bool IsConditional { get; init; }
    public bool CanEditDirectly { get; init; }
    public IReadOnlyList<ScenarioMapCoordinate> Coordinates { get; init; } = [];
    public WmlMacroExpansionProvenance? MacroProvenance { get; init; }

    public string PhaseLabel => Phase switch
    {
        ScenarioSpatialPhase.ScenarioSetup => "scenario setup",
        ScenarioSpatialPhase.Prestart => "prestart",
        ScenarioSpatialPhase.Start => "start",
        _ => string.IsNullOrWhiteSpace(EventName) ? "event" : $"event '{EventName}'"
    };

    public string SourceLabel => SourceKind switch
    {
        ScenarioSpatialSourceKind.CapturedStaticEvaluation => "WCC captured selected-context output",
        ScenarioSpatialSourceKind.WesnothPreprocessed =>
            "Wesnoth-preprocessed selected-context output",
        ScenarioSpatialSourceKind.MacroGenerated when MacroProvenance is not null =>
            $"{{{MacroProvenance.MacroName}}} · {FormatMacroSource(MacroProvenance)}",
        _ => "literal scenario WML"
    };

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
}

public sealed class ScenarioSpatialProjection
{
    public IReadOnlyList<ScenarioSpatialEntry> Entries { get; init; } = [];
    public WmlMacroExpansionResult? MacroExpansion { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public int ExactEntryCount => Entries.Count(x =>
        x.PositionState is ScenarioSpatialPositionState.Exact or
            ScenarioSpatialPositionState.StartingPosition);

    public int UnresolvedEntryCount => Entries.Count(x =>
        x.PositionState == ScenarioSpatialPositionState.Unresolved);

    public int UnplacedEntryCount => Entries.Count(x =>
        x.PositionState == ScenarioSpatialPositionState.NotApplicable);

    public IReadOnlyList<ScenarioSpatialEntry> InitialEntries =>
        Entries.Where(x => x.OwningEventId is null).ToArray();

    public IReadOnlyList<ScenarioSpatialEntry> EntriesForEventContext(string? selectedEventId)
        => Entries.Where(x =>
                x.OwningEventId is null ||
                (!string.IsNullOrWhiteSpace(selectedEventId) &&
                 string.Equals(x.OwningEventId, selectedEventId, StringComparison.Ordinal)))
            .ToArray();
}
