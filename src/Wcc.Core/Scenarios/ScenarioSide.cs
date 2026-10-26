namespace Wcc.Core.Scenarios;

using Wcc.Core.Wml;

/// <summary>
/// Basic v0.1 domain model for a [side].
/// </summary>
public sealed class ScenarioSide
{
    private ScenarioLeader? _legacyLeader;

    public int? SideNumber { get; set; }
    public int? Gold { get; set; }
    public int? Income { get; set; }
    public int? VillageGold { get; set; }
    public int? VillageSupport { get; set; }
    public int? RecallCost { get; set; }

    public string? Controller { get; set; }
    public string? TeamName { get; set; }
    public string? UserTeamName { get; set; }
    public string? SideNameWml { get; set; }
    public string? Color { get; set; }
    public string? Recruit { get; set; }
    public string? Persistent { get; set; }
    public string? SaveId { get; set; }
    public string? PreviousSaveId { get; set; }
    public string? Fog { get; set; }
    public string? Shroud { get; set; }
    public string? ShareVision { get; set; }
    public string? Hidden { get; set; }
    public string? ScrollToLeader { get; set; }
    public string? SuppressEndTurnConfirmation { get; set; }
    public string? DefeatCondition { get; set; }
    public string? NoLeader { get; set; }
    public string? RecruitmentPattern { get; set; }

    /// <summary>
    /// Direct unit-like fields on [side]. This source form remains valid and is
    /// retained separately from literal nested [leader] blocks.
    /// </summary>
    public ScenarioLeader? LegacyLeader => _legacyLeader;

    /// <summary>Literal direct [leader] children in authored order.</summary>
    public List<ScenarioLeader> NestedLeaders { get; } = new();

    // Backwards-compatible accessors used by the existing flat side editor.
    public int? X { get => _legacyLeader?.X; set => SetLegacy(value, (l, v) => l.X = v); }
    public int? Y { get => _legacyLeader?.Y; set => SetLegacy(value, (l, v) => l.Y = v); }
    public string? CanRecruit { get => _legacyLeader?.CanRecruit; set => SetLegacy(value, (l, v) => l.CanRecruit = v); }
    public string? Type { get => _legacyLeader?.Type; set => SetLegacy(value, (l, v) => l.Type = v); }
    public string? Id { get => _legacyLeader?.Id; set => SetLegacy(value, (l, v) => l.Id = v); }
    public string? NameWml { get => _legacyLeader?.NameWml; set => SetLegacy(value, (l, v) => l.NameWml = v); }

    public Dictionary<string, WmlFieldSafetyState> FieldStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FieldsToRemove { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal ScenarioLeader EnsureLegacyLeader()
        => _legacyLeader ??= new ScenarioLeader();

    private void SetLegacy<T>(T? value, Action<ScenarioLeader, T?> setter)
    {
        if (value is null && _legacyLeader is null)
            return;
        setter(EnsureLegacyLeader(), value);
    }
}
