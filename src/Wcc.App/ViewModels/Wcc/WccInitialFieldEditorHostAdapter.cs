using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

internal interface IWccInitialFieldEditorHost
{
    ScenarioWindowViewModel? ScenarioEditor { get; }
    bool HasMapPreview { get; }
    bool IsWccInlineSaveBusy { get; }
    bool AreWccInitialDraftsPending => true;
    event EventHandler? WccFieldVisibilityChanged;
    bool CanReviewWccInitialField(WmlStaticScalarOwner owner, string key);
    bool CanToggleWccDifficultyField(WmlStaticScalarOwner owner, string key, string? value);
    WccScenarioInlineDraft? FindWccInlineDraft(WccScenarioFieldRoute? route);
    IReadOnlyList<CatalogPickerOptionVm> GetUnitTypeCatalogOptions();
    WccFieldImagePreviewVm GetWccFieldImagePreview(WmlStaticScalarOwner owner, string key);
    string? GetWccInitialFieldDefaultValue(WmlStaticScalarOwner owner, ScenarioInitialFieldDefinition definition);
    string GetWccInitialFieldEditStatus(WmlStaticScalarOwner owner, string key);
    WccScenarioFieldRoute? GetWccInitialFieldRoute(WmlStaticScalarOwner owner, string key);
    ScenarioSideSourceLocation? GetWccInitialFieldSourceLocation(WmlStaticScalarOwner owner, string key);
    bool HasAuthoredLocalWccInitialFieldValue(WmlStaticScalarOwner owner, string key);
    bool HasWccDifficultyDraft(WmlStaticScalarOwner owner, string key);
    bool IsRequiredWccInitialField(ScenarioInitialFieldDefinition definition);
    bool IsSelectedWccInitialFieldOwner(WmlStaticScalarOwner owner, ScenarioInitialFieldOwnerScope scope);
    bool IsWccDifficultyIndependent(WmlStaticScalarOwner owner, string key);
    bool IsWccInitialFieldPreferredVisible(ScenarioInitialFieldDefinition definition);
    Task PickWccInitialLeaderCoordinateAsync(WmlStaticScalarOwner owner, ScenarioMapCoordinatePicker? picker = null);
    void RemoveWccInitialFieldOverride(WmlStaticScalarOwner owner, string key);
    void RevertWccInitialField(WmlStaticScalarOwner owner, string key);
    bool SetWccInitialFieldValue(WmlStaticScalarOwner owner, string key, string? value, WccScenarioFieldValueInputKind inputKind);
    void SetWccInitialFieldVisible(ScenarioInitialFieldOwnerScope scope, string key, bool visible);
    bool ToggleWccDifficultyField(WmlStaticScalarOwner owner, string key, string? value, string? baselineDisplayValue);
    bool TryGetWccSideFlagDraftValue(WmlStaticScalarOwner owner, string key, out string value);
    string? WccDifficultyFieldLockReason(WmlStaticScalarOwner owner, string key, string? value);
    IReadOnlyList<WccInitialFieldEditorVm> GetWccPresentationRows(ScenarioInitialFieldOwnerScope scope, string key);
}

/// <summary>Keeps the existing scenario field behavior behind the shared display/draft surface.</summary>
internal sealed class WccInitialFieldEditorHostAdapter : IWccInitialFieldEditorHost
{
    private readonly ScenarioWindowViewModel _editor;
    public WccInitialFieldEditorHostAdapter(ScenarioWindowViewModel editor) => _editor = editor;
    public ScenarioWindowViewModel? ScenarioEditor => _editor;
    public bool HasMapPreview => _editor.HasMapPreview;
    public bool IsWccInlineSaveBusy => _editor.IsWccInlineSaveBusy;
    public event EventHandler? WccFieldVisibilityChanged
    { add => _editor.WccFieldVisibilityChanged += value; remove => _editor.WccFieldVisibilityChanged -= value; }
    public bool CanReviewWccInitialField(WmlStaticScalarOwner owner, string key) => _editor.CanReviewWccInitialField(owner, key);
    public bool CanToggleWccDifficultyField(WmlStaticScalarOwner owner, string key, string? value) => _editor.CanToggleWccDifficultyField(owner, key, value);
    public WccScenarioInlineDraft? FindWccInlineDraft(WccScenarioFieldRoute? route) => _editor.FindWccInlineDraft(route);
    public IReadOnlyList<CatalogPickerOptionVm> GetUnitTypeCatalogOptions() => _editor.GetUnitTypeCatalogOptions();
    public WccFieldImagePreviewVm GetWccFieldImagePreview(WmlStaticScalarOwner owner, string key) => _editor.GetWccFieldImagePreview(owner, key);
    public string? GetWccInitialFieldDefaultValue(WmlStaticScalarOwner owner, ScenarioInitialFieldDefinition definition) => _editor.GetWccInitialFieldDefaultValue(owner, definition);
    public string GetWccInitialFieldEditStatus(WmlStaticScalarOwner owner, string key) => _editor.GetWccInitialFieldEditStatus(owner, key);
    public WccScenarioFieldRoute? GetWccInitialFieldRoute(WmlStaticScalarOwner owner, string key) => _editor.GetWccInitialFieldRoute(owner, key);
    public ScenarioSideSourceLocation? GetWccInitialFieldSourceLocation(WmlStaticScalarOwner owner, string key) => _editor.GetWccInitialFieldSourceLocation(owner, key);
    public bool HasAuthoredLocalWccInitialFieldValue(WmlStaticScalarOwner owner, string key) => _editor.HasAuthoredLocalWccInitialFieldValue(owner, key);
    public bool HasWccDifficultyDraft(WmlStaticScalarOwner owner, string key) => _editor.HasWccDifficultyDraft(owner, key);
    public bool IsRequiredWccInitialField(ScenarioInitialFieldDefinition definition) => _editor.IsRequiredWccInitialField(definition);
    public bool IsSelectedWccInitialFieldOwner(WmlStaticScalarOwner owner, ScenarioInitialFieldOwnerScope scope) => _editor.IsSelectedWccInitialFieldOwner(owner, scope);
    public bool IsWccDifficultyIndependent(WmlStaticScalarOwner owner, string key) => _editor.IsWccDifficultyIndependent(owner, key);
    public bool IsWccInitialFieldPreferredVisible(ScenarioInitialFieldDefinition definition) => _editor.IsWccInitialFieldPreferredVisible(definition);
    public Task PickWccInitialLeaderCoordinateAsync(WmlStaticScalarOwner owner, ScenarioMapCoordinatePicker? picker = null) =>
        _editor.PickWccInitialLeaderCoordinateAsync(owner, picker);
    public void RemoveWccInitialFieldOverride(WmlStaticScalarOwner owner, string key) => _editor.RemoveWccInitialFieldOverride(owner, key);
    public void RevertWccInitialField(WmlStaticScalarOwner owner, string key) => _editor.RevertWccInitialField(owner, key);
    public bool SetWccInitialFieldValue(WmlStaticScalarOwner owner, string key, string? value, WccScenarioFieldValueInputKind inputKind) => _editor.SetWccInitialFieldValue(owner, key, value, inputKind);
    public void SetWccInitialFieldVisible(ScenarioInitialFieldOwnerScope scope, string key, bool visible) => _editor.SetWccInitialFieldVisible(scope, key, visible);
    public bool ToggleWccDifficultyField(WmlStaticScalarOwner owner, string key, string? value,
        string? baselineDisplayValue) => _editor.ToggleWccDifficultyField(owner, key, value, baselineDisplayValue);
    public bool TryGetWccSideFlagDraftValue(WmlStaticScalarOwner owner, string key, out string value) => _editor.TryGetWccSideFlagDraftValue(owner, key, out value);
    public string? WccDifficultyFieldLockReason(WmlStaticScalarOwner owner, string key, string? value) => _editor.WccDifficultyFieldLockReason(owner, key, value);
    public IReadOnlyList<WccInitialFieldEditorVm> GetWccPresentationRows(ScenarioInitialFieldOwnerScope scope, string key) => _editor.GetWccPresentationRows(scope, key);
}
