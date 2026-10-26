using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

// Detached shared Unit form. Only its enclosing Event recipe can stage the result.
internal static class EventSpawnUnitEditor
{
    internal static UnitPlacementWindowViewModel? Create(WccScenarioEventAction action,
        IReadOnlyList<CatalogPickerOptionVm> units, WccCampaignPresetSelectionBinding? presets,
        Func<bool> current, Func<CatalogPickerOptionVm, Task<IReadOnlyList<string>?>>? resolveGenders)
    {
        bool Current() => current() && (action.Unit?.Preset is not { } preset || presets?.ContainsCurrent(preset) == true);
        if (action.Kind != WccScenarioEventActionKind.SpawnUnit || !Current()) return null;
        string? Validate(UnitPlacementResult result)
        {
            if (!Current()) return "The event or unit context changed. Reopen the unit form.";
            if (!int.TryParse(result.Side.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var side) || side < 1)
                return "Enter a positive side number for the spawned unit.";
            if (result.Spec.DifficultyDefine is not null) return "Use an Event difficulty region to limit this action.";
            if (!units.Any(option => option.ValueId == result.Spec.Values.GetValueOrDefault("type")))
                return "Choose a unit type available to this campaign.";
            if (result.Spec.Preset is { } preset && presets?.ContainsCurrent(preset) != true)
                return "The selected Unit preset changed. Reopen the unit form.";
            return null;
        }
        return UnitPlacementWindowViewModel.CreateEventSpawn(action.Unit, action.Side, units, Current, Validate, presets,
            async type =>
            {
                var matches = units.Where(option => option.ValueId == type).Take(2).ToArray();
                if (!Current() || matches.Length != 1 || resolveGenders is null) return null;
                var genders = await resolveGenders(matches[0]);
                return Current() ? genders : null;
            });
    }
}