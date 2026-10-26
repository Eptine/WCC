using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Wcc.Core.Wml;

public enum WmlEvaluationAuthority
{
    /// <summary>
    /// WCC expanded only definitions it can reverse-map safely. Preprocessor
    /// branches remain present and are not treated as one authoritative state.
    /// </summary>
    ConservativeStaticPreview,

    /// <summary>Projection produced by Wesnoth's own preprocessor.</summary>
    WesnothPreprocessor
}

public sealed class WmlEffectiveDocumentProjection
{
    public required string SourceText { get; init; }
    public required WmlEvaluationContext Context { get; init; }
    public required WmlEvaluationAuthority Authority { get; init; }
    public required WmlMacroExpansionResult MacroExpansion { get; init; }
    public required WmlProjectionTree ProjectionTree { get; init; }
    public WmlMacroSourceGraph? MacroSourceGraph { get; init; }
    public bool HasUnevaluatedPreprocessorBranches { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public bool IsAuthoritativeForContext =>
        Authority == WmlEvaluationAuthority.WesnothPreprocessor &&
        !HasUnevaluatedPreprocessorBranches;
}

public interface IWmlEffectiveDocumentProjector
{
    WmlEffectiveDocumentProjection Project(
        string sourceText,
        WmlEvaluationContext? context = null,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlMacroExpansionOptions? expansionOptions = null,
        bool includeMacroSourceGraph = false);

    /// <summary>
    /// Builds an authoritative read-only projection from normalized WML that
    /// was produced successfully by Wesnoth's own preprocessor.
    /// </summary>
    WmlEffectiveDocumentProjection ProjectPreprocessed(
        string preprocessedText,
        WmlEvaluationContext? context = null,
        IEnumerable<string>? diagnostics = null);

    void ClearCache();
}

/// <summary>
/// One reusable projection pipeline for every editor domain. Expansion, tree
/// parsing, optional source-graph construction, context identity, and caching
/// live here so scenario/event/unit consumers do not grow parallel resolvers.
/// </summary>
public sealed class WmlEffectiveDocumentProjector(
    IWmlMacroExpansionService? macroExpansionService = null,
    WmlMacroSourceGraphBuilder? macroSourceGraphBuilder = null,
    int cacheCapacity = 8) : IWmlEffectiveDocumentProjector
{
    private readonly IWmlMacroExpansionService _macroExpansionService =
        macroExpansionService is WmlStandaloneMacroPreviewExpansionService
            ? macroExpansionService
            : new WmlStandaloneMacroPreviewExpansionService(macroExpansionService);
    private readonly WmlMacroSourceGraphBuilder _macroSourceGraphBuilder =
        macroSourceGraphBuilder ?? new WmlMacroSourceGraphBuilder();
    private readonly object _cacheLock = new();
    private readonly Dictionary<ProjectionCacheKey, WmlEffectiveDocumentProjection> _cache = [];
    private readonly Queue<ProjectionCacheKey> _cacheOrder = [];
    private readonly int _cacheCapacity = cacheCapacity > 0
        ? cacheCapacity
        : throw new ArgumentOutOfRangeException(nameof(cacheCapacity));

    public WmlEffectiveDocumentProjection Project(
        string sourceText,
        WmlEvaluationContext? context = null,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null,
        WmlMacroExpansionOptions? expansionOptions = null,
        bool includeMacroSourceGraph = false)
    {
        if (sourceText is null) throw new ArgumentNullException(nameof(sourceText));

        context ??= new WmlEvaluationContext();
        expansionOptions ??= new WmlMacroExpansionOptions();
        var definitions = localDefinitions?.ToArray() ?? [];
        var key = new ProjectionCacheKey(
            Hash(sourceText),
            HashDefinitions(definitions),
            context.CacheKey,
            expansionOptions.MaxExpansionDepth,
            expansionOptions.MaxExpandedLines,
            includeMacroSourceGraph,
            fallbackResolver is null ? 0 : RuntimeHelpers.GetHashCode(fallbackResolver));
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var expansion = _macroExpansionService.Expand(
            sourceText,
            definitions,
            fallbackResolver,
            expansionOptions);
        var tree = WmlProjectionTreeParser.Parse(expansion.ExpandedText);
        var hasConditionalBranches = ContainsConditionalPreprocessor(sourceText);
        var diagnostics = expansion.Issues
            .Select(x => x.Message)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hasConditionalBranches)
        {
            diagnostics.Add(
                $"Preprocessor branches remain unevaluated in the static '{context.DisplayName}' preview; source is preserved until an authoritative evaluator is used.");
        }

        var projection = new WmlEffectiveDocumentProjection
        {
            SourceText = sourceText,
            Context = context,
            Authority = WmlEvaluationAuthority.ConservativeStaticPreview,
            MacroExpansion = expansion,
            ProjectionTree = tree,
            MacroSourceGraph = includeMacroSourceGraph
                ? _macroSourceGraphBuilder.Build(
                    sourceText,
                    localDefinitions: definitions,
                    fallbackResolver: fallbackResolver)
                : null,
            HasUnevaluatedPreprocessorBranches = hasConditionalBranches,
            Diagnostics = diagnostics
        };

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var raced))
                return raced;
            _cache.Add(key, projection);
            _cacheOrder.Enqueue(key);
            while (_cacheOrder.Count > _cacheCapacity)
            {
                var expired = _cacheOrder.Dequeue();
                _cache.Remove(expired);
            }
        }
        return projection;
    }

    /// <summary>
    /// Projects successful Wesnoth preprocessor output without passing it
    /// through WCC's conservative macro expander again. The caller owns the
    /// guarantee that <paramref name="preprocessedText"/> came from a
    /// successful preprocessing run for <paramref name="context"/>.
    /// </summary>
    public WmlEffectiveDocumentProjection ProjectPreprocessed(
        string preprocessedText,
        WmlEvaluationContext? context = null,
        IEnumerable<string>? diagnostics = null)
    {
        if (preprocessedText is null) throw new ArgumentNullException(nameof(preprocessedText));

        context ??= new WmlEvaluationContext();
        var expansion = new WmlMacroExpansionResult { ExpandedText = preprocessedText };
        return new WmlEffectiveDocumentProjection
        {
            SourceText = preprocessedText,
            Context = context,
            Authority = WmlEvaluationAuthority.WesnothPreprocessor,
            MacroExpansion = expansion,
            ProjectionTree = WmlProjectionTreeParser.Parse(preprocessedText),
            MacroSourceGraph = null,
            HasUnevaluatedPreprocessorBranches = false,
            Diagnostics = (diagnostics ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }
    }

    private static bool ContainsConditionalPreprocessor(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            if (WmlPreprocessorScanner.GetDirectiveKind(line.Trim()) is
                WmlPreprocessorDirectiveKind.IfStart or
                WmlPreprocessorDirectiveKind.Else or
                WmlPreprocessorDirectiveKind.EndIf)
            {
                return true;
            }
        }
        return false;
    }

    private static string HashDefinitions(IReadOnlyList<WmlMacroExpansionDefinition> definitions)
    {
        if (definitions.Count == 0)
            return string.Empty;
        var serialized = string.Join(
            "\u001e",
            definitions.Select(x => string.Join(
                "\u001f",
                x.Name,
                x.SourcePath ?? string.Empty,
                x.PackageRevisionId ?? string.Empty,
                x.SourceStartLine?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                x.RawText)));
        return Hash(serialized);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record ProjectionCacheKey(
        string SourceHash,
        string DefinitionHash,
        string ContextKey,
        int MaxExpansionDepth,
        int MaxExpandedLines,
        bool IncludesMacroSourceGraph,
        int ResolverIdentity);
}
