namespace Wcc.Core.Wml;

/// <summary>
/// One concrete macro definition visible to an expansion request. Package and
/// source fields are provenance only; the source WML remains authoritative.
/// </summary>
public sealed record WmlMacroExpansionDefinition(
    string Name,
    string RawText,
    string? SourcePath = null,
    string? PackageName = null,
    string? PackageRevisionId = null,
    int? SourceStartLine = null)
{
    /// <summary>Exact lexical domain preceding this captured definition, when known.</summary>
    public string? TextDomain { get; init; }

    public string SourceLabel
    {
        get
        {
            var package = string.IsNullOrWhiteSpace(PackageName) ? null : PackageName;
            var file = string.IsNullOrWhiteSpace(SourcePath) ? null : Path.GetFileName(SourcePath);
            var source = (package, file) switch
            {
                ({ } packageName, { } fileName) => $"{packageName} · {fileName}",
                ({ } packageName, null) => packageName,
                (null, { } fileName) => fileName,
                _ => "unknown source"
            };
            return SourceStartLine is { } line ? $"{source}:{line + 1}" : source;
        }
    }
}

public interface IWmlMacroExpansionDefinitionResolver
{
    IReadOnlyList<WmlMacroExpansionDefinition> FindDefinitions(string macroName);
}

public enum WmlMacroExpansionIssueKind
{
    Unresolved,
    Ambiguous,
    Unsupported,
    Limit
}

public sealed record WmlMacroExpansionIssue(
    WmlMacroExpansionIssueKind Kind,
    string Message);

public sealed record WmlMacroExpansionProvenance(
    string Id,
    string MacroName,
    string RawInvocation,
    IReadOnlyList<string> Arguments,
    string? SourcePath,
    string? PackageName,
    string? PackageRevisionId,
    int? DefinitionStartLine,
    int InvocationLineIndex,
    int Depth,
    string? ParentExpansionId)
{
    public string SourceLabel
    {
        get
        {
            var package = string.IsNullOrWhiteSpace(PackageName) ? null : PackageName;
            var file = string.IsNullOrWhiteSpace(SourcePath) ? null : Path.GetFileName(SourcePath);
            return (package, file) switch
            {
                ({ } packageName, { } fileName) => $"{packageName} · {fileName}",
                ({ } packageName, null) => packageName,
                (null, { } fileName) => fileName,
                _ => "unknown source"
            };
        }
    }
}

public sealed class WmlMacroExpansionResult
{
    public string ExpandedText { get; internal set; } = string.Empty;
    public List<WmlMacroExpansionProvenance> Expansions { get; } = new();
    public List<WmlMacroExpansionIssue> Issues { get; } = new();

    public WmlMacroExpansionProvenance? FindProvenance(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : Expansions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Creates a text projection that retains the shared provenance graph. It
    /// lets consumers work with one generated child block without expanding
    /// the same source again.
    /// </summary>
    public WmlMacroExpansionResult Project(string expandedText)
    {
        var projection = new WmlMacroExpansionResult { ExpandedText = expandedText };
        projection.Expansions.AddRange(Expansions);
        projection.Issues.AddRange(Issues);
        return projection;
    }

    /// <summary>
    /// Combines already-expanded source fragments into one effective preview
    /// document. Expansion identifiers are unique across requests made through
    /// one service instance, so provenance remains addressable after a WML
    /// inheritance merge.
    /// </summary>
    public static WmlMacroExpansionResult Compose(
        string expandedText,
        params WmlMacroExpansionResult[] sources)
    {
        var result = new WmlMacroExpansionResult { ExpandedText = expandedText };
        foreach (var source in sources)
        {
            foreach (var expansion in source.Expansions)
            {
                if (result.Expansions.All(x => !string.Equals(x.Id, expansion.Id, StringComparison.Ordinal)))
                    result.Expansions.Add(expansion);
            }

            foreach (var issue in source.Issues)
            {
                if (!result.Issues.Contains(issue))
                    result.Issues.Add(issue);
            }
        }

        return result;
    }
}

/// <summary>
/// Bounds for safe editor-side expansion. A request can choose tighter limits
/// without changing the expansion rules used by other editors.
/// </summary>
public sealed record WmlMacroExpansionOptions(
    int MaxExpansionDepth = 16,
    int MaxExpandedLines = 8192);

public sealed record WmlMacroInvocationExpansion(
    int SourceLineIndex,
    string RawInvocation,
    WmlMacroExpansionResult Expansion);

public interface IWmlMacroExpansionService
{
    WmlMacroExpansionResult Expand(
        string sourceText,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlMacroExpansionOptions? options = null);

    IReadOnlyList<WmlMacroInvocationExpansion> ExpandDirectChildInvocations(
        WmlEditableBlock owner,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlMacroExpansionOptions? options = null);

    WmlResolvedValueResult ResolveValue(
        string rawValue,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlResolvedValueOptions? options = null);
}

/// <summary>
/// Central, bounded and deliberately conservative editor-side macro expansion.
/// It is independent of animations, events, schedules, and UI concerns. Only
/// whole-line invocations are expanded; preprocessor control flow is reported
/// instead of guessed.
/// See docs/architecture.md for the separate captured and native evaluation paths.
/// </summary>
public sealed partial class WmlMacroExpansionService : IWmlMacroExpansionService
{
    public const string ProvenanceMarkerPrefix = "# wcc-expanded-macro:";
    private long _nextRequestOrdinal;

    public WmlMacroExpansionResult Expand(
        string sourceText,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlMacroExpansionOptions? options = null)
    {
        if (sourceText is null) throw new ArgumentNullException(nameof(sourceText));

        options ??= new WmlMacroExpansionOptions();
        if (options.MaxExpansionDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Expansion depth must be positive.");
        if (options.MaxExpandedLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Expanded line limit must be positive.");

        var newLine = sourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var result = new WmlMacroExpansionResult { ExpandedText = sourceText };
        var localByName = (localDefinitions ?? Array.Empty<WmlMacroExpansionDefinition>())
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<WmlMacroExpansionDefinition>)x.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var state = new ExpansionState(
            localByName,
            fallbackResolver,
            result,
            options,
            Interlocked.Increment(ref _nextRequestOrdinal));
        var lines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var expanded = ExpandLines(lines, state, depth: 0, parentExpansionId: null, rootLineIndex: null);
        result.ExpandedText = string.Join(newLine, expanded);
        return result;
    }

    /// <summary>
    /// Expands each whole-line macro invocation directly inside an owning WML
    /// block. This supports macros that generate child tags while retaining the
    /// invocation's line in the authoritative source.
    /// </summary>
    public IReadOnlyList<WmlMacroInvocationExpansion> ExpandDirectChildInvocations(
        WmlEditableBlock owner,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlMacroExpansionOptions? options = null)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));

        var definitions = localDefinitions?.ToArray();
        var result = new List<WmlMacroInvocationExpansion>();
        var depth = 0;
        for (var lineIndex = 0; lineIndex < owner.Lines.Count; lineIndex++)
        {
            var rawLine = owner.Lines[lineIndex];
            var trimmed = rawLine.Trim();
            if (IsClosingTag(trimmed))
                depth = Math.Max(0, depth - 1);

            if (depth == 1 &&
                TryReadWholeInvocation(
                    owner.Lines,
                    lineIndex,
                    out var sourceInvocation,
                    out _,
                    out var consumedLineCount))
            {
                var expansion = Expand(
                    sourceInvocation,
                    definitions,
                    fallbackResolver,
                    options);
                for (var i = 0; i < expansion.Expansions.Count; i++)
                {
                    expansion.Expansions[i] = expansion.Expansions[i] with
                    {
                        InvocationLineIndex = lineIndex
                    };
                }

                result.Add(new WmlMacroInvocationExpansion(
                    lineIndex,
                    sourceInvocation,
                    expansion));
                lineIndex += consumedLineCount - 1;
                continue;
            }

            if (IsOpeningTag(trimmed))
                depth++;
        }

        return result;
    }

    public static string? ReadProvenanceMarker(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(ProvenanceMarkerPrefix, StringComparison.Ordinal))
                return trimmed[ProvenanceMarkerPrefix.Length..].Trim();
        }

        return null;
    }

    public static string? ReadLeadingProvenanceMarker(IReadOnlyList<string> lines)
    {
        if (lines.Count < 2)
            return null;

        for (var i = 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.StartsWith(ProvenanceMarkerPrefix, StringComparison.Ordinal))
                return trimmed[ProvenanceMarkerPrefix.Length..].Trim();
            return null;
        }

        return null;
    }

    private static List<string> ExpandLines(
        IReadOnlyList<string> lines,
        ExpansionState state,
        int depth,
        string? parentExpansionId,
        int? rootLineIndex)
    {
        var output = new List<string>();
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (output.Count >= state.Options.MaxExpandedLines)
            {
                state.AddIssueOnce(
                    WmlMacroExpansionIssueKind.Limit,
                    $"Macro expansion exceeded {state.Options.MaxExpandedLines} lines and was truncated.");
                break;
            }

            if (!TryReadWholeInvocation(
                    lines,
                    lineIndex,
                    out var sourceInvocation,
                    out var invocation,
                    out var consumedLineCount))
            {
                output.Add(lines[lineIndex]);
                continue;
            }

            var invocationLineIndex = rootLineIndex ?? lineIndex;
            var expandedInvocation = ExpandInvocation(
                sourceInvocation,
                invocation!,
                state,
                depth,
                parentExpansionId,
                invocationLineIndex);
            output.AddRange(expandedInvocation);
            lineIndex += consumedLineCount - 1;
        }

        return output;
    }

    private static bool TryReadWholeInvocation(
        IReadOnlyList<string> lines,
        int startLineIndex,
        out string sourceInvocation,
        out WmlMacroInvocationInfo? invocation,
        out int consumedLineCount)
        => WmlWholeLineMacroInvocationReader.TryRead(
            lines,
            startLineIndex,
            out sourceInvocation,
            out invocation,
            out consumedLineCount);

    private static IReadOnlyList<string> ExpandInvocation(
        string sourceLine,
        WmlMacroInvocationInfo invocation,
        ExpansionState state,
        int depth,
        string? parentExpansionId,
        int invocationLineIndex)
    {
        if (depth >= state.Options.MaxExpansionDepth)
        {
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Limit,
                $"Macro '{{{invocation.Name}}}' exceeded the expansion depth of {state.Options.MaxExpansionDepth}.");
            return [sourceLine];
        }

        var definitions = state.FindDefinitions(invocation.Name);
        if (definitions.Count == 0)
        {
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Unresolved,
                $"Macro '{{{invocation.Name}}}' has no visible definition and remains unexpanded.");
            return [sourceLine];
        }

        if (definitions.Count > 1)
        {
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Ambiguous,
                $"Macro '{{{invocation.Name}}}' has {definitions.Count} equally visible definitions and remains unexpanded.");
            return [sourceLine];
        }

        var definition = definitions[0];
        if (!WmlMacroDefinitionTemplate.TryParse(definition, out var template, out var parseProblem))
        {
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Unsupported,
                $"Macro '{{{invocation.Name}}}' was not expanded: {parseProblem}");
            return [sourceLine];
        }

        if (template!.BodyLines.Any(WmlMacroDefinitionTemplate.IsPreprocessorControlLine))
        {
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Unsupported,
                $"Macro '{{{invocation.Name}}}' contains preprocessor control flow; Wesnoth validation is required.");
            return [sourceLine];
        }

        if (!template.TryResolveArguments(
                invocation.Arguments,
                out var resolvedArguments,
                out var argumentProblem))
        {
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Unsupported,
                $"Macro '{{{invocation.Name}}}' {argumentProblem}.");
            return [sourceLine];
        }

        var expansionId = $"macro-{state.RequestOrdinal}-{state.NextExpansionOrdinal++}";
        var provenance = new WmlMacroExpansionProvenance(
            expansionId,
            invocation.Name,
            invocation.RawText,
            invocation.Arguments,
            definition.SourcePath,
            definition.PackageName,
            definition.PackageRevisionId,
            definition.SourceStartLine,
            invocationLineIndex,
            depth,
            parentExpansionId);
        state.Result.Expansions.Add(provenance);

        var indentation = sourceLine[..(sourceLine.Length - sourceLine.TrimStart().Length)];
        var substitutedLines = template.SubstituteBody(resolvedArguments);
        var continuationLines = WmlMultilineTextScanner.FindContinuationLines(substitutedLines);
        var bodyIndent = substitutedLines
            .Where((line, index) =>
                !continuationLines[index] && !string.IsNullOrWhiteSpace(line))
            .Select(CountLeadingWhitespace)
            .DefaultIfEmpty(0)
            .Min();
        var output = new List<string>();
        for (var lineIndex = 0; lineIndex < substitutedLines.Count; lineIndex++)
        {
            var bodyLine = substitutedLines[lineIndex];
            var isContinuation = continuationLines[lineIndex];
            var indented = bodyLine.Length == 0
                ? string.Empty
                : isContinuation
                    ? bodyLine
                    : indentation + RemoveLeadingWhitespace(bodyLine, bodyIndent);
            var withoutComment = WmlInlineCommentSplitter.Split(indented).Value;
            if (!isContinuation &&
                WmlMacroInvocationParser.TryParseWholeInvocation(withoutComment, out var nestedInvocation))
            {
                output.AddRange(ExpandInvocation(
                    indented,
                    nestedInvocation!,
                    state,
                    depth + 1,
                    expansionId,
                    invocationLineIndex));
                continue;
            }

            output.Add(indented);
            if (!isContinuation && IsOpeningTag(indented))
            {
                var tagIndent = indented[..CountLeadingWhitespace(indented)];
                output.Add(tagIndent + "    " + ProvenanceMarkerPrefix + expansionId);
            }
        }

        return output;
    }

    private static int CountLeadingWhitespace(string line)
    {
        var count = 0;
        while (count < line.Length && char.IsWhiteSpace(line[count]))
            count++;
        return count;
    }

    private static string RemoveLeadingWhitespace(string line, int count)
    {
        var removable = Math.Min(count, CountLeadingWhitespace(line));
        return line[removable..];
    }

    private static bool IsOpeningTag(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            return false;

        var inner = trimmed[1..^1].Trim();
        if (inner.Length == 0 || inner.StartsWith('/'))
            return false;
        return true;
    }

    private static bool IsClosingTag(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("[/", StringComparison.Ordinal) &&
               trimmed.EndsWith(']');
    }

    private sealed class ExpansionState(
        IReadOnlyDictionary<string, IReadOnlyList<WmlMacroExpansionDefinition>> localDefinitions,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver,
        WmlMacroExpansionResult result,
        WmlMacroExpansionOptions options,
        long requestOrdinal)
    {
        public WmlMacroExpansionResult Result { get; } = result;
        public WmlMacroExpansionOptions Options { get; } = options;
        public long RequestOrdinal { get; } = requestOrdinal;
        public int NextExpansionOrdinal { get; set; } = 1;

        public IReadOnlyList<WmlMacroExpansionDefinition> FindDefinitions(string name)
        {
            if (localDefinitions.TryGetValue(name, out var local) && local.Count > 0)
                return local;
            return fallbackResolver?.FindDefinitions(name) ?? Array.Empty<WmlMacroExpansionDefinition>();
        }

        public void AddIssueOnce(WmlMacroExpansionIssueKind kind, string message)
        {
            if (Result.Issues.Any(x => x.Kind == kind && string.Equals(x.Message, message, StringComparison.Ordinal)))
                return;
            Result.Issues.Add(new WmlMacroExpansionIssue(kind, message));
        }
    }
}
