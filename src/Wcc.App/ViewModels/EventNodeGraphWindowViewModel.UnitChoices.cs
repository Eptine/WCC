using System;
using System.Collections.Generic;
using System.Linq;

namespace Wcc.App.ViewModels;

public sealed partial class EventNodeGraphWindowViewModel
{
    private readonly IReadOnlyList<string> _hostUnitChoices;
    private void RefreshUnitChoices()
    {
        // Suggestions include unfinished nodes; they do not assert that a unit
        // exists when an action executes or change any entered speaker/ID.
        // Shared Unit capture already decodes WML into these semantic values.
        // Decode again here would alter IDs containing literal quote characters.
        var choices = _hostUnitChoices.Concat(_nodes.Where(node => node.IsSpawnUnit && node.Unit is not null)
            .Select(node => node.Unit!.Values.GetValueOrDefault("id")
                ?? node.Unit.Preset?.Payload?.UnitTemplate?.Values.GetValueOrDefault("id")))
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != "narrator")
            .Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (UnitChoices.SequenceEqual(choices, StringComparer.Ordinal)) return;
        UnitChoices = Array.AsReadOnly(choices);
        SpeakerChoices = Array.AsReadOnly(new[] { "narrator" }.Concat(choices).ToArray());
        OnPropertyChanged(nameof(UnitChoices)); OnPropertyChanged(nameof(SpeakerChoices));
    }
}
