namespace Wcc.Core.Scenarios;

public static class ScenarioSideSchema
{
    public static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "side",
        "gold",
        "income",
        "village_gold",
        "village_support",
        "recall_cost",
        "controller",
        "team_name",
        "user_team_name",
        "side_name",
        "color",
        "recruit",
        "persistent",
        "save_id",
        "previous_save_id",
        "fog",
        "shroud",
        "share_vision",
        "hidden",
        "scroll_to_leader",
        "suppress_end_turn_confirmation",
        "defeat_condition",
        "no_leader",
        "recruitment_pattern",
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
