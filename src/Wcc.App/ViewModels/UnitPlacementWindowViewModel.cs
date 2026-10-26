using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed record UnitPlacementSideChoice(string Key, string Label);
public sealed record UnitPlacementScopeChoice(string? Define, string Label);
public sealed record UnitPlacementUpkeepChoice(string? Value, string Label);
public sealed record UnitPlacementGenderChoice(string? Value, string Label);
public sealed record UnitPlacementResult(UnitPlacementSideChoice Side, WccScenarioUnitInstanceSpec Spec);
public sealed record UnitPlacementPresetChoice(WccCampaignLocalPresetEntry? Entry, string Label);

/// <summary>A detached initial-unit draft. Its caller alone owns the side/source and checked Save recipe.</summary>
public sealed partial class UnitPlacementWindowViewModel : ViewModelBase, IDisposable
{
    private readonly Dictionary<string, string> _values;
    private readonly Func<bool> _isCurrent;
    private readonly Func<UnitPlacementResult, string?>? _validate;
    private readonly IReadOnlyList<CatalogPickerOptionVm> _unitTypes;
    private UnitPlacementSideChoice? _selectedSide;
    private UnitPlacementScopeChoice? _selectedScope;
    private long _version;
    private string? _status;
    private bool _disposed;
    private readonly bool _templateMode;
    private readonly bool _lockUnitId;
    private WccCampaignLocalPresetEntry? _preset;
    private WccCampaignPresetSelectionBinding? _unitPresetBinding;
    private IReadOnlyList<UnitPlacementPresetChoice> _unitPresetChoices = [new(null, "Custom unit")];
    private bool? _loyal;
    private readonly string? _originalGender;
    private readonly string? _originalType;
    private Func<string, Task<IReadOnlyList<string>?>>? _genderResolver;
    private IReadOnlyList<UnitPlacementGenderChoice> _genderChoices = Array.AsReadOnly(new[] { new UnitPlacementGenderChoice(null, "Random / default") });
    private bool _genderKnown;
    private bool _genderLoading;
    private long _genderRequest;
    private Task _genderLoadingTask = Task.CompletedTask;

    public UnitPlacementWindowViewModel(WccInitialUnitEditDraft draft)
        : this(draft.Values, draft.DifficultyDefine, draft.SideChoices, draft.InitialSide, draft.ScopeChoices,
            draft.UnitTypeOptions, () => draft.IsCurrent, draft.Validate)
    {
        _loyal = draft.IsLoyal; _preset = draft.Spec?.Preset;
        UnitPresetBinding = draft.UnitPresetBinding;
        GenderResolver = draft.GenderResolver;
    }
    public UnitPlacementWindowViewModel(WccScenarioUnitInstanceSpec spec,
        IReadOnlyList<UnitPlacementSideChoice> sideChoices, UnitPlacementSideChoice? initialSide,
        IReadOnlyList<UnitPlacementScopeChoice> scopeChoices, IReadOnlyList<CatalogPickerOptionVm> unitTypeOptions,
        Func<bool>? isCurrent = null, Func<UnitPlacementResult, string?>? validate = null)
        : this(spec?.Values ?? throw new ArgumentNullException(nameof(spec)), spec.DifficultyDefine,
            sideChoices, initialSide, scopeChoices, unitTypeOptions, isCurrent, validate) { _loyal = spec.IsLoyal; _preset = spec.Preset; }

    public UnitPlacementWindowViewModel(IReadOnlyDictionary<string, string> values, string? difficultyDefine,
        IReadOnlyList<UnitPlacementSideChoice> sideChoices, UnitPlacementSideChoice? initialSide,
        IReadOnlyList<UnitPlacementScopeChoice> scopeChoices, IReadOnlyList<CatalogPickerOptionVm> unitTypeOptions,
        Func<bool>? isCurrent = null, Func<UnitPlacementResult, string?>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(values); ArgumentNullException.ThrowIfNull(sideChoices);
        ArgumentNullException.ThrowIfNull(scopeChoices); ArgumentNullException.ThrowIfNull(unitTypeOptions);
        _values = new(values, StringComparer.Ordinal); _isCurrent = isCurrent ?? (() => true); _validate = validate;
        _originalGender = Read("gender"); _originalType = Read("type");
        SideChoices = Array.AsReadOnly(sideChoices.ToArray()); ScopeChoices = Array.AsReadOnly(scopeChoices.ToArray());
        _unitTypes = Array.AsReadOnly(unitTypeOptions.ToArray());
        _selectedSide = SideChoices.FirstOrDefault(choice => ReferenceEquals(choice, initialSide));
        _selectedScope = ScopeChoices.FirstOrDefault(choice => string.Equals(choice.Define, difficultyDefine, StringComparison.Ordinal));
        RefreshUpkeepChoices();

    }

    private UnitPlacementWindowViewModel(WccScenarioUnitTemplate? template, IReadOnlyList<CatalogPickerOptionVm> unitTypeOptions,
        bool lockUnitId, Func<bool> isCurrent)
        : this(template?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), null, [], null, [], unitTypeOptions, isCurrent)
    { _templateMode = true; _lockUnitId = lockUnitId; _loyal = template?.IsLoyal; }
    public static UnitPlacementWindowViewModel CreateTemplate(WccScenarioUnitTemplate? template,
        IReadOnlyList<CatalogPickerOptionVm> unitTypeOptions, bool lockUnitId, Func<bool> isCurrent,
        Func<string, Task<IReadOnlyList<string>?>>? genderResolver = null) =>
        new(template, unitTypeOptions, lockUnitId, isCurrent) { GenderResolver = genderResolver };
    public bool IsTemplateMode => _templateMode;
    public bool ShowsPlacement => !_templateMode;
    public bool CanEditUnitId => CanEdit && !_lockUnitId && _preset is null;
    public string WindowTitle => _templateMode ? "WCC Unit Preset" : IsEventSpawn ? "WCC Spawn Unit" : "WCC Initial Unit";
    public string Heading => _templateMode ? "Unit preset" : IsEventSpawn ? "Spawn unit" : "Initial unit";
    public string Introduction => _templateMode
        ? "Reusable named unit settings. The scenario supplies side, coordinates and difficulty when this preset is placed."
        : IsEventSpawn ? "Create a unit when this action runs. Choose its side and location; difficulty comes from its event region."
        : "Create a unit on the map when the scenario begins.";
    public string UnitIdLabel => _templateMode ? "Unit ID" : "Unit ID (optional)";
    public string UnitIdGuidance => _templateMode
        ? "Use a stable named unit ID for dialogue, events and carryover. Saved preset unit IDs stay locked."
        : IsEventSpawn ? "Leave ID empty for a generated unit ID. Set an ID for dialogue or actions that run after this unit is spawned."
        : "Leave ID empty for a generated unit ID. Set a unique ID to refer to this unit in dialogue or events; it must be alive when the event fires.";
    public bool TryCreateTemplate(out WccScenarioUnitTemplate? template, out string? problem)
    {
        template = null;
        problem = !_templateMode ? "This form creates a placed unit, not a reusable template." : LocalProblem;
        if (problem is not null) return Refuse(problem);
        if (!_unitTypes.Any(option => option.ValueId == TypeId))
        { problem = "Choose one unit type available in this campaign."; return Refuse(problem); }
        try { template = new(_values, _loyal); }
        catch (ArgumentException failure) { problem = failure.Message; return Refuse(problem); }
        if (!CanEdit) { template = null; problem = "The preset context changed. Reopen the form."; return Refuse(problem); }
        _status = "Unit preset settings are ready."; RefreshAvailability(); return true;
    }
    public WccCampaignPresetSelectionBinding? UnitPresetBinding
    {
        get => _unitPresetBinding;
        init
        {
            if (_templateMode) return;
            _unitPresetBinding = value;
            _unitPresetChoices = Array.AsReadOnly(new[] { new UnitPlacementPresetChoice(null, "Custom unit") }
                .Concat(value?.Entries.Where(entry => entry.Role == WccCampaignLocalPresetRole.Unit && entry.Payload?.UnitTemplate is not null)
                    .Select(entry => new UnitPlacementPresetChoice(entry, entry.Name + " · " + entry.Scope.DisplayName)) ?? []).ToArray());
            RefreshGenderChoices();
        }
    }
    public IReadOnlyList<UnitPlacementPresetChoice> UnitPresetChoices => _unitPresetChoices;
    public bool ShowsUnitPresets => !_templateMode;
    public bool CanChooseUnitPreset => CanEdit && _unitPresetChoices.Count > 1 && _unitPresetBinding?.IsCurrent == true;
    public string UnitPresetGuidance => _unitPresetBinding is null
        ? IsEventSpawn ? "Custom unit. Saved Unit presets appear here when available in the captured event campaign scope."
            : "This unit uses Custom settings. To choose a saved Unit preset, add a new initial unit from the map after saving the preset."
        : !_unitPresetBinding.IsCurrent
            ? "The campaign or preset catalog changed. Reopen this unit dialog to choose a saved Unit preset."
        : _unitPresetChoices.Count <= 1
            ? "No saved Unit presets are available in this chapter. Create and save one in Campaign presets, then reopen this dialog."
            : IsEventSpawn ? "Choose a saved Unit preset or Custom unit. Set its side and location; the event region supplies difficulty."
            : "Choose a saved Unit preset or Custom unit. Side, location and difficulty apply to this placement.";
    public UnitPlacementPresetChoice SelectedUnitPreset
    {
        get => _unitPresetChoices.FirstOrDefault(choice => ReferenceEquals(choice.Entry, _preset)) ?? _unitPresetChoices[0];
        set { if (value is not null) TrySetUnitPreset(value); }
    }
    public bool TrySetUnitPreset(UnitPlacementPresetChoice choice)
    {
        if (_templateMode || !CanChooseUnitPreset || !_unitPresetChoices.Any(current => ReferenceEquals(current, choice))) return false;
        if (ReferenceEquals(choice.Entry, _preset)) return true;
        if (choice.Entry is not { } entry)
        {
            _preset = null; _values.Clear(); _loyal = null;
            _selectedSide = null; _eventSide = "";
            _selectedScope = IsEventSpawn ? ScopeChoices.FirstOrDefault(choice => choice.Define is null) : null;
            RefreshUpkeepChoices(); Changed(); RefreshGenderChoices(); return true;
        }
        if (_unitPresetBinding?.ContainsCurrent(entry) != true || entry.Payload?.UnitTemplate is not { } template) return false;
        var x = Read("x"); var y = Read("y");
        _values.Clear();
        foreach (var pair in template.Values) _values.Add(pair.Key, pair.Value);
        if (x is not null) _values["x"] = x;
        if (y is not null) _values["y"] = y;
        _loyal = template.IsLoyal; _preset = entry;
        RefreshUpkeepChoices(); Changed(); RefreshGenderChoices(); return true;
    }
    public IReadOnlyList<UnitPlacementSideChoice> SideChoices { get; }
    public IReadOnlyList<UnitPlacementScopeChoice> ScopeChoices { get; }
    public IReadOnlyList<UnitPlacementUpkeepChoice> UpkeepChoices { get; private set; } = [];
    public Func<string, Task<IReadOnlyList<string>?>>? GenderResolver
    {
        get => _genderResolver;
        init { _genderResolver = value; RefreshGenderChoices(); }
    }
    public IReadOnlyList<UnitPlacementGenderChoice> GenderChoices => _genderChoices;
    public bool CanChooseGender => CanEdit && !_genderLoading && (_genderKnown || Read("gender") is not null);
    public string GenderStatus => _genderLoading ? "Reading available genders…"
        : Read("gender") is { } gender && !_genderChoices.Any(choice => choice.Value == gender)
            ? "Existing gender '" + gender + "' is preserved. Choose an available value or Random / default to replace it."
        : TypeId.Length == 0 ? "Choose a unit type to see its available genders."
        : !_genderKnown ? "Available genders could not be resolved. Existing values are preserved."
        : "Leave Random / default to use the unit's normal gender selection.";
    public UnitPlacementGenderChoice? SelectedGender
    {
        get => _genderChoices.FirstOrDefault(choice => choice.Value == Read("gender"));
        set
        {
            if (CanChooseGender && value is not null && _genderChoices.Any(choice => ReferenceEquals(choice, value)))
                Set("gender", value.Value);
        }
    }
    public Task WaitForGenderChoicesAsync() => _genderLoadingTask;
    internal bool HasCurrentContext => _isCurrent() && (_preset is null || _unitPresetBinding?.ContainsCurrent(_preset) == true);
    public bool CanEdit => !_disposed && HasCurrentContext;
    public bool CanApply => LocalProblem is null;
    public bool CanChooseSide => CanEdit && SideChoices.Count > 0 && (_selectedSide is null || SideChoices.Count > 1);
    public bool CanChooseScope => CanEdit && ScopeChoices.Count > 0 && (_selectedScope is null || ScopeChoices.Count > 1);
    public bool CanPickType => CanEdit && _unitTypes.Count > 0;
    public string Status => _status ?? LocalProblem ?? (_templateMode ? "Apply returns these reusable unit settings to the preset wizard." : IsEventSpawn ? "Apply returns this unit to its event action. Apply the event, then Save the scenario to write it." : "Apply queues this initial unit. Save the scenario to write it.");
    public string UnitId { get => Read("id") ?? ""; set { if (CanEditUnitId) Set("id", value); } }
    public string UnitName
    {
        get => Read("name") ?? "";
        set
        {
            if (!CanEdit || UnitName == value) return;
            Set("name", value);
            if (_values.ContainsKey("generate_name")) Set("generate_name", string.IsNullOrEmpty(UnitName) ? "yes" : "no");
        }
    }
    public string TypeId => Read("type") ?? "";
    public string X { get => Read("x") ?? ""; set { if (!_templateMode) Set("x", value); } }
    public string Y { get => Read("y") ?? ""; set { if (!_templateMode) Set("y", value); } }
    public string Hitpoints { get => Read("hitpoints") ?? ""; set => Set("hitpoints", value); }
    public string Experience { get => Read("experience") ?? ""; set => Set("experience", value); }
    public string Moves { get => Read("moves") ?? ""; set => Set("moves", value); }
    public bool? RandomTraits { get => ReadBoolean("random_traits"); set => SetBoolean("random_traits", value); }
    public bool? Unrenamable { get => ReadBoolean("unrenamable"); set => SetBoolean("unrenamable", value); }
    public bool? Loyal
    {
        get => _loyal;
        set { if (CanEdit && value != _loyal) { _loyal = value; Changed(); } }
    }
    public UnitPlacementSideChoice? SelectedSide
    {
        get => _selectedSide;
        set { if (CanEdit && value is not null && SideChoices.Any(choice => ReferenceEquals(choice, value)) &&
                   !ReferenceEquals(value, _selectedSide)) { _selectedSide = value; Changed(); } }
    }
    public UnitPlacementScopeChoice? SelectedScope
    {
        get => _selectedScope;
        set { if (CanEdit && value is not null && ScopeChoices.Any(choice => ReferenceEquals(choice, value)) &&
                   !ReferenceEquals(value, _selectedScope)) { _selectedScope = value; Changed(); } }
    }
    public UnitPlacementUpkeepChoice SelectedUpkeep
    {
        get => UpkeepChoices.First(choice => string.Equals(choice.Value, Read("upkeep"), StringComparison.Ordinal));
        set { if (value is not null && UpkeepChoices.Any(choice => ReferenceEquals(choice, value))) Set("upkeep", value.Value); }
    }

    public UnitPlacementTypePick? BeginTypePick()
    {
        if (!CanPickType) return null;
        return new(this, _version, new(_unitTypes, TypeId, false, "Choose unit type",
            _templateMode ? "Unit types available to this campaign. The preset stores reusable unit settings without placement." : "Core and unit types already available in this campaign. This creates an initial unit instance."));
    }
    public bool TrySetPickedType(UnitPlacementTypePick capture, CatalogPickerSelection? selection)
    {
        if (!CanEdit || !ReferenceEquals(capture.Owner, this) || capture.Version != _version)
        { _status = "The unit or scenario changed while the picker was open. Choose the unit type again."; RefreshAvailability(); return false; }
        var matches = _unitTypes.Where(option => option.ToSelection() == selection).ToArray();
        if (selection is null || matches.Length != 1)
        { _status = "Choose one exact unit type available in this campaign."; RefreshAvailability(); return false; }
        var changed = TypeId != matches[0].ValueId;
        Set("type", matches[0].ValueId);
        if (changed) RefreshGenderChoices();
        return true;
    }
    public bool TryCreateResult(out UnitPlacementResult? result, out string? problem)
    {
        result = null; problem = _templateMode ? "Use the template result for this preset form." : LocalProblem;
        if (problem is not null) return Refuse(problem);
        var version = _version;
        UnitPlacementResult candidate;
        try { candidate = new(IsEventSpawn ? EventSideChoice() : _selectedSide!, new(_values, IsEventSpawn ? null : _selectedScope!.Define) { IsLoyal = _loyal, Preset = _preset }); }
        catch (ArgumentException failure) { problem = failure.Message; return Refuse(problem); }
        problem = _validate?.Invoke(candidate);
        if (problem is null && (!CanEdit || version != _version)) problem = "The unit or scenario changed while checking it. Review the fields and apply again.";
        if (problem is not null) return Refuse(problem);
        result = candidate; _status = IsEventSpawn ? "Spawn unit settings are ready." : "Initial unit is ready to queue."; RefreshAvailability(); return true;
    }
    public void RefreshAvailability()
    {
        foreach (var name in new[] { nameof(CanEdit), nameof(CanEditUnitId), nameof(CanApply), nameof(CanChooseSide), nameof(CanChooseEventSide), nameof(CanPickCoordinates), nameof(CanChooseScope), nameof(CanPickType), nameof(CanChooseGender), nameof(CanChooseUnitPreset), nameof(UnitPresetGuidance), nameof(GenderStatus), nameof(Status) }) OnPropertyChanged(name);
    }
    private string? LocalProblem => !CanEdit ? "The scenario or map context changed. Reopen the unit dialog."
        : PresetProblem is { } presetProblem ? presetProblem
        : IsEventSpawn && EventSideNumber is null ? "Enter one positive whole side number for this spawn action."
        : UsesEventSideChoices && !EventSideChoices.Any(choice => choice.Key == EventSideNumber!.Value.ToString(CultureInfo.InvariantCulture)) ? "Choose one of this scenario's captured sides."
        : !_templateMode && !IsEventSpawn && _selectedSide is null ? "Choose a captured side for this unit."
        : !_templateMode && _selectedScope is null ? "Choose an available difficulty scope."
        : _templateMode && string.IsNullOrWhiteSpace(UnitId) ? "Enter a stable unit ID for this preset."
        : UnitId.Length > 0 && (UnitId != UnitId.Trim() || UnitId.Any(char.IsControl) ||
            WccScenarioFieldValueCodec.InferInputKind(UnitId) != WccScenarioFieldValueInputKind.Literal) ? "Enter a literal unit ID or leave it empty."
        : string.IsNullOrWhiteSpace(TypeId) ? "Choose a unit type."
        : Read("gender") is { } gender && (TypeId != _originalType || gender != _originalGender) &&
          (!_genderKnown || !_genderChoices.Any(choice => choice.Value == gender)) ? "Choose a gender available for this unit type, or Random / default."
        : !_templateMode && (!Nonnegative(X, true) || !Nonnegative(Y, true)) ? IsEventSpawn ? "Enter one positive whole X/Y coordinate pair." : "X and Y must identify one playable map hex."
        : Hitpoints.Length > 0 && !Nonnegative(Hitpoints, true) ? "Starting HP must be a positive integer or empty for the default."
        : Experience.Length > 0 && !Nonnegative(Experience, false) ? "Starting XP must be a nonnegative integer or empty for the default."
        : Moves.Length > 0 && !Nonnegative(Moves, false) ? "Movement left must be a nonnegative integer or empty for the default." : null;
    private string? PresetProblem => _preset?.Payload?.UnitTemplate is not { } template ? null
        : UnitId != template.Values.GetValueOrDefault("id") || _loyal != template.IsLoyal ||
          template.Values.Keys.Any(key => !_values.ContainsKey(key))
            ? "This preset keeps its unit ID, loyalty and inherited fields. Choose Custom unit to remove or replace those settings independently."
            : null;
    private static bool Nonnegative(string value, bool positive) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= (positive ? 1 : 0);
    private void RefreshUpkeepChoices()
    {
        var choices = new List<UnitPlacementUpkeepChoice> { new(null, "Default"), new("full", "Full upkeep"), new("loyal", "Loyal (no upkeep)"), new("free", "Free") };
        if (Read("upkeep") is { } upkeep && choices.All(choice => choice.Value != upkeep))
            choices.Add(new(upkeep, upkeep.Length == 0 ? "Empty value" : upkeep));
        UpkeepChoices = Array.AsReadOnly(choices.ToArray()); OnPropertyChanged(nameof(UpkeepChoices));
    }
    private string? Read(string key) => _values.GetValueOrDefault(key);
    private bool? ReadBoolean(string key) => Read(key) switch { "yes" or "true" => true, "no" or "false" => false, _ => null };
    private void SetBoolean(string key, bool? value) { if (ReadBoolean(key) != value) Set(key, value is null ? null : value.Value ? "yes" : "no"); }
    private void Set(string key, string? value)
    {
        if (!CanEdit) return;
        value = string.IsNullOrEmpty(value) ? null : value;
        if (string.Equals(Read(key), value, StringComparison.Ordinal)) return;
        _version++;
        if (value is null) _values.Remove(key); else _values[key] = value;
        _status = null; NotifyValues(); RefreshAvailability();
    }
    private void Changed() { _version++; _status = null; NotifyValues(); RefreshAvailability(); }
    private void NotifyValues()
    {
        foreach (var name in new[] { nameof(EventSide), nameof(SelectedEventSide), nameof(UnitId), nameof(UnitName), nameof(TypeId), nameof(X), nameof(Y), nameof(Hitpoints), nameof(Experience), nameof(Moves),
                     nameof(RandomTraits), nameof(Unrenamable), nameof(Loyal), nameof(SelectedUnitPreset), nameof(SelectedSide), nameof(SelectedScope), nameof(SelectedUpkeep), nameof(SelectedGender), nameof(GenderStatus) }) OnPropertyChanged(name);
    }
    private void RefreshGenderChoices()
    {
        var request = ++_genderRequest;
        var type = TypeId;
        _genderKnown = false;
        _genderLoading = _genderResolver is not null && type.Length > 0 && CanEdit;
        _genderChoices = Array.AsReadOnly(new[] { new UnitPlacementGenderChoice(null, "Random / default") });
        _genderLoadingTask = _genderLoading ? LoadGenderChoicesAsync(request, type) : Task.CompletedTask;
        OnPropertyChanged(nameof(GenderChoices)); OnPropertyChanged(nameof(SelectedGender)); RefreshAvailability();
    }
    private async Task LoadGenderChoicesAsync(long request, string type)
    {
        await Task.Yield();
        IReadOnlyList<string>? genders;
        try { genders = await _genderResolver!(type); }
        catch (OperationCanceledException) { genders = null; }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { genders = null; }
        // A metadata completion cannot restore a retired form or a previous type.
        if (!CanEdit || request != _genderRequest || TypeId != type) return;
        _genderLoading = false;
        _genderKnown = genders is { Count: > 0 } && genders.All(gender => gender is "male" or "female");
        _genderChoices = Array.AsReadOnly(new[] { new UnitPlacementGenderChoice(null, "Random / default") }
            .Concat(_genderKnown ? genders!.Distinct(StringComparer.Ordinal).Select(gender => new UnitPlacementGenderChoice(gender, gender == "male" ? "Male" : "Female")) : [])
            .ToArray());
        OnPropertyChanged(nameof(GenderChoices)); OnPropertyChanged(nameof(SelectedGender)); RefreshAvailability();
    }
    private bool Refuse(string problem) { _status = problem; RefreshAvailability(); return false; }
    public void Dispose() { if (_disposed) return; _disposed = true; RefreshAvailability(); }
}

public sealed class UnitPlacementTypePick
{
    internal UnitPlacementTypePick(UnitPlacementWindowViewModel owner, long version, CatalogPickerWindowViewModel picker)
    { Owner = owner; Version = version; Picker = picker; }
    internal UnitPlacementWindowViewModel Owner { get; }
    internal long Version { get; }
    public CatalogPickerWindowViewModel Picker { get; }
}
