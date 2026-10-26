using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>
/// WCC domain model for a Wesnoth [unit_type].
/// For v0.1 we keep it intentionally small (basic fields only).
/// </summary>
public sealed class UnitType
{
    public string? Id { get; set; }
    public string? NameWml { get; set; }
    public string? Race { get; set; }
    public string? Image { get; set; }
    public string? Profile { get; set; }
    public string? SmallProfile { get; set; }
    public string? Gender { get; set; }

    public int? Hitpoints { get; set; }
    public string? MovementType { get; set; }
    public int? Movement { get; set; }
    public int? Experience { get; set; }
    public int? Level { get; set; }

    public string? Alignment { get; set; }
    public string? AdvancesTo { get; set; }
    public int? Cost { get; set; }
    public string? Usage { get; set; }

    public string? DescriptionWml { get; set; }

    /// <summary>
    /// Direct [base_unit] declarations from the source WML. Wesnoth normally expects one,
    /// but keeping every declaration lets WCC preserve and diagnose conditional or malformed
    /// source without silently choosing an inheritance branch.
    /// </summary>
    public List<UnitBaseReference> BaseUnits { get; } = new();

    public string? SingleBaseUnitId
        => BaseUnits.Count == 1 ? BaseUnits[0].Id : null;

    public Dictionary<string, WmlFieldSafetyState> FieldStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<Attack> Attacks { get; } = new();
    public List<string> AbilityIds { get; } = new();
    public List<string> AbilityMacros { get; } = new();
    public List<WesnothInlineAbilitySpecialDefinition> InlineAbilityDefinitions { get; } = new();
}

public sealed class UnitBaseReference
{
    public required WmlLineBlockSpan Span { get; init; }
    public required WmlFieldSafetyState IdState { get; init; }
    public string? Id { get; init; }
}
