using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Views.Controls;
using Wcc.Core.Scenarios;

namespace Wcc.App.Views;

public partial class StoryEditorWindow
{
    private Control BuildTextSidebar(StoryPartVm part)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "Story", FontSize = 18, FontWeight = FontWeight.SemiBold });
        AddField(content, part.Field("text_alignment"), "Alignment", ["left", "center", "right"]);
        AddField(content, part.Field("text_layout"), "Layout", ["top", "middle", "bottom"]);
        content.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#856E40")), Margin = new Thickness(0, 5) });
        content.Children.Add(new TextBlock { Text = "Title", FontSize = 18, FontWeight = FontWeight.SemiBold });
        if (_vm is { } model)
        {
            var first = model.OrderedParts.FirstOrDefault(item => item.AvailableDefines.Contains(model.PreviewDefine));
            if (ReferenceEquals(part, first))
            {
                var all = new CheckBox { Name = "StoryShowTitleOnAll", Content = "Show on all parts", IsChecked = model.ShowTitleOnAll,
                    IsEnabled = model.CanEdit && part.CanEdit && part.Field("title").CanEdit && part.Field("show_title").CanEdit };
                ToolTip.SetTip(all, "Use this slide's title and show-title value on following slides in the selected difficulty scope.");
                all.IsCheckedChanged += (_, _) =>
                {
                    if (!ReferenceEquals(model, _vm) || !ReferenceEquals(model.SelectedPart, part) || !model.CanEdit
                        || !part.CanEdit || !part.Field("title").CanEdit || !part.Field("show_title").CanEdit) return;
                    model.ShowTitleOnAll = all.IsChecked == true; BuildInspector();
                };
                content.Children.Add(all);
            }
            else if (model.TitleLocked) content.Children.Add(new TextBlock
                { Text = "Title follows the first slide. Uncheck its title option to edit here.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        }
        content.Children.Add(BuildStoryBooleanSetting(part, part.Field("show_title"), "Show title", locked: _vm?.TitleLocked == true));
        AddField(content, part.Field("title_alignment"), "Alignment", ["left", "center", "right"]);
        AddTitlePosition(content, part.Field("title_position"));
        return content;
    }

    private Control BuildTextSettings(StoryPartVm part)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(BuildStoryWideTextSetting(part, part.Field("title"), "Title", "StoryTitleInput", locked: _vm?.TitleLocked == true));
        content.Children.Add(BuildStoryWideTextSetting(part, part.Field("story"), "Story", "StoryTextInput", multiline: true));
        return content;
    }

    private Control BuildImageSettings(StoryPartVm part, StoryChildVm image)
    {
        var content = new StackPanel { Spacing = 10 };
        if (image.MacroName is null)
            content.Children.Add(BuildStoryWideTextSetting(part, image.Field("file"), "Image", "StoryImagePathInput",
                pick: () => PickImage(part, image, image.Field("file")), child: image));
        else
        {
            var label = new TextBlock { Text = image.MacroName + (image.IsExpandedPreview ? " · Inherited image" : " · Core travel marker"),
                Opacity = image.CanEdit ? 1 : 0.65, TextWrapping = TextWrapping.Wrap };
            ToolTip.SetTip(label, image.OriginLabel ?? "Whole macro call; only X/Y coordinates are edited here.");
            content.Children.Add(label);
        }
        var settings = StorySettingsGrid();
        AddStorySettingCell(settings, BuildStorySmallTextSetting(part, image.Field("x"), "X", image), 0, 0);
        AddStorySettingCell(settings, BuildStorySmallTextSetting(part, image.Field("y"), "Y", image), 0, 1);
        if (image.MacroName is null)
        {
            AddStorySettingCell(settings, BuildStorySmallTextSetting(part, image.Field("delay"), "Delay (ms)", image), 0, 2);
            AddStorySettingCell(settings, BuildStoryBooleanSetting(part, image.Field("centered"), "Centered anchor", child: image), 1, 0);
            AddStorySettingCell(settings, BuildStoryBooleanSetting(part, image.Field("resize_with_background"), "Resize with background", child: image), 1, 1);
        }
        content.Children.Add(settings);
        if (image.MacroName is not null && !string.IsNullOrWhiteSpace(image.OriginLabel))
        {
            var origin = new TextBlock { Name = "StoryImageOrigin", Text = "Inherited source", Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
            ToolTip.SetTip(origin, image.OriginLabel); content.Children.Add(origin);
        }
        return content;
    }

    private Control BuildBackgroundSettings(StoryPartVm part, StoryChildVm? layer = null)
    {
        var content = new StackPanel { Spacing = 10 };
        var image = layer?.Field("image") ?? part.Field("background");
        content.Children.Add(BuildStoryWideTextSetting(part, image, "Background", "StoryBackgroundPathInput",
            pick: () => PickImage(part, layer, image), child: layer));
        var settings = StorySettingsGrid();
        var fields = layer is null
            ? new[] { ("scale_background", "Scale"), ("scale_background_horizontally", "Scale horizontally"),
                ("scale_background_vertically", "Scale vertically"), ("keep_aspect_ratio", "Keep aspect ratio"),
                ("tile_background", "Tile"), ("tile_background_horizontally", "Tile horizontally"), ("tile_background_vertically", "Tile vertically") }
            : new[] { ("scale", "Scale"), ("scale_horizontally", "Scale horizontally"), ("scale_vertically", "Scale vertically"),
                ("keep_aspect_ratio", "Keep aspect ratio"), ("tile", "Tile"), ("tile_horizontally", "Tile horizontally"),
                ("tile_vertically", "Tile vertically"), ("base_layer", "Base layer") };
        for (var index = 0; index < fields.Length; index++)
        {
            var (key, label) = fields[index];
            AddStorySettingCell(settings, BuildStoryBooleanSetting(part, layer?.Field(key) ?? part.Field(key), label, child: layer), index / 3, index % 3);
        }
        content.Children.Add(settings);
        return content;
    }

    private bool CanEditStorySetting(StoryPartVm part, StoryFieldVm field, bool locked, StoryChildVm? child) =>
        _vm?.CanEdit == true && ReferenceEquals(_vm.SelectedPart, part) && part.CanEdit && field.CanEdit && !locked
        && !((field.Key is "title" or "show_title") && _vm.TitleLocked)
        && (child is null || child.CanEdit && (child.Tag == "image" ? part.Images : part.BackgroundLayers).Contains(child));

    private Control BuildStoryBooleanSetting(StoryPartVm part, StoryFieldVm field, string label, bool locked = false, StoryChildVm? child = null)
    {
        var content = new StackPanel { Spacing = 3 };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 5 };
        var check = new CheckBox { Name = "StoryBoolean_" + field.Key, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            IsThreeState = false, VerticalAlignment = VerticalAlignment.Center };
        var reset = StorySettingReset(part, field, label, locked, child);
        var diamond = StorySettingDiamond(part, field, locked, child);
        var labelGroup = new Grid { Name = "StoryLabel_" + field.Key, ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        labelGroup.Children.Add(check); Grid.SetColumn(diamond, 1); labelGroup.Children.Add(diamond);
        row.Children.Add(labelGroup); Grid.SetColumn(reset, 1); row.Children.Add(reset);
        var state = new TextBlock { FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(row); content.Children.Add(state);
        var syncing = false;
        check.IsCheckedChanged += (_, _) =>
        {
            if (!syncing && check.IsChecked is { } value && CanEditStorySetting(part, field, locked, child)) field.Text = value ? "yes" : "no";
        };
        void Refresh()
        {
            syncing = true;
            try
            {
                var absent = string.IsNullOrEmpty(field.Text);
                var choice = StoryChoiceValue(field, ["yes", "no"]);
                check.IsChecked = choice switch { "yes" => true, "no" => false, _ => null };
                check.IsEnabled = CanEditStorySetting(part, field, locked, child);
                check.Opacity = absent || !field.CanEdit || child?.CanEdit == false ? 0.6 : 1;
                state.Text = absent && choice is not null ? "Default: " + choice : choice is null ? "Custom WML: " + field.Text : "";
                state.IsVisible = !string.IsNullOrEmpty(state.Text);
                ToolTip.SetTip(check, label + (absent ? " · inherited/default" : " · local value") + (choice is null ? ": " + field.Text : ": " + choice));
            }
            finally { syncing = false; }
        }
        ObserveStoryField(content, field, Refresh);
        // With an absent show_title, changing title text changes the engine's
        // display default. Refreshing that checkbox never authors show_title.
        if (field.Key == "show_title") ObserveStoryField(content, part.Field("title"), Refresh);
        AddStoryFieldOrigin(content, field);
        return content;
    }

    private Control BuildStoryWideTextSetting(StoryPartVm part, StoryFieldVm field, string label, string inputName,
        bool multiline = false, bool locked = false, Action? pick = null, StoryChildVm? child = null)
    {
        var content = new StackPanel { Spacing = 3 };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,Auto,Auto"), ColumnSpacing = 7 };
        var labelGroup = new StackPanel { Name = "StoryLabel_" + field.Key, Orientation = Orientation.Horizontal, Spacing = 4,
            VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = new Thickness(0, multiline ? 7 : 0, 0, 0) };
        labelGroup.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 98,
            FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var diamond = StorySettingDiamond(part, field, locked, child); labelGroup.Children.Add(diamond);
        row.Children.Add(labelGroup);
        var box = new TextBox { Name = inputName, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 130 : 32, VerticalAlignment = VerticalAlignment.Stretch };
        box.Bind(TextBox.TextProperty, new Binding(nameof(StoryFieldVm.Text)) { Source = field, Mode = BindingMode.TwoWay });
        ToolTip.SetTip(box, field.Key is "title" or "story" ? "Enter text normally. Quotes and translation formatting are handled automatically. Empty keeps the inherited/default value." : "Wesnoth resource path. Use … to choose or upload an image.");
        Grid.SetColumn(box, 1); row.Children.Add(box);
        if (field.IsPlainText)
        {
            var syncing = false;
            var translate = new CheckBox { Name = "StoryTranslate_" + field.Key, Content = "Translate", VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(translate, 2); row.Children.Add(translate);
            translate.IsCheckedChanged += (_, _) =>
            { if (!syncing && CanEditStorySetting(part, field, locked, child)) field.Translate = translate.IsChecked == true; };
            ObserveStoryField(content, field, () =>
            {
                syncing = true;
                try { translate.IsChecked = field.Translate; translate.IsEnabled = CanEditStorySetting(part, field, locked, child); }
                finally { syncing = false; }
            });
        }
        if (pick is not null)
        {
            var picker = ActionButton("Choose…", () => { if (CanEditStorySetting(part, field, locked, child)) pick(); }, CanEditStorySetting(part, field, locked, child));
            WccButtonIcon.SetAction(picker, WccActionIcon.Browse); WccButtonIcon.SetShowLabel(picker, true);
            picker.Name = "StoryPicker_" + field.Key; picker.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(picker, "Choose or upload " + label.ToLowerInvariant()); Grid.SetColumn(picker, 2); row.Children.Add(picker);
        }
        var reset = StorySettingReset(part, field, label, locked, child); reset.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(reset, 3); row.Children.Add(reset);
        ObserveStoryField(content, field, () =>
        {
            box.IsReadOnly = !CanEditStorySetting(part, field, locked, child);
            box.Opacity = !field.CanEdit || child?.CanEdit == false ? 0.65 : 1;
            var title = field.Key == "title" && _vm is { } model ? model.Session.GetScenarioTitle(model.PreviewDefine) : null;
            box.Watermark = string.IsNullOrEmpty(title) ? "Inherited / default" : title;
        });
        content.Children.Add(row); AddStoryFieldOrigin(content, field);
        return content;
    }

    private Control BuildStorySmallTextSetting(StoryPartVm part, StoryFieldVm field, string label, StoryChildVm child)
    {
        var content = new StackPanel { Spacing = 4 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 5 };
        var reset = StorySettingReset(part, field, label, false, child); var diamond = StorySettingDiamond(part, field, false, child);
        var labelGroup = new StackPanel { Name = "StoryLabel_" + field.Key, Orientation = Orientation.Horizontal, Spacing = 4 };
        labelGroup.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); labelGroup.Children.Add(diamond);
        content.Children.Add(labelGroup);
        Grid.SetColumn(reset, 1); header.Children.Add(reset); content.Children.Add(header);
        var box = new TextBox { Name = "StorySetting_" + field.Key, MinHeight = 32, Watermark = "Inherited / default" };
        box.Bind(TextBox.TextProperty, new Binding(nameof(StoryFieldVm.Text)) { Source = field, Mode = BindingMode.TwoWay }); header.Children.Add(box);
        ObserveStoryField(content, field, () => { box.IsReadOnly = !CanEditStorySetting(part, field, false, child); box.Opacity = !field.CanEdit || !child.CanEdit ? 0.65 : 1; });
        AddStoryFieldOrigin(content, field); return content;
    }

    private Button StorySettingReset(StoryPartVm part, StoryFieldVm field, string label, bool locked, StoryChildVm? child)
    {
        var button = ActionButton("Reset", () => { if (CanEditStorySetting(part, field, locked, child)) field.Text = ""; });
        WccButtonIcon.SetAction(button, WccActionIcon.Reset);
        button.Name = "StoryReset_" + field.Key; button.Padding = new Thickness(5, 2); button.MinWidth = 26;
        ToolTip.SetTip(button, "Reset " + label.ToLowerInvariant() + " to inherited/default in the selected difficulty scope.");
        ObserveStoryField(button, field, () => button.IsEnabled = CanEditStorySetting(part, field, locked, child) && !string.IsNullOrEmpty(field.Text));
        return button;
    }

    private Button StorySettingDiamond(StoryPartVm part, StoryFieldVm field, bool locked, StoryChildVm? child)
    {
        var button = CreateStoryFieldDifficultyButton(field, () =>
        {
            if (CanEditStorySetting(part, field, locked, child)) FieldDifficulties(field);
        }, true);
        ObserveStoryField(button, field, () => button.IsEnabled = CanEditStorySetting(part, field, locked, child));
        return button;
    }

    private Button CreateStoryFieldDifficultyButton(StoryFieldVm field, Action action, bool enabled)
    {
        var button = ActionButton(field.Diamond, action, enabled);
        button.Name = "StoryDifficulty_" + field.Key;
        button.Theme = (ControlTheme)this.FindResource("DifficultyDiamondTheme")!;
        button.Background = Brushes.Transparent; button.BorderBrush = Brushes.Transparent;
        button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(0);
        button.Opacity = 1; button.FocusAdorner = null;
        button.Padding = new Thickness(2, 0); button.MinWidth = 18; button.MinHeight = 20; button.FontSize = 16;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Bind(Button.ContentProperty, new Binding(nameof(StoryFieldVm.Diamond)) { Source = field });
        var tooltip = "Configure " + field.Key.Replace('_', ' ') + " by difficulty.";
        ToolTip.SetTip(button, tooltip); ToolTip.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, tooltip); AutomationProperties.SetHelpText(button, tooltip);
        return button;
    }

    private static Grid StorySettingsGrid()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 16, RowSpacing = 7 };
        grid.PropertyChanged += (_, args) =>
        {
            if (args.Property != BoundsProperty || grid.Bounds.Width <= 0) return;
            var columns = grid.Bounds.Width < 600 ? 2 : 3;
            if (grid.ColumnDefinitions.Count == columns) return;
            grid.ColumnDefinitions = new ColumnDefinitions(columns == 2 ? "*,*" : "*,*,*");
            grid.RowDefinitions.Clear();
            for (var index = 0; index < grid.Children.Count; index++)
            {
                while (grid.RowDefinitions.Count <= index / columns) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(grid.Children[index], index / columns); Grid.SetColumn(grid.Children[index], index % columns);
            }
        };
        return grid;
    }
    private static void AddStorySettingCell(Grid grid, Control control, int row, int column)
    {
        while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(control, row); Grid.SetColumn(control, column); grid.Children.Add(control);
    }
}
