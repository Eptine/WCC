using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Maps;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed class WccInitialUnitRowVm
{
    internal WccInitialUnitRowVm(string identity, WccScenarioUnitInstanceSnapshot snapshot,
        WccScenarioUnitInstanceEntry? entry, WccScenarioUnitInstanceSpec? spec, bool removed,
        bool writable, string? problem)
    {
        Identity = identity; Snapshot = snapshot; Entry = entry; Spec = spec; Removed = removed;
        var values = spec?.Values ?? entry?.Values;
        string Value(string key) => values?.GetValueOrDefault(key) ?? "";
        UnitId = Value("id");
        UnitTypeId = Value("type");
        Title = Value("name") is { Length: > 0 } name ? name : UnitTypeId;
        if (Title.Length == 0) Title = "Initial unit (RAW)";
        Coordinates = "(" + Value("x") + ", " + Value("y") + ")";
        PendingLabel = removed ? "Removal pending" : spec is not null ? "Pending Save" : "";
        Summary = Value("type") + " · (" + Value("x") + ", " + Value("y") + ")" + (removed ? " · removal pending" : spec is not null ? " · pending" : "");
        ScopeLabel = spec?.DifficultyDefine ?? entry?.DifficultyDefine ?? "All difficulties";
        Problem = problem ?? (entry is { CanEdit: false } ? entry.Diagnostic : null);
        CanEdit = writable && problem is null && !removed && (entry is null || entry.CanEdit);
        CanRemove = writable && !removed && (entry is null || entry.CanRemove);
        CanFocus = !removed && int.TryParse(Value("x"), out _) && int.TryParse(Value("y"), out _);
    }
    internal string Identity { get; }
    internal WccScenarioUnitInstanceSnapshot Snapshot { get; }
    internal WccScenarioUnitInstanceEntry? Entry { get; }
    /// <summary>Immutable pending values for display. Source ownership remains private.</summary>
    public WccScenarioUnitInstanceSpec? Spec { get; }
    internal bool Removed { get; }
    public string Title { get; }
    public string UnitId { get; }
    public string UnitTypeId { get; }
    public string Coordinates { get; }
    public string PendingLabel { get; }
    // Captured catalog art is display only; the private entry/spec still owns edits.
    public CatalogPickerOptionVm? UnitTypeOption { get; internal set; }
    public string Summary { get; }
    public string ScopeLabel { get; }
    public bool CanEdit { get; }
    public bool CanRemove { get; }
    public bool CanFocus { get; }
    public string? Problem { get; }
    public string Details => (UnitId.Length == 0 ? UnitTypeId : UnitId + " · " + UnitTypeId) +
        "\n" + ScopeLabel + (Problem is null ? "" : "\n" + Problem);
}

/// <summary>A detached initial-unit form. Its callbacks stage intent; none grants write authority.</summary>
public sealed class WccInitialUnitEditDraft
{
    internal WccInitialUnitEditDraft(WccScenarioUnitInstanceSpec? spec, IReadOnlyDictionary<string, string> values, string? difficultyDefine, IReadOnlyList<CatalogPickerOptionVm> units,
        IReadOnlyList<UnitPlacementSideChoice> sides, UnitPlacementSideChoice side,
        IReadOnlyList<UnitPlacementScopeChoice> scopes, Func<string?> problem,
        Func<UnitPlacementResult, string?> validate, Func<UnitPlacementResult, bool> stage)
    { Spec = spec; Values = values; DifficultyDefine = difficultyDefine; UnitTypeOptions = units; SideChoices = sides; InitialSide = side; ScopeChoices = scopes;
        _problem = problem; _validate = validate; _stage = stage; }
    private readonly Func<string?> _problem;
    private readonly Func<UnitPlacementResult, string?> _validate;
    private readonly Func<UnitPlacementResult, bool> _stage;
    private bool _closed;
    internal string? ClosedProblem => _closed ? "This unit form is closed." : null;
    public WccScenarioUnitInstanceSpec? Spec { get; }
    public IReadOnlyDictionary<string, string> Values { get; }
    public bool? IsLoyal { get; init; }
    public Func<string, Task<IReadOnlyList<string>?>>? GenderResolver { get; init; }
    public WccCampaignPresetSelectionBinding? UnitPresetBinding { get; init; }
    public string? DifficultyDefine { get; }
    public IReadOnlyList<CatalogPickerOptionVm> UnitTypeOptions { get; }
    public IReadOnlyList<UnitPlacementSideChoice> SideChoices { get; }
    public UnitPlacementSideChoice InitialSide { get; }
    public IReadOnlyList<UnitPlacementScopeChoice> ScopeChoices { get; }
    public bool IsCurrent => !_closed && _problem() is null;
    public string? Validate(UnitPlacementResult result) => _closed ? "This unit form is closed." : _problem() ?? _validate(result);
    public bool TryStage(UnitPlacementResult result, out string? problem)
    { problem = Validate(result); if (problem is not null) return false;
        if (!_stage(result)) { problem = _problem() ?? "The unit could not be staged. Reopen its current form."; return false; }
        _closed = true; return true; }
    public void Cancel() => _closed = true;
}

public partial class ScenarioWindowViewModel
{
    internal Func<CatalogPickerOptionVm, Task<IReadOnlyList<string>?>>? InitialUnitGenderResolver { get; set; }
    private sealed record PendingInitialUnit(string Identity, WccScenarioUnitInstanceSnapshot Snapshot,
        WccScenarioUnitInstanceEntry? Entry, WccScenarioUnitInstanceSpec? Spec);
    private sealed record InitialUnitContribution(object Binding, long Version, IReadOnlyList<WccScenarioUnitInstanceEdit> Edits);
    private WccMusicScheduleSourceBinding? _initialUnitBinding;
    private IReadOnlyList<PendingInitialUnit> _initialUnitDrafts = [];
    private IReadOnlyList<WccInitialUnitRowVm> _initialUnitRows = [];
    private long _initialUnitDraftVersion;
    private bool _refreshingInitialUnits;
    private sealed record InitialUnitSnapshotCache(string Path, string Text, WmlStaticScalarProjection Projection,
        WmlCampaignEvaluationPlan? Plan, WmlStaticScalarOwner Owner, IReadOnlyList<WccCampaignLocalPresetEntry> Presets,
        IReadOnlyList<WccScenarioUnitInstanceSnapshot> Snapshots);
    private InitialUnitSnapshotCache? _initialUnitSnapshotCache;
    internal long WccInitialUnitDraftVersion => _initialUnitDraftVersion;
    public IReadOnlyList<WccInitialUnitRowVm> WccInitialUnitRows => _initialUnitRows;
    public bool HasWccInitialUnitDrafts => _initialUnitDrafts.Count != 0;
    public event EventHandler? WccInitialUnitDraftsChanged;
    public bool CanAddInitialUnits => CanStageInitialUnits && MapPreview is
        { IsCoordinateSelectionActive: false, Map: { HasErrors: false, PlayableWidth: > 0, PlayableHeight: > 0 } } &&
        CaptureInitialUnitSnapshots().Any(snapshot => snapshot.CanAdd);
    public string WccInitialUnitStatus => InitialUnitBindingProblem() ??
        (CanAddInitialUnits ? "Right-click a playable map hex to add an initial unit. Apply queues it for Save." :
         "Initial units need a current writable scenario, captured sides and map.");
    private string? InitialUnitBindingProblem() => _initialUnitBinding is { } binding
        ? GetWccMusicScheduleBindingProblem(binding)?.Replace("Music and schedule", "Initial unit", StringComparison.Ordinal)
            ?? (_collectedSaveRecoveryPending ? "A previous Save requires recovery. Initial unit drafts are retained." : null)
        : HasWccInitialUnitDrafts ? "The initial units have no captured source binding." : null;
    private bool CanStageInitialUnits => !IsSynchronizingUi && !_changingWccDifficultyDrafts && !_collectedSaveRecoveryPending &&
        _fieldDisplayDeferralDepth == 0 && !_flushingFieldDisplay && CanCaptureWccMusicScheduleSource() &&
        HasCurrentStaticScalarProjection && _staticScenarioOwner is not null && _staticScenarioTopologyProjection is not null &&
        _sourceEvaluationPlan is not null && InitialUnitBindingProblem() is null && _initialUnitDrafts.Count < 256;

    private IReadOnlyList<WccScenarioUnitInstanceSnapshot> CaptureInitialUnitSnapshots()
    {
        if (!HasCurrentStaticScalarProjection || _staticScenarioOwner is not { } scenario ||
            _staticScenarioTopologyProjection is null || _pristineLoadedSourceText is null || SelectedScenario is null) return [];
        var binding = _initialUnitBinding ?? CaptureEventSourceBinding();
        var presets = _campaignPresetHost?.AvailableCampaignPresets(WccCampaignLocalPresetRole.Unit) ?? [];
        if (_initialUnitSnapshotCache is { } cache && PathsEqual(cache.Path, binding.Range.Source.Path) && cache.Text == binding.Range.Source.Text &&
            ReferenceEquals(cache.Projection, _staticScenarioTopologyProjection) && ReferenceEquals(cache.Plan, _sourceEvaluationPlan) && ReferenceEquals(cache.Owner, scenario) &&
            SameInitialUnitPresets(cache.Presets, presets))
            return cache.Snapshots;
        var snapshots = scenario.DirectChildren.Where(side => side.TagName == "side" && ReferenceEquals(side.Parent, scenario))
            .Select(side => WccScenarioUnitInstancePlanner.Read(binding.Range.Source, side, _sourceEvaluationPlan, _staticScenarioTopologyProjection, presets))
            .ToArray();
        _initialUnitSnapshotCache = new(binding.Range.Source.Path, binding.Range.Source.Text, _staticScenarioTopologyProjection, _sourceEvaluationPlan, scenario, presets, Array.AsReadOnly(snapshots));
        return _initialUnitSnapshotCache.Snapshots;
    }

    private static bool SameInitialUnitPresets(IReadOnlyList<WccCampaignLocalPresetEntry> left,
        IReadOnlyList<WccCampaignLocalPresetEntry> right) => left.Count == right.Count &&
        left.Zip(right).All(pair => ReferenceEquals(pair.First, pair.Second));
    private void RefreshInitialUnitPresetCatalog()
    {
        var presets = _campaignPresetHost?.AvailableCampaignPresets(WccCampaignLocalPresetRole.Unit) ?? [];
        if (_initialUnitSnapshotCache is { } cache && SameInitialUnitPresets(cache.Presets, presets)) return;
        _initialUnitSnapshotCache = null; RefreshWccInitialUnitRows();
    }
    private void RefreshWccInitialUnitRows()
    {
        if (_refreshingInitialUnits) return;
        _refreshingInitialUnits = true;
        try
        {
            var writable = CanStageInitialUnits;
            var rows = new List<WccInitialUnitRowVm>();
            foreach (var snapshot in CaptureInitialUnitSnapshots())
            {
                foreach (var entry in snapshot.Entries)
                {
                    var identity = InitialUnitIdentity(entry);
                    var pending = _initialUnitDrafts.FirstOrDefault(draft => draft.Identity == identity);
                    rows.Add(new(identity, pending?.Snapshot ?? snapshot, pending?.Entry ?? entry, pending?.Spec,
                        pending is not null && pending.Spec is null, writable, InitialUnitBindingProblem()));
                }
            }
            foreach (var pending in _initialUnitDrafts.Where(draft => draft.Entry is null))
                rows.Add(new(pending.Identity, pending.Snapshot, null, pending.Spec, false, writable, InitialUnitBindingProblem()));
            if (rows.Count != 0)
            {
                var unitTypes = InitialUnitTypeOptions().ToDictionary(option => option.ValueId, StringComparer.Ordinal);
                foreach (var row in rows)
                    row.UnitTypeOption = unitTypes.GetValueOrDefault(row.UnitTypeId);
            }
            _initialUnitRows = Array.AsReadOnly(rows.ToArray());
            OnPropertyChanged(nameof(WccInitialUnitRows)); OnPropertyChanged(nameof(WccInitialUnitStatus));
            OnPropertyChanged(nameof(CanAddInitialUnits));
            RefreshInitialUnitDraftMapOverlays();
        }
        finally { _refreshingInitialUnits = false; }
    }
    private static string InitialUnitIdentity(WccScenarioUnitInstanceEntry entry) =>
        entry.Range.Source.Path + ":" + entry.Range.StartOffset + ":" + entry.Range.Length;

    private IReadOnlyList<CatalogPickerOptionVm> InitialUnitTypeOptions()
    {
        if (_macroLibrarySnapshot is not { } snapshot || _staticRootRevisionSelection is not { } root ||
            !ReferenceEquals(root.Snapshot, snapshot)) return [];
        var declared = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root.RootRevisionId);
        return Array.AsReadOnly(_unitCatalogOptions.Where(option => option.LibraryEntityId is { } id &&
            snapshot.EntitiesById.TryGetValue(id, out var entity) && snapshot.FindPackageForEntity(id) is { IsAvailable: true } package &&
            (WccLibraryRevisionVisibility.ContainsEntity(snapshot, declared, entity) ||
             package.SourceKind == WccPackageSourceKinds.WesnothCore &&
             (package.ContentKind == WccPackageContentKinds.Core || package.ContentKind == WccPackageContentKinds.ResourcePack) ||
             package.SourceKind == WccPackageSourceKinds.WesnothMainline && package.ContentKind == WccPackageContentKinds.ResourcePack))
            .GroupBy(option => option.ValueId, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray());
    }

    public WccInitialUnitEditDraft? BeginInitialUnitAdd(ScenarioMapPreviewVm preview, WesnothMapCell cell)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanAddInitialUnits || !ReferenceEquals(MapPreview, preview) || cell.IsBorder || preview.Map.HasErrors ||
                !ReferenceEquals(preview.Map.GetCell(cell.X, cell.Y), cell)) return null;
            var snapshots = CaptureInitialUnitSnapshots().Where(snapshot => snapshot.CanAdd).ToArray();
            if (snapshots.Length == 0) return null;
            IReadOnlyDictionary<string, string> values = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>
                { ["id"] = "", ["type"] = "", ["x"] = cell.X.ToString(CultureInfo.InvariantCulture), ["y"] = cell.Y.ToString(CultureInfo.InvariantCulture),
                  ["generate_name"] = "yes", ["random_traits"] = "yes", ["random_gender"] = "yes", ["upkeep"] = "full" });
            return CaptureInitialUnitForm(snapshots, null, null, values, preview);
        }
    }
    public WccInitialUnitEditDraft? BeginInitialUnitEdit(WccInitialUnitRowVm row)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanStageInitialUnits || !_initialUnitRows.Contains(row) || !row.CanEdit) return null;
            var values = row.Spec?.Values ?? row.Entry!.Values;
            var spec = row.Spec ?? (row.Entry?.Preset is { } preset ? new WccScenarioUnitInstanceSpec(values, row.Entry.DifficultyDefine)
                { Preset = preset, IsLoyal = row.Entry.IsLoyal } : null);
            return CaptureInitialUnitForm([row.Snapshot], row, spec, values, MapPreview);
        }
    }

    private WccInitialUnitEditDraft CaptureInitialUnitForm(IReadOnlyList<WccScenarioUnitInstanceSnapshot> snapshots,
        WccInitialUnitRowVm? row, WccScenarioUnitInstanceSpec? spec, IReadOnlyDictionary<string, string> values, ScenarioMapPreviewVm? preview)
    {
        InitializeDialogueGraphCurrency();
        var binding = _initialUnitBinding ?? CaptureEventSourceBinding();
        var version = _initialUnitDraftVersion; var host = _sourceHostGeneration; var projection = _staticInitialValuesGeneration;
        var sideVersion = _sideTopologyDraftVersion; var leaderVersion = _leaderAdditionVersion; var inlineVersion = _dialogueGraphInlineDraftVersion;
        var context = _evaluationContext.SemanticKey;
        WccInitialUnitEditDraft? form = null;
        var units = InitialUnitTypeOptions();
        var genderResolver = InitialUnitGenderResolver;
        var unitPresets = CapturePresetSelection(WccCampaignLocalPresetRole.Unit);
        var sides = Array.AsReadOnly(snapshots.Select((snapshot, index) => new UnitPlacementSideChoice(index.ToString(CultureInfo.InvariantCulture),
            "Side " + (WccScenarioFieldValueCodec.DecodeLiteral(snapshot.SideOwner.ResolveField("side").ValueText ?? "") ?? "?") )).ToArray());
        var initialSide = sides.FirstOrDefault(choice => ReferenceEquals(snapshots[int.Parse(choice.Key, CultureInfo.InvariantCulture)].SideOwner, SelectedStaticSideOwner)) ?? sides[0];
        var scopes = Array.AsReadOnly(new[] { new UnitPlacementScopeChoice(null, "All difficulties") }.Concat(binding.Plan!.Contexts
            .Where(slot => !string.IsNullOrWhiteSpace(slot.DifficultyDefine)).Select(slot => new UnitPlacementScopeChoice(slot.DifficultyDefine, slot.DisplayName)))
            .DistinctBy(choice => choice.Define).ToArray());
        string? CurrentProblem()
        {
            lock (_wccInitialFieldRefreshGate)
            {
                if (form?.ClosedProblem is { } closed) return closed;
                if (version != _initialUnitDraftVersion || host != _sourceHostGeneration || projection != _staticInitialValuesGeneration ||
                    sideVersion != _sideTopologyDraftVersion || leaderVersion != _leaderAdditionVersion || inlineVersion != _dialogueGraphInlineDraftVersion ||
                    context != _evaluationContext.SemanticKey || !ReferenceEquals(preview, MapPreview))
                    return "The scenario, map, difficulty or pending units changed. Reopen the unit form.";
                return GetWccMusicScheduleBindingProblem(binding) ??
                    (preview?.IsCoordinateSelectionActive == true || !CanStageInitialUnits ? "Finish the current scenario operation before applying units." : null);
            }
        }
        string? Validate(UnitPlacementResult result)
        {
            if (CurrentProblem() is { } current) return current;
            if (result.Spec.Preset is { } preset && unitPresets?.ContainsCurrent(preset) != true) return "The unit preset or campaign changed; reopen placement.";
            if (!sides.Any(choice => ReferenceEquals(choice, result.Side))) return "Choose one of this form's captured sides.";
            if (!units.Any(option => option.ValueId == result.Spec.Values.GetValueOrDefault("type"))) return "Choose a Core or current campaign unit type.";
            if (!scopes.Any(scope => scope.Define == result.Spec.DifficultyDefine)) return "Choose all difficulties or one declared difficulty.";
            if (preview is null || !int.TryParse(result.Spec.Values.GetValueOrDefault("x"), NumberStyles.None, CultureInfo.InvariantCulture, out var x) ||
                !int.TryParse(result.Spec.Values.GetValueOrDefault("y"), NumberStyles.None, CultureInfo.InvariantCulture, out var y) || preview.Map.GetCell(x, y) is not { IsBorder: false })
                return "Choose complete coordinates on a playable map hex.";
            var id = result.Spec.Values.GetValueOrDefault("id");
            if (id is { Length: > 0 } && (id != id.Trim() || id.Any(char.IsControl))) return "Enter a literal unit ID without surrounding whitespace, or leave it empty.";
            if (id is { Length: > 0 } && InitialUnitIdInUse(id, row?.Identity, result.Spec.DifficultyDefine)) return "Another initial unit or leader uses that ID in this difficulty.";
            try
            {
                var snapshot = snapshots[int.Parse(result.Side.Key, CultureInfo.InvariantCulture)];
                if (row?.Entry is { } entry) WccScenarioUnitInstancePlanner.CreateReplace(snapshot, entry, result.Spec);
                else WccScenarioUnitInstancePlanner.CreateAdd(snapshot, result.Spec);
                return null;
            }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { return refusal.Message; }
        }
        bool Stage(UnitPlacementResult result)
        {
            lock (_wccInitialFieldRefreshGate)
            {
                if (Validate(result) is { } problem) { Status = problem; return false; }
                if (CurrentProblem() is not null) return false;
                WccFieldEditSession.Invalidate();
                if (CurrentProblem() is not null) return false;
                var snapshot = snapshots[int.Parse(result.Side.Key, CultureInfo.InvariantCulture)];
                var identity = row?.Identity ?? Guid.NewGuid().ToString("N");
                _initialUnitBinding = binding;
                _initialUnitDrafts = Array.AsReadOnly(_initialUnitDrafts.Where(draft => draft.Identity != identity)
                    .Append(new PendingInitialUnit(identity, snapshot, row?.Entry, result.Spec)).ToArray());
                _initialUnitDraftVersion++;
                RaiseInitialUnitDraftsChanged(); Status = "Initial unit staged. Save checks all difficulties and writes the unit.";
                return true;
            }
        }
        form = new(spec, values, spec?.DifficultyDefine ?? row?.Entry?.DifficultyDefine, units, sides, initialSide, scopes, CurrentProblem, Validate, Stage)
        {
            IsLoyal = row is null ? false : spec is not null ? spec.IsLoyal : row.Entry?.IsLoyal,
            UnitPresetBinding = row?.Entry?.Preset is { } savedPreset && unitPresets is not null
                ? new WccCampaignPresetSelectionBinding(WccCampaignLocalPresetRole.Unit,
                    unitPresets.Entries.Where(entry => ReferenceEquals(entry, savedPreset)), () => unitPresets.IsCurrent)
                : row is null || spec?.Preset is not null ? unitPresets : null,
            GenderResolver = async type =>
            {
                var option = units.SingleOrDefault(unit => unit.ValueId == type);
                if (option is null || genderResolver is null || CurrentProblem() is not null) return null;
                var genders = await genderResolver(option);
                return CurrentProblem() is null ? genders : null;
            }
        };
        return form;
    }

    private bool InitialUnitIdInUse(string id, string? except, string? scope)
    {
        bool Overlaps(string? other) => scope is null || other is null || scope == other;
        if (_initialUnitRows.Any(row => row.Identity != except && !row.Removed &&
            (row.Spec?.Values ?? row.Entry?.Values)?.GetValueOrDefault("id") == id && Overlaps(row.Spec?.DifficultyDefine ?? row.Entry?.DifficultyDefine))) return true;
        if (_staticScenarioOwner?.DirectChildren.Where(side => side.TagName == "side").Any(side =>
            DialogueLiteral(side, "id") == id || side.DirectChildren.Where(child => child.TagName == "leader").Any(leader => DialogueLiteral(leader, "id") == id)) == true) return true;
        return _pendingLeaders.Any(leader => leader.Fields.TryGetValue("id", out var field) && field.Text == id);
    }

    public bool TryRemoveInitialUnit(WccInitialUnitRowVm row, out string? problem)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            problem = InitialUnitBindingProblem();
            if (problem is not null || !CanStageInitialUnits || !_initialUnitRows.Contains(row) || !row.CanRemove)
            { problem ??= "Select a current editable initial unit before removing it."; return false; }
            var binding = _initialUnitBinding ?? CaptureEventSourceBinding();
            var version = _initialUnitDraftVersion;
            var generation = _staticInitialValuesGeneration;
            var host = _sourceHostGeneration;
            if (row.Entry is { } entry)
            {
                try { WccScenarioUnitInstancePlanner.CreateRemove(row.Snapshot, entry); }
                catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { problem = refusal.Message; return false; }
            }
            bool IsCurrent() => version == _initialUnitDraftVersion && generation == _staticInitialValuesGeneration && host == _sourceHostGeneration &&
                CanStageInitialUnits && GetWccMusicScheduleBindingProblem(binding) is null;
            if (!IsCurrent()) { problem = "The selected unit changed while removing it."; return false; }
            WccFieldEditSession.Invalidate();
            if (!IsCurrent())
            { problem = "The selected unit changed while removing it."; return false; }
            _initialUnitDrafts = Array.AsReadOnly(_initialUnitDrafts.Where(draft => draft.Identity != row.Identity)
                .Concat(row.Entry is null ? [] : new[] { new PendingInitialUnit(row.Identity, row.Snapshot, row.Entry, null) }).ToArray());
            _initialUnitBinding = _initialUnitDrafts.Count == 0 ? null : binding;
            _initialUnitDraftVersion++; RaiseInitialUnitDraftsChanged(); return true;
        }
    }
    public void FocusInitialUnit(WccInitialUnitRowVm row)
    {
        if (!_initialUnitRows.Contains(row) || !row.CanFocus || MapPreview is not { } preview) return;
        var values = row.Spec?.Values ?? row.Entry!.Values;
        if (int.TryParse(values.GetValueOrDefault("x"), out var x) && int.TryParse(values.GetValueOrDefault("y"), out var y) && preview.Map.GetCell(x, y) is { } cell)
            preview.RequestFocusCell(cell);
    }
    internal IEnumerable<string> GetWccInitialUnitSpeakerIds()
    {
        var scope = _evaluationContext.DifficultyDefine;
        return _initialUnitRows.Where(row => !row.Removed && (row.Spec?.DifficultyDefine ?? row.Entry?.DifficultyDefine) is var define &&
                (define is null || define == scope)).Select(row => (row.Spec?.Values ?? row.Entry?.Values)?.GetValueOrDefault("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id) && id == id.Trim() &&
                WccScenarioFieldValueCodec.InferInputKind(id) == WccScenarioFieldValueInputKind.Literal).Select(id => id!).Distinct(StringComparer.Ordinal);
    }
    private void RefreshInitialUnitDraftMapOverlays()
    {
        if (MapPreview is not { } preview) return;
        var scope = _evaluationContext.DifficultyDefine;
        var overlays = new List<ScenarioMapOverlayVm>();
        foreach (var draft in _initialUnitDrafts.Where(draft => draft.Spec is not null && (draft.Spec.DifficultyDefine is null || draft.Spec.DifficultyDefine == scope)))
        {
            var values = draft.Spec!.Values;
            if (int.TryParse(values.GetValueOrDefault("x"), out var x) && int.TryParse(values.GetValueOrDefault("y"), out var y))
                overlays.Add(new(ScenarioMapOverlayKind.Unit, x, y, "+", (values.GetValueOrDefault("name") ?? values.GetValueOrDefault("id") ?? "Unit") + " (pending Save)"));
        }
        preview.SetInitialUnitDraftOverlays(Array.AsReadOnly(overlays.ToArray()));
    }

    public void RevertWccInitialUnitDraft(string identity)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy || _changingWccDifficultyDrafts) return;
            _initialUnitDrafts = Array.AsReadOnly(_initialUnitDrafts.Where(draft => draft.Identity != identity).ToArray());
            if (!HasWccInitialUnitDrafts) _initialUnitBinding = null;
            _initialUnitDraftVersion++; RaiseInitialUnitDraftsChanged();
        }
    }
    public void DiscardWccInitialUnitDrafts()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy || _changingWccDifficultyDrafts) return;
            ClearInitialUnitDrafts(); RaiseInitialUnitDraftsChanged();
        }
    }
    private void ClearInitialUnitDrafts() { _initialUnitDrafts = []; _initialUnitBinding = null; _initialUnitDraftVersion++; }
    private void RaiseInitialUnitDraftsChanged()
    {
        CollectedSaveAuthorityChanging?.Invoke();
        OnPropertyChanged(nameof(HasWccInitialUnitDrafts)); RaiseEditingStateChanged(); RaiseInitialScalarFieldsChanged();
        RefreshWccInitialUnitRows(); WccInitialUnitDraftsChanged?.Invoke(this, EventArgs.Empty);
    }
    private bool TryCaptureInitialUnitContribution(WmlStaticSource source, out InitialUnitContribution? contribution, out string? problem)
    {
        contribution = null; problem = InitialUnitBindingProblem();
        if (problem is not null || _initialUnitBinding is not { } binding || !SameAuthoredFlagSource(source, binding.Range.Source))
        { problem ??= "The collected source differs from the initial unit source."; return false; }
        try
        {
            var presets = _campaignPresetHost?.AvailableCampaignPresets(WccCampaignLocalPresetRole.Unit) ?? [];
            var edits = _initialUnitDrafts.Select(draft =>
            {
                var snapshot = WccScenarioUnitInstancePlanner.Read(source, draft.Snapshot.SideOwner, binding.Plan, _staticScenarioTopologyProjection, presets);
                if (draft.Entry is null) return WccScenarioUnitInstancePlanner.CreateAdd(snapshot, draft.Spec!);
                var entry = snapshot.Entries.Single(entry => entry.Range.StartOffset == draft.Entry.Range.StartOffset &&
                    entry.Range.Length == draft.Entry.Range.Length && entry.Range.Text == draft.Entry.Range.Text && entry.DifficultyDefine == draft.Entry.DifficultyDefine);
                return draft.Spec is null ? WccScenarioUnitInstancePlanner.CreateRemove(snapshot, entry)
                    : WccScenarioUnitInstancePlanner.CreateReplace(snapshot, entry, draft.Spec);
            }).ToArray();
            contribution = new(binding, _initialUnitDraftVersion, Array.AsReadOnly(edits)); return true;
        }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { problem = refusal.Message; return false; }
    }
    private bool IsCurrentInitialUnitContribution(InitialUnitContribution contribution) => HasWccInitialUnitDrafts &&
        ReferenceEquals(contribution.Binding, _initialUnitBinding) && contribution.Version == _initialUnitDraftVersion && InitialUnitBindingProblem() is null;
    private bool CompleteInitialUnitContribution(InitialUnitContribution contribution)
    { if (!IsCurrentInitialUnitContribution(contribution)) return false; ClearInitialUnitDrafts(); RaiseInitialUnitDraftsChanged(); return true; }
}
