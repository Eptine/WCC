namespace Wcc.Core.Maps;

public enum WesnothMapDiagnosticSeverity
{
    Warning,
    Error
}

public sealed record WesnothMapDiagnostic(
    WesnothMapDiagnosticSeverity Severity,
    string Message,
    int? SourceLine = null);

public sealed record WesnothMapCell(
    int ColumnIndex,
    int RowIndex,
    int X,
    int Y,
    string TerrainCode,
    string? LocationId,
    int? StartingPosition)
{
    public bool IsBorder { get; init; }
}

public sealed class WesnothMapDocument
{
    public int BorderSize { get; init; }
    public string? Usage { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<IReadOnlyList<WesnothMapCell>> Rows { get; init; } = [];
    public IReadOnlyList<WesnothMapDiagnostic> Diagnostics { get; init; } = [];

    public int PlayableWidth => Math.Max(0, Width - (BorderSize * 2));
    public int PlayableHeight => Math.Max(0, Height - (BorderSize * 2));
    public bool HasErrors => Diagnostics.Any(x => x.Severity == WesnothMapDiagnosticSeverity.Error);

    public WesnothMapCell? GetCellByStorageIndex(int columnIndex, int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count)
            return null;

        var row = Rows[rowIndex];
        return columnIndex >= 0 && columnIndex < row.Count ? row[columnIndex] : null;
    }

    public WesnothMapCell? GetCell(int x, int y)
        => GetCellByStorageIndex(
            x + BorderSize - 1,
            y + BorderSize - 1);
}
