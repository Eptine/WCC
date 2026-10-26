namespace Wcc.Core.Units;

public static class UnitAnimationTagCatalog
{
    public static readonly IReadOnlyList<string> DirectChildTagNames = new[]
    {
        "animation",
        "attack_anim",
        "defend",
        "death",
        "victory_anim",
        "extra_anim",
        "teleport_anim",
        "standing_anim",
        "idle_anim",
        "levelin_anim",
        "levelout_anim",
        "healing_anim",
        "healed_anim",
        "poison_anim",
        "movement_anim",
        "pre_movement_anim",
        "post_movement_anim",
        "draw_weapon_anim",
        "sheath_weapon_anim",
        "leading_anim",
        "recruit_anim",
        "recruiting_anim"
    };

    private static readonly HashSet<string> Lookup =
        DirectChildTagNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsAnimationTag(string? tagName)
        => !string.IsNullOrWhiteSpace(tagName) && Lookup.Contains(tagName);
}