using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public static class UnitTypePatcher
{
    public static void Apply(UnitType unit, WmlEditableBlock block)
        => Apply(unit, block, null);

    internal static void Apply(UnitType unit, WmlEditableBlock block, IReadOnlySet<string>? withheldStrings)
    {
        Set(block, unit.FieldStates, "id", unit.Id);
        if (withheldStrings?.Contains("name") != true) Set(block, unit.FieldStates, "name", unit.NameWml);
        Set(block, unit.FieldStates, "race", unit.Race);
        Set(block, unit.FieldStates, "image", unit.Image);
        Set(block, unit.FieldStates, "profile", unit.Profile);
        Set(block, unit.FieldStates, "small_profile", unit.SmallProfile);
        Set(block, unit.FieldStates, "gender", unit.Gender);

        SetInt(block, unit.FieldStates, "hitpoints", unit.Hitpoints);
        Set(block, unit.FieldStates, "movement_type", unit.MovementType);
        SetInt(block, unit.FieldStates, "movement", unit.Movement);
        SetInt(block, unit.FieldStates, "experience", unit.Experience);
        SetInt(block, unit.FieldStates, "level", unit.Level);

        Set(block, unit.FieldStates, "alignment", unit.Alignment);
        Set(block, unit.FieldStates, "advances_to", unit.AdvancesTo);
        SetInt(block, unit.FieldStates, "cost", unit.Cost);
        Set(block, unit.FieldStates, "usage", unit.Usage);

        if (withheldStrings?.Contains("description") != true) Set(block, unit.FieldStates, "description", unit.DescriptionWml);
        WesnothRegistryIdList.Apply(block, unit.FieldStates, "abilities_list", unit.AbilityIds);

        // Container macros stay as-is for now; container-level safety is a later pass.
        WmlContainerMacroEditor.SetTopLevelMacros(block, "abilities", unit.AbilityMacros);
    }

    private static void Set(WmlEditableBlock b, IReadOnlyDictionary<string, WmlFieldSafetyState> states, string key, string? value)
    {
        if (value is null) return;
        if (!WmlFieldSafetyWriteGuard.CanWrite(states, key)) return;
        if (key is "name" or "description" or "race" or "movement_type" or "alignment" or "advances_to") WmlBoundedLiteralAssignment.SetValue(b, key, value);
        else b.SetKnownValue(key, value);
    }

    private static void SetInt(WmlEditableBlock b, IReadOnlyDictionary<string, WmlFieldSafetyState> states, string key, int? value)
    {
        if (value is null) return;
        if (!WmlFieldSafetyWriteGuard.CanWrite(states, key)) return;
        b.SetKnownValue(key, value.Value.ToString(CultureInfo.InvariantCulture));
    }
}
