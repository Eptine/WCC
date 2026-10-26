using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>
/// Presents one campaign to the ordinary editor without changing the package that
/// Wesnoth preprocesses. Physical menu sources and the real entrypoint stay distinct.
/// This is source binding evidence; the existing reviewer and writer authorize Save.
/// </summary>
public sealed class WccSelectedCampaignEntryAdapter
{
    private WccSelectedCampaignEntryAdapter(WccCampaignEntryCatalogProjection catalog, WccCampaignEntry entry,
        CampaignInitialFieldProjection menu)
    {
        Catalog = catalog; Entry = entry; Menu = menu;
    }

    public WccCampaignEntryCatalogProjection Catalog { get; }
    public WccStaticSourceCaptureResult Capture => Catalog.Capture;
    public WccCampaignEntry Entry { get; }
    public CampaignInitialFieldProjection Menu { get; }
    public WmlStaticScalarOwner Owner => Entry.Owner;
    public WmlStaticSource OwnerSource => Entry.OwnerSource!;
    public WmlStaticSource RootSource => Capture.EntrySource!;
    public WmlCampaignEvaluationPlan Plan => Entry.Plan!;
    public bool AuthorizesPersistence => false;

    public static WccSelectedCampaignEntryAdapter Create(WccStaticSourceCaptureResult capture,
        string selectedEntryIdentity, WmlCampaignEvaluationPlan? expectedPlan = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedEntryIdentity);
        return Create(WccCampaignEntryCatalog.Project(capture, expectedPlan, cancellationToken),
            selectedEntryIdentity, expectedPlan);
    }

    public static WccSelectedCampaignEntryAdapter Create(WccCampaignEntryCatalogProjection catalog,
        string selectedEntryIdentity, WmlCampaignEvaluationPlan? expectedPlan = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedEntryIdentity);
        if (!catalog.Capture.IsCaptureComplete || catalog.Capture.EntrySource is not { } root || !catalog.Menu.IsComplete)
            throw new InvalidOperationException("The selected campaign needs a complete captured package menu.");
        var entries = catalog.Entries.Where(entry => entry.Identity == selectedEntryIdentity).Take(2).ToArray();
        if (entries.Length != 1 || entries[0] is not { IsContextComplete: true, OwnerSource: { } source,
                AuthoredCampaignSpan: not null, Plan: { } plan } entry ||
            !catalog.Capture.Sources.Any(candidate => WccScenarioFieldRouteAnalyzer.SameSource(candidate, source) && candidate.Text == source.Text))
            throw new InvalidOperationException("The selected campaign needs one exact physical menu owner and complete context metadata.");
        if (expectedPlan is not null && (!expectedPlan.IsContextDiscoveryComplete ||
            !SameRoot(expectedPlan, root) || expectedPlan.CampaignId != plan.CampaignId ||
            expectedPlan.CampaignDefine != plan.CampaignDefine ||
            !expectedPlan.Difficulties.Select(row => (row.Define, row.IsDefault))
                .SequenceEqual(plan.Difficulties.Select(row => (row.Define, row.IsDefault))) ||
            !expectedPlan.Contexts.Select(context => context.SemanticKey)
                .SequenceEqual(plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal)))
            throw new InvalidOperationException("The selected campaign's source or declared contexts changed.");
        var menu = WccCampaignEntryCatalog.CreateSelectedMenuProjection(catalog, entry);
        if (CampaignInitialFieldProjector.CheckContextPlan(menu, entry.Owner, plan) is { } refusal)
            throw new InvalidOperationException(refusal);
        return new(catalog, entry, menu);
    }

    /// <summary>Observes the selected candidate owner after an existing typed recipe.
    /// ExpectedPlan supplies the compatibility target, not permission to change topology.</summary>
    public static WmlCampaignEvaluationPlan ObservePlan(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, WmlStaticSource rootMainSource,
        WmlStaticScalarOwner exactSelectedOwner, WmlCampaignEvaluationPlan? expectedPlan = null)
    {
        ArgumentNullException.ThrowIfNull(evaluation); ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(rootMainSource); ArgumentNullException.ThrowIfNull(exactSelectedOwner);
        if (!evaluation.IsComplete || evaluation.Diagnostics.Any(item => item.IsBlocking) ||
            !ReferenceEquals(projection.SourceEvaluation, evaluation) || projection.Diagnostics.Any(item => item.IsBlocking) ||
            exactSelectedOwner.TagName != "campaign" || exactSelectedOwner.Parent is not null ||
            projection.Owners.Count(owner => ReferenceEquals(owner, exactSelectedOwner)) != 1 ||
            !evaluation.Sources.Any(source => WccScenarioFieldRouteAnalyzer.SameSource(source, rootMainSource) && source.Text == rootMainSource.Text) ||
            WccCampaignCapturedOwner.FindAuthoredSpan(exactSelectedOwner, evaluation) is not { } span)
            throw new InvalidOperationException("The selected campaign plan has no exact complete captured menu occurrence.");
        if (!WmlCapturedCampaignEvaluationPlan.TryObserveMetadata(exactSelectedOwner, out var metadata, out var problem) ||
            exactSelectedOwner.ResolveField("id").State == WmlStaticScalarResolutionState.Unresolved)
            throw new InvalidOperationException(problem ?? "The selected campaign context metadata is unresolved.");
        var target = expectedPlan?.Contexts.FirstOrDefault()?.TargetCompatibility;
        var contexts = metadata!.Difficulties.Count == 0
            ? new[] { new WmlEvaluationContext(metadata.CampaignDefine, additionalDefines: metadata.ExtraDefines, targetCompatibility: target) }
            : metadata.Difficulties.Select(row => new WmlEvaluationContext(metadata.CampaignDefine, row.Define,
                metadata.ExtraDefines, target)).ToArray();
        return new(WccCampaignEntryCatalog.Read(exactSelectedOwner, "id"), metadata.CampaignDefine,
            metadata.Difficulties, contexts, [], rootMainSource.Path, rootMainSource.Text, isContextDiscoveryComplete: true)
        {
            AuthoredCampaignSpan = exactSelectedOwner.OpeningOrigin is { } opening &&
                WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, rootMainSource) ? span : null
        };
    }

    private static bool SameRoot(WmlCampaignEvaluationPlan plan, WmlStaticSource root) =>
        string.Equals(plan.CampaignMainSourcePath, root.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        plan.CampaignMainTextHash == root.ContentHash;
}
