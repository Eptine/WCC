using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.App.Views.Controls;
using Wcc.Core.References;

namespace Wcc.App.Views;

public partial class ImageEditorWindow : Window
{
    private readonly ImageEditorWindowViewModel _model;
    private readonly ImageEditorPreview _main;
    private string? _viewportBaseKey;
    private bool _reviewing;
    public ImageEditorWindow() : this(new ImageEditorWindowViewModel("", standalone: true)) { }
    public ImageEditorWindow(ImageEditorWindowViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        _main = this.FindControl<ImageEditorPreview>("MainPreview")!;
        InitializeWorkspace();
        _main.RectangleChanged += (_, args) => _model.SetGeometry(args.Rectangle);
        _main.InteractionStarted += (_, _) => _model.BeginEditGesture();
        _main.InteractionCompleted += (_, _) => _model.EndEditGesture();
        var advanced = this.FindControl<TextBox>("AdvancedImageArguments")!;
        var editingAdvanced = false;
        advanced.GotFocus += (_, _) =>
        { if (!editingAdvanced && _model.CanEdit) { editingAdvanced = true; _model.BeginEditGesture(pausePreview: false); } };
        advanced.LostFocus += (_, _) =>
        { if (editingAdvanced) { editingAdvanced = false; _model.EndEditGesture(); } };
        model.PropertyChanged += ModelChanged;
        Opened += async (_, _) => { await model.InitializeAsync(); RefreshCanvases(); };
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; _main.Bitmap = null; model.Dispose(); };
        RefreshCanvases();
    }
    public string? ResultExpression { get; private set; }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args) => RefreshCanvases();
    private void RefreshCanvases()
    {
        if (_colorTarget is not null && (!ReferenceEquals(_colorTarget, _model.SelectedModifier) ||
            _colorGeneration != _model.DraftGeneration || !_model.CanChooseColor))
        { _main.IsPickingColor = false; _colorTarget = null; }
        var baseKey = _model.ResolvedImagePath + "|" + _model.BaseSha256;
        if (_model.BaseSha256 is not null && baseKey != _viewportBaseKey) { _viewportBaseKey = baseKey; _main.ResetViewport(); }
        _main.Bitmap = _model.UsesInputCanvas ? _model.InputPreview : _model.Preview;
        _main.ScalePreservesAspect = _model.SelectedModifier?.Name is "SCALE_INTO" or "SCALE_INTO_SHARP";
        _main.InteractionOwner = _model.SelectedModifier; _main.InteractionMode = _model.GeometryMode; _main.Selection = _model.SelectionRectangle;
        _main.CanInteract = _model.CanEditGeometry;
        RefreshParameterControls();
    }
    private void UpClicked(object? sender, RoutedEventArgs args) => _model.MoveSelected(-1);
    private void DownClicked(object? sender, RoutedEventArgs args) => _model.MoveSelected(1);
    private void RemoveClicked(object? sender, RoutedEventArgs args) => _model.RemoveSelected();
    private void CropClicked(object? sender, RoutedEventArgs args) => _model.AddFunction("CROP");
    private void ScaleClicked(object? sender, RoutedEventArgs args) => _model.AddFunction("SCALE");
    private void RotateLeftClicked(object? sender, RoutedEventArgs args) => _model.AddFunction("ROTATE", "270");
    private void RotateRightClicked(object? sender, RoutedEventArgs args) => _model.AddFunction("ROTATE", "90");
    private void OverlayClicked(object? sender, RoutedEventArgs args) => _model.AddFunction("BLEND");
    private void CloseClicked(object? sender, RoutedEventArgs args) => Close(null);
    private async void OpenImageClicked(object? sender, RoutedEventArgs args)
    {
        if (!_model.IsStandalone || !_model.CanEdit) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Open base image", AllowMultiple = false, FileTypeFilter = [new("Images") { Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg", "*.bmp"] }] });
        if (!_model.CanEdit || files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        await _model.LoadImageAsync(path);
    }
    private async void LoadExpressionClicked(object? sender, RoutedEventArgs args)
    {
        if (!_model.CanEdit) return;
        var generation = _model.DraftGeneration;
        var text = new TextBox { Text = _model.Expression, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130 };
        var dialog = new Window { Title = "Load image expression", Width = 700, Height = 300, MinWidth = 440, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Classes.Add("wccShell");
        var accept = new Button { Content = "Load", HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel); actions.Children.Add(accept);
        var panel = new DockPanel { Margin = new Thickness(14), LastChildFill = true };
        var hint = new TextBlock { Text = "Existing and unsupported functions are preserved. Integrated editing keeps the original base image.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions); panel.Children.Add(text); dialog.Content = panel;
        accept.Click += (_, _) => dialog.Close(text.Text ?? ""); cancel.Click += (_, _) => dialog.Close(null);
        var expression = await dialog.ShowDialog<string?>(this);
        if (expression is not null && _model.CanEdit && generation == _model.DraftGeneration) _model.LoadExpression(expression);
    }
    private async void ChooseColorClicked(object? sender, RoutedEventArgs args)
    {
        var selected = _model.SelectedModifier;
        if (selected is null || !_model.CanChooseColor) return;
        var generation = _model.DraftGeneration;
        var values = selected.Arguments.Split(',');
        var dialog = CreateColorWindow(values, out var numbers);
        if (await dialog.ShowDialog<bool>(this) && _model.CanEdit && generation == _model.DraftGeneration && ReferenceEquals(selected, _model.SelectedModifier))
            ImageEditorVisualParameters.SetRgb(selected, byte.Parse(numbers[0].Text!, CultureInfo.InvariantCulture),
                byte.Parse(numbers[1].Text!, CultureInfo.InvariantCulture), byte.Parse(numbers[2].Text!, CultureInfo.InvariantCulture));
    }

    private static Window CreateColorWindow(string[] values, out TextBox[] numbers)
    {
        var dialog = new Window { Title = "RGB color", Width = 470, Height = 345, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        dialog.Classes.Add("wccShell");
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        var swatch = new Border { Height = 45, CornerRadius = new CornerRadius(3) }; panel.Children.Add(swatch);
        var sliders = new Slider[3]; var inputs = new TextBox[3]; numbers = inputs; var syncing = false;
        var use = new Button { Content = "Use color" };
        var validation = new TextBlock { Text = "Enter whole RGB values from 0 to 255.", FontSize = 11 };
        void Refresh()
        {
            var rgb = new byte[3]; var valid = true;
            for (var index = 0; index < 3; index++)
                valid &= byte.TryParse(inputs[index]?.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out rgb[index]);
            swatch.Background = valid ? new SolidColorBrush(Color.FromRgb(rgb[0], rgb[1], rgb[2])) : Brushes.Transparent;
            validation.IsVisible = !valid; use.IsEnabled = valid;
        }
        for (var index = 0; index < 3; index++)
        {
            var component = index;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,64"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock { Text = new[] { "Red", "Green", "Blue" }[index], VerticalAlignment = VerticalAlignment.Center });
            var initial = index < values.Length ? values[index].Trim() : "0";
            var slider = new Slider { Name = "ImageEditorRgbSlider" + index, Minimum = 0, Maximum = 255,
                Value = byte.TryParse(initial, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0 };
            var number = new TextBox { Name = "ImageEditorRgbNumber" + index, Text = initial, MinWidth = 58, MaxLength = 4 };
            sliders[index] = slider; inputs[index] = number; Grid.SetColumn(slider, 1); Grid.SetColumn(number, 2);
            row.Children.Add(slider); row.Children.Add(number); panel.Children.Add(row);
            slider.PropertyChanged += (_, change) =>
            {
                if (syncing || change.Property.Name != nameof(Slider.Value)) return;
                syncing = true; try { number.Text = Math.Round(slider.Value).ToString(CultureInfo.InvariantCulture); } finally { syncing = false; }
                Refresh();
            };
            number.TextChanged += (_, _) =>
            {
                if (syncing) return;
                if (byte.TryParse(number.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var entered))
                { syncing = true; try { sliders[component].Value = entered; } finally { syncing = false; } }
                Refresh();
            };
        }
        panel.Children.Add(validation); Refresh();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel" }; actions.Children.Add(cancel); actions.Children.Add(use); panel.Children.Add(actions);
        cancel.Click += (_, _) => dialog.Close(false); use.Click += (_, _) => dialog.Close(true); dialog.Content = panel;
        return dialog;
    }

    private async void ApplyClicked(object? sender, RoutedEventArgs args)
    {
        if (_reviewing) return;
        _reviewing = true;
        try
        {
            var comparison = await _model.CompareWithEngineAsync();
            if (comparison is null) return;
            if (!await ShowComparisonAsync(comparison)) return;
            var accepted = await _model.AcceptComparisonAsync(comparison);
            if (accepted is null) return;
            if (_model.HasTemporaryResources) { _model.ReportStatus("Temporary effect images can be exported as PNG. Campaign resource paths must be imported before applying the expression."); return; }
            if (_model.IsStandalone)
            {
                if (Clipboard is null) { _model.ReportStatus("Clipboard is unavailable. The reviewed expression is visible below."); return; }
                await Clipboard.SetTextAsync(accepted); _model.ReportStatus("Reviewed expression copied. Continue editing or close this tool.");
            }
            else { ResultExpression = accepted; Close(accepted); }
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { _model.ReportStatus(error.Message); }
        finally { _reviewing = false; }
    }
    private async Task<bool> ShowComparisonAsync(ImageEditorComparison comparison)
    {
        if (!comparison.TryAcquireLease(out var lease)) return false;
        using (lease)
        {
            var dialog = CreateComparisonWindow(comparison);
            return await dialog.ShowDialog<bool>(this);
        }
    }

    private Window CreateComparisonWindow(ImageEditorComparison comparison)
    {
        var dialog = new Window { Title = "Review image expression", Width = 1000, Height = 720, MinWidth = 730, MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Classes.Add("wccShell");
        var grid = new Grid { Margin = new Thickness(14), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), RowSpacing = 9 };
        grid.Children.Add(new TextBlock { Text = "Compare the local preview with Wesnoth's rendered image.", FontSize = 19, TextWrapping = TextWrapping.Wrap });
        var previews = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 }; Grid.SetRow(previews, 1);
        for (var index = 0; index < 2; index++)
        {
            var panel = new DockPanel(); Grid.SetColumn(panel, index);
            var label = new TextBlock { Text = index == 0 ? "Local preview" : "Wesnoth render", Margin = new Thickness(0, 0, 0, 7), HorizontalAlignment = HorizontalAlignment.Center };
            DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
            var canvas = new ImageEditorPreview { Bitmap = index == 0 ? comparison.Local : comparison.Engine };
            canvas.FitToViewport(); panel.Children.Add(canvas); previews.Children.Add(panel);
        }
        grid.Children.Add(previews);
        var notes = new TextBlock { Text = comparison.Notes, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#E9BB70")) };
        var noteScroll = new ScrollViewer { Content = notes, MaxHeight = 100, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(noteScroll, 2); grid.Children.Add(noteScroll);
        var expression = new TextBox { Text = comparison.Expression, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 90 }; Grid.SetRow(expression, 3); grid.Children.Add(expression);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetRow(actions, 4);
        var cancel = new Button { Content = "Back to editor" }; var apply = new Button { Content = _model.IsStandalone ? "Copy expression" : "Use expression" };
        WccButtonIcon.SetAction(cancel, WccActionIcon.Close); WccButtonIcon.SetShowLabel(cancel, true);
        WccButtonIcon.SetAction(apply, WccActionIcon.Accept); WccButtonIcon.SetShowLabel(apply, true);
        var save = new Button { Content = WorkspaceImage("Save_Image.png"), Padding = new Thickness(5) };
        ToolTip.SetTip(save, "Save Wesnoth rendered PNG"); Avalonia.Automation.AutomationProperties.SetName(save, "Save Wesnoth rendered PNG");
        apply.IsEnabled = _model.IsComparisonCurrent(comparison) && !_model.HasTemporaryResources;
        save.IsEnabled = _model.IsComparisonCurrent(comparison);
        void RefreshAcceptance(object? sender, PropertyChangedEventArgs args)
        { apply.IsEnabled = _model.IsComparisonCurrent(comparison) && !_model.HasTemporaryResources; save.IsEnabled = _model.IsComparisonCurrent(comparison); }
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try { await SaveRenderedImageAsync(comparison); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { _model.ReportStatus(error.Message); }
            finally { save.IsEnabled = _model.IsComparisonCurrent(comparison); }
        };
        _model.PropertyChanged += RefreshAcceptance; dialog.Closed += (_, _) => _model.PropertyChanged -= RefreshAcceptance;
        apply.Classes.Add("primary"); cancel.Click += (_, _) => dialog.Close(false); apply.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(save); actions.Children.Add(cancel); actions.Children.Add(apply); grid.Children.Add(actions); dialog.Content = grid;
        return dialog;
    }
}
