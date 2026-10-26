namespace Wcc.Core.Campaigns;

public static class CampaignSchema
{
    public static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "name",
        "abbrev",
        "define",
        "first_scenario",
        "icon",
        "image",
        "background",
        "description",
        "rank",
        "year"
    };
}