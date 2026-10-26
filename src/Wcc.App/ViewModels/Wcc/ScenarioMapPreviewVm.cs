using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Wcc.Core.Maps;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

public enum ScenarioMapOverlayKind
{
    StartingPosition,
    NamedLocation,
    SideLeader,
    Unit,
    Event,
    Item,
    Label,
    MapChange
}

public sealed record ScenarioMapOverlayVm(
    ScenarioMapOverlayKind Kind,
    int X,
    int Y,
    string Label,
    string Description,
    ScenarioSpatialEntry? SpatialEntry = null,
    ScenarioEventProjectionEntry? Event = null);

public sealed class ScenarioMapPreviewVm : ViewModelBase
{
    private readonly Dictionary<string, WesnothTerrainVisual> _visuals =
        new(StringComparer.Ordinal);
    private Dictionary<(int X, int Y), IReadOnlyList<ScenarioMapOverlayVm>> _overlays;
    private WesnothMapCell? _selectedCell;
    private bool _showSideLeaders = true;
    private bool _showUnits = true;
    private bool _showItems = true;
    private bool _showLabels = true;
    private bool _showEvents = true;
    private bool _showMapChanges = true;
    private int _overlayRevision;
    private ScenarioMapCoordinateSelectionSession? _coordinateSelectionSession;
    private TaskCompletionSource<ScenarioMapCoordinateSelectionResult>? _coordinateSelectionCompletion;
    private string _eventSearchText = string.Empty;
    private string _selectedEventTriggerFilter = "All triggers";
    private string _selectedEventSourceFilter = "All sources";
    private string _selectedEventEffectFilter = "All effects";
    private bool _showOnlySpatialEvents;
    private IReadOnlyList<ScenarioEventProjectionEntry> _filteredEvents = [];
    private ScenarioEventProjectionEntry? _selectedEvent;
    private WesnothMapCell? _requestedFocusCell;
    private int _focusRequestRevision;
    private IReadOnlyList<ScenarioMapOverlayVm> _initialUnitDraftOverlays = [];
    private IReadOnlyList<ScenarioMapOverlayVm> _authoredEventSelectionOverlays = [];

    public ScenarioMapPreviewVm(
        WesnothMapDocument map,
        WesnothTerrainVisualCatalog terrainCatalog,
        string sourceDescription,
        string? sourcePath,
        ScenarioSpatialProjection? spatialProjection = null,
        ScenarioEventProjection? eventProjection = null)
    {
        Map = map ?? throw new ArgumentNullException(nameof(map));
        TerrainCatalog = terrainCatalog ?? throw new ArgumentNullException(nameof(terrainCatalog));
        SourceDescription = sourceDescription;
        SourcePath = sourcePath;
        SpatialProjection = spatialProjection ?? new ScenarioSpatialProjection();
        EventProjection = eventProjection ?? new ScenarioEventProjection();

        foreach (var code in map.Rows.SelectMany(x => x).Select(x => x.TerrainCode).Distinct(StringComparer.Ordinal))
            _visuals[code] = terrainCatalog.Resolve(code);

        UnknownTerrainCellCount = map.Rows
            .SelectMany(x => x)
            .Count(cell => !_visuals[cell.TerrainCode].IsFullyResolved);
        StartingPositionCount = map.Rows
            .SelectMany(x => x)
            .Count(cell => cell.StartingPosition is not null);
        NamedLocationCount = map.Rows
            .SelectMany(x => x)
            .Count(cell => cell.LocationId is not null && cell.StartingPosition is null);

        _overlays = BuildOverlayIndex(map);
        RebuildFilteredEvents();
    }

    private Dictionary<(int X, int Y), IReadOnlyList<ScenarioMapOverlayVm>> BuildOverlayIndex(WesnothMapDocument map)
    {
        var overlays = map.Rows
            .SelectMany(x => x)
            .Where(cell => cell.LocationId is not null)
            .Select(cell => cell.StartingPosition is { } start
                ? new ScenarioMapOverlayVm(
                    ScenarioMapOverlayKind.StartingPosition,
                    cell.X,
                    cell.Y,
                    start.ToString(),
                    $"Starting position {start}")
                : new ScenarioMapOverlayVm(
                    ScenarioMapOverlayKind.NamedLocation,
                    cell.X,
                    cell.Y,
                    "•",
                    $"Named location '{cell.LocationId}'"))
            .ToList();
        foreach (var entry in SpatialProjection.Entries)
        {
            if (entry.Kind == ScenarioSpatialKind.EventTrigger)
                continue;

            foreach (var coordinate in ResolveCoordinates(map, entry))
            {
                overlays.Add(new ScenarioMapOverlayVm(
                    ToOverlayKind(entry.Kind),
                    coordinate.X,
                    coordinate.Y,
                    OverlayLabel(entry),
                    entry.Description,
                    entry));
            }
        }

        foreach (var eventEntry in EventProjection.Events)
        {
            foreach (var coordinate in eventEntry.TriggerLocations
                         .Where(x => x.PositionState == ScenarioSpatialPositionState.Exact)
                         .SelectMany(x => x.Coordinates)
                         .Distinct())
            {
                overlays.Add(new ScenarioMapOverlayVm(
                    ScenarioMapOverlayKind.Event,
                    coordinate.X,
                    coordinate.Y,
                    "E",
                    $"Event {eventEntry.TriggerDisplay} · {eventEntry.FilterDisplay} · {eventEntry.SourceDisplay}",
                    Event: eventEntry));
            }
        }

        overlays.AddRange(_initialUnitDraftOverlays);
        overlays.AddRange(_authoredEventSelectionOverlays);
        return overlays
            .GroupBy(x => (x.X, x.Y))
            .ToDictionary(x => x.Key, x => (IReadOnlyList<ScenarioMapOverlayVm>)x.ToArray());
    }

    public WesnothMapDocument Map { get; }
    public WesnothTerrainVisualCatalog TerrainCatalog { get; }
    public string SourceDescription { get; }
    public string? SourcePath { get; }
    public ScenarioSpatialProjection SpatialProjection { get; private set; }
    public ScenarioEventProjection EventProjection { get; private set; }

    internal void SetInitialUnitDraftOverlays(IReadOnlyList<ScenarioMapOverlayVm> overlays)
    {
        if (_initialUnitDraftOverlays.SequenceEqual(overlays)) return;
        _initialUnitDraftOverlays = overlays;
        _overlays = BuildOverlayIndex(Map);
        _overlayRevision++;
        OnPropertyChanged(nameof(OverlayRevision));
        OnPropertyChanged(nameof(SelectedOverlaySummary));
    }

    internal void RequestFocusCell(WesnothMapCell cell)
    {
        if (!ReferenceEquals(Map.GetCell(cell.X, cell.Y), cell)) return;
        _requestedFocusCell = cell;
        SelectedCell = cell;
        _focusRequestRevision++;
        OnPropertyChanged(nameof(RequestedFocusCell));
        OnPropertyChanged(nameof(FocusRequestRevision));
    }

    // Authored source selection supplies possible display locations only. Native
    // event identity and effective spatial output remain separate and untouched.
    internal void SetAuthoredEventSelectionOverlays(IReadOnlyList<ScenarioMapOverlayVm> overlays)
    {
        if (_authoredEventSelectionOverlays.SequenceEqual(overlays)) return;
        _authoredEventSelectionOverlays = overlays;
        _overlays = BuildOverlayIndex(Map);
        _overlayRevision++;
        OnPropertyChanged(nameof(OverlayRevision));
        OnPropertyChanged(nameof(SelectedOverlaySummary));
    }

    // Display-only draft projection. The captured map, scalar owners and coordinate
    // selection stay intact; these event rows confer no source-edit authority.
    internal void ReplaceEventPreview(ScenarioSpatialProjection spatial, ScenarioEventProjection events,
        string? preferredEventId = null)
    {
        ArgumentNullException.ThrowIfNull(spatial);
        ArgumentNullException.ThrowIfNull(events);
        var selectedId = preferredEventId ?? SelectedEvent?.Id;
        SpatialProjection = spatial;
        EventProjection = events;
        _overlays = BuildOverlayIndex(Map);
        RebuildFilteredEvents();
        SelectedEvent = FilteredEvents.FirstOrDefault(entry => entry.Id == selectedId);
        foreach (var property in new[] { nameof(SpatialProjection), nameof(EventProjection), nameof(EventSourceFilterOptions),
                     nameof(EventCount), nameof(SideLeaderCount), nameof(UnitCount), nameof(ItemCount), nameof(LabelCount),
                     nameof(MapChangeCount), nameof(SpatialProjectionSummary), nameof(UnplacedSpatialSummary),
                     nameof(SelectedOverlaySummary), nameof(SelectedEventSummary), nameof(SelectedEventActionSummary) })
            OnPropertyChanged(property);
    }
    public int UnknownTerrainCellCount { get; }
    public int StartingPositionCount { get; }
    public int NamedLocationCount { get; }
    public int SideLeaderCount => SpatialProjection.Entries.Count(x => x.Kind == ScenarioSpatialKind.SideLeader);
    public int UnitCount => SpatialProjection.Entries.Count(x => x.Kind == ScenarioSpatialKind.Unit);
    public int ItemCount => SpatialProjection.Entries.Count(x => x.Kind == ScenarioSpatialKind.Item);
    public int LabelCount => SpatialProjection.Entries.Count(x => x.Kind == ScenarioSpatialKind.Label);
    public int EventCount => EventProjection.Events.Count;
    public int MapChangeCount => SpatialProjection.Entries.Count(x => x.Kind == ScenarioSpatialKind.MapChange);

    public IReadOnlyList<string> EventTriggerFilterOptions { get; } =
    [
        "All triggers", "Startup", "Turn / side", "Location / vision", "Combat",
        "Unit lifecycle", "Scenario flow", "Custom", "Dynamic / invalid"
    ];

    public IReadOnlyList<string> EventSourceFilterOptions =>
        EventProjection.Events.Any(entry => entry.SourceKind == ScenarioEventSourceKind.CapturedStaticEvaluation)
            ? ["All sources", "WCC captured", "Nested"]
        : EventProjection.Events.Any(entry => entry.SourceKind == ScenarioEventSourceKind.WesnothPreprocessed)
            ? ["All sources", "Literal", "Macro-generated", "Wesnoth-preprocessed", "Nested"]
            : ["All sources", "Literal", "Macro-generated", "Nested"];

    public IReadOnlyList<string> EventEffectFilterOptions { get; } =
    [
        "All effects", "Units", "Sides", "Terrain / map", "Turns / time",
        "Objectives / flow", "Variables / conditions", "Events", "Narrative / UI",
        "Audio", "Other / Lua"
    ];

    public string EventSearchText
    {
        get => _eventSearchText;
        set
        {
            if (SetField(ref _eventSearchText, value ?? string.Empty))
                RebuildFilteredEvents();
        }
    }

    public string SelectedEventTriggerFilter
    {
        get => _selectedEventTriggerFilter;
        set
        {
            if (SetField(ref _selectedEventTriggerFilter, value ?? "All triggers"))
                RebuildFilteredEvents();
        }
    }

    public string SelectedEventSourceFilter
    {
        get => _selectedEventSourceFilter;
        set
        {
            if (SetField(ref _selectedEventSourceFilter, value ?? "All sources"))
                RebuildFilteredEvents();
        }
    }

    public string SelectedEventEffectFilter
    {
        get => _selectedEventEffectFilter;
        set
        {
            if (SetField(ref _selectedEventEffectFilter, value ?? "All effects"))
                RebuildFilteredEvents();
        }
    }

    public bool ShowOnlySpatialEvents
    {
        get => _showOnlySpatialEvents;
        set
        {
            if (SetField(ref _showOnlySpatialEvents, value))
                RebuildFilteredEvents();
        }
    }

    public IReadOnlyList<ScenarioEventProjectionEntry> FilteredEvents
    {
        get => _filteredEvents;
        private set => SetField(ref _filteredEvents, value);
    }

    public string EventListSummary =>
        $"Showing {FilteredEvents.Count} of {EventProjection.Events.Count} event handler(s).";

    public ScenarioEventProjectionEntry? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!SetField(ref _selectedEvent, value))
                return;

            _overlayRevision++;
            OnPropertyChanged(nameof(OverlayRevision));
            OnPropertyChanged(nameof(HasSelectedEvent));
            OnPropertyChanged(nameof(SelectedEventSummary));
            OnPropertyChanged(nameof(SelectedEventActionSummary));
            OnPropertyChanged(nameof(UnplacedSpatialSummary));
            OnPropertyChanged(nameof(SpatialProjectionSummary));
            OnPropertyChanged(nameof(SelectedOverlaySummary));
            RequestFocusForSelectedEvent();
        }
    }

    public bool HasSelectedEvent => SelectedEvent is not null;

    public string? SelectedEventSummary => SelectedEvent is null
        ? null
        : $"{SelectedEvent.TriggerDisplay} · {SelectedEvent.TriggerFamilyDisplay} · " +
          $"{SelectedEvent.RepeatDisplay} · {SelectedEvent.NestingDisplay} · {SelectedEvent.SourceDisplay}";

    public string? SelectedEventActionSummary => SelectedEvent is null
        ? null
        : $"Trigger: {SelectedEvent.FilterDisplay} | Location: {SelectedEvent.LocationDisplay} | " +
          $"Possible effects: {SelectedEvent.EffectDisplay} | Actions: {SelectedEvent.ActionDisplay}";

    public WesnothMapCell? RequestedFocusCell => _requestedFocusCell;
    public int FocusRequestRevision => _focusRequestRevision;

    public bool ShowSideLeaders
    {
        get => _showSideLeaders;
        set => SetLayer(ref _showSideLeaders, value);
    }

    public bool ShowUnits
    {
        get => _showUnits;
        set => SetLayer(ref _showUnits, value);
    }

    public bool ShowItems
    {
        get => _showItems;
        set => SetLayer(ref _showItems, value);
    }

    public bool ShowLabels
    {
        get => _showLabels;
        set => SetLayer(ref _showLabels, value);
    }

    public bool ShowEvents
    {
        get => _showEvents;
        set => SetLayer(ref _showEvents, value);
    }

    public bool ShowMapChanges
    {
        get => _showMapChanges;
        set => SetLayer(ref _showMapChanges, value);
    }

    public int OverlayRevision => _overlayRevision;

    public bool IsCoordinateSelectionActive => _coordinateSelectionSession?.IsActive == true;

    public string? CoordinateSelectionPrompt => _coordinateSelectionSession?.Request.Purpose;

    public WesnothMapCell? CoordinateSelectionCandidateCell =>
        _coordinateSelectionSession?.CandidateCell;

    public bool CanConfirmCoordinateSelection =>
        _coordinateSelectionSession?.CanConfirm == true;

    public string? CoordinateSelectionStatus
    {
        get
        {
            if (_coordinateSelectionSession is not { IsActive: true } session)
                return null;
            if (!string.IsNullOrWhiteSpace(session.ValidationMessage))
                return session.ValidationMessage;
            if (session.CandidateCell is not { } cell)
                return "Click one playable hex, then confirm the coordinate.";

            var location = cell.StartingPosition is { } start
                ? $" · starting position {start}"
                : string.IsNullOrWhiteSpace(cell.LocationId)
                    ? string.Empty
                    : $" · location '{cell.LocationId}'";
            return $"Candidate ({cell.X},{cell.Y}) · terrain {cell.TerrainCode}{location}";
        }
    }

    public string SpatialProjectionSummary
    {
        get
        {
            var entries = SpatialEntriesForCurrentContext().ToArray();
            var placed = entries.Count(x => x.PositionState is
                ScenarioSpatialPositionState.Exact or ScenarioSpatialPositionState.StartingPosition);
            var unresolved = entries.Count(x => x.PositionState == ScenarioSpatialPositionState.Unresolved);
            var unplaced = entries.Count(x => x.PositionState == ScenarioSpatialPositionState.NotApplicable);
            var context = SelectedEvent is null
                ? "Authored initial-state projection"
                : $"Initial state + selected '{SelectedEvent.TriggerDisplay}' possible effects";
            return $"{context}: {placed} placed, {unresolved} unresolved, {unplaced} whole-map/unplaced action(s).";
        }
    }

    public string? UnplacedSpatialSummary
    {
        get
        {
            var allEntries = SpatialEntriesForCurrentContext()
                .Where(x => x.PositionState is ScenarioSpatialPositionState.Unresolved or
                    ScenarioSpatialPositionState.NotApplicable)
                .ToArray();
            var entries = allEntries
                .Take(6)
                .Select(x => x.Description)
                .ToArray();
            if (entries.Length == 0)
                return null;

            var remaining = allEntries.Length - entries.Length;
            return "Not drawn: " + string.Join(" | ", entries) +
                   (remaining > 0 ? $" | … and {remaining} more" : string.Empty);
        }
    }

    public WesnothMapCell? SelectedCell
    {
        get => _selectedCell;
        private set
        {
            if (!SetField(ref _selectedCell, value))
                return;
            OnPropertyChanged(nameof(SelectedCellSummary));
            OnPropertyChanged(nameof(SelectedOverlaySummary));
        }
    }

    public string SelectedCellSummary
    {
        get
        {
            if (SelectedCell is null)
                return IsCoordinateSelectionActive
                    ? "Click a playable hex to choose its scenario coordinate."
                    : "Click a hex to inspect its scenario coordinate and terrain code.";

            var coordinate = SelectedCell.IsBorder
                ? $"border cell ({SelectedCell.X},{SelectedCell.Y})"
                : $"hex ({SelectedCell.X},{SelectedCell.Y})";
            var markers = new List<string>();
            if (SelectedCell.StartingPosition is { } start)
                markers.Add($"starting position {start}");
            else if (!string.IsNullOrWhiteSpace(SelectedCell.LocationId))
                markers.Add($"location '{SelectedCell.LocationId}'");

            var visual = GetVisual(SelectedCell.TerrainCode);
            if (visual.UnresolvedCodes.Count > 0)
                markers.Add($"missing texture: {string.Join(", ", visual.UnresolvedCodes)}");
            var suffix = markers.Count == 0 ? string.Empty : $" · {string.Join(" · ", markers)}";
            return $"{coordinate} · terrain {SelectedCell.TerrainCode}{suffix}";
        }
    }

    public string? SelectedOverlaySummary
    {
        get
        {
            if (SelectedCell is null)
                return null;
            var overlays = GetOverlays(SelectedCell.X, SelectedCell.Y)
                .Where(x => x.SpatialEntry is not null || x.Event is not null)
                .Select(x => x.Description)
                .ToArray();
            return overlays.Length == 0 ? null : string.Join(" | ", overlays);
        }
    }

    public WesnothTerrainVisual GetVisual(string terrainCode)
    {
        if (_visuals.TryGetValue(terrainCode, out var visual))
            return visual;

        visual = TerrainCatalog.Resolve(terrainCode);
        _visuals[terrainCode] = visual;
        return visual;
    }

    public IReadOnlyList<ScenarioMapOverlayVm> GetOverlays(int x, int y)
        => (_overlays.GetValueOrDefault((x, y)) ?? [])
            .Where(IsLayerVisible)
            .ToArray();

    public void SelectCell(WesnothMapCell? cell)
    {
        SelectedCell = cell;
        if (_coordinateSelectionSession?.IsActive == true)
        {
            _coordinateSelectionSession.TrySelect(cell);
            RaiseCoordinateSelectionChanged();
            return;
        }

        SelectLinkedEventAtCell(cell);
    }

    public Task<ScenarioMapCoordinateSelectionResult> BeginCoordinateSelectionAsync(
        ScenarioMapCoordinateSelectionRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        CompleteCoordinateSelection(
            _coordinateSelectionSession?.Cancel(
                ScenarioMapCoordinateSelectionOutcome.Superseded,
                "A newer coordinate-selection request replaced this one."));

        _coordinateSelectionSession = new ScenarioMapCoordinateSelectionSession(Map, request);
        _coordinateSelectionCompletion = new TaskCompletionSource<ScenarioMapCoordinateSelectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (_coordinateSelectionSession.CandidateCell is { } initialCell)
            SelectedCell = initialCell;
        RaiseCoordinateSelectionChanged();
        return _coordinateSelectionCompletion.Task;
    }

    public bool ConfirmCoordinateSelection()
    {
        if (_coordinateSelectionSession?.CanConfirm != true)
            return false;

        CompleteCoordinateSelection(_coordinateSelectionSession.Confirm());
        return true;
    }

    public bool CancelCoordinateSelection()
    {
        if (_coordinateSelectionSession?.IsActive != true)
            return false;

        CompleteCoordinateSelection(_coordinateSelectionSession.Cancel());
        return true;
    }

    public void EndCoordinateSelectionForMapChange()
    {
        if (_coordinateSelectionSession?.IsActive != true)
            return;

        CompleteCoordinateSelection(_coordinateSelectionSession.Cancel(
            ScenarioMapCoordinateSelectionOutcome.MapChanged,
            "The map changed before the coordinate was confirmed."));
    }

    private void RebuildFilteredEvents()
    {
        IEnumerable<ScenarioEventProjectionEntry> query = EventProjection.Events;
        var search = EventSearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(entry =>
                entry.TriggerDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (entry.WmlId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                entry.SourceDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.FilterDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.EffectDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.ActionTagNames.Any(x => x.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        query = query.Where(MatchesTriggerFilter)
            .Where(MatchesSourceFilter)
            .Where(MatchesEffectFilter);
        if (ShowOnlySpatialEvents)
            query = query.Where(x => x.HasSpatialTrigger);

        FilteredEvents = query.ToArray();
        OnPropertyChanged(nameof(EventListSummary));
        _overlayRevision++;
        OnPropertyChanged(nameof(OverlayRevision));
        OnPropertyChanged(nameof(SelectedOverlaySummary));
        if (SelectedEvent is not null && !FilteredEvents.Contains(SelectedEvent))
            SelectedEvent = null;
    }

    private bool MatchesTriggerFilter(ScenarioEventProjectionEntry entry)
    {
        var family = SelectedEventTriggerFilter switch
        {
            "Startup" => ScenarioEventTriggerFamily.Startup,
            "Turn / side" => ScenarioEventTriggerFamily.TurnAndSide,
            "Location / vision" => ScenarioEventTriggerFamily.LocationAndVision,
            "Combat" => ScenarioEventTriggerFamily.Combat,
            "Unit lifecycle" => ScenarioEventTriggerFamily.UnitLifecycle,
            "Scenario flow" => ScenarioEventTriggerFamily.ScenarioFlow,
            "Custom" => ScenarioEventTriggerFamily.Custom,
            "Dynamic / invalid" => ScenarioEventTriggerFamily.DynamicOrInvalid,
            _ => (ScenarioEventTriggerFamily?)null
        };
        return family is null || entry.Triggers.Any(x => x.Family == family);
    }

    private bool MatchesSourceFilter(ScenarioEventProjectionEntry entry)
        => SelectedEventSourceFilter switch
        {
            "Literal" => entry.SourceKind == ScenarioEventSourceKind.Literal && !entry.IsNested,
            "Macro-generated" => entry.SourceKind == ScenarioEventSourceKind.MacroGenerated,
            "Wesnoth-preprocessed" => entry.SourceKind == ScenarioEventSourceKind.WesnothPreprocessed,
            "WCC captured" => entry.SourceKind == ScenarioEventSourceKind.CapturedStaticEvaluation,
            "Nested" => entry.IsNested,
            _ => true
        };

    private bool MatchesEffectFilter(ScenarioEventProjectionEntry entry)
    {
        var kind = SelectedEventEffectFilter switch
        {
            "Units" => ScenarioEventEffectKind.Units,
            "Sides" => ScenarioEventEffectKind.Sides,
            "Terrain / map" => ScenarioEventEffectKind.TerrainAndMap,
            "Turns / time" => ScenarioEventEffectKind.TurnsAndTime,
            "Objectives / flow" => ScenarioEventEffectKind.ObjectivesAndFlow,
            "Variables / conditions" => ScenarioEventEffectKind.VariablesAndConditions,
            "Events" => ScenarioEventEffectKind.Events,
            "Narrative / UI" => ScenarioEventEffectKind.NarrativeAndUi,
            "Audio" => ScenarioEventEffectKind.Audio,
            "Other / Lua" => ScenarioEventEffectKind.UnknownOrLua,
            _ => (ScenarioEventEffectKind?)null
        };
        return kind is null || entry.EffectKinds.Contains(kind.Value);
    }

    private void SelectLinkedEventAtCell(WesnothMapCell? cell)
    {
        if (cell is null || !ShowEvents)
            return;

        var linked = FilteredEvents
            .Where(entry => entry.TriggerLocations.Any(location =>
                location.PositionState == ScenarioSpatialPositionState.Exact &&
                location.Coordinates.Any(x => x.X == cell.X && x.Y == cell.Y)))
            .ToArray();
        if (linked.Length == 0)
            return;

        var currentIndex = SelectedEvent is null ? -1 : Array.IndexOf(linked, SelectedEvent);
        SelectedEvent = linked[(currentIndex + 1) % linked.Length];
    }

    private void RequestFocusForSelectedEvent()
    {
        if (SelectedEvent is null)
            return;

        var coordinate = SelectedEvent.TriggerLocations
            .Where(x => x.PositionState == ScenarioSpatialPositionState.Exact)
            .SelectMany(x => x.Coordinates)
            .FirstOrDefault();
        if (coordinate is null || Map.GetCell(coordinate.X, coordinate.Y) is not { } cell)
            return;

        _requestedFocusCell = cell;
        SelectedCell = cell;
        _focusRequestRevision++;
        OnPropertyChanged(nameof(RequestedFocusCell));
        OnPropertyChanged(nameof(FocusRequestRevision));
    }

    private IEnumerable<ScenarioSpatialEntry> SpatialEntriesForCurrentContext()
        => SpatialProjection.EntriesForEventContext(SelectedEvent?.Id);

    private void SetLayer(
        ref bool field,
        bool value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!SetField(ref field, value, propertyName))
            return;
        _overlayRevision++;
        OnPropertyChanged(nameof(OverlayRevision));
        OnPropertyChanged(nameof(SelectedOverlaySummary));
    }

    private void CompleteCoordinateSelection(ScenarioMapCoordinateSelectionResult? result)
    {
        if (result is null)
            return;

        var completion = _coordinateSelectionCompletion;
        _coordinateSelectionSession = null;
        _coordinateSelectionCompletion = null;
        RaiseCoordinateSelectionChanged();
        completion?.TrySetResult(result);
    }

    private void RaiseCoordinateSelectionChanged()
    {
        OnPropertyChanged(nameof(IsCoordinateSelectionActive));
        OnPropertyChanged(nameof(CoordinateSelectionPrompt));
        OnPropertyChanged(nameof(CoordinateSelectionCandidateCell));
        OnPropertyChanged(nameof(CanConfirmCoordinateSelection));
        OnPropertyChanged(nameof(CoordinateSelectionStatus));
        OnPropertyChanged(nameof(SelectedCellSummary));
    }

    private bool IsLayerVisible(ScenarioMapOverlayVm overlay)
    {
        if (overlay.Event is not null && !FilteredEvents.Contains(overlay.Event))
            return false;
        if (overlay.SpatialEntry?.OwningEventId is { } ownerId &&
            !string.Equals(ownerId, SelectedEvent?.Id, StringComparison.Ordinal))
        {
            return false;
        }

        return overlay.Kind switch
        {
            ScenarioMapOverlayKind.SideLeader => ShowSideLeaders,
            ScenarioMapOverlayKind.Unit => ShowUnits,
            ScenarioMapOverlayKind.Item => ShowItems,
            ScenarioMapOverlayKind.Label => ShowLabels,
            ScenarioMapOverlayKind.Event => ShowEvents,
            ScenarioMapOverlayKind.MapChange => ShowMapChanges,
            _ => true
        };
    }

    private static IEnumerable<ScenarioMapCoordinate> ResolveCoordinates(
        WesnothMapDocument map,
        ScenarioSpatialEntry entry)
    {
        if (entry.PositionState == ScenarioSpatialPositionState.Exact)
            return entry.Coordinates;
        if (entry.PositionState != ScenarioSpatialPositionState.StartingPosition ||
            entry.StartingPosition is not { } start)
        {
            return [];
        }

        var cell = map.Rows.SelectMany(x => x).FirstOrDefault(x => x.StartingPosition == start);
        return cell is null ? [] : [new ScenarioMapCoordinate(cell.X, cell.Y)];
    }

    private static ScenarioMapOverlayKind ToOverlayKind(ScenarioSpatialKind kind)
        => kind switch
        {
            ScenarioSpatialKind.SideLeader => ScenarioMapOverlayKind.SideLeader,
            ScenarioSpatialKind.Unit => ScenarioMapOverlayKind.Unit,
            ScenarioSpatialKind.Item => ScenarioMapOverlayKind.Item,
            ScenarioSpatialKind.Label => ScenarioMapOverlayKind.Label,
            ScenarioSpatialKind.EventTrigger => ScenarioMapOverlayKind.Event,
            _ => ScenarioMapOverlayKind.MapChange
        };

    private static string OverlayLabel(ScenarioSpatialEntry entry)
        => entry.Kind switch
        {
            ScenarioSpatialKind.SideLeader => "L",
            ScenarioSpatialKind.Unit => "U",
            ScenarioSpatialKind.Item => "I",
            ScenarioSpatialKind.Label => "T",
            ScenarioSpatialKind.EventTrigger => "E",
            _ => "Δ"
        };
}
