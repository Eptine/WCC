using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.IO;
using System.Security.Cryptography;
using Wcc.Core.References;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.Views.Controls;

/// <summary>One opt-in editor affordance inside the native text input. No file or model writes.</summary>
public sealed class WccImageEditor : AvaloniaObject
{
    public static readonly AttachedProperty<ImageEditorFieldContext?> ContextProperty =
        AvaloniaProperty.RegisterAttached<WccImageEditor, TextBox, ImageEditorFieldContext?>("Context");
    public static readonly AttachedProperty<string?> FieldKeyProperty =
        AvaloniaProperty.RegisterAttached<WccImageEditor, TextBox, string?>("FieldKey");
    public static readonly AttachedProperty<bool> AlwaysVisibleProperty =
        AvaloniaProperty.RegisterAttached<WccImageEditor, TextBox, bool>("AlwaysVisible");
    public static readonly AttachedProperty<bool> ShowWhenDisabledProperty =
        AvaloniaProperty.RegisterAttached<WccImageEditor, TextBox, bool>("ShowWhenDisabled");
    public static readonly AttachedProperty<Func<bool>?> CommitResultProperty =
        AvaloniaProperty.RegisterAttached<WccImageEditor, TextBox, Func<bool>?>("CommitResult");
    private static readonly ConditionalWeakTable<TextBox, Input> Inputs = new();
    static WccImageEditor()
    {
        ContextProperty.Changed.AddClassHandler<TextBox>((box, _) => Inputs.GetValue(box, value => new Input(value)).Refresh());
        AlwaysVisibleProperty.Changed.AddClassHandler<TextBox>((box, _) => Inputs.GetValue(box, value => new Input(value)).Refresh());
        ShowWhenDisabledProperty.Changed.AddClassHandler<TextBox>((box, _) => Inputs.GetValue(box, value => new Input(value)).Refresh());
        FieldKeyProperty.Changed.AddClassHandler<TextBox>((box, _) => Attach(box,
            () => box.DataContext switch
            {
                MainWindowViewModel editor => editor.CaptureImageEditorContext(GetFieldKey(box) ?? "image"),
                CampaignDifficultyDisplayRow row => row.ImagePreview.EditorContext,
                CampaignCreationImageViewModel image => image.CaptureImageEditorContext(),
                CampaignCreationDifficultyViewModel difficulty => difficulty.Image.CaptureImageEditorContext(),
                _ => null
            }, () => GetFieldKey(box) is { } key &&
                (box.DataContext is not MainWindowViewModel editor || editor.CanEditImageFunctions(key))));
    }
    public static string? GetFieldKey(TextBox box) => box.GetValue(FieldKeyProperty);
    public static void SetFieldKey(TextBox box, string? value) => box.SetValue(FieldKeyProperty, value);
    public static bool GetAlwaysVisible(TextBox box) => box.GetValue(AlwaysVisibleProperty);
    public static void SetAlwaysVisible(TextBox box, bool value) => box.SetValue(AlwaysVisibleProperty, value);
    public static bool GetShowWhenDisabled(TextBox box) => box.GetValue(ShowWhenDisabledProperty);
    public static void SetShowWhenDisabled(TextBox box, bool value) => box.SetValue(ShowWhenDisabledProperty, value);
    public static Func<bool>? GetCommitResult(TextBox box) => box.GetValue(CommitResultProperty);
    public static void SetCommitResult(TextBox box, Func<bool>? value) => box.SetValue(CommitResultProperty, value);
    public static ImageEditorFieldContext? GetContext(TextBox box) => box.GetValue(ContextProperty);
    public static void SetContext(TextBox box, ImageEditorFieldContext? value) => box.SetValue(ContextProperty, value);

    internal static void Attach(TextBox box, Func<ImageEditorFieldContext?> context, Func<bool>? isImage = null)
    {
        var input = Inputs.GetValue(box, value => new Input(value));
        input.ContextFactory = context;
        input.IsImage = isImage;
        input.Refresh();
    }

    internal static string? FormatResult(string? original, string? decoded, string result)
    {
        // The standalone recipe may retain pasted WML quotes/comments. A field binding consumes
        // the exact logical body used by the renderer, then applies its own envelope once.
        result = WccImageExpressionParser.TryParse(result)?.NormalizedExpression ?? result;
        decoded = WccImageExpressionParser.TryParse(decoded)?.NormalizedExpression ?? decoded;
        if (string.Equals(decoded, result, StringComparison.Ordinal)) return original;
        var trimmed = original?.TrimStart();
        var translated = trimmed?.StartsWith("_", StringComparison.Ordinal) == true && trimmed[1..].TrimStart().StartsWith("\"", StringComparison.Ordinal);
        if (trimmed?.StartsWith("\"", StringComparison.Ordinal) == true || translated)
            return (translated ? "_ " : "") + "\"" + result.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return result;
    }
    internal static TextTarget CaptureTextTarget(TextBox box, Func<bool> isCurrent)
    {
        var input = Inputs.GetValue(box, value => new Input(value));
        var version = input.TargetVersion;
        return new(box, () => version == input.TargetVersion && isCurrent(), GetCommitResult(box));
    }

    internal sealed class TextTarget(TextBox box, Func<bool> isCurrent, Func<bool>? commitResult)
    {
        private readonly object? _dataContext = box.DataContext;
        internal string? Original { get; } = box.Text;
        internal bool IsCurrent => isCurrent() && ReferenceEquals(_dataContext, box.DataContext) &&
            !box.IsReadOnly && box.IsEffectivelyEnabled && string.Equals(Original, box.Text, StringComparison.Ordinal);
        internal bool TryApply(string? expression)
        {
            if (expression is null || !IsCurrent) return false;
            box.SetCurrentValue(TextBox.TextProperty, FormatResult(Original,
                WccScenarioFieldValueCodec.DecodeLiteral(Original), expression));
            // Tool Apply is a completed field edit. Buffered inputs can opt in
            // to their normal draft commit without waiting for a later focus event.
            return commitResult?.Invoke() ?? true;
        }
    }

    private static string? ImageHash(string? path)
    {
        if (path is null) return null;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 64L * 1024 * 1024) return null;
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private sealed class Input
    {
        private readonly TextBox _box;
        private readonly Button _button;
        private long _targetVersion;
        private bool _opening;
        public long TargetVersion => _targetVersion;
        public Func<ImageEditorFieldContext?>? ContextFactory { get; set; }
        public Func<bool>? IsImage { get; set; }
        public Input(TextBox box)
        {
            _box = box;
            _button = new Button { Content = "Image editor", Width = 27, MinWidth = 27, Height = 26, MinHeight = 26,
                Padding = new Thickness(2), Margin = new Thickness(0, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
            WccButtonIcon.SetAction(_button, WccActionIcon.ImageEditor);
            WccButtonIcon.SetIconSize(_button, 18);
            AutomationProperties.SetName(_button, "Open image path functions editor");
            ToolTip.SetTip(_button, "Edit image path functions");
            box.InnerRightContent = _button;
            box.PointerEntered += (_, _) => Refresh();
            box.PointerExited += (_, _) => Refresh();
            box.GotFocus += (_, _) => Refresh();
            box.LostFocus += (_, _) => Refresh();
            box.DataContextChanged += (_, _) => { _targetVersion++; Refresh(); };
            box.DetachedFromVisualTree += (_, _) => { _targetVersion++; Refresh(); };
            box.PropertyChanged += (_, change) =>
            {
                if (change.Property == TextBox.TextProperty || change.Property == TextBox.IsReadOnlyProperty ||
                    change.Property == InputElement.IsEnabledProperty || change.Property == ContextProperty || change.Property == FieldKeyProperty ||
                    change.Property == CommitResultProperty) { _targetVersion++; Refresh(); }
                else if (change.Property == ToolTip.TipProperty) Refresh();
            };
            _button.Click += async (_, _) => await OpenAsync();
        }
        public void Refresh()
        {
            var available = !_opening && !_box.IsReadOnly && _box.IsEffectivelyEnabled &&
                (IsImage?.Invoke() ?? GetContext(_box) is not null);
            _button.IsVisible = IsImage?.Invoke() ?? GetContext(_box) is not null;
            _button.IsEnabled = available;
            var shown = (available || GetShowWhenDisabled(_box)) && (GetAlwaysVisible(_box) || _box.IsPointerOver || _box.IsKeyboardFocusWithin);
            _button.Opacity = shown ? 1 : 0;
            _button.IsHitTestVisible = shown && available;
            ToolTip.SetTip(_button, !available && GetShowWhenDisabled(_box)
                ? ToolTip.GetTip(_box) ?? "This image field is read-only. Use its source controls for details."
                : "Edit image path functions");
        }
        private async Task OpenAsync()
        {
            if (_opening || _box.IsReadOnly || !_box.IsEffectivelyEnabled || TopLevel.GetTopLevel(_box) is not Window owner) return;
            var context = ContextFactory?.Invoke() ?? GetContext(_box);
            if (context is null || !context.IsCurrent()) return;
            var target = CaptureTextTarget(_box, () => ReferenceEquals(TopLevel.GetTopLevel(_box), owner) && context.IsCurrent());
            var text = target.Original;
            var expression = WccScenarioFieldValueCodec.DecodeLiteral(text);
            bool Current() => target.IsCurrent;
            if (!Current()) return;
            _opening = true; Refresh();
            try
            {
                var resolution = context.Resolve(expression ?? string.Empty);
                var basis = WccImageExpressionParser.TryParse(expression)?.BaseReference;
                var baseHash = ImageHash(resolution.ResolvedAbsolutePath);
                bool ImageCurrent() => baseHash is not null && baseHash == ImageHash(resolution.ResolvedAbsolutePath);
                async Task<ImageEditorEngineResult> Review(string proposed, CancellationToken token)
                {
                    if (!Current() || !ImageCurrent() || basis != WccImageExpressionParser.TryParse(proposed)?.BaseReference ||
                        !string.Equals(resolution.ResolvedAbsolutePath, context.Resolve(proposed).ResolvedAbsolutePath, StringComparison.Ordinal))
                        return new(false, null, "The source image changed. Reopen the image editor.");
                    var reviewed = await context.RenderForReviewAsync(proposed, token);
                    return ImageCurrent() && Current() ? reviewed : new(false, null, "The source image changed during review.");
                }
                var vm = new ImageEditorWindowViewModel(expression ?? string.Empty, resolution.ResolvedAbsolutePath, Current, Review);
                vm.ConfigureAttachmentRendering(context.InstallPath);
                vm.SetPreviewContext(context.CapturePreviewContext());
                if (context.AllowsNestedReferences) vm.SetPreviewImageResolver(value => context.Resolve(value).ResolvedAbsolutePath);
                var result = await new ImageEditorWindow(vm).ShowDialog<string?>(owner);
                if (result is not null && ImageCurrent()) target.TryApply(result);
            }
            finally { _opening = false; Refresh(); }
        }
    }
}
