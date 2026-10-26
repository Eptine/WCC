namespace Wcc.Core.Wml;

/// <summary>
/// Compact provenance retained from Wesnoth's <c>.plain</c> output. The scenario id
/// is the effective id after preprocessing, including ids produced by macros. Source
/// paths are verified, absolute dependency-manifest paths present in Wesnoth's line
/// stacks contributing text to the scenario opening tag. This supports owner
/// navigation, not an edit capability.
/// </summary>
public sealed record WesnothPreprocessedScenarioOrigin(
    string? ScenarioId,
    IReadOnlyList<string> SourcePaths);

public static class WesnothPreprocessedScenarioOriginReader
{
    public static async Task<IReadOnlyList<WesnothPreprocessedScenarioOrigin>> ReadAsync(
        string plainOutputPath,
        IReadOnlyList<string> verifiedSourcePaths,
        IReadOnlyList<string> routingRootPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainOutputPath);
        ArgumentNullException.ThrowIfNull(verifiedSourcePaths);
        ArgumentNullException.ThrowIfNull(routingRootPaths);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(plainOutputPath))
            return [];

        var candidates = BuildCandidates(verifiedSourcePaths, routingRootPaths);
        var origins = new List<WesnothPreprocessedScenarioOrigin>();
        PendingScenarioOrigin? currentScenario = null;
        await WesnothPlainSourceReader.ReadAsync(
            plainOutputPath,
            line => ProcessLogicalLine(line, candidates, origins, ref currentScenario),
            cancellationToken).ConfigureAwait(false);
        FinalizeScenario(origins, ref currentScenario);
        return origins;
    }

    private static void ProcessLogicalLine(
        WesnothPlainLogicalLine line,
        IReadOnlyList<SourceCandidate> candidates,
        List<WesnothPreprocessedScenarioOrigin> origins,
        ref PendingScenarioOrigin? currentScenario)
    {
        var wmlLine = line.Text;
        var trimmed = wmlLine.AsSpan().Trim();
        if (TryReadTag(trimmed, out var tagName, out var closing) &&
            !closing &&
            tagName.Equals("scenario", StringComparison.OrdinalIgnoreCase))
        {
            // Root-scenario provenance is independent. If malformed or fragmented
            // WML left the previous local scanner inside a string, a later verified
            // scenario start must still be discoverable rather than inheriting that
            // corrupted state. Selection later joins path + effective id and refuses
            // duplicate source mappings, so tag-shaped string content fails closed.
            FinalizeScenario(origins, ref currentScenario);
            currentScenario = new PendingScenarioOrigin(ResolveTagSources(line, candidates));
            return;
        }

        if (currentScenario is null)
            return;

        if (!currentScenario.StringState.InsideMultilineString &&
            TryReadTag(trimmed, out tagName, out closing))
        {
            if (closing)
            {
                if (currentScenario.Depth == 1 &&
                    tagName.Equals("scenario", StringComparison.OrdinalIgnoreCase))
                {
                    FinalizeScenario(origins, ref currentScenario);
                    return;
                }

                currentScenario.Depth = Math.Max(1, currentScenario.Depth - 1);
            }
            else
            {
                currentScenario.Depth++;
            }
        }
        else if (!currentScenario.StringState.InsideMultilineString &&
                 currentScenario.Depth == 1 &&
                 TryReadDirectId(trimmed, out var scenarioId))
        {
            if (currentScenario.ScenarioId is not null)
                currentScenario.HasMultipleIds = true;
            else
                currentScenario.ScenarioId = scenarioId;
        }

        var stringState = currentScenario.StringState;
        WmlRawStringScanner.UpdateState(wmlLine, ref stringState);
        currentScenario.StringState = stringState;
    }

    private static void FinalizeScenario(
        List<WesnothPreprocessedScenarioOrigin> origins,
        ref PendingScenarioOrigin? currentScenario)
    {
        if (currentScenario is null)
            return;

        origins.Add(new WesnothPreprocessedScenarioOrigin(
            currentScenario.HasMultipleIds ? null : currentScenario.ScenarioId,
            currentScenario.SourcePaths));
        currentScenario = null;
    }

    private static bool TryReadDirectId(ReadOnlySpan<char> line, out string scenarioId)
    {
        scenarioId = string.Empty;
        if (line.IsEmpty || line[0] is '#' or '{' or '!')
            return false;
        var equals = line.IndexOf('=');
        if (equals <= 0 || !line[..equals].Trim().Equals("id", StringComparison.OrdinalIgnoreCase))
            return false;

        var rawValue = line[(equals + 1)..].Trim();
        var comment = rawValue.IndexOf('#');
        if (comment >= 0)
            rawValue = rawValue[..comment].TrimEnd();
        scenarioId = WmlDisplayStringNormalizer.Normalize(rawValue.ToString()) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(scenarioId);
    }

    private static IReadOnlyList<string> ResolveTagSources(
        WesnothPlainLogicalLine line,
        IReadOnlyList<SourceCandidate> candidates)
    {
        // Commands can enter and leave a macro without producing text. Only
        // markers attached to the opening tag's actual characters contribute to
        // this compact owner map. A later return, comment or indentation must
        // not retroactively claim the tag. Richer macro proof consumes Commands
        // separately through the shared reader.
        var tagStart = line.Text.IndexOf('[');
        var tagEnd = line.Text.IndexOf(']', tagStart) + 1;
        var paths = new HashSet<string>(PathComparer);
        var coveredCharacters = 0;
        foreach (var segment in line.Segments)
        {
            var start = Math.Max(tagStart, segment.TextOffset);
            var end = Math.Min(tagEnd, segment.TextOffset + segment.Text.Length);
            if (start >= end)
                continue;
            if (segment.RawLineMarker is null)
                return [];
            var sources = ResolveMarkerSources(segment.RawLineMarker, candidates);
            if (sources.Count == 0)
                return [];
            paths.UnionWith(sources);
            coveredCharacters += end - start;
        }
        return coveredCharacters == tagEnd - tagStart
            ? paths.Order(PathComparer).ToArray()
            : [];
    }

    private static IReadOnlyList<SourceCandidate> BuildCandidates(
        IReadOnlyList<string> verifiedSourcePaths,
        IReadOnlyList<string> routingRootPaths)
    {
        var roots = routingRootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        var candidates = new List<SourceCandidate>();
        foreach (var rawPath in verifiedSourcePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(PathComparer))
        {
            var fullPath = Path.GetFullPath(rawPath);
            var variants = new HashSet<string>(PathComparer)
            {
                NormalizePath(fullPath)
            };
            foreach (var root in roots)
            {
                if (!IsInsideOrEqual(fullPath, root))
                    continue;
                var relative = NormalizePath(Path.GetRelativePath(root, fullPath));
                variants.Add(relative);
                if (string.Equals(
                        Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
                        "add-ons",
                        StringComparison.OrdinalIgnoreCase))
                {
                    variants.Add("~add-ons/" + relative);
                    variants.Add("add-ons/" + relative);
                }
            }

            candidates.Add(new SourceCandidate(
                fullPath,
                variants.OrderByDescending(x => x.Length).ToArray()));
        }

        return candidates;
    }

    private static IReadOnlyList<string> ResolveMarkerSources(
        ReadOnlySpan<char> marker,
        IReadOnlyList<SourceCandidate> candidates)
    {
        var normalizedMarker = marker.ToString().Replace('\\', '/');
        var matches = new List<SourceMatch>();
        foreach (var candidate in candidates)
        {
            foreach (var variant in candidate.MarkerVariants)
            {
                var searchStart = 0;
                while (searchStart < normalizedMarker.Length)
                {
                    var index = normalizedMarker.IndexOf(
                        variant,
                        searchStart,
                        PathComparison);
                    if (index < 0)
                        break;
                    var end = index + variant.Length;
                    if (HasMarkerBoundary(normalizedMarker, index, end))
                    {
                        matches.Add(new SourceMatch(candidate.FullPath, index, end));
                        break;
                    }

                    searchStart = index + 1;
                }
            }
        }

        return matches
            .Where(match => !matches.Any(other =>
                !PathComparer.Equals(match.FullPath, other.FullPath) &&
                other.Start <= match.Start &&
                other.End >= match.End &&
                other.End - other.Start > match.End - match.Start))
            .Select(match => match.FullPath)
            .Distinct(PathComparer)
            .Order(PathComparer)
            .ToArray();
    }

    private static bool HasMarkerBoundary(string marker, int start, int end) =>
        (start == 0 || char.IsWhiteSpace(marker[start - 1])) &&
        (end == marker.Length || char.IsWhiteSpace(marker[end]));

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

    private static bool IsInsideOrEqual(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return string.Equals(relative, ".", PathComparison) ||
               (!Path.IsPathRooted(relative) &&
                !string.Equals(relative, "..", PathComparison) &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private sealed record SourceCandidate(
        string FullPath,
        IReadOnlyList<string> MarkerVariants);

    private sealed record SourceMatch(string FullPath, int Start, int End);

    private sealed class PendingScenarioOrigin(IReadOnlyList<string> sourcePaths)
    {
        public int Depth { get; set; } = 1;

        public string? ScenarioId { get; set; }

        public bool HasMultipleIds { get; set; }

        public WmlStringScanState StringState { get; set; }

        public IReadOnlyList<string> SourcePaths { get; } = sourcePaths;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
