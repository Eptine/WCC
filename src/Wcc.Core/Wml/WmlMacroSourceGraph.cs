using System.Security.Cryptography;
using System.Text;

namespace Wcc.Core.Wml;

public enum WmlMacroSourceNodeKind
{
    RootSource,
    MacroDefinition
}

public enum WmlMacroConnectionResolution
{
    Resolved,
    Unresolved,
    Ambiguous,
    ExternalDirectiveOrInclude,
    ParameterSubstitution,
    Cycle,
    DefinitionLimit,
    InvocationLimit
}

public sealed class WmlMacroSourceNode
{
    public required string Id { get; init; }
    public required WmlMacroSourceNodeKind Kind { get; init; }
    public string? MacroName { get; init; }
    public required string RawText { get; init; }
    public string? SourcePath { get; init; }
    public string? PackageName { get; init; }
    public string? PackageRevisionId { get; init; }
    public int? SourceStartLine { get; init; }
    public IReadOnlyList<string> Parameters { get; init; } = [];

    public string SourceLabel
    {
        get
        {
            var package = string.IsNullOrWhiteSpace(PackageName) ? null : PackageName;
            var file = string.IsNullOrWhiteSpace(SourcePath) ? null : Path.GetFileName(SourcePath);
            var label = (package, file) switch
            {
                ({ } packageName, { } fileName) => $"{packageName} · {fileName}",
                ({ } packageName, null) => packageName,
                (null, { } fileName) => fileName,
                _ => Kind == WmlMacroSourceNodeKind.RootSource ? "current source" : "unknown source"
            };
            return SourceStartLine is { } line ? $"{label}:{line + 1}" : label;
        }
    }
}

public sealed class WmlMacroSourceEdge
{
    public required string SourceNodeId { get; init; }
    public required WmlMacroInvocationSyntax Invocation { get; init; }
    public required WmlMacroConnectionResolution Resolution { get; init; }
    public IReadOnlyList<string> TargetNodeIds { get; init; } = [];
    public int SourceLine { get; init; }
    public string? Diagnostic { get; init; }
}

public sealed class WmlMacroSourceGraph
{
    public required string RootNodeId { get; init; }
    public List<WmlMacroSourceNode> Nodes { get; } = [];
    public List<WmlMacroSourceEdge> Edges { get; } = [];
    public List<string> Diagnostics { get; } = [];

    public WmlMacroSourceNode Root => Nodes.Single(x => x.Id == RootNodeId);
    public IEnumerable<WmlMacroSourceNode> Definitions =>
        Nodes.Where(x => x.Kind == WmlMacroSourceNodeKind.MacroDefinition);
}

public sealed record WmlMacroSourceGraphOptions(
    int MaxDefinitionDepth = 32,
    int MaxDefinitions = 512,
    int MaxInvocations = 8192);

/// <summary>
/// Builds a static, lossless relationship graph over the same definitions used
/// by <see cref="WmlMacroExpansionService"/>. It resolves ownership and source
/// spans but never selects preprocessor branches and never rewrites source.
/// </summary>
public sealed class WmlMacroSourceGraphBuilder
{
    public WmlMacroSourceGraph Build(
        string sourceText,
        string? sourcePath = null,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlMacroSourceGraphOptions? options = null)
    {
        if (sourceText is null) throw new ArgumentNullException(nameof(sourceText));
        options ??= new WmlMacroSourceGraphOptions();
        if (options.MaxDefinitionDepth <= 0 || options.MaxDefinitions <= 0 || options.MaxInvocations <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Graph limits must be positive.");

        var scannedLocalDefinitions = WmlMacroDefinitionScanner.Scan(sourceText)
            .Select(x => new WmlMacroExpansionDefinition(
                x.Name,
                x.RawText,
                sourcePath,
                "Current source",
                SourceStartLine: x.StartLineIndex));
        var localByName = scannedLocalDefinitions
            .Concat(localDefinitions ?? [])
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<WmlMacroExpansionDefinition>)x
                    .DistinctBy(BuildDefinitionKey, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var root = new WmlMacroSourceNode
        {
            Id = "source-root",
            Kind = WmlMacroSourceNodeKind.RootSource,
            RawText = sourceText,
            SourcePath = sourcePath,
            SourceStartLine = 0
        };
        var graph = new WmlMacroSourceGraph { RootNodeId = root.Id };
        graph.Nodes.Add(root);

        var state = new BuildState(graph, localByName, fallbackResolver, options);
        VisitSource(
            root,
            includeInvocationsInsideDefinitions: false,
            depth: 0,
            activeDefinitionKeys: new HashSet<string>(StringComparer.Ordinal),
            state);
        return graph;
    }

    private static void VisitSource(
        WmlMacroSourceNode source,
        bool includeInvocationsInsideDefinitions,
        int depth,
        HashSet<string> activeDefinitionKeys,
        BuildState state)
    {
        var invocations = WmlMacroInvocationSyntaxScanner.Scan(source.RawText)
            .Where(x => includeInvocationsInsideDefinitions || !x.IsInsideMacroDefinition)
            .ToArray();
        foreach (var invocation in invocations)
        {
            if (state.Graph.Edges.Count >= state.Options.MaxInvocations)
            {
                state.AddDiagnosticOnce(
                    $"Macro graph stopped after {state.Options.MaxInvocations} invocation(s). Increase the graph limit to inspect more source relationships.");
                state.Graph.Edges.Add(new WmlMacroSourceEdge
                {
                    SourceNodeId = source.Id,
                    Invocation = invocation,
                    Resolution = WmlMacroConnectionResolution.InvocationLimit,
                    SourceLine = ToAbsoluteLine(source, invocation)
                });
                return;
            }

            if (state.IsParameterSubstitution(source.Id, invocation.Name))
            {
                state.Graph.Edges.Add(new WmlMacroSourceEdge
                {
                    SourceNodeId = source.Id,
                    Invocation = invocation,
                    Resolution = WmlMacroConnectionResolution.ParameterSubstitution,
                    SourceLine = ToAbsoluteLine(source, invocation),
                    Diagnostic = $"{{{invocation.Name}}} is supplied by this macro invocation; it is not a separately resolved definition."
                });
                continue;
            }

            IReadOnlyList<WmlMacroExpansionDefinition> candidates;
            try
            {
                candidates = state.FindDefinitions(invocation.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                candidates = [];
                state.AddDiagnosticOnce($"Could not load a definition for {{{invocation.Name}}}: {ex.Message}");
            }

            if (candidates.Count == 0)
            {
                var external = LooksLikeExternalDirectiveOrInclude(invocation.Name);
                state.Graph.Edges.Add(new WmlMacroSourceEdge
                {
                    SourceNodeId = source.Id,
                    Invocation = invocation,
                    Resolution = external
                        ? WmlMacroConnectionResolution.ExternalDirectiveOrInclude
                        : WmlMacroConnectionResolution.Unresolved,
                    SourceLine = ToAbsoluteLine(source, invocation),
                    Diagnostic = external
                        ? "This brace expression is an include or preprocessor directive, not a resolved macro definition."
                        : $"No visible definition was found for {{{invocation.Name}}}."
                });
                continue;
            }

            if (candidates.Count > 1)
            {
                var targetIds = candidates
                    .Select(candidate => state.GetOrAddDefinitionNode(candidate)?.Id)
                    .Where(x => x is not null)
                    .Cast<string>()
                    .ToArray();
                state.Graph.Edges.Add(new WmlMacroSourceEdge
                {
                    SourceNodeId = source.Id,
                    Invocation = invocation,
                    Resolution = WmlMacroConnectionResolution.Ambiguous,
                    TargetNodeIds = targetIds,
                    SourceLine = ToAbsoluteLine(source, invocation),
                    Diagnostic = $"{candidates.Count} visible definitions match; WCC will not choose one silently."
                });
                continue;
            }

            var definition = candidates[0];
            var definitionKey = BuildDefinitionKey(definition);
            var target = state.GetOrAddDefinitionNode(definition);
            if (target is null)
            {
                state.Graph.Edges.Add(new WmlMacroSourceEdge
                {
                    SourceNodeId = source.Id,
                    Invocation = invocation,
                    Resolution = WmlMacroConnectionResolution.DefinitionLimit,
                    SourceLine = ToAbsoluteLine(source, invocation),
                    Diagnostic = $"The graph's {state.Options.MaxDefinitions}-definition limit was reached."
                });
                continue;
            }

            var createsCycle = activeDefinitionKeys.Contains(definitionKey);
            state.Graph.Edges.Add(new WmlMacroSourceEdge
            {
                SourceNodeId = source.Id,
                Invocation = invocation,
                Resolution = createsCycle
                    ? WmlMacroConnectionResolution.Cycle
                    : WmlMacroConnectionResolution.Resolved,
                TargetNodeIds = [target.Id],
                SourceLine = ToAbsoluteLine(source, invocation),
                Diagnostic = createsCycle ? "This invocation closes a macro-definition cycle." : null
            });

            if (createsCycle || state.VisitedDefinitionKeys.Contains(definitionKey))
                continue;
            if (depth >= state.Options.MaxDefinitionDepth)
            {
                state.AddDiagnosticOnce(
                    $"Macro graph stopped following definitions at depth {state.Options.MaxDefinitionDepth}.");
                continue;
            }

            state.VisitedDefinitionKeys.Add(definitionKey);
            var nextActive = new HashSet<string>(activeDefinitionKeys, StringComparer.Ordinal)
            {
                definitionKey
            };
            VisitSource(
                target,
                includeInvocationsInsideDefinitions: true,
                depth + 1,
                nextActive,
                state);
        }
    }

    private static int ToAbsoluteLine(
        WmlMacroSourceNode source,
        WmlMacroInvocationSyntax invocation)
        => (source.SourceStartLine ?? 0) + invocation.Span.StartLine;

    private static bool LooksLikeExternalDirectiveOrInclude(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;
        if (name[0] == '~' || name.Contains('/') || name.Contains('\\'))
            return true;
        return name.Contains('(') || name.Contains(')');
    }

    private static string BuildDefinitionKey(WmlMacroExpansionDefinition definition)
        => string.Join(
            "\u001f",
            definition.Name.ToUpperInvariant(),
            definition.SourcePath ?? string.Empty,
            definition.PackageRevisionId ?? string.Empty,
            definition.SourceStartLine?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition.RawText))));

    private sealed class BuildState(
        WmlMacroSourceGraph graph,
        IReadOnlyDictionary<string, IReadOnlyList<WmlMacroExpansionDefinition>> localByName,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver,
        WmlMacroSourceGraphOptions options)
    {
        private readonly Dictionary<string, WmlMacroSourceNode> _nodesByDefinitionKey =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _diagnostics = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlySet<string>> _parametersByNodeId =
            new(StringComparer.Ordinal);
        private int _nextNodeOrdinal = 1;

        public WmlMacroSourceGraph Graph { get; } = graph;
        public WmlMacroSourceGraphOptions Options { get; } = options;
        public HashSet<string> VisitedDefinitionKeys { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<WmlMacroExpansionDefinition> FindDefinitions(string name)
            => localByName.TryGetValue(name, out var local)
                ? local
                : fallbackResolver?.FindDefinitions(name) ?? [];

        public WmlMacroSourceNode? GetOrAddDefinitionNode(WmlMacroExpansionDefinition definition)
        {
            var key = BuildDefinitionKey(definition);
            if (_nodesByDefinitionKey.TryGetValue(key, out var existing))
                return existing;
            if (_nodesByDefinitionKey.Count >= Options.MaxDefinitions)
            {
                AddDiagnosticOnce(
                    $"Macro graph stopped after {Options.MaxDefinitions} definition(s). Increase the graph limit to inspect more dependencies.");
                return null;
            }

            IReadOnlyList<string> parameters = [];
            if (WmlMacroDefinitionTemplate.TryParse(definition, out var template, out _))
            {
                parameters = template!.Parameters
                    .Concat(template.OptionalParameters.Select(x => x.Name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
            var created = new WmlMacroSourceNode
            {
                Id = $"macro-definition-{_nextNodeOrdinal++}",
                Kind = WmlMacroSourceNodeKind.MacroDefinition,
                MacroName = definition.Name,
                RawText = definition.RawText,
                SourcePath = definition.SourcePath,
                PackageName = definition.PackageName,
                PackageRevisionId = definition.PackageRevisionId,
                SourceStartLine = definition.SourceStartLine,
                Parameters = parameters
            };
            _nodesByDefinitionKey.Add(key, created);
            _parametersByNodeId[created.Id] = parameters.ToHashSet(StringComparer.Ordinal);
            Graph.Nodes.Add(created);
            return created;
        }

        public bool IsParameterSubstitution(string nodeId, string name)
            => _parametersByNodeId.TryGetValue(nodeId, out var parameters) &&
               parameters.Contains(name);

        public void AddDiagnosticOnce(string diagnostic)
        {
            if (_diagnostics.Add(diagnostic))
                Graph.Diagnostics.Add(diagnostic);
        }
    }
}
