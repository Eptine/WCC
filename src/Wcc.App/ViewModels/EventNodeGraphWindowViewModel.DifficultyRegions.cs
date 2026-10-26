using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed partial class EventNodeGraphWindowViewModel
{
    public const double ActionCardWidth = 160;
    public const double ActionCardHeight = 84;
    private readonly ObservableCollection<EventDifficultyRegionVm> _regions = [];
    private EventDifficultyRegionVm? _selectedRegion;
    private string? _regionGeometryProblem;
    public ReadOnlyObservableCollection<EventDifficultyRegionVm> DifficultyRegions { get; private set; } = null!;
    public IReadOnlyList<string> DeclaredDifficultyDefines { get; private set; } = [];
    public bool CanAddDifficultyRegion => CanEdit && DeclaredDifficultyDefines.Count != 0 && _regions.Count < WccScenarioEventGraphEditor.MaximumDifficultyRegions;
    public bool HasSelectedNode => SelectedRegion is null;
    public EventDialogueNodeVm? SelectedNodeListItem { get => HasSelectedNode ? SelectedNode : null; set { if (value is not null) SelectedNode = value; } }
    public string SelectionLabel => SelectedRegion?.Label ?? SelectedNode.Label;
    public string DifficultyRegionGuidance => DeclaredDifficultyDefines.Count == 0
        ? "This context has no complete declared difficulty list. Difficulty regions need that captured list."
        : "Resize a region around whole action cards. Its actions must form one consecutive part of the path. Nested regions use the same difficulty. Regions have no execution ports.";
    public EventDifficultyRegionVm? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (value is not null && !_regions.Contains(value) || ReferenceEquals(value, _selectedRegion)) return;
            _selectedRegion = value; _selectionVersion++; _connectionSource = null; RaiseState(); GraphChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private void InitializeDifficultyRegions(WccScenarioEventGraph graph, IReadOnlyList<string>? declaredDifficultyDefines)
    {
        DeclaredDifficultyDefines = Array.AsReadOnly((declaredDifficultyDefines ?? []).ToArray());
        foreach (var region in graph.DifficultyRegions) _regions.Add(new(this, region));
        DifficultyRegions = new(_regions);
    }
    public EventDifficultyRegionVm? AddDifficultyRegion(double? x = null, double? y = null)
    {
        if (!CanAddDifficultyRegion || x is { } requestedX && !double.IsFinite(requestedX) ||
            y is { } requestedY && !double.IsFinite(requestedY)) return null;
        var region = new EventDifficultyRegionVm(this, new(Guid.NewGuid(), DeclaredDifficultyDefines[0], []));
        region.SetBounds(ClampGeometry(x ?? SelectedNode.X - 20), ClampGeometry(y ?? SelectedNode.Y - 38), 240, 160);
        _regions.Add(region); _selectedRegion = region; _selectionVersion++;
        ReconcileDifficultyRegions(); Changed(); GeometryChanged?.Invoke(this, EventArgs.Empty); return region;
    }
    public bool MoveDifficultyRegion(EventDifficultyRegionVm region, double x, double y)
    {
        if (!CanEdit || !_regions.Contains(region) || !double.IsFinite(x) || !double.IsFinite(y)) return false;
        region.SetBounds(ClampGeometry(x), ClampGeometry(y), region.Width, region.Height);
        ReconcileDifficultyRegions(); Changed(); GeometryChanged?.Invoke(this, EventArgs.Empty); return true;
    }
    public bool ResizeDifficultyRegion(EventDifficultyRegionVm region, double width, double height)
    {
        if (!CanEdit || !_regions.Contains(region) || !double.IsFinite(width) || !double.IsFinite(height)) return false;
        region.SetBounds(region.X, region.Y, Math.Clamp(width, 192, 300000), Math.Clamp(height, 132, 300000));
        ReconcileDifficultyRegions(); Changed(); GeometryChanged?.Invoke(this, EventArgs.Empty); return true;
    }
    public bool RemoveSelectedRegion()
    {
        if (!CanEdit || SelectedRegion is not { } region) return false;
        _regions.Remove(region); _selectedRegion = null; _selectionVersion++;
        ReconcileDifficultyRegions(); Changed(); GeometryChanged?.Invoke(this, EventArgs.Empty); return true;
    }
    internal void DifficultyRegionChanged() => Changed();
    public double GeometryCoordinateLimit => _regions.Count == 0
        ? Math.Min(300000, Math.Max(6000, Math.Max(CanvasWidth, CanvasHeight))) : 300000;
    private double ClampGeometry(double value) => GeometryCoordinateLimit == 6000 ? Clamp(value)
        : double.IsFinite(value) ? Math.Clamp(value, 0, GeometryCoordinateLimit) : 0;
    private void ReconcileDifficultyRegions()
    {
        _regionGeometryProblem = null;
        foreach (var region in _regions)
        {
            var members = new List<Guid>();
            foreach (var node in _chain.Concat(_nodes.Where(node => !node.IsTrigger && !_chain.Contains(node))))
            {
                if (ContainsCard(region, node)) members.Add(node.Key);
                else if (IntersectsCard(region, node)) _regionGeometryProblem ??= "Move or resize the difficulty region so it contains whole action cards; a card crosses its edge.";
            }
            if (IntersectsCard(region, TriggerNode)) _regionGeometryProblem ??= "The trigger must stay outside difficulty regions.";
            var parent = _regions.Where(candidate => !ReferenceEquals(candidate, region) && ContainsRegion(candidate, region))
                .OrderBy(candidate => candidate.Width * candidate.Height).FirstOrDefault();
            region.SetMembership(members, parent?.Key);
        }
        for (var first = 0; first < _regions.Count; first++)
            for (var second = first + 1; second < _regions.Count; second++)
            {
                var a = _regions[first]; var b = _regions[second];
                if (Intersects(a.X, a.Y, a.Width, a.Height, b.X, b.Y, b.Width, b.Height) && !ContainsRegion(a, b) && !ContainsRegion(b, a))
                    _regionGeometryProblem ??= "Difficulty regions must be separate or fully nested; crossing regions cannot be applied.";
            }
    }
    // Layout is rebuilt from explicit membership, never inferred from the old
    // rectangles. One horizontal row preserves consecutive region intervals;
    // depth margins leave room for every enclosing header without a row wrap.
    private void ArrangeDifficultyRegions()
    {
        int Depth(EventDifficultyRegionVm region)
        {
            var depth = 0; var seen = new HashSet<Guid> { region.Key }; var parent = region.ParentRegionKey;
            while (parent is { } key && seen.Add(key) && _regions.FirstOrDefault(candidate => candidate.Key == key) is { } enclosing)
            { depth++; parent = enclosing.ParentRegionKey; }
            return depth;
        }
        var maxDepth = _regions.Select(Depth).DefaultIfEmpty(0).Max();
        var ordered = new[] { TriggerNode }.Concat(_chain).Concat(_nodes.Where(node => !node.IsTrigger && !_chain.Contains(node))).ToArray();
        var step = ActionCardWidth + 80 + maxDepth * 48;
        for (var index = 0; index < ordered.Length; index++) ordered[index].SetPosition(80 + index * step, 80 + maxDepth * 28);
        foreach (var region in _regions)
        {
            var members = _nodes.Where(node => !node.IsTrigger && region.ActionKeys.Contains(node.Key)).ToArray();
            if (members.Length == 0) continue;
            var padding = (maxDepth - Depth(region) + 1) * 16;
            var topPadding = (maxDepth - Depth(region) + 1) * 28;
            var left = members.Min(node => node.X) - padding; var top = members.Min(node => node.Y) - topPadding;
            region.SetBounds(Math.Max(0, left), Math.Max(0, top), members.Max(node => node.X) + ActionCardWidth + padding - left,
                members.Max(node => node.Y) + ActionCardHeight + padding - top);
        }
        // Invalid imported memberships remain visible and fail the Core validator.
        // Valid membership intervals keep the exact same keys after arranging.
        _regionGeometryProblem = null;
        RaiseState(); OnPropertyChanged(nameof(CanvasWidth)); OnPropertyChanged(nameof(CanvasHeight));
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }
    private static bool ContainsCard(EventDifficultyRegionVm region, EventDialogueNodeVm node) =>
        node.X >= region.X + 8 && node.Y >= region.Y + 26 && node.X + ActionCardWidth <= region.X + region.Width - 8 && node.Y + ActionCardHeight <= region.Y + region.Height - 8;
    private static bool IntersectsCard(EventDifficultyRegionVm region, EventDialogueNodeVm node) =>
        Intersects(region.X, region.Y, region.Width, region.Height, node.X, node.Y, ActionCardWidth, ActionCardHeight);
    private static bool ContainsRegion(EventDifficultyRegionVm outer, EventDifficultyRegionVm inner) =>
        inner.X > outer.X && inner.Y >= outer.Y + 26 && inner.X + inner.Width < outer.X + outer.Width && inner.Y + inner.Height < outer.Y + outer.Height;
    private static bool Intersects(double x, double y, double width, double height, double otherX, double otherY, double otherWidth, double otherHeight) =>
        x < otherX + otherWidth && otherX < x + width && y < otherY + otherHeight && otherY < y + height;
    private void RaiseDifficultyRegionProperties()
    {
        foreach (var property in new[] { nameof(DifficultyRegions), nameof(SelectedRegion), nameof(HasSelectedNode), nameof(SelectedNodeListItem), nameof(SelectionLabel), nameof(CanAddDifficultyRegion), nameof(DifficultyRegionGuidance) }) OnPropertyChanged(property);
    }
}

public sealed class EventDifficultyRegionVm : ViewModelBase
{
    private readonly EventNodeGraphWindowViewModel _owner;
    private string _difficultyDefine;
    private IReadOnlyList<Guid> _actionKeys;
    private Guid? _parentRegionKey;
    internal EventDifficultyRegionVm(EventNodeGraphWindowViewModel owner, WccScenarioEventDifficultyRegion region)
    { _owner = owner; Key = region.Key; _difficultyDefine = region.DifficultyDefine; _actionKeys = region.ActionKeys; _parentRegionKey = region.ParentRegionKey; }
    public Guid Key { get; }
    public string DifficultyDefine
    {
        get => _difficultyDefine;
        set { if (_owner.CanEdit && DifficultyChoices.Contains(value, StringComparer.Ordinal) && SetField(ref _difficultyDefine, value)) _owner.DifficultyRegionChanged(); }
    }
    public IReadOnlyList<string> DifficultyChoices => _owner.DeclaredDifficultyDefines;
    public IReadOnlyList<Guid> ActionKeys => _actionKeys;
    public Guid? ParentRegionKey => _parentRegionKey;
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Width { get; private set; } = 240;
    public double Height { get; private set; } = 160;
    public bool IsSelected => ReferenceEquals(_owner.SelectedRegion, this);
    public string Label => "If difficulty " + DifficultyDefine;
    public string MembershipSummary => ActionKeys.Count + " action(s) · one consecutive part of the path";
    internal WccScenarioEventDifficultyRegion ToRegion() => new(Key, DifficultyDefine, ActionKeys, ParentRegionKey);
    internal void SetMembership(IReadOnlyList<Guid> keys, Guid? parent) { _actionKeys = Array.AsReadOnly(keys.ToArray()); _parentRegionKey = parent; }
    internal void SetBounds(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; Refresh(); }
    internal void Refresh()
    {
        foreach (var property in new[] { nameof(X), nameof(Y), nameof(Width), nameof(Height), nameof(Label), nameof(IsSelected), nameof(DifficultyDefine), nameof(MembershipSummary) }) OnPropertyChanged(property);
    }
}