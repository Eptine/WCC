using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed record EventDialogueTriggerChoice(WccScenarioEventTrigger Value, string Label);

/// <summary>A detached ordered event draft. Geometry never grants WML or Save authority.</summary>
public sealed partial class EventNodeGraphWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ObservableCollection<EventDialogueNodeVm> _nodes = [];
    private readonly List<EventDialogueNodeVm> _chain = [];
    private readonly Func<bool> _isCurrent;
    private readonly Func<string, string?>? _validate;
    private readonly WccScenarioEventGraphSeed? _seed;
    private bool _disposed;
    private long _semanticVersion;
    private long _selectionVersion;
    private long _localValidationVersion = -1;
    private string? _localValidationProblem;
    private WccScenarioEventTrigger _trigger;
    private bool _firstTimeOnly;
    private string? _eventId;
    private string? _turnNumber;
    private string? _filterUnitId;
    private string? _filterX;
    private string? _filterY;
    private double _zoom = 1;
    private bool _showPalette = true;
    private bool _pickingCoordinates;
    private long _coordinatePickerVersion;
    private Func<string?, string?, Task<(int X, int Y)?>>? _coordinatePicker;
    private EventDialogueNodeVm _selectedNode;
    private EventDialogueNodeVm? _connectionSource;
    private EventDialogueNodeVm? _selectedAfter;
    private string? _status;
    private IReadOnlyList<EventDialogueNodeVm> _connectionTargets = [];

    public EventNodeGraphWindowViewModel(WccScenarioEventGraph graph, IReadOnlyList<string> speakerIds,
        Func<bool>? isCurrent = null, Func<string, string?>? validate = null, WccScenarioEventGraphSeed? seed = null,
        IReadOnlyList<string>? declaredDifficultyDefines = null, IReadOnlyList<WccCampaignLocalPresetEntry>? knownUnitPresets = null)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(speakerIds);
        _isCurrent = isCurrent ?? (() => true); _validate = validate; _seed = seed;
        _knownUnitPresets = Array.AsReadOnly((knownUnitPresets ?? []).ToArray());
        _trigger = graph.Trigger; _firstTimeOnly = graph.FirstTimeOnly; _eventId = graph.EventId;
        _turnNumber = graph.TurnNumber?.ToString(CultureInfo.InvariantCulture);
        _filterUnitId = graph.FilterUnitId; _filterX = graph.FilterX; _filterY = graph.FilterY;
        _hostUnitChoices = Array.AsReadOnly(speakerIds.Where(id => !string.IsNullOrWhiteSpace(id) && id != "narrator")
            .Distinct(StringComparer.Ordinal).ToArray());
        UnitChoices = _hostUnitChoices;
        SpeakerChoices = Array.AsReadOnly(new[] { "narrator" }.Concat(UnitChoices).ToArray());
        TriggerChoices = Array.AsReadOnly(new[]
        {
            new EventDialogueTriggerChoice(WccScenarioEventTrigger.Unselected, "Select trigger"),
            new EventDialogueTriggerChoice(WccScenarioEventTrigger.Start, "Scenario start"),
            new EventDialogueTriggerChoice(WccScenarioEventTrigger.Prestart, "Before scenario display"),
            new EventDialogueTriggerChoice(WccScenarioEventTrigger.Victory, "Victory"),
            new EventDialogueTriggerChoice(WccScenarioEventTrigger.Turn, "Turn number"),
            new EventDialogueTriggerChoice(WccScenarioEventTrigger.Die, "Unit dies"),
            new EventDialogueTriggerChoice(WccScenarioEventTrigger.MoveTo, "Unit enters location")
        });
        TriggerNode = new(this, null); _nodes.Add(TriggerNode);
        foreach (var action in graph.Actions)
        { var node = new EventDialogueNodeVm(this, action); _nodes.Add(node); _chain.Add(node); }
        Nodes = new(_nodes); _selectedNode = TriggerNode; RefreshUnitChoices();
        InitializeDifficultyRegions(graph, declaredDifficultyDefines); ArrangeNodes(); RefreshTargets();
    }
    public ReadOnlyObservableCollection<EventDialogueNodeVm> Nodes { get; }
    public EventDialogueNodeVm TriggerNode { get; }
    public IReadOnlyList<string> SpeakerChoices { get; private set; }
    public IReadOnlyList<string> UnitChoices { get; private set; }
    public IReadOnlyList<EventDialogueTriggerChoice> TriggerChoices { get; }
    public IReadOnlyList<EventDialogueNodeVm> ConnectedActions => Array.AsReadOnly(_chain.ToArray());
    public IReadOnlyList<EventDialogueNodeVm> ConnectionTargets => _connectionTargets;
    public bool CanEdit => !_disposed && _isCurrent();
    public bool CanApply => LocalProblem is null;
    public bool CanRemoveSelected => CanEdit && (SelectedRegion is not null || !SelectedNode.IsTrigger);
    public bool CanConnectSelected => CanEdit && SelectedRegion is null && !SelectedNode.IsTrigger && SelectedAfter is { } from && _connectionTargets.Contains(from);
    public bool CanDisconnectSelected => CanEdit && SelectedRegion is null && !SelectedNode.IsTrigger && _chain.Contains(SelectedNode);
    public bool HasPendingConnection => _connectionSource is not null;
    public EventDialogueNodeVm? ConnectionSource => _connectionSource;
    public double CanvasWidth => Math.Max(1100, Math.Max(_nodes.Max(node => node.X) + 220, _regions.Select(region => region.X + region.Width + 40).DefaultIfEmpty(0).Max()));
    public double CanvasHeight => Math.Max(650, Math.Max(_nodes.Max(node => node.Y) + 160, _regions.Select(region => region.Y + region.Height + 40).DefaultIfEmpty(0).Max()));
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (!double.IsFinite(value) || !SetField(ref _zoom, Math.Clamp(value, 0.5, 2))) return;
            OnPropertyChanged(nameof(ZoomLabel)); GeometryChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string ZoomLabel => _zoom.ToString("0%", CultureInfo.InvariantCulture);
    public bool ShowPalette { get => _showPalette; set => SetField(ref _showPalette, value); }
    public string Status => _status ?? LocalProblem ?? "Connected actions run in numbered order. Apply queues this event; Save writes the scenario.";
    public string ConnectionHint => _connectionSource is { } from
        ? "Choose the input dot of the action that should follow “" + from.Label + "”."
        : "Connect an output arrow to an action input dot. Drag cards to arrange; Ctrl+wheel zooms.";
    public EventDialogueTriggerChoice SelectedTrigger
    {
        get => TriggerChoices.FirstOrDefault(choice => choice.Value == _trigger) ?? TriggerChoices[0];
        set { if (CanEdit && value is not null && TriggerChoices.Contains(value) && _trigger != value.Value) { _trigger = value.Value; Changed(); } }
    }
    public bool FirstTimeOnly { get => _firstTimeOnly; set { if (CanEdit && SetField(ref _firstTimeOnly, value)) Changed(); } }
    public bool CanEditEventId => CanEdit && _seed is null;
    public string? EventId { get => _eventId; set { if (CanEditEventId && SetField(ref _eventId, value)) Changed(); } }
    public string EventIdHint => _seed is null ? "Optional native event ID. Internal spaces are allowed; IDs are matched exactly. Leave blank for an unnamed event."
        : "The captured event ID is preserved. Use RAW source to change it.";
    public bool ShowsTurnNumber => _trigger == WccScenarioEventTrigger.Turn;
    public bool ShowsFilterUnit => _trigger is WccScenarioEventTrigger.Die or WccScenarioEventTrigger.MoveTo;
    public bool ShowsFilterCoordinates => _trigger == WccScenarioEventTrigger.MoveTo;
    public string? TurnNumber { get => _turnNumber; set { if (CanEdit && SetField(ref _turnNumber, value)) Changed(); } }
    public string? FilterUnitId { get => _filterUnitId; set { if (CanEdit && SetField(ref _filterUnitId, value)) Changed(); } }
    public string? FilterX { get => _filterX; set { if (CanEdit && SetField(ref _filterX, value)) Changed(); } }
    public string? FilterY { get => _filterY; set { if (CanEdit && SetField(ref _filterY, value)) Changed(); } }
    public string TriggerHint => _trigger switch
    {
        WccScenarioEventTrigger.Unselected => "Choose when this event should run.",
        WccScenarioEventTrigger.Prestart => "Runs before the scenario map is displayed. Use Scenario start for an opening dialogue.",
        WccScenarioEventTrigger.Victory => "Runs when Wesnoth has already decided the player won.",
        WccScenarioEventTrigger.Turn => "Runs at the beginning of this turn. Enter one positive whole turn number.",
        WccScenarioEventTrigger.Die => "Runs when the unit with this exact ID dies. The dying unit is still available to the event.",
        WccScenarioEventTrigger.MoveTo => "Runs after a unit enters any listed location. X and Y are paired: X=1,2 and Y=3,4 means (1,3) or (2,4). Ranges are also supported; leave unit ID blank for any unit.",
        _ => "Runs after the scenario starts. The trigger connects to the first action."
    };
    public Func<string?, string?, Task<(int X, int Y)?>>? CoordinatePicker
    {
        get => _coordinatePicker;
        set
        {
            if (Equals(_coordinatePicker, value)) return;
            _coordinatePicker = value; _coordinatePickerVersion++;
            OnPropertyChanged(nameof(CoordinatePicker)); OnPropertyChanged(nameof(CanPickSelectedCoordinates)); OnPropertyChanged(nameof(CanAddFilterLocation));
        }
    }
    public bool CanPickSelectedCoordinates => CanEdit && !_pickingCoordinates && _coordinatePicker is not null &&
        SelectedRegion is null && (SelectedNode.HasCoordinates || SelectedNode.IsTrigger && ShowsFilterCoordinates);
    public Task<bool> PickSelectedCoordinatesAsync() => PickSelectedCoordinatesCoreAsync(appendFilterLocation: false);
    public Task<bool> AddFilterLocationAsync() => PickSelectedCoordinatesCoreAsync(appendFilterLocation: true);
    public bool CanAddFilterLocation => CanPickSelectedCoordinates && SelectedNode.IsTrigger && ShowsFilterCoordinates;
    private async Task<bool> PickSelectedCoordinatesCoreAsync(bool appendFilterLocation)
    {
        if (!CanPickSelectedCoordinates || appendFilterLocation && !CanAddFilterLocation || _coordinatePicker is not { } picker) return false;
        if (appendFilterLocation && string.IsNullOrWhiteSpace(FilterX) != string.IsNullOrWhiteSpace(FilterY))
            return Refuse("Complete both existing coordinate lists before adding a location.");
        var node = SelectedNode; var trigger = _trigger; var version = _semanticVersion; var selection = _selectionVersion; var providerVersion = _coordinatePickerVersion;
        _pickingCoordinates = true; OnPropertyChanged(nameof(CanPickSelectedCoordinates)); OnPropertyChanged(nameof(CanAddFilterLocation));
        try
        {
            var result = await picker(node.HasCoordinates ? node.ToX : FilterX, node.HasCoordinates ? node.ToY : FilterY);
            if (result is not { X: > 0, Y: > 0 } point || !CanEdit || !ReferenceEquals(node, SelectedNode) ||
                selection != _selectionVersion || trigger != _trigger || version != _semanticVersion || providerVersion != _coordinatePickerVersion || !ReferenceEquals(picker, _coordinatePicker)) return false;
            if (node.HasCoordinates) node.SetCoordinates(point.X, point.Y);
            else
            {
                var prefixX = appendFilterLocation && !string.IsNullOrWhiteSpace(_filterX) ? _filterX + "," : "";
                var prefixY = appendFilterLocation && !string.IsNullOrWhiteSpace(_filterY) ? _filterY + "," : "";
                var nextX = prefixX + point.X.ToString(CultureInfo.InvariantCulture);
                var nextY = prefixY + point.Y.ToString(CultureInfo.InvariantCulture);
                // Reuse the existing bounded filter grammar before appending; keep
                // incomplete, invalid or full lists intact for correction by the user.
                if (appendFilterLocation && WccScenarioEventGraphEditor.Render(
                        new(WccScenarioEventTrigger.MoveTo, []) { FilterX = nextX, FilterY = nextY }).Diagnostic is { } problem)
                    return Refuse(problem);
                _filterX = nextX; _filterY = nextY;
                Changed();
            }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { _pickingCoordinates = false; OnPropertyChanged(nameof(CanPickSelectedCoordinates)); OnPropertyChanged(nameof(CanAddFilterLocation)); }
    }
    public EventDialogueNodeVm SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (value is null || !_nodes.Contains(value) || ReferenceEquals(value, _selectedNode) && _selectedRegion is null) return;
            _selectedRegion = null; _selectedNode = value; _selectionVersion++; RefreshTargets(); RaiseState(); GraphChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public EventDialogueNodeVm? SelectedAfter
    {
        get => _selectedAfter;
        set { if (value is not null && _connectionTargets.Contains(value)) SetField(ref _selectedAfter, value); }
    }
    public event EventHandler? GraphChanged;
    public event EventHandler? GeometryChanged;
    public EventDialogueNodeVm? AddMessage(double? x = null, double? y = null) => AddAction(WccScenarioEventActionKind.Message, x, y);
    public EventDialogueNodeVm? AddMoveUnit(double? x = null, double? y = null) => AddAction(WccScenarioEventActionKind.MoveUnit, x, y);
    public EventDialogueNodeVm? AddEndLevel(double? x = null, double? y = null) => AddAction(WccScenarioEventActionKind.EndLevel, x, y);
    public EventDialogueNodeVm? AddSpawnUnit(double? x = null, double? y = null) => AddAction(WccScenarioEventActionKind.SpawnUnit, x, y);
    public EventDialogueNodeVm? AddWait(double? x = null, double? y = null) => AddAction(WccScenarioEventActionKind.Wait, x, y);
    public EventDialogueNodeVm? AddScrollCamera(double? x = null, double? y = null) => AddAction(WccScenarioEventActionKind.ScrollCamera, x, y);
    public EventDialogueNodeVm? AddAdjustGold(double? x = null, double? y = null) => AddAction(WccScenarioEventActionKind.AdjustGold, x, y);
    public EventDialogueNodeVm? AddAction(WccScenarioEventActionKind kind, double? x = null, double? y = null)
    {
        if (!CanEdit || !Enum.IsDefined(kind)) return null;
        if (_nodes.Count > WccScenarioEventGraphEditor.MaximumMessages) { _status = "This event view supports up to 128 action nodes."; RaiseState(); return null; }
        var action = kind == WccScenarioEventActionKind.Message ? new WccScenarioEventAction(kind) { Speaker = "narrator", Text = "" } : new(kind);
        var node = new EventDialogueNodeVm(this, action);
        node.SetPosition(ClampGeometry(x ?? 340), ClampGeometry(y ?? 200)); _nodes.Add(node); _selectedRegion = null; _selectedNode = node; _selectionVersion++;
        RefreshTargets(); ReconcileDifficultyRegions(); Changed(); return node;
    }
    public bool BeginConnection(EventDialogueNodeVm from)
    {
        if (!CanEdit || !_nodes.Contains(from) || !from.IsTrigger && !_chain.Contains(from)) return false;
        _selectedRegion = null; _selectedNode = from; _selectionVersion++; _connectionSource = from; _status = null; RaiseState(); GraphChanged?.Invoke(this, EventArgs.Empty); return true;
    }
    public void CancelConnection() { _connectionSource = null; _status = null; RaiseState(); GraphChanged?.Invoke(this, EventArgs.Empty); }
    public bool Connect(EventDialogueNodeVm from, EventDialogueNodeVm to)
    {
        if (!CanEdit || !_nodes.Contains(from) || !_nodes.Contains(to) || to.IsTrigger || ReferenceEquals(from, to) || !from.IsTrigger && !_chain.Contains(from)) return false;
        _chain.Remove(to); _chain.Insert(from.IsTrigger ? 0 : _chain.IndexOf(from) + 1, to);
        _connectionSource = null; _selectedRegion = null; _selectedNode = to; _selectionVersion++; RefreshTargets(); ReconcileDifficultyRegions(); Changed(); return true;
    }
    public bool ConnectSelectedAfter() => SelectedAfter is { } from && Connect(from, SelectedNode);
    public bool ConnectPendingTo(EventDialogueNodeVm to) => _connectionSource is { } from && Connect(from, to);
    public bool DisconnectSelected()
    {
        if (!CanDisconnectSelected) return false;
        _chain.Remove(SelectedNode); _connectionSource = null; RefreshTargets(); ReconcileDifficultyRegions(); Changed(); return true;
    }

    public bool RemoveSelected()
    {
        if (!CanRemoveSelected) return false;
        if (SelectedRegion is not null) return RemoveSelectedRegion();
        var selected = SelectedNode; _chain.Remove(selected); _nodes.Remove(selected); _selectedNode = TriggerNode; _selectionVersion++;
        _connectionSource = null; RefreshTargets(); ReconcileDifficultyRegions(); Changed(); return true;
    }
    public bool MoveNode(EventDialogueNodeVm node, double x, double y)
    {
        if (!CanEdit || !_nodes.Contains(node) || !double.IsFinite(x) || !double.IsFinite(y)) return false;
        node.SetPosition(ClampGeometry(x), ClampGeometry(y));
        if (_regions.Count != 0) { ReconcileDifficultyRegions(); Changed(); }
        OnPropertyChanged(nameof(CanvasWidth)); OnPropertyChanged(nameof(CanvasHeight));
        GeometryChanged?.Invoke(this, EventArgs.Empty); return true;
    }
    public void ArrangeNodes()
    {
        if (_regions.Count != 0) { ArrangeDifficultyRegions(); return; }
        var ordered = new[] { TriggerNode }.Concat(_chain).Concat(_nodes.Where(node => !node.IsTrigger && !_chain.Contains(node))).ToArray();
        for (var index = 0; index < ordered.Length; index++) ordered[index].SetPosition(40 + index * 230, 40);
        OnPropertyChanged(nameof(CanvasWidth)); OnPropertyChanged(nameof(CanvasHeight)); GeometryChanged?.Invoke(this, EventArgs.Empty);
    }
    public WccScenarioEventGraph ToGraph() => new(_trigger, _chain.Select(node => node.ToAction()), _firstTimeOnly, _regions.Select(region => region.ToRegion()))
    {
        EventId = EmptyToNull(EventId), TurnNumber = ShowsTurnNumber ? PositiveInteger(TurnNumber) : null,
        FilterUnitId = ShowsFilterUnit ? EmptyToNull(FilterUnitId) : null,
        FilterX = ShowsFilterCoordinates ? EmptyToNull(FilterX) : null, FilterY = ShowsFilterCoordinates ? EmptyToNull(FilterY) : null
    };
    public bool TryCreateEvent(out string? text, out string? problem)
    {
        text = null; problem = LocalProblem;
        if (problem is not null) return Refuse(problem);
        var version = _semanticVersion;
        var rendered = WccScenarioEventGraphEditor.Render(ToGraph(), _seed, declaredDifficultyDefines: DeclaredDifficultyDefines, unitPresets: _knownUnitPresets);
        if (!rendered.IsSuccess || rendered.Text is null) { problem = rendered.Diagnostic ?? "The event could not be rendered."; return Refuse(problem); }
        problem = _validate?.Invoke(rendered.Text);
        if (problem is null && (!CanEdit || version != _semanticVersion)) problem = "The event or scenario changed while checking it. Review the nodes and apply again.";
        if (problem is not null) return Refuse(problem);
        text = rendered.Text; _status = "Event is ready to queue."; RaiseState(); return true;
    }
    public void RefreshAvailability() { RaiseState(); GraphChanged?.Invoke(this, EventArgs.Empty); }
    internal int OrderOf(EventDialogueNodeVm node) => _chain.IndexOf(node) + 1;
    internal void ActionChanged() => Changed();
    private string? LocalProblem
    {
        get
        {
            if (!CanEdit) return "The scenario context changed. Reopen the event editor.";
            if (_connectionSource is not null) return "Finish or cancel the current connection before applying.";
            if (_nodes.FirstOrDefault(node => !node.IsTrigger && !_chain.Contains(node)) is { } disconnected)
                return "Connect action “" + disconnected.Label + "” before applying.";
            if (_chain.Count == 0) return "Add an action and connect it to the trigger.";
            if (_regionGeometryProblem is not null) return _regionGeometryProblem;
            if (_localValidationVersion != _semanticVersion)
            {
                _localValidationVersion = _semanticVersion;
                // This bounded, detached renderer is validation only. The supplied
                // source reviewer remains mandatory at Apply and at scenario Save.
                _localValidationProblem = WccScenarioEventGraphEditor.Render(ToGraph(), _seed, declaredDifficultyDefines: DeclaredDifficultyDefines, unitPresets: _knownUnitPresets).Diagnostic;
            }
            return _localValidationProblem;
        }
    }
    private bool Refuse(string problem) { _status = problem; RaiseState(); return false; }
    private void RefreshTargets()
    {
        _connectionTargets = Array.AsReadOnly(new[] { TriggerNode }.Concat(_chain).Where(node => !ReferenceEquals(node, SelectedNode)).ToArray());
        var index = _chain.IndexOf(SelectedNode);
        _selectedAfter = index > 0 ? _chain[index - 1] : index == 0 ? TriggerNode : _chain.LastOrDefault(node => !ReferenceEquals(node, SelectedNode)) ?? TriggerNode;
    }
    private void Changed() { _semanticVersion++; _status = null; RaiseState(); GraphChanged?.Invoke(this, EventArgs.Empty); }
    private void RaiseState()
    {
        RefreshUnitChoices();
        foreach (var node in _nodes.ToArray()) node.Refresh();
        foreach (var region in _regions.ToArray()) region.Refresh();
        RaiseDifficultyRegionProperties();
        foreach (var property in new[] { nameof(SelectedNode), nameof(SelectedTrigger), nameof(FirstTimeOnly), nameof(Status), nameof(CanEdit), nameof(CanApply),
            nameof(EventId), nameof(CanEditEventId), nameof(EventIdHint), nameof(TurnNumber), nameof(FilterUnitId), nameof(FilterX), nameof(FilterY),
            nameof(ShowsTurnNumber), nameof(ShowsFilterUnit), nameof(ShowsFilterCoordinates), nameof(TriggerHint), nameof(CanPickSelectedCoordinates), nameof(CanAddFilterLocation),
            nameof(CanEditSelectedUnit), nameof(CanRemoveSelected), nameof(CanConnectSelected), nameof(CanDisconnectSelected), nameof(ConnectionTargets), nameof(SelectedAfter),
            nameof(ConnectionSource), nameof(ConnectionHint), nameof(HasPendingConnection), nameof(ConnectedActions), nameof(CanvasWidth), nameof(CanvasHeight) }) OnPropertyChanged(property);
    }
    internal static int? PositiveInteger(string? text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 6000) : 0;
    public void Dispose() { if (_disposed) return; _disposed = true; _connectionSource = null; RaiseState(); }
}

public sealed class EventDialogueNodeVm : ViewModelBase
{
    private readonly EventNodeGraphWindowViewModel _owner;
    private string _speaker;
    private string _text;
    private string? _caption;
    private bool _translateText;
    private bool _translateCaption;
    private string? _unitId;
    private string? _toX;
    private string? _toY;
    private WccScenarioUnitInstanceSpec? _unit;
    private WccScenarioEventResult _result;
    private string? _delayMilliseconds;
    private string? _side;
    private string? _amount;
    private double _x;
    private double _y;
    internal EventDialogueNodeVm(EventNodeGraphWindowViewModel owner, WccScenarioEventAction? action)
    {
        _owner = owner; IsTrigger = action is null; Key = action?.Key ?? Guid.NewGuid(); Kind = action?.Kind ?? WccScenarioEventActionKind.Message;
        _speaker = action?.Speaker ?? "narrator"; _text = action?.Text ?? ""; _caption = action?.Caption;
        _translateText = action?.TranslateText ?? true; _translateCaption = action?.TranslateCaption ?? true;
        _unitId = action?.UnitId; _toX = action?.ToX?.ToString(CultureInfo.InvariantCulture); _toY = action?.ToY?.ToString(CultureInfo.InvariantCulture);
        _unit = action?.Unit;
        _result = action?.Result ?? WccScenarioEventResult.Victory;
        _delayMilliseconds = action?.DelayMilliseconds?.ToString(CultureInfo.InvariantCulture);
        _side = action?.Side?.ToString(CultureInfo.InvariantCulture); _amount = action?.Amount?.ToString(CultureInfo.InvariantCulture);
    }
    public Guid Key { get; }
    public WccScenarioEventActionKind Kind { get; }
    public bool IsTrigger { get; }
    public bool IsMessage => !IsTrigger && Kind == WccScenarioEventActionKind.Message;
    public bool IsMoveUnit => !IsTrigger && Kind == WccScenarioEventActionKind.MoveUnit;
    public bool IsEndLevel => !IsTrigger && Kind == WccScenarioEventActionKind.EndLevel;
    public bool IsSpawnUnit => !IsTrigger && Kind == WccScenarioEventActionKind.SpawnUnit;
    public WccScenarioUnitInstanceSpec? Unit => _unit;
    public string UnitSummary => _unit is null ? "Configure the unit, side and location."
        : "Side " + Side + " · " + (_unit.Preset?.Name ?? _unit.Values.GetValueOrDefault("type")) + " · " + _unit.Values.GetValueOrDefault("x") + ", " + _unit.Values.GetValueOrDefault("y");
    public bool IsWait => !IsTrigger && Kind == WccScenarioEventActionKind.Wait;
    public bool IsScrollCamera => !IsTrigger && Kind == WccScenarioEventActionKind.ScrollCamera;
    public bool IsAdjustGold => !IsTrigger && Kind == WccScenarioEventActionKind.AdjustGold;
    public bool HasCoordinates => IsMoveUnit || IsScrollCamera;
    public bool CanEdit => _owner.CanEdit && !IsTrigger;
    public bool IsSelected => _owner.SelectedRegion is null && ReferenceEquals(_owner.SelectedNode, this);
    public bool IsConnected => IsTrigger || _owner.OrderOf(this) > 0;
    public bool IsConnectionSource => ReferenceEquals(_owner.ConnectionSource, this);
    public bool CanStartConnection => _owner.CanEdit && IsConnected;
    public bool CanAcceptConnection => _owner.CanEdit && !IsTrigger && _owner.ConnectionSource is { } from && !ReferenceEquals(from, this);
    public int Order => _owner.OrderOf(this);
    public double X => _x;
    public double Y => _y;
    public string KindLabel => IsTrigger ? _owner.SelectedTrigger.Label : Kind switch
    { WccScenarioEventActionKind.Message => "Message", WccScenarioEventActionKind.MoveUnit => "Move unit",
        WccScenarioEventActionKind.SpawnUnit => "Spawn unit", WccScenarioEventActionKind.Wait => "Wait", WccScenarioEventActionKind.ScrollCamera => "Scroll camera",
        WccScenarioEventActionKind.AdjustGold => "Adjust gold", _ => "End scenario" };
    public string Title => IsTrigger ? KindLabel : (IsConnected ? Order + ". " : "") + KindLabel;
    public string Label => Title + (IsMessage ? " — " + Speaker : IsMoveUnit ? " — " + UnitId : IsEndLevel ? " — " + Result : IsWait ? " — " + DelayMilliseconds + " ms" : IsAdjustGold ? " — side " + Side : "");
    public string ConnectionLabel => IsTrigger ? "Trigger" : IsConnected ? "Connected · " + Order : "Not connected";
    public string PreviewText => IsTrigger ? _owner.TriggerHint : IsSpawnUnit ? UnitSummary : IsMoveUnit ? (UnitId ?? "Choose a unit ID") + " → " + (ToX ?? "?") + ", " + (ToY ?? "?")
        : IsEndLevel ? Result + " · ends scenario" : IsWait ? (DelayMilliseconds ?? "?") + " ms"
        : IsScrollCamera ? "Camera → " + (ToX ?? "?") + ", " + (ToY ?? "?")
        : IsAdjustGold ? "Side " + (Side ?? "?") + " · " + (Amount ?? "?") + " gold" : Speaker + ": " + (Text.Length <= 70 ? Text : Text[..70] + "…");
    public IReadOnlyList<string> SpeakerChoices => _owner.SpeakerChoices;
    public IReadOnlyList<string> UnitChoices => _owner.UnitChoices;
    public IReadOnlyList<WccScenarioEventResult> ResultChoices { get; } = Array.AsReadOnly(new[] { WccScenarioEventResult.Victory, WccScenarioEventResult.Defeat });
    public string Speaker { get => _speaker; set { if (CanEdit && IsMessage && value is not null && SetField(ref _speaker, value)) _owner.ActionChanged(); } }
    public string? UnitId { get => _unitId; set { if (CanEdit && IsMoveUnit && SetField(ref _unitId, value)) _owner.ActionChanged(); } }
    public string? ToX { get => _toX; set { if (CanEdit && HasCoordinates && SetField(ref _toX, value)) _owner.ActionChanged(); } }
    public string? ToY { get => _toY; set { if (CanEdit && HasCoordinates && SetField(ref _toY, value)) _owner.ActionChanged(); } }
    public WccScenarioEventResult Result { get => _result; set { if (CanEdit && IsEndLevel && Enum.IsDefined(value) && SetField(ref _result, value)) _owner.ActionChanged(); } }
    public string? DelayMilliseconds { get => _delayMilliseconds; set { if (CanEdit && IsWait && SetField(ref _delayMilliseconds, value)) _owner.ActionChanged(); } }
    public string? Side { get => _side; set { if (CanEdit && IsAdjustGold && SetField(ref _side, value)) _owner.ActionChanged(); } }
    public string? Amount { get => _amount; set { if (CanEdit && IsAdjustGold && SetField(ref _amount, value)) _owner.ActionChanged(); } }
    public string Text
    {
        get => _text;
        set
        {
            if (!CanEdit || !IsMessage || value is null) return;
            var oldTranslate = _translateText;
            var input = value.TrimStart();
            var formatted = input.StartsWith('"') || input.StartsWith('_') && input[1..].TrimStart().StartsWith('"');
            if (formatted && WccWmlPlainTextCodec.TryReadLiteral(value, out var plain, out var translated))
            { value = plain; _translateText = translated; OnPropertyChanged(nameof(TranslateText)); }
            if (SetField(ref _text, value) || oldTranslate != _translateText) _owner.ActionChanged();
        }
    }
    public string? Caption
    {
        get => _caption;
        set
        {
            if (!CanEdit || !IsMessage) return;
            var oldTranslate = _translateCaption;
            var input = value?.TrimStart();
            if (input is not null && (input.StartsWith('"') || input.StartsWith('_') && input[1..].TrimStart().StartsWith('"'))
                && WccWmlPlainTextCodec.TryReadLiteral(value, out var plain, out var translated))
            { value = plain; _translateCaption = translated; OnPropertyChanged(nameof(TranslateCaption)); }
            if (SetField(ref _caption, value) || oldTranslate != _translateCaption) _owner.ActionChanged();
        }
    }
    public bool TranslateText { get => _translateText; set { if (CanEdit && IsMessage && SetField(ref _translateText, value)) _owner.ActionChanged(); } }
    public bool TranslateCaption { get => _translateCaption; set { if (CanEdit && IsMessage && SetField(ref _translateCaption, value)) _owner.ActionChanged(); } }
    internal void SetPosition(double x, double y) { _x = x; _y = y; OnPropertyChanged(nameof(X)); OnPropertyChanged(nameof(Y)); }
    internal void SetCoordinates(int x, int y)
    { if (!CanEdit || !HasCoordinates) return; _toX = x.ToString(CultureInfo.InvariantCulture); _toY = y.ToString(CultureInfo.InvariantCulture); _owner.ActionChanged(); }
    internal void ApplySpawnUnit(WccScenarioUnitInstanceSpec unit, int side)
    {
        if (!CanEdit || !IsSpawnUnit) return;
        _unit = unit; _side = side.ToString(CultureInfo.InvariantCulture); _owner.ActionChanged();
    }
    internal WccScenarioEventAction ToAction() => Kind switch
    {
        WccScenarioEventActionKind.Message => new(Kind) { Key = Key, Speaker = Speaker, Text = Text, Caption = Caption, TranslateText = TranslateText, TranslateCaption = TranslateCaption },
        WccScenarioEventActionKind.MoveUnit => new(Kind) { Key = Key, UnitId = UnitId, ToX = EventNodeGraphWindowViewModel.PositiveInteger(ToX), ToY = EventNodeGraphWindowViewModel.PositiveInteger(ToY) },
        WccScenarioEventActionKind.SpawnUnit => new(Kind) { Key = Key, Unit = _unit, Side = EventNodeGraphWindowViewModel.PositiveInteger(Side) },
        WccScenarioEventActionKind.Wait => new(Kind) { Key = Key, DelayMilliseconds = EventNodeGraphWindowViewModel.PositiveInteger(DelayMilliseconds) },
        WccScenarioEventActionKind.ScrollCamera => new(Kind) { Key = Key, ToX = EventNodeGraphWindowViewModel.PositiveInteger(ToX), ToY = EventNodeGraphWindowViewModel.PositiveInteger(ToY) },
        WccScenarioEventActionKind.AdjustGold => new(Kind) { Key = Key, Side = EventNodeGraphWindowViewModel.PositiveInteger(Side),
            Amount = int.TryParse(Amount, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount) ? amount : null },
        _ => new(Kind) { Key = Key, Result = Result }
    };
    internal void Refresh()
    {
        foreach (var property in new[] { nameof(Title), nameof(Label), nameof(ConnectionLabel), nameof(PreviewText), nameof(Order), nameof(IsConnected), nameof(IsSelected),
            nameof(IsConnectionSource), nameof(CanEdit), nameof(CanStartConnection), nameof(CanAcceptConnection), nameof(Speaker), nameof(Text), nameof(Caption),
            nameof(SpeakerChoices), nameof(UnitChoices), nameof(Unit), nameof(UnitSummary), nameof(DelayMilliseconds), nameof(Side), nameof(Amount), nameof(TranslateText), nameof(TranslateCaption), nameof(UnitId), nameof(ToX), nameof(ToY), nameof(Result) }) OnPropertyChanged(property);
    }
}