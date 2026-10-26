using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>One exact reached scenario owner in a selected entry/difficulty evaluation.</summary>
public sealed record WccCampaignScenarioOccurrence(WmlEvaluationContext Context, WmlStaticScalarOwner Owner,
    WmlStaticEvaluationResult Evaluation);

public sealed class WccCampaignScenarioMembership
{
    internal WccCampaignScenarioMembership(string identity, string? id, string name, WmlStaticSource? source,
        WmlBlockSpan? span, IEnumerable<WccCampaignScenarioOccurrence> owners)
    { Identity = identity; ScenarioId = id; Name = name; OwnerSource = source; AuthoredScenarioSpan = span; Owners = Array.AsReadOnly(owners.ToArray()); }
    public string Identity { get; }
    public string? ScenarioId { get; }
    public string Name { get; }
    public WmlStaticSource? OwnerSource { get; }
    public WmlBlockSpan? AuthoredScenarioSpan { get; }
    /// <summary>
    /// Physical source block matched by its exact literal opening, for display only.
    /// An amended owner may have this span without a writable authored boundary.
    /// </summary>
    public WmlBlockSpan? DisplayScenarioSpan { get; internal init; }
    public IReadOnlyList<WccCampaignScenarioOccurrence> Owners { get; }
    public bool AuthorizesPersistence => false;
}

public sealed class WccCampaignScenarioContext
{
    internal WccCampaignScenarioContext(WmlEvaluationContext context, WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection)
    {
        Context = context; Evaluation = evaluation; Projection = projection;
        Diagnostics = Array.AsReadOnly(evaluation.Diagnostics.Select(item => item.Message)
            .Concat(projection.Diagnostics.Select(item => item.Message)).Distinct(StringComparer.Ordinal).ToArray());
    }
    public WmlEvaluationContext Context { get; }
    public WmlStaticEvaluationResult Evaluation { get; }
    public WmlStaticScalarProjection Projection { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool IsComplete => Evaluation.IsComplete && !Evaluation.Diagnostics.Any(diagnostic => diagnostic.IsBlocking)
        && Projection.Diagnostics.Count == 0;
}

/// <summary>Many-to-many reached membership, never guessed from directories or scenario IDs.</summary>
public sealed class WccCampaignScenarioCatalogProjection
{
    internal WccCampaignScenarioCatalogProjection(WccCampaignEntry entry, IEnumerable<WccCampaignScenarioContext> contexts,
        IEnumerable<WccCampaignScenarioMembership> scenarios, IEnumerable<string> diagnostics)
    {
        Entry = entry; Contexts = Array.AsReadOnly(contexts.ToArray()); Scenarios = Array.AsReadOnly(scenarios.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }
    public WccCampaignEntry Entry { get; }
    public IReadOnlyList<WccCampaignScenarioContext> Contexts { get; }
    public IReadOnlyList<WccCampaignScenarioMembership> Scenarios { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    // Warnings remain visible in Diagnostics without revoking otherwise complete activation evidence.
    internal bool HasCompleteMembershipInventory { get; init; } = true;
    public bool IsComplete => Entry.IsContextComplete && Contexts.Count == Entry.Plan?.Contexts.Count
        && Contexts.All(context => context.IsComplete) && HasCompleteMembershipInventory;
    /// <summary>Only an output-buffer limit is a candidate for the separate bounded inspection path.</summary>
    public bool HasOutputLimitFailure => Contexts.Any(context => context.Evaluation.OutputLimitExceeded);
    public bool AuthorizesPersistence => false;
}

public static partial class WccCampaignScenarioCatalog
{
    private const int MaximumScenarios = 4096;

    /// <summary>Evaluates one declared gameplay context through the same pipeline
    /// used by scenario membership. This is read-only evidence, including for an
    /// empty chapter which does not yet contain a scenario.</summary>
    public static WccCampaignScenarioContext ProjectContext(WccStaticSourceCaptureResult capture, WccCampaignEntry entry,
        WmlEvaluationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(entry.SourceCapture, capture) || !entry.IsContextComplete || entry.Plan is not { } plan ||
            capture.EntrySource is not { } root || root.Path != plan.CampaignMainSourcePath ||
            root.ContentHash != plan.CampaignMainTextHash || !capture.IsCaptureComplete ||
            plan.Contexts.Count(item => item.SemanticKey == context.SemanticKey) != 1)
            throw new InvalidOperationException("Choose one declared gameplay context from this exact captured campaign entry.");
        if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            throw new InvalidOperationException("The exact pinned Core version has no supported singleplayer environment.");
        var selected = plan.Contexts.Single(item => item.SemanticKey == context.SemanticKey);
        var result = EvaluateContext(capture, cancellationToken, plan, selected, root, environment);
        return new(selected, result.Evaluation, result.Projection);
    }
    public static WccCampaignScenarioCatalogProjection Project(WccStaticSourceCaptureResult capture, WccCampaignEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(entry);
        var contexts = new List<WccCampaignScenarioContext>(); var diagnostics = new List<string>();
        var rows = new Dictionary<string, (WmlStaticScalarOwner Owner, WmlStaticEvaluationResult Evaluation,
            List<WccCampaignScenarioOccurrence> Occurrences)>(StringComparer.Ordinal);
        if (!ReferenceEquals(entry.SourceCapture, capture) || !entry.IsContextComplete || entry.Plan is not { } plan || capture.EntrySource is not { } root
            || root.Path != plan.CampaignMainSourcePath || root.ContentHash != plan.CampaignMainTextHash
            || capture.Diagnostics.Count != 0)
            return new(entry, contexts, [], ["The entry and captured package do not share one complete current context plan."]);
        if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out _))
            return new(entry, contexts, [], ["The exact pinned Core version has no supported singleplayer environment."]);
        var completeInventory = true;
        var evaluations = new Dictionary<string, (WmlStaticEvaluationResult Evaluation, WmlStaticScalarProjection Projection)>(StringComparer.Ordinal);
        foreach (var context in plan.Contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!evaluations.TryGetValue(context.CacheKey, out var cached))
            {
                var projected = ProjectContext(capture, entry, context, cancellationToken);
                cached = (projected.Evaluation, projected.Projection);
                evaluations.Add(context.CacheKey, cached);
            }
            var result = new WccCampaignScenarioContext(context, cached.Evaluation, cached.Projection); contexts.Add(result);
            diagnostics.AddRange(result.Diagnostics.Select(problem => context.DisplayName + ": " + problem));
            if (!result.IsComplete && result.Diagnostics.Count == 0)
                diagnostics.Add(context.DisplayName + ": Scenario activation is incomplete.");
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var owner in cached.Projection.Owners.Where(owner => owner.TagName == "scenario" && owner.Parent is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var physical = WccCampaignCapturedOwner.Key(owner);
                var ordinal = occurrences.GetValueOrDefault(physical); occurrences[physical] = ordinal + 1;
                var key = physical + "\u001f" + ordinal;
                if (!rows.TryGetValue(key, out var row))
                {
                    if (rows.Count == MaximumScenarios)
                    { completeInventory = false; diagnostics.Add("The reached scenario inventory exceeds the bounded catalog limit."); break; }
                    row = (owner, cached.Evaluation, []); rows.Add(key, row);
                }
                row.Occurrences.Add(new(context, owner, cached.Evaluation));
            }
        }
        return new(entry, contexts, rows.Select(pair =>
        {
            var owner = pair.Value.Owner; var id = WccCampaignEntryCatalog.Read(owner, "id");
            return new WccCampaignScenarioMembership(WccContentHash.ComputeText(entry.Identity + "\u001f" + pair.Key), id,
                WccCampaignEntryCatalog.Read(owner, "name") ?? id ?? "Unresolved scenario", owner.OpeningOrigin?.Source,
                WccCampaignCapturedOwner.FindAuthoredSpan(owner, pair.Value.Evaluation), pair.Value.Occurrences)
            { DisplayScenarioSpan = WccCampaignCapturedOwner.FindDisplaySpan(owner, pair.Value.Evaluation) };
        }), diagnostics) { HasCompleteMembershipInventory = completeInventory };
    }

    private static (WmlStaticEvaluationResult Evaluation, WmlStaticScalarProjection Projection) EvaluateContext(
        WccStaticSourceCaptureResult capture, CancellationToken cancellationToken, WmlCampaignEvaluationPlan plan,
        WmlEvaluationContext context, WmlStaticSource root, WmlStaticSingleplayerEnvironment? environment)
    {
        (WmlStaticEvaluationResult Evaluation, WmlStaticScalarProjection Projection) cached;
        var undefined = plan.Difficulties.Select(item => item.Define).Concat(["EDITOR", "MULTIPLAYER"])
            .Where(symbol => !context.DefinedSymbols.Contains(symbol)).ToArray();
        var evaluation = new WmlStaticEvaluator().Evaluate(new(root, context,
            new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).ToArray(), undefined,
            true, capture.IncludeResolver, WmlStaticEvaluationLimits.Campaign), cancellationToken);
        cached = (evaluation, WmlStaticScalarProjection.Create(evaluation, cancellationToken));
        return cached;
    }
}
