using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>
/// Basic v0.1 model for the optional [textdomain] block typically found in _main.cfg.
/// </summary>
public sealed class CampaignTextDomain
{
    public string? Name { get; set; }
    public string? Path { get; set; }

    public Dictionary<string, WmlFieldSafetyState> FieldStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}