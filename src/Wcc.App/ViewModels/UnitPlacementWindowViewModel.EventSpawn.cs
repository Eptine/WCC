using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Wcc.Core.Scenarios;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public sealed partial class UnitPlacementWindowViewModel
{
    private string _eventSide = "";
    private IReadOnlyList<UnitPlacementSideChoice>? _eventSideChoices;
    public bool UsesEventSideChoices => IsEventSpawn && _eventSideChoices is not null;
    public bool ShowsEventSideInput => IsEventSpawn && !UsesEventSideChoices;
    public IReadOnlyList<UnitPlacementSideChoice> EventSideChoices => _eventSideChoices ?? [];
    public bool CanChooseEventSide => CanEdit && UsesEventSideChoices && EventSideChoices.Count > 0;
    public UnitPlacementSideChoice? SelectedEventSide
    {
        get => EventSideNumber is { } number ? EventSideChoices.FirstOrDefault(choice => choice.Key == number.ToString(CultureInfo.InvariantCulture)) : null;
        set { if (CanChooseEventSide && value is not null && EventSideChoices.Any(choice => ReferenceEquals(choice, value))) EventSide = value.Key; }
    }
    internal void ConfigureEventSides(IReadOnlyList<UnitPlacementSideChoice> sides)
    {
        ArgumentNullException.ThrowIfNull(sides);
        if (!IsEventSpawn || !CanEdit) return;
        if (sides.Any(choice => !int.TryParse(choice.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1) ||
            sides.Select(choice => choice.Key).Distinct(StringComparer.Ordinal).Count() != sides.Count)
            throw new ArgumentException("Event sides must be exact distinct positive scenario side numbers.", nameof(sides));
        _eventSideChoices = Array.AsReadOnly(sides.ToArray());
        Changed();
        foreach (var name in new[] { nameof(UsesEventSideChoices), nameof(ShowsEventSideInput), nameof(EventSideChoices), nameof(SelectedEventSide) }) OnPropertyChanged(name);
    }
    public bool IsEventSpawn { get; private init; }
    public bool ShowsSidePicker => ShowsPlacement && !IsEventSpawn;
    public bool ShowsDifficultyScope => ShowsPlacement && !IsEventSpawn;
    public string EventSide
    {
        get => _eventSide;
        set { if (CanEdit && IsEventSpawn && SetField(ref _eventSide, value ?? "")) Changed(); }
    }
    private int? EventSideNumber => int.TryParse(EventSide, NumberStyles.None, CultureInfo.InvariantCulture, out var side) && side > 0 ? side : null;
    private UnitPlacementSideChoice EventSideChoice() => new(EventSideNumber!.Value.ToString(CultureInfo.InvariantCulture), "Side " + EventSideNumber.Value);
    public static UnitPlacementWindowViewModel CreateEventSpawn(WccScenarioUnitInstanceSpec? unit, int? side,
        IReadOnlyList<CatalogPickerOptionVm> unitTypeOptions, Func<bool> isCurrent,
        Func<UnitPlacementResult, string?>? validate = null, WccCampaignPresetSelectionBinding? unitPresetBinding = null,
        Func<string, Task<IReadOnlyList<string>?>>? genderResolver = null)
    {
        if (unit?.DifficultyDefine is not null) throw new ArgumentException("An event unit uses its event difficulty region.", nameof(unit));
        return new(unit?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), null, [], null,
            [new(null, "Event difficulty region")], unitTypeOptions, isCurrent, validate)
        {
            IsEventSpawn = true, _eventSide = side?.ToString(CultureInfo.InvariantCulture) ?? "",
            _loyal = unit is null ? false : unit.IsLoyal, _preset = unit?.Preset,
            UnitPresetBinding = unitPresetBinding, GenderResolver = genderResolver
        };
    }
}