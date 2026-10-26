namespace Wcc.Core.Wml;

public enum WmlPreprocessedScenarioSelectionState
{
    Found,
    Missing,
    Ambiguous
}

public sealed record WmlPreprocessedScenarioSelection(
    WmlPreprocessedScenarioSelectionState State,
    string? RawWml,
    int? StartIndex,
    int? EndIndexExclusive,
    int MatchCount,
    string Diagnostic)
{
    public bool IsFound => State == WmlPreprocessedScenarioSelectionState.Found;
}

/// <summary>
/// Selects one root [scenario] from Wesnoth's normalized preprocessor output.
/// It scans the large combined output once and materializes only the selected
/// block, rather than building a projection tree for the complete data set.
/// </summary>
public static class WmlPreprocessedScenarioSelector
{
    public static WmlPreprocessedScenarioSelection Select(
        string normalizedWml,
        string scenarioId)
    {
        ArgumentNullException.ThrowIfNull(normalizedWml);
        if (string.IsNullOrWhiteSpace(scenarioId))
            throw new ArgumentException("A scenario id is required.", nameof(scenarioId));

        var wantedId = scenarioId.Trim();
        var scenarios = ScanRootScenarios(normalizedWml);
        var matches = scenarios
            .Where(scenario => string.Equals(scenario.Id, wantedId, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 1)
        {
            var match = matches[0];
            return Found(
                normalizedWml,
                match,
                $"Selected scenario '{wantedId}' from Wesnoth's normalized output.");
        }

        if (matches.Length > 1)
        {
            return new WmlPreprocessedScenarioSelection(
                WmlPreprocessedScenarioSelectionState.Ambiguous,
                null,
                null,
                null,
                matches.Length,
                $"Wesnoth's normalized output contains {matches.Length} scenarios with id '{wantedId}'; WCC will not guess which one is active.");
        }

        return new WmlPreprocessedScenarioSelection(
            WmlPreprocessedScenarioSelectionState.Missing,
            null,
            null,
            null,
            0,
            $"Wesnoth's normalized output does not contain scenario id '{wantedId}' in the selected context.");
    }

    /// <summary>
    /// Selects a scenario through the verified source-line stack from Wesnoth's
    /// companion .plain output. Provenance is joined to normalized output by the
    /// effective preprocessed id; it never assumes that global root ordinals in the
    /// two representations agree.
    /// </summary>
    public static WmlPreprocessedScenarioSelection SelectBySourcePath(
        string normalizedWml,
        IReadOnlyList<WesnothPreprocessedScenarioOrigin> scenarioOrigins,
        string sourcePath,
        string? scenarioId = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedWml);
        ArgumentNullException.ThrowIfNull(scenarioOrigins);
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("A scenario source path is required.", nameof(sourcePath));

        var wantedPath = Path.GetFullPath(sourcePath);
        var scenarios = ScanRootScenarios(normalizedWml);
        var sourceMatches = scenarioOrigins
            .Where(origin => origin.SourcePaths.Any(path => PathsEqual(path, wantedPath)))
            .ToArray();
        if (sourceMatches.Length == 0)
        {
            return new WmlPreprocessedScenarioSelection(
                WmlPreprocessedScenarioSelectionState.Missing,
                null,
                null,
                null,
                0,
                "Wesnoth's source map does not connect this file to a normalized root scenario in the selected context.");
        }

        if (sourceMatches.Any(origin => string.IsNullOrWhiteSpace(origin.ScenarioId)))
        {
            return new WmlPreprocessedScenarioSelection(
                WmlPreprocessedScenarioSelectionState.Missing,
                null,
                null,
                null,
                0,
                "Wesnoth's source map connects this file to a root scenario without a resolved id; source-based selection was refused.");
        }

        string effectiveId;
        if (!string.IsNullOrWhiteSpace(scenarioId))
        {
            var wantedId = scenarioId.Trim();
            var sourceIdMatches = sourceMatches
                .Where(origin => string.Equals(
                    origin.ScenarioId,
                    wantedId,
                    StringComparison.Ordinal))
                .ToArray();
            if (sourceIdMatches.Length > 1)
            {
                return new WmlPreprocessedScenarioSelection(
                    WmlPreprocessedScenarioSelectionState.Ambiguous,
                    null,
                    null,
                    null,
                    sourceIdMatches.Length,
                    $"Wesnoth's source map connects this file to {sourceIdMatches.Length} root scenarios with id '{wantedId}'; WCC will not guess which one is active.");
            }
            if (sourceIdMatches.Length == 0)
            {
                return new WmlPreprocessedScenarioSelection(
                    WmlPreprocessedScenarioSelectionState.Missing,
                    null,
                    null,
                    null,
                    0,
                    $"Wesnoth's normalized scenario id '{wantedId}' is not connected to this source file; source-based selection was refused.");
            }

            effectiveId = wantedId;
        }
        else
        {
            if (sourceMatches.Length > 1)
            {
                return new WmlPreprocessedScenarioSelection(
                    WmlPreprocessedScenarioSelectionState.Ambiguous,
                    null,
                    null,
                    null,
                    sourceMatches.Length,
                    $"Wesnoth's source map connects this file to {sourceMatches.Length} root scenarios; WCC will not guess which one is active.");
            }

            effectiveId = sourceMatches[0].ScenarioId!;
        }

        var normalizedMatches = scenarios
            .Where(scenario => string.Equals(
                scenario.Id,
                effectiveId,
                StringComparison.Ordinal))
            .ToArray();
        if (normalizedMatches.Length == 0)
        {
            return new WmlPreprocessedScenarioSelection(
                WmlPreprocessedScenarioSelectionState.Missing,
                null,
                null,
                null,
                0,
                $"Wesnoth's source map resolved scenario id '{effectiveId}', but normalized output does not contain it; source-based selection was refused.");
        }
        if (normalizedMatches.Length > 1)
        {
            return new WmlPreprocessedScenarioSelection(
                WmlPreprocessedScenarioSelectionState.Ambiguous,
                null,
                null,
                null,
                normalizedMatches.Length,
                $"Wesnoth's normalized output contains {normalizedMatches.Length} root scenarios with id '{effectiveId}'; source-based selection was refused.");
        }

        return Found(
            normalizedWml,
            normalizedMatches[0],
            $"Selected scenario '{effectiveId}' through Wesnoth's verified source map.");
    }

    private static IReadOnlyList<ScenarioBlock> ScanRootScenarios(string normalizedWml)
    {
        var depth = 0;
        var scenarioStart = -1;
        string? currentScenarioId = null;
        var scenarios = new List<ScenarioBlock>();
        var stringState = new WmlStringScanState();

        for (var lineStart = 0; lineStart <= normalizedWml.Length;)
        {
            var newline = normalizedWml.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? normalizedWml.Length : newline;
            var nextLine = newline < 0 ? normalizedWml.Length + 1 : newline + 1;
            var line = normalizedWml.AsSpan(lineStart, lineEnd - lineStart).Trim();
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1].TrimEnd();

            if (!stringState.InsideMultilineString &&
                TryReadTag(line, out var tagName, out var closing))
            {
                if (closing)
                {
                    if (scenarioStart >= 0 &&
                        depth == 1 &&
                        tagName.Equals("scenario", StringComparison.OrdinalIgnoreCase))
                    {
                        var blockEnd = lineEnd;
                        if (newline >= 0)
                            blockEnd = newline + 1;
                        scenarios.Add(new ScenarioBlock(
                            scenarios.Count,
                            scenarioStart,
                            blockEnd,
                            currentScenarioId));
                        scenarioStart = -1;
                        currentScenarioId = null;
                    }
                    depth = Math.Max(0, depth - 1);
                }
                else
                {
                    if (scenarioStart < 0 &&
                        depth == 0 &&
                        tagName.Equals("scenario", StringComparison.OrdinalIgnoreCase))
                    {
                        scenarioStart = lineStart;
                        currentScenarioId = null;
                    }
                    depth++;
                }
            }
            else if (!stringState.InsideMultilineString && scenarioStart >= 0 && depth == 1 &&
                      TryReadDirectId(line, out var id))
            {
                currentScenarioId = id;
            }

            WmlRawStringScanner.UpdateState(line, ref stringState);

            if (newline < 0)
                break;
            lineStart = nextLine;
        }

        return scenarios;
    }

    private static WmlPreprocessedScenarioSelection Found(
        string normalizedWml,
        ScenarioBlock match,
        string diagnostic) => new(
        WmlPreprocessedScenarioSelectionState.Found,
        normalizedWml[match.Start..match.End],
        match.Start,
        match.End,
        1,
        diagnostic);

    private static bool TryReadDirectId(ReadOnlySpan<char> line, out string? id)
    {
        id = null;
        if (line.IsEmpty || line[0] is '#' or '{' or '!')
            return false;
        var equals = line.IndexOf('=');
        if (equals <= 0 || !line[..equals].Trim().Equals("id", StringComparison.OrdinalIgnoreCase))
            return false;

        var rawValue = line[(equals + 1)..].Trim();
        var comment = rawValue.IndexOf('#');
        if (comment >= 0)
            rawValue = rawValue[..comment].TrimEnd();
        id = WmlDisplayStringNormalizer.Normalize(rawValue.ToString());
        return !string.IsNullOrWhiteSpace(id);
    }

    private static bool TryReadTag(
        ReadOnlySpan<char> line,
        out ReadOnlySpan<char> tagName,
        out bool closing)
    {
        tagName = default;
        closing = false;
        if (line.Length < 3 || line[0] != '[')
            return false;

        var closingBracket = line.IndexOf(']');
        if (closingBracket < 2)
            return false;
        var suffix = line[(closingBracket + 1)..].TrimStart();
        if (!suffix.IsEmpty && suffix[0] != '#')
            return false;

        var inside = line[1..closingBracket].Trim();
        closing = !inside.IsEmpty && inside[0] == '/';
        if (closing)
            inside = inside[1..].TrimStart();
        if (inside.IsEmpty)
            return false;
        var whitespace = inside.IndexOfAny(' ', '\t');
        tagName = whitespace < 0 ? inside : inside[..whitespace];
        return !tagName.IsEmpty;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private sealed record ScenarioBlock(
        int Ordinal,
        int Start,
        int End,
        string? Id);
}
