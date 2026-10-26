using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>
/// Basic v0.1 domain model for a [campaign] block in _main.cfg.
/// Keep values as raw WML-ish strings for safe round-tripping.
/// </summary>
public sealed class CampaignDefinition
{
    public string? Id { get; set; }
    public string? NameWml { get; set; }
    public string? AbbrevWml { get; set; }
    public string? Define { get; set; }
    public string? FirstScenario { get; set; }

    public string? Icon { get; set; }
    public string? Image { get; set; }
    public string? Background { get; set; }
    public string? DescriptionWml { get; set; }

    public int? Rank { get; set; }
    public string? Year { get; set; }

    public Dictionary<string, WmlFieldSafetyState> FieldStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}