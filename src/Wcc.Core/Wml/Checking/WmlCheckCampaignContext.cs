using Wcc.Core.Library;

namespace Wcc.Core.Wml.Checking;

/// <summary>An explicitly selected entry and one immutable, ordered evaluation environment.</summary>
public sealed record WmlCheckCampaignEvaluation(string Label, WmlEvaluationContext Context,
    WmlStaticEvaluationResult Evaluation)
{
    /// <summary>
    /// The host explicitly captured the engine's Core units include in this gameplay
    /// environment. Lexical support catalogs and menu evaluations cannot supply this evidence.
    /// This does not establish game validity or authorize writes.
    /// </summary>
    public bool IncludesEngineCoreUnitData { get; init; }
}

/// <summary>
/// Captured campaign preprocessing evidence. The checker neither discovers files nor
/// evaluates this context, and complete preprocessing does not establish game validity.
/// </summary>
public sealed class WmlCheckCampaignContext
{
    public WmlCheckCampaignContext(string entryPath, IEnumerable<WmlCheckSource> sources,
        IEnumerable<WmlCheckCampaignEvaluation> evaluations, IEnumerable<string>? problems = null,
        WccStaticSourceContext? sourceContext = null, IWmlStaticResourceResolver? resourceResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(evaluations);
        EntryPath = entryPath;
        SourceContext = sourceContext ?? WccStaticSourceContext.Empty;
        ResourceResolver = resourceResolver;
        var captured = sources.ToArray();
        foreach (var source in captured)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(source.Path);
            ArgumentNullException.ThrowIfNull(source.Text);
        }
        Sources = Array.AsReadOnly(captured.Distinct().ToArray());
        var observed = evaluations.ToArray();
        foreach (var evaluation in observed)
        {
            ArgumentNullException.ThrowIfNull(evaluation);
            ArgumentNullException.ThrowIfNull(evaluation.Label);
            ArgumentNullException.ThrowIfNull(evaluation.Context);
            ArgumentNullException.ThrowIfNull(evaluation.Evaluation);
        }
        // Context symbols and evaluation results already freeze their nested collections.
        Evaluations = Array.AsReadOnly(observed);
        Problems = Array.AsReadOnly((problems ?? []).Select(problem =>
            problem ?? throw new ArgumentException("A context problem cannot be null.", nameof(problems))).ToArray());
    }

    public string EntryPath { get; }
    public WccStaticSourceContext SourceContext { get; }
    /// <summary>Read-only resource inventory from this exact source capture; no live IO.</summary>
    public IWmlStaticResourceResolver? ResourceResolver { get; }
    public IReadOnlyList<WmlCheckSource> Sources { get; }
    public IReadOnlyList<WmlCheckCampaignEvaluation> Evaluations { get; }
    public IReadOnlyList<string> Problems { get; }
    public string Summary => $"Campaign context: {EntryPath}; {Sources.Count} captured source(s), " +
        $"{Evaluations.Count} ordered evaluation(s), {Evaluations.Count(item => item.Evaluation.IsComplete)} complete.";
    public string Coverage => $"Campaign preprocessing: {Evaluations.Count} context(s), {Evaluations.Count(item => item.Evaluation.IsComplete)} complete. " +
        "Captured entry environments only; runtime Lua, game semantics and edit safety remain unvalidated.";
    public string Details => Summary + (Evaluations.Count == 0 ? " No evaluation environments were captured." :
        "\n" + string.Join("\n", Evaluations.Select(item =>
            $"{item.Label}: {(item.Evaluation.IsComplete ? "complete" : "partial")}; " +
            $"{item.Evaluation.Sources.Count} visited source(s), {item.Evaluation.Invocations.Count} invocation observation(s)."))) +
        (SourceContext.AddonsNamespacePath is null ? "" : "\nAdd-on namespace: " + SourceContext.AddonsNamespacePath) +
        (SourceContext.DependencyRoots.Count == 0 ? "" : "\nExplicit dependency roots:\n" +
            string.Join("\n", SourceContext.DependencyRoots.Select(root => root.AddonId + ": " + root.RootPath))) +
        (Problems.Count == 0 ? "" : "\n" + string.Join("\n", Problems)) +
        "\nOnly captured entry environments were evaluated. Runtime Lua, game semantics and source-edit safety remain unvalidated.";
}
