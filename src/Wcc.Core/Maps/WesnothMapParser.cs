using System.Globalization;

namespace Wcc.Core.Maps;

public sealed record WesnothMapParserOptions(
    int MaxRows = 4096,
    int MaxColumns = 4096,
    int MaxCells = 1_000_000)
{
    /// <summary>Fallback for headerless maps; current Wesnoth scenarios have one border hex.</summary>
    public int DefaultBorderSize { get; init; }
    /// <summary>Current Wesnoth strips legacy headers without applying their border_size.</summary>
    public bool IgnoreLegacyBorderSize { get; init; }
}

public static class WesnothMapParser
{
    public static WesnothMapDocument Parse(
        string mapText,
        WesnothMapParserOptions? options = null)
    {
        if (mapText is null) throw new ArgumentNullException(nameof(mapText));

        options ??= new WesnothMapParserOptions();
        ValidateOptions(options);

        var diagnostics = new List<WesnothMapDiagnostic>();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceLines = mapText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var gridLines = new List<(int SourceLine, string Text)>();
        var gridStarted = false;

        for (var lineIndex = 0; lineIndex < sourceLines.Length; lineIndex++)
        {
            var trimmed = sourceLines[lineIndex].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            if (!gridStarted && TryParseHeader(trimmed, out var key, out var value))
            {
                headers[key!] = value!;
                continue;
            }

            gridStarted = true;
            gridLines.Add((lineIndex, sourceLines[lineIndex]));
            if (gridLines.Count > options.MaxRows)
            {
                diagnostics.Add(new WesnothMapDiagnostic(
                    WesnothMapDiagnosticSeverity.Error,
                    $"Map exceeds the {options.MaxRows}-row safety limit.",
                    lineIndex));
                break;
            }
        }

        var borderSize = options.DefaultBorderSize;
        if (!options.IgnoreLegacyBorderSize && headers.TryGetValue("border_size", out var borderRaw) &&
            (!int.TryParse(borderRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out borderSize) ||
             borderSize < 0))
        {
            diagnostics.Add(new WesnothMapDiagnostic(
                WesnothMapDiagnosticSeverity.Error,
                $"Invalid non-negative border_size '{borderRaw}'."));
            borderSize = 0;
        }

        var parsedTokens = new List<(int SourceLine, IReadOnlyList<ParsedCell> Cells)>();
        var width = 0;
        long cellCount = 0;
        foreach (var (sourceLine, line) in gridLines)
        {
            var rawCells = line.Split(',');
            if (rawCells.Length > options.MaxColumns)
            {
                diagnostics.Add(new WesnothMapDiagnostic(
                    WesnothMapDiagnosticSeverity.Error,
                    $"Map row exceeds the {options.MaxColumns}-column safety limit.",
                    sourceLine));
                break;
            }

            var cells = new List<ParsedCell>(rawCells.Length);
            for (var columnIndex = 0; columnIndex < rawCells.Length; columnIndex++)
            {
                var token = rawCells[columnIndex].Trim();
                if (!TryParseCell(token, out var cell))
                {
                    diagnostics.Add(new WesnothMapDiagnostic(
                        WesnothMapDiagnosticSeverity.Error,
                        $"Map cell {columnIndex + 1} is empty or malformed.",
                        sourceLine));
                    cell = new ParsedCell("?", null, null);
                }

                cells.Add(cell!);
            }

            cellCount += cells.Count;
            if (cellCount > options.MaxCells)
            {
                diagnostics.Add(new WesnothMapDiagnostic(
                    WesnothMapDiagnosticSeverity.Error,
                    $"Map exceeds the {options.MaxCells:N0}-cell safety limit.",
                    sourceLine));
                break;
            }

            width = Math.Max(width, cells.Count);
            parsedTokens.Add((sourceLine, cells));
        }

        if (parsedTokens.Count == 0)
        {
            diagnostics.Add(new WesnothMapDiagnostic(
                WesnothMapDiagnosticSeverity.Error,
                "Map data contains no terrain rows."));
        }

        foreach (var (sourceLine, cells) in parsedTokens)
        {
            if (cells.Count != width)
            {
                diagnostics.Add(new WesnothMapDiagnostic(
                    WesnothMapDiagnosticSeverity.Warning,
                    $"Map row has {cells.Count} cells while the widest row has {width}; missing cells remain empty.",
                    sourceLine));
            }
        }

        if (borderSize * 2 >= width || borderSize * 2 >= parsedTokens.Count)
        {
            diagnostics.Add(new WesnothMapDiagnostic(
                WesnothMapDiagnosticSeverity.Warning,
                $"border_size={borderSize} leaves no playable map area."));
        }

        var rows = new List<IReadOnlyList<WesnothMapCell>>(parsedTokens.Count);
        for (var rowIndex = 0; rowIndex < parsedTokens.Count; rowIndex++)
        {
            var parsedRow = parsedTokens[rowIndex].Cells;
            var row = new List<WesnothMapCell>(parsedRow.Count);
            for (var columnIndex = 0; columnIndex < parsedRow.Count; columnIndex++)
            {
                var parsed = parsedRow[columnIndex];
                var x = columnIndex - borderSize + 1;
                var y = rowIndex - borderSize + 1;
                row.Add(new WesnothMapCell(
                    columnIndex,
                    rowIndex,
                    x,
                    y,
                    parsed.TerrainCode,
                    parsed.LocationId,
                    parsed.StartingPosition)
                {
                    IsBorder = x < 1 || y < 1 ||
                               x > width - (borderSize * 2) ||
                               y > parsedTokens.Count - (borderSize * 2)
                });
            }

            rows.Add(row);
        }

        return new WesnothMapDocument
        {
            BorderSize = borderSize,
            Usage = headers.GetValueOrDefault("usage"),
            Width = width,
            Height = rows.Count,
            Headers = headers,
            Rows = rows,
            Diagnostics = diagnostics
        };
    }

    private static bool TryParseHeader(string line, out string? key, out string? value)
    {
        key = null;
        value = null;
        if (line.Contains(','))
            return false;

        var equals = line.IndexOf('=');
        if (equals <= 0)
            return false;

        key = line[..equals].Trim();
        value = line[(equals + 1)..].Trim();
        return key.Length > 0;
    }

    private static bool TryParseCell(string token, out ParsedCell? cell)
    {
        cell = null;
        if (token.Length == 0)
            return false;

        var parts = token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            cell = new ParsedCell(parts[0], null, null);
            return true;
        }

        if (parts.Length != 2)
            return false;

        var locationId = parts[0];
        int? startingPosition = int.TryParse(
            locationId,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsedStart)
            ? parsedStart
            : null;
        cell = new ParsedCell(parts[1], locationId, startingPosition);
        return true;
    }

    private static void ValidateOptions(WesnothMapParserOptions options)
    {
        if (options.MaxRows <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaxColumns <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaxCells <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.DefaultBorderSize < 0) throw new ArgumentOutOfRangeException(nameof(options));
    }

    private sealed record ParsedCell(
        string TerrainCode,
        string? LocationId,
        int? StartingPosition);
}
