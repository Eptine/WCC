using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wcc.Core.Scenarios;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public sealed partial class CampaignPresetCreationForm
{
    private readonly WccCampaignLocalPresetRole _contentRole;
    private readonly Func<bool> _contentCurrent = () => false;
    private readonly Func<WccScenarioUnitTemplate?, bool, UnitPlacementWindowViewModel>? _unitEditorFactory;
    private readonly IReadOnlyList<string> _eventSpeakerIds = [];
    private WccScenarioUnitTemplate? _unitTemplate;
    private string? _eventText;
    private string? _savedUnitId;
    private string? _savedEventId;
    private bool _contentClosed;
    private long _eventScopeVersion;

    internal CampaignPresetCreationForm(WccCampaignLocalPresetRole role, Guid id, string path,
        Func<string, WccCampaignLocalPresetPayload, bool> stage, Func<bool> current,
        Func<WccScenarioUnitTemplate?, bool, UnitPlacementWindowViewModel> unitEditorFactory, IReadOnlyList<string> speakerIds)
    {
        if (role is not (WccCampaignLocalPresetRole.Unit or WccCampaignLocalPresetRole.Event))
            throw new ArgumentException("Use the scalar preset form for sides and leaders.", nameof(role));
        _contentRole = role; Id = id; _sourcePath = path; _stage = stage; _contentCurrent = current;
        _unitEditorFactory = unitEditorFactory; _eventSpeakerIds = Array.AsReadOnly(speakerIds.ToArray());
        PropertyChanged += (_, change) => { if (change.PropertyName == nameof(SelectedScope)) _eventScopeVersion++; };
        _name = role == WccCampaignLocalPresetRole.Unit ? "CHARACTER_Character" : "";
    }
    internal Func<ScenarioMapPreviewVm?>? EventMapProvider { get; init; }
    internal Func<IReadOnlyList<string>>? EventDeclaredDifficultyProvider { get; init; }
    internal IReadOnlyList<string> EventDeclaredDifficultyDefines => EventDeclaredDifficultyProvider?.Invoke() ?? [];
    internal Func<WccCampaignPresetSelectionBinding?>? EventUnitPresetProvider { get; init; }
    internal Func<WccScenarioEventAction, WccCampaignPresetSelectionBinding?, Func<bool>, UnitPlacementWindowViewModel?>? EventUnitEditorFactory { get; init; }
    internal IReadOnlyList<WccCampaignLocalPresetEntry> EventUnitPresets => EventUnitPresetProvider?.Invoke()?.Entries ?? [];
    public bool IsUnitPreset => Role == WccCampaignLocalPresetRole.Unit;
    public bool IsEventPreset => Role == WccCampaignLocalPresetRole.Event;
    public bool CanConfigureContent => !_contentClosed && _contentCurrent();
    public WccScenarioUnitTemplate? UnitTemplate => _unitTemplate;
    public string? EventText => _eventText;
    public string ContentSummary => IsUnitPreset
        ? _unitTemplate is { } unit ? unit.Values.GetValueOrDefault("id") + " · " + unit.Values.GetValueOrDefault("type")
            + (unit.Values.GetValueOrDefault("name") is { Length: > 0 } name ? " · " + name : "") : "Configure this reusable unit."
        : _eventText is { } text && WccScenarioEventGraphEditor.Capture(text, EventDeclaredDifficultyDefines, EventUnitPresets).Seed is { } seed
            ? seed.Graph.Trigger + " · " + seed.Graph.Actions.Count + " action(s)" : "Create this event with the shared event editor.";
    public string ContentGuidance => IsUnitPreset
        ? "Unit presets contain a stable unit ID and reusable unit fields. Side, location and difficulty are supplied when the preset is placed."
        : "Give the preset a readable macro name, then configure its trigger and actions. Applying the event editor returns here; Stage preset joins the global Save.";
    public UnitPlacementWindowViewModel? BeginUnitEditor() => IsUnitPreset && CanConfigureContent
        ? _unitEditorFactory?.Invoke(_unitTemplate, IsNameReadOnly) : null;
    public bool ApplyUnitTemplate(WccScenarioUnitTemplate template)
    {
        if (!IsUnitPreset || !CanConfigureContent) return false;
        if (IsNameReadOnly && template.Values.GetValueOrDefault("id") != _savedUnitId)
        { Problem = "The saved unit preset keeps its native unit ID."; return false; }
        _unitTemplate = template;
        if (!_nameExplicit && !IsNameReadOnly)
            SetField(ref _name, "CHARACTER_" + SymbolSuffix(template.Values.GetValueOrDefault("id"), "Character"), nameof(Name));
        Problem = null; OnPropertyChanged(nameof(UnitTemplate)); OnPropertyChanged(nameof(ContentSummary)); return true;
    }
    public EventNodeGraphWindowViewModel? BeginEventEditor() => BeginEventEditor(out _);
    public EventNodeGraphWindowViewModel? BeginEventEditor(out Func<WccScenarioEventAction, UnitPlacementWindowViewModel?>? spawnUnitEditor)
    {
        spawnUnitEditor = null;
        if (!IsEventPreset || !CanConfigureContent) return null;
        var declaredDifficulties = EventDeclaredDifficultyDefines;
        var scopeVersion = _eventScopeVersion;
        var unitBinding = EventUnitPresetProvider?.Invoke();
        var unitPresets = unitBinding?.Entries ?? [];
        var capture = _eventText is null ? null : WccScenarioEventGraphEditor.Capture(_eventText, declaredDifficulties, unitPresets);
        if (capture is { CanEdit: false }) { Problem = capture.Diagnostic; return null; }
        var seed = capture?.Seed;
        bool Current() => CanConfigureContent && scopeVersion == _eventScopeVersion &&
            declaredDifficulties.SequenceEqual(EventDeclaredDifficultyDefines, StringComparer.Ordinal);
        spawnUnitEditor = action => Current() ? EventUnitEditorFactory?.Invoke(action, unitBinding, Current) : null;
        return new(seed?.Graph ?? new(WccScenarioEventTrigger.Unselected, []), _eventSpeakerIds,
            Current, text => Current() ? ValidateEventText(text, unitBinding) : "The event preset scope changed. Reopen the event editor.",
            IsNameReadOnly ? seed : null, declaredDifficultyDefines: declaredDifficulties, knownUnitPresets: unitPresets);
    }
    public string? ValidateEventText(string text) => ValidateEventText(text, EventUnitPresetProvider?.Invoke());
    private string? ValidateEventText(string text, WccCampaignPresetSelectionBinding? unitBinding)
    {
        if (!CanConfigureContent) return "The campaign preset context changed. Reopen its form.";
        try
        {
            var payload = WccCampaignLocalPresetPayload.FromEvent(text, EventDeclaredDifficultyDefines, unitBinding?.Entries);
            if (payload.EventSeed!.Graph.Actions.Any(action => action.Unit?.Preset is { } preset && unitBinding?.ContainsCurrent(preset) != true))
                return "A referenced Unit preset changed. Reopen the event editor.";
            if (IsNameReadOnly && payload.EventSeed!.Graph.EventId != _savedEventId)
                return "The saved event preset keeps its native event ID, including an absent ID.";
            return null;
        }
        catch (Exception issue) when (issue is ArgumentException or InvalidOperationException) { return issue.Message; }
    }
    public bool ApplyEventText(string text)
    {
        var issue = !IsEventPreset ? "Choose an event preset." : ValidateEventText(text);
        if (issue is not null) { Problem = issue; return false; }
        _eventText = text; Problem = null;
        OnPropertyChanged(nameof(EventText)); OnPropertyChanged(nameof(ContentSummary)); return true;
    }
    internal void InitializeSavedContentName(string name)
    { _name = name; _nameExplicit = true; OnPropertyChanged(nameof(Name)); }
    internal void LoadContentPayload(WccCampaignLocalPresetPayload payload, bool saved)
    {
        if (payload.Role != Role) throw new ArgumentException("The preset payload role differs from its form.", nameof(payload));
        _unitTemplate = payload.UnitTemplate; _eventText = payload.EventText;
        if (saved) { _savedUnitId = _unitTemplate?.Values.GetValueOrDefault("id"); _savedEventId = payload.EventSeed?.Graph.EventId; }
        OnPropertyChanged(nameof(UnitTemplate)); OnPropertyChanged(nameof(EventText)); OnPropertyChanged(nameof(ContentSummary));
    }
    private bool StageContent()
    {
        if (!CanConfigureContent) { Problem = "The campaign preset form changed or closed. Reopen it; existing drafts remain."; return false; }
        try
        {
            if (IsEventPreset && _eventText is { } eventText && ValidateEventText(eventText) is { } eventProblem)
            { Problem = eventProblem; return false; }
            var payload = IsUnitPreset
                ? _unitTemplate is { } unit ? WccCampaignLocalPresetPayload.FromUnit(unit) : throw new InvalidOperationException("Configure the unit before staging its preset.")
                : _eventText is { } text ? WccCampaignLocalPresetPayload.FromEvent(text, EventDeclaredDifficultyDefines, EventUnitPresets) : throw new InvalidOperationException("Configure the event before staging its preset.");
            if (_stage(Name, payload)) return true;
            Problem = StageProblemProvider?.Invoke() ?? "The preset could not be staged. Reopen its current form.";
        }
        catch (Exception issue) when (issue is ArgumentException or InvalidOperationException) { Problem = issue.Message; }
        return false;
    }
    private static string SymbolSuffix(string? value, string fallback)
    {
        var suffix = string.IsNullOrWhiteSpace(value) ? fallback : new string(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        return suffix.Length > 118 ? suffix[..118] : suffix;
    }
}

public partial class CampaignWindowViewModel
{
    internal Func<CatalogPickerOptionVm, Task<IReadOnlyList<string>?>>? PresetUnitGenderResolver { get; set; }
    internal Func<IReadOnlyList<string>>? PresetEventSpeakerIds { get; set; }
    internal Func<ScenarioMapPreviewVm?>? PresetEventMapProvider { get; set; }

    private CampaignPresetCreationForm CreateContentPresetForm(WccCampaignLocalPresetRole role, Guid id,
        WccCampaignLocalPresetStorageSnapshot storage, IReadOnlyList<CatalogPickerOptionVm> units,
        CampaignDraftBinding binding, Func<bool> current, WccCampaignLocalPresetEntry? entry = null,
        WccCampaignLocalPresetPayload? initial = null)
    {
        var genderResolver = PresetUnitGenderResolver;
        var eventMapProvider = PresetEventMapProvider;
        var capturedMap = eventMapProvider?.Invoke();
        var plan = EffectiveCampaignPlan;
        var declaredDifficulties = ScenarioWindowViewModel.CaptureDeclaredEventDifficulties(plan);
        var unitPresetBinding = CaptureCampaignPresetSelection(WccCampaignLocalPresetRole.Unit);
        var allowsPackageDifficulties = CampaignEntries.Count == 1;
        var knownIds = (PresetEventSpeakerIds?.Invoke() ?? []).Concat(CampaignLocalPresets.Where(preset => preset.IsSupported && preset.IsAvailableIn(EffectiveCampaignPlan)).SelectMany(preset =>
                preset.Payload is { } payload ? payload.Values.Where(pair => pair.Key == "id" && payload.Role != WccCampaignLocalPresetRole.Event).Select(pair => pair.Value)
                    .Concat(payload.Leaders.Select(leader => leader.Values.GetValueOrDefault("id") ?? "")) : []))
            .Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
        UnitPlacementWindowViewModel UnitEditor(WccScenarioUnitTemplate? template, bool lockId) =>
            UnitPlacementWindowViewModel.CreateTemplate(template, units, lockId, current, async type =>
            {
                var matches = units.Where(unit => unit.ValueId == type).ToArray();
                if (!current() || matches.Length != 1 || genderResolver is null) return null;
                var values = await genderResolver(matches[0]);
                return current() ? values : null;
            });
        CampaignPresetCreationForm? form = null;
        form = new(role, id, entry?.SourcePath ?? storage.StoragePath,
            (name, payload) => entry is null ? StageCampaignPresetCreationPayload(id, name, payload, form, binding, current)
                : current() && name == entry.Name && StageCampaignPresetMutation(new(entry, payload), binding, current),
            current, UnitEditor, knownIds)
        {
            IsNameReadOnly = entry is not null,
            EventDeclaredDifficultyProvider = () => current() && form?.SelectedScope is { } selected &&
                (selected.Scope.IsPackage ? allowsPackageDifficulties : selected.Scope.Targets(plan)) ? declaredDifficulties : [],
            EventUnitPresetProvider = () => current() && form?.SelectedScope is { } scope && unitPresetBinding is not null
                ? new WccCampaignPresetSelectionBinding(WccCampaignLocalPresetRole.Unit,
                    unitPresetBinding.Entries.Where(preset => preset.Scope.IsPackage || !scope.Scope.IsPackage && preset.Scope == scope.Scope),
                    () => current() && unitPresetBinding.IsCurrent,
                    preset => unitPresetBinding.ContainsCurrent(preset)) : null,
            EventUnitEditorFactory = (action, presets, eventCurrent) => EventSpawnUnitEditor.Create(action, units, presets,
                () => current() && eventCurrent() && ReferenceEquals(genderResolver, PresetUnitGenderResolver), genderResolver),
            EventMapProvider = () => current() && ReferenceEquals(PresetEventMapProvider, eventMapProvider) &&
                ReferenceEquals(eventMapProvider?.Invoke(), capturedMap) ? capturedMap : null,
            StageProblemProvider = () => Status,
            ScopeChoices = entry is null ? CampaignPresetScopeChoices(storage) : [new(entry.Scope, entry.Scope.DisplayName, entry.SourcePath)],
            StorageNotice = "Save writes this preset in the selected scope. Changes to a saved preset affect its uses in scenarios."
        };
        if (entry is not null) form.InitializeSavedContentName(entry.Name);
        form.SelectedScope = form.ScopeChoices.First(choice => entry is null ? choice.Scope.IsPackage : choice.Scope == entry.Scope);
        if (initial is not null) form.LoadContentPayload(initial, saved: entry is not null);
        return form;
    }
}
