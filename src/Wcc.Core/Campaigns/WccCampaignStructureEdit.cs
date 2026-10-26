using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public enum WccCampaignPathKind { CfgInclude, UnitsInclude, BinaryRoot }

public sealed record WccCampaignDifficultyPresentation(string Define, string Label,
    string Description, string Image, bool IsDefault);

/// <summary>A reference relative to the current add-on root; this never moves a directory.</summary>
public sealed record WccCampaignPathReference(WccCampaignPathKind Kind, string Path, string? Key = null);

/// <summary>Display inventory and bounded source-shape evidence, never persistence authority.</summary>
public sealed class WccCampaignStructureSnapshot
{
    internal WccCampaignStructureSnapshot(WmlStaticSource source, WccCampaignAuthoringMetadata? authoring,
        IEnumerable<WccCampaignDifficultyPresentation> difficulties, IEnumerable<WccCampaignPathReference> paths,
        bool canEdit, string diagnostic, WccCampaignStructureLayout? layout)
    {
        Source = source;
        Authoring = authoring is null ? null : new WccCampaignAuthoringMetadata
        {
            LayoutVersion = authoring.LayoutVersion, AddonId = authoring.AddonId,
            CampaignDefine = authoring.CampaignDefine, MainRelativePath = authoring.MainRelativePath,
            FixedDifficultyDefines = authoring.FixedDifficultyDefines?.ToList() ?? [],
            UsesExplicitDifficultyValues = authoring.UsesExplicitDifficultyValues
        };
        DifficultyRows = Array.AsReadOnly(difficulties.ToArray());
        PathRows = Array.AsReadOnly(paths.ToArray());
        CanEdit = canEdit; Diagnostic = diagnostic; Layout = layout;
    }
    public WmlStaticSource Source { get; }
    public IReadOnlyList<WccCampaignDifficultyPresentation> DifficultyRows { get; }
    public IReadOnlyList<WccCampaignPathReference> PathRows { get; }
    public bool CanEdit { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal WccCampaignAuthoringMetadata? Authoring { get; }
    internal WccCampaignStructureLayout? Layout { get; }
}

/// <summary>Immutable typed intent; Save must reconstruct its source and evaluated topology.</summary>
public sealed class WccCampaignStructureEdit
{
    internal WccCampaignStructureEdit(WccCampaignStructureSnapshot snapshot,
        IEnumerable<WccCampaignDifficultyPresentation> difficulties, IEnumerable<WccCampaignPathReference> paths)
    {
        Snapshot = snapshot; DifficultyRows = Array.AsReadOnly(difficulties.ToArray());
        PathRows = Array.AsReadOnly(paths.ToArray());
    }
    public WccCampaignStructureSnapshot Snapshot { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public IReadOnlyList<WccCampaignDifficultyPresentation> DifficultyRows { get; }
    public IReadOnlyList<WccCampaignPathReference> PathRows { get; }
    public bool AuthorizesPersistence => false;
}

internal sealed class WccCampaignStructureNode(string tag, int start, WccCampaignStructureNode? parent)
{
    internal string Tag { get; } = tag;
    internal int Start { get; } = start;
    internal int End { get; set; }
    internal int Close { get; set; }
    internal WccCampaignStructureNode? Parent { get; } = parent;
    internal List<WccCampaignStructureNode> Children { get; } = [];
}
internal sealed record WccCampaignStructurePath(WccCampaignPathReference Row, WmlStaticSourceRange Range);
internal sealed record WccCampaignStructureLayout(WccCampaignStructureNode Campaign,
    IReadOnlyList<WccCampaignStructureNode> Difficulties, IReadOnlyList<WccCampaignStructurePath> Paths,
    int GeneralInsertion, int UnitsInsertion, int BinaryInsertion);
internal sealed record WccCampaignStructurePatch(WmlStaticSourceRange Range, string Replacement, string Kind);
