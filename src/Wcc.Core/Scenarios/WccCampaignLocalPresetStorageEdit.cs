using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Captured canonical storage evidence; never file creation or replacement authority.</summary>
public sealed class WccCampaignLocalPresetStorageSnapshot
{
    internal WccCampaignLocalPresetStorageSnapshot(WccCampaignLocalPresetCreationSnapshot main,
        WmlStaticSource? storage, IEnumerable<WccCampaignLocalPresetEntry> moved,
        IEnumerable<WccCampaignLocalPresetEntry> stored, bool canStore, string diagnostic)
    { Main = main; StorageSource = storage; MainEntries = Array.AsReadOnly(moved.ToArray());
        StoredEntries = Array.AsReadOnly(stored.ToArray()); Entries = Array.AsReadOnly(MainEntries.Concat(StoredEntries).ToArray());
        CanStore = canStore; Diagnostic = diagnostic; }
    public WmlStaticSource Source => Main.Source;
    public WmlStaticSource? StorageSource { get; }
    public string StorageRelativePath => WccCampaignLocalPresetStoragePlanner.SharedStorageRelativePath;
    public IReadOnlyList<WccCampaignPresetStorageLocation> Locations { get; internal init; } = [];
    public IReadOnlyList<WccCampaignPresetScope> AvailableScopes { get; internal init; } = [WccCampaignPresetScope.Package];
    public WccCampaignPresetScope SelectedScope { get; internal init; } = WccCampaignPresetScope.Package;
    public IReadOnlyList<WmlCampaignEvaluationPlan> PackagePlans { get; internal init; } = [];
    internal WccCampaignAuthoringMetadata? PackageAuthoring { get; init; }
    internal WccPackageAuthoringManifestSnapshot? Manifest { get; init; }
    internal bool UsesContentReceipts => Manifest is not null;
    internal WccCampaignChapterAuthoringSnapshot? Chapters { get; init; }
    internal Wcc.Core.Library.WccStaticSourceCaptureResult? CapturedContext { get; set; }
    public string StoragePath => Path.Combine(Path.GetDirectoryName(Source.Path)!, StorageRelativePath.Replace('/', Path.DirectorySeparatorChar));
    public bool CanStore { get; }
    public bool CanMigrate => CanStore && MainEntries.Count != 0;
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    public IReadOnlyList<WccCampaignLocalPresetEntry> Entries { get; }
    internal WccCampaignLocalPresetCreationSnapshot Main { get; }
    internal IReadOnlyList<WccCampaignLocalPresetEntry> MainEntries { get; }
    internal IReadOnlyList<WccCampaignLocalPresetEntry> StoredEntries { get; }
}

public sealed class WccCampaignLocalPresetStorageEdit
{
    internal WccCampaignLocalPresetStorageEdit(WccCampaignLocalPresetStorageSnapshot snapshot,
        IEnumerable<WccCampaignLocalPresetCreationSpec> presets)
    { Snapshot = snapshot; Presets = Array.AsReadOnly(presets.ToArray()); }
    internal WccCampaignLocalPresetStorageEdit(WccCampaignLocalPresetStorageSnapshot snapshot,
        IEnumerable<WccCampaignLocalPresetMutation> mutations)
    { Snapshot = snapshot; Presets = []; Mutations = Array.AsReadOnly(mutations.ToArray()); }
    internal WccCampaignLocalPresetStorageEdit(WccCampaignLocalPresetStorageSnapshot snapshot,
        IEnumerable<WccCampaignLocalPresetCreationSpec> presets, IEnumerable<WccCampaignLocalPresetMutation> mutations)
    { Snapshot = snapshot; Presets = Array.AsReadOnly(presets.ToArray()); Mutations = Array.AsReadOnly(mutations.ToArray()); }
    public WccCampaignLocalPresetStorageSnapshot Snapshot { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public IReadOnlyList<WccCampaignLocalPresetCreationSpec> Presets { get; }
    public IReadOnlyList<WccCampaignLocalPresetMutation> Mutations { get; } = [];
    public bool AuthorizesPersistence => false;
}

/// <summary>Only exact byte-identical definition relocation may change an evidence source path.</summary>
internal sealed record WccCampaignPresetSourceRelocation(WmlStaticSourceRange Before, WmlStaticSourceRange After);

public static partial class WccCampaignLocalPresetStoragePlanner
{
    public const string StorageHeader = "# wcc-preset-storage: v2";
    internal const string SharedStorageRelativePath = "utils/presets.cfg";
    internal static string ChapterStorageRelativePath(string chapterId) => "chapters/" + chapterId + "/utils/presets.cfg";
    internal static bool IsPresetStorageRelativePath(string path)
    {
        if (path == SharedStorageRelativePath) return true;
        var segments = path.Split('/');
        return segments.Length == 4 && segments[0] == "chapters"
            && WccCampaignCreationTokenPolicy.ValidateAddonId(segments[1]) is null
            && path == ChapterStorageRelativePath(segments[1]);
    }

    public static WccCampaignLocalPresetStorageSnapshot Read(WmlStaticSource main,
        WccCampaignAuthoringMetadata? authoring, WmlCampaignEvaluationPlan? plan,
        IReadOnlyList<WmlStaticSource> capturedCampaignSources) => Read(main, authoring, plan,
            capturedCampaignSources, WmlCampaignEvaluationPlanDiscovery.Discover(main.Text, main.Path,
                plan?.Contexts.FirstOrDefault()?.TargetCompatibility ?? "1.20"));
    public static WccCampaignLocalPresetStorageEdit CreateEdit(WccCampaignLocalPresetStorageSnapshot snapshot,
        IEnumerable<WccCampaignLocalPresetCreationSpec> newPresets)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(newPresets);
        if (!snapshot.CanStore) throw new InvalidOperationException(snapshot.Diagnostic);
        var rows = newPresets.ToArray();
        ValidateScopedCreation(snapshot, rows);
        var edit = new WccCampaignLocalPresetStorageEdit(snapshot, rows);
        _ = PlanContent(edit);
        return edit;
    }

    internal static IReadOnlyList<WccScenarioFieldSourcePatch> PlanMainPatches(WccCampaignLocalPresetStorageEdit edit) =>
        (edit.Mutations.Count != 0 ? MutationPatches(edit, edit.Source)
            : edit.Snapshot.MainEntries.Select(entry => new WccScenarioFieldSourcePatch(entry.DefinitionRange.StartOffset, entry.DefinitionRange.Length, "")))
        .Concat(PlanScopeIncludes(edit)).OrderBy(patch => patch.Start).ToArray();

    internal static IReadOnlyList<WccScenarioFieldBatchCandidate> PlanStorageCandidates(WccCampaignLocalPresetStorageEdit edit) =>
        PlanContent(edit).Replacements;

    internal static IReadOnlyList<WmlStaticSource> PlanSources(WccCampaignLocalPresetStorageEdit edit) => PlanContent(edit).Creates;
    internal static IReadOnlyList<WccCampaignPresetSourceRelocation> PlanRelocations(WccCampaignLocalPresetStorageEdit edit)
    {
        if (edit.Mutations.Count != 0 || edit.Snapshot.MainEntries.Count == 0) return [];
        var output = PlanContent(edit).Replacements.Select(candidate => candidate.CandidateSource).Concat(PlanSources(edit))
            .Single(source => PathEquals(source.Path, edit.Snapshot.StoragePath));
        var definitions = WccCampaignLocalPresetCatalog.ReadDefinitions(output, edit.Snapshot.Main.Plan);
        return edit.Snapshot.MainEntries.Select(before =>
        {
            var after = definitions.Single(entry => entry.Id == before.Id && entry.Name == before.Name);
            if (after.DefinitionRange.Text != before.DefinitionRange.Text || after.Definition.TextDomain != before.Definition.TextDomain)
                throw new InvalidOperationException("A relocated definition must retain its exact source bytes and translation domain.");
            return new WccCampaignPresetSourceRelocation(before.DefinitionRange, after.DefinitionRange);
        }).ToArray();
    }

    internal static IReadOnlyList<WccCampaignLocalPresetEntry> ReadStorage(WmlStaticSource source, WmlCampaignEvaluationPlan? plan)
    {
        if (!source.IsAuthored || source.Text.Length > 8 * 1024 * 1024
            || source.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) != StorageHeader)
            throw new InvalidOperationException("The existing utility is not an exact managed preset storage file; preserve it and use RAW.");
        _ = ReadStorageEnvelope(source, plan is null ? null : ExpectedStorageGuard(source, Path.GetDirectoryName(plan.CampaignMainSourcePath)!));
        var definitions = WccCampaignLocalPresetCatalog.ReadDefinitions(source, plan);
        ValidateStorageCoverage(source, definitions);
        return definitions;
    }

    // This operates only on the validated managed file. Its supported
    // definitions are declarations, not calls, and cannot change file-scope
    // domain state. The first fence establishes an unknown incoming domain;
    // changed domains must survive even when their definition is removed.
    private static IReadOnlyList<WccScenarioFieldSourcePatch> StorageDomainPatches(WmlStaticSource source,
        WmlCampaignEvaluationPlan? plan)
    {
        var scan = WmlAuthoredStructureScanner.Scan(source.Text);
        var patches = new List<WccScenarioFieldSourcePatch>();
        string? activeDomain = null;
        foreach (var directive in scan.Directives.Where(directive => directive.DefinitionDepth == 0 && directive.Name == "textdomain"))
        {
            var line = scan.Lines[directive.Line];
            var raw = source.Text[line.StartOffset..line.ContentEndOffset].Trim();
            if (!raw.StartsWith("#textdomain", StringComparison.Ordinal)) { activeDomain = null; continue; }
            var domain = raw["#textdomain".Length..].Trim();
            if (domain == activeDomain)
                patches.Add(new(line.StartOffset, line.EndOffsetExclusive - line.StartOffset, ""));
            else activeDomain = domain;
        }
        return Array.AsReadOnly(patches.ToArray());
    }
}
