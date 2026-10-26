using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed record WccSideManagementRowVm(string Identity, int Number, string Name, string State,
    bool IsMacro, bool CanRemove, bool CanRestore, string Reason, WccScenarioSideTopologyEntry? Entry)
{
    public string NumberDisplay => Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class WccSideTopologyManagementVm : ViewModelBase, IDisposable
{
    private readonly ScenarioWindowViewModel _editor;
    private WccSideTopologyManagementToken _token;
    private IReadOnlyList<WccSideManagementRowVm> _rows = [];
    private WccSideManagementRowVm? _selected;
    private bool _updating;
    private bool _disposed;
    private string? _operationProblem;

    internal WccSideTopologyManagementVm(ScenarioWindowViewModel editor, WccSideTopologyManagementToken token)
    {
        _editor = editor; _token = token;
        editor.PropertyChanged += EditorPropertyChanged;
        editor.WccSideTopologyDraftsChanged += EditorDraftsChanged;
        RefreshRows();
    }
    public IReadOnlyList<WccSideManagementRowVm> Rows => _rows;
    public WccSideManagementRowVm? SelectedRow
    {
        get => _selected;
        set { if (value is not null && !_rows.Contains(value)) return; if (SetField(ref _selected, value)) RaiseActions(); }
    }
    public string Guidance => "Side numbers follow declaration order. Add appends a basic side; save before editing its fields. "
        + "Removal starts at the final side. Events and map-start references are not remapped.";
    public string? Status => _operationProblem ?? _editor.SideTopologyTokenProblem(_token)
        ?? (!_token.Snapshot.CanAdd && _token.Snapshot.Entries.Count == 0 ? _token.Snapshot.AddDiagnostic : null);
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool CanAdd => !_disposed && _editor.SideTopologyTokenProblem(_token) is null && _token.Snapshot.CanAdd;
    public bool CanAddFromPreset => CanAdd && _editor.CanAddWccSideFromPreset;
    public string PresetGuidance => _editor.WccPresetApplicationGuidance;
    public string AddReason => _token.Snapshot.AddDiagnostic;
    public bool CanRemoveSelected => !_disposed && _editor.SideTopologyTokenProblem(_token) is null && SelectedRow?.CanRemove == true;
    public bool CanRestoreSelected => !_disposed && _editor.SideTopologyTokenProblem(_token) is null && SelectedRow?.CanRestore == true;

    public bool Add() => Change("add");
    public async Task<bool> AddFromPresetAsync(Func<WccCampaignPresetPickerVm, Task<WccCampaignPresetSelection?>> pick)
    {
        if (!CanAddFromPreset) return false;
        _updating = true;
        try
        {
            if (!await _editor.PickWccSidePresetAsync(_token, pick))
            { _operationProblem = _editor.Status; RaiseActions(); return false; }
            _token = _editor.CaptureSideTopologyManagementToken() ?? _token;
            _operationProblem = null; _selected = null; RefreshRows(); return true;
        }
        finally { _updating = false; }
    }
    public bool RemoveSelected() => Change("remove");
    public bool RestoreSelected() => Change("restore");

    private bool Change(string operation)
    {
        if (_disposed) return false;
        _updating = true;
        try
        {
            var identity = SelectedRow?.Identity;
            if (!_editor.StageSideTopologyManagementChange(_token, operation, SelectedRow))
            { _operationProblem = _editor.Status; RaiseActions(); return false; }
            _token = _editor.CaptureSideTopologyManagementToken() ?? _token;
            _operationProblem = null;
            if (operation == "add") { identity = null; _selected = null; }
            RefreshRows(identity);
            return true;
        }
        finally { _updating = false; }
    }

    private void RefreshRows(string? preferred = null)
    {
        _rows = _editor.SideTopologyManagementRows(_token);
        _selected = _rows.FirstOrDefault(row => row.Identity == (preferred ?? _selected?.Identity))
            ?? _rows.LastOrDefault(row => row.State == "Pending addition") ?? _rows.LastOrDefault();
        OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(SelectedRow)); RaiseActions();
    }

    private void EditorDraftsChanged(object? sender, EventArgs args)
    {
        if (_disposed || _updating) return;
        // An external operation does not silently reauthorize an open window.
        RaiseActions();
    }
    private void EditorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || _updating) return;
        if (args.PropertyName is nameof(ScenarioWindowViewModel.CanOpenWccSideManagement)
            or nameof(ScenarioWindowViewModel.IsWccInlineSaveBusy) or nameof(ScenarioWindowViewModel.IsReadOnly)
            or nameof(ScenarioWindowViewModel.SelectedScenario) or nameof(ScenarioWindowViewModel.CanAddWccSideFromPreset)
            or nameof(ScenarioWindowViewModel.WccPresetApplicationGuidance)) RaiseActions();
    }
    private void RaiseActions()
    {
        OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(CanAddFromPreset)); OnPropertyChanged(nameof(PresetGuidance));
        OnPropertyChanged(nameof(CanRestoreSelected)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(HasStatus));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _editor.PropertyChanged -= EditorPropertyChanged;
        _editor.WccSideTopologyDraftsChanged -= EditorDraftsChanged;
    }
}
