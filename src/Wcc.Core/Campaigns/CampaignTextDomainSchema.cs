namespace Wcc.Core.Campaigns;

public static class CampaignTextDomainSchema
{
    public static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "path"
    };
}