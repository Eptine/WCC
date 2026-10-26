using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Views.Controls;
using Wcc.Core.References;

namespace Wcc.App.Views;

public partial class StoryEditorWindow
{
    private int _activeTab;
    private bool _legacyBackgroundSelected;

    private void ResetElementSelection()
    {
        _selectedImage = null; _selectedLayer = null; _legacyBackgroundSelected = false;
        _preview.SelectImage(null);
    }

    private void BuildSlideSidebar()
    {
        Slides.Children.Clear(); DifficultySlides.Children.Clear(); _thumbnails.Clear();
        DifficultySlidesHost.IsVisible = _vm?.HasDifficultyParts == true;
        RefreshSlideActions();
        if (_vm is not { } model) return;
        void AddRows(StackPanel target, IReadOnlyList<StoryPartVm> parts, bool conditional)
        {
            for (var index = 0; index < parts.Count; index++)
            {
                var part = parts[index];
                var image = new Image { Height = 76, Stretch = Stretch.UniformToFill, Source = _preview.Thumbnail(part) };
                _thumbnails[part] = image;
                var label = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxHeight = 40, FontSize = 12 };
                label.Bind(TextBlock.TextProperty, new Binding(nameof(StoryPartVm.DisplayName)) { Source = part });
                var diamond = new TextBlock { FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
                diamond.Bind(TextBlock.TextProperty, new Binding(nameof(StoryPartVm.Diamond)) { Source = part });
                ToolTip.SetTip(diamond, string.Join(", ", model.Session.Snapshot.Contexts.Where(c => part.AvailableDefines.Contains(c.Define)).Select(c => c.DisplayName)));
                var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 5 };
                header.Children.Add(new TextBlock { Text = conditional ? "" : (index + 1).ToString(), FontSize = 12 });
                Grid.SetColumn(label, 1); header.Children.Add(label); Grid.SetColumn(diamond, 2); header.Children.Add(diamond);
                var content = new StackPanel { Spacing = 4 }; content.Children.Add(image); content.Children.Add(header);
                if (conditional) content.Children.Add(new TextBlock { Text = string.Join(", ", model.Session.Snapshot.Contexts.Where(c => part.AvailableDefines.Contains(c.Define)).Select(c => c.DisplayName)), FontSize = 10, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
                var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(5),
                    BorderThickness = new Thickness(ReferenceEquals(part, model.SelectedPart) ? 2 : 1),
                    BorderBrush = ReferenceEquals(part, model.SelectedPart) ? Brushes.Goldenrod : new SolidColorBrush(Color.Parse("#444B50")), Tag = part };
                button.Click += (_, _) => { model.SelectedPart = part; ResetElementSelection(); BuildSlides(); BuildInspector(); };
                target.Children.Add(button);
            }
        }
        AddRows(Slides, model.VisibleParts, false);
        AddRows(DifficultySlides, model.DifficultyParts, true);
    }

    private void RefreshSlideActions()
    {
        AddSlideButton.IsEnabled = _vm?.CanEditTopology == true;
        DuplicateSlideButton.IsEnabled = _vm?.CanDuplicatePart == true;
        ToolTip.SetTip(DuplicateSlideButton, _vm?.DuplicatePartProblem ?? (_vm?.Scope is null
            ? "Duplicate selected slide after it, retaining its difficulties" : "Duplicate selected slide in this difficulty"));
        RemoveSlideButton.IsEnabled = _vm?.CanRemovePart == true;
        SlideUpButton.IsEnabled = _vm?.CanMovePartUp == true;
        SlideDownButton.IsEnabled = _vm?.CanMovePartDown == true;
        ToolTip.SetTip(RemoveSlideButton, _vm?.Scope is null ? "Remove selected slide from all difficulties" : "Remove selected slide from this difficulty");
    }

    private void BuildMockInspector()
    {
        Inspector.Children.Clear(); _travelThumbnails.Clear();
        if (_vm is not { } model) { Settings.Children.Clear(); return; }
        if (model.Session.Snapshot.DefinitionMacroName is { } macro)
        {
            var origin = new TextBlock { Text = "Story macro: " + macro, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
            ToolTip.SetTip(origin, model.Session.Snapshot.DefinitionSourcePath); Inspector.Children.Add(origin);
        }
        if (model.SelectedPart is not { } part)
        {
            Inspector.Children.Add(new TextBlock { Text = "Add a slide to begin.", TextWrapping = TextWrapping.Wrap });
            Settings.Children.Clear(); return;
        }
        if (!part.CanEdit)
        {
            var inherited = new TextBlock { Text = "Inherited slide", Opacity = 0.65, FontSize = 11 };
            ToolTip.SetTip(inherited, part.OriginLabel ?? part.Diagnostic); Inspector.Children.Add(inherited);
        }
        var tabs = new TabControl { Name = "ElementTabs", HorizontalContentAlignment = HorizontalAlignment.Stretch };
        tabs.ItemsSource = new[]
        {
            new TabItem { Header = "Text", Content = BuildTextSidebar(part) },
            new TabItem { Header = "Images", Content = BuildImageSidebar(part) },
            new TabItem { Header = "Audio", Content = BuildAudioSidebar(part) }
        };
        tabs.SelectedIndex = Math.Clamp(_activeTab, 0, 2);
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedIndex < 0) return;
            _activeTab = tabs.SelectedIndex; BuildSelectedSettings();
        };
        Inspector.Children.Add(tabs); BuildSelectedSettings(); RefreshSlideActions();
    }

    private void BuildSelectedSettings()
    {
        Settings.Children.Clear();
        if (_vm?.SelectedPart is not { } part) return;
        if (_activeTab == 0) Settings.Children.Add(BuildTextSettings(part));
        else if (_activeTab == 2) Settings.Children.Add(BuildAudioSettings(part));
        else if (_selectedImage is { } image && part.Images.Contains(image)) Settings.Children.Add(BuildImageSettings(part, image));
        else if (_selectedLayer is { } layer && part.BackgroundLayers.Contains(layer)) Settings.Children.Add(BuildBackgroundSettings(part, layer));
        else if (_legacyBackgroundSelected) Settings.Children.Add(BuildBackgroundSettings(part));
        else Settings.Children.Add(new TextBlock { Text = "Select a background or image to edit its settings, or use + to add one.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
    }

    private Button SmallAction(string symbol, string tooltip, Action action, bool enabled)
    {
        var button = ActionButton(symbol, action, enabled); button.MinWidth = 30; button.Padding = new Thickness(6, 4);
        ToolTip.SetTip(button, tooltip); return button;
    }

    private Control BuildImageSidebar(StoryPartVm part)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(BuildImageStack(part, false));
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#856E40")), Margin = new Thickness(0, 4) });
        panel.Children.Add(BuildImageStack(part, true));
        return panel;
    }

    private Control BuildImageStack(StoryPartVm part, bool images)
    {
        var model = _vm!; var selected = images ? _selectedImage : _selectedLayer;
        var rows = images ? part.Images : part.BackgroundLayers;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = images ? "Images" : "Background", FontWeight = FontWeight.SemiBold, FontSize = 16 });
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"), ColumnSpacing = 4 };
        var add = SmallAction("+", images ? "Add image" : "Add background layer", () =>
        {
            ResetElementSelection(); _activeTab = 1;
            if (images) { _selectedImage = model.AddChild("image"); _preview.SelectImage(_selectedImage); }
            else _selectedLayer = model.AddChild("background_layer");
            BuildInspector();
        }, part.CanEdit && (images ? model.CanEditTopology : model.CanAddLocalBackgroundLayers));
        WccButtonIcon.SetAction(add, WccActionIcon.Add); actions.Children.Add(add);
        var remove = SmallAction("−", "Remove selected " + (images ? "image" : "background"), () =>
        {
            if (!images && _legacyBackgroundSelected) part.Field("background").Text = "";
            else if (selected is not null)
            {
                if (model.Scope is { } scope && (model.CanEditTopology || selected.Id is null))
                {
                    selected.AvailableDefines.Remove(scope); selected.OrderByDefine.Remove(scope);
                    if (selected.AvailableDefines.Count == 0) rows.Remove(selected);
                }
                else rows.Remove(selected);
            }
            ResetElementSelection(); model.Changed(); BuildInspector();
        }, !images && _legacyBackgroundSelected ? model.CanEdit && part.CanEdit && part.Field("background").CanEdit :
            selected is { CanEdit: true } && part.CanEdit && (model.CanEditTopology || images && selected.MacroName is not null && model.CanEditTravelMarkers || !images && selected.Id is null && model.CanAddLocalBackgroundLayers));
        WccButtonIcon.SetAction(remove, WccActionIcon.Remove);
        Grid.SetColumn(remove, 1); actions.Children.Add(remove);
        if (images)
        {
            var presets = SmallAction("P", "Add a Core travel preset", ShowTravelPresets, model.CanEditTravelMarkers && part.CanEdit);
            WccButtonIcon.SetAction(presets, WccActionIcon.Preset);
            Grid.SetColumn(presets, 2); actions.Children.Add(presets);
        }
        var up = SmallAction("↑", "Move selected layer above the previous layer", () => MoveChild(selected!, 1), CanMoveChild(selected, 1));
        var down = SmallAction("↓", "Move selected layer below the next layer", () => MoveChild(selected!, -1), CanMoveChild(selected, -1));
        WccButtonIcon.SetAction(up, WccActionIcon.Up); WccButtonIcon.SetAction(down, WccActionIcon.Down);
        Grid.SetColumn(up, 4); actions.Children.Add(up); Grid.SetColumn(down, 5); actions.Children.Add(down); panel.Children.Add(actions);
        var list = new StackPanel { Spacing = 4 };
        var children = rows.Where(c => model.Scope is null || c.AvailableDefines.Contains(model.Scope))
            .OrderByDescending(c => c.OrderByDefine.GetValueOrDefault(model.PreviewDefine, rows.IndexOf(c))).ToArray();
        foreach (var child in children)
        {
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var field = child.Field(images ? "file" : "image");
            var row = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(7), Tag = child,
                BorderThickness = new Thickness(ReferenceEquals(child, selected) ? 2 : 1), BorderBrush = ReferenceEquals(child, selected) ? Brushes.Goldenrod : new SolidColorBrush(Color.Parse("#444B50")) };
            ObserveStoryField(row, field, () =>
            {
                var decoded = StoryLiteral(field.Text);
                var path = child.MacroName ?? WccImageExpressionParser.TryParse(decoded)?.BaseReference ?? decoded;
                label.Text = child.MacroName ?? (string.IsNullOrWhiteSpace(path) ? "New " + (images ? "image" : "background") : PathName(path));
                if (child.Fields.Any(f => f.IsIndependent) || child.AvailableDefines.Count != part.AvailableDefines.Count) label.Text += "  ◆";
                label.Opacity = child.CanEdit ? 1 : 0.6;
                ToolTip.SetTip(row, field.Text + (child.CanEdit ? "" : "\nInherited: " + (child.OriginLabel ?? field.OriginLabel)));
            });
            row.Click += (_, _) =>
            {
                ResetElementSelection(); _activeTab = 1;
                if (images) { _selectedImage = child; _preview.SelectImage(child); } else _selectedLayer = child;
                BuildInspector();
            };
            list.Children.Add(row);
        }
        if (!images && part.Field("background").Text is { Length: > 0 })
        {
            var label = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var row = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(7), BorderThickness = new Thickness(_legacyBackgroundSelected ? 2 : 1), BorderBrush = _legacyBackgroundSelected ? Brushes.Goldenrod : new SolidColorBrush(Color.Parse("#444B50")) };
            ObserveStoryField(row, part.Field("background"), () => { label.Text = PathName(StoryLiteral(part.Field("background").Text) ?? "Slide background"); label.Opacity = part.Field("background").CanEdit ? 1 : 0.6; });
            ToolTip.SetTip(row, "Slide background · below background layers");
            row.Click += (_, _) => { ResetElementSelection(); _legacyBackgroundSelected = true; _activeTab = 1; BuildInspector(); };
            list.Children.Add(row);
        }
        if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = images ? "No images" : "No background layers", Opacity = 0.6, FontSize = 12, Margin = new Thickness(6) });
        panel.Children.Add(new Border { Child = new ScrollViewer { Content = list, MaxHeight = images ? 230 : 200 }, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#444B50")), CornerRadius = new CornerRadius(4), Padding = new Thickness(4) });
        panel.Children.Add(new TextBlock { Text = "Top row is drawn on top.", Opacity = 0.55, FontSize = 10 });
        return panel;
    }

    private static string PathName(string path) => path.Replace('\\', '/').Split('/').LastOrDefault() ?? path;

    private bool CanMoveChild(StoryChildVm? child, int delta)
    {
        if (_vm is not { CanEditTopology: true, SelectedPart: { CanEdit: true } part } model || child is not { CanEdit: true }) return false;
        var rows = child.Tag == "image" ? part.Images : part.BackgroundLayers;
        var ordered = rows.Where(c => c.AvailableDefines.Contains(model.PreviewDefine)).OrderBy(c => c.OrderByDefine.GetValueOrDefault(model.PreviewDefine, rows.IndexOf(c))).ToArray();
        var index = Array.IndexOf(ordered, child);
        return index >= 0 && index + delta >= 0 && index + delta < ordered.Length;
    }

    private async void ShowTravelPresets()
    {
        if (_vm is not { } model || model.SelectedPart is not { } part) return;
        var epoch = _hostEpoch; var scope = model.Scope;
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(14) };
        var dialog = new Window { Title = "Core travel presets", Width = 390, Height = 470, Content = new ScrollViewer { Content = panel } };
        panel.Children.Add(new TextBlock { Text = "Choose a marker, then drag it on the slide.", TextWrapping = TextWrapping.Wrap });
        foreach (var macro in Wcc.Core.Scenarios.WccScenarioStoryPlanner.SupportedTravelMacros)
        {
            var icon = new Image { Width = 36, Height = 36, Source = _preview.TravelThumbnail(macro), Stretch = Stretch.Uniform };
            _travelThumbnails[macro] = icon;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(icon); content.Children.Add(new TextBlock { Text = macro.Replace('_', ' '), VerticalAlignment = VerticalAlignment.Center });
            var button = ActionButton(macro, () =>
            {
                if (epoch != _hostEpoch || !ReferenceEquals(_vm, model) || !ReferenceEquals(model.SelectedPart, part) || model.Scope != scope || !model.CanEditTravelMarkers) { dialog.Close(); return; }
                ResetElementSelection(); _activeTab = 1; _selectedImage = model.AddTravelPreset(macro); _preview.SelectImage(_selectedImage); dialog.Close(); BuildInspector();
            }, model.CanEditTravelMarkers && part.CanEdit);
            button.Content = content; panel.Children.Add(button);
        }
        await dialog.ShowDialog(this);
    }
}
