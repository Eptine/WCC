using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>
/// Domain model for a Wesnoth [attack] block. v0.1: keep it basic.
/// </summary>
public sealed class Attack
{
    public string? Name { get; set; }
    public string? DescriptionWml { get; set; }
    public string? Icon { get; set; }
    public string? Type { get; set; }
    public string? Range { get; set; }
    public string? Alignment { get; set; }
    public int? MinRange { get; set; }
    public int? MaxRange { get; set; }
    public int? Damage { get; set; }
    public int? Number { get; set; }

    public Dictionary<string, WmlFieldSafetyState> FieldStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> SpecialIds { get; } = new();
    public List<string> SpecialsMacros { get; } = new();
    public List<WesnothInlineAbilitySpecialDefinition> InlineSpecialDefinitions { get; } = new();
}
