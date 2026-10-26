using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core;
using Wcc.Core.Scenarios;
using Wcc.Core.Projects;
using Wcc.Core.Wml;
using Wcc.Core.Units;
using Wcc.Core.References;
using Wcc.Core.Library;
using Wcc.Core.Maps;
using Avalonia.Media.Imaging;
using Wcc.App.Preview;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel : ViewModelBase
{
    private static readonly HashSet<string> AuthoredMutationPropertyNames = new(StringComparer.Ordinal)
    {
        nameof(ScenarioId),
        nameof(ScenarioNextScenario),
        nameof(ScenarioMapFile),
        nameof(ScenarioMapData),
        nameof(ScenarioTurns),
        nameof(ScenarioTurnAt),
        nameof(ScenarioRandomStartTime),
        nameof(ScenarioMusic),
        nameof(ScenarioVictoryWhenEnemiesDefeated),
        nameof(SideNumber),
        nameof(SideX),
        nameof(SideY),
        nameof(SideGold),
        nameof(SideController),
        nameof(SideTeamName),
        nameof(SideUserTeamName),
        nameof(SideCanRecruit),
        nameof(SideType),
        nameof(SideId),
        nameof(SideName),
        nameof(SideRecruit),
        nameof(SideDisplayName),
        nameof(SideColor),
        nameof(SideIncome),
        nameof(SideVillageGold),
        nameof(SideVillageSupport),
        nameof(SideRecallCost),
        nameof(SidePersistent),
        nameof(SideSaveId),
        nameof(SideFog),
        nameof(SideShroud),
        nameof(SideShareVision),
        nameof(SideHidden),
        nameof(SideScrollToLeader),
        nameof(SideDefeatCondition),
        nameof(SideNoLeader),
        nameof(SideRecruitmentPattern),
        nameof(LeaderGender),
        nameof(LeaderVariation),
        nameof(LeaderFacing),
        nameof(LeaderUnrenamable),
        nameof(LeaderRandomTraits),
        nameof(LeaderRandomGender),
        nameof(LeaderPlacement),
        nameof(LeaderLocationId),
        nameof(LeaderExtraRecruit)
    };

    private readonly IWmlMacroExpansionService _macroExpansionService = new WmlMacroExpansionService();
    private readonly IWmlEffectiveDocumentProjector _effectiveDocumentProjector;
    private readonly ScenarioMapSourceResolver _mapSourceResolver;
    private readonly ScenarioSpatialProjectionBuilder _spatialProjectionBuilder;
    private readonly ScenarioEventProjectionBuilder _eventProjectionBuilder;
    private readonly WesnothPreprocessor _wesnothPreprocessor;
    public event EventHandler<ProjectLibraryBindingSelectionEventArgs>? LibraryBindingSelectionChanged;

    protected new bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!IsSynchronizingUi && (HasWccInlineDrafts || IsWccInlineSaveBusy || HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft)) && propertyName is not null &&
            AuthoredMutationPropertyNames.Contains(propertyName))
        {
            Status = "Save or discard pending field, music, schedule, flag or event drafts before changing other source fields.";
            RunUiSync(() => OnPropertyChanged(propertyName));
            return false;
        }

        if (!IsSynchronizingUi && SelectedSide?.IsGenerated == true && propertyName is not null &&
            AuthoredMutationPropertyNames.Contains(propertyName) &&
            (propertyName.StartsWith("Side", StringComparison.Ordinal) ||
             propertyName.StartsWith("Leader", StringComparison.Ordinal)))
            return false;

        if (!EqualityComparer<T>.Default.Equals(field, value) &&
            propertyName is not null &&
            (AuthoredMutationPropertyNames.Contains(propertyName) ||
             propertyName is nameof(SelectedScenario) or nameof(SelectedSide) or
                 nameof(SelectedLeader) or nameof(LoadedPath) or
                 nameof(SelectedEvaluationContext) or nameof(SelectedEditorSectionIndex)))
        {
            // Publication must end before even the bound input changes: a
            // coordinator can reach its final write on another thread.
            if (!IsMusicDraftUiHydration(propertyName)) InvalidateSourceEditingSession();
        }

        return base.SetField(ref field, value, propertyName);
    }

    /// <summary>Engine WML is paused in the production editor while WCC resolution is completed.</summary>
    public bool IsEngineWmlEvaluationEnabled { get; }

    public ScenarioWindowViewModel() : this(engineWmlEvaluationEnabled: false)
    {
    }

    // Retain the engine integration for explicit regression fixtures without a
    // production UI setting that can restart it during the WCC-only slice.
    private ScenarioWindowViewModel(bool engineWmlEvaluationEnabled)
    {
        IsEngineWmlEvaluationEnabled = engineWmlEvaluationEnabled;
        InitializeSourceEditing();
        _effectiveDocumentProjector = new WmlEffectiveDocumentProjector(_macroExpansionService);
        _mapSourceResolver = new ScenarioMapSourceResolver(_macroExpansionService);
        _spatialProjectionBuilder = new ScenarioSpatialProjectionBuilder(_macroExpansionService);
        _eventProjectionBuilder = new ScenarioEventProjectionBuilder(_macroExpansionService);
        _wesnothPreprocessor = new WesnothPreprocessor(WccAppPaths.GetWesnothPreprocessCacheRoot());
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is { } propertyName &&
                AuthoredMutationPropertyNames.Contains(propertyName))
            {
                if (!IsSynchronizingUi && !IsReadOnly)
                    EditVersion++;
                InvalidateExactPreviewForAuthoredEdit();
                if (propertyName is nameof(ScenarioTurns) or nameof(SideNumber) or nameof(SideGold)
                    or nameof(SideIncome))
                    RaiseInitialScalarFieldsChanged();
            }
        };
    }

    private string? _loadedPath;

    public string? LoadedPath
    {
        get => _loadedPath;
        set => SetField(ref _loadedPath, value);
    }

    private string? _status;

    public string? Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private int _editVersion;

    public int EditVersion
    {
        get => _editVersion;
        private set => SetField(ref _editVersion, value);
    }

    private int _uiSyncDepth;
    public bool IsSynchronizingUi => _uiSyncDepth > 0;

    private void RunUiSync(Action action)
    {
        var displayRefresh = DeferFieldDisplayRefresh();
        Exception? actionFailure = null;
        _uiSyncDepth++;
        try
        {
            action();
        }
        catch (Exception exception)
        {
            actionFailure = exception;
            throw;
        }
        finally
        {
            _uiSyncDepth--;
            if (actionFailure is null) displayRefresh.Dispose();
            else
            {
                // A display callback must not replace the delegate's exception.
                // The refresh queue retains unfinished work for a later request.
                try { displayRefresh.Dispose(); }
                catch (Exception) { }
            }
        }
    }

    public void NotifyDocumentChanged()
    {
        // This notification follows an actual authored mutation. A concurrent
        // preview hydration must not suppress the dirty/source-resolution barrier.
        if (IsReadOnly)
            return;

        InvalidateSourceEditingSession();
        EditVersion++;
        InvalidateAuthoredSourcePreview();
    }

    public void InvalidateExactPreviewForAuthoredEdit()
    {
        if (IsSynchronizingUi)
            return;

        InvalidateAuthoredSourcePreview();
    }

    private void InvalidateAuthoredSourcePreview()
    {
        // Rendering is intentionally conservative and may normalize WML that the
        // user never touched. Once an actual editor mutation occurs, however, the
        // rendered working copy is the only honest input for exact-source matching.
        _hasAuthoredSourceChanges = true;
        CancelBackgroundOperations();
        EffectiveContextValuesSummary = null;
        ClearEffectiveContextSourceBreadcrumbs();
        SetEvaluationContextPreviewDetail(
            IsEngineWmlEvaluationEnabled
                ? "authored WML changed; reload the preview before treating this context as exact."
                : "WCC source changed; save or reload before resolving initial values.");
        const string staleDiagnostic =
            "The displayed map/event projection may predate the latest authored field edit; use Reload Map before relying on it.";
        if (!_mapPreviewDiagnosticMessages.Contains(staleDiagnostic, StringComparer.Ordinal))
            SetMapPreviewDiagnosticMessages(new[] { staleDiagnostic }.Concat(_mapPreviewDiagnosticMessages));
    }

    public void CancelBackgroundOperations() => CancelBackgroundOperations(reuseStaticSources: false);

    private void CancelBackgroundOperations(bool reuseStaticSources)
    {
        CancelWccFieldImagePreviews();
        CancelStaticInitialValues(reuseStaticSources);
        InvalidateSourceEditingSession();
        var cancellation = _exactPreviewCancellation;
        _exactPreviewCancellation = null;
        _exactPreviewGeneration++;
        if (cancellation is null)
            return;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private string? _loadedAssetInternalId;
    private string? _currentSourcePath;
    private ScenarioFileAsset? _currentScenarioAsset;
    private string? _pristineLoadedSourceText;
    private bool _hasAuthoredSourceChanges;
    private readonly List<WmlMacroDefinition> _currentSourceMacroDefinitions = new();
    private WccLibrarySnapshot? _macroLibrarySnapshot;
    private IReadOnlySet<WccPackageRevisionId>? _macroVisibleRevisionIds;
    private IReadOnlySet<WccPackageRevisionId>? _exactPreprocessVisibleRevisionIds;
    private IWmlMacroExpansionDefinitionResolver? _macroDefinitionResolver;

    private WesnothTerrainVisualCatalog _terrainVisualCatalog =
        WesnothTerrainVisualCatalog.LoadCore(null);

    private List<CatalogPickerOptionVm> _unitCatalogOptions = new();
    private string? _wesnothInstallPath;
    private string? _wesnothAddonsPath;
    private string _wesnothRuntimeProfileIdentity = "unconfigured-runtime";
    private CancellationTokenSource? _exactPreviewCancellation;
    private int _exactPreviewGeneration;
    private string? _projectMetadataRoot;
    private WccProjectMetadata? _projectMetadata;
    private Bitmap? _sideLeaderPreviewBitmap;
    private string? _sideLeaderPreviewPath;
    private string? _sideLeaderPreviewStatus;
    private bool _isReadOnly;
    private WmlEvaluationContext _evaluationContext = new();

    public ObservableCollection<WmlEvaluationContext> EvaluationContexts { get; } = new();

    private WmlEvaluationContext? _selectedEvaluationContext;
    private bool _publishingEvaluationContexts;

    public WmlEvaluationContext? SelectedEvaluationContext
    {
        get => _selectedEvaluationContext;
        set
        {
            // A ComboBox clears its selection as the context collection is rebuilt.
            // That transient value is presentation, not a requested context change.
            if (_publishingEvaluationContexts) return;
            if (!IsSynchronizingUi && IsWccInlineSaveBusy)
            {
                OnPropertyChanged(nameof(SelectedEvaluationContext));
                return;
            }

            using var displayRefresh = DeferFieldDisplayRefresh();
            if (!SetField(ref _selectedEvaluationContext, value))
                return;
            SetEvaluationContext(value);
        }
    }

    public WmlEvaluationContext EvaluationContext => _evaluationContext;
    public string EvaluationContextLabel => _evaluationContext.DisplayName;

    public bool HasDeclaredEvaluationContexts =>
        EvaluationContexts.Count > 1 ||
        !string.IsNullOrWhiteSpace(_evaluationContext.CampaignDefine) ||
        _sourceEvaluationPlan is { IsContextDiscoveryComplete: false };

    private string _evaluationContextPreviewDetail =
        "WCC source preview (engine WML evaluation paused).";

    public string EvaluationContextStatus
    {
        get
        {
            var status = $"{_evaluationContext.DisplayName} · {_evaluationContextPreviewDetail}";
            if (_sourceEvaluationPlan is not { IsContextDiscoveryComplete: false })
                return status;
            return status +
                   " · Campaign difficulty discovery is incomplete; preview choices are available, but reviewed editing is unavailable.";
        }
    }

    private string? _effectiveContextValuesSummary;

    public string? EffectiveContextValuesSummary
    {
        get => _effectiveContextValuesSummary;
        private set
        {
            if (!SetField(ref _effectiveContextValuesSummary, value))
                return;
            OnPropertyChanged(nameof(HasEffectiveContextValues));
        }
    }

    public bool HasEffectiveContextValues =>
        IsEngineWmlEvaluationEnabled && !string.IsNullOrWhiteSpace(EffectiveContextValuesSummary);

    public ObservableCollection<ScenarioScalarSourceBreadcrumbVm> EffectiveContextSourceBreadcrumbs { get; } = new();

    public bool HasEffectiveContextSourceBreadcrumbs =>
        IsEngineWmlEvaluationEnabled && EffectiveContextSourceBreadcrumbs.Count > 0;

    private void ClearEffectiveContextSourceBreadcrumbs()
    {
        if (EffectiveContextSourceBreadcrumbs.Count == 0)
            return;

        EffectiveContextSourceBreadcrumbs.Clear();
        OnPropertyChanged(nameof(HasEffectiveContextSourceBreadcrumbs));
    }

    private void SetEffectiveContextSourceBreadcrumbs(
        IEnumerable<ScenarioScalarSourceRoute> routes,
        string authoredScenarioWml,
        string? sourcePath)
    {
        if (!IsEngineWmlEvaluationEnabled)
        {
            ClearEffectiveContextSourceBreadcrumbs();
            return;
        }

        EffectiveContextSourceBreadcrumbs.Clear();
        foreach (var route in routes.Where(route => route.EffectiveValue is not null))
        {
            EffectiveContextSourceBreadcrumbs.Add(new ScenarioScalarSourceBreadcrumbVm
            {
                Route = route,
                AuthoredScenarioWml = authoredScenarioWml,
                SourcePath = sourcePath,
                HostGeneration = _exactPreviewGeneration,
                SessionGeneration = _sourceHostGeneration
            });
        }

        OnPropertyChanged(nameof(HasEffectiveContextSourceBreadcrumbs));
    }

    public void SetEvaluationPlan(WmlCampaignEvaluationPlan? plan)
    {
        using var displayRefresh = DeferFieldDisplayRefresh();
        var previousPreviewGeneration = _exactPreviewGeneration;
        InvalidateSourceEditingSession();
        _sourceEvaluationPlan = plan;
        if (WccFieldEditSession.Recovery is null)
        {
            SourceEditingStatus = null;
            OnPropertyChanged(nameof(SourceEditingStatus));
        }

        OnPropertyChanged(nameof(UsesSourceBackedFieldEditing));
        RaiseEditingStateChanged();
        var previousDifficulty = _evaluationContext.DifficultyDefine;
        var contexts = (plan?.Contexts ?? [new WmlEvaluationContext()]).ToArray();
        if (contexts.Length == 0) contexts = [new WmlEvaluationContext()];
        var defaultDefine = plan?.DefaultDifficulty?.Define;
        var selected = contexts.FirstOrDefault(x =>
                           string.Equals(x.DifficultyDefine, previousDifficulty, StringComparison.Ordinal))
                       ?? contexts.FirstOrDefault(x =>
                           string.Equals(x.DifficultyDefine, defaultDefine, StringComparison.Ordinal))
                       ?? contexts[0];
        var wasPublishing = _publishingEvaluationContexts;
        _publishingEvaluationContexts = true;
        try
        {
            EvaluationContexts.Clear();
            foreach (var context in contexts) EvaluationContexts.Add(context);
            // Source refresh during Save may replace every context object while keeping
            // the same semantic selection. Publish the exact current item even while
            // user-initiated difficulty changes remain blocked by the Save barrier.
            _selectedEvaluationContext = selected;
            RunUiSync(() => SetEvaluationContext(selected));
            OnPropertyChanged(nameof(EvaluationContexts));
            OnPropertyChanged(nameof(SelectedEvaluationContext));
        }
        finally { _publishingEvaluationContexts = wasPublishing; }
        OnPropertyChanged(nameof(HasDeclaredEvaluationContexts));
        OnPropertyChanged(nameof(EvaluationContextStatus));
        if (previousPreviewGeneration == _exactPreviewGeneration)
            RefreshMapPreview();
    }

    public void SetEvaluationContext(WmlEvaluationContext? context)
    {
        if (!IsSynchronizingUi && IsWccInlineSaveBusy)
        {
            Status = "Finish saving the current changes before changing difficulty.";
            return;
        }
        context ??= new WmlEvaluationContext();
        if (HasSameEvaluationSemantics(_evaluationContext, context))
            return;
        using var displayRefresh = DeferFieldDisplayRefresh();
        _savedSourcePreviewNavigationGeneration++;
        ClearPendingWccFieldSelection();
        InvalidateSourceEditingSession();
        _evaluationContext = context;
        OnPropertyChanged(nameof(EvaluationContext));
        OnPropertyChanged(nameof(EvaluationContextLabel));
        OnPropertyChanged(nameof(EvaluationContextStatus));
        RefreshMapPreview(reuseStaticSources: true);
    }

    private ScenarioMapPreviewVm? _mapPreview;

    public ScenarioMapPreviewVm? MapPreview
    {
        get => _mapPreview;
        private set
        {
            if (!ReferenceEquals(_mapPreview, value))
            {
                if (!_publishingExactEventWorkspacePreview)
                    _authoredEventWorkspacePreview = null;
                if (_mapPreview is not null)
                    _mapPreview.PropertyChanged -= MapPreviewOnPropertyChanged;
                _mapPreview?.EndCoordinateSelectionForMapChange();
            }

            if (!SetField(ref _mapPreview, value))
                return;
            if (_mapPreview is not null)
                _mapPreview.PropertyChanged += MapPreviewOnPropertyChanged;
            OnPropertyChanged(nameof(EventWorkspacePreview));
            RefreshWccInitialUnitRows();
            OnPropertyChanged(nameof(HasMapPreview));
            OnPropertyChanged(nameof(HasEventWorkspace));
            OnPropertyChanged(nameof(CanPickSideCoordinate));
            RaiseEventEditingStateChanged();
        }
    }

    public bool HasMapPreview => MapPreview?.Map.Rows.Count > 0;
    public bool HasEventWorkspace => MapPreview is not null;

    private string _mapPreviewStatus = "No scenario loaded.";

    public string MapPreviewStatus
    {
        get => _mapPreviewStatus;
        private set => SetField(ref _mapPreviewStatus, value);
    }

    private string? _mapPreviewDiagnostics;

    public string? MapPreviewDiagnostics
    {
        get => _mapPreviewDiagnostics;
        private set => SetField(ref _mapPreviewDiagnostics, value);
    }

    private double _mapPreviewZoom = 0.75;

    public double MapPreviewZoom
    {
        get => _mapPreviewZoom;
        set
        {
            var bounded = Math.Clamp(value, 0.25, 2.0);
            if (!SetField(ref _mapPreviewZoom, bounded))
                return;
            OnPropertyChanged(nameof(MapPreviewZoomPercent));
        }
    }

    public int MapPreviewZoomPercent
    {
        get => (int)Math.Round(MapPreviewZoom * 100, MidpointRounding.AwayFromZero);
        set => MapPreviewZoom = value / 100.0;
    }

    public IReadOnlyList<int> MapPreviewZoomOptions { get; } =
        [25, 50, 75, 100, 125, 150, 200];

    private int _selectedEditorSectionIndex;

    public int SelectedEditorSectionIndex
    {
        get => _selectedEditorSectionIndex;
        set
        {
            var section = Math.Clamp(value, 0, 2);
            if (section == _selectedEditorSectionIndex) return;
            using var displayRefresh = DeferFieldDisplayRefresh();
            if (HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft))
            {
                RunMusicDraftUiViewingSelection(() => base.SetField(ref _selectedEditorSectionIndex, section,
                    nameof(SelectedEditorSectionIndex)));
                return;
            }
            if (SetField(ref _selectedEditorSectionIndex, section) && !IsSynchronizingUi &&
                (IsEngineWmlEvaluationEnabled || !HasCurrentStaticScalarProjection))
                RefreshMapPreview(reuseStaticSources: true);
        }
    }

    public bool IsReadOnly =>
        _isReadOnly || _campaignEntryInspectionOnly && !HasSelectedScenarioSourceBinding || IsBoundedScenarioInspection || _collectedSaveRecoveryPending || WccFieldEditSession.Recovery is not null;

    public bool CanEditDocument => !_campaignEntryInspectionOnly && !IsReadOnly && !HasWccInlineDrafts && !HasWccSideFlagDrafts && !HasWccEventDrafts && !HasWccSideTopologyDrafts && !HasWccLeaderAdditionDrafts && !HasWccInitialUnitDrafts && !HasWccStoryDraft && !IsWccInlineSaveBusy &&
        (!HasWccMusicScheduleDrafts || IsUsingWccMusicScheduleSourceDraft);

    public void SetReadOnly(bool isReadOnly)
    {
        if (_isReadOnly == isReadOnly)
            return;

        InvalidateSourceEditingSession();
        _isReadOnly = isReadOnly;
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEditDocument));
        RaiseEditingStateChanged();
    }

    public bool ScenarioIdIsEditable => IsEditable(SelectedScenario?.Document.Scenario.FieldStates, "id");

    public bool ScenarioNextScenarioIsEditable =>
        IsEditable(SelectedScenario?.Document.Scenario.FieldStates, "next_scenario");

    public bool ScenarioMapFileIsEditable => IsEditable(SelectedScenario?.Document.Scenario.FieldStates, "map_file");
    public bool ScenarioMapDataIsEditable => IsEditable(SelectedScenario?.Document.Scenario.FieldStates, "map_data");

    public bool ScenarioTurnsIsEditable => !UsesSourceBackedFieldEditing &&
                                           IsEditable(SelectedScenario?.Document.Scenario.FieldStates, "turns");

    public bool ScenarioTurnAtIsEditable => IsEditable(SelectedScenario?.Document.Scenario.FieldStates, "turn_at");

    public bool ScenarioRandomStartTimeIsEditable =>
        IsEditable(SelectedScenario?.Document.Scenario.FieldStates, "random_start_time");

    public bool ScenarioMusicIsEditable => IsEditable(SelectedScenario?.Document.Scenario.FieldStates, "music");

    public bool ScenarioVictoryWhenEnemiesDefeatedIsEditable =>
        IsEditable(SelectedScenario?.Document.Scenario.FieldStates, "victory_when_enemies_defeated");

    public bool ScenarioIdHasRawEditor => HasRawEditor(SelectedScenario?.Document.Scenario.FieldStates, "id");

    public bool ScenarioNextScenarioHasRawEditor =>
        HasRawEditor(SelectedScenario?.Document.Scenario.FieldStates, "next_scenario");

    public bool ScenarioMapFileHasRawEditor =>
        HasRawEditor(SelectedScenario?.Document.Scenario.FieldStates, "map_file");

    public bool ScenarioMapDataHasRawEditor =>
        HasRawEditor(SelectedScenario?.Document.Scenario.FieldStates, "map_data");

    public bool ScenarioTurnsHasRawEditor => HasRawEditor(SelectedScenario?.Document.Scenario.FieldStates, "turns");
    public bool ScenarioTurnAtHasRawEditor => HasRawEditor(SelectedScenario?.Document.Scenario.FieldStates, "turn_at");

    public bool ScenarioRandomStartTimeHasRawEditor =>
        HasRawEditor(SelectedScenario?.Document.Scenario.FieldStates, "random_start_time");

    public bool ScenarioMusicHasRawEditor => HasRawEditor(SelectedScenario?.Document.Scenario.FieldStates, "music");

    public bool ScenarioVictoryWhenEnemiesDefeatedHasRawEditor =>
        HasRawEditor(SelectedScenario?.Document.Scenario.FieldStates, "victory_when_enemies_defeated");

    public bool SideNumberHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "side");
    public bool SideXHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "x");
    public bool SideYHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "y");
    public bool SideGoldHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "gold");

    public bool SideControllerHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "controller");

    public bool SideTeamNameHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "team_name");

    public bool SideUserTeamNameHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "user_team_name");

    public bool SideCanRecruitHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "canrecruit");
    public bool SideRecruitHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "recruit");

    public Bitmap? SideLeaderPreviewBitmap
    {
        get => _sideLeaderPreviewBitmap;
        private set => SetField(ref _sideLeaderPreviewBitmap, value);
    }

    public string? SideLeaderPreviewPath
    {
        get => _sideLeaderPreviewPath;
        private set => SetField(ref _sideLeaderPreviewPath, value);
    }

    public string? SideLeaderPreviewStatus
    {
        get => _sideLeaderPreviewStatus;
        private set => SetField(ref _sideLeaderPreviewStatus, value);
    }

    public ObservableCollection<ScenarioDocVm> Scenarios { get; } = new();

    private ScenarioDocVm? _selectedScenario;

    public ScenarioDocVm? SelectedScenario
    {
        get => _selectedScenario;
        set
        {
            if (_selectedScenario == value)
                return;

            var requestedScenarioIndex = value is null ? -1 : Scenarios.IndexOf(value);
            if (!IsSynchronizingUi && (HasWccInlineDrafts || IsWccInlineSaveBusy || HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft)) &&
                (BeforeWccInlineSourceNavigation?.Invoke(() =>
                     SelectedScenario = value is null ? null : Scenarios.ElementAtOrDefault(requestedScenarioIndex)) !=
                 true))
            {
                Status = "Save or discard the inline field drafts before opening another scenario.";
                OnPropertyChanged(nameof(SelectedScenario));
                return;
            }

            if (!IsSynchronizingUi)
            {
                _savedSourcePreviewNavigationGeneration++;
                ClearPendingWccFieldSelection();
            }

            using var displayRefresh = DeferFieldDisplayRefresh();
            InvalidateSourceEditingSession();
            PushCurrentLeaderFieldsIntoModel();
            PushCurrentSideFieldsIntoModel();
            PushCurrentScenarioFieldsIntoModel();

            if (SetField(ref _selectedScenario, value))
                OnSelectedScenarioChanged();
        }
    }

    public ObservableCollection<ScenarioSideVm> Sides { get; } = new();
    public ObservableCollection<ScenarioLeaderVm> Leaders { get; } = new();

    private ScenarioSideVm? _selectedSide;

    public ScenarioSideVm? SelectedSide
    {
        get => _selectedSide;
        set
        {
            if (value is not null && !Sides.Contains(value))
                return;
            if (_selectedSide == value)
                return;

            using var displayRefresh = DeferFieldDisplayRefresh();
            if (HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft))
            {
                RunMusicDraftUiViewingSelection(() =>
                {
                    if (value is not null && !Sides.Contains(value)) return;
                    if (!IsSynchronizingUi)
                    {
                        ClearPendingWccFieldSelection();
                        _pendingGeneratedSideSelection = null;
                        _pendingGeneratedSideSourceSelection = null;
                    }
                    if (!base.SetField(ref _selectedSide, value, nameof(SelectedSide))) return;
                    OnSelectedSideChanged();
                    if (!IsSynchronizingUi && HasCurrentStaticScalarProjection) RefreshSelectedStaticSideSummary();
                });
                return;
            }

            if (!IsSynchronizingUi && IsWccInlineSaveBusy && HasWccInlineDrafts)
            {
                OnPropertyChanged(nameof(SelectedSide));
                return;
            }

            if (!IsSynchronizingUi)
            {
                ClearPendingWccFieldSelection();
                _pendingGeneratedSideSelection = null;
                _pendingGeneratedSideSourceSelection = null;
            }

            InvalidateSourceEditingSession();
            PushCurrentLeaderFieldsIntoModel();
            PushCurrentSideFieldsIntoModel();

            if (SetField(ref _selectedSide, value))
            {
                OnSelectedSideChanged();
                if (!IsSynchronizingUi)
                {
                    if (!IsEngineWmlEvaluationEnabled && HasCurrentStaticScalarProjection)
                        RefreshSelectedStaticSideSummary();
                    else
                        RefreshMapPreview(reuseStaticSources: true);
                }
            }
        }
    }

    private ScenarioLeaderVm? _selectedLeader;

    public ScenarioLeaderVm? SelectedLeader
    {
        get => _selectedLeader;
        set
        {
            if (SelectedSide?.IsGenerated == true && value is not null)
                return;
            if (_selectedLeader == value)
                return;

            using var displayRefresh = DeferFieldDisplayRefresh();
            if (HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft))
            {
                RunMusicDraftUiViewingSelection(() =>
                {
                    if (value is not null && !Leaders.Contains(value)) return;
                    if (!base.SetField(ref _selectedLeader, value, nameof(SelectedLeader))) return;
                    OnSelectedLeaderChanged();
                    if (!IsSynchronizingUi && HasCurrentStaticScalarProjection) RefreshSelectedStaticSideSummary();
                });
                return;
            }

            InvalidateSourceEditingSession();
            PushCurrentLeaderFieldsIntoModel();
            if (SetField(ref _selectedLeader, value))
            {
                OnSelectedLeaderChanged();
                if (!IsSynchronizingUi)
                {
                    if (!IsEngineWmlEvaluationEnabled && HasCurrentStaticScalarProjection)
                        RefreshSelectedStaticSideSummary();
                    else
                        RefreshMapPreview(reuseStaticSources: true);
                }
            }
        }
    }

    private string? _scenarioId;

    public string? ScenarioId
    {
        get => _scenarioId;
        set => SetField(ref _scenarioId, value);
    }

    private string? _scenarioNextScenario;

    public string? ScenarioNextScenario
    {
        get => _scenarioNextScenario;
        set => SetField(ref _scenarioNextScenario, value);
    }

    private string? _scenarioMapFile;

    public string? ScenarioMapFile
    {
        get => _scenarioMapFile;
        set => SetField(ref _scenarioMapFile, value);
    }

    private string? _scenarioMapData;

    public string? ScenarioMapData
    {
        get => _scenarioMapData;
        set => SetField(ref _scenarioMapData, value);
    }

    private string? _scenarioTurns;

    public string? ScenarioTurns
    {
        get => _scenarioTurns;
        set => SetField(ref _scenarioTurns, value);
    }

    private string? _scenarioTurnAt;

    public string? ScenarioTurnAt
    {
        get => _scenarioTurnAt;
        set => SetField(ref _scenarioTurnAt, value);
    }

    private string? _scenarioRandomStartTime;

    public string? ScenarioRandomStartTime
    {
        get => _scenarioRandomStartTime;
        set => SetField(ref _scenarioRandomStartTime, value);
    }

    private string? _scenarioMusic;

    public string? ScenarioMusic
    {
        get => _scenarioMusic;
        set => SetField(ref _scenarioMusic, value);
    }

    private string? _scenarioVictoryWhenEnemiesDefeated;

    public string? ScenarioVictoryWhenEnemiesDefeated
    {
        get => _scenarioVictoryWhenEnemiesDefeated;
        set => SetField(ref _scenarioVictoryWhenEnemiesDefeated, value);
    }

    private string? _sideNumber;

    public string? SideNumber
    {
        get => _sideNumber;
        set => SetField(ref _sideNumber, value);
    }

    private string? _sideX;

    public string? SideX
    {
        get => _sideX;
        set => SetField(ref _sideX, value);
    }

    private string? _sideY;

    public string? SideY
    {
        get => _sideY;
        set => SetField(ref _sideY, value);
    }

    private string? _sideGold;

    public string? SideGold
    {
        get => _sideGold;
        set => SetField(ref _sideGold, value);
    }

    private string? _sideController;

    public string? SideController
    {
        get => _sideController;
        set => SetField(ref _sideController, value);
    }

    private string? _sideTeamName;

    public string? SideTeamName
    {
        get => _sideTeamName;
        set => SetField(ref _sideTeamName, value);
    }

    private string? _sideUserTeamName;

    public string? SideUserTeamName
    {
        get => _sideUserTeamName;
        set => SetField(ref _sideUserTeamName, value);
    }

    private string? _sideCanRecruit;

    public string? SideCanRecruit
    {
        get => _sideCanRecruit;
        set => SetField(ref _sideCanRecruit, value);
    }

    private string? _sideType;

    public string? SideType
    {
        get => _sideType;
        set
        {
            if (!SetField(ref _sideType, value))
                return;

            RefreshSideLeaderPreview();
        }
    }

    private string? _sideId;

    public string? SideId
    {
        get => _sideId;
        set => SetField(ref _sideId, value);
    }

    private string? _sideName;

    public string? SideName
    {
        get => _sideName;
        set => SetField(ref _sideName, value);
    }

    private string? _sideRecruit;

    public string? SideRecruit
    {
        get => _sideRecruit;
        set => SetField(ref _sideRecruit, value);
    }

    private string? _sideDisplayName;

    public string? SideDisplayName
    {
        get => _sideDisplayName;
        set => SetField(ref _sideDisplayName, value);
    }

    private string? _sideColor;

    public string? SideColor
    {
        get => _sideColor;
        set => SetField(ref _sideColor, value);
    }

    private string? _sideIncome;

    public string? SideIncome
    {
        get => _sideIncome;
        set => SetField(ref _sideIncome, value);
    }

    private string? _sideVillageGold;

    public string? SideVillageGold
    {
        get => _sideVillageGold;
        set => SetField(ref _sideVillageGold, value);
    }

    private string? _sideVillageSupport;

    public string? SideVillageSupport
    {
        get => _sideVillageSupport;
        set => SetField(ref _sideVillageSupport, value);
    }

    private string? _sideRecallCost;

    public string? SideRecallCost
    {
        get => _sideRecallCost;
        set => SetField(ref _sideRecallCost, value);
    }

    private string? _sidePersistent;

    public string? SidePersistent
    {
        get => _sidePersistent;
        set => SetField(ref _sidePersistent, value);
    }

    private string? _sideSaveId;

    public string? SideSaveId
    {
        get => _sideSaveId;
        set => SetField(ref _sideSaveId, value);
    }

    private string? _sideFog;

    public string? SideFog
    {
        get => _sideFog;
        set => SetField(ref _sideFog, value);
    }

    private string? _sideShroud;

    public string? SideShroud
    {
        get => _sideShroud;
        set => SetField(ref _sideShroud, value);
    }

    private string? _sideShareVision;

    public string? SideShareVision
    {
        get => _sideShareVision;
        set => SetField(ref _sideShareVision, value);
    }

    private string? _sideHidden;

    public string? SideHidden
    {
        get => _sideHidden;
        set => SetField(ref _sideHidden, value);
    }

    private string? _sideScrollToLeader;

    public string? SideScrollToLeader
    {
        get => _sideScrollToLeader;
        set => SetField(ref _sideScrollToLeader, value);
    }

    private string? _sideDefeatCondition;

    public string? SideDefeatCondition
    {
        get => _sideDefeatCondition;
        set => SetField(ref _sideDefeatCondition, value);
    }

    private string? _sideNoLeader;

    public string? SideNoLeader
    {
        get => _sideNoLeader;
        set => SetField(ref _sideNoLeader, value);
    }

    private string? _sideRecruitmentPattern;

    public string? SideRecruitmentPattern
    {
        get => _sideRecruitmentPattern;
        set => SetField(ref _sideRecruitmentPattern, value);
    }

    private string? _leaderGender;

    public string? LeaderGender
    {
        get => _leaderGender;
        set => SetField(ref _leaderGender, value);
    }

    private string? _leaderVariation;

    public string? LeaderVariation
    {
        get => _leaderVariation;
        set => SetField(ref _leaderVariation, value);
    }

    private string? _leaderFacing;

    public string? LeaderFacing
    {
        get => _leaderFacing;
        set => SetField(ref _leaderFacing, value);
    }

    private string? _leaderUnrenamable;

    public string? LeaderUnrenamable
    {
        get => _leaderUnrenamable;
        set => SetField(ref _leaderUnrenamable, value);
    }

    private string? _leaderRandomTraits;

    public string? LeaderRandomTraits
    {
        get => _leaderRandomTraits;
        set => SetField(ref _leaderRandomTraits, value);
    }

    private string? _leaderRandomGender;

    public string? LeaderRandomGender
    {
        get => _leaderRandomGender;
        set => SetField(ref _leaderRandomGender, value);
    }

    private string? _leaderPlacement;

    public string? LeaderPlacement
    {
        get => _leaderPlacement;
        set => SetField(ref _leaderPlacement, value);
    }

    private string? _leaderLocationId;

    public string? LeaderLocationId
    {
        get => _leaderLocationId;
        set => SetField(ref _leaderLocationId, value);
    }

    private string? _leaderExtraRecruit;

    public string? LeaderExtraRecruit
    {
        get => _leaderExtraRecruit;
        set => SetField(ref _leaderExtraRecruit, value);
    }

    public string? GetScenarioFieldReason(string key)
        => GetReason(SelectedScenario?.Document.Scenario.FieldStates, key);

    public string? GetSideFieldReason(string key)
        => ScenarioLeaderSchema.KnownKeys.Contains(key)
            ? GetReason(SelectedLeader?.Leader.FieldStates, key)
            : GetReason(SelectedSide?.AuthoredDocument?.Side.FieldStates, key);

    private bool IsSelectedLeaderFieldEditable(string key)
        => SelectedLeader?.CanEditTyped == true &&
           IsEditable(SelectedLeader.Leader.FieldStates, key);

    private bool IsSelectedSideFieldEditable(string key)
        => SelectedSide?.AuthoredDocument?.CanEditTyped == true &&
           IsEditable(SelectedSide.Side.FieldStates, key);

    public bool SideNumberIsEditable => IsSelectedSideFieldEditable("side");
    public bool SideXIsEditable => IsSelectedLeaderFieldEditable("x");
    public bool SideYIsEditable => IsSelectedLeaderFieldEditable("y");
    public bool SideGoldIsEditable => !UsesSourceBackedFieldEditing && IsSelectedSideFieldEditable("gold");
    public bool SideControllerIsEditable => IsSelectedSideFieldEditable("controller");
    public bool SideTeamNameIsEditable => IsSelectedSideFieldEditable("team_name");
    public bool SideUserTeamNameIsEditable => IsSelectedSideFieldEditable("user_team_name");
    public bool SideCanRecruitIsEditable => IsSelectedLeaderFieldEditable("canrecruit");
    public bool SideRecruitIsEditable => IsSelectedSideFieldEditable("recruit");
    public bool SideTypeIsEditable => IsSelectedLeaderFieldEditable("type");
    public bool SideIdIsEditable => IsSelectedLeaderFieldEditable("id");
    public bool SideNameIsEditable => IsSelectedLeaderFieldEditable("name");
    public bool SideDisplayNameIsEditable => IsSelectedSideFieldEditable("side_name");
    public bool SideColorIsEditable => IsSelectedSideFieldEditable("color");
    public bool SideIncomeIsEditable => !UsesSourceBackedFieldEditing && IsSelectedSideFieldEditable("income");
    public bool SideVillageGoldIsEditable => IsSelectedSideFieldEditable("village_gold");
    public bool SideVillageSupportIsEditable => IsSelectedSideFieldEditable("village_support");
    public bool SideRecallCostIsEditable => IsSelectedSideFieldEditable("recall_cost");
    public bool SidePersistentIsEditable => IsSelectedSideFieldEditable("persistent");
    public bool SideSaveIdIsEditable => IsSelectedSideFieldEditable("save_id");
    public bool SideFogIsEditable => IsSelectedSideFieldEditable("fog");
    public bool SideShroudIsEditable => IsSelectedSideFieldEditable("shroud");
    public bool SideShareVisionIsEditable => IsSelectedSideFieldEditable("share_vision");
    public bool SideHiddenIsEditable => IsSelectedSideFieldEditable("hidden");
    public bool SideScrollToLeaderIsEditable => IsSelectedSideFieldEditable("scroll_to_leader");
    public bool SideDefeatConditionIsEditable => IsSelectedSideFieldEditable("defeat_condition");
    public bool SideNoLeaderIsEditable => IsSelectedSideFieldEditable("no_leader");
    public bool SideRecruitmentPatternIsEditable => IsSelectedSideFieldEditable("recruitment_pattern");
    public bool LeaderGenderIsEditable => IsSelectedLeaderFieldEditable("gender");
    public bool LeaderVariationIsEditable => IsSelectedLeaderFieldEditable("variation");
    public bool LeaderFacingIsEditable => IsSelectedLeaderFieldEditable("facing");
    public bool LeaderUnrenamableIsEditable => IsSelectedLeaderFieldEditable("unrenamable");
    public bool LeaderRandomTraitsIsEditable => IsSelectedLeaderFieldEditable("random_traits");
    public bool LeaderRandomGenderIsEditable => IsSelectedLeaderFieldEditable("random_gender");
    public bool LeaderPlacementIsEditable => IsSelectedLeaderFieldEditable("placement");
    public bool LeaderLocationIdIsEditable => IsSelectedLeaderFieldEditable("location_id");
    public bool LeaderExtraRecruitIsEditable => IsSelectedLeaderFieldEditable("extra_recruit");

    public bool CanPickSideCoordinate =>
        HasMapPreview && SelectedLeader is not null && SideXIsEditable && SideYIsEditable;

    public bool HasSelectedMapEvent => MapPreview?.SelectedEvent is not null;
    public bool CanAddRawEvent => CanStageWccEvents;

    public bool CanEditSelectedEventRaw =>
        MapPreview?.SelectedEvent is { } entry && BeginWccEventDraft(entry)?.CanEdit == true;

    public string SelectedEventRawActionLabel =>
        CanEditSelectedEventRaw ? "Edit raw WML..." : "View raw WML...";

    public string? SelectedEventEditabilityText => MapPreview?.SelectedEvent is not { } entry
        ? null
        : CanEditSelectedEventRaw
            ? "This literal direct event maps exactly to authoritative scenario WML."
            : IsReadOnly
                ? "This scenario is read-only."
                : entry.EditabilityReason;

    public bool SideTypeHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "type");
    public bool SideIdHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "id");
    public bool SideNameHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "name");

    public bool SideDisplayNameHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "side_name");

    public bool SideColorHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "color");
    public bool SideIncomeHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "income");

    public bool SideVillageGoldHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "village_gold");

    public bool SideVillageSupportHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "village_support");

    public bool SideRecallCostHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "recall_cost");

    public bool SidePersistentHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "persistent");

    public bool SideSaveIdHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "save_id");
    public bool SideFogHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "fog");
    public bool SideShroudHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "shroud");

    public bool SideShareVisionHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "share_vision");

    public bool SideHiddenHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "hidden");

    public bool SideScrollToLeaderHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "scroll_to_leader");

    public bool SideDefeatConditionHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "defeat_condition");

    public bool SideNoLeaderHasRawEditor => HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "no_leader");

    public bool SideRecruitmentPatternHasRawEditor =>
        HasRawEditor(SelectedSide?.AuthoredDocument?.Side.FieldStates, "recruitment_pattern");

    public bool LeaderGenderHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "gender");
    public bool LeaderVariationHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "variation");
    public bool LeaderFacingHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "facing");
    public bool LeaderUnrenamableHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "unrenamable");
    public bool LeaderRandomTraitsHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "random_traits");
    public bool LeaderRandomGenderHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "random_gender");
    public bool LeaderPlacementHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "placement");
    public bool LeaderLocationIdHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "location_id");
    public bool LeaderExtraRecruitHasRawEditor => HasRawEditor(SelectedLeader?.Leader.FieldStates, "extra_recruit");

    public string? ScenarioConditionalInfo
        => WmlFieldSafetySummaryBuilder.Build(
            SelectedScenario?.Document.Scenario.FieldStates,
            "Scenario source");

    public string? SideConditionalInfo
    {
        get
        {
            if (SelectedSide is null)
                return null;

            if (SelectedSide.IsGenerated)
                return
                    "This side is generated by source expansion. Its resolved values and source invocation are read-only; local override editing is not available yet.";

            if (SelectedSide.Document.IsConditional)
                return
                    "This [side] is inside conditional source. Typed fields are read-only; the exact side block can still be reviewed and edited as raw WML.";

            return WmlFieldSafetySummaryBuilder.Build(
                SelectedSide.Side.FieldStates,
                "Side source");
        }
    }

    public string? LeaderConditionalInfo
    {
        get
        {
            if (SelectedLeader is null)
                return null;
            if (SelectedLeader.Document.IsConditional)
                return
                    "This [leader] is inside conditional source. Typed fields are read-only; use the raw leader editor for an explicit source change.";
            return WmlFieldSafetySummaryBuilder.Build(
                SelectedLeader.Leader.FieldStates,
                "Leader source");
        }
    }

    public bool HasSelectedLeader => SelectedLeader is not null;
    public bool CanAddLeader => UsesCheckedLeaderAuthoring ? CanStageWccLeaderAddition
        : CanEditDocument && SelectedSide?.AuthoredDocument?.CanEditTyped == true;

    public bool CanRemoveSelectedSide =>
        CanEditDocument && SelectedSide?.AuthoredDocument?.IsConditional == false;

    public bool CanRemoveSelectedLeader => SelectedWccInitialLeader is { } initial && IsWccPendingLeaderOwner(initial.Owner)
        ? !IsWccInlineSaveBusy && !_changingWccDifficultyDrafts
        : !UsesCheckedLeaderAuthoring && CanEditDocument && SelectedSide?.AuthoredDocument is not null && SelectedLeader?.CanRemove == true;

    public bool CanMoveSelectedLeaderUp => CanMoveSelectedLeader(-1);
    public bool CanMoveSelectedLeaderDown => CanMoveSelectedLeader(1);
    public string? SelectedLeaderSourceInfo => SelectedLeader?.SourceLabel;

    public bool CanEditSelectedSideRaw =>
        CanEditDocument && SelectedSide?.AuthoredDocument is not null;

    public bool HasSelectedSide => SelectedSide is not null;

    public string SelectedSideRawActionLabel =>
        SelectedSide?.IsGenerated == true ? "View resolved side..." :
        CanEditSelectedSideRaw ? "Edit raw side..." : "View raw side...";

    public string? GetSelectedSideRawWml()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (SelectedScenario is null || SelectedSide is null)
                return null;
            if (SelectedSide.IsGenerated)
                return SelectedStaticSideOwner is { } owner ? RenderResolvedSideAttributes(owner) : null;
            if (HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft))
                return SelectedSide.Document.EditableBlock.ToRawText();
            InvalidateSourceEditingSession();
            PushCurrentLeaderFieldsIntoModel();
            PushCurrentSideFieldsIntoModel();
            ScenarioFileRenderer.RenderUpdatedScenarioBlock(SelectedScenario.Document);
            return SelectedSide.Document.EditableBlock.ToRawText();
        }
    }

    public string? ValidateSelectedSideRawWml(string rawWml)
    {
        if (!CanEditSelectedSideRaw)
            return "This side source is read-only.";
        var validation = ScenarioSideEditor.ValidateRawSide(rawWml);
        return validation.IsValid ? null : validation.Problem;
    }

    public void ApplySelectedSideRawWml(string rawWml)
    {
        if (!CanEditSelectedSideRaw || SelectedScenario is null || SelectedSide is null)
            return;
        var index = GetAuthoredSideIndex(SelectedSide);
        var displayIndex = Sides.IndexOf(SelectedSide);
        if (index < 0 || displayIndex < 0)
            return;
        InvalidateSourceEditingSession();
        var replacement = ScenarioSideEditor.ReplaceSideRaw(
            SelectedScenario.Document,
            index,
            rawWml);
        var replacementVm = new ScenarioSideVm(replacement);
        RunUiSync(() =>
        {
            Sides[displayIndex] = replacementVm;
            SelectedSide = replacementVm;
        });
        Status = "Saved raw side WML.";
        NotifyDocumentChanged();
        RefreshMapPreview();
    }

    public bool CanEditSelectedLeaderRaw =>
        CanEditDocument && SelectedSide?.AuthoredDocument is not null && SelectedLeader is { IsLegacyInline: false };

    public string SelectedLeaderRawActionLabel =>
        CanEditSelectedLeaderRaw ? "Edit raw leader..." : "View raw source...";

    public string? GetSelectedLeaderRawWml()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (SelectedScenario is null || SelectedLeader is null)
                return null;
            if (HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft))
                return SelectedLeader.Document.EditableBlock.ToRawText();
            InvalidateSourceEditingSession();
            PushCurrentLeaderFieldsIntoModel();
            PushCurrentSideFieldsIntoModel();
            ScenarioFileRenderer.RenderUpdatedScenarioBlock(SelectedScenario.Document);
            return SelectedLeader.Document.EditableBlock.ToRawText();
        }
    }

    public string? ValidateSelectedLeaderRawWml(string rawWml)
    {
        if (!CanEditSelectedLeaderRaw)
            return "This leader source is read-only.";
        var validation = ScenarioLeaderEditor.ValidateRawLeader(rawWml);
        return validation.IsValid ? null : validation.Problem;
    }

    public void ApplySelectedLeaderRawWml(string rawWml)
    {
        if (!CanEditSelectedLeaderRaw || SelectedSide is null || SelectedLeader is null)
            return;
        var nestedIndex = SelectedSide.Document.NestedLeaderDocuments
            .ToList()
            .IndexOf(SelectedLeader.Document);
        if (nestedIndex < 0)
            return;
        InvalidateSourceEditingSession();
        var replacement = ScenarioLeaderEditor.ReplaceLiteralLeader(
            SelectedSide.Document,
            nestedIndex,
            rawWml);
        RebuildLeaderList(replacement.InternalId);
        Status = "Saved raw nested leader WML.";
        NotifyDocumentChanged();
        RefreshMapPreview();
    }

    private bool CanMoveSelectedLeader(int delta)
    {
        if (UsesCheckedLeaderAuthoring) return false;
        if (!CanEditDocument || SelectedSide?.AuthoredDocument is null ||
            SelectedLeader is not { IsLegacyInline: false })
            return false;
        var nested = SelectedSide.Document.NestedLeaderDocuments;
        if (nested.Any(x => x.IsConditional))
            return false;
        var index = nested.ToList().IndexOf(SelectedLeader.Document);
        var target = index + delta;
        return index >= 0 && target >= 0 && target < nested.Count;
    }

    public void AddLeader()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (UsesCheckedLeaderAuthoring) { StageWccLeaderAddition(); return; }
            if (!CanAddLeader || SelectedSide is null)
                return;
            InvalidateSourceEditingSession();
            PushCurrentLeaderFieldsIntoModel();
            PushCurrentSideFieldsIntoModel();

            var nested = SelectedSide.Document.NestedLeaderDocuments;
            var insertIndex = SelectedLeader is { IsLegacyInline: false }
                ? Math.Max(0, nested.ToList().IndexOf(SelectedLeader.Document) + 1)
                : nested.Count;
            var document = ScenarioLeaderEditor.InsertNewLeader(
                SelectedSide.Document,
                insertIndex);
            RebuildLeaderList(document.InternalId);
            Status = "Added a nested [leader].";
            NotifyDocumentChanged();
            RefreshMapPreview();
        }
    }

    public void RemoveSelectedLeader()
    {
        if (SelectedWccInitialLeader is { } initial && FindPendingLeader(initial.Owner) is { } pending)
        { RevertWccLeaderAdditionDraft(pending.Identity); return; }
        if (!CanRemoveSelectedLeader || SelectedSide is null || SelectedLeader is null)
            return;
        var nested = SelectedSide.Document.NestedLeaderDocuments;
        var nestedIndex = nested.ToList().IndexOf(SelectedLeader.Document);
        if (nestedIndex < 0)
            return;
        InvalidateSourceEditingSession();
        ScenarioLeaderEditor.RemoveLeaderAt(SelectedSide.Document, nestedIndex);
        RebuildLeaderList(null, Math.Min(nestedIndex, Math.Max(0, Leaders.Count - 2)));
        Status = "Removed nested [leader].";
        NotifyDocumentChanged();
        RefreshMapPreview();
    }

    public void MoveSelectedLeader(int delta)
    {
        if (SelectedSide is null || SelectedLeader is null || !CanMoveSelectedLeader(delta))
            return;
        InvalidateSourceEditingSession();
        PushCurrentLeaderFieldsIntoModel();
        var nested = SelectedSide.Document.NestedLeaderDocuments;
        var from = nested.ToList().IndexOf(SelectedLeader.Document);
        var to = from + delta;
        var id = SelectedLeader.Document.InternalId;
        ScenarioLeaderEditor.MoveLeader(SelectedSide.Document, from, to);
        RebuildLeaderList(id);
        Status = "Reordered nested [leader].";
        NotifyDocumentChanged();
        RefreshMapPreview();
    }

    private void RebuildLeaderList(string? selectedInternalId, int fallbackNestedIndex = 0)
    {
        RunUiSync(() =>
        {
            Leaders.Clear();
            if (SelectedSide?.AuthoredDocument is null)
            {
                SelectedLeader = null;
                return;
            }

            foreach (var document in SelectedSide.Document.LeaderDocuments)
                Leaders.Add(new ScenarioLeaderVm(document));
            SelectedLeader = Leaders.FirstOrDefault(x =>
                string.Equals(x.Document.InternalId, selectedInternalId, StringComparison.Ordinal));
            if (SelectedLeader is null)
            {
                var nestedLeaders = Leaders.Where(x => !x.IsLegacyInline).ToArray();
                SelectedLeader = nestedLeaders.ElementAtOrDefault(fallbackNestedIndex)
                                 ?? Leaders.FirstOrDefault();
            }
        });
        RaiseLeaderEditingStateChanged();
    }

    public void LoadFromText(string path, string text)
    {
        using var displayRefresh = DeferFieldDisplayRefresh();
        InvalidateSourceEditingSession();
        ResetLoadedSourceTracking(text);
        _loadedAssetInternalId = null;
        _currentScenarioAsset = null;
        _currentSourcePath = path;
        OnPropertyChanged(nameof(UsesSourceBackedFieldEditing));
        _currentSourceMacroDefinitions.Clear();
        _currentSourceMacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(text));
        RefreshMacroDefinitionResolver();
        LoadedPath = path;
        Status = null;

        Scenarios.Clear();
        Sides.Clear();

        var docs = ScenarioImporter.ImportAllFromText(text);
        foreach (var d in docs)
            Scenarios.Add(new ScenarioDocVm(d));

        SelectedScenario = Scenarios.Count > 0 ? Scenarios[0] : null;

        Status = $"Loaded {Scenarios.Count} scenario(s).";
    }

    public void LoadFromExistingScenarioAsset(ScenarioFileAsset asset) =>
        LoadFromExistingScenarioAsset(asset, null);

    /// <summary>Loads an exact authored owner before starting its display refresh.</summary>
    public void LoadFromExistingScenarioAsset(ScenarioFileAsset asset, string? scenarioDocumentInternalId)
    {
        // Static preview publication uses this same gate. A shared file reload
        // must install its owner and side rows before a preview can replace them.
        lock (_wccInitialFieldRefreshGate)
        {
            using var displayRefresh = DeferFieldDisplayRefresh();
            InvalidateSourceEditingSession();
            ResetLoadedSourceTracking(
                asset.RawTextSnapshot ?? asset.Documents.FirstOrDefault()?.OriginalText);
            _loadedAssetInternalId = asset.InternalId;
            _currentScenarioAsset = asset;
            _currentSourcePath = asset.SourcePath;
            OnPropertyChanged(nameof(UsesSourceBackedFieldEditing));
            _currentSourceMacroDefinitions.Clear();
            _currentSourceMacroDefinitions.AddRange(asset.MacroDefinitions);
            RefreshMacroDefinitionResolver();
            LoadedPath = asset.SourcePath ?? asset.FileName;
            Status = "Loaded shared scenario file from campaign project.";

            Scenarios.Clear();
            Sides.Clear();

            foreach (var d in asset.Documents)
                Scenarios.Add(new ScenarioDocVm(d));

            SelectedScenario = scenarioDocumentInternalId is null ? Scenarios.FirstOrDefault() :
                Scenarios.FirstOrDefault(item => item.Document.InternalId == scenarioDocumentInternalId);
        }
    }

    public bool IsShowingAsset(ScenarioFileAsset asset)
    {
        if (asset is null) throw new ArgumentNullException(nameof(asset));
        return string.Equals(_loadedAssetInternalId, asset.InternalId, StringComparison.Ordinal);
    }

    public ScenarioFileAsset? CurrentScenarioAsset => _currentScenarioAsset;

    public string? RenderPatchedFile()
    {
        // The renderer patches editable source blocks in place. Background
        // content refreshes render those same blocks, so the entire operation
        // shares their publication gate, including model pushes and selection.
        lock (_wccInitialFieldRefreshGate)
        {
            if (HasWccInlineDrafts || IsWccInlineSaveBusy || HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft))
            {
                Status = "Save or discard pending field, music, schedule, flag or event drafts before rendering or exporting the source.";
                return null;
            }

            if (Scenarios.Count == 0)
                return null;

            InvalidateSourceEditingSession();
            PushCurrentLeaderFieldsIntoModel();
            PushCurrentSideFieldsIntoModel();
            PushCurrentScenarioFieldsIntoModel();

            var docs = Scenarios
                .Select(s => s.Document)
                .ToList();

            return ScenarioFileRenderer.RenderPatchedFile(docs);
        }
    }

    public WccWorkingCopyTextSaveResult SaveCurrentWorkingCopyFile(string projectRoot)
    {
        if (!CanEditDocument || WccFieldEditSession.IsBusy)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                _currentScenarioAsset?.SourcePath,
                WccFieldEditSession.Recovery is not null
                    ? "Scenario persistence requires recovery before another in-place save."
                    : IsReadOnly
                        ? "This scenario is open for read-only inspection."
                        : "Finish the current field operation before saving other scenario changes.");
        }

        InvalidateSourceEditingSession();
        if (_currentScenarioAsset is null)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                ErrorMessage: "No campaign scenario file is open.");
        }

        string? patched;
        try
        {
            patched = RenderPatchedFile();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.RenderFailure,
                _currentScenarioAsset.SourcePath,
                ex.Message);
        }

        if (patched is null)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                _currentScenarioAsset.SourcePath,
                "The current campaign scenario file could not be rendered.");
        }

        var result = WccWorkingCopyScenarioFileSaver.Save(
            projectRoot,
            _currentScenarioAsset,
            patched);
        if (result.IsSuccess)
        {
            // The guarded saver has made this exact text the asset's new
            // optimistic-concurrency baseline. A stale package index still rejects
            // it until the normal post-save index refresh completes.
            ReloadSavedScenarioSource(_currentScenarioAsset, _currentScenarioAsset.RawTextSnapshot ?? patched,
                SelectedScenario is null ? 0 : Scenarios.IndexOf(SelectedScenario),
                GetAuthoredSideIndex(SelectedSide));
        }

        return result;
    }

    private void ResetLoadedSourceTracking(string? sourceText)
    {
        InvalidateSourceEditingSession();
        _pristineLoadedSourceText = sourceText;
        _hasAuthoredSourceChanges = false;
    }

    private string SelectSourceForExactMatching(string renderedSource)
        => !_hasAuthoredSourceChanges && _pristineLoadedSourceText is not null
            ? _pristineLoadedSourceText
            : renderedSource;

    public void AddSide()
    {
        if (!CanAddSide || SelectedScenario is null)
            return;

        InvalidateSourceEditingSession();
        PushCurrentLeaderFieldsIntoModel();
        PushCurrentSideFieldsIntoModel();
        PushCurrentScenarioFieldsIntoModel();

        var scenarioDoc = SelectedScenario.Document;
        int insertIndex = SelectedSide is null
            ? scenarioDoc.SideDocuments.Count
            : GetAuthoredSideIndex(SelectedSide) + 1;
        int displayIndex = SelectedSide is null ? Sides.Count : Sides.IndexOf(SelectedSide) + 1;

        var newSideDoc = ScenarioSideEditor.InsertNewSide(scenarioDoc, insertIndex);

        var vm = new ScenarioSideVm(newSideDoc);
        Sides.Insert(displayIndex, vm);
        SelectedSide = vm;

        Status = "Added side.";
        NotifyDocumentChanged();
    }

    public void RemoveSelectedSide()
    {
        if (!CanRemoveSelectedSide || SelectedScenario is null || SelectedSide is null)
            return;

        int index = GetAuthoredSideIndex(SelectedSide);
        int displayIndex = Sides.IndexOf(SelectedSide);
        if (index < 0 || displayIndex < 0)
            return;

        InvalidateSourceEditingSession();
        ScenarioSideEditor.RemoveSideAt(SelectedScenario.Document, index);
        Sides.RemoveAt(displayIndex);

        SelectedSide = displayIndex < Sides.Count ? Sides[displayIndex] : Sides.LastOrDefault();
        Status = "Removed side.";
        NotifyDocumentChanged();
    }

    public ScenarioStoryDocument? EnsureEditableIntroStory()
    {
        if (!CanEditDocument || SelectedScenario is null)
            return null;

        InvalidateSourceEditingSession();
        PushCurrentLeaderFieldsIntoModel();
        PushCurrentSideFieldsIntoModel();
        PushCurrentScenarioFieldsIntoModel();

        var storyCountBefore = SelectedScenario.Document.StoryDocuments.Count;
        var doc = ScenarioStoryContainerEditor.EnsureFirstStory(SelectedScenario.Document);

        if (SelectedScenario.Document.StoryDocuments.Count != storyCountBefore)
            NotifyDocumentChanged();

        if (SelectedScenario.Document.StoryDocuments.Count > 1)
            Status = "Editing the first [story] block only.";
        else
            Status = "Intro story ready.";

        return doc;
    }

    private void OnSelectedScenarioChanged()
    {
        RunUiSync(OnSelectedScenarioChangedCore);
    }

    private void OnSelectedScenarioChangedCore()
    {
        RaiseWccScenarioContentChanged();
        Sides.Clear();
        Leaders.Clear();
        SelectedLeader = null;
        SelectedSide = null;

        ScenarioId = null;
        ScenarioNextScenario = null;
        ScenarioMapFile = null;
        ScenarioMapData = null;
        ScenarioTurns = null;
        ScenarioTurnAt = null;
        ScenarioRandomStartTime = null;
        ScenarioMusic = null;
        ScenarioVictoryWhenEnemiesDefeated = null;

        SideNumber = null;
        SideX = null;
        SideY = null;
        SideGold = null;
        SideController = null;
        SideTeamName = null;
        SideUserTeamName = null;
        SideCanRecruit = null;
        SideRecruit = null;
        SideType = null;
        SideId = null;
        SideName = null;

        if (SelectedScenario is null)
        {
            RefreshMapPreview();
            OnPropertyChanged(nameof(ScenarioIdIsEditable));
            OnPropertyChanged(nameof(ScenarioNextScenarioIsEditable));
            OnPropertyChanged(nameof(ScenarioMapFileIsEditable));
            OnPropertyChanged(nameof(ScenarioMapDataIsEditable));
            OnPropertyChanged(nameof(ScenarioTurnsIsEditable));
            OnPropertyChanged(nameof(ScenarioTurnAtIsEditable));
            OnPropertyChanged(nameof(ScenarioRandomStartTimeIsEditable));
            OnPropertyChanged(nameof(ScenarioMusicIsEditable));
            OnPropertyChanged(nameof(ScenarioVictoryWhenEnemiesDefeatedIsEditable));
            OnPropertyChanged(nameof(ScenarioConditionalInfo));
            return;
        }

        var s = SelectedScenario.Document.Scenario;

        ScenarioId = s.Id;
        ScenarioNextScenario = s.NextScenario;
        ScenarioMapFile = s.MapFile;
        ScenarioMapData = s.MapData;
        ScenarioTurns = s.Turns?.ToString(CultureInfo.InvariantCulture);
        ScenarioTurnAt = s.TurnAt?.ToString(CultureInfo.InvariantCulture);
        ScenarioRandomStartTime = s.RandomStartTime;
        ScenarioMusic = s.Music;
        ScenarioVictoryWhenEnemiesDefeated = s.VictoryWhenEnemiesDefeated;

        foreach (var sideDocument in SelectedScenario.Document.SideDocuments)
            Sides.Add(new ScenarioSideVm(sideDocument));

        SelectedSide = Sides.Count > 0 ? Sides[0] : null;

        OnPropertyChanged(nameof(ScenarioIdIsEditable));
        OnPropertyChanged(nameof(ScenarioNextScenarioIsEditable));
        OnPropertyChanged(nameof(ScenarioMapFileIsEditable));
        OnPropertyChanged(nameof(ScenarioMapDataIsEditable));
        OnPropertyChanged(nameof(ScenarioTurnsIsEditable));
        OnPropertyChanged(nameof(ScenarioTurnAtIsEditable));
        OnPropertyChanged(nameof(ScenarioRandomStartTimeIsEditable));
        OnPropertyChanged(nameof(ScenarioMusicIsEditable));
        OnPropertyChanged(nameof(ScenarioVictoryWhenEnemiesDefeatedIsEditable));
        OnPropertyChanged(nameof(ScenarioConditionalInfo));
        RefreshMapPreview();

        OnPropertyChanged(nameof(ScenarioIdHasRawEditor));
        OnPropertyChanged(nameof(ScenarioNextScenarioHasRawEditor));
        OnPropertyChanged(nameof(ScenarioMapFileHasRawEditor));
        OnPropertyChanged(nameof(ScenarioMapDataHasRawEditor));
        OnPropertyChanged(nameof(ScenarioTurnsHasRawEditor));
        OnPropertyChanged(nameof(ScenarioTurnAtHasRawEditor));
        OnPropertyChanged(nameof(ScenarioRandomStartTimeHasRawEditor));
        OnPropertyChanged(nameof(ScenarioMusicHasRawEditor));
        OnPropertyChanged(nameof(ScenarioVictoryWhenEnemiesDefeatedHasRawEditor));
    }

    private void OnSelectedSideChanged()
    {
        RunUiSync(OnSelectedSideChangedCore);
        OnPropertyChanged(nameof(CanPickSideCoordinate));
    }

    private void OnSelectedSideChangedCore()
    {
        Leaders.Clear();
        SelectedLeader = null;
        SideNumber = null;
        SideGold = null;
        SideController = null;
        SideTeamName = null;
        SideUserTeamName = null;
        SideRecruit = null;
        SideDisplayName = null;
        SideColor = null;
        SideIncome = null;
        SideVillageGold = null;
        SideVillageSupport = null;
        SideRecallCost = null;
        SidePersistent = null;
        SideSaveId = null;
        SideFog = null;
        SideShroud = null;
        SideShareVision = null;
        SideHidden = null;
        SideScrollToLeader = null;
        SideDefeatCondition = null;
        SideNoLeader = null;
        SideRecruitmentPattern = null;

        if (SelectedSide?.AuthoredDocument is null)
        {
            OnPropertyChanged(nameof(SideNumberIsEditable));
            OnPropertyChanged(nameof(SideXIsEditable));
            OnPropertyChanged(nameof(SideYIsEditable));
            OnPropertyChanged(nameof(SideGoldIsEditable));
            OnPropertyChanged(nameof(SideControllerIsEditable));
            OnPropertyChanged(nameof(SideTeamNameIsEditable));
            OnPropertyChanged(nameof(SideUserTeamNameIsEditable));
            OnPropertyChanged(nameof(SideCanRecruitIsEditable));
            OnPropertyChanged(nameof(SideRecruitIsEditable));
            OnPropertyChanged(nameof(SideConditionalInfo));
            RaiseEditingStateChanged();
            return;
        }

        var s = SelectedSide.Side;

        SideNumber = s.SideNumber?.ToString(CultureInfo.InvariantCulture);
        SideGold = s.Gold?.ToString(CultureInfo.InvariantCulture);
        SideController = s.Controller;
        SideTeamName = s.TeamName;
        SideUserTeamName = s.UserTeamName;
        SideRecruit = s.Recruit;
        SideDisplayName = s.SideNameWml;
        SideColor = s.Color;
        SideIncome = s.Income?.ToString(CultureInfo.InvariantCulture);
        SideVillageGold = s.VillageGold?.ToString(CultureInfo.InvariantCulture);
        SideVillageSupport = s.VillageSupport?.ToString(CultureInfo.InvariantCulture);
        SideRecallCost = s.RecallCost?.ToString(CultureInfo.InvariantCulture);
        SidePersistent = s.Persistent;
        SideSaveId = s.SaveId;
        SideFog = s.Fog;
        SideShroud = s.Shroud;
        SideShareVision = s.ShareVision;
        SideHidden = s.Hidden;
        SideScrollToLeader = s.ScrollToLeader;
        SideDefeatCondition = s.DefeatCondition;
        SideNoLeader = s.NoLeader;
        SideRecruitmentPattern = s.RecruitmentPattern;

        foreach (var leaderDocument in SelectedSide.Document.LeaderDocuments)
            Leaders.Add(new ScenarioLeaderVm(leaderDocument));
        SelectedLeader = Leaders.FirstOrDefault();

        OnPropertyChanged(nameof(SideNumberIsEditable));
        OnPropertyChanged(nameof(SideXIsEditable));
        OnPropertyChanged(nameof(SideYIsEditable));
        OnPropertyChanged(nameof(SideGoldIsEditable));
        OnPropertyChanged(nameof(SideControllerIsEditable));
        OnPropertyChanged(nameof(SideTeamNameIsEditable));
        OnPropertyChanged(nameof(SideUserTeamNameIsEditable));
        OnPropertyChanged(nameof(SideCanRecruitIsEditable));
        OnPropertyChanged(nameof(SideRecruitIsEditable));
        OnPropertyChanged(nameof(SideConditionalInfo));

        OnPropertyChanged(nameof(SideNumberHasRawEditor));
        OnPropertyChanged(nameof(SideXHasRawEditor));
        OnPropertyChanged(nameof(SideYHasRawEditor));
        OnPropertyChanged(nameof(SideGoldHasRawEditor));
        OnPropertyChanged(nameof(SideControllerHasRawEditor));
        OnPropertyChanged(nameof(SideTeamNameHasRawEditor));
        OnPropertyChanged(nameof(SideUserTeamNameHasRawEditor));
        OnPropertyChanged(nameof(SideCanRecruitHasRawEditor));
        OnPropertyChanged(nameof(SideRecruitHasRawEditor));
        OnPropertyChanged(nameof(SideTypeIsEditable));
        OnPropertyChanged(nameof(SideIdIsEditable));
        OnPropertyChanged(nameof(SideNameIsEditable));
        OnPropertyChanged(nameof(SideTypeHasRawEditor));
        OnPropertyChanged(nameof(SideIdHasRawEditor));
        OnPropertyChanged(nameof(SideNameHasRawEditor));
        OnPropertyChanged(nameof(CanPickSideType));
        OnPropertyChanged(nameof(CanPickSideRecruit));
        RaiseSideOverhaulStateChanged();
        RaiseLeaderEditingStateChanged();

        RefreshSideLeaderPreview();
    }

    private void OnSelectedLeaderChanged()
    {
        RunUiSync(OnSelectedLeaderChangedCore);
    }

    private void OnSelectedLeaderChangedCore()
    {
        SideX = null;
        SideY = null;
        SideCanRecruit = null;
        SideType = null;
        SideId = null;
        SideName = null;
        LeaderGender = null;
        LeaderVariation = null;
        LeaderFacing = null;
        LeaderUnrenamable = null;
        LeaderRandomTraits = null;
        LeaderRandomGender = null;
        LeaderPlacement = null;
        LeaderLocationId = null;
        LeaderExtraRecruit = null;

        if (SelectedLeader is { } selected)
        {
            var leader = selected.Leader;
            SideX = leader.X?.ToString(CultureInfo.InvariantCulture);
            SideY = leader.Y?.ToString(CultureInfo.InvariantCulture);
            SideCanRecruit = leader.CanRecruit;
            SideType = leader.Type;
            SideId = leader.Id;
            SideName = leader.NameWml;
            LeaderGender = leader.Gender;
            LeaderVariation = leader.Variation;
            LeaderFacing = leader.Facing;
            LeaderUnrenamable = leader.Unrenamable;
            LeaderRandomTraits = leader.RandomTraits;
            LeaderRandomGender = leader.RandomGender;
            LeaderPlacement = leader.Placement;
            LeaderLocationId = leader.LocationId;
            LeaderExtraRecruit = leader.ExtraRecruit;
        }

        RaiseLeaderEditingStateChanged();
        RefreshSideLeaderPreview();
    }

    private void PushCurrentScenarioFieldsIntoModel()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if ((HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft) || SelectedScenario is null)
                return;

            InvalidateSourceEditingSession();
            var s = SelectedScenario.Document.Scenario;

            if (ScenarioIdIsEditable)
                s.Id = ScenarioId;

            if (ScenarioNextScenarioIsEditable)
                s.NextScenario = ScenarioNextScenario;

            if (ScenarioMapFileIsEditable)
                s.MapFile = ScenarioMapFile;

            if (ScenarioMapDataIsEditable)
                s.MapData = ScenarioMapData;

            if (ScenarioRandomStartTimeIsEditable)
                s.RandomStartTime = ScenarioRandomStartTime;

            if (ScenarioMusicIsEditable)
                s.Music = ScenarioMusic;

            if (ScenarioVictoryWhenEnemiesDefeatedIsEditable)
                s.VictoryWhenEnemiesDefeated = ScenarioVictoryWhenEnemiesDefeated;

            if (ScenarioTurnsIsEditable &&
                int.TryParse(ScenarioTurns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turns))
                s.Turns = turns;

            if (ScenarioTurnAtIsEditable &&
                int.TryParse(ScenarioTurnAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turnAt))
                s.TurnAt = turnAt;

            SelectedScenario.Refresh();
        }
    }

    private void PushCurrentSideFieldsIntoModel()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if ((HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft) || IsSynchronizingUi || SelectedSide?.AuthoredDocument?.CanEditTyped != true)
                return;

            InvalidateSourceEditingSession();
            var s = SelectedSide.Side;

            if (SideControllerIsEditable)
                SetSideString(s, "controller", SideController, value => s.Controller = value);

            if (SideTeamNameIsEditable)
                SetSideString(s, "team_name", SideTeamName, value => s.TeamName = value);

            if (SideUserTeamNameIsEditable)
                SetSideString(s, "user_team_name", SideUserTeamName, value => s.UserTeamName = value);

            if (SideRecruitIsEditable)
                SetSideString(s, "recruit", SideRecruit, value => s.Recruit = value);

            // A null imported integer can represent an authored quoted value or
            // unsupported expression. Only a changed draft requests a numeric
            // update/removal; simply displaying or saving must preserve its source.
            if (SideNumberIsEditable && !string.Equals(SideNumber,
                    s.SideNumber?.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                SetSideInt(s, "side", SideNumber, value => s.SideNumber = value);
            if (SideGoldIsEditable && !string.Equals(SideGold,
                    s.Gold?.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                SetSideInt(s, "gold", SideGold, value => s.Gold = value);

            SetSideString(s, "side_name", SideDisplayName, value => s.SideNameWml = value);
            SetSideString(s, "color", SideColor, value => s.Color = value);
            if (SideIncomeIsEditable && !string.Equals(SideIncome,
                    s.Income?.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                SetSideInt(s, "income", SideIncome, value => s.Income = value);
            SetSideInt(s, "village_gold", SideVillageGold, value => s.VillageGold = value);
            SetSideInt(s, "village_support", SideVillageSupport, value => s.VillageSupport = value);
            SetSideInt(s, "recall_cost", SideRecallCost, value => s.RecallCost = value);
            SetSideString(s, "persistent", SidePersistent, value => s.Persistent = value);
            SetSideString(s, "save_id", SideSaveId, value => s.SaveId = value);
            SetSideString(s, "fog", SideFog, value => s.Fog = value);
            SetSideString(s, "shroud", SideShroud, value => s.Shroud = value);
            SetSideString(s, "share_vision", SideShareVision, value => s.ShareVision = value);
            SetSideString(s, "hidden", SideHidden, value => s.Hidden = value);
            SetSideString(s, "scroll_to_leader", SideScrollToLeader, value => s.ScrollToLeader = value);
            SetSideString(s, "defeat_condition", SideDefeatCondition, value => s.DefeatCondition = value);
            SetSideString(s, "no_leader", SideNoLeader, value => s.NoLeader = value);
            SetSideString(s, "recruitment_pattern", SideRecruitmentPattern, value => s.RecruitmentPattern = value);

            SelectedSide.Refresh();
        }
    }

    private void SetSideString(
        ScenarioSide side,
        string key,
        string? editorValue,
        Action<string?> assign)
    {
        if (!IsEditable(side.FieldStates, key))
            return;
        var value = string.IsNullOrWhiteSpace(editorValue) ? null : editorValue;
        assign(value);
        UpdateExplicitRemoval(side, key, value is null);
    }

    private void SetSideInt(
        ScenarioSide side,
        string key,
        string? editorValue,
        Action<int?> assign)
    {
        if (!IsEditable(side.FieldStates, key))
            return;
        if (string.IsNullOrWhiteSpace(editorValue))
        {
            assign(null);
            UpdateExplicitRemoval(side, key, shouldRemove: true);
            return;
        }

        if (!int.TryParse(editorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return;
        assign(value);
        side.FieldsToRemove.Remove(key);
    }

    private static void UpdateExplicitRemoval(ScenarioSide side, string key, bool shouldRemove)
    {
        if (!shouldRemove)
        {
            side.FieldsToRemove.Remove(key);
            return;
        }

        if (side.FieldStates.TryGetValue(key, out var state) &&
            state.Kind == WmlFieldSafetyKind.PlainEditable)
        {
            side.FieldsToRemove.Add(key);
        }
    }

    private void PushCurrentLeaderFieldsIntoModel()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if ((HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft) || IsSynchronizingUi || SelectedLeader is not { CanEditTyped: true } selected)
                return;

            InvalidateSourceEditingSession();
            var leader = selected.Leader;
            SetLeaderString(leader, "type", SideType, value => leader.Type = value);
            SetLeaderString(leader, "id", SideId, value => leader.Id = value);
            SetLeaderString(leader, "name", SideName, value => leader.NameWml = value);
            SetLeaderString(leader, "gender", LeaderGender, value => leader.Gender = value);
            SetLeaderString(leader, "variation", LeaderVariation, value => leader.Variation = value);
            SetLeaderString(leader, "canrecruit", SideCanRecruit, value => leader.CanRecruit = value);
            SetLeaderInt(leader, "x", SideX, value => leader.X = value);
            SetLeaderInt(leader, "y", SideY, value => leader.Y = value);
            SetLeaderString(leader, "facing", LeaderFacing, value => leader.Facing = value);
            SetLeaderString(leader, "unrenamable", LeaderUnrenamable, value => leader.Unrenamable = value);
            SetLeaderString(leader, "random_traits", LeaderRandomTraits, value => leader.RandomTraits = value);
            SetLeaderString(leader, "random_gender", LeaderRandomGender, value => leader.RandomGender = value);
            SetLeaderString(leader, "placement", LeaderPlacement, value => leader.Placement = value);
            SetLeaderString(leader, "location_id", LeaderLocationId, value => leader.LocationId = value);
            SetLeaderString(leader, "extra_recruit", LeaderExtraRecruit, value => leader.ExtraRecruit = value);

            selected.Refresh();
            SelectedSide?.Refresh();
        }
    }

    private void SetLeaderString(
        ScenarioLeader leader,
        string key,
        string? editorValue,
        Action<string?> assign)
    {
        if (!IsSelectedLeaderFieldEditable(key))
            return;

        var value = string.IsNullOrWhiteSpace(editorValue) ? null : editorValue;
        assign(value);
        UpdateExplicitRemoval(leader, key, value is null);
    }

    private void SetLeaderInt(
        ScenarioLeader leader,
        string key,
        string? editorValue,
        Action<int?> assign)
    {
        if (!IsSelectedLeaderFieldEditable(key))
            return;

        if (string.IsNullOrWhiteSpace(editorValue))
        {
            assign(null);
            UpdateExplicitRemoval(leader, key, shouldRemove: true);
            return;
        }

        if (!int.TryParse(editorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return;
        assign(value);
        leader.FieldsToRemove.Remove(key);
    }

    private static void UpdateExplicitRemoval(
        ScenarioLeader leader,
        string key,
        bool shouldRemove)
    {
        if (!shouldRemove)
        {
            leader.FieldsToRemove.Remove(key);
            return;
        }

        if (leader.FieldStates.TryGetValue(key, out var state) &&
            state.Kind == WmlFieldSafetyKind.PlainEditable)
        {
            leader.FieldsToRemove.Add(key);
        }
    }

    public void RefreshFlowFieldsFromModel()
    {
        if (SelectedScenario is null)
            return;

        ScenarioNextScenario = SelectedScenario.Document.Scenario.NextScenario;
        SelectedScenario.Refresh();
    }

    private bool IsEditable(IReadOnlyDictionary<string, WmlFieldSafetyState>? states, string key)
    {
        if (!CanEditDocument)
            return false;

        if (states is null)
            return true;

        return !states.TryGetValue(key, out var state) || state.IsEditable;
    }

    private static string? GetReason(IReadOnlyDictionary<string, WmlFieldSafetyState>? states, string key)
    {
        if (states is null)
            return null;

        return states.TryGetValue(key, out var state) ? state.Reason : null;
    }

    private bool HasRawEditor(IReadOnlyDictionary<string, WmlFieldSafetyState>? states, string key)
    {
        if (!CanEditDocument || states is null)
            return false;

        return states.TryGetValue(key, out var state) && WmlFieldSafetyRawEditor.CanOpenRawEditor(state);
    }

    private void RaiseEditingStateChanged()
        => RequestEditingStateDisplayRefresh();

    private void RaiseEditingStateChangedCore()
    {
        RaiseWccScenarioContentChanged();
        RaiseInitialScalarFieldsChanged();
        OnPropertyChanged(nameof(ScenarioIdIsEditable));
        OnPropertyChanged(nameof(ScenarioNextScenarioIsEditable));
        OnPropertyChanged(nameof(ScenarioMapFileIsEditable));
        OnPropertyChanged(nameof(ScenarioMapDataIsEditable));
        OnPropertyChanged(nameof(ScenarioTurnsIsEditable));
        OnPropertyChanged(nameof(ScenarioTurnAtIsEditable));
        OnPropertyChanged(nameof(ScenarioRandomStartTimeIsEditable));
        OnPropertyChanged(nameof(ScenarioMusicIsEditable));
        OnPropertyChanged(nameof(ScenarioVictoryWhenEnemiesDefeatedIsEditable));
        OnPropertyChanged(nameof(ScenarioIdHasRawEditor));
        OnPropertyChanged(nameof(ScenarioNextScenarioHasRawEditor));
        OnPropertyChanged(nameof(ScenarioMapFileHasRawEditor));
        OnPropertyChanged(nameof(ScenarioMapDataHasRawEditor));
        OnPropertyChanged(nameof(ScenarioTurnsHasRawEditor));
        OnPropertyChanged(nameof(ScenarioTurnAtHasRawEditor));
        OnPropertyChanged(nameof(ScenarioRandomStartTimeHasRawEditor));
        OnPropertyChanged(nameof(ScenarioMusicHasRawEditor));
        OnPropertyChanged(nameof(ScenarioVictoryWhenEnemiesDefeatedHasRawEditor));
        OnPropertyChanged(nameof(SideNumberIsEditable));
        OnPropertyChanged(nameof(SideXIsEditable));
        OnPropertyChanged(nameof(SideYIsEditable));
        OnPropertyChanged(nameof(SideGoldIsEditable));
        OnPropertyChanged(nameof(SideControllerIsEditable));
        OnPropertyChanged(nameof(SideTeamNameIsEditable));
        OnPropertyChanged(nameof(SideUserTeamNameIsEditable));
        OnPropertyChanged(nameof(SideCanRecruitIsEditable));
        OnPropertyChanged(nameof(SideRecruitIsEditable));
        OnPropertyChanged(nameof(SideTypeIsEditable));
        OnPropertyChanged(nameof(SideIdIsEditable));
        OnPropertyChanged(nameof(SideNameIsEditable));
        OnPropertyChanged(nameof(SideNumberHasRawEditor));
        OnPropertyChanged(nameof(SideXHasRawEditor));
        OnPropertyChanged(nameof(SideYHasRawEditor));
        OnPropertyChanged(nameof(SideGoldHasRawEditor));
        OnPropertyChanged(nameof(SideControllerHasRawEditor));
        OnPropertyChanged(nameof(SideTeamNameHasRawEditor));
        OnPropertyChanged(nameof(SideUserTeamNameHasRawEditor));
        OnPropertyChanged(nameof(SideCanRecruitHasRawEditor));
        OnPropertyChanged(nameof(SideRecruitHasRawEditor));
        OnPropertyChanged(nameof(SideTypeHasRawEditor));
        OnPropertyChanged(nameof(SideIdHasRawEditor));
        OnPropertyChanged(nameof(SideNameHasRawEditor));
        OnPropertyChanged(nameof(CanPickSideType));
        OnPropertyChanged(nameof(CanPickSideRecruit));
        OnPropertyChanged(nameof(CanPickSideCoordinate));
        RaiseSideOverhaulStateChanged();
        RaiseLeaderEditingStateChanged();
        RaiseEventEditingStateChanged();
    }

    private void RaiseSideOverhaulStateChanged()
    {
        OnPropertyChanged(nameof(SideDisplayNameIsEditable));
        OnPropertyChanged(nameof(SideColorIsEditable));
        OnPropertyChanged(nameof(SideIncomeIsEditable));
        OnPropertyChanged(nameof(SideVillageGoldIsEditable));
        OnPropertyChanged(nameof(SideVillageSupportIsEditable));
        OnPropertyChanged(nameof(SideRecallCostIsEditable));
        OnPropertyChanged(nameof(SidePersistentIsEditable));
        OnPropertyChanged(nameof(SideSaveIdIsEditable));
        OnPropertyChanged(nameof(SideFogIsEditable));
        OnPropertyChanged(nameof(SideShroudIsEditable));
        OnPropertyChanged(nameof(SideShareVisionIsEditable));
        OnPropertyChanged(nameof(SideHiddenIsEditable));
        OnPropertyChanged(nameof(SideScrollToLeaderIsEditable));
        OnPropertyChanged(nameof(SideDefeatConditionIsEditable));
        OnPropertyChanged(nameof(SideNoLeaderIsEditable));
        OnPropertyChanged(nameof(SideRecruitmentPatternIsEditable));
        OnPropertyChanged(nameof(SideDisplayNameHasRawEditor));
        OnPropertyChanged(nameof(SideColorHasRawEditor));
        OnPropertyChanged(nameof(SideIncomeHasRawEditor));
        OnPropertyChanged(nameof(SideVillageGoldHasRawEditor));
        OnPropertyChanged(nameof(SideVillageSupportHasRawEditor));
        OnPropertyChanged(nameof(SideRecallCostHasRawEditor));
        OnPropertyChanged(nameof(SidePersistentHasRawEditor));
        OnPropertyChanged(nameof(SideSaveIdHasRawEditor));
        OnPropertyChanged(nameof(SideFogHasRawEditor));
        OnPropertyChanged(nameof(SideShroudHasRawEditor));
        OnPropertyChanged(nameof(SideShareVisionHasRawEditor));
        OnPropertyChanged(nameof(SideHiddenHasRawEditor));
        OnPropertyChanged(nameof(SideScrollToLeaderHasRawEditor));
        OnPropertyChanged(nameof(SideDefeatConditionHasRawEditor));
        OnPropertyChanged(nameof(SideNoLeaderHasRawEditor));
        OnPropertyChanged(nameof(SideRecruitmentPatternHasRawEditor));
        OnPropertyChanged(nameof(CanAddSide));
        OnPropertyChanged(nameof(HasSelectedSideSource));
        OnPropertyChanged(nameof(CanAddLeader));
        RaiseWccPresetApplicationCapabilities();
        OnPropertyChanged(nameof(CanRemoveSelectedSide));
        OnPropertyChanged(nameof(CanEditSelectedSideRaw));
        OnPropertyChanged(nameof(HasSelectedSide));
        OnPropertyChanged(nameof(SelectedSideRawActionLabel));
    }

    private void RaiseLeaderEditingStateChanged()
    {
        OnPropertyChanged(nameof(HasSelectedLeader));
        OnPropertyChanged(nameof(SelectedLeaderSourceInfo));
        OnPropertyChanged(nameof(LeaderConditionalInfo));
        OnPropertyChanged(nameof(CanRemoveSelectedLeader));
        OnPropertyChanged(nameof(CanMoveSelectedLeaderUp));
        OnPropertyChanged(nameof(CanMoveSelectedLeaderDown));
        OnPropertyChanged(nameof(CanEditSelectedLeaderRaw));
        OnPropertyChanged(nameof(SelectedLeaderRawActionLabel));
        OnPropertyChanged(nameof(SideXIsEditable));
        OnPropertyChanged(nameof(SideYIsEditable));
        OnPropertyChanged(nameof(SideCanRecruitIsEditable));
        OnPropertyChanged(nameof(SideTypeIsEditable));
        OnPropertyChanged(nameof(SideIdIsEditable));
        OnPropertyChanged(nameof(SideNameIsEditable));
        OnPropertyChanged(nameof(LeaderGenderIsEditable));
        OnPropertyChanged(nameof(LeaderVariationIsEditable));
        OnPropertyChanged(nameof(LeaderFacingIsEditable));
        OnPropertyChanged(nameof(LeaderUnrenamableIsEditable));
        OnPropertyChanged(nameof(LeaderRandomTraitsIsEditable));
        OnPropertyChanged(nameof(LeaderRandomGenderIsEditable));
        OnPropertyChanged(nameof(LeaderPlacementIsEditable));
        OnPropertyChanged(nameof(LeaderLocationIdIsEditable));
        OnPropertyChanged(nameof(LeaderExtraRecruitIsEditable));
        OnPropertyChanged(nameof(SideXHasRawEditor));
        OnPropertyChanged(nameof(SideYHasRawEditor));
        OnPropertyChanged(nameof(SideCanRecruitHasRawEditor));
        OnPropertyChanged(nameof(SideTypeHasRawEditor));
        OnPropertyChanged(nameof(SideIdHasRawEditor));
        OnPropertyChanged(nameof(SideNameHasRawEditor));
        OnPropertyChanged(nameof(LeaderGenderHasRawEditor));
        OnPropertyChanged(nameof(LeaderVariationHasRawEditor));
        OnPropertyChanged(nameof(LeaderFacingHasRawEditor));
        OnPropertyChanged(nameof(LeaderUnrenamableHasRawEditor));
        OnPropertyChanged(nameof(LeaderRandomTraitsHasRawEditor));
        OnPropertyChanged(nameof(LeaderRandomGenderHasRawEditor));
        OnPropertyChanged(nameof(LeaderPlacementHasRawEditor));
        OnPropertyChanged(nameof(LeaderLocationIdHasRawEditor));
        OnPropertyChanged(nameof(LeaderExtraRecruitHasRawEditor));
        OnPropertyChanged(nameof(CanPickSideType));
        OnPropertyChanged(nameof(CanPickSideCoordinate));
    }

    private void MapPreviewOnPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScenarioMapPreviewVm.SelectedEvent))
            RaiseEventEditingStateChanged();
    }

    private void RaiseEventEditingStateChanged()
    {
        OnPropertyChanged(nameof(HasSelectedMapEvent));
        OnPropertyChanged(nameof(CanAddRawEvent));
        OnPropertyChanged(nameof(CanAddWccEventFromPreset));
        OnPropertyChanged(nameof(CanEditSelectedEventRaw));
        OnPropertyChanged(nameof(SelectedEventRawActionLabel));
        OnPropertyChanged(nameof(SelectedEventEditabilityText));
    }

    public Task<ScenarioMapCoordinateSelectionResult> BeginMapCoordinateSelectionAsync(
        ScenarioMapCoordinateSelectionRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (!HasMapPreview || MapPreview is null)
        {
            return Task.FromResult(new ScenarioMapCoordinateSelectionResult(
                ScenarioMapCoordinateSelectionOutcome.Unavailable,
                Message: "This scenario has no resolved map to select from."));
        }

        SelectedEditorSectionIndex = 2;
        return MapPreview.BeginCoordinateSelectionAsync(request);
    }

    public async Task PickSelectedSideCoordinateAsync()
    {
        if (!CanPickSideCoordinate || SelectedSide is null || SelectedLeader is null)
        {
            Status = HasMapPreview
                ? "The selected side's coordinate fields cannot be edited safely."
                : "This scenario has no resolved map to select from.";
            return;
        }

        var targetScenario = SelectedScenario;
        var targetSide = SelectedSide;
        var targetLeader = SelectedLeader;
        ScenarioMapCoordinate? initial = null;
        if (int.TryParse(SideX, NumberStyles.Integer, CultureInfo.InvariantCulture, out var initialX) &&
            int.TryParse(SideY, NumberStyles.Integer, CultureInfo.InvariantCulture, out var initialY))
        {
            initial = new ScenarioMapCoordinate(initialX, initialY);
        }

        var result = await BeginMapCoordinateSelectionAsync(
            new ScenarioMapCoordinateSelectionRequest(
                $"Choose the map hex for {targetLeader.DisplayName} on {targetSide.DisplayName}.",
                initial));

        if (result.IsConfirmed &&
            result.Selection is { Coordinate: var coordinate } &&
            ReferenceEquals(SelectedScenario, targetScenario) &&
            ReferenceEquals(SelectedSide, targetSide) &&
            ReferenceEquals(SelectedLeader, targetLeader) &&
            SideXIsEditable &&
            SideYIsEditable)
        {
            SideX = coordinate.X.ToString(CultureInfo.InvariantCulture);
            SideY = coordinate.Y.ToString(CultureInfo.InvariantCulture);
            Status = $"Selected leader coordinate ({coordinate.X},{coordinate.Y}); save to write it to scenario WML.";
            SelectedEditorSectionIndex = 1;
        }
        else if (result.Outcome == ScenarioMapCoordinateSelectionOutcome.Cancelled)
        {
            Status = "Map coordinate selection cancelled; scenario WML was not changed.";
            SelectedEditorSectionIndex = 1;
        }
        else if (result.Outcome == ScenarioMapCoordinateSelectionOutcome.MapChanged)
        {
            Status = result.Message;
        }
    }

    public string? GetSelectedEventRawWml()
        => MapPreview?.SelectedEvent?.EffectiveRawWml;

    public string? ValidateSelectedEventRawWml(string rawWml)
    {
        return MapPreview?.SelectedEvent is { } entry && BeginWccEventDraft(entry) is { } token
            ? ValidateWccEventDraft(token, rawWml) : "The selected event cannot be edited here.";
    }

    public void ApplySelectedEventRawWml(string rawWml)
    {
        if (MapPreview?.SelectedEvent is not { } entry || BeginWccEventDraft(entry) is not { } token) return;
        if (!StageWccEventDraft(token, rawWml)) throw new InvalidOperationException(Status);
    }

    public string? GetNewEventRawTemplate()
        => SelectedScenario is null
            ? null
            : ScenarioEventEditor.CreateNewEventTemplate(SelectedScenario.Document);

    public string? ValidateNewEventRawWml(string rawWml)
    {
        return BeginWccNewEventDraft() is { } token ? ValidateWccEventDraft(token, rawWml)
            : "This scenario cannot accept a new event.";
    }

    public void AddRawEvent(string rawWml)
    {
        if (BeginWccNewEventDraft() is not { } token) return;
        if (!StageWccEventDraft(token, rawWml)) throw new InvalidOperationException(Status);
    }

    private ScenarioSideDocument? GetSelectedSideDocument()
    {
        return SelectedSide?.AuthoredDocument;
    }

    public string? GetScenarioFieldRawText(string key)
    {
        if (SelectedScenario is null)
            return null;

        if (!SelectedScenario.Document.Scenario.FieldStates.TryGetValue(key, out var state))
            return null;

        return WmlFieldSafetyRawEditor.GetRawText(SelectedScenario.Document.EditableBlock, state);
    }

    public void ApplyScenarioFieldRawText(string key, string newText)
    {
        if (!CanEditDocument || SelectedScenario is null)
            return;

        if (!SelectedScenario.Document.Scenario.FieldStates.TryGetValue(key, out var state))
            return;

        InvalidateSourceEditingSession();
        WmlFieldSafetyRawEditor.ApplyEditedRawText(SelectedScenario.Document.EditableBlock, state, newText);
        Status = $"Saved raw code for scenario field '{key}'.";
        OnPropertyChanged(nameof(ScenarioConditionalInfo));
        NotifyDocumentChanged();
        if (key is "map_file" or "map_data")
            RefreshMapPreview();
    }

    public string? GetSideFieldRawText(string key)
    {
        var sideDoc = GetSelectedSideDocument();
        if (sideDoc is null)
            return null;

        if (ScenarioLeaderSchema.KnownKeys.Contains(key) && SelectedLeader is { } selectedLeader)
        {
            return selectedLeader.Leader.FieldStates.TryGetValue(key, out var leaderState)
                ? WmlFieldSafetyRawEditor.GetRawText(
                    selectedLeader.Document.EditableBlock,
                    leaderState)
                : null;
        }

        if (!sideDoc.Side.FieldStates.TryGetValue(key, out var state))
            return null;

        return WmlFieldSafetyRawEditor.GetRawText(sideDoc.EditableBlock, state);
    }

    public void ApplySideFieldRawText(string key, string newText)
    {
        if (!CanEditDocument)
            return;

        var sideDoc = GetSelectedSideDocument();
        if (sideDoc is null)
            return;

        if (ScenarioLeaderSchema.KnownKeys.Contains(key) && SelectedLeader is { } selectedLeader)
        {
            if (!selectedLeader.Leader.FieldStates.TryGetValue(key, out var leaderState))
                return;
            InvalidateSourceEditingSession();
            WmlFieldSafetyRawEditor.ApplyEditedRawText(
                selectedLeader.Document.EditableBlock,
                leaderState,
                newText);
            if (selectedLeader.IsLegacyInline)
            {
                ApplySelectedSideRawWml(sideDoc.EditableBlock.ToRawText());
                Status = $"Saved raw code for leader field '{key}'.";
                OnPropertyChanged(nameof(LeaderConditionalInfo));
                return;
            }

            var nestedIndex = sideDoc.NestedLeaderDocuments
                .ToList()
                .IndexOf(selectedLeader.Document);
            if (nestedIndex >= 0)
            {
                var replacement = ScenarioLeaderEditor.ReplaceLiteralLeader(
                    sideDoc,
                    nestedIndex,
                    selectedLeader.Document.EditableBlock.ToRawText());
                RebuildLeaderList(replacement.InternalId);
            }

            Status = $"Saved raw code for leader field '{key}'.";
            OnPropertyChanged(nameof(LeaderConditionalInfo));
            NotifyDocumentChanged();
            RefreshMapPreview();
            return;
        }

        if (!sideDoc.Side.FieldStates.TryGetValue(key, out var state))
            return;

        InvalidateSourceEditingSession();
        WmlFieldSafetyRawEditor.ApplyEditedRawText(sideDoc.EditableBlock, state, newText);
        ApplySelectedSideRawWml(sideDoc.EditableBlock.ToRawText());
        Status = $"Saved raw code for side field '{key}'.";
        OnPropertyChanged(nameof(SideConditionalInfo));
    }

    public void SetLibraryUnitTypeCatalog(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds = null,
        IReadOnlySet<WccPackageRevisionId>? exactPreprocessRevisionIds = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        if (HasCurrentLibraryUnitTypeCatalog(snapshot, visibleRevisionIds, exactPreprocessRevisionIds))
            return;

        InvalidateSourceEditingSession();
        _macroLibrarySnapshot = snapshot;
        _macroVisibleRevisionIds = visibleRevisionIds is null
            ? null
            : new HashSet<WccPackageRevisionId>(visibleRevisionIds);
        var exactScope = exactPreprocessRevisionIds ?? visibleRevisionIds;
        _exactPreprocessVisibleRevisionIds = exactScope is null
            ? null
            : new HashSet<WccPackageRevisionId>(exactScope);
        RefreshMacroDefinitionResolver();
        RefreshMapPreview();

        var libraryOptions = snapshot.FindEntities(WccEntityTypes.UnitType)
            .Where(x => !string.IsNullOrWhiteSpace(x.ExportSymbol))
            .Where(x => visibleRevisionIds is null ||
                        WccLibraryRevisionVisibility.ContainsEntity(
                            snapshot,
                            visibleRevisionIds,
                            x))
            .OrderBy(x => x.ExportSymbol, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                // The index retains authored WML; picker values are semantic unit IDs.
                var symbol = WccScenarioFieldValueCodec.DecodeLiteral(x.ExportSymbol!);
                var package = snapshot.FindPackageForEntity(x.Id);
                var displayName = WmlDisplayStringNormalizer.Normalize(x.DisplayName) ?? symbol;
                return new CatalogPickerOptionVm
                {
                    ValueId = symbol,
                    DisplayName = package is null ? displayName : $"{displayName} [{package.Name}]",
                    LibraryEntityId = x.Id,
                    LibraryPackageId = package?.Id,
                    RaceId = x.Summary.GetValueOrDefault("race") ?? "",
                    PreviewPathResolver = snapshot.SourceFilesById.TryGetValue(x.SourceFileId, out var sourceFile)
                        ? global::Wcc.App.Preview.WccUnitCatalogPreview.CreateResolver(x, sourceFile, package, _wesnothInstallPath)
                        : null
                };
            });

        _unitCatalogOptions = libraryOptions.ToList();
        RefreshWccInitialUnitRows();
        OnPropertyChanged(nameof(CanPickSideType));
        OnPropertyChanged(nameof(CanPickSideRecruit));
        RefreshSideLeaderPreview();
    }

    public bool CanPickSideType =>
        SideTypeIsEditable && _unitCatalogOptions.Count > 0;

    public bool CanPickSideRecruit =>
        SideRecruitIsEditable && _unitCatalogOptions.Count > 0;

    public IReadOnlyList<CatalogPickerOptionVm> GetUnitTypeCatalogOptions()
        => _unitCatalogOptions;

    public void SetProjectMetadata(string? projectRoot, WccProjectMetadata? metadata)
    {
        CancelStaticInitialValues();
        InvalidateSourceEditingSession();
        _projectMetadataRoot = projectRoot;
        _projectMetadata = metadata;
        RaiseWccInlineDraftsChanged();
    }

    public IReadOnlyCollection<WccEntityId> GetCurrentSideTypeBindingEntityIds()
        => GetCurrentSideBindingEntityIds(
            "type",
            string.IsNullOrWhiteSpace(SideType) ? Array.Empty<string>() : new[] { SideType },
            targetsSelectedLeader: true);

    public IReadOnlyCollection<WccEntityId> GetCurrentSideRecruitBindingEntityIds()
        => GetCurrentSideBindingEntityIds(
            "recruit",
            UnitAdvancesToValue.ParseIds(SideRecruit),
            targetsSelectedLeader: false);

    private IReadOnlyCollection<WccEntityId> GetCurrentSideBindingEntityIds(
        string fieldName,
        IEnumerable<string?> currentEmittedValues,
        bool targetsSelectedLeader)
    {
        if (_projectMetadata is null ||
            string.IsNullOrWhiteSpace(_projectMetadataRoot) ||
            SelectedScenario is null ||
            SelectedSide is null ||
            string.IsNullOrWhiteSpace(_currentSourcePath ?? LoadedPath))
        {
            return Array.Empty<WccEntityId>();
        }

        string relativeSourcePath;
        try
        {
            relativeSourcePath = JsonWccProjectMetadataStore.GetRelativeSourcePath(
                _projectMetadataRoot,
                (_currentSourcePath ?? LoadedPath)!);
        }
        catch (ArgumentException)
        {
            return Array.Empty<WccEntityId>();
        }

        var ownerOrdinal = Scenarios.IndexOf(SelectedScenario);
        var sideOrdinal = GetAuthoredSideIndex(SelectedSide);
        if (ownerOrdinal < 0 || sideOrdinal < 0)
            return Array.Empty<WccEntityId>();

        var fieldPath = BuildSelectedSideFieldPath(
            sideOrdinal,
            fieldName,
            targetsSelectedLeader);
        if (fieldPath is null)
            return Array.Empty<WccEntityId>();
        var bindings = _projectMetadata.EntityBindings
            .Where(x =>
                string.Equals(x.OwnerRelativeSourcePath, relativeSourcePath, StringComparison.Ordinal) &&
                x.OwnerEntityType == WccEntityTypes.Scenario &&
                string.Equals(
                    x.OwnerExportSymbol,
                    SelectedScenario.Document.Scenario.Id,
                    StringComparison.Ordinal) &&
                x.OwnerEntityOrdinal == ownerOrdinal &&
                string.Equals(x.FieldPath, fieldPath, StringComparison.Ordinal))
            .OrderBy(x => x.SelectionOrder)
            .ToArray();
        var currentValues = currentEmittedValues
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boundValues = bindings
            .Select(x => x.EmittedValue)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!currentValues.SetEquals(boundValues))
            return Array.Empty<WccEntityId>();

        return bindings.Select(x => x.TargetEntityId).ToArray();
    }

    public void SetSideTypeFromPicker(
        string? unitTypeId,
        IReadOnlyList<CatalogPickerSelection>? selections = null)
    {
        if (!SideTypeIsEditable)
            return;

        SideType = string.IsNullOrWhiteSpace(unitTypeId) ? null : unitTypeId;
        RaiseProjectLibraryBindingSelection("type", selections, targetsSelectedLeader: true);
    }

    public void SetSideRecruitFromPicker(
        string? csv,
        IReadOnlyList<CatalogPickerSelection>? selections = null)
    {
        if (!SideRecruitIsEditable)
            return;

        SideRecruit = csv;
        RaiseProjectLibraryBindingSelection("recruit", selections, targetsSelectedLeader: false);
    }

    private void RaiseProjectLibraryBindingSelection(
        string sideFieldName,
        IReadOnlyList<CatalogPickerSelection>? selections,
        bool targetsSelectedLeader)
    {
        if (selections is null ||
            SelectedScenario is null ||
            SelectedSide is null ||
            string.IsNullOrWhiteSpace(_currentSourcePath ?? LoadedPath))
        {
            return;
        }

        var ownerOrdinal = Scenarios.IndexOf(SelectedScenario);
        var sideOrdinal = GetAuthoredSideIndex(SelectedSide);
        if (ownerOrdinal < 0 || sideOrdinal < 0)
            return;

        var fieldPath = BuildSelectedSideFieldPath(
            sideOrdinal,
            sideFieldName,
            targetsSelectedLeader);
        if (fieldPath is null)
            return;

        LibraryBindingSelectionChanged?.Invoke(
            this,
            new ProjectLibraryBindingSelectionEventArgs
            {
                SourcePath = (_currentSourcePath ?? LoadedPath)!,
                OwnerEntityType = WccEntityTypes.Scenario,
                OwnerExportSymbol = SelectedScenario.Document.Scenario.Id,
                OwnerEntityOrdinal = ownerOrdinal,
                FieldPath = fieldPath,
                Selections = selections
            });
    }

    private string? BuildSelectedSideFieldPath(
        int sideOrdinal,
        string fieldName,
        bool targetsSelectedLeader)
    {
        if (!targetsSelectedLeader)
            return $"sides/{sideOrdinal}/{fieldName}";
        if (SelectedSide?.AuthoredDocument is null || SelectedLeader is null)
            return null;
        if (SelectedLeader.IsLegacyInline)
            return $"sides/{sideOrdinal}/{fieldName}";
        var nestedIndex = SelectedSide.Document.NestedLeaderDocuments
            .ToList()
            .IndexOf(SelectedLeader.Document);
        return nestedIndex < 0
            ? null
            : $"sides/{sideOrdinal}/leaders/{nestedIndex}/{fieldName}";
    }

    public void SetWesnothInstallPath(string? path)
        => SetWesnothRuntimeProfile(path, null, "legacy-runtime-profile");

    public void SetWesnothRuntimeProfile(
        string? installPath,
        string? addonsPath,
        string? profileIdentity)
    {
        var normalizedInstallPath = NormalizeRuntimeInstallRoot(installPath);
        var normalizedAddonsPath = NormalizeDirectoryPath(addonsPath);
        var normalizedProfileIdentity = string.IsNullOrWhiteSpace(profileIdentity)
            ? "unconfigured-runtime"
            : profileIdentity;

        if (string.Equals(_wesnothInstallPath, normalizedInstallPath, PathComparison) &&
            string.Equals(_wesnothAddonsPath, normalizedAddonsPath, PathComparison) &&
            string.Equals(
                _wesnothRuntimeProfileIdentity,
                normalizedProfileIdentity,
                StringComparison.Ordinal))
        {
            return;
        }

        _savedSourcePreviewNavigationGeneration++;
        ClearPendingWccFieldSelection();
        InvalidateSourceEditingSession();
        _wesnothInstallPath = normalizedInstallPath;
        _wesnothAddonsPath = normalizedAddonsPath;
        _wesnothRuntimeProfileIdentity = normalizedProfileIdentity;
        var sourceChanged = ApplyWesnothSourceResourceProfile(
            normalizedInstallPath, normalizedAddonsPath, normalizedProfileIdentity);
        OnPropertyChanged(nameof(UsesSourceBackedFieldEditing));
        RaiseEditingStateChanged();
        if (!sourceChanged)
        {
            RefreshMapPreview();
            RefreshSideLeaderPreview();
        }
    }

    private static string? NormalizeRuntimeInstallRoot(string? path)
    {
        var normalized = NormalizeDirectoryPath(path);
        if (normalized is null || Directory.Exists(normalized))
            return normalized;

        return File.Exists(normalized) ||
               string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(normalized)
            : normalized;
    }

    private static string? NormalizeDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return path.Trim();
        }
    }

    public void RefreshMapPreview() => RefreshMapPreview(reuseStaticSources: false);

    private void RefreshMapPreview(bool reuseStaticSources)
    {
        using var displayRefresh = DeferFieldDisplayRefresh();
        lock (_wccInitialFieldRefreshGate)
        {
            if (DeferRequestedPreviewRefresh(reuseStaticSources))
                return;
            CancelBackgroundOperations(reuseStaticSources);
            var exactPreviewGeneration = _exactPreviewGeneration;

            MapPreview = null;
            _initialMapConditionalAdvisory = null;
            SetMapPreviewDiagnosticMessages([]);
            EffectiveContextValuesSummary = null;
            ClearEffectiveContextSourceBreadcrumbs();
            SetEvaluationContextPreviewDetail(
                IsEngineWmlEvaluationEnabled
                    ? "static source context; #ifdef branches are preserved, not selected yet."
                    : "WCC source preview (engine WML evaluation paused).");
            if (SelectedScenario is null)
            {
                MapPreviewStatus = "No scenario loaded.";
                return;
            }

            string? renderedSource = null;
            if (IsEngineWmlEvaluationEnabled)
            {
                try
                {
                    renderedSource = RenderPatchedFile();
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    SetMapPreviewDiagnosticMessages([
                        $"The current source could not be rendered for exact preview matching: {ex.Message}"]);
                }
            }

            var localDefinitions = BuildCurrentSourceMacroExpansionDefinitions();
            var effectiveScenario = _effectiveDocumentProjector.Project(
                SelectedScenario.Document.EditableBlock.ToRawText(),
                _evaluationContext,
                localDefinitions,
                _macroDefinitionResolver,
                new WmlMacroExpansionOptions(MaxExpansionDepth: 24, MaxExpandedLines: 65536));
            var source = _mapSourceResolver.Resolve(
                SelectedScenario.Document,
                _currentSourcePath ?? LoadedPath,
                _wesnothSourceInstallPath,
                ScenarioMapFile,
                ScenarioMapData,
                localDefinitions,
                _macroDefinitionResolver);
            ApplyMapPreview(effectiveScenario, source);

            StartStaticInitialValuesRefresh();

            if (!IsEngineWmlEvaluationEnabled)
                return;

            if (string.IsNullOrWhiteSpace(renderedSource) ||
                string.IsNullOrWhiteSpace(_evaluationContext.CampaignDefine))
            {
                return;
            }

            SetEvaluationContextPreviewDetail(
                "static preview shown while the exact Wesnoth context is checked…");
            var sourceForExactMatching = SelectSourceForExactMatching(renderedSource);
            var cancellation = new CancellationTokenSource();
            _exactPreviewCancellation = cancellation;
            _exactPreviewScalarHostGeneration = _sourceHostGeneration;
            _ = RefreshExactContextPreviewAsync(
                sourceForExactMatching,
                SelectedScenario.Document,
                ResolveEffectiveScenarioId(SelectedScenario.Document, effectiveScenario),
                _evaluationContext,
                exactPreviewGeneration,
                cancellation.Token);
        }
    }

    private static string? ResolveEffectiveScenarioId(
        ScenarioDocument authoredScenario,
        WmlEffectiveDocumentProjection staticProjection)
    {
        var literalId = WmlDisplayStringNormalizer.Normalize(authoredScenario.Scenario.Id);
        if (IsUsableScenarioId(literalId))
            return literalId;

        var scenario = staticProjection.ProjectionTree.Roots.FirstOrDefault(node =>
            string.Equals(node.TagName, "scenario", StringComparison.OrdinalIgnoreCase));
        if (scenario is null)
            return null;
        var values = WmlKeyValueExtractor.Extract(
            new WmlBlock("scenario", scenario.RawText),
            new HashSet<string>(["id"], StringComparer.OrdinalIgnoreCase));
        var projectedId = values.Known.TryGetValue("id", out var rawId)
            ? WmlDisplayStringNormalizer.Normalize(rawId)
            : null;
        return IsUsableScenarioId(projectedId) ? projectedId : null;
    }

    private static bool IsUsableScenarioId(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.IndexOfAny(['{', '}', '$', '+']) < 0;

    private void ApplyMapPreview(
        WmlEffectiveDocumentProjection effectiveScenario,
        ScenarioMapSourceResult source,
        IEnumerable<string>? additionalDiagnostics = null)
    {
        if (SelectedScenario is null)
            return;

        var spatialProjection = _spatialProjectionBuilder.Build(
            SelectedScenario.Document,
            effectiveScenario);
        var eventProjection = _eventProjectionBuilder.Build(
            SelectedScenario.Document,
            effectiveScenario);
        eventProjection = SeparateInitialMusicEvents(eventProjection);
        _initialMapOverlayDiagnostics = effectiveScenario.Diagnostics.Concat(additionalDiagnostics ?? []).ToArray();
        var branchAdvisory = $"Preprocessor branches remain unevaluated in the static '{effectiveScenario.Context.DisplayName}' preview; source is preserved until an authoritative evaluator is used.";
        _initialMapConditionalAdvisory = effectiveScenario.HasUnevaluatedPreprocessorBranches &&
            effectiveScenario.Diagnostics.Contains(branchAdvisory, StringComparer.Ordinal) ? branchAdvisory : null;
        ApplyMapPreview(source, spatialProjection, eventProjection, _initialMapOverlayDiagnostics);
    }

    private void ApplyMapPreview(
        ScenarioMapSourceResult source,
        ScenarioSpatialProjection spatialProjection,
        ScenarioEventProjection eventProjection,
        IEnumerable<string>? additionalDiagnostics = null,
        Func<bool>? isCurrent = null,
        ScenarioMapPreviewVm? previous = null)
    {
        if (SelectedScenario is null || isCurrent?.Invoke() == false)
            return;
        var extraDiagnostics = additionalDiagnostics ?? [];
        if (!source.IsResolved || string.IsNullOrWhiteSpace(source.MapText))
        {
            MapPreviewStatus = source.State switch
            {
                ScenarioMapSourceState.NoMap => "This scenario has no initial map. Dialogue-only scenarios are valid.",
                ScenarioMapSourceState.Ambiguous => "Map source is ambiguous; preview is disabled rather than guessed.",
                ScenarioMapSourceState.Unsupported =>
                    "Map source requires unsupported preprocessing or runtime context.",
                ScenarioMapSourceState.Error => "The resolved map file could not be read.",
                _ => "The scenario map could not be resolved."
            };
            if (isCurrent?.Invoke() == false) return;
            MapPreview = RestoreCapturedMapViewState(new ScenarioMapPreviewVm(
                new WesnothMapDocument(),
                _terrainVisualCatalog,
                "No resolved initial map",
                null,
                spatialProjection,
                eventProjection), previous);
            if (isCurrent?.Invoke() == false) return;
            SetMapPreviewDiagnosticMessages(
                source.Diagnostics
                    .Concat(spatialProjection.Diagnostics)
                    .Concat(eventProjection.Diagnostics)
                    .Concat(extraDiagnostics));
            return;
        }

        var map = WesnothMapParser.Parse(source.MapText,
            new WesnothMapParserOptions { DefaultBorderSize = 1, IgnoreLegacyBorderSize = true });
        if (map.Rows.Count == 0 || map.HasErrors)
        {
            MapPreviewStatus = map.Rows.Count == 0
                ? "The map source was resolved, but it contains no terrain grid."
                : "The map source contains structural errors; preview is disabled rather than repaired or guessed.";
            if (isCurrent?.Invoke() == false) return;
            MapPreview = RestoreCapturedMapViewState(new ScenarioMapPreviewVm(
                new WesnothMapDocument(),
                _terrainVisualCatalog,
                source.SourceDescription ?? "Invalid initial map",
                source.ResolvedPath,
                spatialProjection,
                eventProjection), previous);
            if (isCurrent?.Invoke() == false) return;
            SetMapPreviewDiagnosticMessages(
                source.Diagnostics
                    .Concat(map.Diagnostics.Select(FormatMapDiagnostic))
                    .Concat(spatialProjection.Diagnostics)
                    .Concat(eventProjection.Diagnostics)
                    .Concat(extraDiagnostics));
            return;
        }

        MapPreview = RestoreCapturedMapViewState(new ScenarioMapPreviewVm(
            map,
            _terrainVisualCatalog,
            source.SourceDescription ?? "Scenario map",
            source.ResolvedPath,
            spatialProjection,
            eventProjection), previous);
        if (isCurrent?.Invoke() == false) return;
        var displayedSize = map.BorderSize > 0
            ? $"{map.PlayableWidth}×{map.PlayableHeight} playable ({map.Width}×{map.Height} including border)"
            : $"{map.Width}×{map.Height}";
        MapPreviewStatus =
            $"{MapPreview.SourceDescription} · {displayedSize} · " +
            $"{MapPreview.StartingPositionCount} starting position(s)";
        if (isCurrent?.Invoke() == false) return;

        var diagnostics = source.Diagnostics
            .Concat(map.Diagnostics.Select(FormatMapDiagnostic))
            .Concat(spatialProjection.Diagnostics)
            .Concat(eventProjection.Diagnostics)
            .Concat(extraDiagnostics)
            .ToList();
        if (!string.IsNullOrWhiteSpace(_terrainVisualCatalog.Problem))
            diagnostics.Add(_terrainVisualCatalog.Problem!);
        if (MapPreview.UnknownTerrainCellCount > 0)
        {
            diagnostics.Add(
                $"{MapPreview.UnknownTerrainCellCount} cell(s) use unavailable Core/custom texture components and show a magenta fallback.");
        }
        SetMapPreviewDiagnosticMessages(diagnostics);
    }

    private async Task RefreshExactContextPreviewAsync(
        string renderedSource,
        ScenarioDocument authoredScenario,
        string? scenarioId,
        WmlEvaluationContext context,
        int generation,
        CancellationToken cancellationToken)
    {
        if (!IsEngineWmlEvaluationEnabled)
            return;
        var scalarRequest = CaptureExactInitialScalarRequest(renderedSource, authoredScenario, context, generation);
        try
        {
            var runtimeSource = await FindMatchingRuntimeSourceAsync(
                renderedSource,
                cancellationToken);
            if (runtimeSource.Path is null)
            {
                SetExactPreviewFallback(
                    generation,
                    authoredScenario,
                    context,
                    runtimeSource.Diagnostic);
                return;
            }

            if (!TryResolvePreprocessRuntime(
                    out var executablePath,
                    out var dataMainPath,
                    out var runtimeDiagnostic))
            {
                SetExactPreviewFallback(
                    generation,
                    authoredScenario,
                    context,
                    runtimeDiagnostic);
                return;
            }

            if (_macroLibrarySnapshot is not { } librarySnapshot ||
                _exactPreprocessVisibleRevisionIds is not { } visibleRevisionIds)
            {
                SetExactPreviewFallback(
                    generation,
                    authoredScenario,
                    context,
                    "exact preprocessing requires the selected runtime's completed Library revision scope.");
                return;
            }

            var dependencyPlan = WesnothPreprocessDependencyPlanner.Build(
                librarySnapshot,
                visibleRevisionIds,
                runtimeSource.Path,
                WccContentHash.ComputeText(renderedSource),
                dataMainPath!,
                _wesnothAddonsPath);
            if (!dependencyPlan.IsComplete)
            {
                SetExactPreviewFallback(
                    generation,
                    authoredScenario,
                    context,
                    dependencyPlan.Diagnostic);
                return;
            }

            var userDataDirectory = TryInferUserDataDirectory(_wesnothAddonsPath);
            if (!string.IsNullOrWhiteSpace(_wesnothAddonsPath) &&
                IsPathInsideRoot(runtimeSource.Path, _wesnothAddonsPath) &&
                userDataDirectory is null)
            {
                SetExactPreviewFallback(
                    generation,
                    authoredScenario,
                    context,
                    "exact add-on preview needs an add-ons path ending in data/add-ons so Wesnoth can use the matching user-data root.");
                return;
            }

            var engineCacheDirectory = Path.Combine(
                WccAppPaths.GetWesnothPreprocessCacheRoot(),
                "engine-usercache");
            var preprocessRequest = new WesnothPreprocessRequest(
                executablePath!,
                dependencyPlan.PreprocessInputPath!,
                context.DefinedSymbols
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray(),
                BuildPreprocessDependencyIdentity(renderedSource, dependencyPlan.Identity),
                UserDataDirectory: userDataDirectory,
                UserCacheDirectory: engineCacheDirectory,
                DependencyScopes: dependencyPlan.DependencyScopes,
                RoutingRoots: dependencyPlan.RoutingRoots);
            var result = await _wesnothPreprocessor.PreprocessAsync(preprocessRequest, cancellationToken);
            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.NormalizedWml))
            {
                SetExactPreviewFallback(
                    generation,
                    authoredScenario,
                    context,
                    $"exact Wesnoth preprocessing failed: {ShortenDiagnostic(result.Diagnostic)}");
                return;
            }

            // Exact opening-bound values do not depend on the supplementary
            // normalized preview's path/id selection or scalar breadcrumbs.
            PublishExactInitialScalarValues(scalarRequest, result, runtimeSource.PackageRevisionId);
            var selection = WmlPreprocessedScenarioSelector.SelectBySourcePath(
                result.NormalizedWml,
                result.ScenarioOrigins ?? [],
                runtimeSource.Path,
                scenarioId);
            if (!selection.IsFound || string.IsNullOrWhiteSpace(selection.RawWml))
            {
                SetExactPreviewFallback(
                    generation,
                    authoredScenario,
                    context,
                    selection.Diagnostic);
                return;
            }

            var effectiveDocument = ScenarioImporter.ImportAllFromText(selection.RawWml).SingleOrDefault();
            if (effectiveDocument is null)
            {
                SetExactPreviewFallback(
                    generation,
                    authoredScenario,
                    context,
                    "Wesnoth selected the scenario, but WCC could not read its normalized block.");
                return;
            }

            var authorityDiagnostic = result.Status == WesnothPreprocessStatus.CacheHit
                ? "Loaded cached output from Wesnoth's exact preprocessor context."
                : "Loaded fresh output from Wesnoth's exact preprocessor context.";
            var effectiveProjection = _effectiveDocumentProjector.ProjectPreprocessed(
                selection.RawWml,
                context,
                [authorityDiagnostic]);
            var effectiveMapSource = _mapSourceResolver.Resolve(
                effectiveDocument,
                runtimeSource.Path,
                _wesnothInstallPath);
            var authoredScenarioWml = authoredScenario.EditableBlock.ToRawText();
            IReadOnlyList<ScenarioScalarSourceRoute> scalarSourceRoutes = [];
            try
            {
                scalarSourceRoutes = ScenarioScalarSourceRouteAnalyzer.Analyze(
                        authoredScenario,
                        effectiveDocument,
                        context)
                    .AllRoutes
                    .ToArray();
            }
            catch (Exception)
            {
                // Source breadcrumbs are supplementary. A conservative analyzer
                // failure must not discard an otherwise valid exact preview.
            }

            if (!IsCurrentExactPreview(generation, authoredScenario, context))
                return;

            ApplyExactMapPreviewRetainingAuthoredEvents(effectiveProjection, effectiveMapSource);
            EffectiveContextValuesSummary = BuildEffectiveContextValuesSummary(effectiveDocument);
            PublishPreprocessedScalarValues(authoredScenario, effectiveDocument, scalarSourceRoutes, context,
                generation);
            SetEffectiveContextSourceBreadcrumbs(
                scalarSourceRoutes,
                authoredScenarioWml,
                runtimeSource.Path);
            try
            {
                PublishNativeSourceBreadcrumbs(renderedSource, authoredScenario, effectiveDocument,
                    runtimeSource.Path, dependencyPlan.Identity, preprocessRequest);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                SourceEditingStatus = "Native source locations are unavailable: " + ex.Message;
                OnPropertyChanged(nameof(SourceEditingStatus));
            }

            SetEvaluationContextPreviewDetail(
                result.Status == WesnothPreprocessStatus.CacheHit
                    ? "exact Wesnoth-preprocessed preview (read-only, cached)."
                    : "exact Wesnoth-preprocessed preview (read-only)."
            );
        }
        catch (OperationCanceledException)
        {
            // A newer scenario, edit, profile, or context superseded this preview.
        }
        catch (Exception ex)
        {
            SetExactPreviewFallback(
                generation,
                authoredScenario,
                context,
                $"exact preview failed safely: {ShortenDiagnostic(ex.Message)}");
        }
    }

    private async Task<RuntimeSourceMatch> FindMatchingRuntimeSourceAsync(
        string renderedSource,
        CancellationToken cancellationToken)
    {
        var contentHash = WccContentHash.ComputeText(renderedSource);
        var currentPath = _currentSourcePath ?? LoadedPath;
        if (!IsRuntimeOwnedPath(currentPath))
        {
            return new RuntimeSourceMatch(
                null,
                null,
                "static fallback because exact preprocessing currently requires the open file itself to be inside the selected Wesnoth runtime. An identical installed copy alone cannot prove that project macro dependencies are identical.");
        }

        if (_macroLibrarySnapshot is not { } snapshot ||
            _exactPreprocessVisibleRevisionIds is not { } exactRevisionIds)
        {
            return new RuntimeSourceMatch(
                null,
                null,
                "static fallback until the selected Wesnoth runtime has a completed package index for dependency verification.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(currentPath!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new RuntimeSourceMatch(null, null, "static fallback because the runtime source path is invalid.");
        }

        var matchingPathFiles = snapshot.SourceFilesById.Values
            .Where(file => !string.IsNullOrWhiteSpace(file.StoragePath) &&
                           PathsEqual(file.StoragePath, fullPath))
            .ToArray();
        if (matchingPathFiles.Length == 0)
        {
            return new RuntimeSourceMatch(
                null,
                null,
                "static fallback because the open runtime file is absent from the completed package index; refresh the package index.");
        }

        var matchingContentFiles = matchingPathFiles
            .Where(file => string.Equals(
                file.ContentHash,
                contentHash,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingContentFiles.Length == 0)
        {
            return new RuntimeSourceMatch(
                null,
                null,
                "static fallback because the open runtime file content differs from its indexed package revision; refresh the package index after saving or reloading the file.");
        }

        var matchingRevisionIds = matchingContentFiles
            .Where(file => exactRevisionIds.Contains(file.PackageRevisionId))
            .Select(file => file.PackageRevisionId)
            .Distinct()
            .ToArray();
        if (matchingRevisionIds.Length != 1)
        {
            return new RuntimeSourceMatch(
                null,
                null,
                matchingRevisionIds.Length == 0
                    ? "static fallback because the matching indexed file is outside the selected Wesnoth runtime revision scope."
                    : "static fallback because the runtime file belongs to more than one visible indexed revision.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(fullPath))
            {
                return new RuntimeSourceMatch(
                    null,
                    null,
                    "static fallback because the indexed runtime source no longer exists.");
            }

            var diskText = await File.ReadAllTextAsync(fullPath, cancellationToken);
            return string.Equals(
                WccContentHash.ComputeText(diskText),
                contentHash,
                StringComparison.OrdinalIgnoreCase)
                ? new RuntimeSourceMatch(fullPath, matchingRevisionIds[0], string.Empty)
                : new RuntimeSourceMatch(
                    null,
                    null,
                    "static fallback because the open WML differs from its configured runtime source.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
        {
            return new RuntimeSourceMatch(
                null,
                null,
                $"static fallback because the runtime source could not be verified: {ShortenDiagnostic(ex.Message)}");
        }
    }

    private bool TryResolvePreprocessRuntime(
        out string? executablePath,
        out string? dataMainPath,
        out string diagnostic)
    {
        executablePath = WccWesnothExecutableLocator.Resolve(_wesnothInstallPath);
        dataMainPath = null;
        if (executablePath is null)
        {
            diagnostic = "exact preview unavailable because the selected Wesnoth executable was not found.";
            return false;
        }

        try
        {
            var installRoot = File.Exists(_wesnothInstallPath)
                ? Path.GetDirectoryName(Path.GetFullPath(_wesnothInstallPath!))
                : _wesnothInstallPath;
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                diagnostic = "exact preview unavailable because the selected Wesnoth data root was not found.";
                return false;
            }

            var coreRoot = WccCoreInstallPaths.GetCoreDataRoot(installRoot);
            var dataRoot = Directory.GetParent(coreRoot)?.FullName;
            dataMainPath = dataRoot is null ? null : Path.Combine(dataRoot, "_main.cfg");
            if (dataMainPath is null || !File.Exists(dataMainPath))
            {
                diagnostic = "exact preview unavailable because Wesnoth data/_main.cfg was not found.";
                return false;
            }

            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            diagnostic = $"exact preview unavailable: {ShortenDiagnostic(ex.Message)}";
            return false;
        }
    }

    private string BuildPreprocessDependencyIdentity(
        string renderedSource,
        string verifiedPlanIdentity)
        => WccContentHash.ComputeText(string.Join(
            "\n",
            _wesnothRuntimeProfileIdentity,
            _wesnothInstallPath ?? string.Empty,
            _wesnothAddonsPath ?? string.Empty,
            verifiedPlanIdentity,
            WccContentHash.ComputeText(renderedSource)));

    private bool IsRuntimeOwnedPath(string? path)
        => IsPathInsideRoot(path, _wesnothInstallPath) ||
           IsPathInsideRoot(path, _wesnothAddonsPath);

    private static bool IsPathInsideRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;
        try
        {
            var relative = Path.GetRelativePath(
                Path.GetFullPath(root),
                Path.GetFullPath(path));
            return string.Equals(relative, ".", PathComparison) ||
                   (!Path.IsPathRooted(relative) &&
                    !string.Equals(relative, "..", PathComparison) &&
                    !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) &&
                    !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string? TryInferUserDataDirectory(string? addonsPath)
    {
        if (string.IsNullOrWhiteSpace(addonsPath))
            return null;
        try
        {
            var addons = new DirectoryInfo(Path.GetFullPath(addonsPath));
            var dataDirectory = addons.Parent;
            return string.Equals(addons.Name, "add-ons", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(dataDirectory?.Name, "data", StringComparison.OrdinalIgnoreCase)
                ? dataDirectory?.Parent?.FullName
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string BuildEffectiveContextValuesSummary(ScenarioDocument document)
    {
        var rows = new List<string>();
        var scenarioValues = new List<string>();
        if (document.Scenario.Turns is { } turns)
            scenarioValues.Add($"turns={turns}");
        if (document.Scenario.TurnAt is { } turnAt)
            scenarioValues.Add($"turn_at={turnAt}");
        if (WmlDisplayStringNormalizer.Normalize(document.Scenario.NextScenario) is { } next)
            scenarioValues.Add($"next={next}");
        if (scenarioValues.Count > 0)
            rows.Add($"Scenario: {string.Join(", ", scenarioValues)}");

        for (var index = 0; index < document.SideDocuments.Count; index++)
        {
            var side = document.SideDocuments[index].Side;
            var values = new List<string>();
            if (side.Gold is { } gold)
                values.Add($"gold={gold}");
            if (side.Income is { } income)
                values.Add($"income={income}");
            if (side.VillageGold is { } villageGold)
                values.Add($"village_gold={villageGold}");
            if (side.VillageSupport is { } villageSupport)
                values.Add($"village_support={villageSupport}");
            if (side.RecallCost is { } recallCost)
                values.Add($"recall_cost={recallCost}");
            if (values.Count == 0)
                continue;
            var sideLabel = side.SideNumber?.ToString(CultureInfo.InvariantCulture) ??
                            (index + 1).ToString(CultureInfo.InvariantCulture);
            rows.Add($"Side {sideLabel}: {string.Join(", ", values)}");
        }

        return rows.Count == 0
            ? "Wesnoth selected this scenario, but it has no tracked scenario/side start values."
            : string.Join(" · ", rows);
    }

    private void SetExactPreviewFallback(
        int generation,
        ScenarioDocument authoredScenario,
        WmlEvaluationContext context,
        string? diagnostic)
    {
        if (!IsCurrentExactPreview(generation, authoredScenario, context))
            return;
        EffectiveContextValuesSummary = null;
        ClearEffectiveContextSourceBreadcrumbs();
        var detail = ShortenDiagnostic(diagnostic);
        _preprocessedScalarValues = null;
        RaiseInitialScalarFieldsChanged();
        SetEvaluationContextPreviewDetail(
            detail.StartsWith("static fallback", StringComparison.OrdinalIgnoreCase)
                ? detail
                : $"static fallback: {detail}");
    }

    private bool IsCurrentExactPreview(
        int generation,
        ScenarioDocument authoredScenario,
        WmlEvaluationContext context)
        => IsEngineWmlEvaluationEnabled && generation == _exactPreviewGeneration &&
           _exactPreviewScalarHostGeneration == _sourceHostGeneration &&
           ReferenceEquals(SelectedScenario?.Document, authoredScenario) &&
           HasSameEvaluationSemantics(_evaluationContext, context);

    private static bool HasSameEvaluationSemantics(
        WmlEvaluationContext left,
        WmlEvaluationContext right)
        => string.Equals(left.CacheKey, right.CacheKey, StringComparison.Ordinal) &&
           string.Equals(
               left.DifficultyDefine,
               right.DifficultyDefine,
               StringComparison.Ordinal);

    private void SetEvaluationContextPreviewDetail(string? detail)
    {
        _evaluationContextPreviewDetail = string.IsNullOrWhiteSpace(detail)
            ? "static source context."
            : detail.Trim();
        OnPropertyChanged(nameof(EvaluationContextStatus));
    }

    private static string ShortenDiagnostic(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
            return "exact preview is unavailable; the static source preview remains active.";
        var singleLine = diagnostic.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 320 ? singleLine : singleLine[..320] + "…";
    }

    private sealed record RuntimeSourceMatch(
        string? Path,
        WccPackageRevisionId? PackageRevisionId,
        string Diagnostic);

    private void RefreshMacroDefinitionResolver()
    {
        _effectiveDocumentProjector.ClearCache();
        _macroDefinitionResolver = _macroLibrarySnapshot is null
            ? null
            : new WccLibraryMacroExpansionDefinitionResolver(
                _macroLibrarySnapshot,
                _macroVisibleRevisionIds,
                _currentSourcePath);
    }

    private IReadOnlyList<WmlMacroExpansionDefinition> BuildCurrentSourceMacroExpansionDefinitions()
        => _currentSourceMacroDefinitions
            .Select(definition => new WmlMacroExpansionDefinition(
                definition.Name,
                definition.RawText,
                _currentSourcePath,
                "Current source",
                SourceStartLine: definition.StartLineIndex))
            .ToArray();

    private static string? BuildMapPreviewDiagnostics(IEnumerable<string> diagnostics)
    {
        var distinct = diagnostics
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var unresolvedMacroNames = new List<string>();
        for (var index = distinct.Count - 1; index >= 0; index--)
        {
            if (!TryReadUnresolvedMacroName(distinct[index], out var macroName))
                continue;
            unresolvedMacroNames.Add(macroName!);
            distinct.RemoveAt(index);
        }

        if (unresolvedMacroNames.Count > 0)
        {
            unresolvedMacroNames = unresolvedMacroNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            const int displayedNameCount = 6;
            var displayed = unresolvedMacroNames
                .Take(displayedNameCount)
                .Select(name => $"{{{name}}}");
            var remainder = unresolvedMacroNames.Count > displayedNameCount
                ? $" (+{unresolvedMacroNames.Count - displayedNameCount} more)"
                : string.Empty;
            distinct.Insert(
                0,
                $"{unresolvedMacroNames.Count} macro(s) have no visible static definition and remain unexpanded: {string.Join(", ", displayed)}{remainder}.");
        }

        return distinct.Count == 0 ? null : string.Join(" ", distinct);
    }

    private static bool TryReadUnresolvedMacroName(string diagnostic, out string? macroName)
    {
        macroName = null;
        const string suffix = " has no visible definition and remains unexpanded.";
        if (!diagnostic.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var openingBrace = diagnostic.IndexOf('{');
        var closingBrace = openingBrace < 0
            ? -1
            : diagnostic.IndexOf('}', openingBrace + 1);
        if (openingBrace < 0 || closingBrace <= openingBrace + 1)
            return false;
        macroName = diagnostic[(openingBrace + 1)..closingBrace];
        return true;
    }

    private static string FormatMapDiagnostic(WesnothMapDiagnostic diagnostic)
        => diagnostic.SourceLine is { } line
            ? $"Line {line + 1}: {diagnostic.Message}"
            : diagnostic.Message;

    private void RefreshSideLeaderPreview()
    {
        SetSideLeaderPreviewBitmap(null);
        SideLeaderPreviewPath = null;
        SideLeaderPreviewStatus = "No leader preview available.";

        if (string.IsNullOrWhiteSpace(SideType))
            return;

        var option = _unitCatalogOptions.FirstOrDefault(x =>
            string.Equals(x.ValueId, SideType, StringComparison.OrdinalIgnoreCase));

        if (option is null)
        {
            SideLeaderPreviewStatus = "No matching unit type found in the current catalog.";
            return;
        }

        SideLeaderPreviewPath = option.PreviewPath;

        if (string.IsNullOrWhiteSpace(option.PreviewPath))
        {
            SideLeaderPreviewStatus = "Leader type found, but no resolved image/portrait preview is available.";
            return;
        }

        var bitmap = BitmapFileLoader.TryLoad(option.PreviewPath);
        SetSideLeaderPreviewBitmap(bitmap);

        SideLeaderPreviewStatus = bitmap is not null
            ? "Leader preview loaded."
            : "Leader preview path resolved, but the image could not be loaded.";
    }

    private void SetSideLeaderPreviewBitmap(Bitmap? bitmap)
    {
        if (!ReferenceEquals(_sideLeaderPreviewBitmap, bitmap))
        {
            _sideLeaderPreviewBitmap?.Dispose();
            SideLeaderPreviewBitmap = bitmap;
        }
    }
}