using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;
using Wcc.Core.Maps;

namespace Wcc.App.ViewModels;

/// <summary>Captured choices for a detached questionnaire; no editor or writer is borrowed.</summary>
public sealed record ScenarioCreationScheduleChoice(CatalogPickerOptionVm Option, string Invocation,
    IReadOnlyList<ScenarioInitialTimeSlot> Times, IReadOnlyList<WmlMacroExpansionDefinition> Definitions);

public sealed class ScenarioCreationWizardSetup : IDisposable
{
    public ScenarioCreationWizardSetup(WmlCampaignEvaluationPlan plan,
        IEnumerable<WccCampaignLocalPresetEntry> presets, IEnumerable<CatalogPickerOptionVm> units,
        IEnumerable<CatalogPickerOptionVm> music, IEnumerable<ScenarioCreationScheduleChoice> schedules,
        Func<WmlStaticScalarOwner, string, Func<string, string?>, WccFieldImagePreviewVm> preview,
        WccScenarioCreationMap? defaultMap = null, IReadOnlyList<WmlMacroExpansionDefinition>? musicDefinitions = null,
        IDisposable? previewLifetime = null, string? defaultHeroId = null, WesnothTerrainVisualCatalog? terrainCatalog = null)
    {
        Plan = plan; Presets = WccCampaignPresetSelectionBinding.AvailableEntries(presets, plan);
        Units = Array.AsReadOnly(units.ToArray()); Music = Array.AsReadOnly(music.ToArray());
        Schedules = Array.AsReadOnly(schedules.Select(choice => choice with
        { Times = Array.AsReadOnly(choice.Times.ToArray()), Definitions = Array.AsReadOnly(choice.Definitions.ToArray()) }).ToArray());
        Preview = preview; DefaultMap = defaultMap;
        DefaultHeroId = defaultHeroId;
        TerrainCatalog = terrainCatalog ?? WesnothTerrainVisualCatalog.LoadCore(null);
        MusicDefinitions = Array.AsReadOnly((musicDefinitions ?? []).ToArray()); _previewLifetime = previewLifetime;
    }
    public WmlCampaignEvaluationPlan Plan { get; }
    public IReadOnlyList<WccCampaignLocalPresetEntry> Presets { get; }
    public IReadOnlyList<CatalogPickerOptionVm> Units { get; }
    public IReadOnlyList<CatalogPickerOptionVm> Music { get; }
    public IReadOnlyList<ScenarioCreationScheduleChoice> Schedules { get; }
    public WccScenarioCreationMap? DefaultMap { get; }
    public string? DefaultHeroId { get; }
    public WesnothTerrainVisualCatalog TerrainCatalog { get; }
    public IReadOnlyList<WmlMacroExpansionDefinition> MusicDefinitions { get; }
    internal Func<WmlStaticScalarOwner, string, Func<string, string?>, WccFieldImagePreviewVm> Preview { get; }
    private readonly IDisposable? _previewLifetime;
    public void Dispose() => _previewLifetime?.Dispose();
}

/// <summary>One retained side page, including local literal fields and saved preset invocations.</summary>
public sealed class ScenarioCreationSideViewModel : ViewModelBase, IDisposable
{
    private readonly Dictionary<WmlStaticScalarOwner, (WccCampaignLocalPresetEntry Entry, string? Id)> _leaderPresets = [];
    private WccCampaignLocalPresetEntry? _sidePreset;
    private string? _saveIdOverride;
    private readonly Func<bool> _current;
    private readonly ScenarioCreationWizardSetup _setup;
    private CampaignPresetFieldSession? _sidePreview;
    private readonly Dictionary<WccCampaignLocalPresetEntry, CampaignPresetFieldSession> _sideInstancePreviews = [];
    private CampaignPresetFieldSession? _leaderPreview;
    private readonly Dictionary<(WmlStaticScalarOwner Owner, WccCampaignLocalPresetEntry Entry), CampaignPresetFieldSession> _leaderInstancePreviews = [];
    private Func<ScenarioMapPreviewVm?>? _coordinateMapProvider;
    private (WmlStaticScalarOwner Owner, WccCampaignLocalPresetEntry Entry)? _leaderPreviewKey;
    private IReadOnlyList<WccInitialFieldEditorGroup>? _leaderDisplayGroupSource;
    private IReadOnlyList<WccInitialFieldEditorGroup> _leaderDisplayGroups = [];
    private bool _refreshing;
    public ScenarioCreationSideViewModel(int number, ScenarioCreationWizardSetup setup, Func<bool> current)
    {
        Number = number; _current = current; _setup = setup;
        Fields = new(WccCampaignLocalPresetRole.Side, setup.Plan, setup.Units, current, setup.Preview);
        Fields.SetWccInitialFieldVisible(ScenarioInitialFieldOwnerScope.Leader, "x", true);
        Fields.SetWccInitialFieldVisible(ScenarioInitialFieldOwnerScope.Leader, "y", true);
        SetSide("controller", number == 1 ? "human" : "ai");
        SetSide("team_name", number == 1 ? "player" : "enemy");
        SetSide("user_team_name", number == 1 ? "Player" : "Enemy");
        SetSide("recruit", number == 1 ? "Spearman,Bowman" : "Orcish Grunt,Orcish Archer");
        SetSide("gold", number == 1 ? "100" : "75");
        Fields.AddLeader();
        SetLeader("type", number == 1 ? "Lieutenant" : "Orcish Warrior");
        SetLeader("id", number == 1 ? setup.DefaultHeroId ?? "WCC_Leader_1" : "WCC_Leader_" + number);
        if (number == 1) SetLeader("name", "Leader");
        Fields.Changed += FieldsChanged;
    }
    public int Number { get; }
    public string Heading => "Side " + Number;
    public CampaignPresetFieldSession Fields { get; }
    public CampaignPresetFieldSession DisplayFields => SidePreset?.Payload?.Leaders.Count > 0 ? _sidePreview! : Fields;
    public WccInitialLeaderVm? SelectedLeader
    {
        get => DisplayFields.SelectedLeader;
        set
        {
            // Replacing the displayed collection transiently clears ListBox selection.
            // Keep the retained custom leader and select from the new display only.
            if (value is null && DisplayFields.Leaders.Count > 0) return;
            if (value is not null && !DisplayFields.Leaders.Contains(value)) return;
            DisplayFields.SelectedLeader = value;
        }
    }
    public IReadOnlyList<WccInitialFieldEditorGroup> SideDisplayGroups => _sidePreview?.SideGroups ?? Fields.SideGroups;
    public IReadOnlyList<WccInitialFieldEditorGroup> LeaderDisplayGroups
    {
        get
        {
            var source = _leaderPreview?.LeaderGroups ?? DisplayFields.LeaderGroups;
            if (!ReferenceEquals(source, _leaderDisplayGroupSource))
            {
                _leaderDisplayGroupSource = source;
                _leaderDisplayGroups = Array.AsReadOnly(source.OrderBy(group => group.Label == "Identity" ? 0 :
                    group.Fields.Any(input => input.Key is "x" or "y") ? 1 : 2).ToArray());
            }
            return _leaderDisplayGroups;
        }
    }
    public WmlEvaluationContext SelectedContext
    {
        get => DisplayFields.SelectedContext;
        set
        {
            if (!_setup.Plan.Contexts.Contains(value)) return;
            _refreshing = true;
            try
            {
                Fields.SelectedContext = value;
                if (_sidePreview is not null) _sidePreview.SelectedContext = value;
                if (_leaderPreview is not null) _leaderPreview.SelectedContext = value;
            }
            finally { _refreshing = false; }
            Refresh();
        }
    }
    public WccCampaignLocalPresetEntry? SidePreset => _sidePreset;
    public bool UsesSidePreset => SidePreset is not null;
    public bool CanEditSideFields => !UsesSidePreset && _current();
    public bool CanEditLeaders => _current() && SidePreset?.Payload?.Leaders.Count is not > 0;
    public bool CanEditLeaderFields => CanEditLeaders && SelectedLeaderPreset is null;
    public bool CanAddLeader => CanEditLeaders && Fields.CanAddLeader;
    public bool CanRemoveLeader => CanEditLeaders && Fields.CanRemoveLeader;
    public string LeaderGuidance => !CanEditLeaders
        ? SidePreset?.Payload?.Leaders.Count == 1
            ? "This leader comes from the side preset. X and Y set its local starting location; other fields remain shared. Clear or Reset restores the preset or map start."
            : "This side preset contains several leaders. Their local coordinates are ambiguous; edit the shared preset or choose Custom."
        : UsesLeaderPreset ? "Preset fields are shared. X and Y set the location for this leader use only. Leave local coordinates unset to keep the preset's location or this side's map start; Reset restores that default."
        : UsesSidePreset ? "This preset supplies side settings only. Configure its leaders separately below."
        : "Configure local leaders or select a saved campaign leader preset. Leave X and Y empty to use this side's map start, or set both coordinates to place the leader elsewhere.";
    public WccFieldVisibilityDialogViewModel CreateVisibilityDialog() => new(_sidePreview ?? Fields, _leaderPreview ?? DisplayFields);
    public string SidePresetLabel => SidePreset is { } preset ? "{" + preset.Name + "}" : "Custom side fields";
    public string? SaveIdOverride { get => _saveIdOverride; set { if (SetField(ref _saveIdOverride, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public WccCampaignLocalPresetEntry? SelectedLeaderPreset => CanEditLeaders && Fields.SelectedLeader is { } leader &&
        _leaderPresets.TryGetValue(leader.Owner, out var preset) ? preset.Entry : null;
    public bool UsesLeaderPreset => SelectedLeaderPreset is not null;
    public string LeaderPresetLabel => !CanEditLeaders && SidePreset is { } side ? "Leaders from {" + side.Name + "}"
        : SelectedLeaderPreset is { } preset ? "{" + preset.Name + "}" : "Custom leader fields";
    public WccFieldImagePreviewVm? LeaderImagePreview => (_leaderPreview ?? DisplayFields).LeaderImagePreview;
    public WccFieldImagePreviewVm? LeaderPortraitPreview => (_leaderPreview ?? DisplayFields).LeaderPortraitPreview;
    public string? LeaderIdOverride
    {
        get => Fields.SelectedLeader is { } leader && _leaderPresets.TryGetValue(leader.Owner, out var preset) ? preset.Id : null;
        set
        {
            if (!CanEditLeaders || Fields.SelectedLeader is not { } leader || !_leaderPresets.TryGetValue(leader.Owner, out var preset)) return;
            _leaderPresets[leader.Owner] = (preset.Entry, value); Refresh();
        }
    }
    public event EventHandler? Changed;
    private void SetSide(string key, string value) => Fields.SetWccInitialFieldValue(Fields.SideFields.Single(field => field.Key == key).Owner,
        key, value, WccScenarioFieldValueInputKind.Literal);
    private void SetLeader(string key, string value) => Fields.LeaderFields.Single(field => field.Key == key).Value = value;
    internal void UseSidePreset(WccCampaignLocalPresetEntry entry, string? saveId)
    {
        if (!_current() || entry.Payload is null || !CanUsePreset(entry, WccCampaignLocalPresetRole.Side)) return;
        var context = SelectedContext;
        if (!_sideInstancePreviews.TryGetValue(entry, out var preview))
        {
            preview = Preview(entry.Payload, context);
            preview.EnableSideInstanceOverrides();
            preview.SetCoordinateMapProvider(() => ReferenceEquals(_sidePreset, entry) && _current() ? _coordinateMapProvider?.Invoke() : null);
            preview.Changed += FieldsChanged;
            _sideInstancePreviews.Add(entry, preview);
        }
        _sidePreview = preview;
        _sidePreview.SelectedContext = context;
        _sidePreset = entry; _saveIdOverride = saveId; Refresh();
    }
    public void UseCustomSide()
    {
        if (!_current()) return;
        var context = SelectedContext; _sidePreset = null; _saveIdOverride = null;
        _sidePreview = null; Fields.SelectedContext = context; Refresh();
    }
    internal void UseLeaderPreset(WccCampaignLocalPresetEntry entry, string? id)
    { if (CanEditLeaders && CanUsePreset(entry, WccCampaignLocalPresetRole.Leader) && Fields.SelectedLeader is { } leader) { _leaderPresets[leader.Owner] = (entry, id); Refresh(); } }
    public void UseCustomLeader()
    { if (CanEditLeaders && Fields.SelectedLeader is { } leader) { _leaderPresets.Remove(leader.Owner); Refresh(); } }
    public void AddLeader() { if (CanAddLeader) Fields.AddLeader(); }
    public void RemoveLeader()
    {
        if (!CanRemoveLeader || Fields.SelectedLeader is not { } leader) return;
        _leaderPresets.Remove(leader.Owner);
        foreach (var key in _leaderInstancePreviews.Keys.Where(key => ReferenceEquals(key.Owner, leader.Owner)).ToArray())
        {
            var preview = _leaderInstancePreviews[key]; preview.Changed -= FieldsChanged; preview.Dispose();
            _leaderInstancePreviews.Remove(key);
        }
        Fields.RemoveLeader();
    }
    internal void SetCoordinateMapProvider(Func<ScenarioMapPreviewVm?> provider)
    {
        _coordinateMapProvider = provider;
        Fields.SetCoordinateMapProvider(() => CanEditLeaderFields ? provider() : null);
    }
    internal void InvalidateCoordinatePicker()
    {
        Fields.InvalidateCoordinatePicker();
        foreach (var preview in _sideInstancePreviews.Values) preview.InvalidateCoordinatePicker();
        foreach (var preview in _leaderInstancePreviews.Values) preview.InvalidateCoordinatePicker();
    }
    public WccScenarioCreationSide BuildSide()
    {
        try { return BuildSideCore(); }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
        { throw new ArgumentException("Side " + Number + ": " + refusal.Message, refusal); }
    }
    private WccScenarioCreationSide BuildSideCore()
    {
        if (!_current()) throw new InvalidOperationException("The scenario wizard no longer matches this campaign.");
        if (SidePreset is { } sidePreset && !CanUsePreset(sidePreset, WccCampaignLocalPresetRole.Side) ||
            _leaderPresets.Values.Any(preset => !CanUsePreset(preset.Entry, WccCampaignLocalPresetRole.Leader)))
            throw new InvalidOperationException("A selected preset is not available in this captured campaign. Reopen the scenario wizard.");
        var sideOverrides = _sidePreview?.BuildSideInstanceOverrides();
        if (SidePreset?.Payload?.Leaders.Count > 0) return new(Preset: SidePreset, SaveIdOverride: EmptyToNull(SaveIdOverride))
        {
            LocalValues = sideOverrides!.Values,
            LocalDifficultyValues = sideOverrides.DifficultyValues,
            PresetLeaderCoordinates = _sidePreview!.BuildSideInstanceLeaderCoordinates()
        };
        var payload = UsesSidePreset ? null : Fields.BuildPayload(owner => !_leaderPresets.ContainsKey(owner));
        var literalLeaders = payload?.Leaders ?? Fields.BuildLeaderAdditions(owner => !_leaderPresets.ContainsKey(owner));
        var literalIndex = 0;
        var leaders = Fields.Leaders.Select(row => _leaderPresets.TryGetValue(row.Owner, out var selected)
            ? BuildPresetLeader(row.Owner, selected.Entry, selected.Id)
            : literalLeaders[literalIndex++]).ToArray();
        return new(payload is null ? null : new(WccCampaignLocalPresetRole.Side, payload.Values, payload.DifficultyValues),
            Preset: SidePreset, SaveIdOverride: UsesSidePreset ? EmptyToNull(SaveIdOverride) : null, Leaders: Array.AsReadOnly(leaders))
        {
            LocalValues = sideOverrides?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
            LocalDifficultyValues = sideOverrides?.DifficultyValues ?? new Dictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(StringComparer.Ordinal)
        };
    }
    private bool CanUsePreset(WccCampaignLocalPresetEntry entry, WccCampaignLocalPresetRole role) =>
        entry.IsSupported && entry.Role == role && entry.IsAvailableIn(_setup.Plan) &&
        _setup.Presets.Any(current => ReferenceEquals(current, entry));
    private WccScenarioLeaderAddition BuildPresetLeader(WmlStaticScalarOwner owner, WccCampaignLocalPresetEntry entry, string? id)
    {
        var local = _leaderInstancePreviews.GetValueOrDefault((owner, entry))?.BuildLeaderInstanceCoordinates();
        var values = local?.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (EmptyToNull(id) is { } identity) values["id"] = identity;
        return WccScenarioLeaderAddition.FromPreset(entry, values, local?.DifficultyValues);
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void FieldsChanged(object? sender, EventArgs args) => Refresh();
    private CampaignPresetFieldSession Preview(WccCampaignLocalPresetPayload payload, WmlEvaluationContext context, string? localId = null)
    {
        var preview = new CampaignPresetFieldSession(payload.Role, _setup.Plan, _setup.Units, _current, _setup.Preview);
        preview.SelectedContext = context; preview.LoadReadOnlyPayload(payload, EmptyToNull(localId)); return preview;
    }
    private void ReleasePreview(ref CampaignPresetFieldSession? preview)
    { if (preview is not null) { preview.Changed -= FieldsChanged; preview.Dispose(); preview = null; } }
    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            // Preview selection never overwrites the retained custom drafts.
            var context = DisplayFields.SelectedContext;
            Fields.SelectedContext = context;
            if (_sidePreview is not null) _sidePreview.SelectedContext = context;
            var key = SelectedLeaderPreset is { } entry && Fields.SelectedLeader is { } row
                ? (row.Owner, entry) : ((WmlStaticScalarOwner, WccCampaignLocalPresetEntry)?)null;
            if (_leaderPreviewKey != key)
            {
                _leaderPreview = null; _leaderPreviewKey = key;
                if (key is { } selected)
                {
                    if (!_leaderInstancePreviews.TryGetValue(selected, out var preview))
                    {
                        preview = Preview(selected.Item2.Payload!, context);
                        preview.EnableLeaderInstanceCoordinates();
                        preview.SetCoordinateMapProvider(() => CanEditLeaders && ReferenceEquals(Fields.SelectedLeader?.Owner, selected.Item1)
                            && ReferenceEquals(SelectedLeaderPreset, selected.Item2) ? _coordinateMapProvider?.Invoke() : null);
                        preview.Changed += FieldsChanged; _leaderInstancePreviews.Add(selected, preview);
                    }
                    _leaderPreview = preview;
                }
            }
            if (_leaderPreview is not null)
            {
                _leaderPreview.SelectedContext = context;
                _leaderPreview.UpdateLeaderInstanceId(EmptyToNull(LeaderIdOverride));
            }
        }
        finally { _refreshing = false; }
        foreach (var property in new[] { nameof(UsesSidePreset), nameof(CanEditSideFields), nameof(CanEditLeaderFields),
            nameof(SidePresetLabel), nameof(SaveIdOverride), nameof(SelectedLeaderPreset), nameof(UsesLeaderPreset),
            nameof(LeaderPresetLabel), nameof(LeaderIdOverride), nameof(LeaderImagePreview), nameof(LeaderPortraitPreview),
            nameof(DisplayFields), nameof(SelectedLeader), nameof(SideDisplayGroups), nameof(LeaderDisplayGroups), nameof(SelectedContext), nameof(CanEditLeaders),
            nameof(CanAddLeader), nameof(CanRemoveLeader), nameof(LeaderGuidance) }) OnPropertyChanged(property);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        foreach (var preview in _sideInstancePreviews.Values) { preview.Changed -= FieldsChanged; preview.Dispose(); }
        _sideInstancePreviews.Clear(); _sidePreview = null;
        foreach (var preview in _leaderInstancePreviews.Values) { preview.Changed -= FieldsChanged; preview.Dispose(); }
        _leaderInstancePreviews.Clear(); _leaderPreview = null;
        Fields.Changed -= FieldsChanged; Fields.Dispose();
    }
}
