using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>Immutable questionnaire result; the campaign host owns staging and checked Save.</summary>
public sealed class ScenarioCreationRequest
{
    internal ScenarioCreationRequest(WccScenarioCreationSpec spec, bool setFirstScenario,
        Func<bool> current, Func<bool> sameContext)
    { Spec = spec; SetFirstScenario = setFirstScenario; IsCurrent = current; IsSameContext = sameContext; }
    public WccScenarioCreationSpec Spec { get; }
    public bool SetFirstScenario { get; }
    internal Func<bool> IsCurrent { get; }
    internal Func<bool> IsSameContext { get; }
    internal Func<bool>? IsSavedContext { get; init; }
}

/// <summary>Detached form: opening or changing this wizard never creates files.</summary>
public sealed partial class ScenarioCreationViewModel : ViewModelBase, IDisposable
{
    private readonly WccScenarioCreationSnapshot _snapshot;
    private readonly Func<bool> _current;
    private readonly Func<bool> _sameContext;
    internal Func<bool>? SavedContext { get; init; }
    private readonly INotifyPropertyChanged _host;
    private string _id = "";
    private string _name = "";
    private string _fileNameStem = "";
    private string _turns = "20";
    private bool _setFirstScenario;
    private bool _disposed;
    private int _step;
    private WccScenarioCreationMode _mode = WccScenarioCreationMode.Guided;
    private string? _validation;
    private readonly ScenarioCreationWizardSetup? _setup;
    private readonly List<ScenarioCreationSideViewModel> _retainedSides = [];
    private string _sideCount = "2";
    private bool _renameFile;
    private bool _interactionBusy;
    private long _draftVersion;

    internal ScenarioCreationViewModel(WccScenarioCreationSnapshot snapshot, bool canSetFirstScenario,
        Func<bool> current, Func<bool> sameContext, INotifyPropertyChanged host)
        : this(snapshot, canSetFirstScenario, current, sameContext, host, null) { }
    internal ScenarioCreationViewModel(WccScenarioCreationSnapshot snapshot, bool canSetFirstScenario,
        Func<bool> current, Func<bool> sameContext, INotifyPropertyChanged host, ScenarioCreationWizardSetup? setup)
    {
        _snapshot = snapshot; CanSetFirstScenario = canSetFirstScenario;
        _current = current; _sameContext = sameContext; _host = host;
        _setup = setup;
        if (setup is not null)
        {
            TurnsFields = new(setup.Plan, setup.Units, () => !_disposed && _current(), setup.Preview);
            TurnsField = TurnsFields.ScenarioFields.Single(field => field.Key == "turns");
            TurnsField.Value = _turns;
            TurnsFields.Changed += SideChanged;
            EnsureSides(2);
            _map = setup.DefaultMap ?? new("starter.map", WccScenarioCreationWriter.DefaultMap, true);
            _generatedMap = setup.DefaultMap is null;
            _schedule = setup.Schedules.FirstOrDefault(choice => choice.Invocation == "{DEFAULT_SCHEDULE}") ?? setup.Schedules.FirstOrDefault();
            ResetScheduleRows();
        }
        _host.PropertyChanged += HostChanged;
        RefreshState();
    }
    public string Id { get => _id; set { if (SetField(ref _id, value ?? "")) DraftChanged(); } }
    public string Name { get => _name; set { if (SetField(ref _name, value ?? "")) DraftChanged(); } }
    public string FileNameStem
    {
        get => _fileNameStem;
        set { if (SetField(ref _fileNameStem, value ?? "")) { if (_fileNameStem.Length != 0) _renameFile = true; DraftChanged(); } }
    }
    public bool RenameScenarioFile { get => _renameFile; set { if (SetField(ref _renameFile, value)) DraftChanged(); } }
    public CampaignPresetFieldSession? TurnsFields { get; }
    public WccInitialFieldEditorVm? TurnsField { get; }
    public string Turns
    {
        get => TurnsField?.Value ?? _turns;
        set { if (TurnsField is not null) TurnsField.Value = value ?? ""; else if (SetField(ref _turns, value ?? "")) DraftChanged(); }
    }
    public string SideCount
    {
        get => _sideCount;
        set
        {
            if (!SetField(ref _sideCount, value ?? "")) return;
            if (TrySideCount(out var count))
            {
                EnsureSides(count);
                if (_generatedMap) _map = new("starter.map", WccScenarioCreationWriter.RenderDefaultMap(count), true);
                _step = Math.Min(_step, 2 + count);
            }
            DraftChanged();
        }
    }
    public WccScenarioCreationMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _mode, value)) { _step = Math.Min(_step, 1); DraftChanged(); }
        }
    }
    public bool IsEmptyMode { get => Mode == WccScenarioCreationMode.Empty; set { if (value) Mode = WccScenarioCreationMode.Empty; } }
    public bool IsGuidedMode { get => Mode == WccScenarioCreationMode.Guided; set { if (value) Mode = WccScenarioCreationMode.Guided; } }
    public bool SetFirstScenario { get => _setFirstScenario; set { if (SetField(ref _setFirstScenario, value && CanSetFirstScenario)) DraftChanged(); } }
    public bool CanSetFirstScenario { get; }
    public bool IsModePage => _step == 0;
    public bool IsDetailsPage => _step == 1;
    public bool HasGuidedPages => IsGuidedMode && _setup is not null;
    public bool IsMusicSchedulePage => HasGuidedPages && _step == 2;
    public bool IsSidePage => HasGuidedPages && _step >= 3;
    public ScenarioCreationSideViewModel? ActiveSide => IsSidePage && TrySideCount(out var count) && _step - 3 < count
        ? _retainedSides[_step - 3] : null;
    private int LastStep => HasGuidedPages && TrySideCount(out var count) ? 2 + count : 1;
    public bool IsFinalPage => _step == LastStep;
    public bool CanInteract => !_disposed && _current() && !_interactionBusy;
    public bool CanGoBack => _step > 0 && CanInteract;
    public bool CanGoNext => _step < LastStep && CanInteract && (IsModePage || _validation is null);
    public bool CanCreate => IsFinalPage && CanInteract && _validation is null;
    public bool HasValidation => !IsModePage && _validation is not null;
    public string? Validation => _validation;
    public string StepTitle => IsModePage ? "Choose a starting point" : IsDetailsPage ? "Scenario essentials" :
        IsMusicSchedulePage ? "Music and schedule" : ActiveSide?.Heading ?? "Side settings";
    public string StepSummary => "Step " + (_step + 1) + " of " + (LastStep + 1);
    internal string ScenarioDirectory { get; init; } = "scenarios";
    private string EffectiveStem => RenameScenarioFile && FileNameStem.Length != 0 ? FileNameStem : Id;
    public string FilesSummary => ScenarioDirectory + "/" + EffectiveStem + ".cfg" +
        (IsEmptyMode ? "\nNo map or sides yet; complete the scaffold in the editor." : _setup is null ? "\nmaps/" + EffectiveStem + ".map" :
            "\nmaps/" + MapOutputName);
    public string FirstScenarioGuidance => CanSetFirstScenario
        ? "Optional: use this scenario as the campaign's starting scenario."
        : "The existing first scenario stays selected. Connect scenario flow in the editor or RAW WML.";
    public bool MoveNext()
    {
        if (!CanGoNext) return false;
        _step++; InvalidateCoordinatePicks(); RefreshState(); return true;
    }
    public void MoveBack() { if (CanGoBack) { _step--; InvalidateCoordinatePicks(); RefreshState(); } }
    public bool TryCreateRequest(out ScenarioCreationRequest? request)
    {
        RefreshState(); request = null;
        if (!CanCreate || BuildSpec() is not { } spec) return false;
        request = new(spec, SetFirstScenario, _current, _sameContext) { IsSavedContext = SavedContext }; return true;
    }
    private string? ValidateEssentials()
    {
        if (HasGuidedPages && !TrySideCount(out _)) return "Choose between 1 and 32 sides.";
        if (HasGuidedPages)
        {
            try { _ = TurnsFields!.CaptureDifficultyValues("turns"); }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { return refusal.Message; }
        }
        if (IsGuidedMode && !int.TryParse(Turns, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return "Turns must be a whole number; use -1 for unlimited turns.";
        var basic = new WccScenarioCreationSpec(Mode, Id, IsEmptyMode ? "" : Name,
            RenameScenarioFile && FileNameStem.Length != 0 ? FileNameStem : null,
            IsEmptyMode ? 20 : int.Parse(Turns, CultureInfo.InvariantCulture));
        try { WccScenarioCreationPlanner.CreateEdit(_snapshot, basic); return null; }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { return refusal.Message; }
    }
    private WccScenarioCreationSpec? BuildSpec()
    {
        var turns = 20;
        if (IsGuidedMode && !int.TryParse(Turns, NumberStyles.Integer, CultureInfo.InvariantCulture, out turns))
        { _validation = "Turns must be a whole number; use -1 for unlimited turns."; return null; }
        var spec = new WccScenarioCreationSpec(Mode, Id, IsEmptyMode ? "" : Name,
            RenameScenarioFile && FileNameStem.Length != 0 ? FileNameStem : null, IsEmptyMode ? 20 : turns);
        try
        {
            if (HasGuidedPages)
            {
                if (!TrySideCount(out var count)) throw new ArgumentException("Choose between 1 and 32 sides.");
                spec = spec with { Guided = new(Array.AsReadOnly(_retainedSides.Take(count).Select(side => side.BuildSide()).ToArray()),
                    _generatedMap ? null : _map, BuildMusic(), BuildSchedule())
                    { TurnsDifficultyValues = TurnsFields!.CaptureDifficultyValues("turns") } };
            }
            var edit = WccScenarioCreationPlanner.CreateEdit(_snapshot, spec);
            // The same pure writer checks resource composition before the questionnaire closes.
            _ = WccScenarioCreationPlanner.PlanSources(edit);
            return edit.Spec;
        }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
        { _validation = refusal.Message; return null; }
    }
    private bool TrySideCount(out int count) => int.TryParse(SideCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count is >= 1 and <= 32;
    private void EnsureSides(int count)
    {
        if (_setup is null) return;
        while (_retainedSides.Count < count)
        {
            var side = new ScenarioCreationSideViewModel(_retainedSides.Count + 1, _setup, () => !_disposed && _current());
            // Local X/Y overrides can be editable while the inherited leader's
            // shared fields/topology stay locked. The exact field-session policy
            // decides coordinate authority; this provider only pins the active page.
            side.SetCoordinateMapProvider(() => ReferenceEquals(ActiveSide, side) && !_disposed && _current()
                ? CaptureCoordinateMap() : null);
            side.Changed += SideChanged; _retainedSides.Add(side);
        }
    }
    private void SideChanged(object? sender, EventArgs args) => DraftChanged();
    private void InvalidateCoordinatePicks()
    { foreach (var side in _retainedSides) side.InvalidateCoordinatePicker(); }
    private void DraftChanged() { _draftVersion++; InvalidateCoordinatePicks(); _interactionProblem = null; RefreshState(); }
    private void HostChanged(object? sender, PropertyChangedEventArgs args) => DraftChanged();
    private void RefreshState()
    {
        _validation = _disposed || !_current() ? "The campaign source or editing context changed. Reopen this wizard; current drafts remain." : null;
        if (_validation is null) _validation = ValidateEssentials();
        if (_validation is null) _validation = _interactionProblem;
        if (_validation is null && IsMusicSchedulePage)
        {
            if (_customSchedule && ScheduleTimes.Count == 0) _validation = "A custom cycle needs at least one time entry.";
            else if (MusicTracks.Any(track => string.IsNullOrWhiteSpace(track.Name))) _validation = "Enter a filename for every music entry or remove the empty entry.";
            else if ((_intro || _finish) && MusicTracks.Count < (_intro ? 2 : 1)) _validation = "Intro music needs an intro track and at least one scenario track.";
        }
        if (_validation is null && IsSidePage && ActiveSide is { } side)
        {
            try { WccScenarioCreationPlanner.ValidateSide(side.BuildSide(), side.Number, _setup!.Plan, CoordinateMapText); }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { _validation = refusal.Message; }
        }
        if (_validation is null && IsFinalPage) BuildSpec();
        foreach (var property in new[] { nameof(IsEmptyMode), nameof(IsGuidedMode), nameof(IsModePage), nameof(IsDetailsPage),
            nameof(CanGoBack), nameof(CanGoNext), nameof(CanCreate), nameof(HasValidation), nameof(Validation),
            nameof(StepTitle), nameof(StepSummary), nameof(FilesSummary), nameof(HasGuidedPages), nameof(IsMusicSchedulePage), nameof(IsSidePage),
            nameof(ActiveSide), nameof(IsFinalPage), nameof(CanInteract), nameof(RenameScenarioFile), nameof(MapSummary), nameof(HasMap),
            nameof(RenameMapToScenario), nameof(CanPickSidePreset), nameof(CanPickLeaderPreset), nameof(ScheduleLabel), nameof(IsCustomSchedule),
            nameof(CanEditCustomSchedule), nameof(SideCount), nameof(Turns) }) OnPropertyChanged(property);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _host.PropertyChanged -= HostChanged;
        if (TurnsFields is not null) { TurnsFields.Changed -= SideChanged; TurnsFields.Dispose(); }
        foreach (var side in _retainedSides) { side.Changed -= SideChanged; side.Dispose(); }
        foreach (var row in MusicTracks) row.PropertyChanged -= MusicChanged;
        _setup?.Dispose();
    }
}
