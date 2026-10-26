using Avalonia.Data.Converters;

namespace Wcc.App.Views.Controls;

/// <summary>Presentation adapters for the existing field-origin and navigation identities.</summary>
public static class WccEditorIconConverters
{
    public static readonly FuncValueConverter<bool, WccActionIcon> CodeAction = new(canEdit =>
        canEdit ? WccActionIcon.EditCode : WccActionIcon.ViewCode);

    // C and S both mean a direct authored value. Their exact campaign/scenario
    // provenance stays in the view model, tooltip and checked source route.
    public static readonly FuncValueConverter<string?, WccFieldStatusIcon> FieldStatus = new(value =>
        (value is { Length: > 0 } ? value[0] : '?') switch
        {
            'C' or 'S' => WccFieldStatusIcon.Value,
            'M' => WccFieldStatusIcon.Macro,
            'O' => WccFieldStatusIcon.Override,
            '+' => WccFieldStatusIcon.Absent,
            _ => WccFieldStatusIcon.Unresolved
        });

    // Section identity, rather than translated labels or the currently selected tab.
    public static readonly FuncValueConverter<int, WccEditorTabIcon> ScenarioTab = new(section => section switch
    {
        0 => WccEditorTabIcon.InitialSettings,
        2 => WccEditorTabIcon.MapEvents,
        _ => WccEditorTabIcon.None
    });
}
