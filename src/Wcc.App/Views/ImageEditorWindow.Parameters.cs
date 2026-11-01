using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Wcc.App.ViewModels;
using Wcc.App.Views.Controls;
using Wcc.Core.References;

namespace Wcc.App.Views;

public partial class ImageEditorWindow
{
    private ImageEditorModifierVm? _parameterRow;
    private string? _parameterName;
    private readonly List<Action> _parameterUpdates = [];
    private bool _updatingParameters;
    private static Bitmap? _pipetteIcon;
    private bool CanChangeParameter(ImageEditorModifierVm row, string name) =>
        !_updatingParameters && _model.CanEdit && ReferenceEquals(row, _model.SelectedModifier) && row.Name == name;
    private void RefreshParameterControls()
    {
        var root = this.FindControl<StackPanel>("VisualParameters");
        if (root is null) return;
        var row = _model.SelectedModifier;
        if (!ReferenceEquals(row, _parameterRow) || row?.Name != _parameterName)
        {
            _parameterRow = row; _parameterName = row?.Name; root.Children.Clear(); _parameterUpdates.Clear();
            var hasVisual = false;
            if (row is not null)
            {
                var name = row.Name;
                if (row.FlavorChoices.Count > 1)
                {
                    var flavors = new ComboBox { Name = "ImageEditorEffectFlavor", ItemsSource = row.FlavorChoices,
                        SelectedItem = row.Flavor, HorizontalAlignment = HorizontalAlignment.Stretch,
                        ItemTemplate = new FuncDataTemplate<WccImageFunctionDefinition>((definition, _) => new TextBlock { Text = definition?.Label }) };
                    ToolTip.SetTip(flavors, "Choose this effect's variation."); root.Children.Add(flavors);
                    flavors.SelectionChanged += (_, _) =>
                    {
                        if (!CanChangeParameter(row, name) || flavors.SelectedItem is not WccImageFunctionDefinition definition) return;
                        _model.BeginEditGesture(pausePreview: false);
                        try { row.Flavor = definition; } finally { _model.EndEditGesture(); }
                    };
                    _parameterUpdates.Add(() => { flavors.IsEnabled = _model.CanEdit; flavors.SelectedItem = row.Flavor; });
                }
                if (name is "BLEND" or "BG") { hasVisual = true; AddColorControls(root, row); }
                if (ImageEditorVisualParameters.SliderShape(name) is { } shape)
                {
                    hasVisual = true;
                    AddValueControl(root, row, shape.Label + (name is "O" or "BLEND" ? " (0–1)" : ""), "ImageEditorFunction",
                        shape.Minimum, shape.Maximum,
                        () => ImageEditorVisualParameters.TrySlider(row, out var current) ? current.Value : null,
                        value => ImageEditorVisualParameters.SetSlider(row, value), name is "BW" or "NEG" or "ROTATE");
                }
                foreach (var componentShape in ImageEditorVisualParameters.ComponentShapes(name))
                {
                    hasVisual = true; var component = componentShape.Component;
                    AddValueControl(root, row, componentShape.Label, "ImageEditorComponent" + component, componentShape.Minimum, componentShape.Maximum,
                        () => ImageEditorVisualParameters.TryComponent(row, component, out var current) ? current : null,
                        value => ImageEditorVisualParameters.SetComponent(row, component, value), true,
                        name is "CS" or "R" or "G" or "B" or "BL" or "PAD");
                }
                if (name is "SCALE" or "SCALE_SHARP" or "SCALE_INTO" or "SCALE_INTO_SHARP")
                    root.Children.Add(new TextBlock { Text = "0 keeps that source dimension. Hold Shift while dragging to keep the proportions.", FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap });
                if (name == "SWAP") { hasVisual = true; AddChannelControls(root, row); }
                if (name == "XBRZ")
                {
                    hasVisual = true;
                    AddLiteralChoices(root, row, "Scale factor", "ImageEditorXbrzScale", ["2", "3", "4", "5"]);
                }
                if (name == "ROTATE") AddPresetButtons(root, row, [("↶ 90°", "270"), ("180°", "180"), ("↷ 90°", "90")]);
                if (name == "O") AddPresetButtons(root, row, [("25%", "25%"), ("50%", "50%"), ("75%", "75%"), ("100%", "100%")]);
                if (name == "NEG") AddPresetButtons(root, row, [("Invert all", ""), ("Solarize", "128")]);
                if (name is "BLIT" or "MASK" or "L")
                {
                    hasVisual = true;
                    var browse = new Button { Name = "ImageEditorBrowseEffectImage", Content = "Choose effect image…", HorizontalAlignment = HorizontalAlignment.Stretch };
                    WccButtonIcon.SetAction(browse, WccActionIcon.Browse); WccButtonIcon.SetShowLabel(browse, true);
                    browse.Click += BrowseEffectImageClicked; root.Children.Add(browse);
                    var reference = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = .8 }; root.Children.Add(reference);
                    _parameterUpdates.Add(() =>
                    {
                        reference.Text = WccImageFunctionDocument.SplitArguments(row.Arguments).FirstOrDefault(); browse.IsEnabled = _model.CanEdit;
                    });
                }
                if (name == "FL")
                {
                    hasVisual = true;
                    var horizontal = new CheckBox { Name = "ImageEditorFlipHorizontal", Content = "Flip horizontally" };
                    var vertical = new CheckBox { Name = "ImageEditorFlipVertical", Content = "Flip vertically" };
                    root.Children.Add(horizontal); root.Children.Add(vertical);
                    void Apply()
                    { if (CanChangeParameter(row, name)) ImageEditorVisualParameters.SetFlip(row, horizontal.IsChecked == true, vertical.IsChecked == true); }
                    horizontal.IsCheckedChanged += (_, _) => Apply(); vertical.IsCheckedChanged += (_, _) => Apply();
                    _parameterUpdates.Add(() =>
                    {
                        var valid = ImageEditorVisualParameters.TryFlip(row, out var h, out var v);
                        horizontal.IsEnabled = vertical.IsEnabled = valid && _model.CanEdit;
                        if (valid) { horizontal.IsChecked = h; vertical.IsChecked = v; }
                    });
                }
                if (name is "PAL" or "RC")
                {
                    hasVisual = true;
                    AddTextParameter(root, row, "Source palette", "ImageEditorSourcePalette", () => ImageEditorVisualParameters.TryPaletteParts(row, out var source, out _) ? source : null,
                        value => ImageEditorVisualParameters.SetPalettePart(row, true, value));
                    AddTextParameter(root, row, name == "PAL" ? "Destination palette" : "Destination color range", "ImageEditorDestinationPalette",
                        () => ImageEditorVisualParameters.TryPaletteParts(row, out _, out var destination) ? destination : null,
                        value => ImageEditorVisualParameters.SetPalettePart(row, false, value));
                    root.Children.Add(new TextBlock { Text = name == "PAL"
                        ? "Replaces exact colors one by one. Both names must be palettes, e.g. magenta → ellipse_red. Hex color lists also work (FF0000 → 00FF00). Colors absent from the image do not change."
                        : "Recolors the source palette into a color range, e.g. magenta → purple. Named colors use the selected Wesnoth Core palettes.",
                        FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = .75 });
                }
                if (name == "TC")
                {
                    hasVisual = true;
                    AddValueControl(root, row, "Team number", "ImageEditorTeamNumber", 1, int.MaxValue,
                        () => ImageEditorVisualParameters.TryTeamColor(row, out var team, out _) ? team : null,
                        value => ImageEditorVisualParameters.SetTeamColorNumber(row, (int)value), true, showSlider: false);
                    AddTextParameter(root, row, "Source palette", "ImageEditorTeamPalette",
                        () => ImageEditorVisualParameters.TryTeamColor(row, out _, out var palette) ? palette : null,
                        value => ImageEditorVisualParameters.SetTeamColorPalette(row, value));
                    root.Children.Add(new TextBlock { Text = "Usually magenta. Team numbers select the configured Core team colors.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = .75 });
                }
            }
            this.FindControl<Expander>("AdvancedArguments")!.IsExpanded = !hasVisual;
        }
        this.FindControl<Expander>("AdvancedArguments")!.IsVisible = row is not null && ImageEditorVisualParameters.NeedsAdvanced(row);
        _updatingParameters = true;
        try { foreach (var refresh in _parameterUpdates) refresh(); }
        finally { _updatingParameters = false; }
    }

    private void AddTextParameter(StackPanel root, ImageEditorModifierVm row, string label, string controlName, Func<string?> get, Action<string> set)
    {
        var name = row.Name; root.Children.Add(new TextBlock { Text = label, FontSize = 11 });
        var input = new TextBox { Name = controlName, MinHeight = 32 }; root.Children.Add(input);
        AttachParameterGesture(input, row, groupFocus: true);
        input.TextChanged += (_, _) => { if (CanChangeParameter(row, name)) set(input.Text ?? ""); };
        input.LostFocus += (_, _) => RefreshParameterControls();
        _parameterUpdates.Add(() =>
        {
            var value = get(); input.IsEnabled = value is not null && _model.CanEdit;
            if (!input.IsFocused) { input.Text = value ?? ""; input.Watermark = value is null ? "Use Advanced for custom syntax" : ""; }
        });
    }
    private void AddColorControls(StackPanel root, ImageEditorModifierVm row)
    {
        var name = row.Name;
        var picker = new ImageEditorColorPicker { Name = "ImageEditorHsvPicker", HorizontalAlignment = HorizontalAlignment.Stretch };
        AttachParameterGesture(picker, row);
        root.Children.Add(picker);
        picker.ColorChanged += (_, _) =>
        { if (CanChangeParameter(row, name)) ImageEditorVisualParameters.SetRgb(row, picker.Color.R, picker.Color.G, picker.Color.B); };
        var swatch = new Border { Width = 36, Height = 20, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 }; content.Children.Add(swatch); content.Children.Add(label);
        var button = new Button { Name = "ImageEditorColorSwatch", Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += ChooseColorClicked;
        var pipette = new Button { Name = "ImageEditorColorPipette", Content = PipetteContent(), Width = 36, Height = 32 };
        ToolTip.SetTip(pipette, "Pick a color from the preview image; Esc cancels."); pipette.Click += PickImageColorClicked;
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 5 };
        actions.Children.Add(button); Grid.SetColumn(pipette, 1); actions.Children.Add(pipette); root.Children.Add(actions);
        _parameterUpdates.Add(() =>
        {
            var valid = ImageEditorVisualParameters.TryRgb(row, out var red, out var green, out var blue);
            if (valid) picker.Color = Color.FromRgb(red, green, blue);
            picker.IsEnabled = button.IsEnabled = pipette.IsEnabled = _model.CanEdit;
            swatch.Background = valid ? new SolidColorBrush(Color.FromRgb(red, green, blue)) : Brushes.Transparent;
            label.Text = valid ? $"#{red:X2}{green:X2}{blue:X2} · RGB" : "Choose RGB color";
        });
        for (var index = 0; index < 3; index++)
        {
            var component = index;
            AddValueControl(root, row, new[] { "Red", "Green", "Blue" }[index], "ImageEditorRgb" + index, 0, 255,
                () => ImageEditorVisualParameters.TryRgb(row, out var red, out var green, out var blue) ? new double[] { red, green, blue }[component] : null,
                value =>
                {
                    if (!ImageEditorVisualParameters.TryRgb(row, out var red, out var green, out var blue)) return;
                    var channels = new[] { red, green, blue }; channels[component] = (byte)value;
                    ImageEditorVisualParameters.SetRgb(row, channels[0], channels[1], channels[2]);
                }, true);
        }
    }
    private static object PipetteContent()
    {
        try
        {
            if (_pipetteIcon is null)
            {
                using var stream = new StandardAssetLoader().Open(new Uri("avares://Wcc.App/Assets/Wcc/Icons/ImageEditor/colorpicker.png"));
                _pipetteIcon = new Bitmap(stream);
            }
            return new Image { Source = _pipetteIcon, Width = 25, Height = 25, Stretch = Stretch.Uniform };
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return "⌾"; }
    }
    private void AddChannelControls(StackPanel root, ImageEditorModifierVm row)
    {
        var name = row.Name;
        for (var index = 0; index < 4; index++)
        {
            var component = index;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("65,*"), ColumnSpacing = 7 };
            grid.Children.Add(new TextBlock { Text = new[] { "Red", "Green", "Blue", "Alpha" }[index], VerticalAlignment = VerticalAlignment.Center });
            var choice = new ComboBox { Name = "ImageEditorSwapChannel" + index, ItemsSource = new[] { "red", "green", "blue", "alpha" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(choice, 1); grid.Children.Add(choice); root.Children.Add(grid);
            choice.SelectionChanged += (_, _) =>
            { if (CanChangeParameter(row, name) && choice.SelectedItem is string channel) ImageEditorVisualParameters.SetChannel(row, component, channel); };
            _parameterUpdates.Add(() =>
            {
                var valid = ImageEditorVisualParameters.TryChannels(row, out var channels); choice.IsEnabled = valid && _model.CanEdit;
                choice.SelectedItem = valid ? channels[component] : null;
            });
        }
    }
    private void AddLiteralChoices(StackPanel root, ImageEditorModifierVm row, string label, string controlName, string[] values)
    {
        var name = row.Name; root.Children.Add(new TextBlock { Text = label, FontSize = 11 });
        var choice = new ComboBox { Name = controlName, ItemsSource = values, HorizontalAlignment = HorizontalAlignment.Stretch }; root.Children.Add(choice);
        choice.SelectionChanged += (_, _) =>
        { if (CanChangeParameter(row, name) && choice.SelectedItem is string selected && selected != row.Arguments.Trim()) row.Arguments = selected; };
        _parameterUpdates.Add(() => { choice.IsEnabled = _model.CanEdit; choice.SelectedItem = values.FirstOrDefault(value => value == row.Arguments.Trim()); });
    }
    private void AddPresetButtons(StackPanel root, ImageEditorModifierVm row, (string Label, string Arguments)[] presets)
    {
        var name = row.Name; var panel = new WrapPanel(); root.Children.Add(panel);
        foreach (var preset in presets)
        {
            var button = new Button { Content = preset.Label, FontSize = 11, Padding = new Thickness(7, 3), Margin = new Thickness(0, 0, 4, 3) };
            button.Click += (_, _) => { if (CanChangeParameter(row, name)) row.Arguments = preset.Arguments; }; panel.Children.Add(button);
            _parameterUpdates.Add(() => button.IsEnabled = _model.CanEdit);
        }
    }
    private void AddValueControl(StackPanel root, ImageEditorModifierVm row, string label, string controlName,
        double minimum, double maximum, Func<double?> get, Action<double> set, bool integer, bool showSlider = true)
    {
        var name = row.Name;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(showSlider ? "50,*,66" : "*,80"), ColumnSpacing = 6 };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var slider = new Slider { Name = controlName + "Slider", Minimum = minimum, Maximum = maximum, MinWidth = 50, Height = 44, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };
        var number = new TextBox { Name = controlName + "Number", MinWidth = 60, FontSize = 12, Padding = new Thickness(5, 4), Height = 32, VerticalAlignment = VerticalAlignment.Center };
        AttachParameterGesture(slider, row); AttachParameterGesture(number, row, groupFocus: true);
        if (showSlider) { Grid.SetColumn(slider, 1); grid.Children.Add(slider); }
        Grid.SetColumn(number, showSlider ? 2 : 1); grid.Children.Add(number); root.Children.Add(grid);
        ToolTip.SetTip(number, integer ? $"Whole value from {minimum} to {maximum}. Advanced arguments retain custom values." : $"Value from {minimum} to {maximum}. Advanced arguments also accept percentages.");
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property.Name != nameof(Slider.Value) || !CanChangeParameter(row, name)) return;
            set(integer ? Math.Round(slider.Value) : Math.Round(slider.Value, 3));
        };
        number.TextChanged += (_, _) =>
        {
            if (!CanChangeParameter(row, name) || !double.TryParse(number.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value) || value < minimum || value > maximum || integer && value != Math.Truncate(value)) return;
            set(value);
        };
        number.LostFocus += (_, _) => RefreshParameterControls();
        _parameterUpdates.Add(() =>
        {
            var value = get(); slider.IsEnabled = number.IsEnabled = value is not null && _model.CanEdit;
            if (value is { } current)
            {
                slider.Value = current;
                if (!number.IsFocused) number.Text = current.ToString(integer ? "0" : "0.###", CultureInfo.InvariantCulture);
            }
            else if (!number.IsFocused) { number.Text = ""; number.Watermark = "Custom"; }
        });
    }

    private void AttachParameterGesture(Control control, ImageEditorModifierVm row, bool groupFocus = false)
    {
        var active = false;
        void Begin()
        {
            if (active || !_model.CanEdit || !ReferenceEquals(row, _model.SelectedModifier)) return;
            active = true; _model.BeginEditGesture(pausePreview: false);
        }
        void End() { if (active) { active = false; _model.EndEditGesture(); } }
        if (groupFocus)
        { control.GotFocus += (_, _) => Begin(); control.LostFocus += (_, _) => End(); }
        else
        {
            control.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
            { if (args.GetCurrentPoint(control).Properties.IsLeftButtonPressed) Begin(); }, RoutingStrategies.Tunnel);
            control.AddHandler(InputElement.PointerReleasedEvent, (_, _) => End(), RoutingStrategies.Bubble, handledEventsToo: true);
            control.PointerCaptureLost += (_, _) => End();
        }
        control.DetachedFromVisualTree += (_, _) => End();
    }
}
