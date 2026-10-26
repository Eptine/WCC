using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed partial class EventNodeGraphWindowViewModel
{
    private readonly IReadOnlyList<WccCampaignLocalPresetEntry> _knownUnitPresets;
    private Func<WccScenarioEventAction, Task<WccScenarioEventAction?>>? _spawnUnitEditor;
    private long _spawnUnitEditorVersion;
    private bool _editingUnit;
    public IReadOnlyList<WccCampaignLocalPresetEntry> KnownUnitPresets => _knownUnitPresets;
    public Func<WccScenarioEventAction, Task<WccScenarioEventAction?>>? SpawnUnitEditor
    {
        get => _spawnUnitEditor;
        set
        {
            if (Equals(_spawnUnitEditor, value)) return;
            _spawnUnitEditor = value; _spawnUnitEditorVersion++;
            OnPropertyChanged(nameof(SpawnUnitEditor)); OnPropertyChanged(nameof(CanEditSelectedUnit));
        }
    }
    public bool CanEditSelectedUnit => CanEdit && !_editingUnit && SelectedRegion is null && SelectedNode.IsSpawnUnit && _spawnUnitEditor is not null;
    public async Task<bool> EditSelectedUnitAsync()
    {
        if (!CanEditSelectedUnit || _spawnUnitEditor is not { } editor) return false;
        var node = SelectedNode; var version = _semanticVersion; var selection = _selectionVersion; var providerVersion = _spawnUnitEditorVersion;
        _editingUnit = true; OnPropertyChanged(nameof(CanEditSelectedUnit));
        try
        {
            var result = await editor(node.ToAction());
            if (!CanEdit || !_nodes.Contains(node) || !ReferenceEquals(node, SelectedNode) || SelectedRegion is not null ||
                version != _semanticVersion || selection != _selectionVersion || providerVersion != _spawnUnitEditorVersion || !ReferenceEquals(editor, _spawnUnitEditor)) return false;
            if (result is null) return false;
            if (result.Kind != WccScenarioEventActionKind.SpawnUnit || result.Unit is not { DifficultyDefine: null } unit || result.Side is not > 0)
                return Refuse("The unit editor must return one spawn unit with a positive side; difficulty belongs to its event region.");
            node.ApplySpawnUnit(unit, result.Side.Value); return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { _editingUnit = false; OnPropertyChanged(nameof(CanEditSelectedUnit)); }
    }
}