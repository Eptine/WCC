using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

/// <summary>Persistent display state; changing it cannot author WML or invalidate a preview.</summary>
public sealed class WccInitialFieldEditorGroup : ViewModelBase
{
    private bool _isExpanded;
    private bool _isDetailsExpanded;
    private IReadOnlyList<WccInitialFieldEditorVm> _fields = Array.Empty<WccInitialFieldEditorVm>();
    public string Label { get; }
    public ScenarioInitialFieldOwnerScope Scope { get; }
    public ScenarioWindowViewModel? Editor { get; }
    public bool IsAppearance => Editor is not null && Scope == ScenarioInitialFieldOwnerScope.Side && Label == "Appearance";
    public bool IsNotAppearance => !IsAppearance;
    // Hidden branches must not bind all their fields and flag controls. Only
    // the side Appearance group supplies the preset/detail branch's context.
    public WccInitialFieldEditorGroup? OrdinaryFieldsGroup => IsAppearance ? null : this;
    public WccInitialFieldEditorGroup? AppearanceFieldsGroup => IsAppearance ? this : null;
    public IReadOnlyList<WccInitialFieldEditorVm> Fields => _fields;
    public ObservableCollection<WccInitialFieldEditorVm> VisibleFields { get; } = new();
    public ObservableCollection<WccInitialFieldEditorVm> SeparateFields { get; } = new();
    public WccInitialFieldEditorVm? CoordinateX { get; private set; }
    public WccInitialFieldEditorVm? CoordinateY { get; private set; }
    public bool HasCoordinatePair => CoordinateX is not null && CoordinateY is not null;
    public ObservableCollection<WccInitialFieldEditorVm> AppearanceMainFields { get; } = new();
    public ObservableCollection<WccInitialFieldEditorVm> AppearanceDetailFields { get; } = new();
    public bool HasAppearanceDetails => AppearanceDetailFields.Count != 0;
    public bool HasVisibleFields => VisibleFields.Count != 0;
    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }
    public bool IsDetailsExpanded { get => _isDetailsExpanded; set => SetField(ref _isDetailsExpanded, value); }

    internal WccInitialFieldEditorGroup(ScenarioWindowViewModel editor, ScenarioInitialFieldOwnerScope scope, string label)
        : this(new WccInitialFieldEditorHostAdapter(editor), scope, label) { }

    internal WccInitialFieldEditorGroup(IWccInitialFieldEditorHost editor, ScenarioInitialFieldOwnerScope scope, string label)
    {
        Editor = editor.ScenarioEditor;
        Scope = scope;
        Label = label;
        _isExpanded = label is "Identity" or "Economy" or "Recruitment" or "Location" ||
            scope == ScenarioInitialFieldOwnerScope.Scenario && label is not ("Runtime" or "Compatibility");
    }

    internal void SetFields(IReadOnlyList<WccInitialFieldEditorVm> fields)
    {
        if (!_fields.SequenceEqual(fields))
        {
            _fields = fields;
            OnPropertyChanged(nameof(Fields));
        }
        RefreshVisibility();
    }

    internal void RefreshVisibility()
    {
        var visible = Fields.Where(row => row.IsVisible).ToArray();
        var hadVisible = HasVisibleFields;
        for (var index = VisibleFields.Count - 1; index >= 0; index--)
            if (!visible.Contains(VisibleFields[index])) VisibleFields.RemoveAt(index);
        for (var index = 0; index < visible.Length; index++)
        {
            var oldIndex = VisibleFields.IndexOf(visible[index]);
            if (oldIndex < 0) VisibleFields.Insert(index, visible[index]);
            else if (oldIndex != index) VisibleFields.Move(oldIndex, index);
        }
        var x = visible.SingleOrDefault(field => field.Key == "x");
        var y = visible.SingleOrDefault(field => field.Key == "y");
        var canPair = x is not null && y is not null && ReferenceEquals(x.Owner, y.Owner);
        if (!ReferenceEquals(CoordinateX, canPair ? x : null) || !ReferenceEquals(CoordinateY, canPair ? y : null))
        {
            CoordinateX = canPair ? x : null; CoordinateY = canPair ? y : null;
            OnPropertyChanged(nameof(CoordinateX)); OnPropertyChanged(nameof(CoordinateY)); OnPropertyChanged(nameof(HasCoordinatePair));
        }
        SyncAppearanceFields(SeparateFields, canPair ? visible.Where(field => field.Key is not ("x" or "y")).ToArray() : visible);
        SyncAppearanceFields(AppearanceMainFields, IsAppearance ? visible.Where(row => row.Key == "color").ToArray() : []);
        var hadDetails = HasAppearanceDetails;
        SyncAppearanceFields(AppearanceDetailFields, IsAppearance ? visible.Where(row => row.Key != "color").ToArray() : []);
        if (hadDetails != HasAppearanceDetails) OnPropertyChanged(nameof(HasAppearanceDetails));
        if (hadVisible != HasVisibleFields) OnPropertyChanged(nameof(HasVisibleFields));
    }

    private static void SyncAppearanceFields(ObservableCollection<WccInitialFieldEditorVm> rows, IReadOnlyList<WccInitialFieldEditorVm> wanted)
    {
        for (var index = rows.Count - 1; index >= 0; index--)
            if (!wanted.Contains(rows[index])) rows.RemoveAt(index);
        for (var index = 0; index < wanted.Count; index++)
        {
            var oldIndex = rows.IndexOf(wanted[index]);
            if (oldIndex < 0) rows.Insert(index, wanted[index]);
            else if (oldIndex != index) rows.Move(oldIndex, index);
        }
    }
}
