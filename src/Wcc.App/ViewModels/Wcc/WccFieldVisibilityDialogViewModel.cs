using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

public sealed record WccFieldVisibilityCategoryVm(string Label, IReadOnlyList<WccFieldVisibilityOptionVm> Fields);

/// <summary>Display preferences only; read-only documents permit these choices too.</summary>
public sealed class WccFieldVisibilityDialogViewModel : ViewModelBase, IDisposable
{
    private readonly IReadOnlyList<IWccInitialFieldEditorHost> _editors;
    public IReadOnlyList<WccFieldVisibilityCategoryVm> Categories { get; }
    public string Guidance => "Show or hide fields without changing their values. Required fields, authored local values and pending changes stay visible.";

    internal WccFieldVisibilityDialogViewModel(ScenarioWindowViewModel editor, ScenarioInitialFieldOwnerScope? scope)
        : this(new WccInitialFieldEditorHostAdapter(editor), scope) { }

    internal WccFieldVisibilityDialogViewModel(IWccInitialFieldEditorHost editor, ScenarioInitialFieldOwnerScope? scope)
        : this(_ => editor, scope, [editor]) { }
    internal WccFieldVisibilityDialogViewModel(IWccInitialFieldEditorHost side, IWccInitialFieldEditorHost leader)
        : this(scope => scope == ScenarioInitialFieldOwnerScope.Leader ? leader : side,
            ScenarioInitialFieldOwnerScope.Side, new[] { side, leader }.Distinct().ToArray()) { }
    private WccFieldVisibilityDialogViewModel(Func<ScenarioInitialFieldOwnerScope, IWccInitialFieldEditorHost> host,
        ScenarioInitialFieldOwnerScope? scope, IReadOnlyList<IWccInitialFieldEditorHost> editors)
    {
        _editors = editors;
        var scopes = scope == ScenarioInitialFieldOwnerScope.Side
            ? new[] { ScenarioInitialFieldOwnerScope.Side, ScenarioInitialFieldOwnerScope.Leader }
            : scope is { } selected ? new[] { selected } : new[] {
            ScenarioInitialFieldOwnerScope.Scenario, ScenarioInitialFieldOwnerScope.Side, ScenarioInitialFieldOwnerScope.Leader };
        Categories = scopes.SelectMany(selected => ScenarioWindowViewModel.WccPresentationDefinitions(selected)
            .GroupBy(field => field.Group)
            .Select(group => new WccFieldVisibilityCategoryVm(ScopeLabel(selected) + " — " + group.Key,
                group.Select(field => new WccFieldVisibilityOptionVm(host(selected), field)).ToArray()))).ToArray();
        foreach (var editor in _editors) editor.WccFieldVisibilityChanged += VisibilityChanged;
    }

    private static string ScopeLabel(ScenarioInitialFieldOwnerScope scope) => scope switch
    {
        ScenarioInitialFieldOwnerScope.Scenario => "Scenario",
        ScenarioInitialFieldOwnerScope.Side => "Side",
        ScenarioInitialFieldOwnerScope.Leader => "Leader",
        _ => scope.ToString()
    };

    private void VisibilityChanged(object? sender, EventArgs args)
    {
        foreach (var field in Categories.SelectMany(category => category.Fields)) field.Refresh();
    }

    public void Dispose() { foreach (var editor in _editors) editor.WccFieldVisibilityChanged -= VisibilityChanged; }
}

public sealed class WccFieldVisibilityOptionVm : ViewModelBase
{
    private readonly IWccInitialFieldEditorHost _editor;
    public ScenarioInitialFieldDefinition Definition { get; }
    public string Label => Definition.Label;
    public string Key => Definition.Key;
    private IReadOnlyList<WccInitialFieldEditorVm> Rows => _editor.GetWccPresentationRows(Definition.OwnerScope, Key);
    public bool CanChangeVisibility => !_editor.IsRequiredWccInitialField(Definition) && Rows.All(row => row.CanChangeVisibility);
    public bool IsShown
    {
        get => !CanChangeVisibility || _editor.IsWccInitialFieldPreferredVisible(Definition);
        set => _editor.SetWccInitialFieldVisible(Definition.OwnerScope, Key, value);
    }
    public string Reason => _editor.IsRequiredWccInitialField(Definition) ? "Required field — always shown."
        : Rows.FirstOrDefault(row => !row.CanChangeVisibility)?.VisibilityReason
            ?? "Display preference only; no source value will be changed.";

    internal WccFieldVisibilityOptionVm(IWccInitialFieldEditorHost editor, ScenarioInitialFieldDefinition definition)
    { _editor = editor; Definition = definition; }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(CanChangeVisibility));
        OnPropertyChanged(nameof(Reason));
    }
}
