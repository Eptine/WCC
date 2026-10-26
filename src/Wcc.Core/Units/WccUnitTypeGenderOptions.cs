using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>Available runtime genders from a captured, base-unit-resolved definition.</summary>
public static class WccUnitTypeGenderOptions
{
    // Null means unresolved; it must not be presented as a fabricated male default.
    public static IReadOnlyList<string>? Read(WccUnitTypeEffectiveDisplay? display)
    {
        if (display is not { HasResolvedInheritance: true, Root.IsStructureKnown: true }) return null;
        if (!display.Root.Fields.TryGetValue("gender", out var field)) return Array.AsReadOnly(new[] { "male" });
        if (!field.IsKnown || !WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var value, out _)) return null;
        if (string.IsNullOrWhiteSpace(value)) return Array.AsReadOnly(new[] { "male" });
        var genders = value.Split(',').Select(gender => gender.Trim()).ToArray();
        if (genders.Any(gender => gender is not ("male" or "female"))) return null;
        return Array.AsReadOnly(genders.Distinct(StringComparer.Ordinal).ToArray());
    }
}
