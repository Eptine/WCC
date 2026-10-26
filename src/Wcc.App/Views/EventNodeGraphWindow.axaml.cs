using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wcc.App.ViewModels;
using Wcc.App.Views.Controls;
using Wcc.Core.Scenarios;
using GraphPath = Avalonia.Controls.Shapes.Path;

namespace Wcc.App.Views;

public partial class EventNodeGraphWindow : Window
{
    private const double CardWidth = EventNodeGraphWindowViewModel.ActionCardWidth;
    private const double CardHeight = EventNodeGraphWindowViewModel.ActionCardHeight;
    private static readonly DataFormat<string> PaletteDragFormat = DataFormat.CreateStringApplicationFormat("wcc.event-action");
    private readonly string _paletteDragToken = Guid.NewGuid().ToString("N");
    private EventNodeGraphWindowViewModel? _model;
    private readonly Dictionary<EventDialogueNodeVm, Border> _cards = [];
    private readonly Dictionary<EventDialogueNodeVm, NodeCard> _visuals = [];
    private readonly List<Control> _connectors = [];
    private EventDialogueNodeVm? _dragNode;
    private Point _dragPointer;
    private Point _dragOrigin;
    private EventDialogueNodeVm? _speakerPopupNode;
    private EventDialogueNodeVm? _movePopupNode;
    private EventDialogueNodeVm? _filterPopupNode;
    private EventNodeGraphWindowViewModel? _palettePressModel;
    private PointerPressedEventArgs? _palettePress;
    private Button? _palettePressButton;
    private Point _palettePressPoint;
    private WccScenarioEventActionKind? _paletteDragging;
    public string? ResultWml { get; private set; }
    public string? Result => ResultWml;
    public EventNodeGraphWindow()
    {
        InitializeComponent(); DataContextChanged += (_, _) => BindModel();
        EventGraphCanvas.Focusable = true;
        EventGraphCanvas.PointerMoved += NodePointerMoved;
        EventGraphCanvas.PointerReleased += NodePointerReleased;
        EventGraphCanvas.PointerCaptureLost += (_, _) => { _dragNode = null; _dragRegion = null; };
        EventGraphCanvas.AddHandler(DragDrop.DragOverEvent, PaletteDragOver);
        EventGraphCanvas.AddHandler(DragDrop.DropEvent, PaletteDropped);
        EventGraphScroll.AddHandler(PointerWheelChangedEvent, GraphWheelChanged, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, GraphKeyDown, RoutingStrategies.Bubble);
        foreach (var button in new[] { EventGraphAddMessageButton, EventGraphAddSpawnUnitButton, EventGraphAddMoveUnitButton, EventGraphAddEndLevelButton, EventGraphAddWaitButton, EventGraphAddScrollCameraButton, EventGraphAddAdjustGoldButton })
        {
            // Button handles a normal press before ordinary XAML handlers run.
            button.AddHandler(PointerPressedEvent, PalettePointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            // A normal button release can be handled by its class handler.
            // Clear our candidate first; an active native drag retains its token.
            button.AddHandler(PointerReleasedEvent, (_, _) => ResetPalettePress(), RoutingStrategies.Tunnel, handledEventsToo: true);
            button.PointerCaptureLost += (_, _) => ResetPalettePress();
        }
        EventGraphSpeakerChoices.Flyout!.Opened += (_, _) => _speakerPopupNode = _model?.SelectedNode;
        EventGraphSpeakerChoices.Flyout.Closed += (_, _) => _speakerPopupNode = null;
        EventGraphMoveUnitChoices.Flyout!.Opened += (_, _) => _movePopupNode = _model?.SelectedNode;
        EventGraphMoveUnitChoices.Flyout.Closed += (_, _) => _movePopupNode = null;
        EventGraphFilterUnitChoices.Flyout!.Opened += (_, _) => _filterPopupNode = _model?.SelectedNode;
        EventGraphFilterUnitChoices.Flyout.Closed += (_, _) => _filterPopupNode = null;
        Activated += (_, _) => _model?.RefreshAvailability();
        Closed += (_, _) =>
        {
            if (_model is not { } model) return;
            model.GraphChanged -= GraphChanged; model.GeometryChanged -= GeometryChanged; model.Dispose();
        };
    }
    public EventNodeGraphWindow(EventNodeGraphWindowViewModel model) : this() => DataContext = model;
    private void BindModel()
    {
        if (_model is { } previous) { previous.GraphChanged -= GraphChanged; previous.GeometryChanged -= GeometryChanged; }
        _model = DataContext as EventNodeGraphWindowViewModel; _dragNode = null; _dragRegion = null;
        _palettePress = null; _palettePressButton = null; _palettePressModel = null; _paletteDragging = null;
        CloseChoicePopups();
        if (_model is { } model) { model.GraphChanged += GraphChanged; model.GeometryChanged += GeometryChanged; }
        RebuildGraph();
    }
    private void CloseChoicePopups()
    {
        _speakerPopupNode = null; _movePopupNode = null; _filterPopupNode = null;
        EventGraphSpeakerChoices.Flyout?.Hide(); EventGraphMoveUnitChoices.Flyout?.Hide(); EventGraphFilterUnitChoices.Flyout?.Hide();
    }
    private void GraphChanged(object? sender, EventArgs args)
    {
        if (_speakerPopupNode is not null && !ReferenceEquals(_speakerPopupNode, _model?.SelectedNode) ||
            _movePopupNode is not null && !ReferenceEquals(_movePopupNode, _model?.SelectedNode) ||
            _filterPopupNode is not null && !ReferenceEquals(_filterPopupNode, _model?.SelectedNode)) CloseChoicePopups();
        if (_model is null || _cards.Count != _model.Nodes.Count || _model.Nodes.Any(node => !_cards.ContainsKey(node)) || _regionVisuals.Count != _model.DifficultyRegions.Count || _model.DifficultyRegions.Any(region => !_regionVisuals.ContainsKey(region))) RebuildGraph();
        else { UpdateCards(); UpdateRegions(); DrawConnectors(); }
    }
    private void GeometryChanged(object? sender, EventArgs args)
    {
        foreach (var (node, card) in _cards) { Canvas.SetLeft(card, node.X); Canvas.SetTop(card, node.Y); }
        UpdateRegions();
        DrawConnectors();
    }
    private void RebuildGraph()
    {
        EventGraphCanvas.Children.Clear(); _cards.Clear(); _visuals.Clear(); _connectors.Clear(); _regionVisuals.Clear();
        if (_model is not { } model) return;
        if (_dragNode is not null && !model.Nodes.Contains(_dragNode)) _dragNode = null;
        RebuildRegions(model);
        foreach (var node in model.Nodes)
        {
            var colour = node.IsTrigger ? "#32433C" : node.IsMoveUnit ? "#293E4B" : node.IsEndLevel ? "#4B3930" : node.IsScrollCamera ? "#30434B" : node.IsAdjustGold ? "#4B452C" : "#343530";
            var card = new Border { Width = CardWidth, Height = CardHeight, Background = Brush(colour),
                BorderBrush = CardBrush(node), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(5), Tag = node, DataContext = node };
            var panel = new DockPanel { Margin = new Thickness(6) };
            var header = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 0, 0, 3), Cursor = new Cursor(StandardCursorType.SizeAll), Tag = node };
            var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,21") };
            var headerText = new TextBlock { Text = node.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var delete = new Button { Content = "×", Tag = node, Name = "EventGraphDelete_" + node.Key.ToString("N"), Width = 20, MinWidth = 20, Height = 21, MinHeight = 21,
                Padding = new Thickness(0), IsVisible = node.IsSelected && !node.IsTrigger, IsEnabled = node.CanEdit };
            UseGraphIcon(delete, WccActionIcon.Close);
            delete.Click += DeleteNodeClicked; ToolTip.SetTip(delete, "Remove this action"); Grid.SetColumn(delete, 1);
            headerGrid.Children.Add(headerText); headerGrid.Children.Add(delete); header.Child = headerGrid;
            ToolTip.SetTip(header, node.Label + "\nDrag to arrange this node."); header.PointerPressed += NodePointerPressed;
            DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
            var ports = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,22"), Margin = new Thickness(0, 3, 0, 0) };
            var input = new Button { Content = new Ellipse { Width = 7, Height = 7, Fill = Brush("#E6DDC5"), IsHitTestVisible = false }, Tag = node, IsEnabled = node.CanAcceptConnection, Padding = new Thickness(0), MinWidth = 20, MinHeight = 20, Height = 20,
                IsVisible = !node.IsTrigger, Name = "EventGraphInput_" + node.Key.ToString("N") };
            CenterGraphButton(input); Avalonia.Automation.AutomationProperties.SetName(input, "Connect to this action");
            ToolTip.SetTip(input, "Connect the pending output to this action."); input.Click += InputPortClicked;
            var output = new Button { Content = "→", Tag = node, IsEnabled = node.CanStartConnection, Padding = new Thickness(0), MinWidth = 20, MinHeight = 20, Height = 20,
                Name = "EventGraphOutput_" + node.Key.ToString("N") };
            UseGraphIcon(output, WccActionIcon.Next);
            ToolTip.SetTip(output, "Choose the action that follows this node."); output.Click += OutputPortClicked;
            var state = new TextBlock { Text = node.ConnectionLabel, FontSize = 9, Opacity = 0.7, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(state, 1); Grid.SetColumn(output, 2); ports.Children.Add(input); ports.Children.Add(state); ports.Children.Add(output);
            DockPanel.SetDock(ports, Dock.Bottom); panel.Children.Add(ports);
            var preview = new TextBlock { Text = node.PreviewText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 28, FontSize = 10, Opacity = 0.85 };
            panel.Children.Add(preview); card.Child = panel;
            card.PointerPressed += (_, args) =>
            {
                if (args.Source is Visual source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any())) return;
                if (args.GetCurrentPoint(card).Properties.IsLeftButtonPressed) { model.SelectedNode = node; EventGraphCanvas.Focus(); }
            };
            Canvas.SetLeft(card, node.X); Canvas.SetTop(card, node.Y); EventGraphCanvas.Children.Add(card); _cards.Add(node, card);
            _visuals.Add(node, new(card, header, headerText, preview, state, input, output, delete));
        }
        DrawConnectors();
    }
    private static void CenterGraphButton(Button button)
    {
        button.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        button.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }
    private static void UseGraphIcon(Button button, WccActionIcon action)
    {
        CenterGraphButton(button);
        WccButtonIcon.SetIsCompact(button, false);
        WccButtonIcon.SetIconSize(button, 16);
        WccButtonIcon.SetAction(button, action);
    }
    private static SolidColorBrush Brush(string colour) => new(Color.Parse(colour));
    private static SolidColorBrush CardBrush(EventDialogueNodeVm node) => Brush(node.IsSelected ? "#D9BF85" : node.IsConnectionSource ? "#D6CEAB" : node.IsConnected ? "#75887C" : "#BB7D4B");
    private void UpdateCards()
    {
        foreach (var (node, visual) in _visuals)
        {
            visual.Card.BorderBrush = CardBrush(node); visual.HeaderText.Text = node.Title; visual.Preview.Text = node.PreviewText; visual.State.Text = node.ConnectionLabel;
            visual.Input.IsEnabled = node.CanAcceptConnection; visual.Output.IsEnabled = node.CanStartConnection;
            visual.Delete.IsVisible = node.IsSelected && !node.IsTrigger; visual.Delete.IsEnabled = node.CanEdit;
            ToolTip.SetTip(visual.Header, node.Label + "\nDrag to arrange this node.");
        }
    }
    private void DrawConnectors()
    {
        foreach (var connector in _connectors) EventGraphCanvas.Children.Remove(connector); _connectors.Clear();
        if (_model is not { } model) return;
        var previous = model.TriggerNode; var index = 0;
        foreach (var node in model.ConnectedActions)
        {
            var start = new Point(previous.X + CardWidth, previous.Y + CardHeight - 17);
            var end = new Point(node.X, node.Y + CardHeight - 17);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, false);
                if (end.X >= start.X + 20)
                {
                    var bend = Math.Max(24, (end.X - start.X) / 2);
                    context.CubicBezierTo(new(start.X + bend, start.Y), new(end.X - bend, end.Y), end);
                }
                else
                {
                    // Route backward/row-wrap links through a separate outside
                    // lane instead of laying one shared vertical elbow on cards.
                    var lane = Math.Max(previous.Y, node.Y) + CardHeight + 24 + index % 3 * 12;
                    var right = start.X + 28; var left = end.X - 24;
                    context.CubicBezierTo(new(right, start.Y), new(right, lane), new(right - 12, lane));
                    context.CubicBezierTo(new(right - 36, lane), new(left, lane), new(left, lane - 12));
                    context.CubicBezierTo(new(left, end.Y), new(end.X - 16, end.Y), end);
                }
                context.EndFigure(false);
            }
            var line = new GraphPath { Data = geometry, Stroke = Brush("#BAA375"), StrokeThickness = 2, IsHitTestVisible = false };
            var arrow = new Polygon { Points = [end, new(end.X - 7, end.Y - 4), new(end.X - 7, end.Y + 4)], Fill = Brush("#D9BF85"), IsHitTestVisible = false };
            EventGraphCanvas.Children.Insert(0, line); EventGraphCanvas.Children.Insert(1, arrow); _connectors.Add(line); _connectors.Add(arrow); previous = node; index++;
        }
    }
    private void NodePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (_model is not { CanEdit: true } model || sender is not Border { Tag: EventDialogueNodeVm node } header || !args.GetCurrentPoint(header).Properties.IsLeftButtonPressed ||
            args.Source is Visual source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any())) return;
        model.SelectedNode = node; EventGraphCanvas.Focus(); _dragNode = node; _dragPointer = args.GetPosition(EventGraphCanvas); _dragOrigin = new(node.X, node.Y);
        args.Pointer.Capture(EventGraphCanvas); args.Handled = true;
    }
    private void NodePointerMoved(object? sender, PointerEventArgs args)
    {
        if (MoveDraggedRegion(args)) return;
        if (_dragNode is not { } node || _model is not { } model) return;
        var point = args.GetPosition(EventGraphCanvas); model.MoveNode(node, _dragOrigin.X + point.X - _dragPointer.X, _dragOrigin.Y + point.Y - _dragPointer.Y); args.Handled = true;
    }
    private void NodePointerReleased(object? sender, PointerReleasedEventArgs args)
    { if (_dragNode is null && _dragRegion is null) return; _dragNode = null; _dragRegion = null; args.Pointer.Capture(null); args.Handled = true; }
    private void ResetPalettePress()
    {
        if (_paletteDragging is not null) return;
        _palettePress = null; _palettePressButton = null; _palettePressModel = null;
    }
    private void PalettePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (_paletteDragging is not null) return;
        ResetPalettePress();
        if (_model is not { CanEdit: true } model || sender is not Button button || !args.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
        _palettePress = args; _palettePressButton = button; _palettePressPoint = args.GetPosition(button); _palettePressModel = model;
    }
    private async void PalettePointerMoved(object? sender, PointerEventArgs args)
    {
        if (_palettePress is not { } press || sender is not Button button || !ReferenceEquals(button, _palettePressButton) ||
            _palettePressModel is not { CanEdit: true } model || !ReferenceEquals(model, _model) ||
            !args.GetCurrentPoint(button).Properties.IsLeftButtonPressed || !Enum.TryParse<WccScenarioEventActionKind>(button.Tag?.ToString(), out var kind)) return;
        var delta = args.GetPosition(button) - _palettePressPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
        _palettePress = null; _palettePressButton = null; _paletteDragging = kind;
        var transfer = new DataTransfer(); transfer.Add(DataTransferItem.Create(PaletteDragFormat, _paletteDragToken)); args.Handled = true;
        try { await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy); }
        finally { _paletteDragging = null; ResetPalettePress(); }
    }
    private void PaletteDragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = _paletteDragging is not null && args.DataTransfer.TryGetValue(PaletteDragFormat) == _paletteDragToken &&
            ReferenceEquals(_palettePressModel, _model) && _model is { CanEdit: true } ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }
    private void PaletteDropped(object? sender, DragEventArgs args)
    {
        if (_paletteDragging is not { } kind || args.DataTransfer.TryGetValue(PaletteDragFormat) != _paletteDragToken ||
            _model is not { CanEdit: true } model || !ReferenceEquals(model, _palettePressModel)) return;
        var point = args.GetPosition(EventGraphCanvas); model.AddAction(kind, point.X - CardWidth / 2, point.Y - CardHeight / 2); args.Handled = true;
    }
    private void GraphWheelChanged(object? sender, PointerWheelEventArgs args)
    {
        if ((args.KeyModifiers & KeyModifiers.Control) == 0 || _model is not { } model) return;
        var point = args.GetPosition(EventGraphScroll); var oldZoom = model.Zoom; var offset = EventGraphScroll.Offset;
        model.Zoom *= Math.Pow(1.12, args.Delta.Y); var zoom = model.Zoom;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(model, _model) && model.Zoom == zoom)
                EventGraphScroll.Offset = new(Math.Max(0, (offset.X + point.X) * zoom / oldZoom - point.X), Math.Max(0, (offset.Y + point.Y) * zoom / oldZoom - point.Y));
        }, DispatcherPriority.Loaded);
        args.Handled = true;
    }
    private void GraphKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Delete || _model is not { CanRemoveSelected: true } model) return;
        if (args.Source is Visual source && (source is TextBox or ComboBox || source.GetVisualAncestors().Any(parent => parent is TextBox or ComboBox))) return;
        args.Handled = model.RemoveSelected();
    }
    private void OutputPortClicked(object? sender, RoutedEventArgs args) { if (sender is Button { Tag: EventDialogueNodeVm node }) _model?.BeginConnection(node); args.Handled = true; }
    private void InputPortClicked(object? sender, RoutedEventArgs args) { if (sender is Button { Tag: EventDialogueNodeVm node }) _model?.ConnectPendingTo(node); args.Handled = true; }
    private void AddCentred(WccScenarioEventActionKind kind)
    {
        ResetPalettePress();
        if (_model is not { CanEdit: true } model || _paletteDragging is not null || FindClickPosition(model) is not { } point ||
            model.AddAction(kind, point.X, point.Y) is not { } node) return;
        // GraphChanged publishes the new card synchronously. Scroll only after
        // layout knows its extent, without moving any existing manual placement.
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(model, _model) && ReferenceEquals(node, model.SelectedNode) && _cards.TryGetValue(node, out var card))
                card.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
    private Point? FindClickPosition(EventNodeGraphWindowViewModel model)
    {
        const double gap = 12;
        var limit = model.GeometryCoordinateLimit;
        var stepX = CardWidth + gap; var stepY = CardHeight + gap;
        var centre = new Point(Math.Clamp((EventGraphScroll.Offset.X + EventGraphScroll.Viewport.Width / 2) / model.Zoom - CardWidth / 2, 0, limit),
            Math.Clamp((EventGraphScroll.Offset.Y + EventGraphScroll.Viewport.Height / 2) / model.Zoom - CardHeight / 2, 0, limit));
        var left = Math.Clamp(EventGraphScroll.Offset.X / model.Zoom + gap, 0, limit);
        var top = Math.Clamp(EventGraphScroll.Offset.Y / model.Zoom + gap, 0, limit);
        var right = Math.Clamp((EventGraphScroll.Offset.X + EventGraphScroll.Viewport.Width) / model.Zoom - CardWidth - gap, 0, limit);
        var bottom = Math.Clamp((EventGraphScroll.Offset.Y + EventGraphScroll.Viewport.Height) / model.Zoom - CardHeight - gap, 0, limit);
        bool Free(Point candidate) => candidate.X >= 0 && candidate.X <= limit && candidate.Y >= 0 && candidate.Y <= limit && model.Nodes.All(node =>
            candidate.X + CardWidth + gap <= node.X || node.X + CardWidth + gap <= candidate.X ||
            candidate.Y + CardHeight + gap <= node.Y || node.Y + CardHeight + gap <= candidate.Y);
        if (Free(centre)) return centre;
        foreach (var offset in new Vector[] { new(stepX, 0), new(-stepX, 0), new(0, stepY), new(0, -stepY),
            new(stepX, stepY), new(-stepX, stepY), new(stepX, -stepY), new(-stepX, -stepY) })
        {
            var candidate = centre + offset;
            if (candidate.X >= left && candidate.X <= right && candidate.Y >= top && candidate.Y <= bottom && Free(candidate)) return candidate;
        }
        for (var y = top; y <= bottom; y += stepY)
            for (var x = left; x <= right; x += stepX)
                if (Free(new(x, y))) return new(x, y);
        var below = new Point(centre.X, model.Nodes.Max(node => node.Y + CardHeight) + gap);
        if (Free(below)) return below;
        // Nodes are bounded to 128; this finite grid has far more free slots.
        // Test coordinates before AddAction applies its matching world clamp.
        for (var y = 0d; y <= limit; y += stepY)
            for (var x = 0d; x <= limit; x += stepX)
                if (Free(new(x, y))) return new(x, y);
        return null;
    }
    private void AddMessageClicked(object? sender, RoutedEventArgs args) => AddCentred(WccScenarioEventActionKind.Message);
    private void AddMoveUnitClicked(object? sender, RoutedEventArgs args) => AddCentred(WccScenarioEventActionKind.MoveUnit);
    private void AddEndLevelClicked(object? sender, RoutedEventArgs args) => AddCentred(WccScenarioEventActionKind.EndLevel);
    private void AddSpawnUnitClicked(object? sender, RoutedEventArgs args) => AddCentred(WccScenarioEventActionKind.SpawnUnit);
    private async void ConfigureUnitClicked(object? sender, RoutedEventArgs args) { if (_model is { } model) await model.EditSelectedUnitAsync(); }
    private void AddWaitClicked(object? sender, RoutedEventArgs args) => AddCentred(WccScenarioEventActionKind.Wait);
    private void AddScrollCameraClicked(object? sender, RoutedEventArgs args) => AddCentred(WccScenarioEventActionKind.ScrollCamera);
    private void AddAdjustGoldClicked(object? sender, RoutedEventArgs args) => AddCentred(WccScenarioEventActionKind.AdjustGold);
    private void ArrangeClicked(object? sender, RoutedEventArgs args) { _model?.ArrangeNodes(); EventGraphScroll.Offset = Vector.Zero; }
    private void ZoomInClicked(object? sender, RoutedEventArgs args) { if (_model is { } model) model.Zoom *= 1.2; }
    private void ZoomOutClicked(object? sender, RoutedEventArgs args) { if (_model is { } model) model.Zoom /= 1.2; }
    private void ConnectClicked(object? sender, RoutedEventArgs args) => _model?.ConnectSelectedAfter();
    private void DisconnectClicked(object? sender, RoutedEventArgs args) => _model?.DisconnectSelected();
    private void RemoveClicked(object? sender, RoutedEventArgs args) => _model?.RemoveSelected();
    private void DeleteNodeClicked(object? sender, RoutedEventArgs args)
    { if (_model is { CanEdit: true } model && sender is Button { Tag: EventDialogueNodeVm node } && model.Nodes.Contains(node)) { model.SelectedNode = node; model.RemoveSelected(); } args.Handled = true; }
    private void CancelConnectionClicked(object? sender, RoutedEventArgs args) => _model?.CancelConnection();
    private void ClearCaptionClicked(object? sender, RoutedEventArgs args) { if (_model?.SelectedNode is { IsMessage: true } node) node.Caption = null; }
    private async void PickCoordinatesClicked(object? sender, RoutedEventArgs args) { if (_model is { } model) await model.PickSelectedCoordinatesAsync(); }
    private async void AddFilterLocationClicked(object? sender, RoutedEventArgs args) { if (_model is { } model) await model.AddFilterLocationAsync(); }
    private void SpeakerChoiceClicked(object? sender, RoutedEventArgs args)
    {
        if (_model is { CanEdit: true } model && _speakerPopupNode is { IsMessage: true } node && ReferenceEquals(node, model.SelectedNode) &&
            sender is Button { DataContext: string choice } && model.SpeakerChoices.Contains(choice)) node.Speaker = choice;
        EventGraphSpeakerChoices.Flyout?.Hide();
    }
    private void MoveUnitChoiceClicked(object? sender, RoutedEventArgs args)
    {
        if (_model is { CanEdit: true } model && _movePopupNode is { IsMoveUnit: true } node && ReferenceEquals(node, model.SelectedNode) &&
            sender is Button { DataContext: string choice } && model.UnitChoices.Contains(choice)) node.UnitId = choice;
        EventGraphMoveUnitChoices.Flyout?.Hide();
    }
    private void FilterUnitChoiceClicked(object? sender, RoutedEventArgs args)
    {
        if (_model is { CanEdit: true, ShowsFilterUnit: true } model && _filterPopupNode is { IsTrigger: true } node && ReferenceEquals(node, model.SelectedNode) &&
            sender is Button { DataContext: string choice } && model.UnitChoices.Contains(choice)) model.FilterUnitId = choice;
        EventGraphFilterUnitChoices.Flyout?.Hide();
    }
    private void ApplyClicked(object? sender, RoutedEventArgs args) { if (_model?.TryCreateEvent(out var text, out _) == true) { ResultWml = text; Close(text); } }
    private void CancelClicked(object? sender, RoutedEventArgs args) { ResultWml = null; Close(null); }
    private void SourceClicked(object? sender, RoutedEventArgs args)
    {
        if (_model?.TryCreateEvent(out var text, out _) != true || text is null) return;
        new RawCodeEditorWindow("Event source", text, isReadOnly: true,
            descriptionText: "Connected action order. Apply queues the event; scenario Save checks and writes it.").Show(this);
    }
    private sealed record NodeCard(Border Card, Border Header, TextBlock HeaderText, TextBlock Preview, TextBlock State, Button Input, Button Output, Button Delete);
}