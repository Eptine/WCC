using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>A detached literal payload form. Its synthetic owners carry no source-write authority.</summary>
public sealed class CampaignPresetFieldSession : ViewModelBase, IWccInitialFieldEditorHost, IDisposable
{
    private sealed class Piece(WmlStaticScalarOwner owner)
    {
        public WmlStaticScalarOwner Owner { get; } = owner;
        public Dictionary<string, WccScenarioInlineDraft> Shared { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, WccScenarioInlineDraft>> Independent { get; } = new(StringComparer.Ordinal);
    }
    private readonly Func<bool> _isCurrent;
    private readonly IReadOnlyList<CatalogPickerOptionVm> _units;
    private readonly Func<WmlStaticScalarOwner, string, Func<string, string?>, WccFieldImagePreviewVm> _preview;
    private readonly Dictionary<(ScenarioInitialFieldOwnerScope, string), bool> _visible = new();
    private readonly Dictionary<WmlStaticScalarOwner, Piece> _pieces = new();
    private readonly Piece _root;
    private WmlEvaluationContext _context;
    private bool _disposed;
    private bool _readOnly;
    private readonly HashSet<WmlStaticScalarOwner> _savedIdentityOwners = [];
    private string? _difficultyEditingProblem;
    private Piece? _leaderInstanceCoordinates;
    private readonly Dictionary<WmlStaticScalarOwner, Piece> _instanceOverrides = [];
    private readonly Dictionary<WmlStaticScalarOwner, HashSet<string>> _instanceOverrideKeys = [];
    private string? _leaderInstanceId;
    private Func<ScenarioMapPreviewVm?>? _coordinateMapProvider;
    private long _revision;
    private WccInitialLeaderVm? _selectedLeader;
    public WccCampaignLocalPresetRole Role { get; }
    public WmlCampaignEvaluationPlan Plan { get; }
    public IReadOnlyList<WmlEvaluationContext> Contexts => Plan.Contexts;
    public WmlEvaluationContext SelectedContext
    {
        get => _context;
        set { if (!_disposed && Plan.Contexts.Contains(value) && SetField(ref _context, value)) Refresh(); }
    }
    public ObservableCollection<WccInitialLeaderVm> Leaders { get; } = [];
    public WccInitialLeaderVm? SelectedLeader
    {
        get => _selectedLeader;
        set { if (!_disposed && (value is null || Leaders.Contains(value)) && SetField(ref _selectedLeader, value)) Refresh(); }
    }
    public IReadOnlyList<WccInitialFieldEditorVm> SideFields { get; private set; } = [];
    public IReadOnlyList<WccInitialFieldEditorVm> ScenarioFields { get; private set; } = [];
    public IReadOnlyList<WccInitialFieldEditorGroup> ScenarioGroups { get; private set; } = [];
    public IReadOnlyList<WccInitialFieldEditorVm> LeaderFields { get; private set; } = [];
    public IReadOnlyList<WccInitialFieldEditorGroup> SideGroups { get; private set; } = [];
    public IReadOnlyList<WccInitialFieldEditorGroup> LeaderGroups { get; private set; } = [];
    public bool IsScenario => _root.Owner.TagName == "scenario";
    public bool IsSide => _root.Owner.TagName == "side";
    public int LeaderColumn => IsSide ? 1 : 0;
    public int LeaderColumnSpan => IsSide ? 1 : 2;
    public bool HasLeader => SelectedLeader is not null;
    internal string? SuggestedPresetIdentity => IsSide
        ? GetValue(_root.Owner, "save_id") ?? (Leaders.FirstOrDefault() is { } first ? GetValue(first.Owner, "id") : null)
        : GetValue(_root.Owner, "id");
    public bool CanAddLeader => IsSide && !_readOnly && !_disposed && _isCurrent() && Leaders.Count < 256;
    public bool CanRemoveLeader => IsSide && !_readOnly && HasLeader && !_disposed && _isCurrent() &&
        !(SelectedLeader is { } leader && _savedIdentityOwners.Contains(leader.Owner) && GetValue(leader.Owner, "id") is { Length: > 0 });
    public WccFieldImagePreviewVm? LeaderImagePreview => SelectedLeader is { } row ? GetWccFieldImagePreview(row.Owner, "unit_sprite") : null;
    public WccFieldImagePreviewVm? LeaderPortraitPreview => SelectedLeader is { } row ? GetWccFieldImagePreview(row.Owner, "profile") : null;
    public event EventHandler? WccFieldVisibilityChanged;
    public event EventHandler? Changed;

    internal CampaignPresetFieldSession(WccCampaignLocalPresetRole role, WmlCampaignEvaluationPlan plan,
        IReadOnlyList<CatalogPickerOptionVm> units, Func<bool> isCurrent,
        Func<WmlStaticScalarOwner, string, Func<string, string?>, WccFieldImagePreviewVm> preview)
        : this(role, plan, units, isCurrent, preview, false) { }
    internal CampaignPresetFieldSession(WmlCampaignEvaluationPlan plan,
        IReadOnlyList<CatalogPickerOptionVm> units, Func<bool> isCurrent,
        Func<WmlStaticScalarOwner, string, Func<string, string?>, WccFieldImagePreviewVm> preview)
        : this(WccCampaignLocalPresetRole.Side, plan, units, isCurrent, preview, true) { }
    private CampaignPresetFieldSession(WccCampaignLocalPresetRole role, WmlCampaignEvaluationPlan plan,
        IReadOnlyList<CatalogPickerOptionVm> units, Func<bool> isCurrent,
        Func<WmlStaticScalarOwner, string, Func<string, string?>, WccFieldImagePreviewVm> preview, bool scenario)
    {
        Role = role; Plan = plan; _units = units; _isCurrent = isCurrent; _preview = preview;
        _context = plan.Contexts.First();
        _root = NewPiece(scenario ? "scenario" : role == WccCampaignLocalPresetRole.Leader ? "leader" : "side");
        if (IsScenario)
        {
            ScenarioFields = ScenarioInitialFieldCatalog.ScenarioFields.Select(field => new WccInitialFieldEditorVm(this, _root.Owner, field)).ToArray();
            ScenarioGroups = Groups(ScenarioFields);
        }
        else if (IsSide)
        {
            SideFields = ScenarioInitialFieldCatalog.SideFields.Where(field => field.Key != "side")
                .Select(field => new WccInitialFieldEditorVm(this, _root.Owner, field)).ToArray();
            SideGroups = Groups(SideFields);
        }
        else
        {
            var row = new WccInitialLeaderVm(_root.Owner, false, "Leader preset", "Reusable campaign-local leader fields.");
            Leaders.Add(row); SelectedLeader = row;
            SetWccInitialFieldValue(row.Owner, "canrecruit", "yes", WccScenarioFieldValueInputKind.Literal);
        }
        Refresh();
    }
    private Piece NewPiece(string tag)
    {
        var text = tag == "scenario" ? "[scenario]\n[/scenario]\n" : "[scenario]\n[side]\n" + (tag == "leader" ? "[leader]\n[/leader]\n" : "") + "[/side]\n[/scenario]\n";
        var source = new WmlStaticSource("wcc-preset-presentation-" + Guid.NewGuid().ToString("N") + ".cfg", text);
        var projection = WmlStaticScalarProjection.Create(new(text, [new(0, text, new(source, 0, text.Length), [])], [], [source], [], true));
        var owner = projection.Owners.Single(candidate => candidate.TagName == tag);
        var piece = new Piece(owner); _pieces.Add(owner, piece); return piece;
    }
    public void AddLeader()
    {
        if (!CanAddLeader) return;
        var piece = NewPiece("leader");
        var row = new WccInitialLeaderVm(piece.Owner, false, "New leader", "Nested leader in this shared side preset.");
        Leaders.Add(row); SelectedLeader = row;
        SetWccInitialFieldValue(row.Owner, "canrecruit", "yes", WccScenarioFieldValueInputKind.Literal);
    }
    public void RemoveLeader()
    {
        if (!CanRemoveLeader || SelectedLeader is not { } row) return;
        _pieces.Remove(row.Owner); Leaders.Remove(row); SelectedLeader = Leaders.FirstOrDefault(); Refresh();
    }
    public WccFieldVisibilityDialogViewModel CreateVisibilityDialog() => new(this,
        IsScenario ? ScenarioInitialFieldOwnerScope.Scenario : IsSide ? ScenarioInitialFieldOwnerScope.Side : ScenarioInitialFieldOwnerScope.Leader);
    public WccCampaignLocalPresetPayload BuildPayload() => BuildPayload(_ => true);
    public WccCampaignLocalPresetPayload BuildPayload(Func<WmlStaticScalarOwner, bool> includeLeader)
    {
        ArgumentNullException.ThrowIfNull(includeLeader);
        if (IsScenario) throw new InvalidOperationException("Scenario fields are not a preset payload.");
        EnsureCurrent();
        var leaders = IsSide ? Leaders.Where(row => includeLeader(row.Owner)).ToArray() : [];
        var pieces = new[] { _root }.Concat(leaders.Select(row => _pieces[row.Owner]));
        ValidatePieces(pieces);
        return new(Role, Values(_root), Slots(_root), IsSide ? leaders.Select(row =>
            new WccScenarioLeaderAddition(Values(_pieces[row.Owner]), Slots(_pieces[row.Owner]))) : null);
    }
    internal IReadOnlyList<WccScenarioLeaderAddition> BuildLeaderAdditions(Func<WmlStaticScalarOwner, bool> includeLeader)
    {
        ArgumentNullException.ThrowIfNull(includeLeader); EnsureCurrent();
        var pieces = Leaders.Where(row => includeLeader(row.Owner)).Select(row => _pieces[row.Owner]).ToArray();
        ValidatePieces(pieces);
        return Array.AsReadOnly(pieces.Select(piece => new WccScenarioLeaderAddition(Values(piece), Slots(piece))).ToArray());
    }
    internal void EnableLeaderInstanceCoordinates()
    {
        EnsureCurrent();
        if (!_readOnly || IsScenario || IsSide || Role != WccCampaignLocalPresetRole.Leader || _leaderInstanceCoordinates is not null)
            throw new InvalidOperationException("Instance coordinates require a captured read-only Leader preset.");
        _leaderInstanceCoordinates = new Piece(_root.Owner);
        _instanceOverrides.Add(_root.Owner, _leaderInstanceCoordinates);
        _instanceOverrideKeys.Add(_root.Owner, new(["x", "y"], StringComparer.Ordinal));
        _visible[(ScenarioInitialFieldOwnerScope.Leader, "x")] = true;
        _visible[(ScenarioInitialFieldOwnerScope.Leader, "y")] = true;
        Refresh();
    }
    internal void EnableSideInstanceOverrides()
    {
        EnsureCurrent();
        if (!_readOnly || !IsSide || _instanceOverrides.Count != 0)
            throw new InvalidOperationException("Instance overrides require a captured read-only Side preset.");
        _instanceOverrides.Add(_root.Owner, new(_root.Owner));
        _instanceOverrideKeys.Add(_root.Owner, new(["gold"], StringComparer.Ordinal));
        _visible[(ScenarioInitialFieldOwnerScope.Leader, "x")] = true;
        _visible[(ScenarioInitialFieldOwnerScope.Leader, "y")] = true;
        // [+leader] selects the last child. Only one captured leader has an
        // unambiguous local location contract in the current guided writer.
        if (Leaders.Count == 1)
        {
            var owner = Leaders[0].Owner;
            _instanceOverrides.Add(owner, new(owner));
            _instanceOverrideKeys.Add(owner, new(["x", "y"], StringComparer.Ordinal));
        }
        Refresh();
    }
    internal WccCampaignLocalPresetPayload BuildSideInstanceOverrides()
    {
        EnsureCurrent();
        if (!IsSide || !_instanceOverrides.TryGetValue(_root.Owner, out var local))
            throw new InvalidOperationException("This form is not a Side preset instance.");
        ValidatePieces([local]);
        return new(WccCampaignLocalPresetRole.Side, Values(local), Slots(local));
    }
    internal WccScenarioLeaderAddition? BuildSideInstanceLeaderCoordinates()
    {
        EnsureCurrent();
        if (!IsSide) throw new InvalidOperationException("This form is not a Side preset instance.");
        var local = Leaders.Count == 1 ? _instanceOverrides.GetValueOrDefault(Leaders[0].Owner) : null;
        if (local is null || local.Shared.Count + local.Independent.Count == 0) return null;
        ValidatePieces([local]);
        return new(Values(local), Slots(local));
    }
    internal WccScenarioLeaderAddition BuildLeaderInstanceCoordinates()
    {
        EnsureCurrent();
        if (_leaderInstanceCoordinates is not { } coordinates)
            throw new InvalidOperationException("This form is not a Leader preset instance.");
        ValidatePieces([coordinates]);
        return new(Values(coordinates), Slots(coordinates));
    }
    internal void UpdateLeaderInstanceId(string? localId)
    {
        EnsureCurrent();
        if (_leaderInstanceCoordinates is null) throw new InvalidOperationException("This form is not a Leader preset instance.");
        var before = GetValue(_root.Owner, "id");
        _leaderInstanceId = string.IsNullOrWhiteSpace(localId) ? null : localId.Trim();
        if (!string.Equals(before, GetValue(_root.Owner, "id"), StringComparison.Ordinal)) Refresh();
    }
    internal IReadOnlyList<WccScenarioDifficultyFieldValue>? CaptureDifficultyValues(string key)
    {
        EnsureCurrent(); ValidatePieces([_root]);
        return _root.Independent.ContainsKey(key) ? Array.AsReadOnly(Slots(_root).Single(field => field.Key == key).Value.ToArray()) : null;
    }
    internal void SetDifficultyEditingProblem(string? problem)
    { _difficultyEditingProblem = problem; Refresh(); }
    internal void LockSavedIdentities()
    { foreach (var owner in _pieces.Keys) _savedIdentityOwners.Add(owner); Refresh(); }
    private void EnsureCurrent()
    { if (_disposed || !_isCurrent()) throw new InvalidOperationException("The field form is stale. Reopen it in the current campaign."); }
    private static void ValidatePieces(IEnumerable<Piece> pieces)
    {
        var error = pieces.SelectMany(piece => piece.Shared.Values.Concat(piece.Independent.Values.SelectMany(slots => slots.Values)))
            .FirstOrDefault(field => field.ValidationError is not null)?.ValidationError;
        if (error is not null) throw new ArgumentException(error);
    }
    /// <summary>Displays the captured preset through the same fields without granting definition-edit authority.</summary>
    internal void LoadReadOnlyPayload(WccCampaignLocalPresetPayload payload, string? localLeaderId = null)
    {
        LoadEditablePayload(payload);
        if (localLeaderId is not null && !IsSide) SetWccInitialFieldValue(_root.Owner, "id", localLeaderId, WccScenarioFieldValueInputKind.Literal);
        _readOnly = true; Refresh();
    }
    internal void LoadEditablePayload(WccCampaignLocalPresetPayload payload)
    {
        if (_readOnly || IsScenario || payload.Role != Role) throw new InvalidOperationException("Mismatched preset preview.");
        _root.Shared.Clear(); _root.Independent.Clear();
        Load(_root, payload.Values, payload.DifficultyValues);
        foreach (var leader in payload.Leaders)
        {
            AddLeader(); var piece = _pieces[SelectedLeader!.Owner]; piece.Shared.Clear(); piece.Independent.Clear();
            Load(piece, leader.Values, leader.DifficultyValues);
        }
        SelectedLeader = Leaders.FirstOrDefault(); Refresh();
    }
    private void Load(Piece piece, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> slots)
    {
        var selected = _context;
        foreach (var field in values) SetWccInitialFieldValue(piece.Owner, field.Key, field.Value, WccScenarioFieldValueInputKind.Literal);
        foreach (var field in slots)
        {
            var first = field.Value.Single(slot => slot.ContextSemanticKey == selected.SemanticKey).ValueText;
            SetWccInitialFieldValue(piece.Owner, field.Key, first, WccScenarioFieldValueInputKind.Literal);
            ToggleWccDifficultyField(piece.Owner, field.Key, first, first);
            foreach (var slot in field.Value)
            {
                SelectedContext = Contexts.Single(context => context.SemanticKey == slot.ContextSemanticKey);
                SetWccInitialFieldValue(piece.Owner, field.Key, slot.ValueText, WccScenarioFieldValueInputKind.Literal);
            }
            SelectedContext = selected;
        }
    }
    private static IEnumerable<KeyValuePair<string, string>> Values(Piece piece) => piece.Shared.Select(field => new KeyValuePair<string, string>(field.Key, field.Value.Text ?? ""));
    private IEnumerable<KeyValuePair<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>> Slots(Piece piece) => piece.Independent.Select(field =>
        new KeyValuePair<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(field.Key, Plan.Contexts.Select(context =>
            new WccScenarioDifficultyFieldValue(context.DifficultyDefine!, context.SemanticKey, field.Value[context.SemanticKey].Text)).ToArray()));
    private IReadOnlyList<WccInitialFieldEditorGroup> Groups(IEnumerable<WccInitialFieldEditorVm> fields) => fields.GroupBy(field => field.Definition.Group)
        .Select(group => { var result = new WccInitialFieldEditorGroup(this, group.First().Definition.OwnerScope, group.Key); result.SetFields(group.ToArray()); return result; }).ToArray();
    private void Refresh()
    {
        _revision++;
        if (SelectedLeader is { } selected && (LeaderFields.Count == 0 || !ReferenceEquals(LeaderFields[0].Owner, selected.Owner)))
        {
            LeaderFields = ScenarioInitialFieldCatalog.LeaderFields.Select(field => new WccInitialFieldEditorVm(this, selected.Owner, field)).ToArray();
            LeaderGroups = Groups(LeaderFields);
        }
        else if (SelectedLeader is null) { LeaderFields = []; LeaderGroups = []; }
        foreach (var field in ScenarioFields.Concat(SideFields).Concat(LeaderFields)) field.Refresh();
        foreach (var group in ScenarioGroups.Concat(SideGroups).Concat(LeaderGroups)) group.RefreshVisibility();
        foreach (var row in Leaders) row.RefreshPreview(GetValue(row.Owner, "name") ?? GetValue(row.Owner, "id") ?? GetValue(row.Owner, "type") ?? "Leader",
            GetWccFieldImagePreview(row.Owner, "unit_sprite"));
        foreach (var property in new[] { nameof(SideFields), nameof(LeaderFields), nameof(SideGroups), nameof(LeaderGroups), nameof(HasLeader),
            nameof(CanAddLeader), nameof(CanRemoveLeader), nameof(LeaderImagePreview), nameof(LeaderPortraitPreview) }) OnPropertyChanged(property);
        WccFieldVisibilityChanged?.Invoke(this, EventArgs.Empty); Changed?.Invoke(this, EventArgs.Empty);
    }
    private bool IsLeaderInstanceCoordinate(WmlStaticScalarOwner owner, string key) =>
        _leaderInstanceCoordinates is not null && ReferenceEquals(owner, _root.Owner) && key is "x" or "y";
    private bool IsInstanceOverride(WmlStaticScalarOwner owner, string key) =>
        _instanceOverrideKeys.TryGetValue(owner, out var keys) && keys.Contains(key);
    private static WccScenarioInlineDraft? FieldIn(Piece piece, string key, string context) =>
        piece.Independent.TryGetValue(key, out var slots) ? slots.GetValueOrDefault(context) : piece.Shared.GetValueOrDefault(key);
    private string? InheritedValue(WmlStaticScalarOwner owner, string key)
    {
        if (_instanceOverrides.Count == 0 || !_pieces.TryGetValue(owner, out var original)) return null;
        if (ReferenceEquals(owner, _root.Owner) && key == "id" && _leaderInstanceId is not null) return _leaderInstanceId;
        return FieldIn(original, key, _context.SemanticKey) is { ValidationError: null } inherited ? inherited.Text : null;
    }
    private string? GetValue(WmlStaticScalarOwner owner, string key) =>
        FindWccInlineDraft(GetWccInitialFieldRoute(owner, key)) is { } field
            ? field.ValidationError is null ? field.Text : null : InheritedValue(owner, key);
    public ScenarioWindowViewModel? ScenarioEditor => null;
    public bool HasMapPreview => !_disposed && _coordinateMapProvider?.Invoke()?.Map.Rows.Count > 0;
    internal void SetCoordinateMapProvider(Func<ScenarioMapPreviewVm?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _coordinateMapProvider = provider;
        Refresh();
    }
    internal void InvalidateCoordinatePicker() => _revision++;
    public bool IsWccInlineSaveBusy => !_isCurrent() || _disposed;
    public bool AreWccInitialDraftsPending => !_readOnly || _instanceOverrides.Count != 0;
    public bool IsSelectedWccInitialFieldOwner(WmlStaticScalarOwner owner, ScenarioInitialFieldOwnerScope scope) =>
        scope == ScenarioInitialFieldOwnerScope.Scenario ? IsScenario && ReferenceEquals(owner, _root.Owner) :
        scope == ScenarioInitialFieldOwnerScope.Side ? IsSide && ReferenceEquals(owner, _root.Owner) : ReferenceEquals(owner, SelectedLeader?.Owner);
    public bool CanReviewWccInitialField(WmlStaticScalarOwner owner, string key) =>
        (!_readOnly || IsInstanceOverride(owner, key)) && !_disposed && _isCurrent() && _pieces.ContainsKey(owner) && key != "side" &&
        !(_savedIdentityOwners.Contains(owner) && key is "id" or "save_id");
    public WccScenarioFieldRoute? GetWccInitialFieldRoute(WmlStaticScalarOwner owner, string key)
    {
        if (!_pieces.ContainsKey(owner)) return null;
        var route = WccScenarioFieldRouteAnalyzer.Analyze(owner, key, owner.OpeningOrigin!.Source, _context);
        if (_instanceOverrides.Count == 0) return route;
        var local = IsInstanceOverride(owner, key) && FieldIn(_instanceOverrides[owner], key, _context.SemanticKey) is not null;
        return route with { Origin = local ? WccScenarioFieldOrigin.Override : InheritedValue(owner, key) is not null
            ? WccScenarioFieldOrigin.Macro : route.Origin, CanRemove = local && !_instanceOverrides[owner].Independent.ContainsKey(key) };
    }
    public WccScenarioInlineDraft? FindWccInlineDraft(WccScenarioFieldRoute? route) =>
        route is not null && _pieces.TryGetValue(route.Owner, out var piece)
            ? _instanceOverrides.Count != 0
                ? IsInstanceOverride(route.Owner, route.Key) ? FieldIn(_instanceOverrides[route.Owner], route.Key, route.ContextSemanticKey) : null
                : FieldIn(piece, route.Key, route.ContextSemanticKey) : null;
    public bool SetWccInitialFieldValue(WmlStaticScalarOwner owner, string key, string? value, WccScenarioFieldValueInputKind inputKind)
    {
        if (!StageFieldValue(owner, key, value, inputKind)) return false;
        Refresh(); return true;
    }
    private bool StageFieldValue(WmlStaticScalarOwner owner, string key, string? value, WccScenarioFieldValueInputKind inputKind)
    {
        if (!CanReviewWccInitialField(owner, key) || !ScenarioInitialFieldCatalog.TryGet(owner.TagName, key, out var definition) || !definition.IsEditable) return false;
        var piece = IsInstanceOverride(owner, key) ? _instanceOverrides[owner] : _pieces[owner];
        // Removing a location override restores the captured preset, including
        // all its difficulty values. Blank never authors an empty coordinate.
        if (IsInstanceOverride(owner, key) && inputKind == WccScenarioFieldValueInputKind.Literal && string.IsNullOrWhiteSpace(value))
        { piece.Shared.Remove(key); piece.Independent.Remove(key); return true; }
        var error = inputKind != WccScenarioFieldValueInputKind.Literal ? "Preset fields currently require literal values." : WccScenarioFieldValueCodec.ValidateInput(definition, value, inputKind);
        var draft = new WccScenarioInlineDraft("preset:" + key, GetWccInitialFieldRoute(owner, key)!, WmlFieldOperation.Insert, value, definition.Label, error) { ValueInputKind = inputKind };
        if (IsInstanceOverride(owner, key) && !piece.Shared.ContainsKey(key) && !piece.Independent.ContainsKey(key)
            && _pieces[owner].Independent.TryGetValue(key, out var inheritedSlots))
            piece.Independent[key] = inheritedSlots.ToDictionary(slot => slot.Key, slot => slot.Value, StringComparer.Ordinal);
        if (piece.Independent.TryGetValue(key, out var slots)) slots[_context.SemanticKey] = draft; else piece.Shared[key] = draft;
        return true;
    }
    public void RevertWccInitialField(WmlStaticScalarOwner owner, string key)
    {
        if (!CanReviewWccInitialField(owner, key)) return;
        var piece = IsInstanceOverride(owner, key) ? _instanceOverrides[owner] : _pieces[owner];
        piece.Shared.Remove(key); piece.Independent.Remove(key); Refresh();
    }
    public void RemoveWccInitialFieldOverride(WmlStaticScalarOwner owner, string key) => RevertWccInitialField(owner, key);
    public bool HasWccDifficultyDraft(WmlStaticScalarOwner owner, string key) => _instanceOverrides.Count != 0
        ? IsInstanceOverride(owner, key) && _instanceOverrides[owner].Independent.ContainsKey(key)
        : _pieces.GetValueOrDefault(owner)?.Independent.ContainsKey(key) == true;
    public bool IsWccDifficultyIndependent(WmlStaticScalarOwner owner, string key) => _instanceOverrides.Count != 0
        ? IsInstanceOverride(owner, key) && _instanceOverrides[owner].Shared.ContainsKey(key) ? false
            : HasWccDifficultyDraft(owner, key) || _pieces.GetValueOrDefault(owner)?.Independent.ContainsKey(key) == true
        : HasWccDifficultyDraft(owner, key);
    public string? WccDifficultyFieldLockReason(WmlStaticScalarOwner owner, string key, string? value) => _difficultyEditingProblem ?? (!CanReviewWccInitialField(owner, key)
        ? _readOnly ? "These values belong to the selected shared preset. Choose Custom for local fields." : "This form no longer matches the current campaign." : key is "id" or "side" or "save_id" ? "Identity fields stay shared."
        : value is null ? "Set a literal value before creating separate difficulty values."
        : WccScenarioFieldValueCodec.InferInputKind(value) != WccScenarioFieldValueInputKind.Literal ? "Preset difficulty fields require literal values."
        : ScenarioInitialFieldCatalog.TryGet(owner.TagName, key, out var definition) ? WccScenarioFieldValueCodec.ValidateInput(definition, value) : "Unknown field.");
    public bool CanToggleWccDifficultyField(WmlStaticScalarOwner owner, string key, string? value) => WccDifficultyFieldLockReason(owner, key, value) is null;
    public bool ToggleWccDifficultyField(WmlStaticScalarOwner owner, string key, string? value,
        string? baselineDisplayValue)
    {
        if (!CanToggleWccDifficultyField(owner, key, value)) return false;
        var piece = IsInstanceOverride(owner, key) ? _instanceOverrides[owner] : _pieces[owner];
        WccScenarioInlineDraft Field(WmlEvaluationContext context) => new("preset:" + key,
            WccScenarioFieldRouteAnalyzer.Analyze(owner, key, owner.OpeningOrigin!.Source, context), WmlFieldOperation.Insert, value, key, null);
        if (piece.Independent.Remove(key) || IsInstanceOverride(owner, key) && !piece.Shared.ContainsKey(key) && _pieces[owner].Independent.ContainsKey(key))
            piece.Shared[key] = Field(_context);
        else { piece.Shared.Remove(key); piece.Independent[key] = Plan.Contexts.ToDictionary(context => context.SemanticKey, Field, StringComparer.Ordinal); }
        Refresh(); return true;
    }
    public string GetWccInitialFieldEditStatus(WmlStaticScalarOwner owner, string key) => _savedIdentityOwners.Contains(owner) && key is "id" or "save_id"
        ? "Saved preset identities stay fixed so existing invocations and unit references keep their meaning." : _instanceOverrides.Count != 0
        ? IsInstanceOverride(owner, key)
            ? "Local value for this preset use. Clear or Reset restores the captured shared value for every difficulty."
            : IsSide && owner.TagName == "leader" && (key is "x" or "y") && Leaders.Count != 1
                ? "This side preset has several leaders. A local location is ambiguous; edit its shared definition or choose Custom."
                : "Captured shared preset value. Gold and an unambiguous leader's X and Y can be set locally; other fields belong to the shared definition."
        : _readOnly
        ? "Captured shared preset values. Choose Custom to configure local fields; this preview does not edit the preset."
        : IsScenario ? "New scenario value. The diamond opts into separate difficulty values." : "Shared preset definition fields. Stage the preset, then use global Save to write it.";
    public ScenarioSideSourceLocation? GetWccInitialFieldSourceLocation(WmlStaticScalarOwner owner, string key) => null;
    public bool HasAuthoredLocalWccInitialFieldValue(WmlStaticScalarOwner owner, string key) => false;
    public bool IsRequiredWccInitialField(ScenarioInitialFieldDefinition definition) => definition.OwnerScope == ScenarioInitialFieldOwnerScope.Leader && definition.Key == "type";
    public bool IsWccInitialFieldPreferredVisible(ScenarioInitialFieldDefinition definition) => _visible.GetValueOrDefault((definition.OwnerScope, definition.Key),
        definition.Key is "turns" or "type" or "id" or "name" or "canrecruit" or "controller" or "side_name" or "gold" or "income" or "recruit" or "team_name" or "user_team_name" ||
        _coordinateMapProvider is not null && definition.OwnerScope == ScenarioInitialFieldOwnerScope.Leader && definition.Key is "x" or "y" ||
        _instanceOverrides.Count != 0 && (definition.OwnerScope == ScenarioInitialFieldOwnerScope.Leader && SelectedLeader is { } selected
            ? _pieces[selected.Owner].Shared.ContainsKey(definition.Key) || _pieces[selected.Owner].Independent.ContainsKey(definition.Key)
            : _root.Shared.ContainsKey(definition.Key) || _root.Independent.ContainsKey(definition.Key)));
    public void SetWccInitialFieldVisible(ScenarioInitialFieldOwnerScope scope, string key, bool visible)
    { if (GetWccPresentationRows(scope, key).Any(field => !field.CanChangeVisibility)) return; _visible[(scope, key)] = visible; Refresh(); }
    public IReadOnlyList<WccInitialFieldEditorVm> GetWccPresentationRows(ScenarioInitialFieldOwnerScope scope, string key) => ScenarioFields.Concat(SideFields).Concat(LeaderFields).Where(field => field.Definition.OwnerScope == scope && field.Key == key).ToArray();
    public IReadOnlyList<CatalogPickerOptionVm> GetUnitTypeCatalogOptions() => _units;
    public WccFieldImagePreviewVm GetWccFieldImagePreview(WmlStaticScalarOwner owner, string key) => _preview(owner, key, name => GetValue(owner, name));
    public string? GetWccInitialFieldDefaultValue(WmlStaticScalarOwner owner, ScenarioInitialFieldDefinition definition) =>
        InheritedValue(owner, definition.Key) ?? (definition.Key switch
        { "hitpoints" => GetValue(owner, "max_hitpoints"), "moves" => GetValue(owner, "max_moves"), "attacks_left" => GetValue(owner, "max_attacks"), _ => null });
    public async Task PickWccInitialLeaderCoordinateAsync(WmlStaticScalarOwner owner, ScenarioMapCoordinatePicker? picker = null)
    {
        if (picker is null || !ReferenceEquals(owner, SelectedLeader?.Owner) ||
            !CanReviewWccInitialField(owner, "x") || !CanReviewWccInitialField(owner, "y") ||
            _coordinateMapProvider?.Invoke() is not { } map || map.Map.Rows.Count == 0) return;
        var revision = _revision;
        var context = _context;
        var x = GetValue(owner, "x"); var y = GetValue(owner, "y");
        ScenarioMapCoordinate? initial = int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var initialX) &&
            int.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out var initialY) ? new(initialX, initialY) : null;
        var result = await picker(map, new("Choose the map hex for the selected leader.", initial));
        if (!result.IsConfirmed || result.Selection is not { Coordinate: var coordinate } || _revision != revision ||
            !ReferenceEquals(context, _context) || !ReferenceEquals(owner, SelectedLeader?.Owner) ||
            !ReferenceEquals(map, _coordinateMapProvider?.Invoke()) || !CanReviewWccInitialField(owner, "x") ||
            !CanReviewWccInitialField(owner, "y")) return;
        // Stage both components before publishing one settled form change, so
        // callbacks never observe a coordinate with only its X or Y replaced.
        StageFieldValue(owner, "x", coordinate.X.ToString(CultureInfo.InvariantCulture), WccScenarioFieldValueInputKind.Literal);
        StageFieldValue(owner, "y", coordinate.Y.ToString(CultureInfo.InvariantCulture), WccScenarioFieldValueInputKind.Literal);
        Refresh();
    }
    public bool TryGetWccSideFlagDraftValue(WmlStaticScalarOwner owner, string key, out string value) { value = ""; return false; }
    public void Dispose() { _disposed = true; _pieces.Clear(); _instanceOverrides.Clear(); _instanceOverrideKeys.Clear(); _leaderInstanceCoordinates = null; }
}
