namespace Wcc.Core.Scenarios;

using Wcc.Core.Wml;

/// <summary>
/// Unit-like data authored either directly on a legacy [side] or in a literal
/// [side][leader] child. Arbitrary unit children remain authoritative raw WML.
/// </summary>
public sealed class ScenarioLeader
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? NameWml { get; set; }
    public string? Gender { get; set; }
    public string? Variation { get; set; }
    public string? CanRecruit { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool UsesCombinedCoordinates { get; set; }
    public bool CoordinatesAreAmbiguous { get; set; }
    public string? Facing { get; set; }
    public string? Unrenamable { get; set; }
    public string? RandomTraits { get; set; }
    public string? RandomGender { get; set; }
    public string? Placement { get; set; }
    public string? LocationId { get; set; }
    public string? ExtraRecruit { get; set; }

    public Dictionary<string, WmlFieldSafetyState> FieldStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A null typed value normally means "leave absent source alone". UI code
    /// records an explicit clear here when an existing plain key must be removed.
    /// </summary>
    public HashSet<string> FieldsToRemove { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasAnyIdentityOrPlacement =>
        !string.IsNullOrWhiteSpace(Type) ||
        !string.IsNullOrWhiteSpace(Id) ||
        !string.IsNullOrWhiteSpace(NameWml) ||
        !string.IsNullOrWhiteSpace(CanRecruit) ||
        X is not null ||
        Y is not null;
}
