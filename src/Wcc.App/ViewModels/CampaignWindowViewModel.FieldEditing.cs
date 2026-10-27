using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed record CampaignFieldSourceLocation(string SourcePath, string SourceText,
    int SelectionStart, int SelectionLength, string Description);

public sealed record CampaignFieldGroupVm(string Label, IReadOnlyList<WccCampaignFieldEditorVm> Fields)
{
    public bool IsExpanded => Label is "Identity" or "Presentation" or "Dates" or "Rules" or "Ending";
}

/// <summary>Campaign menu evidence and proposals are separate from authored campaign models.</summary>
public partial class CampaignWindowViewModel
{
    private readonly object _campaignFieldGate = new();
    private readonly WccScenarioFieldEditSession _campaignFieldSession = new();
    internal IReadOnlyList<WccSourceSaveDiagnostics> LastCampaignBatchSaveDiagnostics => _campaignFieldSession.LastBatchSaveDiagnostics;
    private readonly ObservableCollection<WccCampaignFieldEditorVm> _campaignFields = new();
    private readonly ObservableCollection<WccScenarioInlineDraft> _campaignDrafts = new();
    private readonly Dictionary<(WmlStaticScalarOwner Owner, string Key), WccScenarioFieldRoute> _campaignRoutes = new();
    private ReadOnlyObservableCollection<WccCampaignFieldEditorVm>? _campaignFieldView;
    private ReadOnlyObservableCollection<WccScenarioInlineDraft>? _campaignDraftView;
    private IReadOnlyList<CampaignFieldGroupVm> _campaignFieldGroups = [];
    private WccCampaignFieldEditorVm[] _campaignGroupedRows = [];
    private CampaignFieldConfiguration? _campaignFieldConfiguration;
    private CampaignInitialFieldProjection? _campaignProjection;
    private CampaignInitialFieldProjection? _contextCheckProjection;
    private WmlStaticScalarOwner? _contextCheckOwner;
    private WmlCampaignEvaluationPlan? _contextCheckPlan;
    private string? _contextCheckProblem;
    private WccCampaignStructureInventoryProjection? _campaignStructureInventory;
    private string? _campaignStructureInventoryAuthoringKey;
    internal WccCampaignStructureInventoryProjection? CampaignStructureInventory => _campaignStructureInventory;
    // The collected writer freezes the complete shared plan, including provenance.
    // Ordinary single-entry editors keep that same host plan after menu confirmation.
    private WmlCampaignEvaluationPlan? EffectiveCampaignPlan => RequiresSelectedCampaignAdapter
        ? _selectedCampaignEntry?.Plan ?? _campaignProjection?.EffectivePlan ?? _campaignFieldConfiguration?.Plan
        : _campaignProjection?.EffectivePlan ?? _campaignFieldConfiguration?.Plan ?? _selectedCampaignEntry?.Plan;
    private WmlStaticScalarOwner? _campaignMenuOwner;
    private CampaignDocument? _campaignProjectionDocument;
    private CampaignDraftBinding? _campaignProjectionBinding;
    private bool _publishingCampaignProjection;
    private long _campaignFieldGeneration;
    private long _campaignLoadRequestVersion;
    private CancellationTokenSource? _campaignProjectionCancellation;
    private Task _campaignProjectionTask = Task.CompletedTask;
    private Task<WccScenarioFieldBatchEditResult>? _campaignSaveTask;
    private bool _campaignSaveBusy;
    private bool _campaignHasRawSourceChanges;
    private bool _raisingCampaignFields;
    private CampaignDraftBinding? _campaignDraftBinding;
    private string? _campaignFieldStatus;
    private string? _campaignReviewSummary;

    public event EventHandler? CampaignFieldDraftsChanged;
    public event EventHandler? CampaignFieldsSaved;
    internal event EventHandler? CampaignFieldProjectionPublished;
    public Func<Task>? CampaignFieldLibraryRefreshAsync { get; set; }
    public Func<Action, bool>? BeforeCampaignSourceNavigation { get; set; }
    public ReadOnlyObservableCollection<WccCampaignFieldEditorVm> CampaignFields => _campaignFieldView ??= new(_campaignFields);
    // UI bindings retain their live observable view. Host readers instead take
    // one short snapshot while menu publication owns the same existing gate.
    internal IReadOnlyList<WccCampaignFieldEditorVm> CaptureCampaignFieldsSnapshot()
    {
        lock (_campaignFieldGate) return _campaignFields.ToArray();
    }
    public ReadOnlyObservableCollection<WccScenarioInlineDraft> CampaignFieldDrafts => _campaignDraftView ??= new(_campaignDrafts);
    public IReadOnlyList<CampaignFieldGroupVm> CampaignFieldGroups
    {
        get
        {
            lock (_campaignFieldGate)
            {
                if (!_campaignGroupedRows.SequenceEqual(_campaignFields))
                {
                    _campaignGroupedRows = _campaignFields.ToArray();
                    _campaignFieldGroups = _campaignGroupedRows.GroupBy(@field => @field.Definition.Group)
                        .Select(group => new CampaignFieldGroupVm(group.Key, group.ToArray())).ToArray();
                }
                return _campaignFieldGroups;
            }
        }
    }
    public bool HasCampaignFieldDrafts => _campaignDrafts.Count != 0 || HasCampaignStructureDrafts || HasCampaignCreditsDrafts;
    public bool IsCampaignFieldSaveBusy => _campaignSaveBusy;
    private bool _campaignUiLoading;
    private long _campaignUiLoadingGeneration;
    public bool IsCampaignUiLoading => _campaignUiLoading;
    public string? CampaignUiLoadingStatus => IsCampaignUiLoading ? "Reading campaign menu fields…" : null;
    private void SetCampaignUiLoading(bool loading)
    {
        _campaignUiLoading = loading;
        OnPropertyChanged(nameof(IsCampaignUiLoading));
        OnPropertyChanged(nameof(CampaignUiLoadingStatus));
        OnPropertyChanged(nameof(CanOpenCampaignPresets));
        OnPropertyChanged(nameof(CanCreateCampaignPreset));
        OnPropertyChanged(nameof(CampaignPresetCreationProblem));
        OnPropertyChanged(nameof(CanMigrateCampaignPresets));
        OnPropertyChanged(nameof(CampaignPresetMigrationProblem));
        OnPropertyChanged(nameof(CanCreateCampaignScenario));
        OnPropertyChanged(nameof(CampaignScenarioCreationProblem));
        OnPropertyChanged(nameof(CanCreateCampaignUnitType));
        OnPropertyChanged(nameof(CampaignUnitTypeCreationProblem));
        OnPropertyChanged(nameof(CanSelectCampaignEntry));
        RaiseCampaignPackageAuthoringChanged();
    }
    public bool CanDiscardCampaignFieldDrafts => HasCampaignSourceDrafts && !IsCampaignFieldSaveBusy;
    public bool HasCampaignSourceChanges => _campaignHasRawSourceChanges;
    public string CampaignFieldGuidance => GetCampaignDraftBindingProblem() ?? _campaignFieldStatus ??
        "Edit values directly. Save checks the collected changes in the campaign menu and every declared play context.";
    public string CampaignFieldDraftSummary => (_campaignDrafts.Count + CampaignStructurePendingChanges.Count + CampaignCreditsPendingChanges.Count + CampaignPresetPendingChanges.Count + CampaignScenarioCreationPendingChanges.Count + CampaignUnitTypeCreationPendingChanges.Count + CampaignPackagePendingChanges.Count) + " pending campaign source changes.";
    public string? CampaignFieldReviewSummary => _campaignReviewSummary;
    public bool HasCampaignFieldReview => !string.IsNullOrWhiteSpace(_campaignReviewSummary);
    public string? CampaignFieldSaveProgress => IsCampaignFieldSaveBusy ? _campaignFieldStatus : null;
    internal bool UsesCampaignFieldEditing => _campaignFieldConfiguration is not null;
    internal bool CanUseLegacyCampaignFields => !RequiresSelectedCampaignAdapter && !UsesCampaignFieldEditing && !HasCampaignSourceDrafts &&
        !IsCampaignFieldSaveBusy && !_campaignHasRawSourceChanges && !IsReadOnly;

    public void ConfigureCampaignFieldEditing(WccEditorSessionContext session, WccLibrarySnapshot? snapshot,
        WccPackageRevisionId? rootRevisionId, WmlCampaignEvaluationPlan? plan, string? projectIdentity,
        string? addonsRoot, string? runtimeIdentity)
    {
        lock (_campaignFieldGate)
        {
            var configuration = new CampaignFieldConfiguration(session, snapshot, rootRevisionId, plan,
                projectIdentity ?? session.RootPath, addonsRoot, runtimeIdentity);
            if (_campaignFieldConfiguration is { } current && current.IsEquivalent(configuration)) return;
            if (TryRebindCheckedCampaignProjection(configuration)) return;
            RevokeCampaignFieldAuthority();
            _campaignFieldConfiguration = configuration;
            StartCampaignFieldProjection();
        }
    }

    public void CancelCampaignFieldEditing()
    {
        lock (_campaignFieldGate) { RevokeCampaignFieldAuthority(); CancelDeferredCampaignProjection(); RaiseCampaignFieldsChanged(); }
    }

    public Task WaitForCampaignFieldProjectionAsync(CancellationToken cancellationToken = default)
    {
        lock (_campaignFieldGate)
            return (_campaignProjectionDeferralCompletion?.Task ?? _campaignProjectionTask).WaitAsync(cancellationToken);
    }
    public async Task WaitForCampaignFieldSaveAsync(CancellationToken cancellationToken = default)
    {
        if (_campaignSaveTask is { } task) await task.WaitAsync(cancellationToken);
    }

    private void RevokeCampaignFieldAuthority()
    {
        CollectedSaveAuthorityChanging?.Invoke();
        _campaignFieldGeneration++;
        _contextCheckProjection = null;
        _contextCheckOwner = null;
        _contextCheckPlan = null;
        _contextCheckProblem = null;
        _campaignProjectionCancellation?.Cancel();
        _campaignProjectionCancellation?.Dispose();
        _campaignProjectionCancellation = null;
        _campaignFieldSession.Invalidate();
        SetCampaignUiLoading(false);
    }

    private void CampaignSourceWasRebound()
    {
        lock (_campaignFieldGate)
        {
            RetireCampaignProjectionDeferrals();
            _campaignLoadRequestVersion++;
            RevokeCampaignFieldAuthority();
            _campaignHasRawSourceChanges = false;
            _campaignProjection = null;
            _campaignProjectionBinding = null;
            if (_campaignSaveTask is { IsCompleted: true }) _campaignSaveTask = null;
            ResetCampaignEntries();
            _campaignPresetStorageCache = null;
            _campaignPresetCatalog = null;
            _campaignPresetCapturedSources = null;
            _campaignPresetCapture = null;
            _campaignPresetCaptureComplete = false;
            _campaignStructureInventory = null;
            _campaignStructureInventoryAuthoringKey = null;
            _campaignMenuOwner = null;
            _campaignProjectionDocument = null;
            _campaignRoutes.Clear();
            if (!HasCampaignSourceDrafts)
            {
                _campaignFields.Clear();
                // Release the previous owners even when this page is hidden
                // and no binding reads the lazily grouped field getter again.
                _campaignGroupedRows = [];
                _campaignFieldGroups = [];
            }
            StartCampaignFieldProjection();
        }
    }
    private void CampaignAccessWasChanged()
    {
        lock (_campaignFieldGate) { RevokeCampaignFieldAuthority(); StartCampaignFieldProjection(); }
    }

    private void StartCampaignFieldProjection()
    {
        if (DeferRequestedCampaignFieldProjection()) return;
        if (HasCampaignSourceDrafts || IsCampaignFieldSaveBusy && !_refreshingCheckedCampaignProjection || _campaignHasRawSourceChanges)
        { RaiseCampaignFieldsChanged(); return; }
        if (_project is not { } project || GetCurrentMainSourceRawWml() is not { } mainText || _campaignFieldConfiguration is not
            { Snapshot: { } snapshot, RootRevision: { } root } configuration || string.IsNullOrWhiteSpace(project.SourcePath))
        {
            _campaignFieldStatus = _project is null ? "Open a campaign main file." :
                "Reading campaign fields requires the current indexed project and pinned Core context.";
            RaiseCampaignFieldsChanged();
            return;
        }
        _campaignProjectionCancellation = new();
        var token = _campaignProjectionCancellation.Token;
        var generation = _campaignFieldGeneration;
        var document = project.Campaign;
        var source = new WmlStaticSource(project.SourcePath, mainText);
        _campaignUiLoadingGeneration = generation;
        SetCampaignUiLoading(true);
        _campaignFieldStatus = "Resolving campaign menu fields…";
        RaiseCampaignFieldsChanged();
        if (generation != _campaignFieldGeneration || token.IsCancellationRequested) return;
        _campaignProjectionTask = CaptureCampaignFieldsAsync(project, document, source, configuration, snapshot, root,
            _campaignAuthoring, AuthoringKey(_campaignAuthoring), generation, token);
    }

    private async Task CaptureCampaignFieldsAsync(WccProject project, CampaignDocument? document, WmlStaticSource source,
        CampaignFieldConfiguration configuration, WccLibrarySnapshot snapshot, WccPackageRevisionId root,
        WccCampaignAuthoringMetadata? authoring, string authoringKey, long generation, CancellationToken cancellationToken)
    {
        // Register the task before any cached capture or publication callback can rebind this host.
        await Task.Yield();
        var catalogChanged = false;
        var projectionPublished = false;
        try
        {
            var catalog = ReusableCampaignEntryCatalog(configuration, source);
            if (catalog is null)
            {
                var canOverlay = snapshot.RevisionsById.TryGetValue(root, out var revision) &&
                                 snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) &&
                                 package.StorageMode == WccPackageStorageMode.WorkingCopy;
                var freshCapture = await WccStaticSourceCapture.CaptureAsync(snapshot, root, source.Path,
                    canOverlay ? [source] : null, cancellationToken, configuration.AddonsRoot);
                // External inspection reads its exact indexed disk source. It must not
                // replace a stale opened main with newly indexed text behind the UI.
                if (!canOverlay && freshCapture.EntrySource is { } captured &&
                    (!SamePresentationSource(captured, source) || captured.Text != source.Text))
                    throw new InvalidOperationException("The campaign source changed. Reopen its main file before reading its menu and scenarios.");
                catalog = await Task.Run(() => WccCampaignEntryCatalog.Project(freshCapture, configuration.Plan, cancellationToken), cancellationToken);
            }
            var capture = catalog.Capture;
            var projection = catalog.Menu;
            var committedEntry = FindCheckedCampaignEntryPublication(catalog);
            var selectedPlan = committedEntry?.Plan ?? catalog.Entries.FirstOrDefault(entry => entry.Identity == _selectedCampaignEntry?.Identity)?.Plan
                ?? catalog.Entries.FirstOrDefault(entry => entry.CampaignId == configuration.Plan?.CampaignId)?.Plan
                ?? catalog.Entries.FirstOrDefault()?.Plan ?? projection.EffectivePlan ?? configuration.Plan;
            var selectedEntry = committedEntry ?? catalog.Entries.FirstOrDefault(entry => entry.Identity == _selectedCampaignEntry?.Identity)
                ?? catalog.Entries.FirstOrDefault(entry => entry.CampaignId == configuration.Plan?.CampaignId)
                ?? catalog.Entries.FirstOrDefault();
            var needsSelectedDisplay = selectedEntry is not null && (catalog.Entries.Count > 1 ||
                selectedEntry.OwnerSource is not { } ownerSource || !SamePresentationSource(ownerSource, source) ||
                selectedEntry.AuthoredCampaignSpan is null);
            var inventory = await Task.Run(() => needsSelectedDisplay
                ? WccCampaignStructureInventory.ProjectSelectedEntry(catalog, selectedEntry!, authoring, cancellationToken)
                : WccCampaignStructureInventory.Project(capture, projection, selectedPlan, authoring, cancellationToken), cancellationToken);
            WccSelectedCampaignEntryAdapter? selectedAdapter = null;
            if (needsSelectedDisplay && selectedEntry is not null)
            {
                try { selectedAdapter = WccSelectedCampaignEntryAdapter.Create(catalog, selectedEntry.Identity, selectedPlan); }
                catch (InvalidOperationException) { /* Display stays available; ordinary capability checks explain unsupported provenance. */ }
                if (selectedAdapter is not null) projection = selectedAdapter.Menu;
            }
            var presets = await Task.Run(() => WccCampaignLocalPresetCatalog.Build(capture,
                selectedPlan, catalog.Entries.Select(entry => entry.Plan).OfType<WmlCampaignEvaluationPlan>().ToArray()), cancellationToken);
            lock (_campaignFieldGate)
            {
                if (cancellationToken.IsCancellationRequested || generation != _campaignFieldGeneration ||
                    authoringKey != AuthoringKey(_campaignAuthoring) ||
                    !ReferenceEquals(project, _project) || !ReferenceEquals(document, _project.Campaign) ||
                    !ReferenceEquals(configuration, _campaignFieldConfiguration) || HasCampaignSourceDrafts) return;
                // An included menu can discover its first complete plan only in
                // this capture. Publish the adapter and its configuration together
                // so the shell's matching selection does not recapture the package.
                if (configuration.Plan is null && needsSelectedDisplay && selectedAdapter is not null &&
                    ReferenceEquals(selectedAdapter.Catalog, catalog) && ReferenceEquals(selectedAdapter.Entry, selectedEntry) &&
                    ReferenceEquals(selectedAdapter.Plan, selectedPlan) && SamePresentationSource(selectedAdapter.RootSource, source) &&
                    selectedAdapter.RootSource.Text == source.Text)
                {
                    configuration = configuration with { Plan = selectedAdapter.Plan };
                    _campaignFieldConfiguration = configuration;
                }
                var binding = new CampaignDraftBinding(project, document, configuration, generation);
                _publishingCampaignProjection = true;
                _campaignProjectionBinding = binding;
                _campaignProjection = projection;
                _selectedCampaignAdapter = selectedAdapter;
                catalogChanged = !ReferenceEquals(catalog, _campaignEntryCatalog);
                PublishCampaignEntryCatalog(catalog);
                _campaignPresetCatalog = presets;
                _campaignPresetCapturedSources = Array.AsReadOnly(capture.Sources.ToArray());
                _campaignPresetCapture = capture;
                _campaignPresetCaptureComplete = capture.IsCaptureComplete;
                _campaignStructureInventory = inventory;
                _campaignStructureInventoryAuthoringKey = AuthoringKey(_campaignAuthoring);
                _campaignProjectionDocument = document;
                _campaignMenuOwner = _selectedCampaignEntry?.Owner ?? (document is null ? null : CampaignInitialFieldProjector.BindAuthoredCampaign(projection, document));
                _campaignRoutes.Clear();
                _campaignFields.Clear();
                if (_campaignMenuOwner is { } owner)
                    foreach (var definition in CampaignInitialFieldCatalog.Fields)
                    {
                        if (!IsCurrentCampaignBinding(binding)) break;
                        _campaignFields.Add(new(this, owner, definition));
                    }
                _publishingCampaignProjection = false;
                if (!IsCurrentCampaignBinding(binding))
                {
                    if (ReferenceEquals(binding, _campaignProjectionBinding) && !HasCampaignSourceDrafts) _campaignFields.Clear();
                    return;
                }
                _campaignFieldStatus = CampaignEntryEditingGuidance ?? (_campaignMenuOwner is null ? projection.Diagnostic ??
                    "The campaign menu has no unique captured source owner. Use Main file RAW." :
                    projection.IsComplete ? null : projection.Diagnostic);
                RefreshCampaignStructure();
                RefreshCampaignCredits();
                RaiseCampaignFieldsChanged();
                projectionPublished = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            lock (_campaignFieldGate)
            {
                _publishingCampaignProjection = false;
                if (generation != _campaignFieldGeneration) return;
                _campaignFieldStatus = "Campaign menu fields could not be resolved: " + exception.Message;
                RaiseCampaignFieldsChanged();
            }
        }
        finally
        {
            lock (_campaignFieldGate)
                if (generation == _campaignFieldGeneration && generation == _campaignUiLoadingGeneration)
                {
                    SetCampaignUiLoading(false);
                    if (catalogChanged) CampaignEntryCatalogChanged?.Invoke(this, EventArgs.Empty);
                    if (projectionPublished) CampaignFieldProjectionPublished?.Invoke(this, EventArgs.Empty);
                }
        }
    }

    internal WccScenarioFieldRoute? GetCampaignFieldRoute(WmlStaticScalarOwner owner, string key)
    {
        lock (_campaignFieldGate)
        {
            if (_campaignRoutes.TryGetValue((owner, key), out var route)) return route;
            if (!ReferenceEquals(owner, _campaignMenuOwner) || _campaignProjection?.Source is not { } source) return null;
            route = WccScenarioFieldRouteAnalyzer.Analyze(owner, key, source, _campaignProjection.Context);
            _campaignRoutes[(owner, key)] = route;
            return route;
        }
    }

    internal bool CanReviewCampaignField(WmlStaticScalarOwner owner, string key) =>
        CampaignInitialFieldCatalog.TryGet(key, out var definition) && definition.IsEditable &&
        GetCampaignFieldBlockReason(GetCampaignFieldRoute(owner, key)) is null;
    internal string GetCampaignFieldEditStatus(WmlStaticScalarOwner owner, string key) =>
        GetCampaignFieldBlockReason(GetCampaignFieldRoute(owner, key)) ??
        "Save checks this exact source value with all campaign drafts.";

    private string? GetCampaignFieldBlockReason(WccScenarioFieldRoute? route)
    {
        if (HasCampaignPackageEditDraft || IsCampaignDependencySourceRefreshing) return "Save or discard the pending dependency or chapter change before editing campaign fields.";
        if (RequiresSelectedCampaignAdapter && !HasSelectedCampaignEntryBinding)
            return "The selected campaign needs one exact captured menu source and complete context plan before editing. Its source remains available for inspection.";
        if (_changingCampaignPresets) return "Finish the current preset draft update before editing.";
        if ((_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation)) return "Finish the current new scenario draft update before editing.";
        if (IsReadOnly || _campaignFieldConfiguration?.Session.CanWrite != true) return "This campaign is open read-only; source remains available for inspection.";
        if (IsCampaignFieldSaveBusy || _campaignFieldSession.IsBusy) return "Finish the current campaign save before editing.";
        if (_campaignFieldSession.Recovery is not null) return "A previous save requires source recovery before editing.";
        if (_campaignHasRawSourceChanges) return "Save source changes before editing resolved campaign fields.";
        if (_publishingCampaignProjection || _campaignProjectionBinding is not { } projectionBinding ||
            !IsCurrentCampaignBinding(projectionBinding)) return "Resolving the current campaign source and context…";
        if (GetCampaignDraftBindingProblem() is { } bindingProblem) return bindingProblem;
        if (_campaignFieldConfiguration is not { Snapshot: not null, RootRevision: not null } ||
            _campaignProjection is not { IsComplete: true } || !ReferenceEquals(_campaignProjectionDocument, _project?.Campaign))
            return _campaignFieldStatus ?? "The campaign needs a current captured menu and context plan before a checked edit.";
        if (_campaignFieldConfiguration.Plan is { } configuredPlan && (_campaignProjection.EvaluationEntrySource ?? _campaignProjection.Source) is { } main &&
            (!string.Equals(configuredPlan.CampaignMainSourcePath, main.Path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                configuredPlan.CampaignMainTextHash != main.ContentHash))
            return "The configured play plan no longer matches the captured campaign main source; refresh its context before editing.";
        if (_campaignMenuOwner is not { } currentOwner) return "This field has no current exact campaign owner.";
        if (CheckCurrentCampaignContextPlan(currentOwner) is { } contextProblem)
            return contextProblem;
        if (route is null || !ReferenceEquals(route.Owner, _campaignMenuOwner)) return "This field has no current exact campaign owner.";
        if (!CampaignInitialFieldCatalog.TryGet(route.Key, out var definition) || !definition.IsEditable)
            return definition?.Hint ?? "Campaign context configuration is available in RAW source in this slice.";
        if (route.Kind == WccScenarioFieldRouteKind.RawOnly || !(route.CanSet || route.CanInsert)) return route.Diagnostic;
        if (!IsPathInsideCampaignRoot(route.Source.Path)) return "The source must be inside the open writable campaign project.";
        return null;
    }

    // These objects are immutable captured evidence. Cache only this display
    // eligibility check; live access/source guards above and the fresh Save
    // reviewer still run normally. Revocation releases the previous capture.
    private string? CheckCurrentCampaignContextPlan(WmlStaticScalarOwner owner)
    {
        var plan = EffectiveCampaignPlan;
        if (!ReferenceEquals(_contextCheckProjection, _campaignProjection) || !ReferenceEquals(_contextCheckOwner, owner) ||
            !ReferenceEquals(_contextCheckPlan, plan))
        {
            _contextCheckProblem = CampaignInitialFieldProjector.CheckContextPlan(_campaignProjection!, owner, plan);
            _contextCheckProjection = _campaignProjection;
            _contextCheckOwner = owner;
            _contextCheckPlan = plan;
        }
        return _contextCheckProblem;
    }

    internal WccScenarioInlineDraft? FindCampaignFieldDraft(WccScenarioFieldRoute? route)
    {
        lock (_campaignFieldGate)
        {
        if (!HasCampaignFieldDrafts || route is null || route.Kind == WccScenarioFieldRouteKind.RawOnly || !(route.CanSet || route.CanInsert)) return null;
        var identity = WccScenarioFieldBatchCandidatePlanner.GetSourceEditIdentity(route);
        return _campaignDrafts.FirstOrDefault(draft => draft.GroupIdentity == identity);
        }
    }

    internal void SetCampaignFieldValue(WmlStaticScalarOwner owner, string key, string? value, WccScenarioFieldValueInputKind inputKind)
    {
        lock (_campaignFieldGate)
        {
            var route = GetCampaignFieldRoute(owner, key);
            if (route is null || !CanReviewCampaignField(owner, key)) return;
            SetCampaignFieldDraft(route, route.CanSet ? WmlFieldOperation.Set : WmlFieldOperation.Insert, value, inputKind);
        }
    }
    internal void RemoveCampaignFieldOverride(WmlStaticScalarOwner owner, string key)
    {
        lock (_campaignFieldGate)
        {
            var route = GetCampaignFieldRoute(owner, key);
            if (route?.CanRemove != true || !CanReviewCampaignField(owner, key)) return;
            SetCampaignFieldDraft(route, WmlFieldOperation.Remove, null, WccScenarioFieldValueInputKind.Literal);
        }
    }
    private void SetCampaignFieldDraft(WccScenarioFieldRoute route, WmlFieldOperation operation, string? text,
        WccScenarioFieldValueInputKind inputKind)
    {
        if (!CampaignInitialFieldCatalog.TryGet(route.Key, out var definition)) return;
        var identity = WccScenarioFieldBatchCandidatePlanner.GetSourceEditIdentity(route);
        var existing = _campaignDrafts.FirstOrDefault(draft => draft.GroupIdentity == identity);
        var validation = operation == WmlFieldOperation.Remove ? null : WccScenarioFieldValueCodec.ValidateInput(definition, text, inputKind);
        var sourceIsExpression = WccScenarioFieldValueCodec.InferInputKind(route.SourceValue) == WccScenarioFieldValueInputKind.WmlExpression;
        var unchanged = operation != WmlFieldOperation.Remove && validation is null && route.CanSet &&
            (inputKind == WccScenarioFieldValueInputKind.Literal && !sourceIsExpression && WccScenarioFieldValueCodec.Equivalent(definition, route.Value, text) ||
             inputKind == WccScenarioFieldValueInputKind.WmlExpression && string.Equals(route.SourceValue, text, StringComparison.Ordinal));
        if (unchanged)
        {
            if (existing is not null) _campaignDrafts.Remove(existing);
            FinishCampaignDraftMutation();
            return;
        }
        _campaignDraftBinding ??= new(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
        var draft = new WccScenarioInlineDraft(identity, route, operation, text, definition.Label, validation) { ValueInputKind = inputKind };
        if (existing is null) _campaignDrafts.Add(draft); else _campaignDrafts[_campaignDrafts.IndexOf(existing)] = draft;
        _campaignFieldStatus = null;
        _campaignReviewSummary = null;
        FinishCampaignDraftMutation();
    }
    internal void RevertCampaignField(WmlStaticScalarOwner owner, string key)
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignFieldSaveBusy || _publishingCampaignStructure || _changingCampaignStructure ||
                _publishingCampaignCredits || _changingCampaignCredits || _changingCampaignPresets || (_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation)) return;
            if (FindCampaignFieldDraft(GetCampaignFieldRoute(owner, key)) is { } draft) _campaignDrafts.Remove(draft);
            FinishCampaignDraftMutation();
        }
    }
    public void DiscardCampaignFieldDrafts()
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignFieldSaveBusy || _publishingCampaignStructure || _changingCampaignStructure ||
                _publishingCampaignCredits || _changingCampaignCredits || _changingCampaignPresets || (_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation)) return;
            DiscardCampaignTextInputs();
            foreach (var field in _campaignFields) field.ResetInputState();
            _campaignDrafts.Clear();
            ClearCampaignStructureDrafts();
            ClearCampaignCreditsDrafts();
            ClearCampaignPresetDrafts();
            ClearCampaignScenarioCreationDraft();
            ClearCampaignUnitTypeCreationDraft();
            ClearCampaignPackageEditDraft();
            _campaignDraftBinding = null;
            _campaignFieldStatus = null;
            RevokeCampaignFieldAuthority();
            StartCampaignFieldProjection();
        }
    }
    private void FinishCampaignDraftMutation()
    {
        if (!HasCampaignSourceDrafts) _campaignDraftBinding = null;
        RaiseCampaignFieldsChanged();
    }
    private string? GetCampaignDraftBindingProblem()
    {
        if (_campaignDraftBinding is not { } binding) return null;
        return binding.Generation != _campaignFieldGeneration || !ReferenceEquals(binding.Project, _project) ||
            !ReferenceEquals(binding.Document, _project?.Campaign) || !ReferenceEquals(binding.Configuration, _campaignFieldConfiguration)
            ? "The campaign source or editing context changed. Drafts are retained; discard them to reload current fields." : null;
    }
    private bool IsCurrentCampaignBinding(CampaignDraftBinding binding) => binding.Generation == _campaignFieldGeneration &&
        ReferenceEquals(binding.Project, _project) && ReferenceEquals(binding.Document, _project?.Campaign) &&
        ReferenceEquals(binding.Configuration, _campaignFieldConfiguration);

    internal string? GetCampaignFieldDefaultValue(WmlStaticScalarOwner owner, ScenarioInitialFieldDefinition definition)
    {
        if (owner.ResolveField(definition.Key).State != WmlStaticScalarResolutionState.ProvenAbsent) return null;
        if (definition.Default.Value is { } value) return value;
        if (definition.Key is not ("start_year" or "end_year")) return null;
        string? Dependency(string key)
        {
            var draft = FindCampaignFieldDraft(GetCampaignFieldRoute(owner, key));
            if (draft is not null) return draft.Operation != WmlFieldOperation.Remove && draft.ValidationError is null &&
                draft.ValueInputKind == WccScenarioFieldValueInputKind.Literal ? draft.Text : null;
            var resolution = owner.ResolveField(key);
            return resolution.State == WmlStaticScalarResolutionState.Known ? WccScenarioFieldValueCodec.DecodeLiteral(resolution.ValueText) : null;
        }
        if (definition.Key == "start_year") return Dependency("year");
        var startDraft = FindCampaignFieldDraft(GetCampaignFieldRoute(owner, "start_year"));
        if (startDraft is not null) return Dependency("start_year");
        return owner.ResolveField("start_year").State == WmlStaticScalarResolutionState.ProvenAbsent ? Dependency("year") : Dependency("start_year");
    }

    internal CampaignFieldSourceLocation? GetCampaignFieldSourceLocation(WmlStaticScalarOwner owner, string key)
    {
        var route = GetCampaignFieldRoute(owner, key);
        var range = route?.Invocation ?? route?.Assignment ?? route?.Target;
        range ??= owner.Fields.TryGetValue(key, out var capturedField)
            ? capturedField.WinningContribution?.AssignmentOrigin : null;
        range ??= owner.OpeningExpansionStack.Reverse().Where(frame => frame.InvocationOrigins.Count == 1)
            .Select(frame => frame.InvocationOrigins[0]).FirstOrDefault();
        range ??= owner.OpeningOrigin;
        return range is not null ? new(range.Source.Path, range.Source.Text, range.StartOffset, range.Length,
            "Captured source for " + key + ". This view is read-only; shared definitions remain unchanged.") : null;
    }

    public Task<WccScenarioFieldBatchEditResult> SaveCampaignFieldDraftsAsync(CancellationToken cancellationToken = default)
    {
        lock (_campaignFieldGate)
        {
            if (_campaignSaveTask is { IsCompleted: false } running) return running;
            var completion = new TaskCompletionSource<WccScenarioFieldBatchEditResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _campaignSaveTask = completion.Task;
            _ = CompleteCampaignFieldSaveAsync(completion, cancellationToken);
            return _campaignSaveTask;
        }
    }
    private async Task CompleteCampaignFieldSaveAsync(TaskCompletionSource<WccScenarioFieldBatchEditResult> completion,
        CancellationToken cancellationToken)
    {
        try { completion.TrySetResult(await SaveCampaignFieldDraftsCoreAsync(cancellationToken)); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
    private async Task<WccScenarioFieldBatchEditResult> SaveCampaignFieldDraftsCoreAsync(CancellationToken cancellationToken)
    {
        if (!CommitCampaignTextInputs(out var inputProblem)) return RefuseCampaignSave(inputProblem!);
        if (HasSelectedCampaignEntryDrafts) return RefuseCampaignSave("Use the global Save to check the selected campaign's source changes together.");
        if (HasCampaignStructureDrafts || HasCampaignCreditsDrafts || HasCampaignPresetDrafts || HasCampaignScenarioCreationDraft || HasCampaignUnitTypeCreationDraft) return RefuseCampaignSave("Use the global Save button to check campaign presentation, credits, presets, new scenarios, unit types and field changes together.");
        if (!HasCampaignFieldDrafts) return new(WccScenarioFieldEditStatus.InvalidRequest, "There are no campaign field drafts to save.");
        if (GetCampaignDraftBindingProblem() is { } stale) return RefuseCampaignSave(stale);
        if (_campaignDrafts.FirstOrDefault(draft => draft.ValidationError is not null)?.ValidationError is { } validation)
            return RefuseCampaignSave(validation);
        if (GetCampaignFieldBlockReason(_campaignDrafts[0].Route) is { } problem) return RefuseCampaignSave(problem);
        var binding = _campaignDraftBinding!;
        var configuration = binding.Configuration;
        var owner = _campaignDrafts[0].Route.Owner;
        var state = new WccScenarioFieldEditSessionState(configuration.ProjectIdentity!, configuration.Session.RootPath!,
            configuration.RootRevision!.Value.Value.ToString(), true, configuration.Snapshot!, configuration.RootRevision.Value,
            _campaignProjection!.Source!, EffectiveCampaignPlan, _campaignProjection.Context, owner,
            configuration.AddonsRoot, configuration.RuntimeIdentity) { ReviewScope = WccScenarioFieldReviewScope.CampaignMenu, EntryReview = CurrentCampaignEntryReview };
        var proposals = _campaignDrafts.Select(BuildCampaignFieldProposal).ToArray();
        _campaignSaveBusy = true;
        _campaignFieldStatus = Status = "Checking and saving campaign fields…";
        RaiseCampaignFieldsChanged();
        WccScenarioFieldBatchEditResult? savedResult = null;
        try
        {
            Task<WccScenarioFieldBatchEditResult> operation;
            lock (_campaignFieldGate)
            {
                if (!IsCurrentCampaignBinding(binding) || IsReadOnly || _campaignFieldConfiguration?.Session.CanWrite != true)
                    return RefuseCampaignSave("The campaign source or access changed before checking. Drafts are retained.");
                // Register authority while holding the host gate. Any later host
                // revocation reaches this registered session before persistence.
                operation = _campaignFieldSession.SaveBatchAsync(state, proposals, cancellationToken);
            }
            var result = await operation;
            if (!result.IsSuccess)
            { _campaignFieldStatus = Status = result.Diagnostic; return result; }
            savedResult = result;
            if (result.Candidate is not { } candidate) return RefuseCampaignSave("The saved campaign candidate is unavailable.");
            var text = candidate.CandidateSource.Text;

            lock (_campaignFieldGate)
            {
                if (!ReferenceEquals(binding.Project, _project) || !ReferenceEquals(binding.Document, _project?.Campaign)) return result;
                SourceChanging?.Invoke();
                if (!ReferenceEquals(binding.Project, _project) || !ReferenceEquals(binding.Document, _project?.Campaign)) return result;
                PublishSavedCampaignMenuSource(text, state.Source.Path);
                _campaignHasRawSourceChanges = false;
                _campaignDrafts.Clear();
                _campaignDraftBinding = null;
                _campaignProjection = null;
                _campaignStructureInventory = null;
                _campaignMenuOwner = null;
                _campaignRoutes.Clear();
                _campaignReviewSummary = BuildCampaignReviewSummary(result.Review);
                if (_project?.Campaign is not null) RunUiSync(RefreshLegacyCampaignFields);
                _campaignFieldStatus = Status = "Refreshing campaign preview…";
                RaiseCampaignFieldsChanged();
                CampaignFieldsSaved?.Invoke(this, EventArgs.Empty);
            }
            if (CampaignFieldLibraryRefreshAsync is { } refresh) await refresh();
            _campaignFieldStatus = Status = "Campaign changes saved.";
            return result;
        }
        catch (OperationCanceledException)
        {
            if (savedResult is not null)
            { _campaignFieldStatus = Status = "Campaign changes were saved; the preview refresh was cancelled."; return savedResult; }
            return RefuseCampaignSave("The campaign check was cancelled. Drafts are retained.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (savedResult is not null)
            { _campaignFieldStatus = Status = "Campaign changes were saved; the preview could not refresh: " + exception.Message; return savedResult; }
            return RefuseCampaignSave("Could not finish the campaign save: " + exception.Message);
        }
        finally
        {
            lock (_campaignFieldGate)
            {
                _campaignSaveBusy = false;
                if (!HasCampaignSourceDrafts) { RevokeCampaignFieldAuthority(); StartCampaignFieldProjection(); }
                RaiseCampaignFieldsChanged();
            }
        }
    }
    private WccScenarioFieldBatchEditResult RefuseCampaignSave(string diagnostic)
    {
        _campaignFieldStatus = Status = diagnostic;
        RaiseCampaignFieldsChanged();
        return new(WccScenarioFieldEditStatus.InvalidRequest, diagnostic);
    }
    private static string? BuildCampaignReviewSummary(WccScenarioFieldBatchReviewResult? review)
    {
        if (review is null) return null;
        var lines = review.Fields.SelectMany(field => field.Contexts.Select(context => field.Route.Key + " (" +
            context.DisplayName + "): " + (context.OldValue ?? "absent") + " → " + (context.NewValue ?? "absent"))).ToList();
        if (review.CoupledEffectCount > 0) lines.Add(review.CoupledEffectCount + " additional resolved values changed through the same source.");
        lines.AddRange(review.Contexts.SelectMany(context => context.CoupledEffects.Select(effect => context.DisplayName + ": " + effect)));
        return string.Join(Environment.NewLine, lines);
    }

    private bool IsPathInsideCampaignRoot(string path)
    {
        var root = _campaignFieldConfiguration?.Session.RootPath;
        if (string.IsNullOrWhiteSpace(root)) return false;
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparer);
    }
    private void RaiseCampaignFieldsChanged()
    {
        lock (_campaignFieldGate)
        {
        if (_raisingCampaignFields) return;
        _raisingCampaignFields = true;
        try
        {
            foreach (var field in _campaignFields.ToArray()) field.Refresh();
            RaiseCampaignStructureChanged();
            RaiseCampaignCreditsChanged();
            RaiseCampaignPresentationChanged();
            RaiseCampaignPackageAuthoringChanged();
            RaiseCampaignChapterAuthoringChanged();
            foreach (var property in new[] { nameof(CampaignFields), nameof(CampaignFieldGroups), nameof(HasCampaignFieldDrafts),
                nameof(HasCampaignSourceDrafts), nameof(HasCampaignPresetDrafts), nameof(CampaignLocalPresets), nameof(CampaignPresetPendingChanges),
                nameof(HasCampaignPresetMigrationDraft), nameof(CanMigrateCampaignPresets), nameof(CanRevertCampaignPresetMigration),
                nameof(CampaignPresetMigrationProblem), nameof(CampaignPresetStoragePath),
                nameof(HasCampaignScenarioCreationDraft), nameof(CampaignScenarioCreationPendingChanges), nameof(CanCreateCampaignScenario), nameof(CampaignScenarioCreationProblem),
                nameof(HasCampaignUnitTypeCreationDraft), nameof(CampaignUnitTypeCreationPendingChanges), nameof(CanCreateCampaignUnitType), nameof(CampaignUnitTypeCreationProblem),
                nameof(CanOpenCampaignPresets), nameof(CanCreateCampaignPreset), nameof(CampaignPresetCreationProblem), nameof(CampaignPresetApplicationProblem),
                nameof(IsCampaignFieldSaveBusy), nameof(CanDiscardCampaignFieldDrafts), nameof(CampaignFieldGuidance),
                nameof(CampaignFieldDraftSummary), nameof(CampaignFieldReviewSummary), nameof(HasCampaignFieldReview),
                nameof(CampaignFieldSaveProgress), nameof(HasCampaignSourceChanges), nameof(CanEditProject),
                nameof(CanEditCampaignMainRaw), nameof(CampaignSourceGuidance), nameof(HasCampaignMainSource),
                nameof(CampaignAdditionalCodeEntries), nameof(CampaignDifficultyEntries), nameof(CampaignDifficulties) })
                OnPropertyChanged(property);
            RaiseEditingStateChanged();
            CampaignFieldDraftsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _raisingCampaignFields = false; }
        }
    }
    private void RefreshLegacyCampaignFields()
    {
        var doc = _project!.Campaign!;
        TextDomainName = doc.TextDomain?.Name;
        TextDomainPath = doc.TextDomain?.Path;
        CampaignId = doc.Campaign.Id;
        CampaignNameWml = doc.Campaign.NameWml;
        CampaignAbbrevWml = doc.Campaign.AbbrevWml;
        CampaignDefine = doc.Campaign.Define;
        CampaignFirstScenario = doc.Campaign.FirstScenario;
        CampaignIcon = doc.Campaign.Icon;
        CampaignImage = doc.Campaign.Image;
        CampaignBackground = doc.Campaign.Background;
        CampaignRank = doc.Campaign.Rank?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CampaignYear = doc.Campaign.Year;
        CampaignDescriptionWml = doc.Campaign.DescriptionWml;
    }
    private sealed record CampaignDraftBinding(WccProject Project, CampaignDocument? Document,
        CampaignFieldConfiguration Configuration, long Generation);
    private sealed record CampaignFieldConfiguration(WccEditorSessionContext Session, WccLibrarySnapshot? Snapshot,
        WccPackageRevisionId? RootRevision, WmlCampaignEvaluationPlan? Plan, string? ProjectIdentity,
        string? AddonsRoot, string? RuntimeIdentity)
    {
        public bool IsEquivalent(CampaignFieldConfiguration other) => Session == other.Session &&
            ReferenceEquals(Snapshot, other.Snapshot) && RootRevision == other.RootRevision &&
            PlanIdentity(Plan) == PlanIdentity(other.Plan) && ProjectIdentity == other.ProjectIdentity &&
            AddonsRoot == other.AddonsRoot && RuntimeIdentity == other.RuntimeIdentity;
        private static string? PlanIdentity(WmlCampaignEvaluationPlan? plan) => plan is null ? null :
            plan.CampaignMainSourcePath + "\u001f" + plan.CampaignMainTextHash + "\u001f" + plan.IsContextDiscoveryComplete +
            "\u001f" + plan.AuthoredCampaignSpan?.StartIndex + "\u001f" + plan.AuthoredCampaignSpan?.EndIndexExclusive +
            "\u001f" + plan.AuthoredCampaignSpan?.RawText +
            "\u001f" + string.Join("\u001e", plan.Contexts.Select(context => context.SemanticKey));
    }
}
