namespace Wcc.Core.Scenarios;

using Wcc.Core.Wml;

/// <summary>
/// Basic v0.1 domain model for a [scenario].
/// </summary>
public sealed class Scenario
{
    public string? Id { get; set; }
    public string? NextScenario { get; set; }
    public string? MapFile { get; set; }
    public string? MapData { get; set; }

    public int? Turns { get; set; }
    public int? TurnAt { get; set; }

    /// <summary>
    /// Keep raw WML-ish text for now: yes / no / "2,3,5,6"
    /// </summary>
    public string? RandomStartTime { get; set; }

    public string? Music { get; set; }

    /// <summary>
    /// Stored as raw WML-ish text ("yes"/"no") for now to keep patching simple.
    /// </summary>
    public string? VictoryWhenEnemiesDefeated { get; set; }

    public Dictionary<string, WmlFieldSafetyState> FieldStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<ScenarioSide> Sides { get; } = new();
    public List<ScenarioStory> Stories { get; } = new();
}