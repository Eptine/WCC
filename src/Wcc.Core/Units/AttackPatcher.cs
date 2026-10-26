using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public static class AttackPatcher
{
    public static void Apply(Attack a, WmlEditableBlock b)
        => Apply(a, b, deferDescription: false);

    internal static void Apply(Attack a, WmlEditableBlock b, bool deferDescription)
    {
        Set(b, a.FieldStates, "name", a.Name);
        if (!deferDescription) SetDescription(b, a.FieldStates, a.DescriptionWml);
        Set(b, a.FieldStates, "icon", a.Icon);
        Set(b, a.FieldStates, "type", a.Type);
        Set(b, a.FieldStates, "range", a.Range);
        Set(b, a.FieldStates, "alignment", a.Alignment);
        SetInt(b, a.FieldStates, "min_range", a.MinRange);
        SetInt(b, a.FieldStates, "max_range", a.MaxRange);
        SetInt(b, a.FieldStates, "damage", a.Damage);
        SetInt(b, a.FieldStates, "number", a.Number);
        WesnothRegistryIdList.Apply(b, a.FieldStates, "specials_list", a.SpecialIds);

        // Container macros stay as-is for now; container-level safety is a later pass.
        WmlContainerMacroEditor.SetTopLevelMacros(b, "specials", a.SpecialsMacros);
    }

    private static void SetDescription(WmlEditableBlock block, IReadOnlyDictionary<string, WmlFieldSafetyState> states, string? value)
    {
        if (value is null || !WmlFieldSafetyWriteGuard.CanWrite(states, "description")) return;
        WmlBoundedLiteralAssignment.SetValue(block, "description", value);
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
