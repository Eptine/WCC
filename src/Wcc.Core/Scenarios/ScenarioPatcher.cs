using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static class ScenarioPatcher
{
    public static void Apply(Scenario scenario, WmlEditableBlock block)
    {
        Set(block, scenario.FieldStates, "id", scenario.Id);
        Set(block, scenario.FieldStates, "next_scenario", scenario.NextScenario);
        Set(block, scenario.FieldStates, "map_file", scenario.MapFile);
        Set(block, scenario.FieldStates, "map_data", scenario.MapData);

        SetInt(block, scenario.FieldStates, "turns", scenario.Turns);
        SetInt(block, scenario.FieldStates, "turn_at", scenario.TurnAt);

        Set(block, scenario.FieldStates, "random_start_time", scenario.RandomStartTime);
        Set(block, scenario.FieldStates, "music", scenario.Music);
        Set(block, scenario.FieldStates, "victory_when_enemies_defeated", scenario.VictoryWhenEnemiesDefeated);
    }

    private static void Set(WmlEditableBlock b, IReadOnlyDictionary<string, WmlFieldSafetyState> states, string key, string? value)
    {
        if (value is null) return;
        if (!WmlFieldSafetyWriteGuard.CanWrite(states, key)) return;
        b.SetKnownValue(key, value);
    }

    private static void SetInt(WmlEditableBlock b, IReadOnlyDictionary<string, WmlFieldSafetyState> states, string key, int? value)
    {
        if (value is null) return;
        if (!WmlFieldSafetyWriteGuard.CanWrite(states, key)) return;
        b.SetKnownValue(key, value.Value.ToString(CultureInfo.InvariantCulture));
    }
}