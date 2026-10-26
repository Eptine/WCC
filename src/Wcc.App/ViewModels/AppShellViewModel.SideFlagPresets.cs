using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    public string? GetFlagPresetStageBlockReason(ScenarioWindowViewModel editor)
    {
        if (!ReferenceEquals(ScenarioEditor, editor)) return "The scenario editor changed. Reopen the flag picker.";
        if (IsGlobalSaveBusy) return "Wait for the current Save to finish before choosing a flag preset.";
        if (_campaignEditorDirty || _scenarioEditorDirty || _unitEditorDirty || _libraryUnitEditorDirty || CampaignEditor.HasCampaignSourceChanges)
            return "Applied RAW, structural or Unit edits use a separate source save. Save or discard them before staging a flag preset.";
        return null;
    }

    public bool StageFlagPreset(ScenarioWindowViewModel editor, WccSideFlagPresetSelectionContext context,
        CatalogPickerSelection selection)
    {
        if (GetFlagPresetStageBlockReason(editor) is { } problem) { Status = problem; return false; }
        return editor.StageWccSideFlagPreset(context, selection);
    }

    public string? GetFlagPresetSourceEditBlockReason(ScenarioWindowViewModel editor)
    {
        if (!ReferenceEquals(ScenarioEditor, editor))
            return "The scenario editor changed. Reopen the flag picker.";
        if (IsGlobalSaveBusy)
            return "Wait for the current Save to finish before applying a flag preset.";
        if (HasPendingEditorChanges)
            return "Save or discard pending Campaign, Scenario and Unit changes before applying a flag preset in RAW source.";
        return null;
    }

    public bool CanBeginFlagPresetSourceEdit(ScenarioWindowViewModel editor) =>
        GetFlagPresetSourceEditBlockReason(editor) is null;

    public WccSideFlagPresetEditDraft? BeginFlagPresetSourceEdit(ScenarioWindowViewModel editor,
        CatalogPickerSelection selection)
    {
        if (GetFlagPresetSourceEditBlockReason(editor) is { } problem)
        { Status = problem; return null; }
        return editor.BeginWccSideFlagPresetEdit(selection);
    }

    public string? ValidateFlagPresetSourceEdit(ScenarioWindowViewModel editor,
        WccSideFlagPresetEditDraft draft, string invocation) =>
        GetFlagPresetSourceEditBlockReason(editor) ?? editor.ValidateWccSideFlagPresetEdit(draft, invocation);

    public bool ApplyFlagPresetSourceEdit(ScenarioWindowViewModel editor,
        WccSideFlagPresetEditDraft draft, string invocation)
    {
        if (GetFlagPresetSourceEditBlockReason(editor) is { } problem)
        { Status = problem; return false; }
        return editor.ApplyWccSideFlagPresetEdit(draft, invocation);
    }
}
