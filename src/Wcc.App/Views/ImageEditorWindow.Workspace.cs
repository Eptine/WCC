using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Wcc.App.ViewModels;
using Wcc.App.Views.Controls;
using Wcc.Core.References;

namespace Wcc.App.Views;

public partial class ImageEditorWindow
{
    private static readonly Dictionary<string, Bitmap> WorkspaceIcons = new(StringComparer.Ordinal);
    private ImageEditorModifierVm? _dragLayer;
    private Point _layerPress;
    private bool _layerDragging;
    private ImageEditorModifierVm? _colorTarget;
    private long _colorGeneration;

    private static Image WorkspaceImage(string filename)
    {
        if (!WorkspaceIcons.TryGetValue(filename, out var bitmap))
        {
            using var stream = AssetLoader.Open(new Uri("avares://Wcc.App/Assets/Wcc/Icons/ImageEditor/" + filename));
            WorkspaceIcons.Add(filename, bitmap = new Bitmap(stream));
        }
        return new Image { Source = bitmap, Width = 29, Height = 29, Stretch = Stretch.Uniform };
    }
    private static Button WorkspaceButton(string label, string icon, Action action)
    {
        var button = new Button { Content = WorkspaceImage(icon), Width = 38, Height = 38, Padding = new Thickness(3), Margin = new Thickness(2) };
        ToolTip.SetTip(button, label); AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); return button;
    }
    private void InitializeWorkspace()
    {
        var tools = this.FindControl<StackPanel>("ToolStrip")!;
        foreach (var tool in new[] {
            ("Rotate left", "rotate_left.png", "ROTATE", "270"), ("Rotate right", "rotate_right.png", "ROTATE", "90"),
            ("Flip horizontally", "flip_horizontal.png", "FL", "horiz"), ("Flip vertically", "flip_vertical.png", "FL", "vert"),
            ("Crop", "crop.png", "CROP", (string?)null), ("Resize", "resize.png", "SCALE", (string?)null),
            ("Fit inside", "fit_into.png", "SCALE_INTO", (string?)null) })
            tools.Children.Add(WorkspaceButton(tool.Item1, tool.Item2, () => _model.AddFunction(tool.Item3, tool.Item4)));
        var effects = this.FindControl<WrapPanel>("EffectRack")!;
        foreach (var effect in ImageEditorEffectCatalog.CommonEffects)
        {
            var button = WorkspaceButton(effect.Label, effect.IconFile, () => _model.AddEffect(effect));
            button.Width = button.Height = 34; effects.Children.Add(button);
        }
        var more = new Button { Content = "…", Width = 34, Height = 34, Margin = new Thickness(2) };
        ToolTip.SetTip(more, "Other effects"); AutomationProperties.SetName(more, "Other effects");
        more.Click += OtherEffectsClicked; effects.Children.Add(more);
        _main.ZoomChanged += (_, _) => this.FindControl<TextBlock>("ZoomLabel")!.Text = _main.Zoom.ToString("P0", CultureInfo.InvariantCulture);
        _main.ColorPicked += (_, args) =>
        {
            _main.IsPickingColor = false;
            if (_colorTarget is { } row && ReferenceEquals(row, _model.SelectedModifier) && _model.CanChooseColor && _colorGeneration == _model.DraftGeneration)
                ImageEditorVisualParameters.SetRgb(row, args.Color.R, args.Color.G, args.Color.B);
            _colorTarget = null;
        };
        AddHandler(KeyDownEvent, WorkspaceKeyDown, RoutingStrategies.Tunnel);
        var list = this.FindControl<ListBox>("LayerList")!;
        list.AddHandler(PointerPressedEvent, LayerPressed, RoutingStrategies.Tunnel);
        list.AddHandler(PointerMovedEvent, LayerMoved, RoutingStrategies.Tunnel);
        list.AddHandler(PointerReleasedEvent, LayerReleased, RoutingStrategies.Tunnel);
        list.PointerCaptureLost += (_, _) =>
        { _dragLayer = null; if (_layerDragging) { _layerDragging = false; _model.EndEditGesture(); } list.Cursor = Cursor.Default; };
    }
    private async void OtherEffectsClicked(object? sender, RoutedEventArgs args)
    {
        if (!_model.CanEdit) return;
        var generation = _model.DraftGeneration;
        var dialog = new Window { Title = "Other effects", Width = 470, Height = 430, MinWidth = 360, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Classes.Add("wccShell");
        var panel = new DockPanel { Margin = new Thickness(14) };
        var hint = new TextBlock { Text = "Less common and context-dependent effects. Check the selected effect's help before using it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var rows = new StackPanel { Spacing = 5 };
        foreach (var definition in ImageEditorEffectCatalog.OtherEffects)
        {
            var button = new Button { Content = definition.Label + " · " + definition.Name + "()", HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(button, definition.ParameterHint + "\n" + definition.PreviewNote);
            button.Click += (_, _) => dialog.Close(definition.Name); rows.Children.Add(button);
        }
        panel.Children.Add(new ScrollViewer { Content = rows }); dialog.Content = panel;
        var selected = await dialog.ShowDialog<string?>(this);
        if (selected is not null && _model.CanEdit && generation == _model.DraftGeneration) _model.AddFunction(selected);
    }
    private void VisibilityClicked(object? sender, RoutedEventArgs args)
    { if (_model.CanEdit && sender is Button { Tag: ImageEditorModifierVm row } && _model.Modifiers.Contains(row)) row.IsEnabled = !row.IsEnabled; }
    private void RemoveLayerClicked(object? sender, RoutedEventArgs args)
    { if (sender is Button { Tag: ImageEditorModifierVm row }) _model.RemoveModifier(row); }
    private void ZoomOutClicked(object? sender, RoutedEventArgs args) => _main.ZoomBy(1 / 1.25);
    private void ZoomInClicked(object? sender, RoutedEventArgs args) => _main.ZoomBy(1.25);
    private void ZoomResetClicked(object? sender, RoutedEventArgs args) => _main.ResetZoom();
    private void ZoomFitClicked(object? sender, RoutedEventArgs args) => _main.FitToViewport();
    private void UndoClicked(object? sender, RoutedEventArgs args) => _model.Undo();
    private void RedoClicked(object? sender, RoutedEventArgs args) => _model.Redo();
    private void WorkspaceKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && _main.IsPickingColor) { _main.IsPickingColor = false; _colorTarget = null; args.Handled = true; return; }
        if (!args.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (args.Key is Key.Z or Key.Y)
        {
            // Preserve native text editing undo inside the active numeric/code input.
            if (args.Source is Visual source && (source is TextBox { IsReadOnly: false } || source.GetVisualAncestors().OfType<TextBox>().Any(box => !box.IsReadOnly))) return;
            if (args.Key == Key.Y || args.KeyModifiers.HasFlag(KeyModifiers.Shift)) _model.Redo(); else _model.Undo();
            args.Handled = true; return;
        }
        if (args.Key is Key.Add or Key.OemPlus) _main.ZoomBy(1.25);
        else if (args.Key is Key.Subtract or Key.OemMinus) _main.ZoomBy(1 / 1.25);
        else if (args.Key is Key.D0 or Key.NumPad0) _main.ResetZoom();
        else return;
        args.Handled = true;
    }
    private void PickImageColorClicked(object? sender, RoutedEventArgs args)
    {
        if (!_model.CanChooseColor || _main.Bitmap is null) return;
        _colorTarget = _model.SelectedModifier; _colorGeneration = _model.DraftGeneration;
        _main.IsPickingColor = true; _main.Focus(); _model.ReportStatus("Click a color in the image. Escape cancels the pipette.");
    }
    private void LayerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!_model.CanEdit || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed || args.Source is not Visual source) return;
        if (source is Button || source.GetVisualAncestors().OfType<Button>().Any()) return;
        var item = source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        _dragLayer = item?.DataContext as ImageEditorModifierVm; _layerPress = args.GetPosition((Control)sender!); _layerDragging = false;
    }
    private void LayerMoved(object? sender, PointerEventArgs args)
    {
        if (_dragLayer is null || !_model.CanEdit || sender is not ListBox list) return;
        var point = args.GetPosition(list);
        if (!_layerDragging && Math.Abs(point.Y - _layerPress.Y) < 6) return;
        if (!_layerDragging) { _layerDragging = true; _model.BeginEditGesture(); }
        args.Pointer.Capture(list); list.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        foreach (var item in list.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var origin = item.TranslatePoint(default, list);
            if (origin is { } top && point.Y >= top.Y && point.Y < top.Y + item.Bounds.Height && item.DataContext is ImageEditorModifierVm target)
            { _model.MoveModifier(_dragLayer, _model.Modifiers.IndexOf(target)); break; }
        }
        args.Handled = true;
    }
    private void LayerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (!_layerDragging) { _dragLayer = null; return; }
        args.Handled = true; _dragLayer = null; _layerDragging = false; _model.EndEditGesture(); args.Pointer.Capture(null);
        if (sender is ListBox list) list.Cursor = Cursor.Default;
    }
}
