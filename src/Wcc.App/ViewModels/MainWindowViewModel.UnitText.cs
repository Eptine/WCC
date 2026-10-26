using System;
using System.Runtime.CompilerServices;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel
{
    // Display plain literals, while the model/backing fields retain exact authored WML.
    private static string? UnitLiteralText(string? raw)
        => WccWmlPlainTextCodec.TryReadLiteral(raw, out var text, out _) ? text : raw;

    private void SetUnitLiteral(ref string? direct, string? value, string key, bool translatedText, bool editable,
        [CallerMemberName] string? property = null)
    {
        if (IsSynchronizingUi)
        {
            SetRootDisplayValue(ref direct, value, key, property);
            return;
        }
        if (!editable) return;
        var previous = RootDisplayValue(direct, key);
        if (UnitLiteralText(previous) == value) return;
        if (!TryEncodeUnitLiteral(value ?? "", translatedText, previous, out var encoded)) return;
        SetRootDisplayValue(ref direct, encoded, key, property);
    }

    private void SetVisualName(ref string? direct, string? value, [CallerMemberName] string? property = null)
    {
        if (IsSynchronizingUi)
        {
            SetVisualDisplayValue(ref direct, value, "name", property);
            return;
        }
        if (!VisualNameIsEditable) return;
        var previous = VisualDisplayValue(direct, "name");
        if (UnitLiteralText(previous) == value) return;
        if (!TryEncodeUnitLiteral(value ?? "", true, previous, out var encoded)) return;
        SetVisualDisplayValue(ref direct, encoded, "name", property);
    }

    private bool TryEncodeUnitLiteral(string value, bool translatedText, string? previous, out string encoded)
    {
        encoded = value;
        var translate = translatedText;
        if (previous is not null)
        {
            if (!WccWmlPlainTextCodec.TryReadLiteral(previous, out _, out var wasTranslated))
            {
                Status = "This value contains custom WML. Preserve it or edit its RAW source.";
                return false;
            }
            translate = translatedText && wasTranslated;
        }
        if (!WccWmlPlainTextCodec.TryEncodeInput(value, translate, previous, out encoded, out var problem))
        {
            Status = problem;
            return false;
        }
        if (!translatedText && (!WccWmlPlainTextCodec.TryReadLiteral(encoded, out var plain, out var translated)
            || translated || plain.IndexOfAny(['\r', '\n']) >= 0))
        {
            Status = "Enter one untranslated scalar value. Preserve expressions in RAW source.";
            return false;
        }
        return true;
    }
}
