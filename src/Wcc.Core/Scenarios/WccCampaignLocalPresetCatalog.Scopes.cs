using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccCampaignLocalPresetCatalog
{
    public static WccCampaignLocalPresetCatalogSnapshot Build(WccStaticSourceCaptureResult capture,
        WmlCampaignEvaluationPlan? plan, IReadOnlyList<WmlCampaignEvaluationPlan>? packagePlans)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var problems = capture.Diagnostics.ToList();
        if (capture.EntrySource is not { IsAuthored: true } main)
            return new(capture.EntrySource, [], problems.Append("The local preset catalog needs the exact captured package entry source."));
        if (plan is not null)
        {
            var planSources = capture.Sources.Append(main).Where(source => source.IsAuthored && string.Equals(source.Path,
                    plan.CampaignMainSourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .DistinctBy(source => (source.Path, source.ContentHash)).ToArray();
            if (planSources.Length != 1 || planSources[0].ContentHash != plan.CampaignMainTextHash)
                return new(main, [], problems.Append("The campaign plan no longer matches its exact captured authored source."));
        }
        var plans = packagePlans ?? WmlCampaignEvaluationPlanDiscovery.Discover(main.Text, main.Path, plan?.Contexts.FirstOrDefault()?.TargetCompatibility ?? "1.20");
        var root = Path.GetDirectoryName(main.Path)!;
        IReadOnlyList<WccPackagePresetReceipt> receipts = [];
        var receiptIssue = (string?)null;
        IReadOnlyList<WccCampaignPresetScope> verifiedScopes = [WccCampaignPresetScope.Package];
        try
        {
            var manifest = WccPackageAuthoringManifest.Read(main, Path.GetFileName(root), capture.Sources);
            var content = WccPackageContentAuthoringManifest.Read(manifest.Document);
            receipts = content.Presets; receiptIssue = content.Issue;
            var primary = manifest.Document.Chapters.SingleOrDefault(chapter => chapter.IsPrimary);
            if (primary is not null)
            {
                var authoring = new WccCampaignAuthoringMetadata { LayoutVersion = 2, AddonId = manifest.Document.ProjectAddonId,
                    CampaignDefine = primary.CampaignDefine, MainRelativePath = "_main.cfg", UsesExplicitDifficultyValues = true,
                    FixedDifficultyDefines = primary.DifficultyDefines.ToList() };
                var chapters = WccCampaignChapterAuthoringPlanner.Read(main, authoring, capture.Sources);
                if (chapters.CanEdit) verifiedScopes = new[] { WccCampaignPresetScope.Package }.Concat(chapters.Chapters.Select(chapter =>
                    new WccCampaignPresetScope(WccCampaignPresetScopeKind.Campaign, chapter.CampaignId, chapter.CampaignDefine, chapter.Id))).ToArray();
            }
        }
        catch (Exception issue) when (issue is InvalidOperationException or ArgumentException) { receiptIssue = issue.Message; }
        if (receiptIssue is not null) problems.Add(receiptIssue);
        if (capture.Sources.Count > 200_000 || capture.Sources.Sum(source => (long)source.Text.Length) > 64 * 1024 * 1024)
            return new(main, [], problems.Append("The captured preset inventory exceeds its bounded size."));
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = ReadPackageDefinitions(capture.Sources, plan, receipts, root, plans, verifiedScopes).ToList();
        if (entries.Count > 4096) return new(main, [], problems.Append("The captured package has too many managed presets."));
        var scopes = verifiedScopes;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entries.Any(other => !ReferenceEquals(other, entry) && (other.Id == entry.Id || other.Name == entry.Name
                    && WccCampaignPresetScopePolicy.Overlaps(entry.Scope, other.Scope, scopes))))
                entries[index] = entry.Refused("The managed preset has an overlapping symbol or duplicate durable identity; use RAW.");
            else if (!entry.Scope.IsPackage && !verifiedScopes.Contains(entry.Scope))
                entries[index] = entry.Refused("The chapter preset scope has no exact supported activation layout; inspect its RAW definition.");
            else if (!capture.IsCaptureComplete)
                entries[index] = entry.Refused("The captured context is incomplete; the preset remains available for inspection only.");
            else if (entry.HasContentReceipt && receiptIssue is not null)
                entries[index] = entry.Refused(receiptIssue);
        }
        return new(main, entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ThenBy(entry => entry.SourcePath, paths), problems);
    }

    internal static IReadOnlyList<WccCampaignLocalPresetEntry> ReadDefinitions(WmlStaticSource source,
        WmlCampaignEvaluationPlan? selectedPlan, IReadOnlyList<WccPackagePresetReceipt> receipts, string packageRoot,
        IReadOnlyList<WmlCampaignEvaluationPlan> packagePlans, IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets)
    {
        if (unitPresets is null) return ReadPackageDefinitions([source], selectedPlan, receipts, packageRoot, packagePlans);
        if (!source.IsAuthored || source.Text.Length > 8 * 1024 * 1024) return [];
        var relative = Path.GetRelativePath(packageRoot, source.Path).Replace('\\', '/');
        var relevant = receipts.Where(receipt => receipt.SourcePath == relative).ToArray();
        var scan = WmlAuthoredStructureScanner.Scan(source.Text);
        var entries = new List<WccCampaignLocalPresetEntry>();
        foreach (var opening in scan.Directives.Where(directive => directive.Name == "define" && directive.DefinitionDepth == 0))
        {
            var end = scan.Directives.FirstOrDefault(directive => directive.Name == "enddef" && directive.DefinitionDepth == 1 && directive.Line > opening.Line);
            if (end.Name is null) continue;
            var first = scan.Lines[opening.Line]; var last = scan.Lines[end.Line];
            var header = source.Text[first.StartOffset..first.ContentEndOffset].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var matching = relevant.Where(receipt => header.Length > 1 && receipt.Symbol == header[1]).ToArray();
            var receipt = matching.Length == 1 ? matching[0] : null;
            var plan = receipt is { Scope.IsPackage: false } ? packagePlans.SingleOrDefault(receipt.Scope.Targets) : selectedPlan;
            var entry = ReadPhysical(source, new(source, first.StartOffset, last.EndOffsetExclusive - first.StartOffset),
                new(source, first.EndOffsetExclusive, last.StartOffset - first.EndOffsetExclusive), plan, scan.Problem, receipt, unitPresets);
            if (entry is null) continue;
            entries.Add(matching.Length > 1 ? entry.Refused("Multiple content receipts claim this declaration.") : entry);
        }
        return Array.AsReadOnly(entries.ToArray());
    }
    // Two physical reads over the same captured declarations. Unit entries from
    // the first pass are retained verbatim; only Events are rebound to that exact
    // inventory, so definition/file order never becomes reference authority.
    internal static IReadOnlyList<WccCampaignLocalPresetEntry> ReadPackageDefinitions(
        IReadOnlyList<WmlStaticSource> sources, WmlCampaignEvaluationPlan? selectedPlan,
        IReadOnlyList<WccPackagePresetReceipt> receipts, string root, IReadOnlyList<WmlCampaignEvaluationPlan> plans,
        IReadOnlyList<WccCampaignPresetScope>? verifiedScopes = null)
    {
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var groups = sources.Where(source => source.IsAuthored).GroupBy(source => source.Path, paths)
            .Where(group => IsPackageSource(group.Key)).ToArray();
        var entries = new List<WccCampaignLocalPresetEntry>();
        var completeSources = new List<WmlStaticSource>();
        foreach (var group in groups)
        {
            var source = group.First();
            var definitions = ReadDefinitions(source, selectedPlan, receipts, root, plans, []);
            var consistent = group.Select(item => item.ContentHash).Distinct(StringComparer.Ordinal).Count() == 1;
            entries.AddRange(consistent ? definitions : definitions.Select(entry => entry.Refused("This source has conflicting captured revisions.")));
            if (entries.Count > 4096) return Array.AsReadOnly(entries.ToArray());
            if (consistent) completeSources.Add(source);
        }
        var scopes = verifiedScopes ?? new[] { WccCampaignPresetScope.Package }.Concat(receipts.Select(receipt => receipt.Scope)).Distinct().ToArray();
        var units = entries.Where(entry => entry.Role == WccCampaignLocalPresetRole.Unit && entry.IsSupported &&
            (entry.Scope.IsPackage || scopes.Contains(entry.Scope)) && !entries.Any(other => !ReferenceEquals(other, entry) &&
                (other.Id == entry.Id || other.Name == entry.Name && WccCampaignPresetScopePolicy.Overlaps(other.Scope, entry.Scope, scopes)))).ToArray();
        var eventSources = entries.Where(entry => entry.Role == WccCampaignLocalPresetRole.Event).Select(entry => entry.DefinitionRange.Source).ToHashSet();
        foreach (var source in completeSources.Where(eventSources.Contains))
        {
            var rebound = ReadDefinitions(source, selectedPlan, receipts, root, plans, units);
            for (var index = 0; index < entries.Count; index++)
                if (entries[index].Role == WccCampaignLocalPresetRole.Event && ReferenceEquals(entries[index].DefinitionRange.Source, source))
                    entries[index] = rebound.Single(entry => entry.DefinitionRange.StartOffset == entries[index].DefinitionRange.StartOffset);
        }
        return Array.AsReadOnly(entries.ToArray());

        bool IsPackageSource(string path)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal);
        }
    }
}