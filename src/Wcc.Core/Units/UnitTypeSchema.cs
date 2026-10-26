namespace Wcc.Core.Units;

public static class UnitTypeSchema
{
    public static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "name",
        "race",
        "image",
        "profile",
        "small_profile",
        "gender",
        "hitpoints",
        "movement_type",
        "movement",
        "experience",
        "level",
        "alignment",
        "advances_to",
        "cost",
        "usage",
        "description",
        "abilities_list"
    };
}
