namespace Wcc.Core.Campaigns;

public sealed class CampaignIncludeReference
{
    public required CampaignIncludeKind Kind { get; init; }

    /// <summary>
    /// Raw include macro inner text, e.g.:
    /// campaigns/Two_Brothers/scenarios
    /// ~add-ons/To_Lands_Unknown/units/
    /// ./scenarios
    /// </summary>
    public required string RawIncludePath { get; init; }

    /// <summary>
    /// Resolved filesystem file or directory, when the include belongs to the current campaign folder.
    /// </summary>
    public string? ResolvedDirectoryPath { get; init; }

    /// <summary>Literal top-level [units] context, rather than a directory-name hint. Discovery only.</summary>
    public bool IsUnitContainerInclude { get; init; }
}