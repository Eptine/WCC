using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed partial class CampaignPresetCreationForm : ViewModelBase, IDisposable
{
    private readonly Func<string, WccCampaignLocalPresetPayload, bool> _stage;
    private readonly WccPresetFieldImagePreviews? _previews;
    private readonly CampaignPresetFieldSession? _fields;
    public CampaignPresetFieldSession Fields => _fields ?? throw new InvalidOperationException("This preset uses its unit or event editor.");
    public CampaignPresetFieldSession? ScalarFields => _fields;
    public bool HasScalarFields => _fields is not null;
    private CampaignPresetScopeChoice? _selectedScope;
    public IReadOnlyList<CampaignPresetScopeChoice> ScopeChoices { get; internal init; } = [];
    public CampaignPresetScopeChoice? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (value is null || !ScopeChoices.Contains(value) || IsNameReadOnly && _selectedScope is not null) return;
            if (!SetField(ref _selectedScope, value)) return;
            ScalarFields?.SetDifficultyEditingProblem(value.DifficultyProblem);
            OnPropertyChanged(nameof(SourcePath));
        }
    }
    public bool CanChooseScope => !IsNameReadOnly && ScopeChoices.Count > 1;
    public string SourcePath => SelectedScope?.SourcePath ?? _sourcePath;
    private readonly string _sourcePath;
    public string StorageNotice { get; internal init; } = "";
    public string? Problem { get; private set; }
    internal Func<string?>? StageProblemProvider { get; init; }
    public string RoleLabel => Role.ToString();
    public WccCampaignLocalPresetRole Role => _fields?.Role ?? _contentRole;
    public Guid Id { get; }
    private string _name = "";
    private bool _nameExplicit;
    public string Name
    {
        get => _name;
        set { if (IsNameReadOnly && _name.Length != 0) return; _nameExplicit = true; SetField(ref _name, value); }
    }
    public bool IsNameReadOnly { get; internal init; }
    public string WindowTitle => IsNameReadOnly ? "Edit campaign preset" : "Create campaign preset";
    public string StageLabel => IsNameReadOnly ? "Stage changes" : "Stage preset";
    public string Introduction => IsNameReadOnly
        ? "Shared campaign definition. Changes affect every invocation. Apply stages the complete edited definition; global Save checks every difficulty before writing."
        : "Shared campaign definition. Only fields you set are included. This form stages a preset; global Save writes it.";
    internal CampaignPresetCreationForm(CampaignPresetFieldSession fields, WccPresetFieldImagePreviews previews,
        Guid id, string path, Func<string, WccCampaignLocalPresetPayload, bool> stage)
    { _fields = fields; _previews = previews; Id = id; _sourcePath = path; _stage = stage; _name = fields.IsSide ? "SIDE_Character" : "LEADER_Character"; Fields.Changed += SuggestMacroName; }
    public bool Stage()
    {
        Problem = null;
        if (!HasScalarFields) return StageContent();
        if (Fields.IsWccInlineSaveBusy)
        { Problem = "The preset form is stale or busy. Reopen it in the current campaign; existing drafts remain."; return false; }
        WccCampaignLocalPresetPayload payload;
        try { payload = Fields.BuildPayload(); }
        catch (InvalidOperationException refusal) when (Fields.IsWccInlineSaveBusy)
        { Problem = refusal.Message; return false; }
        if (_stage(Name, payload)) return true;
        Problem = StageProblemProvider?.Invoke() ?? "The campaign or source changed. Reopen the form; existing drafts remain.";
        return false;
    }
    private void SuggestMacroName(object? sender, EventArgs args)
    {
        if (_nameExplicit || IsNameReadOnly) return;
        var identity = Fields.SuggestedPresetIdentity;
        var suffix = string.IsNullOrWhiteSpace(identity) ? "Character" : new string(identity.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        if (suffix.Length > 118) suffix = suffix[..118];
        SetField(ref _name, (Fields.IsSide ? "SIDE_" : "LEADER_") + suffix, nameof(Name));
    }
    public void Dispose() { _contentClosed = true; if (_fields is not null) { _fields.Changed -= SuggestMacroName; _fields.Dispose(); } _previews?.Dispose(); }
}

public sealed record CampaignPresetScopeChoice(WccCampaignPresetScope Scope, string Label,
    string SourcePath, string? DifficultyProblem = null);

public partial class CampaignWindowViewModel
{
    private WccCampaignLocalPresetCatalogSnapshot? _campaignPresetCatalog;
    private readonly List<WccCampaignLocalPresetCreationSpec> _campaignPresetDrafts = [];
    private long _campaignPresetDraftVersion;
    private string? _campaignPresetInstallPath;
    private bool _changingCampaignPresets;
    public bool HasCampaignPresetDrafts => _campaignPresetDrafts.Count != 0 || _campaignPresetMigrationPending || _campaignPresetMutations.Count != 0;
    public bool HasCampaignSourceDrafts => HasCampaignBufferedText || HasCampaignFieldDrafts || HasCampaignPresetDrafts || HasCampaignScenarioCreationDraft || HasCampaignUnitTypeCreationDraft || HasCampaignPackageEditDraft && !IsCampaignDependencySourceRefreshing;
    public bool CanOpenCampaignPresets => (_project?.Campaign is not null || HasSelectedCampaignEntryBinding) && !IsCampaignFieldSaveBusy && !IsCampaignUiLoading;
    public IReadOnlyList<WccCampaignLocalPresetEntry> CampaignLocalPresets => _campaignPresetCatalog?.Entries ?? [];
    internal IReadOnlyList<WccCampaignLocalPresetCreationSpec> CampaignPresetDrafts => _campaignPresetDrafts.ToArray();
    public string CampaignPresetGuidance => "Create reusable Side, Leader, Unit and Event presets for the whole package or this campaign. Save them before selecting them in a scenario.";
    public string? CampaignPresetCreationProblem
    {
        get
        {
            lock (_campaignFieldGate)
            {
                if (HasCampaignPackageEditDraft) return "Save or discard the pending dependency or chapter changes before creating presets.";
                if (HasCampaignScenarioCreationDraft) return "Save or discard the new scenario before creating presets.";

                if (IsSynchronizingUi || (_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation) || _changingCampaignPresets || _publishingCampaignProjection || _publishingCampaignStructure || _changingCampaignStructure || _publishingCampaignCredits || _changingCampaignCredits)
                    return "Finish the current campaign update before creating a preset.";
                if (_campaignMenuOwner is not { } owner || _campaignProjection?.Source is not { } source || EffectiveCampaignPlan is not { } plan)
                    return "Wait for the current campaign source and difficulties to resolve.";
                if (GetCampaignFieldBlockReason(GetCampaignFieldRoute(owner, "rank")) is { } problem) return problem;
                try
                {
                    var snapshot = ReadCampaignPresetStorage();
                    return snapshot.CanStore ? null : snapshot.Diagnostic;
                }
                catch (InvalidOperationException refusal) { return refusal.Message; }
            }
        }
    }
    public bool CanCreateCampaignPreset => CampaignPresetCreationProblem is null;
    public IReadOnlyList<PendingEditorChange> CampaignPresetPendingChanges => _campaignPresetDrafts.Select(preset =>
        new PendingEditorChange("Campaign", "New " + preset.Payload.Role + " preset", preset.Name + " -> " + CampaignPresetDraftStoragePath(preset)))
        .Concat(CampaignPresetMigrationPendingChanges).Concat(_campaignPresetMutations.Select(row =>
            new PendingEditorChange("Campaign", (row.IsDeletion ? "Delete " : "Edit ") + row.Target.Role + " preset",
                row.Target.Name + " -> " + row.Target.SourcePath))).ToArray();
    internal void ConfigureCampaignPresetPreviews(string? installPath) => _campaignPresetInstallPath = installPath;
    public CampaignPresetCreationForm? BeginCampaignPresetCreation(WccCampaignLocalPresetRole role)
    {
        lock (_campaignFieldGate)
        {
            if (CampaignPresetCreationProblem is { } problem) { Status = problem; return null; }
            var binding = _campaignProjectionBinding!;
            var source = CampaignPresetPackageSource!;
            var plan = EffectiveCampaignPlan!;
            var authoring = _campaignAuthoring;
            var sources = _campaignPresetCapturedSources;
            var capture = _campaignPresetCapture;
            var storage = ReadCampaignPresetStorage();
            var install = _campaignPresetInstallPath;
            var id = Guid.NewGuid();
            bool Current() => IsCurrentCampaignBinding(binding) && IsCurrentCampaignPresetPackageSource(source) &&
                ReferenceEquals(plan, EffectiveCampaignPlan) && ReferenceEquals(authoring, _campaignAuthoring) &&
                ReferenceEquals(sources, _campaignPresetCapturedSources) && ReferenceEquals(capture, _campaignPresetCapture) && install == _campaignPresetInstallPath &&
                !_campaignHasRawSourceChanges && !IsReadOnly && !IsCampaignFieldSaveBusy && !IsSynchronizingUi && !_collectedSaveRecoveryPending;
            var snapshot = binding.Configuration.Snapshot!;
            var root = binding.Configuration.RootRevision!.Value;
            var visible = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root);
            var units = PresetUnitChoices(snapshot, visible, install);
            if (role is WccCampaignLocalPresetRole.Unit or WccCampaignLocalPresetRole.Event)
                return CreateContentPresetForm(role, id, storage, units, binding, Current);
            var previews = new WccPresetFieldImagePreviews(snapshot, root, source.Path, install, Current);
            var fields = new CampaignPresetFieldSession(role, plan, units, Current, previews.Get);
            CampaignPresetCreationForm? form = null;
            form = new CampaignPresetCreationForm(fields, previews, id, storage.StoragePath, (name, payload) =>
            {
                return StageCampaignPresetCreationPayload(id, name, payload, form, binding, Current);
            }) { StageProblemProvider = () => Status, ScopeChoices = CampaignPresetScopeChoices(storage), StorageNotice = storage.CanMigrate && _campaignPresetMutations.Count == 0
                ? "Global Save writes this definition to utils/presets.cfg and moves existing managed presets from the main file into that same utility. Their names, IDs and contents stay the same."
                : "Global Save writes the selected scope utility and authoring companion together. Shared package definitions are available to every chapter." };
            form.SelectedScope = form.ScopeChoices.First(choice => choice.Scope.IsPackage);
            return form;
        }
    }

    private bool StageCampaignPresetCreationPayload(Guid id, string name, WccCampaignLocalPresetPayload payload,
        CampaignPresetCreationForm? form, CampaignDraftBinding binding, Func<bool> current)
    {
        lock (_campaignFieldGate)
        {
            if (!current() || CampaignPresetCreationProblem is { })
            {
                Status = "The campaign preset form changed or is busy. Reopen it; current drafts remain.";
                return false;
            }
            try
            {
                var spec = new WccCampaignLocalPresetCreationSpec(id, name, payload) { Scope = form!.SelectedScope!.Scope };
                var next = _campaignPresetDrafts.Append(spec).ToArray();
                WccCampaignLocalPresetStoragePlanner.ValidatePendingEdits(ReadCampaignPresetStorage(), next, _campaignPresetMutations, _campaignPresetCapturedSources!);
                var version = _campaignPresetDraftVersion;
                _changingCampaignPresets = true;
                try
                {
                    _campaignFieldSession.Invalidate(); CollectedSaveAuthorityChanging?.Invoke();
                    if (!current() || version != _campaignPresetDraftVersion) return false;
                    _campaignDraftBinding ??= binding;
                    _campaignPresetDrafts.Add(spec); _campaignPresetDraftVersion++;
                }
                finally { _changingCampaignPresets = false; }
                RaiseCampaignFieldsChanged(); Status = "Preset staged. Global Save writes the shared definition."; return true;
            }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
            {
                Status = refusal.Message;
                return false;
            }
        }
    }

    public void RevertCampaignPreset(Guid id)
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignFieldSaveBusy || (_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation) || _changingCampaignPresets || _publishingCampaignProjection || IsSynchronizingUi) return;
            var removed = _campaignPresetDrafts.RemoveAll(preset => preset.Id == id) + _campaignPresetMutations.RemoveAll(row => row.Target.Id == id);
            if (removed == 0) return;
            _campaignPresetDraftVersion++; CollectedSaveAuthorityChanging?.Invoke(); FinishCampaignDraftMutation();
        }
    }
    private void ClearCampaignPresetDrafts()
    { _campaignPresetDrafts.Clear(); _campaignPresetMutations.Clear(); _campaignPresetMigrationPending = false; _campaignPresetDraftVersion++; }
    private WccCampaignLocalPresetStorageEdit? CaptureCampaignPresetStorage() => HasCampaignPresetDrafts
        ? WccCampaignLocalPresetStoragePlanner.CreateCombinedEdit(ReadCampaignPresetStorage(), _campaignPresetDrafts,
            _campaignPresetMutations, _campaignPresetCapturedSources!) : null;
    public CampaignMainRawEditDraft? BeginCampaignPresetRawEdit(WccCampaignLocalPresetEntry entry)
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignPresetRaw(entry) ||
                BeginCampaignMainRawEdit() is not { } main || main.SourcePath != entry.SourcePath || main.SourceText != entry.DefinitionRange.Source.Text) return null;
            return new(main.Project, main.Document, main.SourcePath, main.SourceText, main.Generation, entry.DefinitionRange.StartOffset, entry.DefinitionRange.Length);
        }
    }
    public bool CanEditCampaignPresetRaw(WccCampaignLocalPresetEntry entry) => CanEditCampaignMainRaw &&
        CampaignSourcePublicationGuard?.Invoke() != false && CampaignLocalPresets.Any(current => ReferenceEquals(current, entry)) &&
        entry.SourcePath == _project?.SourcePath && entry.DefinitionRange.Source.Text == GetCurrentMainSourceRawWml();

    public string? CampaignPresetApplicationProblem
    {
        get
        {

            if (_campaignHasRawSourceChanges) return "Save the campaign RAW changes before selecting a preset instance.";
            if (IsReadOnly || _campaignFieldConfiguration?.Session.CanWrite != true) return "This campaign is open read-only.";
            if (IsCampaignUiLoading || IsCampaignFieldSaveBusy || IsSynchronizingUi || _publishingCampaignProjection ||
                (_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation) || _changingCampaignPresets || _publishingCampaignStructure || _changingCampaignStructure || _publishingCampaignCredits || _changingCampaignCredits)
                return "Wait for the current campaign update before selecting a preset.";
            if (_collectedSaveRecoveryPending || _campaignFieldSession.Recovery is not null) return "Finish source recovery before selecting a preset.";
            if (_campaignPresetCatalog is null || _campaignProjectionBinding is not { } binding || !IsCurrentCampaignBinding(binding)
                || !IsCurrentCampaignPresetPackageSource(_campaignPresetCatalog.MainSource) || EffectiveCampaignPlan is null)
                return "Wait for the current campaign catalog and difficulties to resolve.";
            return GetCampaignDraftBindingProblem();
        }
    }

    private bool CampaignPresetAvailableForApplication(WccCampaignLocalPresetEntry entry) =>
        !_campaignPresetMutations.Any(mutation => mutation.Target.Id == entry.Id && mutation.IsDeletion);

    internal IReadOnlyList<WccCampaignLocalPresetEntry> AvailableCampaignPresets(WccCampaignLocalPresetRole role) =>
        WccCampaignPresetSelectionBinding.AvailableEntries(CampaignLocalPresets.Where(CampaignPresetAvailableForApplication), EffectiveCampaignPlan, role);

    public WccCampaignPresetSelectionBinding? CaptureCampaignPresetSelection(WccCampaignLocalPresetRole role)
    {
        lock (_campaignFieldGate)
        {
            if (CampaignPresetApplicationProblem is { } problem) { Status = problem; return null; }
            var catalog = _campaignPresetCatalog!; var binding = _campaignProjectionBinding!;
            var plan = EffectiveCampaignPlan; var authoring = _campaignAuthoring; var install = _campaignPresetInstallPath;
            bool Current() => ReferenceEquals(catalog, _campaignPresetCatalog) && IsCurrentCampaignBinding(binding)
                && ReferenceEquals(plan, EffectiveCampaignPlan) && ReferenceEquals(authoring, _campaignAuthoring)
                && install == _campaignPresetInstallPath
                && IsCurrentCampaignPresetPackageSource(catalog.MainSource) && !_campaignHasRawSourceChanges
                && !IsReadOnly && !_collectedSaveRecoveryPending;
            var entries = AvailableCampaignPresets(role);
            var mutations = entries.ToDictionary(entry => entry.Id, entry => _campaignPresetMutations.SingleOrDefault(row => row.Target.Id == entry.Id));
            bool EntryCurrent(WccCampaignLocalPresetEntry entry) => CampaignPresetAvailableForApplication(entry)
                && mutations.TryGetValue(entry.Id, out var mutation)
                && ReferenceEquals(mutation, _campaignPresetMutations.SingleOrDefault(row => row.Target.Id == entry.Id));
            return WccCampaignPresetSelectionBinding.CaptureAvailable(role, entries, plan, Current, EntryCurrent);
        }
    }
}
