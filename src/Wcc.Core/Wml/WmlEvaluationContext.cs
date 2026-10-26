using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Wcc.Core.Library;

namespace Wcc.Core.Wml;

/// <summary>
/// The compile-time symbols under which an effective WML projection is read.
/// Difficulty is kept explicit for the editor, but all symbols are exposed in
/// one canonical set so the same context can later be passed to Wesnoth's
/// preprocessor or a conservative in-process evaluator.
/// </summary>
public sealed class WmlEvaluationContext
{
    public WmlEvaluationContext(
        string? campaignDefine = null,
        string? difficultyDefine = null,
        IEnumerable<string>? additionalDefines = null,
        string? targetCompatibility = null)
    {
        CampaignDefine = Normalize(campaignDefine);
        DifficultyDefine = Normalize(difficultyDefine);
        TargetCompatibility = Normalize(targetCompatibility);

        var symbols = (additionalDefines ?? [])
            .Select(Normalize)
            .Where(x => x is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (CampaignDefine is not null)
            symbols.Add(CampaignDefine);
        if (DifficultyDefine is not null)
            symbols.Add(DifficultyDefine);
        DefinedSymbols = symbols.ToFrozenSet(StringComparer.Ordinal);

        CacheKey = string.Join(
            "\u001f",
            new[] { TargetCompatibility ?? string.Empty }
                .Concat(DefinedSymbols.OrderBy(x => x, StringComparer.Ordinal)));
        SemanticKey = string.Join(
            "\u001f",
            new[]
                {
                    TargetCompatibility ?? string.Empty,
                    CampaignDefine ?? string.Empty,
                    DifficultyDefine ?? string.Empty
                }
                .Concat(DefinedSymbols.OrderBy(x => x, StringComparer.Ordinal)));
    }

    public string? CampaignDefine { get; }
    public string? DifficultyDefine { get; }
    public string? TargetCompatibility { get; }
    public FrozenSet<string> DefinedSymbols { get; }

    /// <summary>A stable, order-independent key for projection caches.</summary>
    public string CacheKey { get; }

    /// <summary>
    /// A stable routing identity that also retains the explicit campaign and
    /// difficulty roles. Two contexts can feed identical symbol sets to the
    /// preprocessor while still selecting different editor helper slots.
    /// </summary>
    public string SemanticKey { get; }

    public string DisplayName => DifficultyDefine ?? "Campaign default context";

    public bool IsDefined(string symbol)
        => !string.IsNullOrWhiteSpace(symbol) && DefinedSymbols.Contains(symbol.Trim());

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record WmlDifficultyVariant(
    string Define,
    string? ImageExpression,
    string? LabelWml,
    string? DescriptionWml,
    bool IsDefault,
    WmlTextSpan? SourceSpan,
    string? RawDeclaration);

public sealed class WmlCampaignEvaluationPlan
{
    private readonly ReadOnlyCollection<WmlDifficultyVariant> _difficulties;
    private readonly ReadOnlyCollection<WmlEvaluationContext> _contexts;
    private readonly ReadOnlyCollection<string> _diagnostics;

    internal WmlCampaignEvaluationPlan(
        string? campaignId,
        string? campaignDefine,
        IEnumerable<WmlDifficultyVariant> difficulties,
        IEnumerable<WmlEvaluationContext> contexts,
        IEnumerable<string> diagnostics,
        string campaignMainSourcePath,
        string campaignMainText,
        bool isContextDiscoveryComplete = false)
    {
        if (string.IsNullOrWhiteSpace(campaignMainSourcePath))
            throw new ArgumentException("A campaign-main source path is required.", nameof(campaignMainSourcePath));
        ArgumentNullException.ThrowIfNull(campaignMainText);

        CampaignId = campaignId;
        CampaignDefine = campaignDefine;
        CampaignMainSourcePath = Path.GetFullPath(campaignMainSourcePath);
        CampaignMainTextHash = WccContentHash.ComputeText(campaignMainText);
        IsContextDiscoveryComplete = isContextDiscoveryComplete;
        _difficulties = Array.AsReadOnly(
            (difficulties ?? throw new ArgumentNullException(nameof(difficulties))).ToArray());
        _contexts = Array.AsReadOnly(
            (contexts ?? throw new ArgumentNullException(nameof(contexts))).ToArray());
        _diagnostics = Array.AsReadOnly(
            (diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))).ToArray());
    }

    public string? CampaignId { get; }
    public string? CampaignDefine { get; }
    public string CampaignMainSourcePath { get; }
    public string CampaignMainTextHash { get; }
    /// <summary>Exact literal source anchor from discovery; normalized or generated previews have none.</summary>
    public WmlBlockSpan? AuthoredCampaignSpan { get; internal init; }
    /// <summary>
    /// True only when discovery understood every possible contribution to the
    /// campaign context metadata in its supported source scope. An incomplete
    /// plan may populate preview choices, but cannot define an exhaustive edit
    /// validation scope. This does not itself authorize any source edit.
    /// </summary>
    public bool IsContextDiscoveryComplete { get; }
    public IReadOnlyList<WmlDifficultyVariant> Difficulties => _difficulties;
    public IReadOnlyList<WmlEvaluationContext> Contexts => _contexts;
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public WmlDifficultyVariant? DefaultDifficulty =>
        Difficulties.FirstOrDefault(x => x.IsDefault);
}
