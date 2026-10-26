using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>A detached context result for navigation, without expanded output or owner graphs.</summary>
public sealed class WccCampaignScenarioNavigationContext
{
    internal WccCampaignScenarioNavigationContext(WmlEvaluationContext context, bool isComplete,
        IEnumerable<string> diagnostics)
    {
        Context = context; IsComplete = isComplete;
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }
    public WmlEvaluationContext Context { get; }
    public bool IsComplete { get; }
    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>
/// One reached scenario's navigation data. Physical offsets and identity preserve routing,
/// but neither these offsets nor the reached contexts establish an editable owner.
/// </summary>
public sealed class WccCampaignScenarioNavigationMembership
{
    internal WccCampaignScenarioNavigationMembership(string identity, string? id, string name,
        string? sourcePath, string? sourceContentHash, int? displayStartIndex, int? displayEndIndexExclusive,
        IEnumerable<WmlEvaluationContext> contexts)
    {
        Identity = identity; ScenarioId = id; Name = name; OwnerSourcePath = sourcePath;
        OwnerSourceContentHash = sourceContentHash; DisplayStartIndex = displayStartIndex;
        DisplayEndIndexExclusive = displayEndIndexExclusive; Contexts = Array.AsReadOnly(contexts.ToArray());
    }
    public string Identity { get; }
    public string? ScenarioId { get; }
    public string Name { get; }
    public string? OwnerSourcePath { get; }
    public string? OwnerSourceContentHash { get; }
    public int? DisplayStartIndex { get; }
    public int? DisplayEndIndexExclusive { get; }
    public IReadOnlyList<WmlEvaluationContext> Contexts { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>
/// Compact reached membership for unopened chapters. Completeness concerns this navigation
/// inventory only; selected fields, unit absence and saving still need ordinary full evaluation.
/// </summary>
public sealed class WccCampaignScenarioNavigationProjection
{
    internal WccCampaignScenarioNavigationProjection(WccCampaignEntry entry,
        IEnumerable<WccCampaignScenarioNavigationContext> contexts,
        IEnumerable<WccCampaignScenarioNavigationMembership> scenarios, IEnumerable<string> diagnostics,
        bool inventoryComplete)
    {
        Entry = entry; Contexts = Array.AsReadOnly(contexts.ToArray()); Scenarios = Array.AsReadOnly(scenarios.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
        HasCompleteMembershipInventory = inventoryComplete;
    }
    public WccCampaignEntry Entry { get; }
    public IReadOnlyList<WccCampaignScenarioNavigationContext> Contexts { get; }
    public IReadOnlyList<WccCampaignScenarioNavigationMembership> Scenarios { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    private bool HasCompleteMembershipInventory { get; }
    public bool IsComplete => Entry.IsContextComplete && Contexts.Count == Entry.Plan?.Contexts.Count
        && Contexts.All(context => context.IsComplete) && HasCompleteMembershipInventory;
    public bool AuthorizesPersistence => false;
}

public static partial class WccCampaignScenarioCatalog
{
    /// <summary>
    /// Runs the existing bounded scenario inspection one context at a time, retaining only
    /// detached navigation data. Full output and scalar owners never enter this result.
    /// </summary>
    public static WccCampaignScenarioNavigationProjection ProjectNavigation(WccStaticSourceCaptureResult capture,
        WccCampaignEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(entry);
        var contexts = new List<WccCampaignScenarioNavigationContext>();
        var diagnostics = new List<string>();
        var rows = new Dictionary<string, NavigationRow>(StringComparer.Ordinal);
        if (!ReferenceEquals(entry.SourceCapture, capture) || !entry.IsContextComplete || entry.Plan is not { } plan ||
            capture.EntrySource is not { } root || root.Path != plan.CampaignMainSourcePath ||
            root.ContentHash != plan.CampaignMainTextHash || !capture.IsCaptureComplete)
            return new(entry, contexts, [], ["The entry and captured package do not share one complete current navigation plan."], false);
        if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            return new(entry, contexts, [], ["The exact pinned Core version has no supported singleplayer environment."], false);

        var completeInventory = true;
        foreach (var context in plan.Contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // This helper returns no evaluation references, so a previous difficulty's
            // expanded graphs are not kept alive while the next difficulty is processed.
            var detached = InspectNavigationContext(capture, entry, plan, root, environment!, context, cancellationToken);
            contexts.Add(detached.Context);
            completeInventory &= detached.HasCompleteInventory;
            diagnostics.AddRange(detached.Context.Diagnostics.Select(problem => context.DisplayName + ": " + problem));
            if (!detached.Context.IsComplete && detached.Context.Diagnostics.Count == 0)
                diagnostics.Add(context.DisplayName + ": Reached scenario navigation is incomplete.");
            foreach (var occurrence in detached.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!rows.TryGetValue(occurrence.Identity, out var row))
                {
                    if (rows.Count == MaximumScenarios)
                    { completeInventory = false; continue; }
                    rows.Add(occurrence.Identity, row = occurrence);
                }
                row.Contexts.Add(context);
            }
        }
        if (!completeInventory) diagnostics.Add("The reached scenario navigation inventory exceeds the bounded catalog limit.");
        return new(entry, contexts, rows.Values.Select(row => row.Freeze()), diagnostics, completeInventory);
    }

    /// <summary>Reuses already evaluated selected-entry membership without retaining its detailed graphs.</summary>
    public static WccCampaignScenarioNavigationProjection DetachNavigation(WccCampaignScenarioCatalogProjection catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return DetachNavigation(catalog.Entry,
            catalog.Contexts.Select(context => new WccCampaignScenarioNavigationContext(context.Context,
                context.IsComplete, context.Diagnostics)), catalog.Scenarios, catalog.Diagnostics, catalog.IsComplete);
    }

    /// <summary>Detaches read-only inspection membership without upgrading it to ordinary evaluation.</summary>
    public static WccCampaignScenarioNavigationProjection DetachNavigation(WccCampaignScenarioInspectionCatalogProjection catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return DetachNavigation(catalog.Entry,
            catalog.Contexts.Select(context => new WccCampaignScenarioNavigationContext(context.Context,
                context.IsComplete, context.Diagnostics)), catalog.Scenarios, catalog.Diagnostics, catalog.IsComplete);
    }

    private static WccCampaignScenarioNavigationProjection DetachNavigation(WccCampaignEntry entry,
        IEnumerable<WccCampaignScenarioNavigationContext> contextResults,
        IEnumerable<WccCampaignScenarioMembership> memberships, IReadOnlyList<string> diagnostics, bool inventoryComplete)
    {
        var contexts = contextResults.ToArray();
        var completeContexts = contexts.Where(context => context.IsComplete).Select(context => context.Context).ToHashSet();
        var scenarios = new List<WccCampaignScenarioNavigationMembership>();
        foreach (var membership in memberships)
        {
            var occurrences = membership.Owners.Where(occurrence => completeContexts.Contains(occurrence.Context)).ToArray();
            if (occurrences.Length == 0) continue;
            var first = occurrences[0];
            var row = DetachNavigationRow(membership.Identity, first.Owner, first.Evaluation);
            row.Contexts.AddRange(occurrences.Select(occurrence => occurrence.Context));
            scenarios.Add(row.Freeze());
        }
        return new(entry, contexts, scenarios, diagnostics, inventoryComplete);
    }

    private static (WccCampaignScenarioNavigationContext Context, IReadOnlyList<NavigationRow> Rows,
        bool HasCompleteInventory) InspectNavigationContext(WccStaticSourceCaptureResult capture, WccCampaignEntry entry,
        WmlCampaignEvaluationPlan plan, WmlStaticSource root, WmlStaticSingleplayerEnvironment environment,
        WmlEvaluationContext context, CancellationToken cancellationToken)
    {
        var undefined = plan.Difficulties.Select(item => item.Define).Concat(["EDITOR", "MULTIPLAYER"])
            .Where(symbol => !context.DefinedSymbols.Contains(symbol)).ToArray();
        var inspection = new WmlStaticEvaluator().InspectScenarios(new(root, context,
            new[] { environment.DefinitionsSource }.Concat(capture.PreludeSources).ToArray(), undefined,
            true, capture.IncludeResolver, WmlStaticEvaluationLimits.Campaign), cancellationToken);
        var details = new WccCampaignScenarioInspectionContext(context, inspection);
        var result = new WccCampaignScenarioNavigationContext(context, details.IsComplete, details.Diagnostics);
        var rows = new List<NavigationRow>();
        if (!result.IsComplete) return (result, rows, true);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var owner in inspection.Projection.Owners.Where(owner => owner.TagName == "scenario" && owner.Parent is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count == MaximumScenarios) return (result, rows, false);
            var physical = WccCampaignCapturedOwner.Key(owner);
            var ordinal = ordinals.GetValueOrDefault(physical); ordinals[physical] = ordinal + 1;
            var identity = WccContentHash.ComputeText(entry.Identity + "\u001f" + physical + "\u001f" + ordinal);
            rows.Add(DetachNavigationRow(identity, owner, inspection.Evaluation));
        }
        return (result, rows, true);
    }

    private static NavigationRow DetachNavigationRow(string identity, WmlStaticScalarOwner owner, WmlStaticEvaluationResult evaluation)
    {
        var id = WccCampaignEntryCatalog.Read(owner, "id");
        var source = owner.OpeningOrigin?.Source;
        var span = WccCampaignCapturedOwner.FindDisplaySpan(owner, evaluation);
        return new(identity, id, WccCampaignEntryCatalog.Read(owner, "name") ?? id ?? "Unresolved scenario",
            source?.Path, source?.ContentHash, span?.StartIndex, span?.EndIndexExclusive);
    }

    // The mutable construction rows contain only detached values as well. They never
    // retain a closure over the evaluator, its source ranges or a scalar owner.
    private sealed class NavigationRow(string identity, string? id, string name, string? sourcePath,
        string? sourceContentHash, int? start, int? end)
    {
        public string Identity { get; } = identity;
        public List<WmlEvaluationContext> Contexts { get; } = [];
        public WccCampaignScenarioNavigationMembership Freeze() =>
            new(Identity, id, name, sourcePath, sourceContentHash, start, end, Contexts);
    }
}
