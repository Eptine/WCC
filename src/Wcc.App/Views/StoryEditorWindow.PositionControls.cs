using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Wcc.App.Views.Controls;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.Views;

public partial class StoryEditorWindow
{
    private static string? StoryLiteral(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : WccScenarioFieldValueCodec.DecodeLiteral(raw).Trim();

    // These are display defaults. Selecting or inspecting a field never writes them into the draft.
    private string? StoryChoiceValue(StoryFieldVm field, IReadOnlyList<string> choices)
    {
        var literal = StoryLiteral(field.Text);
        if (literal is not null)
        {
            if (choices.Contains(literal)) return literal;
            if ((literal is "true" or "false") && choices.Contains("yes")) return literal == "true" ? "yes" : "no";
            return null;
        }
        return field.Key switch
        {
            "text_alignment" or "title_alignment" => "left",
            "text_layout" => "bottom",
            "show_title" => _vm is { SelectedPart: { } part } model && part.Field("title").Decoded(model.PreviewDefine) is { Length: > 0 } ? "yes" : "no",
            "keep_aspect_ratio" or "scale" or "scale_horizontally" or "scale_vertically" or
                "scale_background" or "scale_background_horizontally" or "scale_background_vertically" => "yes",
            "centered" or "resize_with_background" or "base_layer" or "tile" or "tile_horizontally" or
                "tile_vertically" or "tile_background" or "tile_background_horizontally" or "tile_background_vertically" => "no",
            _ => null
        };
    }

    private static void ObserveStoryField(Control owner, StoryFieldVm field, Action refresh)
    {
        var listening = false;
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(StoryFieldVm.Text) or null or "") refresh();
        }
        void Listen()
        {
            if (!listening) { field.PropertyChanged += Changed; listening = true; }
            refresh();
        }
        owner.AttachedToVisualTree += (_, _) => Listen();
        owner.DetachedFromVisualTree += (_, _) => { if (listening) { field.PropertyChanged -= Changed; listening = false; } };
        Listen();
    }

    private static void AddStoryFieldOrigin(StackPanel section, StoryFieldVm field)
    {
        if (!field.CanEdit && !string.IsNullOrWhiteSpace(field.OriginLabel))
        {
            var origin = new TextBlock { Text = "Inherited value", Opacity = 0.6, FontSize = 11 };
            ToolTip.SetTip(origin, field.OriginLabel); section.Children.Add(origin);
        }
        if (!field.CanEdit && !string.IsNullOrWhiteSpace(field.Diagnostic)) section.Children.Add(new TextBlock
            { Text = field.Diagnostic, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
    }

    private void AddTitlePosition(StackPanel target, StoryFieldVm field)
    {
        var enabled = _vm?.CanEdit == true && _vm.SelectedPart?.CanEdit == true && field.CanEdit;
        var section = new StackPanel { Spacing = 5 };
        var header = new DockPanel();
        var diamond = CreateStoryFieldDifficultyButton(field, () => FieldDifficulties(field), enabled);
        var reset = SmallAction("Reset", "Reset title position to its default", () => field.Text = "", enabled);
        WccButtonIcon.SetAction(reset, WccActionIcon.Reset);
        DockPanel.SetDock(reset, Dock.Right); header.Children.Add(reset);
        var labelGroup = new StackPanel { Name = "StoryLabel_" + field.Key, Orientation = Orientation.Horizontal, Spacing = 4 };
        labelGroup.Children.Add(new TextBlock { Text = "Title position", VerticalAlignment = VerticalAlignment.Center });
        labelGroup.Children.Add(diamond); header.Children.Add(labelGroup);
        section.Children.Add(header);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        var positions = new[] { "left,top", "center,top", "right,top", "left,middle", "centered", "right,middle", "left,bottom", "center,bottom", "right,bottom" };
        var buttons = new List<(Button Button, Ellipse Dot)>();
        for (var index = 0; index < positions.Length; index++)
        {
            var selected = index;
            var dot = new Ellipse { Width = 9, Height = 9, Fill = Brushes.White };
            var button = new Button { Content = dot, Width = 31, Height = 31, Padding = new Thickness(9), IsEnabled = enabled,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center, Tag = positions[index] };
            ToolTip.SetTip(button, positions[index] == "centered" ? "Center" : positions[index].Replace(',', ' '));
            button.Click += (_, _) => { if (enabled) field.Text = "\"" + positions[selected] + "\""; };
            Grid.SetColumn(button, index % 3); Grid.SetRow(button, index / 3); grid.Children.Add(button); buttons.Add((button, dot));
        }
        section.Children.Add(new Border { Child = grid, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#666666")),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left });
        var avoid = new CheckBox { Content = "Avoid story text", IsEnabled = enabled };
        ToolTip.SetTip(avoid, "For the center position, let Wesnoth move the title above story text placed in the middle.");
        var syncing = false;
        avoid.IsCheckedChanged += (_, _) =>
        {
            if (!syncing && enabled) field.Text = avoid.IsChecked == true ? "\"center,middle\"" : "\"centered\"";
        };
        section.Children.Add(avoid);
        var status = new TextBlock { Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        section.Children.Add(status);
        ObserveStoryField(section, field, () =>
        {
            syncing = true;
            try
            {
                var raw = StoryLiteral(field.Text); var position = TitlePositionIndex(raw);
                for (var i = 0; i < buttons.Count; i++)
                {
                    buttons[i].Dot.Fill = i == position ? Brushes.Goldenrod : Brushes.White;
                    buttons[i].Button.Opacity = !field.CanEdit || raw is null && i == position ? 0.6 : 1;
                    buttons[i].Button.BorderBrush = Brushes.Goldenrod;
                    buttons[i].Button.BorderThickness = new Thickness(i == position ? 1 : 0);
                }
                avoid.IsVisible = position == 4; avoid.IsChecked = raw != "centered";
                avoid.Opacity = field.CanEdit ? 1 : 0.6;
                status.Text = raw is null ? "Default: top left" : position < 0 ? "Custom WML position: " + raw : "";
                status.IsVisible = !string.IsNullOrEmpty(status.Text);
            }
            finally { syncing = false; }
        });
        AddStoryFieldOrigin(section, field); target.Children.Add(section);
    }

    private static int TitlePositionIndex(string? value)
    {
        if (value is null or "") return 0;
        if (value == "centered") return 4;
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return 0;
        var horizontal = parts[0] switch { "left" => 0, "center" => 1, "right" => 2, _ => parts.Length == 1 ? 0 : -1 };
        var vertical = (parts.Length > 1 ? parts[1] : parts[0]) switch { "top" => 0, "middle" => 1, "bottom" => 2, "left" or "center" or "right" => 0, _ => -1 };
        return horizontal < 0 || vertical < 0 ? -1 : vertical * 3 + horizontal;
    }
}
