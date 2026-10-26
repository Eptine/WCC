namespace Wcc.Core.Scenarios;

public static class ScenarioLeaderSchema
{
    public static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type",
        "id",
        "name",
        "gender",
        "variation",
        "canrecruit",
        "x",
        "y",
        "x,y",
        "facing",
        "unrenamable",
        "random_traits",
        "random_gender",
        "placement",
        "location_id",
        "extra_recruit"
    };

    public static readonly HashSet<string> LegacyInlineKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type",
        "id",
        "name",
        "gender",
        "variation",
        "canrecruit",
        "x",
        "y",
        "x,y",
        "facing",
        "unrenamable",
        "random_traits",
        "random_gender",
        "placement",
        "location_id",
        "extra_recruit"
    };
}
