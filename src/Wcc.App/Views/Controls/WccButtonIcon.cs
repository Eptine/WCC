using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Wcc.App.Views.Controls;

/// <summary>Explicit action meanings with reviewed, bundled artwork; never inferred from button text.</summary>
public enum WccActionIcon
{
    None, Add, Remove, Delete, Copy, Browse, Info, Help, Close, Undo, Redo,
    Previous, Next, Up, Down, Preview, Play, Pause, Stop, Settings,
    Accept, Edit, EditCode, FieldSettings, Locate, Manage, Preset, Reset, Save, Select, ViewCode
}

public enum WccFieldStatusIcon { None, Absent, Macro, Override, Unresolved, Value }
public enum WccEditorTabIcon { None, InitialSettings, MapEvents }

/// <summary>
/// Opt-in button content presentation. The original Content, bindings, command,
/// native control template and focus behavior remain owned by the button.
/// </summary>
public sealed class WccButtonIcon : AvaloniaObject
{
    public static readonly AttachedProperty<WccActionIcon> ActionProperty =
        AvaloniaProperty.RegisterAttached<WccButtonIcon, Button, WccActionIcon>("Action");
    public static readonly AttachedProperty<bool> ShowLabelProperty =
        AvaloniaProperty.RegisterAttached<WccButtonIcon, Button, bool>("ShowLabel");
    public static readonly AttachedProperty<bool> IsCompactProperty =
        AvaloniaProperty.RegisterAttached<WccButtonIcon, Button, bool>("IsCompact", true);
    public static readonly AttachedProperty<double> IconSizeProperty =
        AvaloniaProperty.RegisterAttached<WccButtonIcon, Button, double>("IconSize", 25);
    public static readonly AttachedProperty<WccFieldStatusIcon> FieldStatusProperty =
        AvaloniaProperty.RegisterAttached<WccButtonIcon, Button, WccFieldStatusIcon>("FieldStatus");
    public static readonly AttachedProperty<bool> IsBlockedProperty =
        AvaloniaProperty.RegisterAttached<WccButtonIcon, Button, bool>("IsBlocked");
    public static readonly AttachedProperty<WccEditorTabIcon> TabProperty =
        AvaloniaProperty.RegisterAttached<WccButtonIcon, Button, WccEditorTabIcon>("Tab");
    public static readonly AttachedProperty<bool> IsSelectedProperty =
        AvaloniaProperty.RegisterAttached<WccButtonIcon, Button, bool>("IsSelected");
    public static readonly AttachedProperty<bool> HasPendingChangesProperty =
        AvaloniaProperty.RegisterAttached<WccButtonIcon, Button, bool>("HasPendingChanges", true);

    private static readonly ConditionalWeakTable<Button, Presentation> Presentations = new();
    private static readonly Dictionary<string, Bitmap> Images = new(StringComparer.Ordinal);

    static WccButtonIcon()
    {
        ActionProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
        ShowLabelProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
        IsCompactProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
        IconSizeProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
        FieldStatusProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
        IsBlockedProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
        TabProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
        IsSelectedProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
        HasPendingChangesProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
    }

    private WccButtonIcon() { }
    public static WccActionIcon GetAction(Button button) => button.GetValue(ActionProperty);
    public static void SetAction(Button button, WccActionIcon action) => button.SetValue(ActionProperty, action);
    public static bool GetShowLabel(Button button) => button.GetValue(ShowLabelProperty);
    public static void SetShowLabel(Button button, bool value) => button.SetValue(ShowLabelProperty, value);
    public static bool GetIsCompact(Button button) => button.GetValue(IsCompactProperty);
    public static void SetIsCompact(Button button, bool value) => button.SetValue(IsCompactProperty, value);
    public static double GetIconSize(Button button) => button.GetValue(IconSizeProperty);
    public static void SetIconSize(Button button, double value) => button.SetValue(IconSizeProperty, value);
    public static WccFieldStatusIcon GetFieldStatus(Button button) => button.GetValue(FieldStatusProperty);
    public static void SetFieldStatus(Button button, WccFieldStatusIcon value) => button.SetValue(FieldStatusProperty, value);
    public static bool GetIsBlocked(Button button) => button.GetValue(IsBlockedProperty);
    public static void SetIsBlocked(Button button, bool value) => button.SetValue(IsBlockedProperty, value);
    public static WccEditorTabIcon GetTab(Button button) => button.GetValue(TabProperty);
    public static void SetTab(Button button, WccEditorTabIcon value) => button.SetValue(TabProperty, value);
    public static bool GetIsSelected(Button button) => button.GetValue(IsSelectedProperty);
    public static void SetIsSelected(Button button, bool value) => button.SetValue(IsSelectedProperty, value);
    public static bool GetHasPendingChanges(Button button) => button.GetValue(HasPendingChangesProperty);
    public static void SetHasPendingChanges(Button button, bool value) => button.SetValue(HasPendingChangesProperty, value);

    private static void Update(Button button)
    {
        if (GetAction(button) == WccActionIcon.None && GetFieldStatus(button) == WccFieldStatusIcon.None && GetTab(button) == WccEditorTabIcon.None)
        {
            if (Presentations.TryGetValue(button, out var prior))
            { Presentations.Remove(button); prior.Dispose(); }
            return;
        }
        Presentations.GetValue(button, static owner => new Presentation(owner)).Refresh();
    }

    private static string AssetPath(Button button)
    {
        const string custom = "avares://Wcc.App/Assets/Wcc/Icons/";
        // Status is explicit presentation metadata, independent of a button's
        // source navigation/edit authority. Static status artwork has no hover state.
        if (GetFieldStatus(button) is { } status && status != WccFieldStatusIcon.None)
            return custom + "FS_" + status + (GetIsBlocked(button) ? "_Blocked" : "") + ".png";
        if (GetTab(button) is { } tab && tab != WccEditorTabIcon.None)
            return custom + (tab == WccEditorTabIcon.InitialSettings ? "IS" : "ME")
                + (!button.IsEffectivelyEnabled ? "_inactive" : GetIsSelected(button) ? "_active"
                    : button.IsPointerOver ? "_hover" : "_inactive") + ".png";
        if (GetAction(button) == WccActionIcon.Save)
            return custom + (!button.IsEffectivelyEnabled || !GetHasPendingChanges(button) ? "save_inactive"
                : button.IsPressed ? "save_pending_pressed" : button.IsPointerOver ? "save_pending_hover" : "save_pending") + ".png";
        var customStem = GetAction(button) switch
        {
            WccActionIcon.Accept => "Accept", WccActionIcon.Edit => "Edit", WccActionIcon.EditCode => "EditCode",
            WccActionIcon.FieldSettings => "FieldSettings", WccActionIcon.Locate => "Locate", WccActionIcon.Manage => "Manage",
            WccActionIcon.Preset => "Preset", WccActionIcon.Reset => "Reset", WccActionIcon.Select => "Select",
            WccActionIcon.ViewCode => "ViewCode", _ => null
        };
        if (customStem is not null)
        {
            var suffix = !button.IsEffectivelyEnabled ? "" : button.IsPressed ? "_pressed" : button.IsPointerOver ? "_hover" : "";
            // Supplied source spelling is preserved for case-sensitive packages.
            if (GetAction(button) == WccActionIcon.Locate && suffix.Length == 0) customStem = "locate";
            if (GetAction(button) == WccActionIcon.ViewCode && suffix == "_pressed") customStem = "VIewCode";
            return custom + customStem + suffix + ".png";
        }
        var stem = GetAction(button) switch
        {
            WccActionIcon.Add => "action/add_25", WccActionIcon.Remove => "action/delete_25",
            WccActionIcon.Delete => "action/trash_25", WccActionIcon.Copy => "action/copy_25",
            WccActionIcon.Browse => "action/browse_25", WccActionIcon.Info => "action/about_25",
            WccActionIcon.Help => "action/help_25", WccActionIcon.Close => "action/close_25",
            WccActionIcon.Undo => "action/undo_25", WccActionIcon.Redo => "action/redo_25",
            WccActionIcon.Previous => "arrows/arrows_left_25", WccActionIcon.Next => "arrows/arrows_right_25",
            WccActionIcon.Up => "arrows/arrows_up_25", WccActionIcon.Down => "arrows/arrows_down_25",
            WccActionIcon.Preview => "action/preview_25", WccActionIcon.Play => "action/play_25",
            WccActionIcon.Pause => "action/pause_25", WccActionIcon.Stop => "action/stop_25",
            WccActionIcon.Settings => "action/settings", _ => throw new ArgumentOutOfRangeException(nameof(button))
        };
        // Disabled actions use normal artwork. Pressed wins over hover; keyboard
        // focus keeps the normal artwork and the native button's focus cue.
        var state = !button.IsEffectivelyEnabled ? "" : button.IsPressed ? "-pressed"
            : button.IsPointerOver ? "-active" : "";
        return "avares://Wcc.App/Assets/Wesnoth/UI/icons/" + stem + state + ".png";
    }

    private static Bitmap? ImageFor(string path)
    {
        lock (Images)
        {
            if (Images.TryGetValue(path, out var bitmap)) return bitmap;
            try
            {
                // These fixed application assets never depend on a live install,
                // package/profile, image-import path or mutable Library record.
                var loader = new StandardAssetLoader();
                using var stream = loader.Open(new Uri(path));
                bitmap = new Bitmap(stream);
                Images.Add(path, bitmap);
                return bitmap;
            }
            catch (Exception failure) when (failure is System.IO.IOException or InvalidOperationException
                or ArgumentException or NotSupportedException)
            { return null; }
        }
    }

    private sealed class Presentation : IDisposable
    {
        private readonly Button _button;
        private readonly IDisposable? _templateLease;
        private readonly List<IDisposable> _compactLeases = [];
        private readonly List<WeakReference<StackPanel>> _views = [];
        private IDisposable? _nameLease, _tipLease;
        private bool _subscribed, _refreshing, _disposed;
        private bool? _canShowIcon;

        public Presentation(Button button)
        {
            _button = button;
            var template = new FuncDataTemplate(_ => true, (_, _) => BuildView());
            // A reversible presentation overlay leaves existing values/bindings
            // in the store. Removing the opt-in reveals their current values.
            _templateLease = button.SetValue(ContentControl.ContentTemplateProperty, template, BindingPriority.Animation);
            button.AttachedToVisualTree += Attached;
            button.DetachedFromVisualTree += Detached;
            Subscribe();
        }

        private Control BuildView()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            panel.Children.Add(new Image { Width = 25, Height = 25, Stretch = Stretch.Uniform, IsHitTestVisible = false });
            panel.Children.Add(new ContentPresenter
            {
                RecognizesAccessKey = true,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
            });
            _views.Add(new(panel));
            RefreshView(panel);
            return panel;
        }

        public void Refresh()
        {
            if (_disposed || _refreshing) return;
            _refreshing = true;
            try
            {
                RefreshViews();
                foreach (var lease in _compactLeases) lease.Dispose();
                _compactLeases.Clear();
                if (GetIsCompact(_button) && !GetShowLabel(_button) && _canShowIcon != false)
                {
                    var width = double.IsNaN(_button.Width) ? 28 : Math.Min(_button.Width, 28);
                    var height = double.IsNaN(_button.Height) ? 28 : Math.Min(_button.Height, 28);
                    Add(_button.SetValue(Layoutable.WidthProperty, width, BindingPriority.Animation));
                    Add(_button.SetValue(Layoutable.MinWidthProperty, width, BindingPriority.Animation));
                    Add(_button.SetValue(Layoutable.HeightProperty, height, BindingPriority.Animation));
                    Add(_button.SetValue(Layoutable.MinHeightProperty, height, BindingPriority.Animation));
                    Add(_button.SetValue(Button.PaddingProperty, new Thickness(0), BindingPriority.Animation));
                }
                // Measure against the current mode, after releasing any compact
                // overlay. Native toolbar and rail dimensions remain owned by XAML.
                RefreshViews();
                RefreshAccessibility();
            }
            finally { _refreshing = false; }
            void Add(IDisposable? lease) { if (lease is not null) _compactLeases.Add(lease); }
        }

        private void RefreshViews()
        {
            for (var index = _views.Count - 1; index >= 0; index--)
                if (_views[index].TryGetTarget(out var view)) RefreshView(view);
                else _views.RemoveAt(index);
        }

        private void RefreshView(StackPanel panel)
        {
            if (_disposed) return;
            var image = (Image)panel.Children[0];
            image.Source = ImageFor(AssetPath(_button));
            image.IsVisible = image.Source is not null;
            var hasDedicatedState = GetFieldStatus(_button) != WccFieldStatusIcon.None || GetTab(_button) != WccEditorTabIcon.None
                || GetAction(_button) == WccActionIcon.Save;
            image.Opacity = _button.IsEffectivelyEnabled || hasDedicatedState ? 1 : 0.45;
            // Keep the complete source canvas. Larger rail artwork is explicit;
            // ordinary field/list icons keep their existing 25px presentation.
            var size = GetIconSize(_button);
            if (!double.IsFinite(size) || size <= 0) size = 25;
            var width = double.IsNaN(_button.Width) ? size : Math.Max(1, _button.Width - _button.BorderThickness.Left - _button.BorderThickness.Right - _button.Padding.Left - _button.Padding.Right);
            var height = double.IsNaN(_button.Height) ? size : Math.Max(1, _button.Height - _button.BorderThickness.Top - _button.BorderThickness.Bottom - _button.Padding.Top - _button.Padding.Bottom);
            image.Width = image.Height = Math.Min(size, Math.Min(width, height));
            var label = (ContentPresenter)panel.Children[1];
            label.ContentTemplate = _button.GetBaseValue(ContentControl.ContentTemplateProperty).GetValueOrDefault();
            label.Content = _button.Content;
            label.IsVisible = GetShowLabel(_button) || image.Source is null;
            var changed = _canShowIcon != image.IsVisible;
            _canShowIcon = image.IsVisible;
            if (changed && !_refreshing) Refresh();
        }

        private void RefreshAccessibility()
        {
            _nameLease?.Dispose(); _nameLease = null;
            _tipLease?.Dispose(); _tipLease = null;
            var fallback = GetFieldStatus(_button) != WccFieldStatusIcon.None ? GetFieldStatus(_button).ToString()
                : GetTab(_button) != WccEditorTabIcon.None ? GetTab(_button).ToString() : GetAction(_button).ToString();
            var label = HumanText(ToolTip.GetTip(_button)) ?? HumanText(_button.Content) ?? fallback;
            if (!_button.IsSet(AutomationProperties.NameProperty)
                && BindingOperations.GetBindingExpressionBase(_button, AutomationProperties.NameProperty) is null)
                _nameLease = _button.SetValue(AutomationProperties.NameProperty, label, BindingPriority.Style);
            if (!_button.IsSet(ToolTip.TipProperty)
                && BindingOperations.GetBindingExpressionBase(_button, ToolTip.TipProperty) is null)
                _tipLease = _button.SetValue(ToolTip.TipProperty, label, BindingPriority.Style);
        }

        private static string? HumanText(object? value) => value is string text && text.Any(char.IsLetterOrDigit) ? text : null;
        private void Subscribe()
        { if (!_subscribed && !_disposed) { _button.PropertyChanged += Changed; _subscribed = true; } }
        private void Attached(object? sender, VisualTreeAttachmentEventArgs args) { Subscribe(); Refresh(); }
        private void Detached(object? sender, VisualTreeAttachmentEventArgs args)
        { _button.PropertyChanged -= Changed; _subscribed = false; }
        private void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (_disposed || _refreshing) return;
            if (args.Property == ContentControl.ContentProperty || args.Property == ContentControl.ContentTemplateProperty || args.Property == ToolTip.TipProperty
                || args.Property == AutomationProperties.NameProperty) Refresh();
            else if (args.Property == Button.IsPressedProperty || args.Property == InputElement.IsPointerOverProperty
                || args.Property == InputElement.IsKeyboardFocusWithinProperty || args.Property == InputElement.IsEffectivelyEnabledProperty)
                RefreshViews();
            else if (args.Property == Layoutable.WidthProperty || args.Property == Layoutable.HeightProperty
                || args.Property == Button.PaddingProperty || args.Property == Button.BorderThicknessProperty)
                RefreshViews();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _button.PropertyChanged -= Changed;
            _button.AttachedToVisualTree -= Attached;
            _button.DetachedFromVisualTree -= Detached;
            _nameLease?.Dispose(); _tipLease?.Dispose();
            foreach (var lease in _compactLeases) lease.Dispose();
            _templateLease?.Dispose();
            _views.Clear();
        }
    }
}
