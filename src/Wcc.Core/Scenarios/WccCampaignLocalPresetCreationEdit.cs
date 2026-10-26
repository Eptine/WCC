using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>One new unused shared definition. This proposal never grants write authority.</summary>
public sealed record WccCampaignLocalPresetCreationSpec(Guid Id, string Name, WccCampaignLocalPresetPayload Payload)
{
    public WccCampaignPresetScope Scope { get; init; } = WccCampaignPresetScope.Package;
}

public sealed class WccCampaignLocalPresetCreationSnapshot
{
    internal WccCampaignLocalPresetCreationSnapshot(WccCampaignStructureSnapshot structure,
        WmlCampaignEvaluationPlan? plan, bool canCreate, string diagnostic, int insertionOffset)
    { Structure = structure; Plan = plan; CanCreate = canCreate; Diagnostic = diagnostic; InsertionOffset = insertionOffset; }
    public WmlStaticSource Source => Structure.Source;
    public bool CanCreate { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal WccCampaignStructureSnapshot Structure { get; }
    internal WmlCampaignEvaluationPlan? Plan { get; }
    internal int InsertionOffset { get; }
}

/// <summary>Reads the canonical main and fixed contexts used by scoped preset storage; it grants no edit authority.</summary>
public static class WccCampaignLocalPresetCreationPlanner
{
    public static WccCampaignLocalPresetCreationSnapshot Read(WmlStaticSource mainSource,
        WccCampaignAuthoringMetadata? authoring, WmlCampaignEvaluationPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(mainSource);
        var structure = WccCampaignStructurePlanner.Read(mainSource, authoring);
        if (!structure.CanEdit) return new(structure, plan, false, structure.Diagnostic, 0);
        if (plan is null || !string.Equals(Path.GetFullPath(plan.CampaignMainSourcePath), mainSource.Path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || plan.CampaignMainTextHash != mainSource.ContentHash || plan.CampaignDefine != authoring!.CampaignDefine
            || plan.Contexts.Count is < 1 or > 16 || plan.Contexts.Count != structure.DifficultyRows.Count
            || !plan.Difficulties.Select(row => row.Define).SequenceEqual(authoring.FixedDifficultyDefines, StringComparer.Ordinal)
            || plan.Contexts.Select(context => context.SemanticKey).Distinct(StringComparer.Ordinal).Count() != plan.Contexts.Count
            || plan.Contexts.Where((context, index) => context.CampaignDefine != plan.CampaignDefine
                || context.DifficultyDefine != plan.Difficulties[index].Define).Any())
            return new(structure, plan, false, "Preset creation requires this exact main source and its complete fixed difficulty plan.", 0);
        var shape = WccCampaignChapterAuthoringPlanner.MaskOtherChaptersForStructure(
            new WmlStaticSource(mainSource.Path, WccCampaignDependencyAuthoringPlanner.MaskManagedDependencyBlocksForStructure(mainSource)), authoring!.CampaignDefine);
        var scan = WmlAuthoredStructureScanner.Scan(shape);
        var managed = WccCampaignLocalPresetCatalog.ReadDefinitions(mainSource, plan).Where(entry => entry.IsSupported).ToArray();
        var gates = scan.Directives.Where(directive => directive.Name != "textdomain"
            && !managed.Any(entry => Contains(entry.DefinitionRange, scan.Lines[directive.Line].StartOffset))).ToArray();
        if (gates.Length != 2 || gates[0].Name != "ifdef" || gates[1].Name != "endif")
            return new(structure, plan, false, "Preset creation needs the actual single canonical campaign gate.", 0);
        return new(structure, plan, true, "Creates shared definitions only; apply an instance separately.", scan.Lines[gates[0].Line].EndOffsetExclusive);
    }

    private static bool Contains(WmlStaticSourceRange range, int offset) =>
        offset >= range.StartOffset && offset < range.StartOffset + range.Length;
}
