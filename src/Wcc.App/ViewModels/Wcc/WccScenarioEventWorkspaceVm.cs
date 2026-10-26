using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>Presentation of the current event projection; rows retain exact projection references.</summary>
public sealed class WccScenarioEventWorkspaceVm : ViewModelBase, IDisposable
{
    private ScenarioMapPreviewVm? _map;
    private IReadOnlyList<WccScenarioEventRowVm> _rows = [];
    private bool _rebuildingRows;
    private bool _disposed;
    private bool _canAdd;

    public WccScenarioEventWorkspaceVm(ScenarioWindowViewModel editor)
    {
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Editor.PropertyChanged += EditorChanged;
        BindMap();
    }

    public ScenarioWindowViewModel Editor { get; }
    public ScenarioMapPreviewVm? Map => _map;
    public IReadOnlyList<WccScenarioEventRowVm> Rows => _rows;
    public string Summary => Map?.EventListSummary ?? "No event preview is available for this scenario.";
    public bool HasRows => Rows.Count != 0;
    public bool CanAdd => !_disposed && _canAdd;
    public bool CanAddDialogue => CanAdd && Editor.CanAddWccDialogueGraph;
    public bool CanApplyEventPreset => !_disposed && Editor.CanAddWccEventFromPreset;
    public bool CanManageEventPresets => !_disposed && Editor.CanManageWccEventPresets;
    public bool CanEditDialogue => !_disposed && Editor.CanAddWccDialogueGraph && SelectedRow?.CanEditDialogue == true;
    public string DialogueEditTooltip => SelectedRow?.DialogueReason ?? "Select a local event with supported triggers and actions.";
    public bool HasSelection => SelectedRow is not null;
    public string EditActionLabel => SelectedRow is { CanEdit: true } row
        ? "Edit WML"
        : "View WML";

    public WccScenarioEventRowVm? SelectedRow
    {
        get => Rows.FirstOrDefault(row => ReferenceEquals(row.Entry, Map?.SelectedEvent));
        set
        {
            if (_disposed || _rebuildingRows || Map is null ||
                value is not null && !Rows.Any(row => ReferenceEquals(row, value))) return;
            Map.SelectedEvent = value?.Entry;
        }
    }

    internal WccEventEditDraft? BeginSelectedDraft() => SelectedRow is { } row ? BeginDraft(row) : null;
    public WccEventDialogueGraphDraft? BeginNewDialogue() => !_disposed ? Editor.BeginWccDialogueGraphAdd() : null;
    public WccEventDialogueGraphDraft? BeginSelectedDialogue() => !_disposed && SelectedRow is { CanEditDialogue: true } row
        ? Editor.BeginWccDialogueGraphEdit(row.Entry) : null;
    internal WccEventEditDraft? BeginDraft(WccScenarioEventRowVm row) => !_disposed &&
        Rows.Any(current => ReferenceEquals(current, row)) &&
        Map?.FilteredEvents.Any(entry => ReferenceEquals(entry, row.Entry)) == true
            ? Editor.BeginWccEventDraft(row.Entry) : null;

    private void EditorChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (_disposed) return;
        if (change.PropertyName == nameof(ScenarioWindowViewModel.EventWorkspacePreview)) BindMap();
        else if (change.PropertyName is "HasWccEventDrafts" or "WccEventDrafts" or "CanEditDocument" or
                 "IsReadOnly" or "IsWccInlineSaveBusy" or nameof(ScenarioWindowViewModel.CanAddWccDialogueGraph) or
                 nameof(ScenarioWindowViewModel.CanAddWccEventFromPreset) or nameof(ScenarioWindowViewModel.CanManageWccEventPresets))
            RefreshActions();
    }

    private void BindMap()
    {
        if (_map is not null) _map.PropertyChanged -= MapChanged;
        _map = Editor.EventWorkspacePreview;
        if (_map is not null) _map.PropertyChanged += MapChanged;
        OnPropertyChanged(nameof(Map));
        RebuildRows();
    }

    private void MapChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (_disposed) return;
        if (change.PropertyName is nameof(ScenarioMapPreviewVm.FilteredEvents) or
            nameof(ScenarioMapPreviewVm.EventProjection)) RebuildRows();
        else if (change.PropertyName == nameof(ScenarioMapPreviewVm.SelectedEvent)) RefreshSelection();
        else if (change.PropertyName == nameof(ScenarioMapPreviewVm.EventListSummary)) OnPropertyChanged(nameof(Summary));
    }

    private void RebuildRows()
    {
        _rebuildingRows = true;
        try
        {
            var previous = _rows;
            _rows = Map?.FilteredEvents.Select(entry =>
                previous.FirstOrDefault(row => ReferenceEquals(row.Entry, entry)) ?? new WccScenarioEventRowVm(this, entry))
                .ToArray() ?? [];
            foreach (var row in previous.Where(row => !_rows.Any(current => ReferenceEquals(current, row))))
                row.ClearDisplayEligibility();
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(Summary));
            RefreshActions();
        }
        finally { _rebuildingRows = false; }
    }

    private void RefreshActions()
    {
        _canAdd = !_disposed && Editor.BeginWccNewEventDraft()?.CanEdit == true;
        foreach (var row in Rows) row.Refresh();
        RefreshSelection();
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanAddDialogue));
        OnPropertyChanged(nameof(CanApplyEventPreset)); OnPropertyChanged(nameof(CanManageEventPresets));
    }

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(EditActionLabel));
        OnPropertyChanged(nameof(CanEditDialogue));
        OnPropertyChanged(nameof(DialogueEditTooltip));
        foreach (var row in Rows) row.RefreshSelection();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Editor.PropertyChanged -= EditorChanged;
        if (_map is not null) _map.PropertyChanged -= MapChanged;
        var previous = _rows;
        _map = null;
        _rows = [];
        foreach (var row in previous) row.ClearDisplayEligibility();
        OnPropertyChanged(nameof(Map));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Summary));
        RefreshActions();
    }
}

public sealed class WccScenarioEventRowVm : ViewModelBase
{
    private readonly WccScenarioEventWorkspaceVm _workspace;
    private WccEventEditDraft? _displayDraft;
    private string? _dialogueSource;
    private string? _dialogueDifficultySignature;
    private IReadOnlyList<WccCampaignLocalPresetEntry> _dialogueUnitPresets = [];
    private bool _supportsDialogue;
    private string? _dialogueProblem;

    internal WccScenarioEventRowVm(WccScenarioEventWorkspaceVm workspace, ScenarioEventProjectionEntry entry)
    {
        _workspace = workspace;
        Entry = entry;
    }

    public ScenarioEventProjectionEntry Entry { get; }
    public string Name
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Entry.WmlId)) return Entry.WmlId;
            if (Entry.SourceKind == ScenarioEventSourceKind.CapturedStaticEvaluation) return "Captured event";
            if (_displayDraft is { IsMacroGroup: true } draft &&
                WmlMacroInvocationSyntaxScanner.TryParseSingle(draft.Text.Trim(), out var invocation) && invocation is not null)
                return "{" + invocation.Name + "}";
            return "Event at line " + (Entry.AuthoritativeLine ?? Entry.ProjectedLine);
        }
    }
    public string Trigger => Entry.TriggerDisplay;
    public string ShortEffects => Entry.EffectKinds.Count <= 3 ? Entry.EffectDisplay
        : string.Join(", ", Entry.EffectDisplay.Split(", ").Take(3)) + " +" + (Entry.EffectKinds.Count - 3);
    public bool IsMacroGroup => _displayDraft?.IsMacroGroup == true;
    public bool IsSelected => ReferenceEquals(_workspace.SelectedRow, this);
    public bool CanEdit => _displayDraft?.CanEdit == true;
    public bool CanRemove => _displayDraft?.CanRemove == true;
    public bool CanEditDialogue => CanEdit && !IsMacroGroup && _supportsDialogue;
    public string DialogueReason => CanEditDialogue ? "Edit this local event with supported triggers and actions. Apply queues the event; Save writes it."
        : _displayDraft is not { CanEdit: true, IsMacroGroup: false } ? Editability
        : _dialogueProblem ?? "Additional WML stays available through RAW editing.";
    public string RemovalTooltip => _displayDraft?.RemovalSummary ??
        "This projected event has no removable local source fragment.";
    public string SourceTooltip => Entry.SourceDisplay + "\n" + Entry.RepeatDisplay + "\n" + Entry.NestingDisplay +
        (Entry.SourceKind == ScenarioEventSourceKind.MacroGenerated && !IsMacroGroup
            ? "\nGenerated output has no exact local whole-invocation group here." : "");
    public string MacroTooltip => "Macro-produced event. RAW shows the containing invocation. A local invocation edit changes the whole event group; removal removes that group. The shared definition stays unchanged.";
    public string DetailSummary => Trigger + " · " + Entry.TriggerFamilyDisplay + " · " + Entry.RepeatDisplay + " · " + Entry.NestingDisplay;
    public string DetailActions => "Trigger: " + Entry.FilterDisplay + "\nLocation: " + Entry.LocationDisplay +
        "\nPossible effects: " + Entry.EffectDisplay + "\nActions: " + Entry.ActionDisplay;
    public string Editability => _displayDraft is { } draft
        ? draft.IsMacroGroup ? draft.CanEdit
            ? "Edit this whole local macro invocation as one collected draft. Save preserves its event count in each context and leaves the shared definition unchanged."
            : draft.RemovalSummary : draft.CanEdit
            ? "Edit this local event as a collected RAW draft, then use the shared Save."
            : Entry.EditabilityReason ?? "This event is read-only in the current source context."
        : Entry.EditabilityReason ?? "No exact editable source fragment is available.";

    internal void Refresh()
    {
        _displayDraft = _workspace.BeginDraft(this);
        var declaredDifficulties = _workspace.Editor.EventDeclaredDifficultyDefines;
        var difficultySignature = string.Join("\u001f", declaredDifficulties);
        var unitPresets = _workspace.Editor.EventUnitPresets;
        if (_displayDraft is { IsMacroGroup: false } literal &&
            (literal.Text != _dialogueSource || difficultySignature != _dialogueDifficultySignature ||
             unitPresets.Count != _dialogueUnitPresets.Count || unitPresets.Where((preset, index) => !ReferenceEquals(preset, _dialogueUnitPresets[index])).Any()))
        {
            _dialogueSource = literal.Text;
            _dialogueDifficultySignature = difficultySignature;
            _dialogueUnitPresets = unitPresets;
            var captured = WccScenarioEventGraphEditor.Capture(literal.Text, declaredDifficulties, unitPresets);
            _supportsDialogue = captured.CanEdit;
            _dialogueProblem = captured.Diagnostic;
        }
        foreach (var name in new[] { nameof(Name), nameof(IsSelected), nameof(IsMacroGroup), nameof(CanEdit), nameof(CanRemove),
                     nameof(CanEditDialogue), nameof(DialogueReason),
                     nameof(RemovalTooltip), nameof(SourceTooltip), nameof(Editability) }) OnPropertyChanged(name);
    }

    internal void RefreshSelection() => OnPropertyChanged(nameof(IsSelected));

    internal void ClearDisplayEligibility()
    {
        _displayDraft = null;
        foreach (var name in new[] { nameof(IsSelected), nameof(IsMacroGroup), nameof(CanEdit), nameof(CanRemove),
                     nameof(CanEditDialogue), nameof(DialogueReason),
                     nameof(RemovalTooltip), nameof(Editability) }) OnPropertyChanged(name);
    }
}
