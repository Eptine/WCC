namespace Wcc.Core.Units;

public static class AttackSchema
{
    public static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "description",
        "icon",
        "type",
        "range",
        "alignment",
        "min_range",
        "max_range",
        "damage",
        "number",
        "specials_list"
    };
}
