using System.Text;

namespace Wcc.Core.Wml;

public enum WmlResolvedValueState
{
    Literal,
    Resolved,
    Unresolved,
    Ambiguous,
    Unsupported,
    Limit
}

public sealed record WmlResolvedValueOptions(
    int MaxExpansionDepth = 16,
    int MaxExpandedCharacters = 65536);

public sealed record WmlResolvedValueMacroUse(
    string MacroName,
    string RawInvocation,
    WmlResolvedValueState State,
    IReadOnlyList<WmlMacroExpansionDefinition> CandidateDefinitions,
    WmlMacroExpansionProvenance? Provenance);

public sealed class WmlResolvedValueResult
{
    public required string RawValue { get; init; }
    public string EffectiveValue { get; internal set; } = string.Empty;
    public WmlResolvedValueState State { get; internal set; }
    public List<WmlResolvedValueMacroUse> MacroUses { get; } = new();
    public List<WmlMacroExpansionIssue> Issues { get; } = new();

    public IReadOnlyList<WmlMacroExpansionProvenance> Expansions => MacroUses
        .Select(x => x.Provenance)
        .Where(x => x is not null)
        .Cast<WmlMacroExpansionProvenance>()
        .ToArray();

    public IReadOnlyList<WmlMacroExpansionDefinition> SourceDefinitions => MacroUses
        .SelectMany(x => x.CandidateDefinitions)
        .DistinctBy(x => new
        {
            x.Name,
            x.SourcePath,
            x.PackageName,
            x.PackageRevisionId,
            x.SourceStartLine
        })
        .ToArray();
}

public sealed partial class WmlMacroExpansionService
{
    /// <summary>
    /// Resolves macro invocations embedded anywhere inside one raw WML value.
    /// This is a read-only textual preprocessor projection; variables, formulas,
    /// conditional preprocessing, and WML runtime semantics are not evaluated.
    /// </summary>
    public WmlResolvedValueResult ResolveValue(
        string rawValue,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlResolvedValueOptions? options = null)
    {
        if (rawValue is null) throw new ArgumentNullException(nameof(rawValue));

        options ??= new WmlResolvedValueOptions();
        if (options.MaxExpansionDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Expansion depth must be positive.");
        if (options.MaxExpandedCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Expanded character limit must be positive.");

        var result = new WmlResolvedValueResult
        {
            RawValue = rawValue,
            EffectiveValue = rawValue,
            State = WmlResolvedValueState.Literal
        };
        var localByName = (localDefinitions ?? Array.Empty<WmlMacroExpansionDefinition>())
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<WmlMacroExpansionDefinition>)x.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var state = new ResolvedValueExpansionState(
            localByName,
            fallbackResolver,
            result,
            options,
            Interlocked.Increment(ref _nextRequestOrdinal));

        var expansion = ExpandValueText(rawValue, state, depth: 0, parentExpansionId: null);
        result.EffectiveValue = expansion.ExceededLimit ? rawValue : expansion.Value;

        if (state.SawMalformedInvocation)
        {
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Unsupported,
                "The value contains an incomplete or malformed macro invocation and remains partially unresolved.");
        }

        result.State = DetermineResolvedValueState(result);
        return result;
    }

    private static ValueExpansion ExpandValueText(
        string text,
        ResolvedValueExpansionState state,
        int depth,
        string? parentExpansionId)
    {
        var output = new StringBuilder(Math.Min(text.Length, state.Options.MaxExpandedCharacters));
        var cursor = 0;
        while (cursor < text.Length)
        {
            var openingBrace = text.IndexOf('{', cursor);
            if (openingBrace < 0)
            {
                output.Append(text, cursor, text.Length - cursor);
                break;
            }

            output.Append(text, cursor, openingBrace - cursor);
            var remaining = text[openingBrace..];
            if (!WmlMacroInvocationParser.TryParseLeadingInvocation(
                    remaining,
                    out var invocation,
                    out var consumedLength))
            {
                state.SawMalformedInvocation = true;
                output.Append('{');
                cursor = openingBrace + 1;
                continue;
            }

            var replacement = ExpandValueInvocation(
                invocation!,
                state,
                depth,
                parentExpansionId);
            output.Append(replacement.Value);
            cursor = openingBrace + consumedLength;

            if (replacement.ExceededLimit || output.Length > state.Options.MaxExpandedCharacters)
            {
                state.AddIssueOnce(
                    WmlMacroExpansionIssueKind.Limit,
                    $"Value macro expansion exceeded {state.Options.MaxExpandedCharacters} characters and was abandoned.");
                return new ValueExpansion(text, ExceededLimit: true);
            }
        }

        if (output.Length > state.Options.MaxExpandedCharacters)
        {
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Limit,
                $"Value macro expansion exceeded {state.Options.MaxExpandedCharacters} characters and was abandoned.");
            return new ValueExpansion(text, ExceededLimit: true);
        }

        return new ValueExpansion(output.ToString(), ExceededLimit: false);
    }

    private static ValueExpansion ExpandValueInvocation(
        WmlMacroInvocationInfo invocation,
        ResolvedValueExpansionState state,
        int depth,
        string? parentExpansionId)
    {
        if (depth >= state.Options.MaxExpansionDepth)
        {
            state.Result.MacroUses.Add(new WmlResolvedValueMacroUse(
                invocation.Name,
                invocation.RawText,
                WmlResolvedValueState.Limit,
                Array.Empty<WmlMacroExpansionDefinition>(),
                null));
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Limit,
                $"Value macro '{{{invocation.Name}}}' exceeded the expansion depth of {state.Options.MaxExpansionDepth}.");
            return new ValueExpansion(invocation.RawText, ExceededLimit: true);
        }

        var definitions = state.FindDefinitions(invocation.Name);
        if (definitions.Count == 0)
        {
            state.Result.MacroUses.Add(new WmlResolvedValueMacroUse(
                invocation.Name,
                invocation.RawText,
                WmlResolvedValueState.Unresolved,
                definitions,
                null));
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Unresolved,
                $"Value macro '{{{invocation.Name}}}' has no visible definition and remains unexpanded.");
            return new ValueExpansion(invocation.RawText, ExceededLimit: false);
        }

        if (definitions.Count > 1)
        {
            state.Result.MacroUses.Add(new WmlResolvedValueMacroUse(
                invocation.Name,
                invocation.RawText,
                WmlResolvedValueState.Ambiguous,
                definitions,
                null));
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Ambiguous,
                $"Value macro '{{{invocation.Name}}}' has {definitions.Count} equally visible definitions and remains unexpanded.");
            return new ValueExpansion(invocation.RawText, ExceededLimit: false);
        }

        var definition = definitions[0];
        if (!WmlMacroDefinitionTemplate.TryParse(definition, out var template, out var parseProblem))
        {
            state.Result.MacroUses.Add(new WmlResolvedValueMacroUse(
                invocation.Name,
                invocation.RawText,
                WmlResolvedValueState.Unsupported,
                definitions,
                null));
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Unsupported,
                $"Value macro '{{{invocation.Name}}}' was not expanded: {parseProblem}.");
            return new ValueExpansion(invocation.RawText, ExceededLimit: false);
        }

        if (template!.BodyLines.Any(WmlMacroDefinitionTemplate.IsPreprocessorControlLine))
        {
            state.Result.MacroUses.Add(new WmlResolvedValueMacroUse(
                invocation.Name,
                invocation.RawText,
                WmlResolvedValueState.Unsupported,
                definitions,
                null));
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Unsupported,
                $"Value macro '{{{invocation.Name}}}' contains preprocessor control flow; Wesnoth validation is required.");
            return new ValueExpansion(invocation.RawText, ExceededLimit: false);
        }

        if (!template.TryResolveArguments(
                invocation.Arguments,
                out var resolvedArguments,
                out var argumentProblem))
        {
            state.Result.MacroUses.Add(new WmlResolvedValueMacroUse(
                invocation.Name,
                invocation.RawText,
                WmlResolvedValueState.Unsupported,
                definitions,
                null));
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Unsupported,
                $"Value macro '{{{invocation.Name}}}' {argumentProblem}.");
            return new ValueExpansion(invocation.RawText, ExceededLimit: false);
        }

        var expansionId = $"value-macro-{state.RequestOrdinal}-{state.NextExpansionOrdinal++}";
        var provenance = new WmlMacroExpansionProvenance(
            expansionId,
            invocation.Name,
            invocation.RawText,
            invocation.Arguments,
            definition.SourcePath,
            definition.PackageName,
            definition.PackageRevisionId,
            definition.SourceStartLine,
            InvocationLineIndex: 0,
            depth,
            parentExpansionId);
        state.Result.MacroUses.Add(new WmlResolvedValueMacroUse(
            invocation.Name,
            invocation.RawText,
            WmlResolvedValueState.Resolved,
            definitions,
            provenance));

        var substitutedBody = string.Join("\n", template.SubstituteBody(resolvedArguments)).Trim();
        if (substitutedBody.Length > state.Options.MaxExpandedCharacters)
        {
            state.AddIssueOnce(
                WmlMacroExpansionIssueKind.Limit,
                $"Value macro '{{{invocation.Name}}}' exceeds the {state.Options.MaxExpandedCharacters}-character expansion limit.");
            return new ValueExpansion(invocation.RawText, ExceededLimit: true);
        }

        return ExpandValueText(substitutedBody, state, depth + 1, expansionId);
    }

    private static WmlResolvedValueState DetermineResolvedValueState(WmlResolvedValueResult result)
    {
        if (result.Issues.Any(x => x.Kind == WmlMacroExpansionIssueKind.Limit))
            return WmlResolvedValueState.Limit;
        if (result.Issues.Any(x => x.Kind == WmlMacroExpansionIssueKind.Ambiguous))
            return WmlResolvedValueState.Ambiguous;
        if (result.Issues.Any(x => x.Kind == WmlMacroExpansionIssueKind.Unsupported))
            return WmlResolvedValueState.Unsupported;
        if (result.Issues.Any(x => x.Kind == WmlMacroExpansionIssueKind.Unresolved))
            return WmlResolvedValueState.Unresolved;
        return result.MacroUses.Count == 0
            ? WmlResolvedValueState.Literal
            : WmlResolvedValueState.Resolved;
    }

    private sealed record ValueExpansion(string Value, bool ExceededLimit);

    private sealed class ResolvedValueExpansionState(
        IReadOnlyDictionary<string, IReadOnlyList<WmlMacroExpansionDefinition>> localDefinitions,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver,
        WmlResolvedValueResult result,
        WmlResolvedValueOptions options,
        long requestOrdinal)
    {
        public WmlResolvedValueResult Result { get; } = result;
        public WmlResolvedValueOptions Options { get; } = options;
        public long RequestOrdinal { get; } = requestOrdinal;
        public int NextExpansionOrdinal { get; set; } = 1;
        public bool SawMalformedInvocation { get; set; }

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
