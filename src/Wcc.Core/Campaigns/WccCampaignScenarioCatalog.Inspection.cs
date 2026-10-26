using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>A complete retained scenario view is distinct from complete package evaluation.</summary>
public sealed class WccCampaignScenarioInspectionContext
{
    internal WccCampaignScenarioInspectionContext(WmlEvaluationContext context, WmlScenarioInspectionResult inspection)
    {
        Context = context;
        Inspection = inspection;
        Diagnostics = Array.AsReadOnly(inspection.Evaluation.Diagnostics.Select(item => item.Message)
            .Concat(inspection.Projection.Diagnostics.Select(item => item.Message)).Distinct(StringComparer.Ordinal).ToArray());
    }

    public WmlEvaluationContext Context { get; }
    public WmlScenarioInspectionResult Inspection { get; }
    public WmlStaticEvaluationResult Evaluation => Inspection.Evaluation;
    public WmlStaticScalarProjection Projection => Inspection.Projection;
    public IReadOnlyList<string> Diagnostics { get; }
    public bool IsComplete => Inspection.IsComplete;
}

/// <summary>Reached scenario inspection only. Missing unit interiors cannot prove unit absence or authorize saving.</summary>
public sealed class WccCampaignScenarioInspectionCatalogProjection
{
    internal WccCampaignScenarioInspectionCatalogProjection(WccCampaignEntry entry,
        IEnumerable<WccCampaignScenarioInspectionContext> contexts,
        IEnumerable<WccCampaignScenarioMembership> scenarios, IEnumerable<string> diagnostics, bool inventoryComplete)
    {
        Entry = entry;
        Contexts = Array.AsReadOnly(contexts.ToArray());
        Scenarios = Array.AsReadOnly(scenarios.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
        HasCompleteMembershipInventory = inventoryComplete;
    }

    public WccCampaignEntry Entry { get; }
    public IReadOnlyList<WccCampaignScenarioInspectionContext> Contexts { get; }
    public IReadOnlyList<WccCampaignScenarioMembership> Scenarios { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    private bool HasCompleteMembershipInventory { get; }
    public bool IsComplete => Entry.IsContextComplete && Contexts.Count == Entry.Plan?.Contexts.Count
        && Contexts.All(context => context.IsComplete) && HasCompleteMembershipInventory;
    public bool AuthorizesPersistence => false;
}

public static partial class WccCampaignScenarioCatalog
{
    /// <summary>
    /// Runs the same ordered preprocessor with a distinct bounded inspection output policy.
    /// This never replaces full membership/write evidence. Every retained occurrence is still
    /// mapped to its captured physical opening; folder layout and IDs do not establish ownership.
    /// </summary>
    public static WccCampaignScenarioInspectionCatalogProjection ProjectInspection(WccStaticSourceCaptureResult capture,
        WccCampaignEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(entry);
        var contexts = new List<WccCampaignScenarioInspectionContext>();
        var diagnostics = new List<string>();
        var rows = new Dictionary<string, (WmlStaticScalarOwner Owner, WmlStaticEvaluationResult Evaluation,
            List<WccCampaignScenarioOccurrence> Occurrences)>(StringComparer.Ordinal);
        if (!ReferenceEquals(entry.SourceCapture, capture) || !entry.IsContextComplete || entry.Plan is not { } plan ||
            capture.EntrySource is not { } root || root.Path != plan.CampaignMainSourcePath ||
            root.ContentHash != plan.CampaignMainTextHash || !capture.IsCaptureComplete)
            return new(entry, contexts, [], ["The entry and captured package do not share one complete current inspection plan."], false);
        if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            return new(entry, contexts, [], ["The exact pinned Core version has no supported singleplayer environment."], false);

        var completeInventory = true;
        var inspections = new Dictionary<string, WmlScenarioInspectionResult>(StringComparer.Ordinal);
        foreach (var context in plan.Contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inspections.TryGetValue(context.SemanticKey, out var inspection))
            {
                var undefined = plan.Difficulties.Select(item => item.Define).Concat(["EDITOR", "MULTIPLAYER"])
                    .Where(symbol => !context.DefinedSymbols.Contains(symbol)).ToArray();
                inspection = new WmlStaticEvaluator().InspectScenarios(new(root, context,
                    new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).ToArray(), undefined,
                    true, capture.IncludeResolver, WmlStaticEvaluationLimits.Campaign), cancellationToken);
                inspections.Add(context.SemanticKey, inspection);
            }
            var result = new WccCampaignScenarioInspectionContext(context, inspection);
            contexts.Add(result);
            diagnostics.AddRange(result.Diagnostics.Select(problem => context.DisplayName + ": " + problem));
            if (!result.IsComplete)
            {
                if (result.Diagnostics.Count == 0) diagnostics.Add(context.DisplayName + ": Retained scenario inspection is incomplete.");
                continue; // A partial filtered stream cannot establish reached membership or field ownership.
            }

            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var owner in inspection.Projection.Owners.Where(owner => owner.TagName == "scenario" && owner.Parent is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var physical = WccCampaignCapturedOwner.Key(owner);
                var ordinal = occurrences.GetValueOrDefault(physical);
                occurrences[physical] = ordinal + 1;
                var key = physical + "\u001f" + ordinal;
                if (!rows.TryGetValue(key, out var row))
                {
                    if (rows.Count == MaximumScenarios)
                    {
                        completeInventory = false;
                        diagnostics.Add("The reached scenario inspection inventory exceeds the bounded catalog limit.");
                        break;
                    }
                    row = (owner, inspection.Evaluation, []);
                    rows.Add(key, row);
                }
                row.Occurrences.Add(new(context, owner, inspection.Evaluation));
            }
        }

        return new(entry, contexts, rows.Select(pair =>
        {
            var owner = pair.Value.Owner;
            var id = WccCampaignEntryCatalog.Read(owner, "id");
            return new WccCampaignScenarioMembership(WccContentHash.ComputeText(entry.Identity + "\u001f" + pair.Key), id,
                WccCampaignEntryCatalog.Read(owner, "name") ?? id ?? "Unresolved scenario", owner.OpeningOrigin?.Source,
                null, pair.Value.Occurrences) // Inspection never publishes a strict authored write span.
            { DisplayScenarioSpan = WccCampaignCapturedOwner.FindDisplaySpan(owner, pair.Value.Evaluation) };
        }), diagnostics, completeInventory);
    }
}
