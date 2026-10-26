using Wcc.Core.Maps;

namespace Wcc.Core.Scenarios;

public sealed record ScenarioMapCoordinateSelectionRequest(
    string Purpose,
    ScenarioMapCoordinate? InitialCoordinate = null,
    bool AllowBorderCells = false);

public sealed record ScenarioMapCoordinateSelection(
    ScenarioMapCoordinate Coordinate,
    string TerrainCode,
    string? LocationId,
    int? StartingPosition,
    bool IsBorder);

public enum ScenarioMapCoordinateSelectionOutcome
{
    Confirmed,
    Cancelled,
    Superseded,
    MapChanged,
    Unavailable
}

public sealed record ScenarioMapCoordinateSelectionResult(
    ScenarioMapCoordinateSelectionOutcome Outcome,
    ScenarioMapCoordinateSelection? Selection = null,
    string? Message = null)
{
    public bool IsConfirmed =>
        Outcome == ScenarioMapCoordinateSelectionOutcome.Confirmed && Selection is not null;
}

/// <summary>
/// Owns one bounded, read-only map-selection interaction. It deliberately
/// returns a coordinate result instead of changing scenario WML; callers decide
/// whether and how a confirmed result can be applied to their authoritative node.
/// </summary>
public sealed class ScenarioMapCoordinateSelectionSession
{
    private readonly WesnothMapDocument _map;

    public ScenarioMapCoordinateSelectionSession(
        WesnothMapDocument map,
        ScenarioMapCoordinateSelectionRequest request)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        Request = request ?? throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.Purpose))
            throw new ArgumentException("A coordinate-selection purpose is required.", nameof(request));

        if (request.InitialCoordinate is { } initial)
        {
            var cell = map.GetCell(initial.X, initial.Y);
            if (cell is not null)
                TrySelect(cell);
        }
    }

    public ScenarioMapCoordinateSelectionRequest Request { get; }
    public WesnothMapCell? CandidateCell { get; private set; }
    public string? ValidationMessage { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool CanConfirm => IsActive && CandidateCell is not null;
    public ScenarioMapCoordinateSelectionResult? Completion { get; private set; }

    public bool TrySelect(WesnothMapCell? cell)
    {
        if (!IsActive)
            return false;

        if (cell is null || !BelongsToMap(cell))
        {
            CandidateCell = null;
            ValidationMessage = "The selected cell does not belong to this map.";
            return false;
        }

        if (cell.IsBorder && !Request.AllowBorderCells)
        {
            CandidateCell = null;
            ValidationMessage =
                $"({cell.X},{cell.Y}) is a map border cell. Choose a playable scenario hex.";
            return false;
        }

        CandidateCell = cell;
        ValidationMessage = null;
        return true;
    }

    public ScenarioMapCoordinateSelectionResult Confirm()
    {
        if (!IsActive)
            return Completion!;
        if (CandidateCell is null)
            throw new InvalidOperationException("A playable map hex must be selected before confirmation.");

        return Complete(
            ScenarioMapCoordinateSelectionOutcome.Confirmed,
            ToSelection(CandidateCell));
    }

    public ScenarioMapCoordinateSelectionResult Cancel(
        ScenarioMapCoordinateSelectionOutcome outcome = ScenarioMapCoordinateSelectionOutcome.Cancelled,
        string? message = null)
    {
        if (outcome is ScenarioMapCoordinateSelectionOutcome.Confirmed or
            ScenarioMapCoordinateSelectionOutcome.Unavailable)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "An active selection can only be cancelled, superseded, or ended by a map change.");
        }

        return IsActive
            ? Complete(outcome, null, message)
            : Completion!;
    }

    private ScenarioMapCoordinateSelectionResult Complete(
        ScenarioMapCoordinateSelectionOutcome outcome,
        ScenarioMapCoordinateSelection? selection,
        string? message = null)
    {
        IsActive = false;
        Completion = new ScenarioMapCoordinateSelectionResult(outcome, selection, message);
        return Completion;
    }

    private bool BelongsToMap(WesnothMapCell cell)
        => ReferenceEquals(
            _map.GetCellByStorageIndex(cell.ColumnIndex, cell.RowIndex),
            cell);

    private static ScenarioMapCoordinateSelection ToSelection(WesnothMapCell cell)
        => new(
            new ScenarioMapCoordinate(cell.X, cell.Y),
            cell.TerrainCode,
            cell.LocationId,
            cell.StartingPosition,
            cell.IsBorder);
}
