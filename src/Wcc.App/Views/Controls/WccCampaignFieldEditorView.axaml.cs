using Avalonia;
using Avalonia.Input;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Views;

namespace Wcc.App.Views.Controls;

public partial class WccCampaignFieldEditorView : UserControl
{
    public static readonly StyledProperty<double> LabelWidthProperty = AvaloniaProperty.Register<WccCampaignFieldEditorView, double>(nameof(LabelWidth), 130);
    public static readonly StyledProperty<bool> ShowImagePreviewProperty = AvaloniaProperty.Register<WccCampaignFieldEditorView, bool>(nameof(ShowImagePreview), true);
    public static readonly StyledProperty<bool> ShowPresenceProperty = AvaloniaProperty.Register<WccCampaignFieldEditorView, bool>(nameof(ShowPresence), true);
    public static readonly StyledProperty<bool> IsCompactProperty = AvaloniaProperty.Register<WccCampaignFieldEditorView, bool>(nameof(IsCompact));
    public static readonly StyledProperty<string?> LabelTextProperty = AvaloniaProperty.Register<WccCampaignFieldEditorView, string?>(nameof(LabelText));
    public string? LabelText { get => GetValue(LabelTextProperty); set => SetValue(LabelTextProperty, value); }
    public static readonly StyledProperty<object?> FooterContentProperty = AvaloniaProperty.Register<WccCampaignFieldEditorView, object?>(nameof(FooterContent));
    public static readonly StyledProperty<bool> UseInlineChoiceProperty = AvaloniaProperty.Register<WccCampaignFieldEditorView, bool>(nameof(UseInlineChoice));
    public object? FooterContent { get => GetValue(FooterContentProperty); set => SetValue(FooterContentProperty, value); }
    public bool UseInlineChoice { get => GetValue(UseInlineChoiceProperty); set => SetValue(UseInlineChoiceProperty, value); }
    public double LabelWidth { get => GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public bool ShowImagePreview { get => GetValue(ShowImagePreviewProperty); set => SetValue(ShowImagePreviewProperty, value); }
    public bool ShowPresence { get => GetValue(ShowPresenceProperty); set => SetValue(ShowPresenceProperty, value); }
    public bool IsCompact { get => GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelWidthProperty || change.Property == ShowImagePreviewProperty || change.Property == ShowPresenceProperty || change.Property == IsCompactProperty || change.Property == UseInlineChoiceProperty || change.Property == FooterContentProperty || change.Property == LabelTextProperty)
            ApplyFieldLayout();
    }
    private void ApplyFieldLayout()
    {
        if (this.FindControl<TextBlock>("FieldLabel") is not { } label) return;
        label.Width = LabelWidth;
        label.Text = LabelText ?? (DataContext as WccCampaignFieldEditorVm)?.Label;
        var below = IsCompact && DataContext is WccCampaignFieldEditorVm { IsMultiline: true };
        Grid.SetColumnSpan(InputContainer, below ? 3 : 1);
        Grid.SetRow(SourceButton, below ? 1 : 0); Grid.SetRow(FieldActions, below ? 1 : 0);
        var inlineChoice = UseInlineChoice && DataContext is WccCampaignFieldEditorVm { Key: "description_alignment" };
        StaticChoices.IsVisible = !inlineChoice && DataContext is WccCampaignFieldEditorVm { ShowsStaticChoice: true };
        InlineChoicesButton.IsVisible = inlineChoice;
        TextInput.Padding = inlineChoice ? new Thickness(8, 5, 31, 5) : new Thickness(8, 5);
        FooterPresenter.IsVisible = below && FooterContent is not null;
        PresenceText.IsVisible = ShowPresence;
        ImagePreviewBorder.IsVisible = ShowImagePreview && DataContext is WccCampaignFieldEditorVm { ShowsImagePreview: true };
    }
    private readonly Dictionary<TextBox, WccCampaignFieldEditorVm> _textOwners = new();
    private void TextFocusGained(object? sender, GotFocusEventArgs args)
    {
        if (sender is TextBox box && DataContext is WccCampaignFieldEditorVm field) _textOwners[box] = field;
    }
    private void TextFocusLost(object? sender, RoutedEventArgs args)
    {
        if (sender is TextBox box && _textOwners.Remove(box, out var field) && ReferenceEquals(field, DataContext)) field.CommitTextInput();
    }
    private WccCampaignFieldEditorVm? _inlineChoiceField;
    private IReadOnlyList<string>? _inlineChoiceValues;
    private WccCampaignFieldEditorVm? _actionsField;
    private WccCampaignFieldEditorVm? _scenarioChoiceField;
    private IReadOnlyList<string>? _scenarioChoiceValues;
    public WccCampaignFieldEditorView()
    {
        InitializeComponent();
        ApplyFieldLayout();
        InlineChoicesButton.Flyout!.Opened += (_, _) =>
        { _inlineChoiceField = DataContext as WccCampaignFieldEditorVm; _inlineChoiceValues = _inlineChoiceField?.Choices; };
        InlineChoicesButton.Flyout.Closed += (_, _) => { _inlineChoiceField = null; _inlineChoiceValues = null; };
        FieldActions.Flyout!.Opened += (_, _) => _actionsField = DataContext as WccCampaignFieldEditorVm;
        FieldActions.Flyout.Closed += (_, _) => _actionsField = null;
        ScenarioChoicesButton.Flyout!.Opened += (_, _) =>
        { _scenarioChoiceField = DataContext as WccCampaignFieldEditorVm; _scenarioChoiceValues = _scenarioChoiceField?.Choices; };
        ScenarioChoicesButton.Flyout.Closed += (_, _) => { _scenarioChoiceField = null; _scenarioChoiceValues = null; };
        DataContextChanged += (_, _) => { _inlineChoiceField = null; _inlineChoiceValues = null; InlineChoicesButton.Flyout.Hide(); _textOwners.Clear(); _actionsField = null; FieldActions.Flyout.Hide(); ApplyFieldLayout(); _scenarioChoiceField = null; _scenarioChoiceValues = null; ScenarioChoicesButton.Flyout.Hide(); };
    }
    private void InlineChoiceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WccCampaignFieldEditorVm field && ReferenceEquals(field, _inlineChoiceField) &&
            _inlineChoiceValues is { } choices && sender is Button { DataContext: string choice })
            field.TrySetInlineSuggestion(choice, choices);
        InlineChoicesButton.Flyout?.Hide();
    }
    private void ScenarioChoiceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WccCampaignFieldEditorVm field && ReferenceEquals(field, _scenarioChoiceField) &&
            _scenarioChoiceValues is { } choices && sender is Button { DataContext: string choice })
            field.TrySetScenarioSuggestion(choice, choices);
        ScenarioChoicesButton.Flyout?.Hide();
    }
    private WccCampaignFieldEditorVm? CurrentActionField() => ReferenceEquals(DataContext, _actionsField) ? _actionsField : null;
    private void ResetClicked(object? sender, RoutedEventArgs args) => CurrentActionField()?.Reset();
    private void RemoveOverrideClicked(object? sender, RoutedEventArgs args) => CurrentActionField()?.RemoveOverride();
    private void SetDefaultClicked(object? sender, RoutedEventArgs args) => CurrentActionField()?.SetDefault();
    private void FitImageClicked(object? sender, RoutedEventArgs args) => CurrentActionField()?.FitImage();
    private async void SourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not WccCampaignFieldEditorVm { SourceLocation: { } source } field) return;
        var window = new RawCodeEditorWindow("Campaign field source — " + source.SourcePath, source.SourceText,
            isReadOnly: true, descriptionText: source.Description + " " + field.Status,
            initialSelectionStart: source.SelectionStart, initialSelectionLength: source.SelectionLength);
        if (TopLevel.GetTopLevel(this) is Window owner) await window.ShowDialog(owner);
        else window.Show();
    }
}
