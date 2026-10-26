using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed record CampaignLocalPresetRow(Guid Id, string Name, string Role, string Source, string Description,
    string Text, WccCampaignLocalPresetEntry? Entry, bool IsPending)
{
    public string ScopeLabel { get; init; } = "Whole package";
    public bool IsPackageScope { get; init; } = true;
    public bool IsCurrentCampaignScope { get; init; } = true;
}

public sealed class CampaignLocalPresetManagerViewModel : ViewModelBase, IDisposable
{
    private readonly CampaignWindowViewModel _campaign;
    private string? _search;
    private string _roleFilter = "All types";
    private string _scopeFilter = "All scopes";
    private CampaignLocalPresetRow? _selected;
    public CampaignLocalPresetManagerViewModel(CampaignWindowViewModel campaign)
    { _campaign = campaign; _campaign.CampaignFieldDraftsChanged += CampaignChanged; _campaign.PropertyChanged += CampaignPropertyChanged; Refresh(); }
    public ObservableCollection<CampaignLocalPresetRow> Rows { get; } = [];
    public string[] RoleFilters { get; } = ["All types", "Leader", "Side", "Unit", "Event"];
    public string[] ScopeFilters { get; } = ["All scopes", "Whole package", "This campaign"];
    public string RoleFilter { get => _roleFilter; set { if (RoleFilters.Contains(value) && SetField(ref _roleFilter, value)) Refresh(); } }
    public string ScopeFilter { get => _scopeFilter; set { if (ScopeFilters.Contains(value) && SetField(ref _scopeFilter, value)) Refresh(); } }
    public string? SearchText { get => _search; set { if (SetField(ref _search, value)) Refresh(); } }
    public CampaignLocalPresetRow? SelectedRow
    {
        get => _selected;
        set { if (value is not null && !Rows.Contains(value)) return;
            if (SetField(ref _selected, value)) { OnPropertyChanged(nameof(CanRevert)); OnPropertyChanged(nameof(CanViewSource)); OnPropertyChanged(nameof(SourceButtonLabel)); OnPropertyChanged(nameof(CanEditSelectedSource));
                OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanDelete)); OnPropertyChanged(nameof(DeleteProblem)); } }
    }
    public bool CanCreate => _campaign.CanCreateCampaignPreset;
    public bool CanMoveMainPresets => _campaign.CanMigrateCampaignPresets;
    public bool CanRevertMove => _campaign.CanRevertCampaignPresetMigration;
    public string? MoveProblem => _campaign.CampaignPresetMigrationProblem;
    public string Guidance => _campaign.CampaignPresetGuidance;
    public string? CreationProblem => _campaign.CampaignPresetCreationProblem;
    public bool CanRevert => SelectedRow?.IsPending == true && !_campaign.IsCampaignFieldSaveBusy;
    public bool CanViewSource => SelectedRow is not null;
    public bool CanEdit => SelectedRow?.Entry is { } entry && _campaign.CampaignPresetEditProblem(entry) is null;
    public bool CanDelete => SelectedRow?.Entry is { } entry && _campaign.CampaignPresetDeleteProblem(entry) is null;
    public string? DeleteProblem => SelectedRow?.Entry is { } entry ? _campaign.CampaignPresetDeleteProblem(entry) : "Save new presets before editing or deleting their shared definitions.";
    public bool CanEditSelectedSource => SelectedRow?.Entry is { } entry &&
        (_campaign.CanEditCampaignPresetRaw(entry) || _campaign.CanEditCampaignPresetSharedRaw(entry));
    public string SourceButtonLabel => CanEditSelectedSource ? "Edit shared RAW" : "View source";
    public CampaignPresetCreationForm? BeginCreate(WccCampaignLocalPresetRole role) => _campaign.BeginCampaignPresetCreation(role);
    public CampaignPresetCreationWizardViewModel? BeginCreateWizard(WccCampaignLocalPresetRole? preselectedRole = null)
    {
        if (!CanCreate) return null;
        var project = _campaign.CurrentProject;
        var entry = _campaign.SelectedCampaignEntry;
        var stamp = _campaign.GetPendingSourceChangeStamp();
        string? CurrentProblem() => ReferenceEquals(project, _campaign.CurrentProject) && ReferenceEquals(entry, _campaign.SelectedCampaignEntry)
            && stamp == _campaign.GetPendingSourceChangeStamp()
                ? CreationProblem : "The campaign changed while choosing a preset type. Reopen Add preset; existing drafts remain.";
        return new(BeginCreate, CurrentProblem, preselectedRole);
    }
    public CampaignPresetCreationForm? BeginEdit() => CanEdit && SelectedRow?.Entry is { } entry ? _campaign.BeginCampaignPresetEdit(entry) : null;
    public void Delete() { if (SelectedRow?.Entry is { } entry) _campaign.StageCampaignPresetDeletion(entry); }
    public CampaignMainRawEditDraft? BeginSelectedRaw() => SelectedRow?.Entry is { } entry ? _campaign.BeginCampaignPresetRawEdit(entry) : null;
    public WmlEditorDocumentViewModel? BeginSelectedSharedRaw() => SelectedRow?.Entry is { } entry ? _campaign.BeginCampaignPresetSharedRaw(entry) : null;
    public void MoveMainPresets() => _campaign.StageCampaignPresetMigration();
    public void RevertMove() => _campaign.RevertCampaignPresetMigration();
    public string? ValidateRaw(CampaignMainRawEditDraft draft, string text) => _campaign.ValidateCampaignMainRawEdit(draft, text);
    public bool ApplyRaw(CampaignMainRawEditDraft draft, string text) => _campaign.ApplyCampaignMainRawEdit(draft, text);
    public void Revert() { if (CanRevert && SelectedRow is { } row) _campaign.RevertCampaignPreset(row.Id); }
    private void CampaignChanged(object? sender, EventArgs args) => Refresh();
    private void CampaignPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(CampaignWindowViewModel.CanCreateCampaignPreset) or
            nameof(CampaignWindowViewModel.CanMigrateCampaignPresets) or nameof(CampaignWindowViewModel.CanRevertCampaignPresetMigration) or
            nameof(CampaignWindowViewModel.IsCampaignUiLoading) or nameof(CampaignWindowViewModel.IsCampaignFieldSaveBusy)) Refresh();
    }
    public void Refresh()
    {
        var selected = SelectedRow?.Id;
        var entries = _campaign.CampaignLocalPresets.Select(entry =>
        {
            var mutation = _campaign.CampaignPresetMutations.SingleOrDefault(row => row.Target.Id == entry.Id);
            return new CampaignLocalPresetRow(entry.Id, entry.Name, entry.Role.ToString(), entry.SourcePath,
                mutation is null ? entry.Diagnostic ?? "Shared campaign definition. Editing affects every invocation."
                    : mutation.IsDeletion ? "Pending deletion. Global Save checks that no captured caller remains."
                    : "Pending shared edit. Global Save checks all difficulties and callers.",
                mutation is null ? entry.DefinitionRange.Text : mutation.IsDeletion ? "Preset deletion is pending."
                    : WccCampaignLocalPresetStoragePlanner.RenderMutationPreview(mutation, _campaign.CampaignPresetEditingPlan), entry, mutation is not null)
                { ScopeLabel = entry.Scope.DisplayName, IsPackageScope = entry.Scope.IsPackage,
                    IsCurrentCampaignScope = entry.Scope.Targets(_campaign.CampaignPresetEditingPlan) };
        });
        var pending = _campaign.CampaignPresetDrafts.Select(preset => new CampaignLocalPresetRow(preset.Id, preset.Name, preset.Payload.Role.ToString(),
            _campaign.CampaignPresetDraftStoragePath(preset), "Pending creation. Global Save writes this definition.", "Preset creation is pending.", null, true)
                { ScopeLabel = preset.Scope.DisplayName, IsPackageScope = preset.Scope.IsPackage,
                    IsCurrentCampaignScope = preset.Scope.Targets(_campaign.CampaignPresetEditingPlan) });
        var query = SearchText?.Trim();
        Rows.Clear();
        foreach (var row in entries.Concat(pending).Where(row => (RoleFilter == "All types" || row.Role == RoleFilter) &&
                (ScopeFilter == "All scopes" || ScopeFilter == "Whole package" && row.IsPackageScope || ScopeFilter == "This campaign" && row.IsCurrentCampaignScope))
            .Where(row => string.IsNullOrEmpty(query) || (row.Name + " " + row.Role + " " + row.ScopeLabel + " " + row.Source).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)) Rows.Add(row);
        SelectedRow = Rows.FirstOrDefault(row => row.Id == selected) ?? Rows.FirstOrDefault();
        foreach (var property in new[] { nameof(CanCreate), nameof(CreationProblem), nameof(CanRevert), nameof(SourceButtonLabel), nameof(CanEditSelectedSource),
            nameof(CanMoveMainPresets), nameof(CanRevertMove), nameof(MoveProblem), nameof(CanEdit), nameof(CanDelete), nameof(DeleteProblem) }) OnPropertyChanged(property);
    }
    public void Dispose()
    { _campaign.CampaignFieldDraftsChanged -= CampaignChanged; _campaign.PropertyChanged -= CampaignPropertyChanged; }
}
