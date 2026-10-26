using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Views;

namespace Wcc.App.Views.Controls;

public partial class WccInitialFieldEditorView : UserControl
{
    public static readonly StyledProperty<bool> IsCoordinateComponentProperty =
        AvaloniaProperty.Register<WccInitialFieldEditorView, bool>(nameof(IsCoordinateComponent));
    public static readonly StyledProperty<double> PresentationMaximumWidthProperty =
        AvaloniaProperty.Register<WccInitialFieldEditorView, double>(nameof(PresentationMaximumWidth), double.PositiveInfinity);
    public static readonly StyledProperty<HorizontalAlignment> PresentationAlignmentProperty =
        AvaloniaProperty.Register<WccInitialFieldEditorView, HorizontalAlignment>(nameof(PresentationAlignment), HorizontalAlignment.Left);
    public static readonly StyledProperty<bool> ShowsStandaloneCoordinatePickerProperty =
        AvaloniaProperty.Register<WccInitialFieldEditorView, bool>(nameof(ShowsStandaloneCoordinatePicker), true);
    public double PresentationMaximumWidth { get => GetValue(PresentationMaximumWidthProperty); private set => SetValue(PresentationMaximumWidthProperty, value); }
    public HorizontalAlignment PresentationAlignment { get => GetValue(PresentationAlignmentProperty); private set => SetValue(PresentationAlignmentProperty, value); }
    public bool ShowsStandaloneCoordinatePicker { get => GetValue(ShowsStandaloneCoordinatePickerProperty); private set => SetValue(ShowsStandaloneCoordinatePickerProperty, value); }
    internal TextBox CoordinateInput => ValueInput;
    private WccInitialFieldEditorVm? _presentationField;
    private WccInitialFieldEditorVm? _scenarioChoiceField;
    private IReadOnlyList<string>? _scenarioChoiceValues;
    static WccInitialFieldEditorView() => IsCoordinateComponentProperty.Changed
        .AddClassHandler<WccInitialFieldEditorView>((control, _) => control.UpdateCoordinateLayout());
    public bool IsCoordinateComponent { get => GetValue(IsCoordinateComponentProperty); set => SetValue(IsCoordinateComponentProperty, value); }
    public WccInitialFieldEditorView()
    {
        InitializeComponent();
        ChoicesButton.Flyout!.Opened += (_, _) =>
        { _scenarioChoiceField = DataContext as WccInitialFieldEditorVm; _scenarioChoiceValues = _scenarioChoiceField?.Choices; };
        ChoicesButton.Flyout.Closed += (_, _) => { _scenarioChoiceField = null; _scenarioChoiceValues = null; };
        DataContextChanged += (_, _) =>
        {
            _scenarioChoiceField = null; _scenarioChoiceValues = null; ChoicesButton.Flyout.Hide();
            if (_presentationField is not null) _presentationField.PropertyChanged -= PresentationFieldChanged;
            _presentationField = DataContext as WccInitialFieldEditorVm;
            if (_presentationField is not null) _presentationField.PropertyChanged += PresentationFieldChanged;
            UpdateCoordinateLayout();
        };
    }
    private void PresentationFieldChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WccInitialFieldEditorVm.RowMaximumWidth) or nameof(WccInitialFieldEditorVm.RowAlignment))
            UpdatePresentationSize();
    }
    private void UpdatePresentationSize()
    {
        var compact = IsCoordinateComponent;
        PresentationMaximumWidth = compact ? double.PositiveInfinity : _presentationField?.RowMaximumWidth ?? double.PositiveInfinity;
        PresentationAlignment = compact ? HorizontalAlignment.Stretch : _presentationField?.RowAlignment ?? HorizontalAlignment.Left;
        ShowsStandaloneCoordinatePicker = !compact;
    }
    private void UpdateCoordinateLayout()
    {
        UpdatePresentationSize();
        var compact = IsCoordinateComponent;
        FieldGrid.ColumnDefinitions = new(compact ? "*,Auto,Auto" : "135,*,Auto,Auto");
        FieldGrid.ColumnSpacing = compact ? 3 : 5;
        Grid.SetColumnSpan(FieldLabel, compact ? 3 : 1);
        Grid.SetRow(FieldInput, compact ? 1 : 0); Grid.SetColumn(FieldInput, compact ? 0 : 1);
        Grid.SetRow(FieldOrigin, compact ? 1 : 0); Grid.SetColumn(FieldOrigin, compact ? 1 : 2);
        Grid.SetRow(FieldActions, compact ? 1 : 0); Grid.SetColumn(FieldActions, compact ? 2 : 3);
        Grid.SetRow(ResolvedValue, compact ? 2 : 1); Grid.SetColumn(ResolvedValue, compact ? 0 : 1);
        Grid.SetRow(FieldDetails, compact ? 3 : 2); Grid.SetColumn(FieldDetails, compact ? 0 : 1);
        // The text box includes its own border/padding. Center the adjacent
        // compact controls on its row rather than their independent top edges.
        FieldOrigin.VerticalAlignment = VerticalAlignment.Center;
        FieldActions.VerticalAlignment = VerticalAlignment.Center;
        ChoicesButton.VerticalAlignment = VerticalAlignment.Center;
        ChoicesButton.MinWidth = compact ? 20 : 28;
        ChoicesButton.Padding = compact ? new Thickness(2, 3) : new Thickness(5, 3);
        ValueInput.Width = compact ? 64 : double.NaN;
        foreach (var button in new[] { OriginButton, FieldActionsButton })
        {
            button.MinWidth = compact ? 24 : 28;
            button.Padding = compact ? new Thickness(3, 3) : new Thickness(5, 3);
        }
    }
    private void ChoiceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WccInitialFieldEditorVm { CanEdit: true } field &&
            sender is Button { DataContext: string choice })
        {
            if (field.Key == "next_scenario")
            {
                if (ReferenceEquals(field, _scenarioChoiceField) && _scenarioChoiceValues is { } choices)
                    field.TrySetScenarioSuggestion(choice, choices);
            }
            else field.SelectedChoice = choice;
        }
        ChoicesButton.Flyout?.Hide();
    }
    private void ResetClicked(object? sender, RoutedEventArgs args) => (DataContext as WccInitialFieldEditorVm)?.Reset();
    private void RemoveOverrideClicked(object? sender, RoutedEventArgs args) => (DataContext as WccInitialFieldEditorVm)?.RemoveOverride();
    private void SetDefaultClicked(object? sender, RoutedEventArgs args) => (DataContext as WccInitialFieldEditorVm)?.SetDefault();
    private void DifficultyClicked(object? sender, RoutedEventArgs args) => (DataContext as WccInitialFieldEditorVm)?.ToggleDifficulty();

    private async void PickUnitTypesClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not WccInitialFieldEditorVm { CanPickUnitTypes: true } field ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var window = new CatalogPickerWindow(field.CreateUnitTypePicker());
        await window.ShowDialog(owner);
        if (window.SelectedCsv is { } value) field.SetPickedUnitTypes(value);
    }

    private async void PickCoordinateClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WccInitialFieldEditorVm { CanPickCoordinate: true } field &&
            TopLevel.GetTopLevel(this) is Window owner)
            await PresentCoordinatePickerAsync(field, owner);
    }

    internal static Task PresentCoordinatePickerAsync(WccInitialFieldEditorVm field, Window owner) =>
        field.PickCoordinateAsync(async (preview, request) =>
            {
                using var picker = new ScenarioMapCoordinatePickerViewModel(preview, request);
                var window = new ScenarioMapCoordinatePickerWindow(picker);
                await window.ShowDialog(owner);
                return await picker.Completion;
            });

    private async void SourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not WccInitialFieldEditorVm { SourceLocation: { } source } field) return;
        var window = new RawCodeEditorWindow("Field source - " + source.SourcePath, source.SourceText,
            isReadOnly: true, descriptionText: source.Description + " " + field.Status,
            initialSelectionStart: source.SelectionStart, initialSelectionLength: source.SelectionLength);
        if (TopLevel.GetTopLevel(this) is Window owner) await window.ShowDialog(owner);
        else window.Show();
    }
}
