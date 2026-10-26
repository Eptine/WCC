using System.Text.Json.Serialization;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public enum WccCampaignDependencyHook { Menu, Gameplay, Campaign }
public enum WccCampaignDependencyStepKind { AssetPath, Include, UnitsInclude, LoadResource, Define, Undefine, Macro, CustomWml }

/// <summary>One ordered loading instruction. Paths are relative to the dependency, never a local machine.</summary>
public sealed record WccCampaignDependencyStep(WccCampaignDependencyHook Hook,
    WccCampaignDependencyStepKind Kind, string Value, string Arguments = "");

/// <summary>Portable loading intent. Selecting a source folder does not itself activate any of these steps.</summary>
public sealed class WccCampaignDependencySpec
{
    [JsonConstructor]
    public WccCampaignDependencySpec(Guid id, string addonId, IReadOnlyList<string> targetCampaignDefines,
        IReadOnlyList<WccCampaignDependencyStep> steps, bool declareDependency = true, bool appliesToAllCampaigns = false)
    {
        Id = id; AddonId = addonId; DeclareDependency = declareDependency; AppliesToAllCampaigns = appliesToAllCampaigns;
        TargetCampaignDefines = Array.AsReadOnly((targetCampaignDefines ?? []).ToArray());
        Steps = Array.AsReadOnly((steps ?? []).ToArray());
    }
    public Guid Id { get; }
    public string AddonId { get; }
    public IReadOnlyList<string> TargetCampaignDefines { get; }
    public IReadOnlyList<WccCampaignDependencyStep> Steps { get; }
    public bool DeclareDependency { get; }
    public bool AppliesToAllCampaigns { get; }
}

public sealed record WccCampaignPackageSourceReplacement(WmlStaticSource Source, string Candidate)
{
    public WmlStaticSource CandidateSource => new(Source.Path, Candidate);
}

public sealed class WccCampaignDependencyEntry
{
    internal WccCampaignDependencyEntry(WccCampaignDependencySpec spec, WmlStaticSourceRange root,
        WmlStaticSource loader, IEnumerable<WmlStaticSourceRange> hooks)
    { Spec = spec; RootRange = root; LoaderSource = loader; CampaignHookRanges = Array.AsReadOnly(hooks.ToArray()); }
    public WccCampaignDependencySpec Spec { get; }
    public WmlStaticSourceRange RootRange { get; }
    public WmlStaticSource LoaderSource { get; }
    public IReadOnlyList<WmlStaticSourceRange> CampaignHookRanges { get; }
}

public sealed class WccCampaignDependencySnapshot
{
    internal WccCampaignDependencySnapshot(WmlStaticSource main, string addonId,
        IReadOnlyList<WmlStaticSource> sources, IEnumerable<WccCampaignDependencyEntry> entries,
        bool canAdd, string diagnostic)
    { MainSource = main; ProjectAddonId = addonId; Sources = sources; Entries = Array.AsReadOnly(entries.ToArray());
        CanAdd = canAdd; Diagnostic = diagnostic; }
    public WmlStaticSource MainSource { get; }
    public string ProjectAddonId { get; }
    public IReadOnlyList<WmlStaticSource> Sources { get; }
    public IReadOnlyList<WccCampaignDependencyEntry> Entries { get; }
    public bool CanAdd { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal WccCampaignDependencyMetadataReceipt? MetadataReceipt { get; set; }
    internal WmlStaticSource? ManifestSource { get; set; }
    internal WccPackageAuthoringManifestSnapshot? Manifest { get; set; }
}

/// <summary>Pure source proposal. Fresh capture/review and the common coordinator still own persistence.</summary>
public sealed class WccCampaignDependencyMutationPlan
{
    internal WccCampaignDependencyMutationPlan(WccCampaignDependencySnapshot snapshot,
        IEnumerable<WccCampaignDependencySpec> specs, IEnumerable<WccCampaignPackageSourceReplacement> replacements,
        IEnumerable<WmlStaticSource> creates, string identity, IEnumerable<WmlStaticSource>? deletes = null)
    { Snapshot = snapshot; DesiredSpecs = Array.AsReadOnly(specs.ToArray());
        SourceReplacements = Array.AsReadOnly(replacements.ToArray()); SourceCreates = Array.AsReadOnly(creates.ToArray()); SourceDeletes = Array.AsReadOnly((deletes ?? []).ToArray()); Identity = identity; }
    public WccCampaignDependencySnapshot Snapshot { get; }
    public IReadOnlyList<WccCampaignDependencySpec> DesiredSpecs { get; }
    public IReadOnlyList<WccCampaignPackageSourceReplacement> SourceReplacements { get; }
    public IReadOnlyList<WmlStaticSource> SourceCreates { get; }
    public IReadOnlyList<WmlStaticSource> SourceDeletes { get; }
    public string Identity { get; }
    public bool AuthorizesPersistence => false;
}

public sealed record WccCampaignDependencyRecommendation(string Label, string Description,
    IReadOnlyList<WccCampaignDependencyStep> Steps);
public sealed record WccCampaignDependencyRecommendations(string AddonId,
    IReadOnlyList<WccCampaignDependencyRecommendation> Suggestions, IReadOnlyList<string> Notes);
public sealed record WccCampaignDependencyMetadataReceipt(string RelativePath, string? OriginalAssignment,
    string Prefix, string Suffix, IReadOnlyList<string> OriginalIds);
