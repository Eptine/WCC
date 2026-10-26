namespace Wcc.Core.Scenarios;

public static class ScenarioSchema
{
    public static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "next_scenario",
        "map_file",
        "map_data",
        "turns",
        "turn_at",
        "random_start_time",
        "music",
        "victory_when_enemies_defeated"
    };
}