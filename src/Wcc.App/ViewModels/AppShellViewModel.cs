using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wcc.App.Preview;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core;
using Wcc.Core.Compatibility;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Settings;
using Wcc.Core.Macros;
using Wcc.Core.Library;
using Wcc.Core.Presets;
using Wcc.Core.Units;
using Wcc.Core.Wml;


namespace Wcc.App.ViewModels;

/// <summary>
/// Connects editor sessions, Library snapshots and application navigation.
/// PendingChanges owns prompts/draft reporting; Saving dispatches global Save;
/// CollectedSaving bridges typed editor recipes to the Core review/commit coordinator.
/// See docs/architecture.md for these flows and the separate preview/evaluation paths.
/// </summary>
public partial class AppShellViewModel : ViewModelBase
{
    // ---------------------------------------------------------------------
    // Paths, settings, lazy rebuild flags, and cached view-model lists
    // ---------------------------------------------------------------------
    private readonly string _unitCatalogLibraryRoot;
    private readonly string _libraryIndexRoot;
    // Raw analysis reuse belongs to this Library root; manifests and resolved snapshots stay fresh.
    private readonly JsonWccLibraryStore _libraryIndexStore;
    private (string Profile, string Install, string Core, string Campaigns, string Schema,
        string Version, string Target, string? Addons)? _libraryAnalysisConfiguration;
    private readonly string _settingsFilePath;
    private readonly WccSettings _settings;
    private WesnothInstallationIdentity? _activeWesnothInstallation;
    private string _activeWesnothInstallationProfileNameDraft = string.Empty;
    private Task? _libraryRefreshTask;
    private bool _libraryRefreshRequestedAgain;
    private int _libraryPickerProjectionDeferralDepth;
    private bool _libraryPickerProjectionRestorePending;
    private CancellationTokenSource? _unitEntityLoadCancellation;
    private CancellationTokenSource? _libraryUnitEntityLoadCancellation;
    private CancellationTokenSource? _catalogInspectorDetailLoadCancellation;
    private readonly SemaphoreSlim _projectMetadataWriteLock = new(1, 1);
    private int _projectMetadataLoadGeneration;
    private bool _isCurrentProjectMetadataLoadPending;
    private string? _currentProjectMetadataRoot;
    private WccProjectMetadata? _currentProjectMetadata;
    private readonly HashSet<int> _preparedCatalogInspectorTabs = new();
    private readonly HashSet<WccPackageRevisionId> _activeLibraryRevisionIds = new();
    private bool _unitEditorDirty;
    private bool _libraryUnitEditorDirty;
    private bool _scenarioEditorDirty;
    private bool _campaignEditorDirty;
    private string? _pendingManagedUnitSourcePathSelection;
    private string? _pendingManagedInspectorUnitSourcePathSelection;

    private readonly List<UnitTypeCatalogEntryVm> _allUnitCatalogEntryVms = new();
    private readonly List<UnitSidebarRaceGroupVm> _allUnitSidebarGroups = new();
    private readonly List<AssetRegistryEntryVm> _allAssetRegistryEntryVms = new();
    private readonly List<MacroCatalogEntryVm> _allMacroCatalogEntryVms = new();
    private readonly List<MovementTypeCatalogEntryVm> _allMovementTypeEntryVms = new();
    private readonly List<TraitCatalogEntryVm> _allTraitEntryVms = new();
    private readonly List<SchedulePresetCatalogEntryVm> _allScheduleEntryVms = new();
    private readonly List<AbilityPresetCatalogEntryVm> _allAbilityEntryVms = new();
    private readonly List<WeaponSpecialPresetCatalogEntryVm> _allWeaponSpecialEntryVms = new();
    private readonly List<RaceCatalogEntryVm> _allRaceEntryVms = new();
    private readonly List<ResistanceDefaultsCatalogEntryVm> _allResistanceDefaultsEntryVms = new();
    private readonly List<TerrainDefaultsCatalogEntryVm> _allTerrainDefaultsEntryVms = new();
    private readonly List<TerrainTypeCatalogEntryVm> _allTerrainTypeEntryVms = new();
    private readonly List<ItemCatalogEntryVm> _allItemEntryVms = new();
    private readonly Dictionary<string, WccEntityId> _projectedCatalogEntityIds =
        new(StringComparer.Ordinal);






    // ---------------------------------------------------------------------
    // Lazy rebuild flags
    // ---------------------------------------------------------------------
    private bool _areDirectPresetCatalogsDirty = true;
    private bool _areDirectPresetCatalogsBuilt;

    private const string UndefinedRaceKey = "__undefined__";


    // ---------------------------------------------------------------------
    // Shell state, child editors, and shared catalogs
    // ---------------------------------------------------------------------
    private int _selectedWorkspaceIndex;
    private int _selectedTabIndex;
    private int _selectedLibraryTabIndex;

    public int SelectedWorkspaceIndex
    {
        get => _selectedWorkspaceIndex;
        set
        {
            if (value is < 0 or > 1 || value == _selectedWorkspaceIndex)
                return;

            // Workspace tabs do not replace the open sources. Keep drafts and warmed previews.
            _selectedWorkspaceIndex = value;
            if (value == 0)
                RestoreLibraryPickerProjection();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsProjectWorkspace));
            OnPropertyChanged(nameof(IsLibraryWorkspace));
            OnPropertyChanged(nameof(CanSaveCurrentContext));
            OnPropertyChanged(nameof(IsPackageScenarioRawToolbarVisible));
        }
    }

    public bool IsProjectWorkspace => SelectedWorkspaceIndex == 0;

    public bool IsLibraryWorkspace => SelectedWorkspaceIndex == 1;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (value is < 0 or > 2) return;
            // Campaign, Scenario and Units stay open together until a source changes.
            if (SetField(ref _selectedTabIndex, value))
            {
                RestoreLibraryPickerProjection();
                OnPropertyChanged(nameof(IsPackageScenarioRawToolbarVisible));
            }
        }
    }

    public int SelectedLibraryTabIndex
    {
        get => _selectedLibraryTabIndex;
        set => SetField(ref _selectedLibraryTabIndex, value);
    }

    public CampaignWindowViewModel CampaignEditor { get; } = new();
    public ScenarioWindowViewModel ScenarioEditor { get; } = new();
    public MainWindowViewModel UnitEditor { get; } = new();
    public MainWindowViewModel LibraryUnitEditor { get; } = new();
    public PresetManagerWindowViewModel LibraryMovementTypeManager { get; private set; } = null!;
    public PresetManagerWindowViewModel LibraryTraitManager { get; private set; } = null!;
    public PresetManagerWindowViewModel LibraryScheduleManager { get; private set; } = null!;
    public PresetManagerWindowViewModel LibraryAbilityManager { get; private set; } = null!;
    public PresetManagerWindowViewModel LibraryWeaponSpecialManager { get; private set; } = null!;
    public PresetManagerWindowViewModel LibraryRaceManager { get; private set; } = null!;
    public PresetManagerWindowViewModel LibraryMacroManager { get; private set; } = null!;
    public PresetManagerWindowViewModel LibraryTerrainTypeManager { get; private set; } = null!;
    public PresetManagerWindowViewModel LibraryItemManager { get; private set; } = null!;

    public WccAssetRegistry AssetRegistry { get; private set; } = new();
    public MovementTypeCatalog MovementTypeCatalog { get; } = new();
    public TraitCatalog TraitCatalog { get; } = new();
    public SchedulePresetCatalog SchedulePresetCatalog { get; } = new();
    public AbilityPresetCatalog AbilityPresetCatalog { get; } = new();
    public WeaponSpecialPresetCatalog WeaponSpecialPresetCatalog { get; } = new();
    public RaceCatalog RaceCatalog { get; } = new();
    public ResistanceDefaultsCatalog ResistanceDefaultsCatalog { get; } = new();
    public TerrainDefaultsCatalog TerrainDefaultsCatalog { get; } = new();
    public TerrainTypeCatalog TerrainTypeCatalog { get; } = new();
    public ItemCatalog ItemCatalog { get; } = new();





    // ---------------------------------------------------------------------
    // Shared shell status, external paths, and campaign/standalone selection
    // ---------------------------------------------------------------------
    private string? _status;

    public string? Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private string? _libraryIndexStatus;

    public string? LibraryIndexStatus
    {
        get => _libraryIndexStatus;
        private set => SetField(ref _libraryIndexStatus, value);
    }

    private bool _isLibraryIndexRefreshing;

    public bool IsLibraryIndexRefreshing
    {
        get => _isLibraryIndexRefreshing;
        private set
        {
            if (!SetField(ref _isLibraryIndexRefreshing, value))
                return;

            OnPropertyChanged(nameof(CanSwitchWesnothInstallationProfile));
            OnPropertyChanged(nameof(CanClearAddonsPath));
        }
    }

    public WccLibrarySnapshot? LibrarySnapshot { get; private set; }
    public event EventHandler? LibrarySnapshotChanged;

    public IReadOnlyList<WccEntityRecord> GetActiveMacroDefinitions(
        WccMacroPurposeKind purposeKind)
        => LibraryUnitEditor.GetVisibleMacroDefinitions(purposeKind);

    public WccEffectiveAbilitySpecialRegistry? GetActiveAbilitySpecialRegistry()
        => LibraryUnitEditor.EffectiveAbilitySpecialRegistry;
    public int LibraryPackageCount => LibrarySnapshot is { } snapshot
        ? _activeLibraryRevisionIds
            .Select(revisionId => snapshot.RevisionsById.GetValueOrDefault(revisionId)?.PackageId)
            .Where(packageId => packageId is not null)
            .Distinct()
            .Count()
        : 0;
    public int LibraryEntityCount => LibrarySnapshot is { } snapshot
        ? snapshot.EntitiesById.Values.Count(IsVisibleInActiveLibrary)
        : 0;
    public int LibraryReferenceCount => LibrarySnapshot is { } snapshot
        ? snapshot.ReferencesById.Values.Count(reference =>
            snapshot.SourceFilesById.TryGetValue(reference.SourceFileId, out var sourceFile) &&
            _activeLibraryRevisionIds.Contains(sourceFile.PackageRevisionId))
        : 0;

    public ObservableCollection<WccWesnothInstallationProfile> WesnothInstallationProfiles
    {
        get;
    }

    public WccWesnothInstallationProfile? ActiveWesnothInstallationProfile
    {
        get => FindActiveWesnothInstallationProfile();
        set => ActivateWesnothInstallationProfile(value);
    }

    public bool HasWesnothInstallationProfiles =>
        _settings.WesnothInstallationProfiles.Count > 0;

    public bool CanSwitchWesnothInstallationProfile => !IsLibraryIndexRefreshing;

    public string ActiveWesnothInstallationProfileName
    {
        get => _activeWesnothInstallationProfileNameDraft;
        set => SetField(
            ref _activeWesnothInstallationProfileNameDraft,
            value ?? string.Empty);
    }

    public string? WesnothInstallPath
    {
        get => ActiveWesnothInstallationProfile?.InstallPath;
        private set
        {
            var profile = EnsureActiveWesnothInstallationProfile();
            if (string.Equals(profile.InstallPath, value, StringComparison.Ordinal))
                return;

            profile.InstallPath = value;
            OnPropertyChanged(nameof(WesnothInstallPath));
            OnPropertyChanged(nameof(HasWesnothInstallPath));
        }
    }

    public bool HasWesnothInstallPath => !string.IsNullOrWhiteSpace(WesnothInstallPath);

    public bool HasDetectedWesnothInstallation => _activeWesnothInstallation is not null;

    public string DetectedWesnothVersion =>
        ActiveWesnothInstallationProfile?.LastDetectedInstallation?.ExactVersion ?? "Not detected";

    public string TargetWesnothVersion =>
        ActiveWesnothInstallationProfile?.CompatibilityTarget ??
        ActiveWesnothInstallationProfile?.LastDetectedInstallation?.CompatibilityTarget ??
        "Not detected";

    public string WesnothReleaseLine =>
        ActiveWesnothInstallationProfile?.LastDetectedInstallation?.ReleaseLine ?? "Unknown";

    public string DetectedCoreFingerprint =>
        ActiveWesnothInstallationProfile?.LastDetectedInstallation?.CoreContentFingerprint ??
        "Not indexed yet";

    private string _wesnothInstallationIdentityStatus =
        "No Wesnoth installation has been detected.";

    public string WesnothInstallationIdentityStatus
    {
        get => _wesnothInstallationIdentityStatus;
        private set => SetField(ref _wesnothInstallationIdentityStatus, value);
    }

    public string ActiveWesnothProfileRoleStatus =>
        WccCompatibilityPolicy.ClassifyRuntime(TargetWesnothVersion) switch
        {
            WccRuntimeProfileRole.CurrentAuthoring =>
                $"Current authoring runtime. New projects created by this WCC release target Wesnoth {WccCompatibilityPolicy.CurrentAuthoringTarget}.",
            WccRuntimeProfileRole.LegacyMigration =>
                $"Legacy runtime for inspection, validation, and future migration into Wesnoth {WccCompatibilityPolicy.CurrentAuthoringTarget}. It is not a new-project target.",
            WccRuntimeProfileRole.FutureUnsupported =>
                $"This runtime is newer than the Wesnoth {WccCompatibilityPolicy.CurrentAuthoringTarget} authoring target supported by this WCC release.",
            _ =>
                $"Select a detected Wesnoth {WccCompatibilityPolicy.CurrentAuthoringTarget} runtime to create or pin a new project."
        };

    public string? AddonsPath
    {
        get => ActiveWesnothInstallationProfile?.AddonsPath;
        private set
        {
            var profile = EnsureActiveWesnothInstallationProfile();
            if (string.Equals(profile.AddonsPath, value, StringComparison.Ordinal))
                return;

            profile.AddonsPath = value;
            OnPropertyChanged(nameof(AddonsPath));
            OnPropertyChanged(nameof(HasAddonsPath));
            OnPropertyChanged(nameof(CanClearAddonsPath));
        }
    }

    public bool HasAddonsPath => !string.IsNullOrWhiteSpace(AddonsPath);
    public bool CanClearAddonsPath => HasAddonsPath && CanSwitchWesnothInstallationProfile;

    public bool ImportCoreScenarios
    {
        get => _settings.ImportCoreScenarios;
        set
        {
            if (_settings.ImportCoreScenarios == value)
                return;

            _settings.ImportCoreScenarios = value;
            SaveSettings();
            OnPropertyChanged(nameof(ImportCoreScenarios));
        }
    }

    public string CurrentProjectCompatibilityStatus
    {
        get
        {
            if (_currentProjectMetadata is null || string.IsNullOrWhiteSpace(_currentProjectMetadataRoot))
                return "Open a writable campaign to establish its Wesnoth compatibility target.";

            var compatibility = _currentProjectMetadata.Compatibility;
            if (compatibility is null)
            {
                var selectedTarget = ActiveWesnothInstallationProfile?.CompatibilityTarget;
                if (!string.IsNullOrWhiteSpace(selectedTarget) &&
                    !WccCompatibilityPolicy.CanCreateFor(selectedTarget))
                {
                    return
                        $"Unpinned project: the selected Wesnoth {selectedTarget} profile is a " +
                        $"migration/validation runtime. Switch to a Wesnoth {WccCompatibilityPolicy.CurrentAuthoringTarget} " +
                        "runtime to establish a new project target.";
                }

                return "Unpinned project: the active Core is used for this session only. " +
                       "Pin before relying on library links.";
            }

            var activeTarget = ActiveWesnothInstallationProfile?.CompatibilityTarget;
            if (string.IsNullOrWhiteSpace(activeTarget))
                return $"Project target: Wesnoth {compatibility.TargetWesnothVersion}. No active target is detected.";

            return string.Equals(
                    compatibility.TargetWesnothVersion,
                    activeTarget,
                    StringComparison.Ordinal)
                ? $"Project target: Wesnoth {compatibility.TargetWesnothVersion}. " +
                  $"Active build {DetectedWesnothVersion} matches this compatibility line."
                : $"Project target: Wesnoth {compatibility.TargetWesnothVersion}; active profile targets " +
                  $"{activeTarget}. The project pin will not be changed.";
        }
    }

    public string CurrentProjectCorePinStatus
    {
        get
        {
            var pin = _currentProjectMetadata?.Compatibility?.CoreRevision;
            if (pin is null)
                return "Exact Core revision: not pinned.";

            var fingerprint = string.IsNullOrWhiteSpace(pin.ContentFingerprint)
                ? "fingerprint unavailable"
                : $"fingerprint {pin.ContentFingerprint[..Math.Min(12, pin.ContentFingerprint.Length)]}…";
            var availability = LibrarySnapshot is null
                ? "library not ready"
                : IsPinnedCoreRevisionAvailable(LibrarySnapshot, pin)
                    ? "available"
                    : "currently unavailable; WCC will not substitute another Core";
            return $"Exact Core revision: Wesnoth {pin.ExactWesnothVersion}, {fingerprint}; " +
                   $"{availability}.";
        }
    }

    public bool CanPinCurrentProjectToActiveWesnoth =>
        _currentProjectMetadata is not null &&
        _currentProjectMetadata.Compatibility is null &&
        !string.IsNullOrWhiteSpace(_currentProjectMetadataRoot) &&
        ProjectSession.CanWrite &&
        _activeWesnothInstallation is not null &&
        WccCompatibilityPolicy.CanCreateFor(_activeWesnothInstallation.CompatibilityTarget) &&
        LibrarySnapshot is { } snapshot &&
        TryFindActiveCoreRevision(snapshot, out _, out var revision) &&
        !string.IsNullOrWhiteSpace(revision.ContentFingerprint);

    public int AssetRegistryEntryCount => AssetRegistry.Entries.Count;
    public string AssetRegistrySummary => $"{AssetRegistry.Entries.Count} unique tracked asset(s)";

    private ProjectScenarioFileVm? _selectedCampaignScenarioFile;

    public ProjectScenarioFileVm? SelectedCampaignScenarioFile
    {
        get => _selectedCampaignScenarioFile;
        set => SetField(ref _selectedCampaignScenarioFile, value);
    }

    public ObservableCollection<ProjectScenarioFileVm> StandaloneScenarioFiles { get; } = new();

    private ProjectScenarioFileVm? _selectedStandaloneScenarioFile;

    public ProjectScenarioFileVm? SelectedStandaloneScenarioFile
    {
        get => _selectedStandaloneScenarioFile;
        set => SetField(ref _selectedStandaloneScenarioFile, value);
    }

    private WccEditorSessionContext _projectSession = WccEditorSessionContext.Empty;
    private WccEditorSessionContext _scenarioSession = WccEditorSessionContext.Empty;
    private WccEditorSessionContext _unitSession = WccEditorSessionContext.Empty;
    private bool _showLooseScenarioWorkspace;
    private bool _showLooseUnitWorkspace;

    public WccEditorSessionContext ProjectSession
    {
        get => _projectSession;
        private set
        {
            if (_projectSession != value)
            {
                ScenarioEditor.CancelBackgroundOperations();
                CampaignEditor.CancelCampaignFieldEditing();
            }
            if (!SetField(ref _projectSession, value))
                return;

            RaiseScenarioWorkspaceStateChanged();
            ConfigureCampaignFieldHost();
        }
    }

    public WccEditorSessionContext ScenarioSession
    {
        get => _scenarioSession;
        private set
        {
            if (_scenarioSession != value)
                ScenarioEditor.CancelBackgroundOperations();
            if (!SetField(ref _scenarioSession, value))
                return;

            ScenarioEditor.SetSourceEditingHostSession(value);
            ScenarioEditor.SetReadOnly(value.IsReadOnly);
            OnPropertyChanged(nameof(ScenarioSessionLabel));
            OnPropertyChanged(nameof(IsCurrentScenarioReadOnly));
            OnPropertyChanged(nameof(HasCurrentScenarioDocument));
            OnPropertyChanged(nameof(HasNoCurrentScenarioDocument));
            OnPropertyChanged(nameof(CanSaveCurrentContext));
            ConfigureEditorsForActiveWesnothInstallation();
            RestoreLibraryPickerProjection();
        }
    }

    public WccEditorSessionContext UnitSession
    {
        get => _unitSession;
        private set
        {
            if (!SetField(ref _unitSession, value))
                return;

            UnitEditor.SetReadOnly(value.IsReadOnly || value.Scope == WccEditorSessionScope.Project && RequiresSelectedCampaignUnitInspection);
            UnitEditor.SetWritablePackageAssetRoot(
                value.CanImportPackageAssets ? value.AssetRootPath : null);
            OnPropertyChanged(nameof(UnitSessionLabel));
            OnPropertyChanged(nameof(IsCurrentUnitReadOnly));
            OnPropertyChanged(nameof(HasCurrentUnitDocument));
            OnPropertyChanged(nameof(HasNoCurrentUnitDocument));
            OnPropertyChanged(nameof(CanSaveCurrentContext));
            ConfigureEditorsForActiveWesnothInstallation();
            RestoreLibraryPickerProjection();
        }
    }

    public bool HasOpenCampaignProject => CampaignEditor.CurrentProject?.Kind == WccProjectKind.Campaign;
    public bool IsProjectScenarioWorkspace => HasOpenCampaignProject && !_showLooseScenarioWorkspace;
    public bool IsLooseScenarioWorkspace => !IsProjectScenarioWorkspace;
    public bool CanReturnToProjectScenarios => HasOpenCampaignProject && _showLooseScenarioWorkspace;
    public bool CanModifyProjectScenarios => IsProjectScenarioWorkspace && ProjectSession.CanWrite && !CampaignEditor.RequiresSelectedCampaignAdapter;
    public bool HasCurrentScenarioDocument => ScenarioEditor.CurrentScenarioAsset is not null;
    public bool HasNoCurrentScenarioDocument => !HasCurrentScenarioDocument;
    public bool IsCurrentScenarioReadOnly => ScenarioSession.IsReadOnly && HasCurrentScenarioDocument;
    public string ScenarioSessionLabel => ScenarioSession.DisplayLabel;
    public string ProjectSessionLabel => ProjectSession.DisplayLabel;

    public ObservableCollection<ProjectUnitFileVm> StandaloneUnitFiles { get; } = new();

    private ProjectUnitFileVm? _selectedStandaloneUnitFile;

    public ProjectUnitFileVm? SelectedStandaloneUnitFile
    {
        get => _selectedStandaloneUnitFile;
        set => SetField(ref _selectedStandaloneUnitFile, value);
    }

    public bool IsProjectUnitWorkspace => HasOpenCampaignProject && !_showLooseUnitWorkspace;
    public bool IsLooseUnitWorkspace => !IsProjectUnitWorkspace;
    public bool CanReturnToProjectUnits => HasOpenCampaignProject && _showLooseUnitWorkspace;
    public bool CanModifyProjectUnits => IsProjectUnitWorkspace && ProjectSession.CanWrite;
    public bool HasCurrentUnitDocument => UnitEditor.CurrentUnitAsset is not null && UnitEditor.Units.Count > 0;
    public bool HasNoCurrentUnitDocument => !HasCurrentUnitDocument;
    public bool IsCurrentUnitReadOnly => UnitSession.IsReadOnly && HasCurrentUnitDocument;
    public string UnitSessionLabel => UnitSession.DisplayLabel;

    private readonly List<ProjectUnitWorkspaceEntryVm> _allProjectOwnedUnitEntries = new();
    private readonly List<ProjectUnitWorkspaceEntryVm> _allProjectReferencedUnitEntries = new();
    private readonly List<ProjectUnitWorkspaceEntryVm> _allProjectUnresolvedUnitEntries = new();

    public ObservableCollection<ProjectUnitWorkspaceEntryVm> ProjectOwnedUnitEntries { get; } = new();
    public ObservableCollection<ProjectUnitWorkspaceEntryVm> ProjectReferencedUnitEntries { get; } = new();
    public ObservableCollection<ProjectUnitWorkspaceEntryVm> ProjectUnresolvedUnitEntries { get; } = new();
    public ObservableCollection<ProjectUnitRaceGroupVm> ProjectUnitRaceGroups { get; } = new();

    private ProjectUnitWorkspaceEntryVm? _selectedProjectUnitEntry;
    public ProjectUnitWorkspaceEntryVm? SelectedProjectUnitEntry
    {
        get => _selectedProjectUnitEntry;
        set
        {
            if (ReferenceEquals(_selectedProjectUnitEntry, value))
                return;

            if (_selectedProjectUnitEntry is not null)
                _selectedProjectUnitEntry.IsSelected = false;

            if (SetField(ref _selectedProjectUnitEntry, value))
            {
                if (value is not null)
                    value.IsSelected = true;

                OnPropertyChanged(nameof(IsUnresolvedProjectUnitSelected));
                OnPropertyChanged(nameof(IsProjectUnitEditorVisible));
                OnPropertyChanged(nameof(IsUnitWorkspaceEmptyMessageVisible));
            }
        }
    }

    private string? _projectUnitSearchText;
    public string? ProjectUnitSearchText
    {
        get => _projectUnitSearchText;
        set
        {
            if (!SetField(ref _projectUnitSearchText, value))
                return;

            ApplyProjectUnitWorkspaceFilter();
        }
    }

    public bool HasProjectOwnedUnitEntries => ProjectOwnedUnitEntries.Count > 0;
    public bool HasProjectReferencedUnitEntries => ProjectReferencedUnitEntries.Count > 0;
    public bool HasProjectUnresolvedUnitEntries => ProjectUnresolvedUnitEntries.Count > 0;
    public bool HasProjectUnitRaceGroups => ProjectUnitRaceGroups.Count > 0;
    public bool IsUnresolvedProjectUnitSelected =>
        IsProjectUnitWorkspace && SelectedProjectUnitEntry?.IsUnresolved == true;
    public bool IsProjectUnitEditorVisible =>
        HasCurrentUnitDocument && !IsUnresolvedProjectUnitSelected;
    public bool IsUnitWorkspaceEmptyMessageVisible =>
        HasNoCurrentUnitDocument && !IsUnresolvedProjectUnitSelected;


    // ---------------------------------------------------------------------
    // Unit sidebar and unit catalog inspector state
    // ---------------------------------------------------------------------
    // Unit sidebar
    public ObservableCollection<UnitSidebarRaceGroupVm> UnitSidebarGroups { get; } = new();

    private string? _unitSidebarSearchText;

    public string? UnitSidebarSearchText
    {
        get => _unitSidebarSearchText;
        set
        {
            if (!SetField(ref _unitSidebarSearchText, value))
                return;

            RefreshUnitSidebarEntries();
        }
    }

    private UnitTypeCatalogEntryVm? _selectedUnitCatalogEntry;

    public UnitTypeCatalogEntryVm? SelectedUnitCatalogEntry
    {
        get => _selectedUnitCatalogEntry;
        set
        {
            if (!SetField(ref _selectedUnitCatalogEntry, value))
                return;

            RefreshUnitSidebarSelectionState();
            ExpandGroupForSelectedUnit();
        }
    }

    public bool CanAddCurrentUnitToLibrary =>
        UnitEditor.CanCreateLibrarySnapshotAsset &&
        !UnitEditor.IsCurrentUnitWccOwned;

    public bool CanCloneCurrentUnit =>
        UnitEditor.CanCreateLibrarySnapshotAsset &&
        !string.IsNullOrWhiteSpace(UnitEditor.CurrentSelectedUnitTypeId);

    public bool CanDeleteCurrentUnit =>
        UnitEditor.IsCurrentUnitWccOwned;

    public bool CanSaveCurrentContext => HasPendingEditorChanges && !IsShellBusy;

    // Catalog inspector - units
    public ObservableCollection<UnitTypeCatalogEntryVm> CatalogInspectorUnitEntries { get; } = new();
    public ObservableCollection<UnitSidebarRaceGroupVm> CatalogInspectorUnitRaceGroups { get; } = new();
    public bool HasCatalogInspectorUnitRaceGroups => CatalogInspectorUnitRaceGroups.Count > 0;

    private string? _catalogInspectorUnitSearchText;

    public string? CatalogInspectorUnitSearchText
    {
        get => _catalogInspectorUnitSearchText;
        set
        {
            if (!SetField(ref _catalogInspectorUnitSearchText, value))
                return;

            RefreshCatalogInspectorUnitEntries();
        }
    }

    private UnitTypeCatalogEntryVm? _selectedInspectorUnitCatalogEntry;

    public UnitTypeCatalogEntryVm? SelectedInspectorUnitCatalogEntry
    {
        get => _selectedInspectorUnitCatalogEntry;
        set
        {
            if (ReferenceEquals(_selectedInspectorUnitCatalogEntry, value))
                return;

            if (_selectedInspectorUnitCatalogEntry is not null)
                _selectedInspectorUnitCatalogEntry.IsInspectorSelected = false;

            if (SetField(ref _selectedInspectorUnitCatalogEntry, value))
            {
                if (value is not null)
                    value.IsInspectorSelected = true;

                ExpandCatalogInspectorGroupForSelectedUnit();
            }
        }
    }

    private WccEditorSessionContext _libraryUnitSession = WccEditorSessionContext.Empty;
    public WccEditorSessionContext LibraryUnitSession
    {
        get => _libraryUnitSession;
        private set
        {
            if (!SetField(ref _libraryUnitSession, value))
                return;

            LibraryUnitEditor.SetReadOnly(value.IsReadOnly);
            LibraryUnitEditor.SetWritablePackageAssetRoot(
                value.CanImportPackageAssets ? value.AssetRootPath : null);
            OnPropertyChanged(nameof(LibraryUnitSessionLabel));
            RaiseLibraryUnitEditorStateChanged();
        }
    }

    public string LibraryUnitSessionLabel => LibraryUnitSession.DisplayLabel;
    public bool HasLibraryUnitDocument =>
        LibraryUnitEditor.CurrentUnitAsset is not null && LibraryUnitEditor.Units.Count > 0;
    public bool HasNoLibraryUnitDocument => !HasLibraryUnitDocument;
    public bool CanSaveLibraryUnit =>
        HasLibraryUnitDocument && LibraryUnitSession.CanWrite && _libraryUnitEditorDirty;
    public bool CanCloneLibraryUnit =>
        HasLibraryUnitDocument && !string.IsNullOrWhiteSpace(LibraryUnitEditor.CurrentSelectedUnitTypeId);
    public bool CanDeleteLibraryUnit =>
        HasLibraryUnitDocument && LibraryUnitEditor.IsCurrentUnitWccOwned;
    private void MarkCurrentUnitDirty()
    {
        if (_unitEditorDirty)
            return;

        _unitEditorDirty = true;
        UpdateScenarioExternalSourceChanges();
        OnPropertyChanged(nameof(CanSaveCurrentContext));
    }

    private void ClearCurrentUnitDirty()
    {
        if (!_unitEditorDirty)
            return;

        _unitEditorDirty = false;
        UpdateScenarioExternalSourceChanges();
        OnPropertyChanged(nameof(CanSaveCurrentContext));
    }

    private void MarkLibraryUnitDirty()
    {
        if (_libraryUnitEditorDirty)
            return;

        _libraryUnitEditorDirty = true;
        UpdateScenarioExternalSourceChanges();
        OnPropertyChanged(nameof(CanSaveLibraryUnit));
    }

    private void ClearLibraryUnitDirty()
    {
        if (!_libraryUnitEditorDirty)
            return;

        _libraryUnitEditorDirty = false;
        UpdateScenarioExternalSourceChanges();
        OnPropertyChanged(nameof(CanSaveLibraryUnit));
    }

    private void RaiseLibraryUnitEditorStateChanged()
    {
        OnPropertyChanged(nameof(HasLibraryUnitDocument));
        OnPropertyChanged(nameof(HasNoLibraryUnitDocument));
        OnPropertyChanged(nameof(CanSaveLibraryUnit));
        OnPropertyChanged(nameof(CanCloneLibraryUnit));
        OnPropertyChanged(nameof(CanDeleteLibraryUnit));
    }

    private void MarkCurrentScenarioDirty()
    {
        if (_scenarioEditorDirty)
            return;

        _scenarioEditorDirty = true;
        OnPropertyChanged(nameof(CanSaveCurrentContext));
    }

    private void ClearCurrentScenarioDirty()
    {
        if (!_scenarioEditorDirty)
            return;

        _scenarioEditorDirty = false;
        OnPropertyChanged(nameof(CanSaveCurrentContext));
    }

    private void MarkCurrentCampaignDirty()
    {
        if (_campaignEditorDirty)
            return;

        _campaignEditorDirty = true;
        UpdateScenarioExternalSourceChanges();
        OnPropertyChanged(nameof(CanSaveCurrentContext));
    }

    private void ClearCurrentCampaignDirty()
    {
        if (!_campaignEditorDirty && !CampaignEditor.HasCampaignSourceChanges)
            return;

        _campaignEditorDirty = false;
        UpdateScenarioExternalSourceChanges();
        OnPropertyChanged(nameof(CanSaveCurrentContext));
    }

    private void UpdateScenarioExternalSourceChanges() =>
        ScenarioEditor.SetExternalSourceChangesPending(
            _campaignEditorDirty || _unitEditorDirty || _libraryUnitEditorDirty);

    private bool SaveCurrentUnitIfDirty(
        bool refreshCatalogEntries = false,
        bool refreshSidebarEntries = false)
    {
        if (!_unitEditorDirty)
            return true;

        var currentAsset = UnitEditor.CurrentUnitAsset;
        var project = CampaignEditor.CurrentProject;
        var descriptor = CreateCurrentWorkingProjectDescriptor();
        var isWorkingCopyAsset = currentAsset is not null &&
                                 project is not null &&
                                 project.ProjectUnitFiles.Any(asset => ReferenceEquals(asset, currentAsset));

        if (UnitSession.Scope == WccEditorSessionScope.Project)
        {
            if (!isWorkingCopyAsset || !UnitSession.CanWrite || descriptor is null)
            {
                Status = UnitSession.IsReadOnly
                    ? "This project is open for read-only inspection; its unit files cannot be changed."
                    : "Could not save the current unit because it is not a file in the open campaign project.";
                return false;
            }

            Status = "Use the shared Save to check this unit and copy its campaign dependencies together.";
            return false;
        }

        if (UnitSession.Scope == WccEditorSessionScope.LooseFile)
        {
            if (currentAsset is null ||
                !UnitSession.CanWrite ||
                string.IsNullOrWhiteSpace(UnitSession.RootPath) ||
                !StandaloneUnitFiles.Any(item => ReferenceEquals(item.Asset, currentAsset)))
            {
                Status = UnitSession.IsReadOnly
                    ? "This unit file is open for read-only inspection and cannot be changed."
                    : "Could not save the current unit because its loose-file session is no longer open.";
                return false;
            }

            var result = UnitEditor.SaveCurrentWorkingCopyFile(UnitSession.RootPath);
            if (!result.IsSuccess)
            {
                Status = FormatWorkingCopySaveFailure(result);
                return false;
            }

            ClearCurrentUnitDirty();
            return true;
        }

        if (!UnitSession.CanWrite)
        {
            Status = "This unit is open for read-only inspection. Clone it before editing it.";
            return false;
        }

        var loadedText = currentAsset?.RawTextSnapshot;
        var expectedContentHash = loadedText is not null
            ? WccContentHash.ComputeText(loadedText)
            : null;
        if (!UnitEditor.SaveCurrentWccOwnedAssetSnapshot("wesnoth-wcc-library") || currentAsset is null)
        {
            Status = UnitEditor.IsCurrentUnitWccOwned ? UnitEditor.Status
                : "This unit is a read-only library source. Clone it into WCC before editing it.";
            return false;
        }

        var managedResult = WccManagedLibraryFileStore.Update(
            _unitCatalogLibraryRoot,
            WccManagedLibraryFileKind.UnitType,
            currentAsset.SourcePath,
            currentAsset.RawTextSnapshot ?? string.Empty,
            expectedContentHash);
        if (!managedResult.IsSuccess)
        {
            if (loadedText is not null)
            {
                currentAsset.RawTextSnapshot = loadedText;
                currentAsset.MacroDefinitions.Clear();
                currentAsset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(loadedText));
            }
            Status = FormatManagedLibraryMutationFailure(managedResult, "save the current unit");
            return false;
        }

        if (!UnitEditor.RebaseSavedUnitSource())
            Status = "The unit was saved. Reopen its source to refresh the editor.";
        _ = RefreshOwnedLibraryPackageIndexAsync();

        if (refreshCatalogEntries)
            RefreshUnitCatalogEntries();
        else if (refreshSidebarEntries)
            RefreshUnitSidebarEntries();

        ClearCurrentUnitDirty();
        return true;
    }

    private bool SaveCurrentScenarioIfDirty()
    {
        if (ScenarioEditor.HasWccSourceRecipeDrafts)
        {
            Status = "Use the shared Save for the pending flag preset, event and field changes.";
            return false;
        }
        if (ScenarioEditor.HasWccMusicScheduleDrafts)
        {
            Status = "Use the shared Save for pending music and schedule changes.";
            return false;
        }
        if (ScenarioEditor.HasWccInlineDrafts || ScenarioEditor.IsWccInlineSaveBusy)
        {
            Status = "Save the pending field edits before saving other scenario changes.";
            return false;
        }
        if (!_scenarioEditorDirty)
            return true;

        var currentAsset = ScenarioEditor.CurrentScenarioAsset;
        if (currentAsset is null || !ScenarioSession.CanWrite || string.IsNullOrWhiteSpace(ScenarioSession.RootPath))
        {
            Status = ScenarioSession.IsReadOnly
                ? "This scenario is open for read-only inspection and cannot be changed."
                : "Could not save the current scenario because it has no writable session.";
            return false;
        }

        if (ScenarioSession.Scope == WccEditorSessionScope.Project)
        {
            var project = CampaignEditor.CurrentProject;
            if (project is null ||
                !project.ProjectScenarioFiles.Any(asset => ReferenceEquals(asset, currentAsset)))
            {
                Status = "Could not save the current scenario because it is not part of the open project.";
                return false;
            }
        }
        else if (ScenarioSession.Scope == WccEditorSessionScope.LooseFile &&
                 !StandaloneScenarioFiles.Any(asset => ReferenceEquals(asset.Asset, currentAsset)))
        {
            Status = "Could not save the current scenario because its loose-file session is no longer open.";
            return false;
        }

        var result = ScenarioEditor.SaveCurrentWorkingCopyFile(ScenarioSession.RootPath);
        if (!result.IsSuccess)
        {
            Status = FormatWorkingCopySaveFailure(result);
            return false;
        }

        ClearCurrentScenarioDirty();

        if (result.Changed && ScenarioSession.Scope == WccEditorSessionScope.Project)
            _ = RefreshConfiguredLibraryIndexAsync();

        return true;
    }

    private bool SaveLibraryUnitIfDirty()
    {
        if (!_libraryUnitEditorDirty)
            return true;

        var currentAsset = LibraryUnitEditor.CurrentUnitAsset;
        if (!LibraryUnitSession.CanWrite ||
            currentAsset is null ||
            !LibraryUnitEditor.IsCurrentUnitWccOwned)
        {
            Status = "This Library unit is read-only. Clone it into the WCC package before editing it.";
            return false;
        }

        var loadedText = currentAsset.RawTextSnapshot;
        var expectedContentHash = loadedText is null
            ? null
            : WccContentHash.ComputeText(loadedText);
        if (!LibraryUnitEditor.SaveCurrentWccOwnedAssetSnapshot("wesnoth-wcc-library"))
        {
            Status = LibraryUnitEditor.Status ?? "Could not render the current WCC Library unit.";
            return false;
        }

        var result = WccManagedLibraryFileStore.Update(
            _unitCatalogLibraryRoot,
            WccManagedLibraryFileKind.UnitType,
            currentAsset.SourcePath,
            currentAsset.RawTextSnapshot ?? string.Empty,
            expectedContentHash);
        if (!result.IsSuccess)
        {
            if (loadedText is not null)
            {
                currentAsset.RawTextSnapshot = loadedText;
                currentAsset.MacroDefinitions.Clear();
                currentAsset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(loadedText));
            }

            Status = FormatManagedLibraryMutationFailure(result, "save the current Library unit");
            return false;
        }

        if (!LibraryUnitEditor.RebaseSavedUnitSource())
            Status = "The Library unit was saved. Reopen its source to refresh the editor.";
        ClearLibraryUnitDirty();
        _ = RefreshOwnedLibraryPackageIndexAsync();
        return true;
    }

    private bool SaveCurrentCampaignIfDirty()
    {
        if (CampaignEditor.HasCampaignSourceDrafts || CampaignEditor.IsCampaignFieldSaveBusy)
        {
            Status = "Save the pending campaign field edits before writing other campaign changes.";
            return false;
        }
        if (!_campaignEditorDirty)
            return true;

        var project = CampaignEditor.CurrentProject;
        var descriptor = CreateCurrentWorkingProjectDescriptor();
        if (!ProjectSession.CanWrite || project?.Campaign is null || descriptor is null)
        {
            Status = ProjectSession.IsReadOnly
                ? "This project is open for read-only inspection; its campaign metadata cannot be changed."
                : "Could not save campaign metadata because no writable campaign project is open.";
            return false;
        }

        var result = CampaignEditor.SaveCurrentWorkingCopyMainFile(descriptor.RootDirectory);
        if (!result.IsSuccess)
        {
            Status = FormatWorkingCopySaveFailure(result);
            return false;
        }

        ClearCurrentCampaignDirty();
        ConfigureScenarioEvaluationPlan(project);

        if (result.Changed)
            _ = RefreshConfiguredLibraryIndexAsync();

        return true;
    }

    private bool SaveCurrentEditorsIfDirty(
        bool refreshCatalogEntries = false,
        bool refreshSidebarEntries = false)
    {
        if (HasPackageScenarioRawDraft)
        { Status = "Use the shared Save or Discard for pending package RAW text."; return false; }
        if (_wmlDefinitionRecovery is { } recovery)
        { Status = DescribeWmlDefinitionResult(recovery); SetGlobalSaveError(true); return false; }
        if (_wmlDefinitionRefreshTask is { IsCompleted: false })
        { Status = "The shared definition was saved. Wait for its campaign previews to refresh."; return false; }
        if (ScenarioEditor.HasWccSourceRecipeDrafts)
        {
            Status = "Use the shared Save for the pending flag preset, event and field changes.";
            return false;
        }
        if (CampaignEditor.HasCampaignSourceDrafts || CampaignEditor.IsCampaignFieldSaveBusy)
        {
            Status = "Save the pending campaign field edits before leaving this source.";
            return false;
        }
        if (ScenarioEditor.HasWccMusicScheduleDrafts)
        {
            Status = "Use the shared Save for pending music and schedule changes.";
            return false;
        }
        if (ScenarioEditor.HasWccInlineDrafts || ScenarioEditor.IsWccInlineSaveBusy)
        {
            Status = "Save the pending field edits before leaving this source.";
            return false;
        }
        return SaveCurrentCampaignIfDirty() &&
               SaveCurrentScenarioIfDirty() &&
               SaveCurrentUnitIfDirty(refreshCatalogEntries, refreshSidebarEntries) &&
               SaveLibraryUnitIfDirty();
    }

    private static string FormatWorkingCopySaveFailure(WccWorkingCopyTextSaveResult result)
    {
        var fileName = string.IsNullOrWhiteSpace(result.SourcePath)
            ? "the current project file"
            : Path.GetFileName(result.SourcePath);

        return result.Status switch
        {
            WccWorkingCopyTextSaveStatus.ExternalChangeDetected =>
                $"Did not save {fileName}: it changed on disk after WCC loaded it. Your WCC edits remain open.",
            WccWorkingCopyTextSaveStatus.OutsideProjectRoot =>
                $"Did not save {fileName}: campaign editing may only write inside the open project folder.",
            WccWorkingCopyTextSaveStatus.SourceMissing =>
                $"Did not save {fileName}: the source file no longer exists. Your WCC edits remain open.",
            WccWorkingCopyTextSaveStatus.MissingBaseline =>
                $"Did not save {fileName}: WCC cannot verify the originally loaded text.",
            _ => $"Could not save {fileName}: {result.ErrorMessage ?? "unknown error"}"
        };
    }

    private static string FormatManagedLibraryMutationFailure(
        WccManagedLibraryMutationResult result,
        string operation)
    {
        return result.Status switch
        {
            WccManagedLibraryMutationStatus.ExternalChangeDetected =>
                $"Could not {operation}: the managed cfg changed on disk. Refresh the package index before retrying.",
            WccManagedLibraryMutationStatus.OutsideManagedKindDirectory =>
                $"Could not {operation}: the selected entity is not stored in the expected WCC content folder.",
            WccManagedLibraryMutationStatus.SourceMissing =>
                $"Could not {operation}: the managed cfg no longer exists.",
            _ => $"Could not {operation}: {result.ErrorMessage ?? "unknown managed-library error"}"
        };
    }

    public async Task<CurrentSourceWmlView?> BuildCurrentSourceWmlViewAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsProjectWorkspace)
        {
            return SelectedTabIndex switch
            {
                0 => BuildCampaignSourceView(),
                1 => ScenarioEditor.BuildSourceWmlView(),
                2 => BuildEditorSourceView(
                    "Unit source WML",
                    UnitEditor.LoadedPath,
                    UnitEditor.RenderPatchedFile()),
                _ => null
            };
        }

        if (SelectedLibraryTabIndex == 0)
        {
            return BuildEditorSourceView(
                "Library unit source WML",
                LibraryUnitEditor.LoadedPath,
                LibraryUnitEditor.RenderPatchedFile());
        }

        var manager = GetLibraryManagerForTab(SelectedLibraryTabIndex);
        if (manager?.SelectedEntry is not { } selected)
            return null;

        var entityId = selected.LibraryEntityId;
        if (entityId is null &&
            _projectedCatalogEntityIds.TryGetValue(selected.InternalId, out var projectedEntityId))
        {
            entityId = projectedEntityId;
        }

        if (entityId is not null && LibrarySnapshot is { } snapshot)
        {
            var source = await WccLibraryEntityContentLoader
                .LoadAsync(snapshot, entityId.Value, cancellationToken)
                .ConfigureAwait(false);
            var path = source.SourceFile.StoragePath ?? source.SourceFile.RelativePath;
            return new CurrentSourceWmlView(
                $"{manager.TitleText} source WML — {Path.GetFileName(path)}",
                source.SourceFileText,
                path,
                $"Read-only full source file containing '{selected.DisplayName}'.");
        }

        if (!await manager.EnsureSelectedRawTextAsync().ConfigureAwait(false) ||
            string.IsNullOrWhiteSpace(manager.SelectedRawText))
        {
            return null;
        }

        return new CurrentSourceWmlView(
            $"{manager.TitleText} WML — {selected.SourceFileName}",
            manager.SelectedRawText!,
            selected.SourceFileName,
            $"Read-only selected WML fragment for '{selected.DisplayName}'. The containing source file is not available in the current index.");
    }

    public async Task<CurrentSourceWmlView?> BuildCurrentMacroConnectionsViewAsync(
        CancellationToken cancellationToken = default)
    {
        var source = await BuildCurrentSourceWmlViewAsync(cancellationToken);
        if (source is null)
            return null;

        var mainText = IsProjectWorkspace
            ? CampaignEditor.GetCurrentMainSourceRawWml()
            : null;
        var mainSourcePath = IsProjectWorkspace
            ? CampaignEditor.CurrentProject?.SourcePath
            : null;
        var compatibility = CampaignEditor.CheckedCampaignEntryTargetCompatibility ??
                            _currentProjectMetadata?.Compatibility?.TargetWesnothVersion ??
                            FindActiveWesnothInstallationProfile()?.CompatibilityTarget;
        var evaluationPlans = string.IsNullOrWhiteSpace(mainText) ||
                              string.IsNullOrWhiteSpace(mainSourcePath)
            ? Array.Empty<WmlCampaignEvaluationPlan>()
            : WmlCampaignEvaluationPlanDiscovery
                .Discover(mainText!, mainSourcePath!, compatibility)
                .ToArray();

        var snapshot = LibrarySnapshot;
        IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds = null;
        if (snapshot is not null)
        {
            if (IsProjectWorkspace && FindCurrentProjectRevisionId(snapshot) is { } projectRevision)
            {
                visibleRevisionIds = new HashSet<WccPackageRevisionId>(
                    WccProjectAuthoringRevisionScope.GetVisibleRevisionIds(snapshot, projectRevision));
            }
            else
            {
                visibleRevisionIds = new HashSet<WccPackageRevisionId>(_activeLibraryRevisionIds);
            }
        }

        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IWmlMacroExpansionDefinitionResolver? resolver = snapshot is null
                    ? null
                    : new WccLibraryMacroExpansionDefinitionResolver(
                        snapshot,
                        visibleRevisionIds,
                        source.SourcePath);
                var graph = new WmlMacroSourceGraphBuilder().Build(
                    source.RawText,
                    source.SourcePath,
                    fallbackResolver: resolver);
                cancellationToken.ThrowIfCancellationRequested();
                var report = WmlMacroSourceGraphFormatter.Format(graph, evaluationPlans);
                var fileName = string.IsNullOrWhiteSpace(source.SourcePath)
                    ? "current source"
                    : Path.GetFileName(source.SourcePath);
                return new CurrentSourceWmlView(
                    $"Connected macros - {fileName}",
                    report,
                    source.SourcePath,
                    "Read-only static source graph using the same macro definitions as WCC previews. " +
                    "It includes exact definition WML and provenance; preprocessor branches and runtime conditions are not guessed.");
            },
            cancellationToken);
    }

    private static CurrentSourceWmlView? BuildEditorSourceView(
        string title,
        string? sourcePath,
        string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        var fileName = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.GetFileName(sourcePath);
        return new CurrentSourceWmlView(
            fileName is null ? title : $"{title} — {fileName}",
            rawText,
            sourcePath,
            "Read-only full source WML for the active editor. Unsaved structured field changes are included in this view.");
    }

    private PresetManagerWindowViewModel? GetLibraryManagerForTab(int tabIndex)
        => tabIndex switch
        {
            2 => LibraryMacroManager,
            3 => LibraryMovementTypeManager,
            4 => LibraryTraitManager,
            5 => LibraryScheduleManager,
            6 => LibraryAbilityManager,
            7 => LibraryWeaponSpecialManager,
            8 => LibraryRaceManager,
            9 => LibraryTerrainTypeManager,
            10 => LibraryItemManager,
            _ => null
        };


    // ---------------------------------------------------------------------
    // Asset inspector state
    // ---------------------------------------------------------------------
    // Catalog inspector - assets
    public ObservableCollection<AssetRegistryEntryVm> CatalogInspectorAssetEntries { get; } = new();

    private string? _catalogInspectorAssetSearchText;

    public string? CatalogInspectorAssetSearchText
    {
        get => _catalogInspectorAssetSearchText;
        set
        {
            if (!SetField(ref _catalogInspectorAssetSearchText, value))
                return;

            RefreshCatalogInspectorAssetEntries();
        }
    }


    public ObservableCollection<AssetReferenceVm> SelectedAssetReferences { get; } = new();

    private AssetRegistryEntryVm? _selectedAssetRegistryEntry;

    public AssetRegistryEntryVm? SelectedAssetRegistryEntry
    {
        get => _selectedAssetRegistryEntry;
        set
        {
            if (!SetField(ref _selectedAssetRegistryEntry, value))
                return;

            RefreshSelectedAssetReferences();
            RefreshSelectedAssetResolution();

            OnPropertyChanged(nameof(SelectedAssetLookupPath));
            OnPropertyChanged(nameof(SelectedAssetKindLabel));
            OnPropertyChanged(nameof(SelectedAssetUsageCountText));
            OnPropertyChanged(nameof(SelectedAssetOriginsLabel));
        }
    }


    public string? SelectedAssetLookupPath => SelectedAssetRegistryEntry?.LookupPath;
    public string? SelectedAssetKindLabel => SelectedAssetRegistryEntry?.KindLabel;

    public string SelectedAssetUsageCountText => SelectedAssetRegistryEntry is null
        ? "0"
        : SelectedAssetRegistryEntry.UsageCount.ToString();

    public string? SelectedAssetOriginsLabel => SelectedAssetRegistryEntry?.OriginsLabel;

    private string? _selectedAssetResolvedPath;

    public string? SelectedAssetResolvedPath
    {
        get => _selectedAssetResolvedPath;
        set => SetField(ref _selectedAssetResolvedPath, value);
    }

    private string? _selectedAssetResolutionStatus;

    public string? SelectedAssetResolutionStatus
    {
        get => _selectedAssetResolutionStatus;
        set => SetField(ref _selectedAssetResolutionStatus, value);
    }

    private string? _selectedAssetCandidateCountText;

    public string? SelectedAssetCandidateCountText
    {
        get => _selectedAssetCandidateCountText;
        set => SetField(ref _selectedAssetCandidateCountText, value);
    }

    private Bitmap? _selectedAssetPreviewBitmap;

    public Bitmap? SelectedAssetPreviewBitmap
    {
        get => _selectedAssetPreviewBitmap;
        private set => SetField(ref _selectedAssetPreviewBitmap, value);
    }

    private string? _selectedAssetPreviewStatus;

    public string? SelectedAssetPreviewStatus
    {
        get => _selectedAssetPreviewStatus;
        private set => SetField(ref _selectedAssetPreviewStatus, value);
    }


    // ---------------------------------------------------------------------
    // Macro inspector state
    // ---------------------------------------------------------------------
    public ObservableCollection<MacroCatalogEntryVm> CatalogInspectorMacroEntries { get; } = new();

    private string? _catalogInspectorMacroSearchText;

    public string? CatalogInspectorMacroSearchText
    {
        get => _catalogInspectorMacroSearchText;
        set
        {
            if (!SetField(ref _catalogInspectorMacroSearchText, value))
                return;

            RefreshCatalogInspectorMacroEntries();
        }
    }

    private MacroCatalogEntryVm? _selectedMacroCatalogEntry;

    public MacroCatalogEntryVm? SelectedMacroCatalogEntry
    {
        get => _selectedMacroCatalogEntry;
        set
        {
            if (!SetField(ref _selectedMacroCatalogEntry, value))
                return;

            RefreshSelectedMacroDetails();

            OnPropertyChanged(nameof(SelectedMacroName));
            OnPropertyChanged(nameof(SelectedMacroPurposeLabel));
            OnPropertyChanged(nameof(SelectedMacroScopeSummaryText));
            OnPropertyChanged(nameof(SelectedMacroPreferredDefinitionScopeLabel));
            OnPropertyChanged(nameof(SelectedMacroDefinitionCountText));
            OnPropertyChanged(nameof(SelectedMacroInvocationCountText));
            OnPropertyChanged(nameof(SelectedMacroTemplatedPathUsageCountText));
            OnPropertyChanged(nameof(SelectedMacroPickerEligibilityText));
            OnPropertyChanged(nameof(SelectedMacroTemplatedPathStatus));
            OnPropertyChanged(nameof(SelectedMacroPurposeConfidenceLabel));
            OnPropertyChanged(nameof(SelectedMacroPurposeEvidenceSummary));
            _ = LoadSelectedMacroDefinitionsAsync(value);
        }
    }

    public string? SelectedMacroPurposeConfidenceLabel => SelectedMacroCatalogEntry?.PurposeConfidenceLabel;
    public string? SelectedMacroPurposeEvidenceSummary => SelectedMacroCatalogEntry?.PurposeEvidenceSummary;
    public string? SelectedMacroName => SelectedMacroCatalogEntry?.Name;
    public string? SelectedMacroPurposeLabel => SelectedMacroCatalogEntry?.PurposeLabel;
    public string? SelectedMacroScopeSummaryText => SelectedMacroCatalogEntry?.ScopeSummary;

    public string? SelectedMacroPreferredDefinitionScopeLabel =>
        SelectedMacroCatalogEntry?.PreferredDefinitionScopeLabel;

    public string SelectedMacroDefinitionCountText => SelectedMacroCatalogEntry?.DefinitionCount.ToString() ?? "0";
    public string SelectedMacroInvocationCountText => SelectedMacroCatalogEntry?.InvocationCount.ToString() ?? "0";

    public string SelectedMacroTemplatedPathUsageCountText =>
        SelectedMacroCatalogEntry?.TemplatedPathUsageCount.ToString() ?? "0";

    public string SelectedMacroPickerEligibilityText =>
        SelectedMacroCatalogEntry is null
            ? "No macro selected."
            : SelectedMacroCatalogEntry.IsPickerEligible
                ? "Eligible as a reusable preset candidate for v0.1."
                : "Catalog-only for now; not a safe reusable preset candidate.";

    public string SelectedMacroTemplatedPathStatus =>
        SelectedMacroCatalogEntry is null
            ? "No macro selected."
            : SelectedMacroCatalogEntry.Entry.IsTemplatedPathMacro
                ? "Used as templated path macro."
                : "No templated path usages tracked.";

    public ObservableCollection<MacroCatalogScopeSummaryVm> SelectedMacroScopes { get; } = new();
    public ObservableCollection<MacroDefinitionOccurrenceVm> SelectedMacroDefinitions { get; } = new();
    public ObservableCollection<MacroTemplatedPathUsageVm> SelectedMacroTemplatedPathUsages { get; } = new();
    public ObservableCollection<MacroInvocationUsageVm> SelectedMacroInvocations { get; } = new();

    // ---------------------------------------------------------------------
    // Construction and settings
    // ---------------------------------------------------------------------
    public AppShellViewModel() : this(WccAppPaths.GetDefaultLibraryRoot(), WccAppPaths.GetDefaultSettingsFilePath(), null)
    {
    }

    // Tests and owned artifact probes can isolate settings and catalog writes;
    // the public application constructor uses executable-adjacent portable paths.
    private AppShellViewModel(string libraryRoot, string settingsFilePath, ScenarioWindowViewModel? scenarioEditor)
    {
        _unitCatalogLibraryRoot = libraryRoot;
        _libraryIndexRoot = Path.Combine(libraryRoot, "index-v1");
        _libraryIndexStore = new JsonWccLibraryStore(_libraryIndexRoot);
        _settingsFilePath = settingsFilePath;
        if (scenarioEditor is not null) ScenarioEditor = scenarioEditor;
        _settings = WccSettingsStorage.Load(_settingsFilePath);
        WesnothInstallationProfiles = new ObservableCollection<WccWesnothInstallationProfile>(
            _settings.WesnothInstallationProfiles);
        _activeWesnothInstallationProfileNameDraft =
            FindActiveWesnothInstallationProfile()?.Name ?? string.Empty;
        RefreshWesnothInstallationIdentity(persistChanges: true);
        LibraryMovementTypeManager = new PresetManagerWindowViewModel(
            this,
            new MovementTypePresetManagerAdapter());
        LibraryTraitManager = new PresetManagerWindowViewModel(
            this,
            new TraitPresetManagerAdapter());
        LibraryScheduleManager = new PresetManagerWindowViewModel(
            this,
            new SchedulePresetManagerAdapter());
        LibraryAbilityManager = new PresetManagerWindowViewModel(
            this,
            new AbilityPresetManagerAdapter());
        LibraryWeaponSpecialManager = new PresetManagerWindowViewModel(
            this,
            new WeaponSpecialPresetManagerAdapter());
        LibraryRaceManager = new PresetManagerWindowViewModel(
            this,
            new RacePresetManagerAdapter());
        LibraryMacroManager = new PresetManagerWindowViewModel(
            this,
            new MacroPresetManagerAdapter());
        LibraryTerrainTypeManager = new PresetManagerWindowViewModel(
            this,
            new TerrainTypePresetManagerAdapter());
        LibraryItemManager = new PresetManagerWindowViewModel(
            this,
            new ItemPresetManagerAdapter());
        ConfigureEditorsForActiveWesnothInstallation();

        UnitEditor.LibraryBindingSelectionChanged += (_, e) =>
        {
            if (UnitSession.Scope == WccEditorSessionScope.Project && UnitSession.CanWrite)
                QueueProjectLibraryBindingPersistence(e);
        };
        ScenarioEditor.LibraryBindingSelectionChanged += (_, e) =>
        {
            if (ScenarioSession.Scope == WccEditorSessionScope.Project)
                QueueProjectLibraryBindingPersistence(e);
        };

        ScenarioEditor.ScenarioSourceSaved += (_, _) => ClearCurrentScenarioDirty();
        ScenarioEditor.SourceLibraryRefreshAsync = () => RefreshOwnedProjectPackageIndexAsync();
        ScenarioEditor.BeforeWccInlineSourceNavigation = TrySaveBeforeInlineNavigation;
        CampaignEditor.SourceChanging += ScenarioEditor.CancelBackgroundOperations;
        CampaignEditor.BeforeCampaignSourceNavigation = TrySaveBeforeInlineNavigation;
        CampaignEditor.CampaignEntryCatalogChanged += (_, _) => StartCampaignEntryMembershipRefresh();
        CampaignEditor.CampaignFieldProjectionPublished += (_, _) => ConfigureCampaignEntryInspection();
        CampaignEditor.CampaignEntrySelectionRequested += entry =>
        {
            if (entry is not null) _ = SelectCampaignEntryAsync(entry.Identity);
        };
        CampaignEditor.CampaignFieldsSaved += (_, _) =>
        {
            ClearCurrentCampaignDirty();
            if (CampaignEditor.CurrentProject is { } project)
                ConfigureScenarioEvaluationPlan(project);
        };
        CampaignEditor.CampaignFieldLibraryRefreshAsync = () => RefreshOwnedProjectPackageIndexAsync();
        CampaignEditor.CampaignFieldDraftsChanged += (_, _) => OnPropertyChanged(nameof(CanSaveCurrentContext));

        CampaignEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CampaignWindowViewModel.HasCampaignSourceDrafts) or
                nameof(CampaignWindowViewModel.HasCampaignSourceChanges) or
                nameof(CampaignWindowViewModel.IsCampaignFieldSaveBusy))
                OnPropertyChanged(nameof(CanSaveCurrentContext));
            if (e.PropertyName == nameof(CampaignWindowViewModel.Status) && SelectedTabIndex == 0)
                Status = CampaignEditor.Status;

            if (e.PropertyName == nameof(CampaignWindowViewModel.CurrentProject))
            {
                using var projectionBatch = CampaignEditor.DeferCampaignFieldProjection();
                RetireCheckedPackageWorkspacePublication();
                RetireCampaignUnitDisplayScopes();
                EnsureCampaignDependencySourceSession();
                InvalidateProjectMetadataForCurrentProjectChange();
                ClearCurrentCampaignDirty();
                ConfigureSessionForCurrentProject();
                _currentProjectOpeningTask = HandleCurrentProjectChangedAsync();
            }

            if (!CampaignEditor.IsSynchronizingUi && CampaignEditor.CurrentProject?.Campaign is not null)
            {
                switch (e.PropertyName)
                {
                    case nameof(CampaignWindowViewModel.TextDomainName):
                    case nameof(CampaignWindowViewModel.TextDomainPath):
                    case nameof(CampaignWindowViewModel.CampaignId):
                    case nameof(CampaignWindowViewModel.CampaignNameWml):
                    case nameof(CampaignWindowViewModel.CampaignAbbrevWml):
                    case nameof(CampaignWindowViewModel.CampaignDefine):
                    case nameof(CampaignWindowViewModel.CampaignFirstScenario):
                    case nameof(CampaignWindowViewModel.CampaignIcon):
                    case nameof(CampaignWindowViewModel.CampaignImage):
                    case nameof(CampaignWindowViewModel.CampaignBackground):
                    case nameof(CampaignWindowViewModel.CampaignRank):
                    case nameof(CampaignWindowViewModel.CampaignYear):
                    case nameof(CampaignWindowViewModel.CampaignDescriptionWml):
                    case nameof(CampaignWindowViewModel.EditVersion):
                        MarkCurrentCampaignDirty();
                        break;
                }
            }
        };

        ScenarioEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ScenarioWindowViewModel.HasWccInlineDrafts) or
                nameof(ScenarioWindowViewModel.HasWccMusicScheduleDrafts) or
                nameof(ScenarioWindowViewModel.HasWccSideFlagDrafts) or
                nameof(ScenarioWindowViewModel.HasWccEventDrafts) or
                nameof(ScenarioWindowViewModel.HasWccSideTopologyDrafts) or
                nameof(ScenarioWindowViewModel.HasWccLeaderAdditionDrafts) or
                nameof(ScenarioWindowViewModel.HasWccInitialUnitDrafts) or
                nameof(ScenarioWindowViewModel.HasWccStoryDraft) or
                nameof(ScenarioWindowViewModel.IsWccInlineSaveBusy))
                OnPropertyChanged(nameof(CanSaveCurrentContext));
            if (e.PropertyName == nameof(ScenarioWindowViewModel.Status) && SelectedTabIndex == 1)
            {
                Status = ScenarioEditor.Status;
            }

            if (!ScenarioEditor.IsSynchronizingUi &&
                ScenarioSession.CanWrite &&
                ScenarioEditor.CurrentScenarioAsset is not null)
            {
                switch (e.PropertyName)
                {
                    case nameof(ScenarioWindowViewModel.ScenarioId):
                    case nameof(ScenarioWindowViewModel.ScenarioNextScenario):
                    case nameof(ScenarioWindowViewModel.ScenarioMapFile):
                    case nameof(ScenarioWindowViewModel.ScenarioMapData):
                    case nameof(ScenarioWindowViewModel.ScenarioTurns):
                    case nameof(ScenarioWindowViewModel.ScenarioTurnAt):
                    case nameof(ScenarioWindowViewModel.ScenarioRandomStartTime):
                    case nameof(ScenarioWindowViewModel.ScenarioMusic):
                    case nameof(ScenarioWindowViewModel.ScenarioVictoryWhenEnemiesDefeated):
                    case nameof(ScenarioWindowViewModel.SideNumber):
                    case nameof(ScenarioWindowViewModel.SideX):
                    case nameof(ScenarioWindowViewModel.SideY):
                    case nameof(ScenarioWindowViewModel.SideGold):
                    case nameof(ScenarioWindowViewModel.SideController):
                    case nameof(ScenarioWindowViewModel.SideTeamName):
                    case nameof(ScenarioWindowViewModel.SideUserTeamName):
                    case nameof(ScenarioWindowViewModel.SideCanRecruit):
                    case nameof(ScenarioWindowViewModel.SideType):
                    case nameof(ScenarioWindowViewModel.SideId):
                    case nameof(ScenarioWindowViewModel.SideName):
                    case nameof(ScenarioWindowViewModel.SideRecruit):
                    case nameof(ScenarioWindowViewModel.SideDisplayName):
                    case nameof(ScenarioWindowViewModel.SideColor):
                    case nameof(ScenarioWindowViewModel.SideIncome):
                    case nameof(ScenarioWindowViewModel.SideVillageGold):
                    case nameof(ScenarioWindowViewModel.SideVillageSupport):
                    case nameof(ScenarioWindowViewModel.SideRecallCost):
                    case nameof(ScenarioWindowViewModel.SidePersistent):
                    case nameof(ScenarioWindowViewModel.SideSaveId):
                    case nameof(ScenarioWindowViewModel.SideFog):
                    case nameof(ScenarioWindowViewModel.SideShroud):
                    case nameof(ScenarioWindowViewModel.SideShareVision):
                    case nameof(ScenarioWindowViewModel.SideHidden):
                    case nameof(ScenarioWindowViewModel.SideScrollToLeader):
                    case nameof(ScenarioWindowViewModel.SideDefeatCondition):
                    case nameof(ScenarioWindowViewModel.SideNoLeader):
                    case nameof(ScenarioWindowViewModel.SideRecruitmentPattern):
                    case nameof(ScenarioWindowViewModel.LeaderGender):
                    case nameof(ScenarioWindowViewModel.LeaderVariation):
                    case nameof(ScenarioWindowViewModel.LeaderFacing):
                    case nameof(ScenarioWindowViewModel.LeaderUnrenamable):
                    case nameof(ScenarioWindowViewModel.LeaderRandomTraits):
                    case nameof(ScenarioWindowViewModel.LeaderRandomGender):
                    case nameof(ScenarioWindowViewModel.LeaderPlacement):
                    case nameof(ScenarioWindowViewModel.LeaderLocationId):
                    case nameof(ScenarioWindowViewModel.LeaderExtraRecruit):
                    case nameof(ScenarioWindowViewModel.EditVersion):
                        MarkCurrentScenarioDirty();
                        break;
                }
            }
        };

        UnitEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Status) &&
                SelectedTabIndex == 2)
            {
                Status = UnitEditor.Status;
            }

            if (!UnitEditor.IsSynchronizingUi &&
                UnitSession.CanWrite &&
                IsUnitEditorMutationProperty(e.PropertyName))
            {
                _unitSourceSaveDraftVersion++;
                MarkCurrentUnitDirty();
            }

            OnPropertyChanged(nameof(CanAddCurrentUnitToLibrary));
            OnPropertyChanged(nameof(CanCloneCurrentUnit));
            OnPropertyChanged(nameof(CanDeleteCurrentUnit));
        };

        LibraryUnitEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Status) && IsLibraryWorkspace)
                Status = LibraryUnitEditor.Status;

            if (!LibraryUnitEditor.IsSynchronizingUi &&
                LibraryUnitSession.CanWrite &&
                IsUnitEditorMutationProperty(e.PropertyName))
            {
                MarkLibraryUnitDirty();
            }

            // Preview frames, animation controls and bitmap completions do not
            // change document ownership, the session or the pending source state.
            // Publish capabilities at the existing source boundary; dirty/session
            // transitions already notify through their own setters.
            if (!LibraryUnitEditor.IsPublishingUnitSource &&
                (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is
                    nameof(MainWindowViewModel.IsPublishingUnitSource) or
                    nameof(MainWindowViewModel.LoadedPath) or
                    nameof(MainWindowViewModel.SelectedUnit) or
                    nameof(MainWindowViewModel.IsReadOnly) ||
                 !LibraryUnitEditor.IsSynchronizingUi && e.PropertyName == nameof(MainWindowViewModel.UnitId)))
                RaiseLibraryUnitEditorStateChanged();
        };

        RestoreLibraryPickerProjection();
        RefreshUnitSidebarEntries();

        RaiseWesnothProfilePropertiesChanged();
        OnPropertyChanged(nameof(ImportCoreScenarios));
        OnPropertyChanged(nameof(CanAddCurrentUnitToLibrary));
        OnPropertyChanged(nameof(CanCloneCurrentUnit));
        InitializeShellPresentation();
        InitializeScenarioIdSuggestions();
        InitializeUnitInheritanceDisplay();
    }

    private static bool IsUnitEditorMutationProperty(string? propertyName) => propertyName is
        nameof(MainWindowViewModel.UnitRace) or
        nameof(MainWindowViewModel.UnitId) or
        nameof(MainWindowViewModel.UnitNameWml) or
        nameof(MainWindowViewModel.UnitImage) or
        nameof(MainWindowViewModel.UnitProfile) or
        nameof(MainWindowViewModel.UnitSmallProfile) or
        nameof(MainWindowViewModel.UnitGender) or
        nameof(MainWindowViewModel.UnitMovementType) or
        nameof(MainWindowViewModel.UnitAlignment) or
        nameof(MainWindowViewModel.UnitAdvancesTo) or
        nameof(MainWindowViewModel.UnitUsage) or
        nameof(MainWindowViewModel.UnitDescriptionWml) or
        nameof(MainWindowViewModel.UnitHitpoints) or
        nameof(MainWindowViewModel.UnitMovement) or
        nameof(MainWindowViewModel.UnitExperience) or
        nameof(MainWindowViewModel.UnitLevel) or
        nameof(MainWindowViewModel.AttackDamage) or
        nameof(MainWindowViewModel.AttackName) or
        nameof(MainWindowViewModel.AttackDescription) or
        nameof(MainWindowViewModel.AttackIcon) or
        nameof(MainWindowViewModel.AttackNumber) or
        nameof(MainWindowViewModel.AttackRange) or
        nameof(MainWindowViewModel.AttackType) or
        nameof(MainWindowViewModel.AttackAlignment) or
        nameof(MainWindowViewModel.AttackMinRange) or
        nameof(MainWindowViewModel.AttackMaxRange) or
        nameof(MainWindowViewModel.VisualNameWml) or
        nameof(MainWindowViewModel.VisualGender) or
        nameof(MainWindowViewModel.VisualImage) or
        nameof(MainWindowViewModel.VisualProfile) or
        nameof(MainWindowViewModel.VisualSmallProfile) or
        nameof(MainWindowViewModel.AbilityMacros) or
        nameof(MainWindowViewModel.SelectedAttackSpecials) or
        nameof(MainWindowViewModel.Attacks) or
        nameof(MainWindowViewModel.Animations) or
        nameof(MainWindowViewModel.EditVersion);

    private void ConfigureSessionForCurrentProject()
    {
        var project = CampaignEditor.CurrentProject;
        // A raw-save navigation receipt only belongs to its original project.
        // Releasing it here also releases both captured catalogs on unload.
        if (_packageRawNavigationPublication is { } publication && !ReferenceEquals(publication.Project, project))
            _packageRawNavigationPublication = null;
        StartCampaignEntryMembershipRefresh();
        if (project?.Kind != WccProjectKind.Campaign || string.IsNullOrWhiteSpace(project.SourcePath))
        {
            ScenarioEditor.SetEvaluationPlan(null);
            ProjectSession = WccEditorSessionContext.Empty;
            CampaignEditor.SetReadOnly(true);
            SelectedCampaignScenarioFile = null;

            if (ScenarioSession.Scope == WccEditorSessionScope.Project)
            {
                ScenarioEditor.LoadFromText("(no scenario)", string.Empty);
                ScenarioSession = WccEditorSessionContext.Empty;
                ClearCurrentScenarioDirty();
            }

            if (UnitSession.Scope == WccEditorSessionScope.Project)
            {
                UnitEditor.LoadFromText("(no unit)", string.Empty);
                UnitSession = WccEditorSessionContext.Empty;
                ClearCurrentUnitDirty();
            }

            ClearProjectUnitWorkspaceEntries();

            RaiseScenarioWorkspaceStateChanged();
            RaiseUnitWorkspaceStateChanged();
            return;
        }

        var sourcePath = Path.GetFullPath(project.SourcePath);
        var projectRoot = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            ProjectSession = WccEditorSessionContext.Empty;
            CampaignEditor.SetReadOnly(true);
            RaiseScenarioWorkspaceStateChanged();
            return;
        }

        var access = ResolveEditorAccess(sourcePath);
        ProjectSession = new WccEditorSessionContext(
            WccEditorSessionScope.Project,
            access,
            projectRoot,
            access == WccEditorAccessMode.ReadOnly
                ? $"Project inspection (read-only): {Path.GetFileName(projectRoot)}"
                : $"Project: {Path.GetFileName(projectRoot)}",
            access == WccEditorAccessMode.Writable ? projectRoot : null);
        CampaignEditor.SetReadOnly(ProjectSession.IsReadOnly);
        ConfigureScenarioEvaluationPlan(project);

        _showLooseScenarioWorkspace = false;
        _showLooseUnitWorkspace = false;
        SelectedCampaignScenarioFile = CampaignEditor.SelectedScenarioFile;

        if (SelectedCampaignScenarioFile is { } selected)
        {
            LoadScenarioDocument(
                selected.Asset,
                CreateProjectEditorSession($"Project scenario: {selected.DisplayName}"));
        }
        else
        {
            ScenarioEditor.LoadFromText("(no project scenario)", string.Empty);
            ScenarioSession = WccEditorSessionContext.Empty;
            ClearCurrentScenarioDirty();
        }

        RefreshProjectUnitWorkspaceEntries();
        var firstProjectUnit = ProjectOwnedUnitEntries.FirstOrDefault();
        if (firstProjectUnit is not null)
        {
            SelectedProjectUnitEntry = firstProjectUnit;
            LoadOwnedProjectUnitDocument(firstProjectUnit);
        }
        else
        {
            UnitEditor.LoadFromText("(no project unit)", string.Empty);
            UnitSession = WccEditorSessionContext.Empty;
            ClearCurrentUnitDirty();
        }

        RaiseScenarioWorkspaceStateChanged();
        RaiseUnitWorkspaceStateChanged();
    }

    private void ConfigureScenarioEvaluationPlan(WccProject project)
    {
        if (CampaignEditor.RequiresSelectedCampaignAdapter && CampaignEditor.SelectedCampaignEntry?.Plan is { } entryPlan &&
            entryPlan.CampaignMainTextHash == WccContentHash.ComputeText(project.CampaignRawTextSnapshot ?? project.Campaign?.OriginalText ?? ""))
        {
            ScenarioEditor.SetEvaluationPlan(entryPlan);
            _campaignFieldEvaluationPlan = entryPlan;
            ConfigureCampaignEntryInspection();
            ConfigureCampaignFieldHost();
            return;
        }
        // The evaluation plan must bind to the loaded/saved manifest revision.
        // Rendering here can normalize untouched authored tokens such as rank=010.
        var mainText = project.CampaignRawTextSnapshot ?? project.Campaign?.OriginalText;
        if (string.IsNullOrWhiteSpace(mainText) ||
            string.IsNullOrWhiteSpace(project.SourcePath))
        {
            ScenarioEditor.SetEvaluationPlan(null);
            _campaignFieldEvaluationPlan = null;
            ConfigureCampaignFieldHost();
            return;
        }

        var compatibility = CampaignEditor.CheckedCampaignEntryTargetCompatibility ??
                            _currentProjectMetadata?.Compatibility?.TargetWesnothVersion ??
                            FindActiveWesnothInstallationProfile()?.CompatibilityTarget;
        var plans = WmlCampaignEvaluationPlanDiscovery.Discover(
            mainText,
            project.SourcePath,
            compatibility);
        var campaignId = project.Campaign?.Campaign.Id;
        var selected = plans.FirstOrDefault(x =>
                           string.Equals(x.CampaignId, campaignId, StringComparison.Ordinal))
                       ?? plans.FirstOrDefault();
        ScenarioEditor.SetEvaluationPlan(selected);
        _campaignFieldEvaluationPlan = selected;
        ConfigureCampaignFieldHost();
    }

    private WccEditorSessionContext CreateProjectEditorSession(string label) => new(
        WccEditorSessionScope.Project,
        ProjectSession.AccessMode,
        ProjectSession.RootPath,
        ProjectSession.IsReadOnly ? $"{label} (read-only)" : label,
        ProjectSession.AssetRootPath);

    private WccEditorSessionContext CreateLooseFileEditorSession(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var access = ResolveEditorAccess(fullPath);
        return new WccEditorSessionContext(
            WccEditorSessionScope.LooseFile,
            access,
            Path.GetDirectoryName(fullPath),
            access == WccEditorAccessMode.ReadOnly
                ? $"Loose file inspection (read-only): {Path.GetFileName(fullPath)}"
                : $"Loose file: {Path.GetFileName(fullPath)}");
    }

    private WccEditorAccessMode ResolveEditorAccess(string sourcePath)
    {
        foreach (var profile in _settings.WesnothInstallationProfiles)
        {
            var protectedRoots = new[]
            {
                profile.InstallPath,
                profile.LastDetectedInstallation?.InstallRoot
            };
            if (protectedRoots.Any(root =>
                    !string.IsNullOrWhiteSpace(root) && IsPathInsideRoot(root, sourcePath)))
            {
                return WccEditorAccessMode.ReadOnly;
            }
        }

        return WccEditorAccessMode.Writable;
    }

    public bool CanSaveScenarioCopyTo(string path)
    {
        if (ResolveEditorAccess(path) == WccEditorAccessMode.ReadOnly)
        {
            Status = "Choose a new file outside the protected Wesnoth installation.";
            return false;
        }
        return ScenarioEditor.CanSaveScenarioCopyTo(path);
    }

    private void LoadScenarioDocument(ScenarioFileAsset asset, WccEditorSessionContext session)
    {
        ScenarioSession = session;
        ConfigureEditorsForActiveWesnothInstallation();
        var documentId = session.Scope == WccEditorSessionScope.Project
            ? FindCurrentProjectScenarioRow(asset)?.ScenarioDocumentInternalId : null;
        ScenarioEditor.LoadFromExistingScenarioAsset(asset, documentId);
        ConfigureCampaignEntryInspection();
        ClearCurrentScenarioDirty();
        OnPropertyChanged(nameof(HasCurrentScenarioDocument));
        OnPropertyChanged(nameof(HasNoCurrentScenarioDocument));
        OnPropertyChanged(nameof(IsCurrentScenarioReadOnly));
        OnPropertyChanged(nameof(CanSaveCurrentContext));
    }

    private void RaiseScenarioWorkspaceStateChanged()
    {
        OnPropertyChanged(nameof(HasOpenCampaignProject));
        OnPropertyChanged(nameof(IsProjectScenarioWorkspace));
        OnPropertyChanged(nameof(IsLooseScenarioWorkspace));
        OnPropertyChanged(nameof(CanReturnToProjectScenarios));
        OnPropertyChanged(nameof(CanModifyProjectScenarios));
        OnPropertyChanged(nameof(ProjectSessionLabel));
    }

    private void RaiseUnitWorkspaceStateChanged()
    {
        OnPropertyChanged(nameof(HasOpenCampaignProject));
        OnPropertyChanged(nameof(IsProjectUnitWorkspace));
        OnPropertyChanged(nameof(IsLooseUnitWorkspace));
        OnPropertyChanged(nameof(CanReturnToProjectUnits));
        OnPropertyChanged(nameof(CanModifyProjectUnits));
        OnPropertyChanged(nameof(ProjectSessionLabel));
        OnPropertyChanged(nameof(HasCurrentUnitDocument));
        OnPropertyChanged(nameof(HasNoCurrentUnitDocument));
        OnPropertyChanged(nameof(IsCurrentUnitReadOnly));
        OnPropertyChanged(nameof(IsUnresolvedProjectUnitSelected));
        OnPropertyChanged(nameof(IsProjectUnitEditorVisible));
        OnPropertyChanged(nameof(IsUnitWorkspaceEmptyMessageVisible));
    }

    private bool UnloadCurrentProjectForLooseFile()
    {
        var project = CampaignEditor.CurrentProject;
        if (project?.Kind != WccProjectKind.Campaign)
            return true;

        if (!SaveCurrentEditorsIfDirty(refreshCatalogEntries: true))
            return false;

        var unitWasFromProject = UnitEditor.CurrentUnitAsset is { } currentUnit &&
                                 project.ProjectUnitFiles.Any(asset => ReferenceEquals(asset, currentUnit));

        CampaignEditor.UnloadProject();

        if (unitWasFromProject)
        {
            UnitEditor.LoadFromText("(no unit)", string.Empty);
            ClearCurrentUnitDirty();
            OnPropertyChanged(nameof(CanAddCurrentUnitToLibrary));
            OnPropertyChanged(nameof(CanCloneCurrentUnit));
            OnPropertyChanged(nameof(CanDeleteCurrentUnit));
        }

        Status = "Closed the campaign project before opening a loose file.";
        return true;
    }


    // ---------------------------------------------------------------------
    // Settings and import entry points
    // ---------------------------------------------------------------------
    public Task RefreshConfiguredLibraryIndexAsync(CancellationToken cancellationToken = default) =>
        StartLibraryRefreshAsync(null, cancellationToken);
    private async Task RefreshConfiguredLibraryIndexCoreAsync(CancellationToken cancellationToken)
    {
        var refreshProfile = ActiveWesnothInstallationProfile;
        if (refreshProfile is null || string.IsNullOrWhiteSpace(refreshProfile.InstallPath))
        {
            LibraryIndexStatus = "Library index idle: no Wesnoth installation configured.";
            return;
        }

        var refreshProfileId = refreshProfile.Id;

        var installationDetection = await ReadWesnothInstallationForRefreshAsync(refreshProfile, cancellationToken);
        if (installationDetection.Identity is null)
        {
            LibraryIndexStatus =
                $"Library index idle: {installationDetection.Error ?? "invalid Wesnoth installation."}";
            return;
        }

        IsLibraryIndexRefreshing = true;
        LibraryIndexStatus = "Preparing the Wesnoth package index...";

        try
        {
            var installation = installationDetection.Identity;
            var addonsPath = refreshProfile.AddonsPath;
            ConfigureLibraryAnalysisReuse(refreshProfile, installation);
            var additionalAddonSources = CampaignDependencySourcePaths(refreshProfile).ToArray();
            var additionalSourceSignature = DependencySourceSignature(refreshProfile);
            var workingProject = CreateCurrentWorkingProjectDescriptor();
            var refreshingProject = CampaignEditor.CurrentProject;
            var refreshingMetadata = _currentProjectMetadata;
            var refreshingSession = ProjectSession;
            bool HasCurrentRefreshContext() => ReferenceEquals(refreshingProject, CampaignEditor.CurrentProject) &&
                ReferenceEquals(refreshingMetadata, _currentProjectMetadata) && refreshingSession == ProjectSession &&
                ReferenceEquals(installation, _activeWesnothInstallation) &&
                additionalSourceSignature == DependencySourceSignature(refreshProfile) &&
                refreshProfileId == FindActiveWesnothInstallationProfile()?.Id;
            var workingProjectCorePin = workingProject is not null &&
                                        PathsEqual(
                                            _currentProjectMetadataRoot,
                                            workingProject.RootDirectory)
                ? _currentProjectMetadata?.Compatibility?.CoreRevision
                : null;
            var store = _libraryIndexStore;
            var manifest = await store.LoadManifestAsync(cancellationToken);
            var mainlinePlan = await Task.Run(
                () => WccWesnothInstallPackagePlanner.Build(manifest, installation),
                cancellationToken);
            var managedPlan = await Task.Run(
                () => WccManagedLibraryPackagePlanner.Build(
                    mainlinePlan.Manifest,
                    _unitCatalogLibraryRoot,
                    mainlinePlan.DistributionRevisionId),
                cancellationToken);
            var addonPlan = await Task.Run(
                () => WccAddonPackagePlanner.Build(
                    managedPlan.Manifest,
                    addonsPath,
                    mainlinePlan.DistributionPackageId,
                    mainlinePlan.DistributionRevisionId,
                    additionalAddonSources),
                cancellationToken);
            var projectCoreRevisionId = ResolveCurrentProjectCoreRevisionForPlan(
                addonPlan.Manifest,
                mainlinePlan.DistributionRevisionId,
                workingProjectCorePin);
            var hasUnavailableProjectCorePin =
                workingProject is not null &&
                workingProjectCorePin is not null &&
                projectCoreRevisionId is null;
            var workingProjectDependencyRevisionIds = ResolveProjectDependenciesForCorePin(
                addonPlan.Manifest, workingProject, projectCoreRevisionId, workingProjectCorePin);
            var projectPlan = await Task.Run(
                () => WccWorkingProjectPackagePlanner.Build(
                    addonPlan.Manifest,
                    workingProject,
                    projectCoreRevisionId,
                    workingProjectDependencyRevisionIds),
                cancellationToken);

            await store.SaveManifestAsync(projectPlan.Manifest, cancellationToken);
            var removedSourceFileIds = mainlinePlan.RemovedSourceFileIds
                .Concat(managedPlan.RemovedSourceFileIds)
                .Concat(addonPlan.RemovedSourceFileIds)
                .Concat(projectPlan.RemovedSourceFileIds)
                .Distinct()
                .ToArray();
            foreach (var sourceFileId in removedSourceFileIds)
                await store.DeleteFileAnalysisAsync(sourceFileId, cancellationToken);

            using var progress = new WccLibraryProgressRelay(
                action => Dispatcher.UIThread.Post(action),
                value => LibraryIndexStatus = FormatLibraryProgress(value));
            var coordinator = new WccLibraryCoordinator(store);
            var retainedRevisionIds = projectPlan.Manifest.PackageRevisions
                .Select(x => x.Id)
                .ToHashSet();
            var refreshRequests = mainlinePlan.RefreshRequests
                .Concat(managedPlan.RefreshRequest is null
                    ? Array.Empty<WccPackageRefreshRequest>()
                    : new[] { managedPlan.RefreshRequest })
                .Concat(addonPlan.RefreshRequests)
                .Concat(projectPlan.RefreshRequest is null
                    ? Array.Empty<WccPackageRefreshRequest>()
                    : new[] { projectPlan.RefreshRequest })
                .Where(x => retainedRevisionIds.Contains(x.PackageRevisionId))
                .ToArray();
            var result = await coordinator.RefreshPackagesAsync(
                refreshRequests,
                progress,
                cancellationToken,
                addonPlan.Diagnostics);
            if (!HasCurrentRefreshContext()) { RequestLibraryRefreshAgain(); return; }

            // A fresh Library only learns its complete Core fingerprint during indexing.
            // Finish an identical rebuilt pin's project closure before publishing editors.
            result = await CompleteRebuiltProjectCoreIndexAsync(result, coordinator, store,
                workingProject, workingProjectCorePin, progress, addonPlan.Diagnostics, cancellationToken);
            if (!HasCurrentRefreshContext()) { RequestLibraryRefreshAgain(); return; }
            var dependencyCore = ResolveCurrentProjectCoreRevisionForPlan(result.Snapshot.Manifest,
                mainlinePlan.DistributionRevisionId, workingProjectCorePin);
            result = await CompleteCampaignDependencySourceIndexAsync(result, coordinator, store,
                workingProject, dependencyCore, addonsPath, progress, addonPlan.Diagnostics,
                HasCurrentRefreshContext, cancellationToken);
            if (!HasCurrentRefreshContext()) { RequestLibraryRefreshAgain(); return; }
            hasUnavailableProjectCorePin = workingProject is not null && workingProjectCorePin is not null &&
                !WccProjectCorePinResolver.Resolve(result.Snapshot.Manifest, workingProjectCorePin).IsResolved;

            cancellationToken.ThrowIfCancellationRequested();
            progress.Complete();
            LibraryIndexStatus = "Applying refreshed library snapshot...";
            UpdateDetectedCoreFingerprint(
                refreshProfileId,
                result.Snapshot.RevisionsById.TryGetValue(
                    mainlinePlan.DistributionRevisionId,
                    out var coreRevision)
                    ? coreRevision.ContentFingerprint
                    : null);
            var nextActiveRevisionIds = WccStandaloneLibraryRevisionScope.GetVisibleRevisionIds(
                result.Snapshot,
                mainlinePlan.DistributionPackageId, addonsPath);
            var nextAssetRegistry = await Task.Run(
                () => WccLibraryAssetRegistryProjection.Build(
                    result.Snapshot,
                    nextActiveRevisionIds),
                cancellationToken);
            using var scenarioPreviewRefresh = ScenarioEditor.DeferPreviewRefresh();
            BeginLibraryPickerProjectionDeferral();
            try
            {
                LibrarySnapshot = result.Snapshot;
                ResetUnitDisplayScopes();
                ApplyActiveLibraryView(nextActiveRevisionIds, nextAssetRegistry);
                ConfigureEditorsForActiveWesnothInstallation();
                RestoreLibraryPickerProjection();
                await ReconcileCurrentProjectBindingsAsync(result.Snapshot, cancellationToken);
                RaiseProjectCompatibilityPropertiesChanged();

                RefreshUnitCatalogEntries();
                RefreshProjectUnitWorkspaceEntries();
            }
            finally
            {
                EndLibraryPickerProjectionDeferral();
            }
            OnPropertyChanged(nameof(LibrarySnapshot));
            LibrarySnapshotChanged?.Invoke(this, EventArgs.Empty);
            var addonDependencyCycleCount = addonPlan.Diagnostics.Count(diagnostic =>
                diagnostic.Code == "addon.dependency_cycle");
            var addonProblemCount = addonPlan.Diagnostics.Count(diagnostic =>
                diagnostic.Severity is WccDiagnosticSeverity.Warning or WccDiagnosticSeverity.Error);
            LibraryIndexStatus =
                $"Library ready: {LibraryPackageCount} package(s), {LibraryEntityCount} entities, " +
                $"{LibraryReferenceCount} references; {result.AnalyzedFileCount} changed file(s), " +
                $"{result.CacheHitCount} cache hit(s)." +
                (addonDependencyCycleCount > 0
                    ? $" Retained {addonDependencyCycleCount} add-on dependency cycle(s)."
                    : string.Empty) +
                (addonProblemCount > 0
                    ? $" Detected {addonProblemCount} add-on metadata/dependency warning(s)."
                    : string.Empty) +
                (hasUnavailableProjectCorePin
                    ? " The project's pinned Core revision is unavailable; WCC did not substitute the active Core."
                    : string.Empty);
        }
        catch (OperationCanceledException)
        {
            LibraryIndexStatus = "Library refresh cancelled; the previous snapshot remains available.";
        }
        catch (Exception ex)
        {
            LibraryIndexStatus = $"Library refresh failed: {ex.Message}";
        }
        finally
        {
            IsLibraryIndexRefreshing = false;
            ConfigureCampaignDependencySourceHost();
        }
    }

    private void ConfigureLibraryAnalysisReuse(WccWesnothInstallationProfile profile,
        WesnothInstallationIdentity installation)
    {
        var configuration = (profile.Id, installation.InstallRoot, installation.CoreDataRoot,
            installation.CampaignsRoot, installation.SchemaRoot, installation.ExactVersion,
            installation.CompatibilityTarget, profile.AddonsPath);
        if (_libraryAnalysisConfiguration == configuration) return;
        // Keep the admitted base across project/manual-source changes. A configured
        // installation/profile change starts a fresh bounded admission set instead.
        _libraryIndexStore.ClearFileAnalysisCache();
        _libraryAnalysisConfiguration = configuration;
    }
    private WccWorkingProjectDescriptor? CreateCurrentWorkingProjectDescriptor()
    {
        var project = CampaignEditor.CurrentProject;
        if (!ProjectSession.CanWrite ||
            project?.Kind != WccProjectKind.Campaign ||
            string.IsNullOrWhiteSpace(project.SourcePath))
        {
            return null;
        }

        var sourcePath = Path.GetFullPath(project.SourcePath);
        var root = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        var name = !string.IsNullOrWhiteSpace(CampaignEditor.CampaignId)
            ? WmlDisplayStringNormalizer.Normalize(CampaignEditor.CampaignId)
              ?? CampaignEditor.CampaignId!.Trim()
            : Path.GetFileName(root);
        var stableProjectId = _currentProjectMetadata is not null &&
                              !string.IsNullOrWhiteSpace(_currentProjectMetadataRoot) &&
                              PathsEqual(_currentProjectMetadataRoot, root)
            ? _currentProjectMetadata.ProjectId.Value
            : (Guid?)null;
        return new WccWorkingProjectDescriptor(
            name,
            root,
            WccPackageContentKinds.Project,
            stableProjectId);
    }

    private async Task HandleCurrentProjectChangedAsync()
    {
        // Library refresh updates configuration without waiting for campaign
        // projection. Release this batch before any later projection/membership wait.
        using var projectionBatch = CampaignEditor.DeferCampaignFieldProjection();
        var project = CampaignEditor.CurrentProject;
        var sourcePath = project?.SourcePath;
        var metadataLoad = QueueCurrentProjectMetadataLoad();
        var metadataGeneration = _projectMetadataLoadGeneration;
        await metadataLoad;
        if (metadataGeneration != _projectMetadataLoadGeneration || !ReferenceEquals(project, CampaignEditor.CurrentProject) ||
            sourcePath != project?.SourcePath) return;
        await RefreshConfiguredLibraryIndexAsync();
    }

    private void InvalidateProjectMetadataForCurrentProjectChange()
    {
        // Invalidate in-flight reads before session setup can load a scenario and
        // request an exact preview. QueueCurrentProjectMetadataLoad establishes
        // the new root after ConfigureSessionForCurrentProject has set its access.
        Interlocked.Increment(ref _projectMetadataLoadGeneration);
        ApplyProjectMetadata(
            projectRoot: null,
            metadata: null,
            metadataLoadPending: true);
    }

    private Task QueueCurrentProjectMetadataLoad()
    {
        var generation = Interlocked.Increment(ref _projectMetadataLoadGeneration);
        var descriptor = CreateCurrentWorkingProjectDescriptor();
        if (descriptor is null)
        {
            ApplyProjectMetadata(projectRoot: null, metadata: null);
            return Task.CompletedTask;
        }

        // CurrentProject changes are observed before its metadata read completes.
        // Clear the previous project's metadata/profile synchronously so an exact
        // preview started in this window cannot run against the old Core pin.
        ApplyProjectMetadata(
            descriptor.RootDirectory,
            metadata: null,
            metadataLoadPending: true);
        return LoadCurrentProjectMetadataAsync(
            descriptor.RootDirectory,
            generation);
    }

    private async Task LoadCurrentProjectMetadataAsync(string projectRoot, int generation)
    {
        try
        {
            var store = new JsonWccProjectMetadataStore(projectRoot);
            var metadata = await store.LoadOrCreateAsync();
            if (generation != _projectMetadataLoadGeneration)
                return;

            var currentDescriptor = CreateCurrentWorkingProjectDescriptor();
            if (currentDescriptor is null ||
                !PathsEqual(currentDescriptor.RootDirectory, projectRoot))
            {
                return;
            }

            ApplyProjectMetadata(projectRoot, metadata);
        }
        catch (Exception ex)
        {
            if (generation == _projectMetadataLoadGeneration)
            {
                ApplyProjectMetadata(projectRoot, metadata: null);
                Status = $"Could not load WCC project metadata: {ex.Message}";
            }
        }
    }

    private void ApplyProjectMetadata(
        string? projectRoot,
        WccProjectMetadata? metadata,
        bool metadataLoadPending = false)
    {
        using var previewBatch = ScenarioEditor.DeferPreviewRefresh();
        using var projectionBatch = CampaignEditor.DeferCampaignFieldProjection();
        var sameUnitResourceRoot = projectRoot is null
            ? _currentProjectMetadataRoot is null
            : PathsEqual(_currentProjectMetadataRoot, projectRoot);
        var unitResourceContextChanged = !sameUnitResourceRoot ||
            !Equals(_currentProjectMetadata?.Compatibility?.CoreRevision, metadata?.Compatibility?.CoreRevision) ||
            !string.Equals(_currentProjectMetadata?.Compatibility?.TargetWesnothVersion,
                metadata?.Compatibility?.TargetWesnothVersion, StringComparison.Ordinal);
        ScenarioEditor.CancelBackgroundOperations();
        _isCurrentProjectMetadataLoadPending = metadataLoadPending;
        _currentProjectMetadataRoot = projectRoot;
        _currentProjectMetadata = metadata;
        if (unitResourceContextChanged) RetireCampaignUnitDisplayScopes();
        UnitEditor.SetProjectMetadata(projectRoot, metadata);
        ScenarioEditor.SetProjectMetadata(projectRoot, metadata);
        ConfigureEditorsForActiveWesnothInstallation();
        RestoreLibraryPickerProjection();
        if (CampaignEditor.CurrentProject is { Kind: WccProjectKind.Campaign } project)
            ConfigureScenarioEvaluationPlan(project);
        RaiseProjectCompatibilityPropertiesChanged();
    }

    public async Task PinCurrentProjectToActiveWesnothAsync()
    {
        var metadata = _currentProjectMetadata;
        var projectRoot = _currentProjectMetadataRoot;
        var installation = _activeWesnothInstallation;
        var snapshot = LibrarySnapshot;
        if (metadata is null || metadata.Compatibility is not null ||
            string.IsNullOrWhiteSpace(projectRoot) || !ProjectSession.CanWrite ||
            installation is null || snapshot is null)
        {
            Status = "The current project cannot be pinned in its present state.";
            return;
        }

        if (!WccCompatibilityPolicy.CanCreateFor(installation.CompatibilityTarget))
        {
            Status =
                $"This WCC release creates projects for Wesnoth {WccCompatibilityPolicy.CurrentAuthoringTarget}. " +
                $"The selected {installation.CompatibilityTarget} profile is available as a migration and validation runtime.";
            return;
        }

        if (!TryFindActiveCoreRevision(snapshot, out var corePackage, out var coreRevision) ||
            string.IsNullOrWhiteSpace(coreRevision.ContentFingerprint))
        {
            Status = "Refresh the active installation's package index before pinning this project.";
            return;
        }

        var compatibility = new WccProjectCompatibility
        {
            TargetWesnothVersion = installation.CompatibilityTarget,
            ImportedFromWesnothVersion = installation.ExactVersion,
            CoreRevision = new WccProjectCoreRevisionPin(
                corePackage.Id,
                coreRevision.Id,
                coreRevision.ContentFingerprint,
                installation.ExactVersion,
                installation.CompatibilityTarget)
        };
        var updated = new WccProjectMetadata
        {
            FormatVersion = metadata.FormatVersion,
            ProjectId = metadata.ProjectId,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Compatibility = compatibility,
            Authoring = metadata.Authoring,
            EntityBindings = metadata.EntityBindings.ToList()
        };

        await _projectMetadataWriteLock.WaitAsync();
        try
        {
            if (!PathsEqual(_currentProjectMetadataRoot, projectRoot) ||
                _currentProjectMetadata?.Compatibility is not null)
            {
                Status = "The current project changed before its compatibility pin could be saved.";
                return;
            }

            var store = new JsonWccProjectMetadataStore(projectRoot);
            await store.SaveAsync(updated);
            ApplyProjectMetadata(projectRoot, updated);
            Status =
                $"Pinned the project to Wesnoth {installation.CompatibilityTarget} using exact " +
                $"Core build {installation.ExactVersion}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not pin the project compatibility target: {ex.Message}";
            return;
        }
        finally
        {
            _projectMetadataWriteLock.Release();
        }

        await RefreshConfiguredLibraryIndexAsync();
    }

    private void RaiseProjectCompatibilityPropertiesChanged()
    {
        OnPropertyChanged(nameof(CurrentProjectCompatibilityStatus));
        OnPropertyChanged(nameof(CurrentProjectCorePinStatus));
        OnPropertyChanged(nameof(CanPinCurrentProjectToActiveWesnoth));
    }

    private void QueueProjectLibraryBindingPersistence(
        ProjectLibraryBindingSelectionEventArgs selection)
    {
        var descriptor = CreateCurrentWorkingProjectDescriptor();
        var snapshot = LibrarySnapshot;
        if (descriptor is null || snapshot is null)
            return;

        var workingRevisionId = FindCurrentWorkingProjectRevisionId(snapshot);
        if (workingRevisionId is null)
            return;

        _ = PersistProjectLibraryBindingSelectionAsync(
            descriptor.RootDirectory,
            snapshot,
            workingRevisionId.Value,
            selection);
    }

    private async Task PersistProjectLibraryBindingSelectionAsync(
        string projectRoot,
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId workingRevisionId,
        ProjectLibraryBindingSelectionEventArgs selection)
    {
        await _projectMetadataWriteLock.WaitAsync();
        try
        {
            var visibleRevisionIds = WccProjectAuthoringRevisionScope.GetVisibleRevisionIds(
                snapshot,
                workingRevisionId);
            var targets = new List<WccProjectBindingTarget>();

            foreach (var selected in selection.Selections)
            {
                if (selected.LibraryEntityId is not { } entityId)
                {
                    throw new InvalidOperationException(
                        $"'{selected.EmittedValue}' has no durable library entity identity.");
                }
                if (!snapshot.EntitiesById.TryGetValue(entityId, out var entity) ||
                    !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile) ||
                    !snapshot.RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out var revision) ||
                    !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package))
                {
                    throw new InvalidOperationException(
                        $"The selected library entity '{selected.EmittedValue}' is no longer available.");
                }
                if (!visibleRevisionIds.Contains(revision.Id))
                {
                    throw new InvalidOperationException(
                        $"Package '{package.Name}' is not a dependency of the current project.");
                }

                targets.Add(new WccProjectBindingTarget(
                    selected.EmittedValue,
                    entity.Id,
                    entity.EntityType,
                    entity.ExportSymbol,
                    entity.ContentFingerprint,
                    package.Id,
                    revision.Id,
                    package.Name,
                    package.SourceKind,
                    package.ContentKind,
                    revision.ContentFingerprint));
            }

            var relativeSourcePath = JsonWccProjectMetadataStore.GetRelativeSourcePath(
                projectRoot,
                selection.SourcePath);
            var owner = new WccProjectBindingOwner(
                relativeSourcePath,
                selection.OwnerEntityType,
                selection.OwnerExportSymbol,
                selection.OwnerEntityOrdinal,
                selection.FieldPath);
            var store = new JsonWccProjectMetadataStore(projectRoot);
            var updatedMetadata = await store.ReplaceFieldBindingsAsync(owner, targets);
            var currentDescriptor = CreateCurrentWorkingProjectDescriptor();
            if (currentDescriptor is not null &&
                PathsEqual(currentDescriptor.RootDirectory, projectRoot))
            {
                ApplyProjectMetadata(projectRoot, updatedMetadata);
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not save the project library binding: {ex.Message}";
        }
        finally
        {
            _projectMetadataWriteLock.Release();
        }
    }

    private async Task ReconcileCurrentProjectBindingsAsync(
        WccLibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var metadata = _currentProjectMetadata;
        var projectRoot = _currentProjectMetadataRoot;
        if (metadata is null || string.IsNullOrWhiteSpace(projectRoot))
            return;

        var workingRevisionId = FindCurrentWorkingProjectRevisionId(snapshot);
        if (workingRevisionId is null)
            return;

        await _projectMetadataWriteLock.WaitAsync(cancellationToken);
        try
        {
            var visibleRevisionIds = WccProjectAuthoringRevisionScope.GetVisibleRevisionIds(
                snapshot,
                workingRevisionId.Value);
            var reconciliation = WccProjectBindingReconciler.Reconcile(
                metadata,
                snapshot,
                visibleRevisionIds);
            if (!reconciliation.HasChanges)
                return;

            var store = new JsonWccProjectMetadataStore(projectRoot);
            await store.SaveAsync(reconciliation.Metadata, cancellationToken);
            ApplyProjectMetadata(projectRoot, reconciliation.Metadata);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Status = $"Could not reconcile WCC project bindings: {ex.Message}";
        }
        finally
        {
            _projectMetadataWriteLock.Release();
        }
    }

    private void RestoreLibraryPickerProjection()
    {
        if (_libraryPickerProjectionDeferralDepth > 0)
        {
            _libraryPickerProjectionRestorePending = true;
            return;
        }

        RestoreLibraryPickerProjectionCore();
    }

    private void RestoreLibraryPickerProjectionCore()
    {
        var snapshot = LibrarySnapshot;
        if (snapshot is null)
            return;

        var projectRevisionId = FindCurrentProjectRevisionId(snapshot);
        var projectVisibleRevisionIds = projectRevisionId is { } revisionId
            ? WccProjectAuthoringRevisionScope.GetVisibleRevisionIds(snapshot, revisionId)
            : new HashSet<WccPackageRevisionId>();
        var unitUsesProjectScope = UnitSession.Scope == WccEditorSessionScope.Project &&
                                   SelectedTabIndex == 2;
        var scenarioUsesProjectScope = ScenarioSession.Scope == WccEditorSessionScope.Project;
        var scenarioRuntimeProfile = ScenarioSession.Scope == WccEditorSessionScope.Project
            ? ResolvePinnedProjectInstallationProfile()
            : FindActiveWesnothInstallationProfile();
        var scenarioRuntimeRevisionIds =
            TryFindProfileDistributionPackage(
                snapshot,
                scenarioRuntimeProfile,
                out var scenarioDistributionPackage)
                ? WccLibraryProfileRevisionScope.GetVisibleRuntimeRevisionIds(
                    snapshot,
                    scenarioDistributionPackage.Id).ToHashSet()
                : new HashSet<WccPackageRevisionId>();
        if (scenarioUsesProjectScope &&
            FindCurrentWorkingProjectRevisionId(snapshot) is { } workingRevisionId &&
            IsSelectedRuntimeAddonWorkingOverlay(
                snapshot,
                workingRevisionId,
                scenarioRuntimeRevisionIds,
                scenarioDistributionPackage?.CurrentRevisionId,
                scenarioRuntimeProfile?.AddonsPath))
        {
            // Opening an installed add-on creates a writable working-project overlay
            // and deliberately removes its duplicate read-only package. Admit only
            // that current overlay to exact verification; ordinary projects and
            // projects outside this profile's add-ons root remain excluded.
            scenarioRuntimeRevisionIds.Add(workingRevisionId);
        }

        UnitEditor.SetLibraryPickerCatalogs(
            snapshot,
            unitUsesProjectScope ? projectVisibleRevisionIds : _activeLibraryRevisionIds);
        LibraryUnitEditor.SetLibraryPickerCatalogs(snapshot, _activeLibraryRevisionIds);
        var scenarioVisibleRevisions = scenarioUsesProjectScope ? projectVisibleRevisionIds : _activeLibraryRevisionIds;
        var scenarioRootRevision = scenarioUsesProjectScope ? projectRevisionId : null;
        // Entering a preview deferral revokes the current evidence immediately.
        // Pure editor-tab changes keep the already-bound source inputs intact.
        if (!ScenarioEditor.HasCurrentScenarioCatalogBinding(snapshot, scenarioVisibleRevisions,
                scenarioRuntimeRevisionIds, scenarioRootRevision))
        {
            using var scenarioPreviewRefresh = ScenarioEditor.DeferPreviewRefresh();
            ScenarioEditor.SetLibraryUnitTypeCatalog(snapshot, scenarioVisibleRevisions, scenarioRuntimeRevisionIds);
            ScenarioEditor.SetStaticEvaluationRootRevision(snapshot, scenarioRootRevision);
        }
        ConfigureCampaignFieldHost();
    }

    private bool IsSelectedRuntimeAddonWorkingOverlay(
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId workingRevisionId,
        IReadOnlySet<WccPackageRevisionId> runtimeRevisionIds,
        WccPackageRevisionId? distributionRevisionId,
        string? addonsRoot)
    {
        if (string.IsNullOrWhiteSpace(addonsRoot) ||
            distributionRevisionId is not { } selectedDistributionRevisionId ||
            !snapshot.RevisionsById.ContainsKey(selectedDistributionRevisionId) ||
            !runtimeRevisionIds.Contains(selectedDistributionRevisionId) ||
            !snapshot.RevisionsById.TryGetValue(workingRevisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) ||
            !package.IsAvailable ||
            package.SourceKind != WccPackageSourceKinds.WccProject ||
            package.StorageMode != WccPackageStorageMode.WorkingCopy ||
            package.CurrentRevisionId != workingRevisionId ||
            string.IsNullOrWhiteSpace(package.SourceLocation) ||
            string.IsNullOrWhiteSpace(ScenarioSession.RootPath) ||
            !PathsEqual(package.SourceLocation, ScenarioSession.RootPath) ||
            !IsPathInsideRoot(addonsRoot, package.SourceLocation))
        {
            return false;
        }

        var visibleRevisionIds = WccLibraryRevisionVisibility
            .GetVisibleRevisionIds(snapshot, workingRevisionId);
        if (!visibleRevisionIds.Contains(selectedDistributionRevisionId))
            return false;

        var dependencyRevisionIds = visibleRevisionIds
            .Where(candidate => candidate != workingRevisionId)
            .ToArray();
        var verifiedShadowAliasIds = dependencyRevisionIds
            .Where(candidate => WccWorkingAddonOverlayRevisionAlias.IsVerified(
                snapshot,
                workingRevisionId,
                candidate,
                selectedDistributionRevisionId))
            .ToArray();
        if (verifiedShadowAliasIds.Length > 1)
            return false;

        var verifiedShadowAliasIdSet = verifiedShadowAliasIds.ToHashSet();
        var runtimeDependencyRevisionIds = dependencyRevisionIds
            .Where(candidate => !verifiedShadowAliasIdSet.Contains(candidate))
            .ToArray();
        return runtimeDependencyRevisionIds.Length > 0 &&
               runtimeDependencyRevisionIds.All(runtimeRevisionIds.Contains);
    }

    private void BeginLibraryPickerProjectionDeferral()
        => _libraryPickerProjectionDeferralDepth++;

    private void EndLibraryPickerProjectionDeferral()
    {
        if (_libraryPickerProjectionDeferralDepth <= 0)
            throw new InvalidOperationException("Library picker projection deferral is not active.");

        _libraryPickerProjectionDeferralDepth--;
        if (_libraryPickerProjectionDeferralDepth > 0 || !_libraryPickerProjectionRestorePending)
            return;

        _libraryPickerProjectionRestorePending = false;
        RestoreLibraryPickerProjectionCore();
    }

    private WccPackageRevisionId? FindCurrentWorkingProjectRevisionId(WccLibrarySnapshot snapshot)
    {
        var descriptor = CreateCurrentWorkingProjectDescriptor();
        if (descriptor is null)
            return null;

        var package = snapshot.PackagesById.Values.SingleOrDefault(x =>
            x.SourceKind == WccPackageSourceKinds.WccProject &&
            x.StorageMode == WccPackageStorageMode.WorkingCopy &&
            PathsEqual(x.SourceLocation, descriptor.RootDirectory));
        if (package is null)
            return null;

        if (package.CurrentRevisionId is { } currentRevisionId &&
            snapshot.RevisionsById.TryGetValue(currentRevisionId, out var currentRevision) &&
            currentRevision.PackageId == package.Id)
        {
            return currentRevisionId;
        }

        // A legacy root without an explicit selection must be unambiguous.
        var legacyRevisions = snapshot.RevisionsById.Values
            .Where(x => x.PackageId == package.Id)
            .Take(2).ToArray();
        return package.CurrentRevisionId is null && legacyRevisions.Length == 1
            ? legacyRevisions[0].Id
            : null;
    }

    private WccPackageRevisionId? FindCurrentProjectRevisionId(WccLibrarySnapshot snapshot)
    {
        var workingRevisionId = FindCurrentWorkingProjectRevisionId(snapshot);
        if (workingRevisionId is not null)
            return workingRevisionId;

        var projectRoot = ProjectSession.RootPath;
        if (string.IsNullOrWhiteSpace(projectRoot))
            return null;

        var packageIds = snapshot.PackagesById.Values
            .Where(package => PathsEqual(package.SourceLocation, projectRoot))
            .Select(package => package.Id)
            .ToHashSet();
        if (packageIds.Count == 0)
            return null;

        var selectedRevisions = snapshot.PackagesById.Values
            .Where(package => package.IsAvailable && packageIds.Contains(package.Id))
            .Select(package => package.CurrentRevisionId is { } currentId &&
                               snapshot.RevisionsById.TryGetValue(currentId, out var current) &&
                               current.PackageId == package.Id
                ? current
                : null)
            .Where(revision => revision is not null)
            .ToArray();
        if (selectedRevisions.Length == 1)
            return selectedRevisions[0]!.Id;
        if (selectedRevisions.Length > 1)
            return null;

        // Legacy manifests without an explicit selection are usable only when
        // the physical root has exactly one revision. Identical entry bytes do
        // not make different dependency pins interchangeable.
        var legacyRevisions = snapshot.RevisionsById.Values
            .Where(revision => packageIds.Contains(revision.PackageId) &&
                               snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) &&
                               package.IsAvailable && package.CurrentRevisionId is null)
            .Take(2).ToArray();
        return legacyRevisions.Length == 1 ? legacyRevisions[0].Id : null;
    }

    private bool TryFindActiveCoreRevision(
        WccLibrarySnapshot snapshot,
        out WccPackageRecord package,
        out WccPackageRevisionRecord revision)
    {
        package = null!;
        revision = null!;
        var installation = _activeWesnothInstallation;
        if (installation is null)
            return false;

        var matchingPackage = snapshot.PackagesById.Values.FirstOrDefault(candidate =>
            candidate.SourceKind == WccPackageSourceKinds.WesnothMainline &&
            candidate.ContentKind == WccPackageContentKinds.ResourcePack &&
            candidate.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
            PathsEqual(candidate.SourceLocation, installation.CoreDataRoot))!;
        if (matchingPackage is null)
            return false;
        package = matchingPackage;

        revision = snapshot.RevisionsById.Values
            .Where(candidate =>
                candidate.PackageId == matchingPackage.Id &&
                string.Equals(candidate.Version, installation.ExactVersion, StringComparison.Ordinal) &&
                string.Equals(
                    candidate.TargetWesnothVersion,
                    installation.CompatibilityTarget,
                    StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.ImportedAtUtc)
            .FirstOrDefault()!;
        return revision is not null;
    }

    private WccPackageRevisionId? ResolveCurrentProjectCoreRevisionForPlan(
        WccLibraryManifest manifest,
        WccPackageRevisionId activeCoreRevisionId,
        WccProjectCoreRevisionPin? pin)
    {
        if (pin is null)
            return activeCoreRevisionId;

        return WccProjectCorePinResolver.Resolve(manifest, pin).Revision?.Id;
    }

    private static IReadOnlyCollection<WccPackageRevisionId>?
        ResolveWorkingProjectDependenciesForPlan(
            WccLibraryManifest manifest,
            WccWorkingProjectDescriptor? project,
            WccPackageRevisionId? projectCoreRevisionId)
        => WccWorkingProjectDependencyResolver.Resolve(manifest, project, projectCoreRevisionId);

    private static bool IsPinnedCoreRevisionAvailable(
        WccLibrarySnapshot snapshot,
        WccProjectCoreRevisionPin pin)
        => WccProjectCorePinResolver.Resolve(snapshot.Manifest, pin).IsResolved;

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;

        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsPathInsideRoot(string rootPath, string candidatePath)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var normalizedRoot = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedCandidate = Path.GetFullPath(candidatePath);
            return normalizedCandidate.StartsWith(normalizedRoot, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string FormatLibraryProgress(WccLibraryProgress progress)
    {
        var phase = progress.Phase switch
        {
            WccLibraryWorkPhase.LoadingManifest => "Loading library manifest",
            WccLibraryWorkPhase.CheckingPackageFiles => "Checking package files",
            WccLibraryWorkPhase.LoadingAnalysisCache => "Loading analysis cache",
            WccLibraryWorkPhase.BuildingSnapshot => "Building library snapshot",
            _ => "Refreshing library"
        };

        var status = progress.TotalItems <= 0
            ? phase + "..."
            : $"{phase}: {progress.ProcessedItems}/{progress.TotalItems}";
        return string.IsNullOrWhiteSpace(progress.CurrentItem) ? status : status + " — " + progress.CurrentItem;
    }

    public bool SetWesnothInstallPath(string path)
    {
        if (IsLibraryIndexRefreshing)
        {
            Status = "Wait for the current package refresh before changing the installation path.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!CanResetLibraryDisplaysForProfileChange())
            return false;

        var profile = EnsureActiveWesnothInstallationProfile();
        var proposedDetection = WesnothInstallationIdentityDetector.Detect(path);
        if (proposedDetection.Identity is { } proposedInstallation &&
            !string.IsNullOrWhiteSpace(profile.CompatibilityTarget) &&
            !string.Equals(
                profile.CompatibilityTarget,
                proposedInstallation.CompatibilityTarget,
                StringComparison.Ordinal))
        {
            Status =
                $"This profile targets Wesnoth {profile.CompatibilityTarget}, but the selected " +
                $"installation targets {proposedInstallation.CompatibilityTarget}. Add a separate " +
                "installation profile instead of silently retargeting this one.";
            WesnothInstallationIdentityStatus = Status;
            return false;
        }

        WesnothInstallPath = path;
        var detection = RefreshWesnothInstallationIdentity(persistChanges: false);
        SaveSettings();

        ConfigureEditorsForActiveWesnothInstallation();
        RefreshAfterWesnothProfileChange();
        Status = detection.Identity is { } installation
            ? $"Saved Wesnoth {installation.ExactVersion} installation (target {installation.CompatibilityTarget})."
            : $"Saved Wesnoth install path, but detection failed: {detection.Error}";
        return true;
    }

    public void AddWesnothInstallationProfile()
    {
        if (IsLibraryIndexRefreshing)
        {
            Status = "Wait for the current package refresh before changing installation profiles.";
            return;
        }
        if (!CanResetLibraryDisplaysForProfileChange())
            return;

        var profile = new WccWesnothInstallationProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = CreateUniqueWesnothProfileName("New Wesnoth installation")
        };
        _settings.WesnothInstallationProfiles.Add(profile);
        _settings.ActiveWesnothInstallationProfileId = profile.Id;
        WesnothInstallationProfiles.Add(profile);
        ActiveWesnothInstallationProfileName = profile.Name;
        _activeWesnothInstallation = null;
        WesnothInstallationIdentityStatus =
            "This profile does not have a Wesnoth installation folder yet.";
        SaveSettings();
        ConfigureEditorsForActiveWesnothInstallation();
        RefreshAfterWesnothProfileChange();
        RaiseWesnothProfilePropertiesChanged();
        Status = "Added a new Wesnoth installation profile. Select its install folder next.";
    }

    public bool ApplyActiveWesnothInstallationProfileName()
    {
        var profile = ActiveWesnothInstallationProfile;
        var normalized = ActiveWesnothInstallationProfileName.Trim();
        if (profile is null || string.IsNullOrWhiteSpace(normalized))
        {
            Status = "Profile names cannot be empty.";
            ActiveWesnothInstallationProfileName = profile?.Name ?? string.Empty;
            return false;
        }
        if (_settings.WesnothInstallationProfiles.Any(candidate =>
                !ReferenceEquals(candidate, profile) &&
                string.Equals(candidate.Name, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"Another installation profile is already named {normalized}.";
            ActiveWesnothInstallationProfileName = profile.Name;
            return false;
        }

        profile.Name = normalized;
        SaveSettings();
        ActiveWesnothInstallationProfileName = profile.Name;
        Status = $"Renamed the active installation profile to {profile.Name}.";
        return true;
    }

    public bool SetAddonsPath(string path)
    {
        if (IsLibraryIndexRefreshing)
        {
            Status = "Wait for the current package refresh before changing the add-on path.";
            return false;
        }

        AddonsPath = path;
        SaveSettings();
        ConfigureEditorsForActiveWesnothInstallation();
        MarkDirectPresetCatalogsDirty();
        RestoreLibraryPickerProjection();
        RefreshUnitSidebarEntries();

        Status = "Saved add-on folder path.";
        return true;
    }

    public bool ClearAddonsPath()
    {
        if (IsLibraryIndexRefreshing)
        {
            Status = "Wait for the current package refresh before changing the add-on path.";
            return false;
        }
        if (!HasAddonsPath)
            return false;

        AddonsPath = null;
        SaveSettings();
        ConfigureEditorsForActiveWesnothInstallation();
        MarkDirectPresetCatalogsDirty();
        RestoreLibraryPickerProjection();
        RefreshUnitSidebarEntries();
        Status = "Cleared the add-on folder for this installation profile.";
        return true;
    }

    public void AddCurrentUnitToLibrary()
    {
        var asset = UnitEditor.CreateLibrarySnapshotAsset();
        if (asset is null)
        {
            Status = "No current unit file available to add.";
            return;
        }

        var result = WccManagedLibraryFileStore.Create(
            _unitCatalogLibraryRoot,
            WccManagedLibraryFileKind.UnitType,
            asset.RawTextSnapshot ?? string.Empty,
            asset.InternalId);
        if (!result.IsSuccess)
        {
            Status = FormatManagedLibraryMutationFailure(result, "add the current unit file to WCC");
            return;
        }

        if (string.IsNullOrWhiteSpace(result.SourcePath))
        {
            Status = "Could not add the current unit file to WCC: no managed source path was created.";
            return;
        }

        var managedAsset = CreateManagedUnitAsset(asset, result.SourcePath);
        _pendingManagedUnitSourcePathSelection = result.SourcePath;
        _ = RefreshConfiguredLibraryIndexAsync();
        UnitEditor.LoadFromExistingUnitAsset(managedAsset);
        UnitSession = new WccEditorSessionContext(
            WccEditorSessionScope.Library,
            WccEditorAccessMode.Writable,
            Path.GetDirectoryName(result.SourcePath),
            $"WCC library unit: {Path.GetFileName(result.SourcePath)}",
            _unitCatalogLibraryRoot);
        SelectedTabIndex = 2;
        ClearCurrentUnitDirty();

        OnPropertyChanged(nameof(CanAddCurrentUnitToLibrary));
        OnPropertyChanged(nameof(CanDeleteCurrentUnit));
        Status = "Added current unit file to the WCC managed package; refreshing the package index.";
    }
    public void CloneCurrentUnitToLibrary()
    {
        var cloneId = WccOwnedAssetIdFactory.CreateCloneId(
            UnitEditor.CurrentSelectedUnitTypeId,
            QueryLibraryItems(WccEntityTypes.UnitType)
                .Select(x => x.ExportSymbol ?? string.Empty));

        var asset = UnitEditor.CreateClonedSingleUnitAsset(cloneId);
        if (asset is null)
        {
            Status = "Could not clone the current unit.";
            return;
        }

        var result = WccManagedLibraryFileStore.Create(
            _unitCatalogLibraryRoot,
            WccManagedLibraryFileKind.UnitType,
            asset.RawTextSnapshot ?? string.Empty,
            asset.InternalId);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.SourcePath))
        {
            Status = FormatManagedLibraryMutationFailure(result, "clone the current unit into WCC");
            return;
        }

        var managedAsset = CreateManagedUnitAsset(asset, result.SourcePath);
        _pendingManagedUnitSourcePathSelection = result.SourcePath;
        _ = RefreshConfiguredLibraryIndexAsync();
        UnitEditor.LoadFromExistingUnitAsset(managedAsset);
        UnitSession = new WccEditorSessionContext(
            WccEditorSessionScope.Library,
            WccEditorAccessMode.Writable,
            Path.GetDirectoryName(result.SourcePath),
            $"WCC library unit: {Path.GetFileName(result.SourcePath)}",
            _unitCatalogLibraryRoot);
        SelectedTabIndex = 2;
        ClearCurrentUnitDirty();

        OnPropertyChanged(nameof(CanAddCurrentUnitToLibrary));
        OnPropertyChanged(nameof(CanCloneCurrentUnit));
        OnPropertyChanged(nameof(CanDeleteCurrentUnit));
        Status = $"Cloned unit as {cloneId} in the WCC managed package.";
    }
    public void DeleteCurrentUnitFromLibrary()
    {
        var asset = UnitEditor.CurrentUnitAsset;
        if (asset is null || asset.Origin != WccSourceOrigin.WccGenerated)
            return;

        var result = WccManagedLibraryFileStore.Delete(
            _unitCatalogLibraryRoot,
            WccManagedLibraryFileKind.UnitType,
            asset.SourcePath,
            asset.RawTextSnapshot is { } loadedText
                ? WccContentHash.ComputeText(loadedText)
                : null);
        if (!result.IsSuccess)
        {
            Status = FormatManagedLibraryMutationFailure(result, "delete the current WCC unit file");
            return;
        }

        _ = RefreshConfiguredLibraryIndexAsync();

        ClearCurrentUnitDirty();
        UnitEditor.LoadFromText("(empty)", string.Empty);
        UnitSession = WccEditorSessionContext.Empty;
        OnPropertyChanged(nameof(CanAddCurrentUnitToLibrary));
        OnPropertyChanged(nameof(CanCloneCurrentUnit));
        OnPropertyChanged(nameof(CanDeleteCurrentUnit));
        OnPropertyChanged(nameof(CanSaveCurrentContext));

        Status = "Deleted WCC-owned unit file; refreshing the package index.";
    }

    private static UnitFileAsset CreateManagedUnitAsset(UnitFileAsset source, string sourcePath)
    {
        var rawText = source.RawTextSnapshot ?? string.Empty;
        var asset = new UnitFileAsset
        {
            InternalId = source.InternalId,
            FileName = source.FileName,
            SourcePath = sourcePath,
            Origin = WccSourceOrigin.WccGenerated,
            RawTextSnapshot = rawText
        };
        asset.Documents.AddRange(UnitTypeImporter.ImportAllFromText(rawText));
        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(rawText));
        return asset;
    }


    // ---------------------------------------------------------------------
    // Shell navigation and workspace opening
    // ---------------------------------------------------------------------
    public void OpenCampaignMetadataInCampaignTab()
    {
        SelectedTabIndex = 0;
        Status = "Opened campaign metadata in campaign workspace.";
    }

    public void OpenScenarioAssetInShell(ScenarioFileAsset asset)
    {
        if (!TrySaveBeforeInlineNavigation(() => OpenScenarioAssetInShell(asset)))
        {
            RestoreStandaloneScenarioSelectionToCurrentEditorAsset();
            return;
        }
        if (!SaveCurrentEditorsIfDirty(refreshCatalogEntries: true))
        {
            if (_scenarioEditorDirty)
                RestoreStandaloneScenarioSelectionToCurrentEditorAsset();
            return;
        }

        _showLooseScenarioWorkspace = true;
        LoadScenarioDocument(
            asset,
            CreateLooseFileEditorSession(asset.SourcePath ?? asset.FileName));
        RaiseScenarioWorkspaceStateChanged();
        SelectedTabIndex = 1;
        Status = $"Opened loose scenario file: {asset.FileName}";
    }

    public void OpenUnitAssetInShell(UnitFileAsset asset)
    {
        if (!TrySaveBeforeInlineNavigation(() => OpenUnitAssetInShell(asset)))
            return;
        if (!SaveCurrentEditorsIfDirty(refreshCatalogEntries: true))
        {
            if (_unitEditorDirty)
                RestoreStandaloneUnitSelectionToCurrentEditorAsset();
            return;
        }

        _showLooseUnitWorkspace = true;
        UnitSession = CreateLooseFileEditorSession(asset.SourcePath ?? asset.FileName);
        UnitEditor.LoadFromExistingUnitAsset(asset);
        SelectedTabIndex = 2;
        ClearCurrentUnitDirty();
        RaiseUnitWorkspaceStateChanged();
        OnPropertyChanged(nameof(CanSaveCurrentContext));
        OnPropertyChanged(nameof(CanDeleteCurrentUnit));
        OnPropertyChanged(nameof(CanAddCurrentUnitToLibrary));
        OnPropertyChanged(nameof(CanCloneCurrentUnit));
        Status = $"Opened unit type in standalone shell tab: {asset.FileName}";
    }

    public async Task OpenUnitCatalogEntryInShellAsync(UnitTypeCatalogEntryVm entryVm)
    {
        if (entryVm is null) throw new ArgumentNullException(nameof(entryVm));
        var navigation = BeginUnitNavigation();
        if (UnitSession.Scope == WccEditorSessionScope.Library && IsShowingIndexedUnit(UnitEditor, entryVm)) return;
        if (entryVm.LibraryEntityId is not { } selectedId || CaptureIndexedUnitDestination(selectedId, false) is not { } destination) return;
        if (!await ConfirmUnitNavigationAsync(navigation)) return;
        if (ResolveIndexedUnitDestination(destination, out var snapshot) is not { } entityId || snapshot is null)
        { ReportChangedUnitDestination(navigation); return; }

        _unitEntityLoadCancellation?.Cancel();
        _unitEntityLoadCancellation?.Dispose();
        _unitEntityLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _unitEntityLoadCancellation.Token;

        using var loading = BeginEditorSourceLoading($"Loading {entryVm.UnitTypeId} from {entryVm.OriginLabel}...");
        if (!IsCurrentUnitNavigation(navigation) || !ReferenceEquals(snapshot, LibrarySnapshot)) return;
        Status = $"Loading {entryVm.UnitTypeId} from {entryVm.OriginLabel}...";

        try
        {
            var loaded = await Task.Run(() => WccLibraryUnitEntityLoader.LoadAsync(snapshot, entityId, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentUnitNavigation(navigation) || !ReferenceEquals(snapshot, LibrarySnapshot) ||
                !LoadedIndexedUnitMatches(destination, loaded))
            { ReportChangedUnitDestination(navigation); return; }

            UnitEditor.LoadFromExistingUnitAsset(
                loaded.SourceFile,
                loaded.PreferredUnitDocumentInternalId);
            if (navigation.Version != _unitNavigationVersion) return;
            var canWrite = UnitEditor.IsCurrentUnitWccOwned;
            UnitSession = new WccEditorSessionContext(
                WccEditorSessionScope.Library,
                canWrite ? WccEditorAccessMode.Writable : WccEditorAccessMode.ReadOnly,
                Path.GetDirectoryName(loaded.SourceFile.SourcePath),
                canWrite
                    ? $"WCC library unit: {entryVm.DisplayName}"
                    : $"Library unit inspection (read-only): {entryVm.DisplayName} [{entryVm.OriginLabel}]",
                canWrite ? _unitCatalogLibraryRoot : null);
            SelectedTabIndex = 2;
            ClearCurrentUnitDirty();
            SelectedUnitCatalogEntry = FindCurrentUnitCatalogRow(entityId);
            OnPropertyChanged(nameof(CanSaveCurrentContext));
            OnPropertyChanged(nameof(CanDeleteCurrentUnit));
            OnPropertyChanged(nameof(CanAddCurrentUnitToLibrary));
            OnPropertyChanged(nameof(CanCloneCurrentUnit));

            Status = loaded.MatchesIndexedContentHash
                ? $"Opened indexed unit type: {entryVm.DisplayName} [{entryVm.OriginLabel}]"
                : $"Opened {entryVm.DisplayName}, but its source changed after indexing; refresh the package index.";
        }
        catch (OperationCanceledException)
        {
            // A newer unit selection superseded this load.
        }
        catch (Exception ex)
        {
            if (IsCurrentUnitNavigation(navigation))
                Status = $"Could not open indexed unit type: {ex.Message}";
        }
    }

    public async Task OpenLibraryUnitCatalogEntryAsync(UnitTypeCatalogEntryVm entryVm)
    {
        if (entryVm is null) throw new ArgumentNullException(nameof(entryVm));
        var navigation = BeginUnitNavigation();
        if (IsShowingIndexedUnit(LibraryUnitEditor, entryVm)) return;
        if (entryVm.LibraryEntityId is not { } selectedId || CaptureIndexedUnitDestination(selectedId, false) is not { } destination) return;
        if (!await ConfirmUnitNavigationAsync(navigation)) return;
        if (ResolveIndexedUnitDestination(destination, out var snapshot) is not { } entityId || snapshot is null)
        { ReportChangedUnitDestination(navigation); return; }

        _libraryUnitEntityLoadCancellation?.Cancel();
        _libraryUnitEntityLoadCancellation?.Dispose();
        _libraryUnitEntityLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _libraryUnitEntityLoadCancellation.Token;
        using var loading = BeginEditorSourceLoading($"Loading Library unit {entryVm.UnitTypeId} from {entryVm.OriginLabel}...");
        if (!IsCurrentUnitNavigation(navigation) || !ReferenceEquals(snapshot, LibrarySnapshot)) return;
        Status = $"Loading Library unit {entryVm.UnitTypeId} from {entryVm.OriginLabel}...";

        try
        {
            var loaded = await Task.Run(() => WccLibraryUnitEntityLoader.LoadAsync(snapshot, entityId, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentUnitNavigation(navigation) || !ReferenceEquals(snapshot, LibrarySnapshot) ||
                !LoadedIndexedUnitMatches(destination, loaded))
            { ReportChangedUnitDestination(navigation); return; }
            LibraryUnitEditor.LoadFromExistingUnitAsset(
                loaded.SourceFile,
                loaded.PreferredUnitDocumentInternalId);
            if (navigation.Version != _unitNavigationVersion) return;
            var canWrite = LibraryUnitEditor.IsCurrentUnitWccOwned;
            LibraryUnitSession = new WccEditorSessionContext(
                WccEditorSessionScope.Library,
                canWrite ? WccEditorAccessMode.Writable : WccEditorAccessMode.ReadOnly,
                Path.GetDirectoryName(loaded.SourceFile.SourcePath),
                canWrite
                    ? $"WCC Library unit: {entryVm.DisplayName}"
                    : $"Library inspection (read-only): {entryVm.DisplayName} [{entryVm.OriginLabel}]",
                canWrite ? _unitCatalogLibraryRoot : null);
            ClearLibraryUnitDirty();
            SelectedInspectorUnitCatalogEntry = FindCurrentUnitCatalogRow(entityId);
            RaiseLibraryUnitEditorStateChanged();

            Status = loaded.MatchesIndexedContentHash
                ? $"Opened Library unit: {entryVm.DisplayName} [{entryVm.OriginLabel}]"
                : $"Opened {entryVm.DisplayName}, but its source changed after indexing; refresh the package index.";
        }
        catch (OperationCanceledException)
        {
            // A newer Library selection superseded this load.
        }
        catch (Exception ex)
        {
            if (IsCurrentUnitNavigation(navigation))
                Status = $"Could not open Library unit: {ex.Message}";
        }
    }

    public void SaveLibraryUnit()
    {
        if (SaveLibraryUnitIfDirty())
            Status = "Saved WCC Library unit.";
    }

    public void CloneSelectedLibraryUnit()
    {
        if (!SaveLibraryUnitIfDirty())
            return;

        var cloneId = WccOwnedAssetIdFactory.CreateCloneId(
            LibraryUnitEditor.CurrentSelectedUnitTypeId,
            QueryLibraryItems(WccEntityTypes.UnitType)
                .Select(item => item.ExportSymbol ?? string.Empty));
        var asset = LibraryUnitEditor.CreateClonedSingleUnitAsset(cloneId);
        if (asset is null)
        {
            Status = "Could not clone the selected Library unit.";
            return;
        }

        var result = WccManagedLibraryFileStore.Create(
            _unitCatalogLibraryRoot,
            WccManagedLibraryFileKind.UnitType,
            asset.RawTextSnapshot ?? string.Empty,
            asset.InternalId);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.SourcePath))
        {
            Status = FormatManagedLibraryMutationFailure(result, "clone the selected unit into WCC");
            return;
        }

        var managedAsset = CreateManagedUnitAsset(asset, result.SourcePath);
        _pendingManagedInspectorUnitSourcePathSelection = result.SourcePath;
        LibraryUnitEditor.LoadFromExistingUnitAsset(managedAsset);
        LibraryUnitSession = new WccEditorSessionContext(
            WccEditorSessionScope.Library,
            WccEditorAccessMode.Writable,
            Path.GetDirectoryName(result.SourcePath),
            $"WCC Library unit: {cloneId}",
            _unitCatalogLibraryRoot);
        ClearLibraryUnitDirty();
        RaiseLibraryUnitEditorStateChanged();
        _ = RefreshConfiguredLibraryIndexAsync();
        Status = $"Cloned unit as {cloneId} in the WCC Library package.";
    }

    public void DeleteSelectedLibraryUnit()
    {
        if (!CanDeleteLibraryUnit || !SaveLibraryUnitIfDirty())
            return;

        var asset = LibraryUnitEditor.CurrentUnitAsset;
        if (asset is null)
            return;

        var result = WccManagedLibraryFileStore.Delete(
            _unitCatalogLibraryRoot,
            WccManagedLibraryFileKind.UnitType,
            asset.SourcePath,
            asset.RawTextSnapshot is { } loadedText
                ? WccContentHash.ComputeText(loadedText)
                : null);
        if (!result.IsSuccess)
        {
            Status = FormatManagedLibraryMutationFailure(result, "delete the selected WCC Library unit");
            return;
        }

        LibraryUnitEditor.LoadFromText("(no Library unit)", string.Empty);
        LibraryUnitSession = WccEditorSessionContext.Empty;
        ClearLibraryUnitDirty();
        RaiseLibraryUnitEditorStateChanged();
        _ = RefreshConfiguredLibraryIndexAsync();
        Status = "Deleted WCC Library unit; refreshing the package index.";
    }

    public void RefreshUnitCatalogEntries(bool selectFallbackEntry = true)
    {
        lock (_unitDisplaySync)
        {
        var selectedSidebarKey = SelectedUnitCatalogEntry?.SelectionKey;
        var selectedInspectorKey = SelectedInspectorUnitCatalogEntry?.SelectionKey;

        _allUnitCatalogEntryVms.Clear();

        if (LibrarySnapshot is { } snapshot)
        {
            var libraryUnits = WccLibraryCatalogProjection.Query(snapshot, new WccCatalogQuery
            {
                EntityType = WccEntityTypes.UnitType,
                VisibleRevisionIds = _activeLibraryRevisionIds,
                Take = int.MaxValue
            });

            foreach (var item in libraryUnits.Items)
            {
                snapshot.SourceFilesById.TryGetValue(item.SourceFileId, out var sourceFile);
                snapshot.EntitiesById.TryGetValue(item.EntityId, out var entity);
                var package = snapshot.FindPackageForEntity(item.EntityId);
                var inheritedDisplay = entity is not null && sourceFile is not null
                    ? CachedIndexedUnitSummary(entity, sourceFile) : null;
                _allUnitCatalogEntryVms.Add(new UnitTypeCatalogEntryVm(
                    item,
                    sourceFile?.StoragePath,
                    inheritedDisplay?.PreviewDocument is { } preview && sourceFile is not null
                        ? () => WccUnitPreviewPathResolver.Resolve(sourceFile.StoragePath,
                            WccSourceOrigin.WccGenerated, preview, WesnothInstallPath).ResolvedAbsolutePath
                        : entity is not null && sourceFile is not null
                        ? CreateIndexedUnitPreviewResolver(entity, sourceFile, package)
                        : null,
                    inheritedDisplay?.EffectiveRace));
            }
        }

        _allUnitCatalogEntryVms.Sort((left, right) =>
        {
            var byId = StringComparer.OrdinalIgnoreCase.Compare(left.UnitTypeId, right.UnitTypeId);
            if (byId != 0)
                return byId;

            var byPackage = StringComparer.OrdinalIgnoreCase.Compare(left.OriginLabel, right.OriginLabel);
            return byPackage != 0
                ? byPackage
                : StringComparer.OrdinalIgnoreCase.Compare(left.SourceFileName, right.SourceFileName);
        });

        RefreshUnitSidebarEntries();
        RefreshCatalogInspectorUnitEntries();

        var pendingManagedSelection = !string.IsNullOrWhiteSpace(_pendingManagedUnitSourcePathSelection)
            ? _allUnitCatalogEntryVms.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.SourcePath) &&
                PathsEqual(x.SourcePath, _pendingManagedUnitSourcePathSelection))
            : null;
        if (pendingManagedSelection is not null)
            _pendingManagedUnitSourcePathSelection = null;

        var pendingManagedInspectorSelection =
            !string.IsNullOrWhiteSpace(_pendingManagedInspectorUnitSourcePathSelection)
                ? _allUnitCatalogEntryVms.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x.SourcePath) &&
                    PathsEqual(x.SourcePath, _pendingManagedInspectorUnitSourcePathSelection))
                : null;
        if (pendingManagedInspectorSelection is not null)
            _pendingManagedInspectorUnitSourcePathSelection = null;

        SelectedUnitCatalogEntry = pendingManagedSelection
                                   ?? _allUnitCatalogEntryVms.FirstOrDefault(x => x.SelectionKey == selectedSidebarKey)
                                   ?? (selectFallbackEntry ? _allUnitCatalogEntryVms.FirstOrDefault() : null);

        SelectedInspectorUnitCatalogEntry =
            pendingManagedInspectorSelection
            ?? CatalogInspectorUnitEntries.FirstOrDefault(x => x.SelectionKey == selectedInspectorKey)
            ?? (selectFallbackEntry ? CatalogInspectorUnitEntries.FirstOrDefault() : null);

        RestoreLibraryPickerProjection();
        }
        QueueManagedUnitInheritanceDisplay();
    }

    public void RefreshProjectUnitWorkspaceEntries()
    {
        lock (_unitDisplaySync)
        {
        var previousSelectionKey = SelectedProjectUnitEntry?.SelectionKey;
        _allProjectOwnedUnitEntries.Clear();
        _allProjectReferencedUnitEntries.Clear();
        _allProjectUnresolvedUnitEntries.Clear();

        var project = CampaignEditor.CurrentProject;
        if (project?.Kind != WccProjectKind.Campaign)
        {
            ApplyProjectUnitWorkspaceFilter();
            SelectedProjectUnitEntry = null;
            return;
        }

        // These readers belong to this locked refresh, never to retained unit rows.
        Func<UnitFileAsset, UnitTypeDocument, bool>? isUnitActive = null;
        Func<UnitFileAsset, UnitTypeDocument, UnitDisplaySummary?>? readUnitSummary = null;
        foreach (var asset in project.ProjectUnitFiles)
        {
            foreach (var document in asset.Documents)
            {
                isUnitActive ??= CreateCampaignUnitActivityPredicate();
                if (!isUnitActive(asset, document)) continue;
                readUnitSummary ??= CreateProjectUnitSummaryReader();
                var inheritedDisplay = readUnitSummary(asset, document);
                // Sidebar art needs only the detached document. Capturing the effective
                // display would retain its complete expanded unit registry.
                var preview = inheritedDisplay?.PreviewDocument ?? document;
                var race = inheritedDisplay?.EffectiveRace ??
                    UnitTypeCatalogEntryVm.DecodeSourceRace(document.Unit.Race);
                _allProjectOwnedUnitEntries.Add(ProjectUnitWorkspaceEntryVm.CreateOwned(
                    asset,
                    document.InternalId,
                    document.Unit.Id,
                    WmlDisplayStringNormalizer.Normalize(document.Unit.NameWml) ?? document.Unit.Id,
                    race,
                    () => WccUnitPreviewPathResolver.Resolve(
                        asset,
                        preview,
                        WesnothInstallPath).ResolvedAbsolutePath));
            }
        }

        var snapshot = LibrarySnapshot;
        var projectRevisionId = snapshot is null ? null : FindCurrentProjectRevisionId(snapshot);
        if (snapshot is not null && projectRevisionId is { } revisionId)
        {
            var projectSourceFileIds = snapshot.SourceFilesById.Values
                .Where(file => file.PackageRevisionId == revisionId)
                .Select(file => file.Id)
                .ToHashSet();
            var linkedEntityIds = new HashSet<WccEntityId>();
            var unresolvedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unitCandidatesBySymbol = snapshot.EntitiesById.Values
                .Where(entity =>
                    entity.EntityType == WccEntityTypes.UnitType &&
                    !string.IsNullOrWhiteSpace(entity.ExportSymbol))
                .GroupBy(entity => entity.ExportSymbol!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray(),
                    StringComparer.Ordinal);

            foreach (var reference in snapshot.ReferencesById.Values.Where(reference =>
                         projectSourceFileIds.Contains(reference.SourceFileId) &&
                         (reference.ReferenceType == WccReferenceTypes.UnitType ||
                          reference.ReferenceType == WccReferenceTypes.AdvancesTo)))
            {
                snapshot.SourceFilesById.TryGetValue(reference.SourceFileId, out var referencingFile);
                var referencingFileName = Path.GetFileName(
                    referencingFile?.StoragePath ?? referencingFile?.RelativePath ?? "project cfg");

                if (reference.TargetEntityId is { } targetEntityId &&
                    snapshot.EntitiesById.TryGetValue(targetEntityId, out var targetEntity) &&
                    targetEntity.EntityType == WccEntityTypes.UnitType &&
                    snapshot.SourceFilesById.TryGetValue(targetEntity.SourceFileId, out var targetFile))
                {
                    if (targetFile.PackageRevisionId == revisionId || !linkedEntityIds.Add(targetEntityId))
                        continue;

                    snapshot.RevisionsById.TryGetValue(targetFile.PackageRevisionId, out var targetRevision);
                    WccPackageRecord? targetPackage = null;
                    if (targetRevision is not null)
                        snapshot.PackagesById.TryGetValue(targetRevision.PackageId, out targetPackage);

                    targetEntity.Summary.TryGetValue("name_wml", out var nameWml);
                    targetEntity.Summary.TryGetValue("race", out var raceId);
                    _allProjectReferencedUnitEntries.Add(ProjectUnitWorkspaceEntryVm.CreateReferenced(
                        targetEntity.Id,
                        targetEntity.ExportSymbol,
                        WmlDisplayStringNormalizer.Normalize(nameWml) ?? targetEntity.DisplayName ?? targetEntity.ExportSymbol,
                        UnitTypeCatalogEntryVm.DecodeSourceRace(raceId),
                        targetPackage?.Name ?? "Dependency package",
                        Path.GetFileName(targetFile.StoragePath ?? targetFile.RelativePath),
                        CreateIndexedUnitPreviewResolver(
                            targetEntity,
                            targetFile,
                            targetPackage)));
                    continue;
                }

                var lookupValue = reference.LookupValue ?? reference.RawValue;
                var unresolvedKey = $"{reference.ResolutionState}:{lookupValue}";
                if (!unresolvedKeys.Add(unresolvedKey))
                    continue;

                var normalizedLookup = lookupValue.Trim();
                var candidates = unitCandidatesBySymbol.TryGetValue(normalizedLookup, out var exactCandidates)
                    ? exactCandidates
                    : Array.Empty<WccEntityRecord>();
                var candidatePackageNames = candidates
                    .Select(candidate => snapshot.FindPackageForEntity(candidate.Id)?.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var candidateSummary = candidatePackageNames.Length switch
                {
                    0 => null,
                    1 => $"library candidate: {candidatePackageNames[0]}",
                    _ => $"{candidates.Length} library candidates: {string.Join(", ", candidatePackageNames)}"
                };

                _allProjectUnresolvedUnitEntries.Add(ProjectUnitWorkspaceEntryVm.CreateUnresolved(
                    lookupValue,
                    reference.ResolutionState,
                    referencingFileName,
                    candidates.Select(candidate => candidate.Id).ToArray(),
                    candidateSummary));
            }
        }

        static int CompareEntries(ProjectUnitWorkspaceEntryVm left, ProjectUnitWorkspaceEntryVm right)
        {
            var byRace = StringComparer.OrdinalIgnoreCase.Compare(left.RaceId, right.RaceId);
            return byRace != 0
                ? byRace
                : StringComparer.OrdinalIgnoreCase.Compare(left.UnitTypeId, right.UnitTypeId);
        }

        _allProjectOwnedUnitEntries.Sort(CompareEntries);
        _allProjectReferencedUnitEntries.Sort(CompareEntries);
        _allProjectUnresolvedUnitEntries.Sort(CompareEntries);
        ApplyProjectUnitWorkspaceFilter();

        SelectedProjectUnitEntry = ProjectOwnedUnitEntries
                                       .Concat(ProjectReferencedUnitEntries)
                                       .Concat(ProjectUnresolvedUnitEntries)
                                       .FirstOrDefault(entry => entry.SelectionKey == previousSelectionKey)
                                   ?? ProjectOwnedUnitEntries.FirstOrDefault()
                                   ?? ProjectReferencedUnitEntries.FirstOrDefault()
                                   ?? ProjectUnresolvedUnitEntries.FirstOrDefault();
        }
        QueueProjectUnitInheritanceDisplay();
    }

    private void ApplyProjectUnitWorkspaceFilter()
    {
        lock (_unitDisplaySync)
        {
        var query = ProjectUnitSearchText?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(query);

        static void Refill(
            ObservableCollection<ProjectUnitWorkspaceEntryVm> target,
            IEnumerable<ProjectUnitWorkspaceEntryVm> source)
        {
            target.Clear();
            foreach (var item in source)
                target.Add(item);
        }

        bool Matches(ProjectUnitWorkspaceEntryVm entry) =>
            !hasQuery || entry.SearchText.Contains(query!, StringComparison.OrdinalIgnoreCase);

        Refill(ProjectOwnedUnitEntries, _allProjectOwnedUnitEntries.Where(Matches));
        Refill(ProjectReferencedUnitEntries, _allProjectReferencedUnitEntries.Where(Matches));
        Refill(ProjectUnresolvedUnitEntries, _allProjectUnresolvedUnitEntries.Where(Matches));
        RebuildProjectUnitRaceGroups();
        OnPropertyChanged(nameof(HasProjectOwnedUnitEntries));
        OnPropertyChanged(nameof(HasProjectReferencedUnitEntries));
        OnPropertyChanged(nameof(HasProjectUnresolvedUnitEntries));
        }
    }

    private void RebuildProjectUnitRaceGroups()
    {
        var previousExpansion = ProjectUnitRaceGroups.ToDictionary(
            group => group.RaceKey,
            group => group.IsExpanded,
            StringComparer.OrdinalIgnoreCase);
        ProjectUnitRaceGroups.Clear();

        var entries = ProjectOwnedUnitEntries
            .Concat(ProjectReferencedUnitEntries)
            .Concat(ProjectUnresolvedUnitEntries);
        foreach (var raceGroup in entries
                     .GroupBy(entry => entry.RaceId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => string.Equals(group.Key, "(unknown)", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                     .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var raceKey = raceGroup.Key;
            var isExpanded = previousExpansion.TryGetValue(raceKey, out var previous)
                ? previous
                : true;
            var groupVm = new ProjectUnitRaceGroupVm(raceKey, raceKey, isExpanded);
            foreach (var entry in raceGroup.OrderBy(item => item.UnitTypeId, StringComparer.OrdinalIgnoreCase))
                groupVm.Units.Add(entry);
            ProjectUnitRaceGroups.Add(groupVm);
        }

        OnPropertyChanged(nameof(HasProjectUnitRaceGroups));
    }

    private Func<string?> CreateIndexedUnitPreviewResolver(
        WccEntityRecord entity,
        WccSourceFileRecord sourceFile,
        WccPackageRecord? package)
    {
        IReadOnlyList<WmlMacroDefinition>? sourceMacroDefinitions = null;
        var sourceMacroDefinitionsLoaded = false;
        return () =>
        {
            var origin = package?.SourceKind == WccPackageSourceKinds.WesnothCore ||
                         package?.SourceKind == WccPackageSourceKinds.WesnothMainline
                ? WccSourceOrigin.CoreInstall
                : package?.SourceKind == WccPackageSourceKinds.WccManagedLibrary
                    ? WccSourceOrigin.WccGenerated
                    : WccSourceOrigin.ImportedExternal;

            foreach (var candidate in new[]
                     {
                         (SummaryKey: "image", Kind: WccAssetReferenceKind.Image),
                         (SummaryKey: "profile", Kind: WccAssetReferenceKind.Portrait)
                     })
            {
                if (!entity.Summary.TryGetValue(candidate.SummaryKey, out var rawValue))
                    continue;

                var lookupPath = NormalizeUnitPreviewLookup(rawValue);
                if (string.IsNullOrWhiteSpace(lookupPath))
                    continue;

                if (!sourceMacroDefinitionsLoaded &&
                    (lookupPath.Contains('{') || lookupPath.Contains('}')))
                {
                    sourceMacroDefinitionsLoaded = true;
                    try
                    {
                        sourceMacroDefinitions = File.Exists(sourceFile.StoragePath)
                            ? WmlMacroDefinitionScanner.Scan(File.ReadAllText(sourceFile.StoragePath))
                            : Array.Empty<WmlMacroDefinition>();
                    }
                    catch (IOException)
                    {
                        sourceMacroDefinitions = Array.Empty<WmlMacroDefinition>();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        sourceMacroDefinitions = Array.Empty<WmlMacroDefinition>();
                    }
                }

                var resolution = WccUnitPreviewPathResolver.ResolveRawImageExpression(
                    sourceFile.StoragePath,
                    origin,
                    rawValue,
                    candidate.Kind,
                    WesnothInstallPath,
                    sourceMacroDefinitions);
                if (resolution.IsResolved)
                    return resolution.ResolvedAbsolutePath;
            }

            return null;
        };
    }

    private static string? NormalizeUnitPreviewLookup(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var value = rawValue.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1].Trim();
        }

        var modifierIndex = value.IndexOf('~');
        if (modifierIndex >= 0)
            value = value[..modifierIndex];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void ClearProjectUnitWorkspaceEntries()
    {
        _allProjectOwnedUnitEntries.Clear();
        _allProjectReferencedUnitEntries.Clear();
        _allProjectUnresolvedUnitEntries.Clear();
        ApplyProjectUnitWorkspaceFilter();
        SelectedProjectUnitEntry = null;
    }

    public void RefreshAssetRegistryEntries()
    {
        var selectedAssetId = SelectedAssetRegistryEntry?.Entry.InternalId;

        _allAssetRegistryEntryVms.Clear();

        foreach (var entry in AssetRegistry.Entries
                     .OrderBy(e => e.KindLabel, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.LookupPath, StringComparer.OrdinalIgnoreCase))
        {
            _allAssetRegistryEntryVms.Add(new AssetRegistryEntryVm(entry));
        }

        RefreshCatalogInspectorAssetEntries();

        SelectedAssetRegistryEntry =
            CatalogInspectorAssetEntries.FirstOrDefault(x => x.Entry.InternalId == selectedAssetId)
            ?? CatalogInspectorAssetEntries.FirstOrDefault();
    }

    public void OpenScenarioAssetInProjectScenarioTab(ScenarioFileAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (_publishingCampaignEntrySelection) return;
        if (SelectedCampaignScenarioFile is { IsUnresolvedCampaignMembership: true } unresolved && ReferenceEquals(unresolved.Asset, asset))
        { _ = OpenUnresolvedCampaignScenarioSourceAsync(unresolved); return; }
        if ((CampaignEditor.RequiresSelectedCampaignAdapter || CampaignEditor.RequiresManagedChapterScenarioNavigation) &&
            (SelectedCampaignScenarioFile is { CampaignEntry: not null } selectedRow && ReferenceEquals(selectedRow.Asset, asset)
                ? selectedRow : FindCurrentProjectScenarioRow(asset)) is { CampaignEntry: not null } entryRow)
        {
            _ = OpenCampaignScenarioRowAsync(entryRow);
            return;
        }
        var project = CampaignEditor.CurrentProject;
        if (FindCurrentProjectScenarioRow(asset) is null)
        {
            RestoreCampaignScenarioSelectionToCurrentEditorAsset();
            Status = "The requested scenario is no longer part of the open project.";
            return;
        }
        if (TryShowCurrentProjectScenario(asset)) return;
        if (!TrySaveBeforeInlineNavigation(() => OpenScenarioAssetInProjectScenarioTab(asset)))
        {
            RestoreCampaignScenarioSelectionToCurrentEditorAsset();
            return;
        }
        if (!SaveCurrentEditorsIfDirty(refreshCatalogEntries: true))
        {
            RestoreCampaignScenarioSelectionToCurrentEditorAsset();
            return;
        }
        if (!ReferenceEquals(project, CampaignEditor.CurrentProject) || FindCurrentProjectScenarioRow(asset) is null)
        {
            RestoreCampaignScenarioSelectionToCurrentEditorAsset();
            Status = "The project scenario selection changed. The requested scenario was not opened.";
            return;
        }

        _showLooseScenarioWorkspace = false;
        LoadScenarioDocument(
            asset,
            CreateProjectEditorSession($"Project scenario: {asset.FileName}"));
        if (!ReferenceEquals(project, CampaignEditor.CurrentProject) ||
            !ReferenceEquals(asset, ScenarioEditor.CurrentScenarioAsset)) return;
        SynchronizeProjectScenarioSelection(asset);
        if (!ReferenceEquals(project, CampaignEditor.CurrentProject) ||
            !ReferenceEquals(asset, ScenarioEditor.CurrentScenarioAsset)) return;
        RaiseScenarioWorkspaceStateChanged();
        SelectedTabIndex = 1;
        Status = $"Opened project scenario: {asset.FileName}";
    }

    public void ShowProjectScenarioWorkspace()
    {
        if (!HasOpenCampaignProject) return;
        var selected = FindCurrentProjectScenarioRow(SelectedCampaignScenarioFile?.Asset) ??
            FindCurrentProjectScenarioRow(CampaignEditor.SelectedScenarioFile?.Asset) ??
            FindCurrentProjectScenarioRow(ScenarioEditor.CurrentScenarioAsset);
        if (selected is not null)
        {
            OpenScenarioAssetInProjectScenarioTab(selected.Asset);
            return;
        }
        if (!TrySaveBeforeInlineNavigation(ShowProjectScenarioWorkspace))
            return;
        if (!HasOpenCampaignProject || !SaveCurrentEditorsIfDirty(refreshCatalogEntries: true))
            return;

        _showLooseScenarioWorkspace = false;
        ScenarioEditor.LoadFromText("(no project scenario)", string.Empty);
        ScenarioSession = WccEditorSessionContext.Empty;
        ClearCurrentScenarioDirty();

        RaiseScenarioWorkspaceStateChanged();
        SelectedTabIndex = 1;
        Status = "Showing scenarios from the open project.";
    }

    public async Task OpenProjectUnitEntryAsync(ProjectUnitWorkspaceEntryVm entry)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        var navigation = BeginUnitNavigation();
        if (entry.OwnedAsset is { } current && UnitEditor.IsShowingAsset(current) &&
            UnitEditor.SelectedUnit?.Document.InternalId == entry.OwnedDocumentInternalId &&
            UnitSession.Scope == WccEditorSessionScope.Project)
        {
            // The campaign loader can warm this document before the Units tab opens.
            // Activate its project picker scope without replacing the document or drafts.
            SelectedProjectUnitEntry = entry;
            SelectedTabIndex = 2;
            return;
        }
        var requestedKey = entry.SelectionKey;
        var indexed = entry.LibraryEntityId is { } selectedId ? CaptureIndexedUnitDestination(selectedId, true) : null;
        if (!await ConfirmUnitNavigationAsync(navigation)) return;
        var currentEntry = FindCurrentProjectUnitNavigationRow(requestedKey);
        if (currentEntry is null || (entry.IsProjectOwned && (!ReferenceEquals(entry.OwnedAsset, currentEntry.OwnedAsset) ||
            entry.OwnedDocumentInternalId != currentEntry.OwnedDocumentInternalId)))
        { ReportChangedUnitDestination(navigation); return; }
        entry = currentEntry;
        if (!entry.CanOpen)
        {
            SelectedProjectUnitEntry = entry;
            Status = entry.HasDependencyCandidates
                ? $"{entry.UnitTypeId} is not in the project's dependency scope. {entry.CandidateSummary}. A later Resolve action will let you confirm the package or copy the unit."
                : $"{entry.UnitTypeId} has no exact unit definition in the current project scope and cannot be opened.";
            return;
        }

        if (entry.IsProjectOwned)
        {
            using var ownedLoading = BeginEditorSourceLoading("Opening " + entry.UnitTypeId + "...");
            if (!IsCurrentUnitNavigation(navigation)) return;
            LoadOwnedProjectUnitDocument(entry);
            if (navigation.Version != _unitNavigationVersion) return;
            SelectedProjectUnitEntry = entry;
            SelectedTabIndex = 2;
            Status = $"Opened project unit type: {entry.UnitTypeId}";
            return;
        }

        if (indexed is null || ResolveIndexedUnitDestination(indexed, out var snapshot) is not { } entityId || snapshot is null)
        { ReportChangedUnitDestination(navigation); return; }

        _unitEntityLoadCancellation?.Cancel();
        _unitEntityLoadCancellation?.Dispose();
        _unitEntityLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _unitEntityLoadCancellation.Token;
        using var loading = BeginEditorSourceLoading($"Loading linked unit {entry.UnitTypeId} from {entry.OriginLabel}...");
        if (!IsCurrentUnitNavigation(navigation) || !ReferenceEquals(snapshot, LibrarySnapshot)) return;
        Status = $"Loading linked unit {entry.UnitTypeId} from {entry.OriginLabel}...";

        try
        {
            var loaded = await Task.Run(() => WccLibraryUnitEntityLoader.LoadAsync(snapshot, entityId, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentUnitNavigation(navigation) || !ReferenceEquals(snapshot, LibrarySnapshot) ||
                !LoadedIndexedUnitMatches(indexed, loaded))
            { ReportChangedUnitDestination(navigation); return; }
            UnitSession = new WccEditorSessionContext(
                WccEditorSessionScope.Project,
                WccEditorAccessMode.ReadOnly,
                ProjectSession.RootPath,
                $"Linked project unit (read-only): {entry.UnitTypeId} [{entry.OriginLabel}]");
            UnitEditor.LoadFromExistingUnitAsset(
                loaded.SourceFile,
                loaded.PreferredUnitDocumentInternalId);
            if (navigation.Version != _unitNavigationVersion) return;
            ClearCurrentUnitDirty();
            SelectedProjectUnitEntry = entry;
            RaiseUnitWorkspaceStateChanged();
            SelectedTabIndex = 2;
            Status = loaded.MatchesIndexedContentHash
                ? $"Opened linked unit type read-only: {entry.UnitTypeId} [{entry.OriginLabel}]"
                : $"Opened linked unit {entry.UnitTypeId}, but its source changed after indexing; refresh the package index.";
        }
        catch (OperationCanceledException)
        {
            // A newer unit selection superseded this load.
        }
        catch (Exception ex)
        {
            if (IsCurrentUnitNavigation(navigation))
                Status = $"Could not open linked unit type: {ex.Message}";
        }
    }

    private void LoadOwnedProjectUnitDocument(ProjectUnitWorkspaceEntryVm entry)
    {
        if (entry.OwnedAsset is null)
            return;

        UnitSession = CreateProjectEditorSession($"Project unit: {entry.UnitTypeId}");
        UnitEditor.LoadFromExistingUnitAsset(entry.OwnedAsset, entry.OwnedDocumentInternalId);
        ConfigureCampaignEntryInspection();
        ClearCurrentUnitDirty();
        RaiseUnitWorkspaceStateChanged();
    }

    public void ShowProjectUnitWorkspace()
    {
        if (!TrySaveBeforeInlineNavigation(ShowProjectUnitWorkspace))
            return;
        if (!HasOpenCampaignProject || !SaveCurrentEditorsIfDirty(refreshCatalogEntries: true))
            return;

        _showLooseUnitWorkspace = false;
        RefreshProjectUnitWorkspaceEntries();
        var selected = SelectedProjectUnitEntry;
        if (selected is { IsProjectOwned: true })
            LoadOwnedProjectUnitDocument(selected);
        else if (ProjectOwnedUnitEntries.FirstOrDefault() is { } firstOwned)
        {
            SelectedProjectUnitEntry = firstOwned;
            LoadOwnedProjectUnitDocument(firstOwned);
        }
        else
        {
            UnitEditor.LoadFromText("(no project unit)", string.Empty);
            UnitSession = WccEditorSessionContext.Empty;
            ClearCurrentUnitDirty();
        }

        RaiseUnitWorkspaceStateChanged();
        SelectedTabIndex = 2;
        Status = "Showing unit types from the open project.";
    }

    private void RestoreCampaignScenarioSelectionToCurrentEditorAsset()
    {
        var currentAsset = ScenarioEditor.CurrentScenarioAsset;
        if (currentAsset is null)
            return;

        var current = FindCurrentProjectScenarioRow(currentAsset);
        if (current is null)
            return;

        CampaignEditor.SelectedScenarioFile = current;
        SelectedCampaignScenarioFile = current;
    }

    private void RestoreProjectUnitSelectionToCurrentEditorAsset()
    {
        var currentAsset = UnitEditor.CurrentUnitAsset;
        var currentDocumentId = UnitEditor.SelectedUnit?.Document.InternalId;
        if (currentAsset is null)
            return;

        SelectedProjectUnitEntry = ProjectOwnedUnitEntries.FirstOrDefault(entry =>
            ReferenceEquals(entry.OwnedAsset, currentAsset) &&
            (currentDocumentId is null || entry.OwnedDocumentInternalId == currentDocumentId))
            ?? ProjectReferencedUnitEntries.FirstOrDefault(entry => entry.SelectionKey == SelectedProjectUnitEntry?.SelectionKey);
    }

    private void RestoreStandaloneScenarioSelectionToCurrentEditorAsset()
    {
        var currentAsset = ScenarioEditor.CurrentScenarioAsset;
        if (currentAsset is null)
            return;

        SelectedStandaloneScenarioFile = StandaloneScenarioFiles.FirstOrDefault(
            item => ReferenceEquals(item.Asset, currentAsset));
    }

    private void RestoreStandaloneUnitSelectionToCurrentEditorAsset()
    {
        var currentAsset = UnitEditor.CurrentUnitAsset;
        if (currentAsset is null)
            return;

        SelectedStandaloneUnitFile = StandaloneUnitFiles.FirstOrDefault(
            item => ReferenceEquals(item.Asset, currentAsset));
    }

    public bool AddStandaloneScenarioFromText(string path, string text)
    {
        if (!TrySaveBeforeInlineNavigation(() => AddStandaloneScenarioFromText(path, text)))
            return false;
        if (!UnloadCurrentProjectForLooseFile())
            return false;

        var existing = StandaloneScenarioFiles.FirstOrDefault(x =>
            string.Equals(x.Asset.SourcePath, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            SelectedStandaloneScenarioFile = existing;
            OpenScenarioAssetInShell(existing.Asset);
            Status = "Scenario file already loaded in standalone workspace.";
            return true;
        }

        var project = WccProjectFactory.CreateFromSingleScenario(path, text);
        foreach (var asset in project.ProjectScenarioFiles)
            StandaloneScenarioFiles.Add(new ProjectScenarioFileVm(asset));

        SelectedStandaloneScenarioFile = StandaloneScenarioFiles.LastOrDefault();
        if (SelectedStandaloneScenarioFile is { } selected)
            OpenScenarioAssetInShell(selected.Asset);

        return SelectedStandaloneScenarioFile is not null;
    }

    public void RemoveSelectedStandaloneScenario()
    {
        if (SelectedStandaloneScenarioFile is null)
            return;

        var requestedFile = SelectedStandaloneScenarioFile;
        if (!TrySaveBeforeInlineNavigation(() =>
            {
                SelectedStandaloneScenarioFile = requestedFile;
                RemoveSelectedStandaloneScenario();
            })) return;

        if (!SaveCurrentEditorsIfDirty(refreshCatalogEntries: true))
            return;

        var index = StandaloneScenarioFiles.IndexOf(SelectedStandaloneScenarioFile);
        if (index < 0)
            return;

        StandaloneScenarioFiles.RemoveAt(index);
        SelectedStandaloneScenarioFile = index < StandaloneScenarioFiles.Count
            ? StandaloneScenarioFiles[index]
            : StandaloneScenarioFiles.LastOrDefault();

        if (SelectedStandaloneScenarioFile is { } selected)
        {
            LoadScenarioDocument(selected.Asset, CreateLooseFileEditorSession(selected.Asset.SourcePath ?? selected.Asset.FileName));
            Status = "Removed loose scenario; opened the next loose file.";
        }
        else if (HasOpenCampaignProject)
        {
            ShowProjectScenarioWorkspace();
        }
        else
        {
            ScenarioEditor.LoadFromText("(no scenario)", string.Empty);
            ScenarioSession = WccEditorSessionContext.Empty;
            ClearCurrentScenarioDirty();
            RaiseScenarioWorkspaceStateChanged();
            Status = "Closed the loose scenario session.";
        }
    }

    public bool AddStandaloneUnitFromText(string path, string text)
    {
        if (!TrySaveBeforeInlineNavigation(() => AddStandaloneUnitFromText(path, text)))
            return false;
        if (!UnloadCurrentProjectForLooseFile())
            return false;

        var existing = StandaloneUnitFiles.FirstOrDefault(x =>
            string.Equals(x.Asset.SourcePath, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            SelectedStandaloneUnitFile = existing;
            OpenUnitAssetInShell(existing.Asset);
            Status = "Unit file already loaded in standalone workspace.";
            return true;
        }

        var project = WccProjectFactory.CreateFromSingleUnit(path, text);
        foreach (var asset in project.ProjectUnitFiles)
            StandaloneUnitFiles.Add(new ProjectUnitFileVm(asset));

        SelectedStandaloneUnitFile = StandaloneUnitFiles.LastOrDefault();
        if (SelectedStandaloneUnitFile is { } selected)
            OpenUnitAssetInShell(selected.Asset);

        RaiseUnitWorkspaceStateChanged();
        Status = "Loaded loose unit file.";
        return SelectedStandaloneUnitFile is not null;
    }

    public void RemoveSelectedStandaloneUnit()
    {
        if (SelectedStandaloneUnitFile is null)
            return;

        var requestedFile = SelectedStandaloneUnitFile;
        if (!TrySaveBeforeInlineNavigation(() =>
            {
                SelectedStandaloneUnitFile = requestedFile;
                RemoveSelectedStandaloneUnit();
            })) return;

        if (!SaveCurrentEditorsIfDirty(refreshCatalogEntries: true))
            return;

        var index = StandaloneUnitFiles.IndexOf(SelectedStandaloneUnitFile);
        if (index < 0)
            return;

        StandaloneUnitFiles.RemoveAt(index);
        SelectedStandaloneUnitFile = index < StandaloneUnitFiles.Count
            ? StandaloneUnitFiles[index]
            : StandaloneUnitFiles.LastOrDefault();

        if (SelectedStandaloneUnitFile is { } selected)
        {
            OpenUnitAssetInShell(selected.Asset);
            Status = "Closed loose unit; opened the next loose file.";
        }
        else if (HasOpenCampaignProject)
        {
            ShowProjectUnitWorkspace();
        }
        else
        {
            UnitEditor.LoadFromText("(no unit)", string.Empty);
            UnitSession = WccEditorSessionContext.Empty;
            ClearCurrentUnitDirty();
            RaiseUnitWorkspaceStateChanged();
            Status = "Closed the loose unit session.";
        }
    }


    // ---------------------------------------------------------------------
    // Catalog persistence and sidebar/catalog refresh helpers
    // ---------------------------------------------------------------------
    private void RefreshUnitSidebarEntries()
    {
        lock (_unitDisplaySync)
        {
        var previousExpansion = UnitSidebarGroups.ToDictionary(
            x => x.RaceKey,
            x => x.IsExpanded,
            StringComparer.OrdinalIgnoreCase);

        UnitSidebarGroups.Clear();
        _allUnitSidebarGroups.Clear();

        var query = UnitSidebarSearchText?.Trim();

        var filteredEntries = _allUnitCatalogEntryVms
            .Where(entry =>
                string.IsNullOrWhiteSpace(query) ||
                entry.UnitTypeId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(entry.RaceId) && entry.RaceId.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var raceNames = RaceCatalog.Entries
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => ResolveRaceDisplayName(g.First()),
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in filteredEntries
                     .GroupBy(x => GetUnitSidebarRaceKey(x), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => IsUndefinedRaceKey(x.Key) ? 1 : 0)
                     .ThenBy(x => ResolveRaceGroupSortLabel(x.Key, raceNames), StringComparer.OrdinalIgnoreCase))
        {
            var displayName = ResolveRaceGroupDisplayName(group.Key, raceNames);
            var isExpanded = previousExpansion.TryGetValue(group.Key, out var expanded)
                ? expanded
                : !IsUndefinedRaceKey(group.Key);

            var groupVm = new UnitSidebarRaceGroupVm(group.Key, displayName, isExpanded);

            foreach (var entry in group
                         .OrderBy(x => x.SidebarLabel, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.SourceFileName, StringComparer.OrdinalIgnoreCase))
            {
                groupVm.Units.Add(entry);
            }

            _allUnitSidebarGroups.Add(groupVm);
            UnitSidebarGroups.Add(groupVm);
        }

        RefreshUnitSidebarSelectionState();
        ExpandGroupForSelectedUnit();
        }
    }

    private void RefreshUnitSidebarSelectionState()
    {
        foreach (var entry in _allUnitCatalogEntryVms)
            entry.IsSidebarSelected = ReferenceEquals(entry, SelectedUnitCatalogEntry);
    }

    private void ExpandGroupForSelectedUnit()
    {
        if (SelectedUnitCatalogEntry is null)
            return;

        var raceKey = GetUnitSidebarRaceKey(SelectedUnitCatalogEntry);
        var group = UnitSidebarGroups.FirstOrDefault(x => string.Equals(x.RaceKey, raceKey, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
            group.IsExpanded = true;
    }

    private static string GetUnitSidebarRaceKey(UnitTypeCatalogEntryVm entry)
        => string.IsNullOrWhiteSpace(entry.RaceId) ? UndefinedRaceKey : entry.RaceId!.Trim();

    private static bool IsUndefinedRaceKey(string raceKey)
        => string.Equals(raceKey, UndefinedRaceKey, StringComparison.OrdinalIgnoreCase);

    private static string ResolveRaceDisplayName(RaceCatalogEntry entry)
    {
        return FirstNonEmpty(
                   WmlDisplayStringNormalizer.Normalize(entry.PluralName),
                   WmlDisplayStringNormalizer.Normalize(entry.Name),
                   WmlDisplayStringNormalizer.Normalize(entry.MaleName),
                   WmlDisplayStringNormalizer.Normalize(entry.FemaleName),
                   WmlDisplayStringNormalizer.HumanizeIdentifier(entry.Id),
                   entry.Id)
               ?? entry.Id;
    }

    private static string ResolveRaceGroupDisplayName(
        string raceKey,
        IReadOnlyDictionary<string, string> raceNames)
    {
        if (IsUndefinedRaceKey(raceKey))
            return "Undefined";

        return raceNames.TryGetValue(raceKey, out var displayName)
            ? displayName
            : raceKey;
    }

    private static string ResolveRaceGroupSortLabel(
        string raceKey,
        IReadOnlyDictionary<string, string> raceNames)
        => ResolveRaceGroupDisplayName(raceKey, raceNames);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private void RefreshCatalogInspectorUnitEntries()
    {
        lock (_unitDisplaySync)
        {
        CatalogInspectorUnitEntries.Clear();

        var query = CatalogInspectorUnitSearchText?.Trim();

        foreach (var entry in _allUnitCatalogEntryVms)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!entry.UnitTypeId.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            CatalogInspectorUnitEntries.Add(entry);
        }

        RebuildCatalogInspectorUnitRaceGroups();
        }
    }

    private void RebuildCatalogInspectorUnitRaceGroups()
    {
        var previousExpansion = CatalogInspectorUnitRaceGroups.ToDictionary(
            group => group.RaceKey,
            group => group.IsExpanded,
            StringComparer.OrdinalIgnoreCase);
        CatalogInspectorUnitRaceGroups.Clear();

        var raceNames = RaceCatalog.Entries
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => ResolveRaceDisplayName(group.First()),
                StringComparer.OrdinalIgnoreCase);

        foreach (var raceGroup in CatalogInspectorUnitEntries
                     .GroupBy(GetUnitSidebarRaceKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => IsUndefinedRaceKey(group.Key) ? 1 : 0)
                     .ThenBy(
                         group => ResolveRaceGroupSortLabel(group.Key, raceNames),
                         StringComparer.OrdinalIgnoreCase))
        {
            var displayName = ResolveRaceGroupDisplayName(raceGroup.Key, raceNames);
            var isExpanded = !string.IsNullOrWhiteSpace(CatalogInspectorUnitSearchText) ||
                             (previousExpansion.TryGetValue(raceGroup.Key, out var expanded) && expanded);
            var groupVm = new UnitSidebarRaceGroupVm(raceGroup.Key, displayName, isExpanded);
            foreach (var entry in raceGroup
                         .OrderBy(item => item.SidebarLabel, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.OriginLabel, StringComparer.OrdinalIgnoreCase))
            {
                groupVm.Units.Add(entry);
            }

            CatalogInspectorUnitRaceGroups.Add(groupVm);
        }

        ExpandCatalogInspectorGroupForSelectedUnit();
        OnPropertyChanged(nameof(HasCatalogInspectorUnitRaceGroups));
    }

    private void ExpandCatalogInspectorGroupForSelectedUnit()
    {
        if (SelectedInspectorUnitCatalogEntry is null)
            return;

        var raceKey = GetUnitSidebarRaceKey(SelectedInspectorUnitCatalogEntry);
        var group = CatalogInspectorUnitRaceGroups.FirstOrDefault(candidate =>
            string.Equals(candidate.RaceKey, raceKey, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
            group.IsExpanded = true;
    }

    private void RefreshCatalogInspectorAssetEntries()
    {
        CatalogInspectorAssetEntries.Clear();

        var query = CatalogInspectorAssetSearchText?.Trim();

        foreach (var entry in _allAssetRegistryEntryVms)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!entry.LookupPath.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.KindLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            CatalogInspectorAssetEntries.Add(entry);
        }
    }

    private void RefreshSelectedAssetReferences()
    {
        SelectedAssetReferences.Clear();

        if (SelectedAssetRegistryEntry is null)
            return;

        foreach (var reference in SelectedAssetRegistryEntry.Entry.References
                     .OrderBy(r => r.OwnerBlockKind, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.SourceFieldKey, StringComparer.OrdinalIgnoreCase))
        {
            SelectedAssetReferences.Add(new AssetReferenceVm(reference));
        }
    }

    private void RefreshSelectedAssetResolution()
    {
        SelectedAssetResolvedPath = null;
        SelectedAssetResolutionStatus = null;
        SelectedAssetCandidateCountText = null;

        if (!ReferenceEquals(_selectedAssetPreviewBitmap, null))
        {
            _selectedAssetPreviewBitmap?.Dispose();
            SelectedAssetPreviewBitmap = null;
        }

        SelectedAssetPreviewStatus = "No preview available.";

        if (SelectedAssetRegistryEntry is null)
            return;

        var templatedRef = SelectedAssetRegistryEntry.Entry.References
            .FirstOrDefault(r => r.IsTemplatedPath);

        if (templatedRef is not null)
        {
            SelectedAssetResolutionStatus = !string.IsNullOrWhiteSpace(templatedRef.TemplatedMacroName)
                ? $"Templated macro path via {templatedRef.TemplatedMacroName} (not resolved yet)"
                : "Templated macro path (not resolved yet)";

            SelectedAssetCandidateCountText = "0";
            return;
        }

        if (LibrarySnapshot is not { } snapshot)
        {
            SelectedAssetResolutionStatus = "The package index is not ready.";
            SelectedAssetCandidateCountText = "0";
            return;
        }

        var result = WccAssetPathResolver.ResolveFromLibrarySnapshot(
            snapshot,
            SelectedAssetRegistryEntry.Entry,
            WesnothInstallPath);

        SelectedAssetResolvedPath = result.ResolvedAbsolutePath;
        SelectedAssetCandidateCountText = result.CandidatePaths.Count.ToString();
        SelectedAssetResolutionStatus = result.IsResolved
            ? "Resolved on disk"
            : "Not resolved yet";

        if (!result.IsResolved)
            return;

        if (SelectedAssetRegistryEntry.Entry.Kind is WccAssetReferenceKind.Image
            or WccAssetReferenceKind.Portrait
            or WccAssetReferenceKind.Icon
            or WccAssetReferenceKind.BackgroundImage)
        {
            SelectedAssetPreviewBitmap = BitmapFileLoader.TryLoad(result.ResolvedAbsolutePath);
            SelectedAssetPreviewStatus = SelectedAssetPreviewBitmap is not null
                ? "Preview loaded."
                : "Resolved image, but preview could not be loaded.";
        }
    }

    private void SaveSettings()
    {
        WccSettingsStorage.Save(_settingsFilePath, _settings);
    }

    private WccWesnothInstallationProfile? FindActiveWesnothInstallationProfile()
    {
        var activeId = _settings.ActiveWesnothInstallationProfileId;
        return _settings.WesnothInstallationProfiles.FirstOrDefault(profile =>
                   string.Equals(profile.Id, activeId, StringComparison.Ordinal)) ??
               _settings.WesnothInstallationProfiles.FirstOrDefault();
    }

    private WccWesnothInstallationProfile EnsureActiveWesnothInstallationProfile()
    {
        if (FindActiveWesnothInstallationProfile() is { } existing)
        {
            if (!string.Equals(
                    _settings.ActiveWesnothInstallationProfileId,
                    existing.Id,
                    StringComparison.Ordinal))
            {
                _settings.ActiveWesnothInstallationProfileId = existing.Id;
            }

            return existing;
        }

        var profile = new WccWesnothInstallationProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = CreateUniqueWesnothProfileName("Wesnoth installation")
        };
        _settings.WesnothInstallationProfiles.Add(profile);
        _settings.ActiveWesnothInstallationProfileId = profile.Id;
        WesnothInstallationProfiles.Add(profile);
        ActiveWesnothInstallationProfileName = profile.Name;
        RaiseWesnothProfilePropertiesChanged();
        return profile;
    }

    private void ActivateWesnothInstallationProfile(
        WccWesnothInstallationProfile? requestedProfile)
    {
        if (requestedProfile is null)
            return;
        if (IsLibraryIndexRefreshing)
        {
            OnPropertyChanged(nameof(ActiveWesnothInstallationProfile));
            Status = "Wait for the current package refresh before switching installations.";
            return;
        }
        if (!CanResetLibraryDisplaysForProfileChange())
        {
            OnPropertyChanged(nameof(ActiveWesnothInstallationProfile));
            return;
        }

        var profile = _settings.WesnothInstallationProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, requestedProfile.Id, StringComparison.Ordinal));
        if (profile is null || string.Equals(
                _settings.ActiveWesnothInstallationProfileId,
                profile.Id,
                StringComparison.Ordinal))
        {
            return;
        }

        _settings.ActiveWesnothInstallationProfileId = profile.Id;
        ActiveWesnothInstallationProfileName = profile.Name;
        _activeWesnothInstallation = null;
        var detection = RefreshWesnothInstallationIdentity(persistChanges: false);
        SaveSettings();
        ConfigureEditorsForActiveWesnothInstallation();
        RefreshAfterWesnothProfileChange();
        RaiseWesnothProfilePropertiesChanged();
        Status = detection.Identity is { } installation
            ? $"Selected {profile.Name}: Wesnoth {installation.ExactVersion} " +
              $"(target {installation.CompatibilityTarget}). Refresh the package index if needed."
            : $"Selected {profile.Name}. {detection.Error}";
    }

    private string CreateUniqueWesnothProfileName(string baseName)
    {
        var names = _settings.WesnothInstallationProfiles
            .Select(profile => profile.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName} {suffix}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private void ConfigureEditorsForActiveWesnothInstallation()
    {
        var activeInstallPath = WesnothInstallPath;
        var activeProfile = FindActiveWesnothInstallationProfile();
        var activeCompatibilityTarget = ActiveWesnothInstallationProfile?.CompatibilityTarget ??
                                        _activeWesnothInstallation?.CompatibilityTarget;
        var projectProfile = ResolvePinnedProjectInstallationProfile();
        var projectInstallPath = ResolvePinnedProjectInstallPath();
        var projectCompatibilityTarget =
            _currentProjectMetadata?.Compatibility?.TargetWesnothVersion ??
            activeCompatibilityTarget;
        UnitEditor.SetWesnothInstallPath(
            UnitSession.Scope == WccEditorSessionScope.Project
                ? projectInstallPath
                : activeInstallPath);
        UnitEditor.SetCompatibilityTarget(
            UnitSession.Scope == WccEditorSessionScope.Project
                ? projectCompatibilityTarget
                : activeCompatibilityTarget);
        LibraryUnitEditor.SetWesnothInstallPath(WesnothInstallPath);
        LibraryUnitEditor.SetCompatibilityTarget(activeCompatibilityTarget);
        var scenarioProfile = ScenarioSession.Scope == WccEditorSessionScope.Project
            ? projectProfile
            : activeProfile;
        ScenarioEditor.SetWesnothRuntimeProfile(
            ScenarioSession.Scope == WccEditorSessionScope.Project
                ? projectInstallPath
                : activeInstallPath,
            scenarioProfile?.AddonsPath,
            BuildWesnothRuntimeProfileIdentity(scenarioProfile));
        var scenarioSourceProfile = ScenarioSession.Scope == WccEditorSessionScope.Project
            ? ResolvePinnedProjectSourceResourceProfile()
            : activeProfile;
        ScenarioEditor.SetWesnothSourceResourceProfile(
            scenarioSourceProfile?.InstallPath ?? scenarioSourceProfile?.LastDetectedInstallation?.InstallRoot,
            scenarioSourceProfile?.AddonsPath,
            BuildWesnothSourceResourceProfileIdentity(scenarioSourceProfile));
        ConfigureCampaignFieldHost();
    }

    private string? ResolvePinnedProjectInstallPath()
    {
        var profile = ResolvePinnedProjectInstallationProfile();
        return profile?.InstallPath ?? profile?.LastDetectedInstallation?.InstallRoot;
    }

    private WccWesnothInstallationProfile? ResolvePinnedProjectInstallationProfile()
    {
        if (_isCurrentProjectMetadataLoadPending)
            return null;

        // A current project whose metadata failed to load must not inherit an
        // unrelated active runtime. A successfully loaded unpinned metadata
        // object still follows the active profile below.
        if (_currentProjectMetadata is null &&
            !string.IsNullOrWhiteSpace(_currentProjectMetadataRoot))
        {
            return null;
        }

        var pin = _currentProjectMetadata?.Compatibility?.CoreRevision;
        if (pin is null)
            return FindActiveWesnothInstallationProfile();

        string? pinnedCoreRoot = null;
        if (LibrarySnapshot is { } snapshot)
        {
            var resolution = WccProjectCorePinResolver.Resolve(snapshot.Manifest, pin);
            if (!resolution.IsResolved) return null;
            pinnedCoreRoot = resolution.Package!.SourceLocation;
        }

        var candidates = new List<WccWesnothInstallationProfile>();
        foreach (var profile in _settings.WesnothInstallationProfiles)
        {
            var detected = profile.LastDetectedInstallation;
            if (detected is null ||
                !string.Equals(
                    detected.ExactVersion,
                    pin.ExactWesnothVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    detected.CompatibilityTarget,
                    pin.TargetWesnothVersion,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(pinnedCoreRoot) &&
                (string.IsNullOrWhiteSpace(detected.CoreDataRoot) ||
                 !PathsEqual(detected.CoreDataRoot, pinnedCoreRoot)))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(pin.ContentFingerprint) &&
                (string.IsNullOrWhiteSpace(detected.CoreContentFingerprint) ||
                 !string.Equals(
                     pin.ContentFingerprint,
                     detected.CoreContentFingerprint,
                     StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            candidates.Add(profile);
        }

        var activeProfileId = FindActiveWesnothInstallationProfile()?.Id;
        var projectRoot = ResolveCurrentProjectOrSessionRoot();
        var selected = candidates
            .OrderByDescending(profile => ProfileAddonsContainRoot(profile, projectRoot))
            .ThenByDescending(profile => string.Equals(
                profile.Id,
                activeProfileId,
                StringComparison.Ordinal))
            .ThenBy(profile => profile.Id ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(profile => profile.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (selected is not null)
            return selected;

        // An exact project pin must not silently fall back to another active Core. Its
        // package data remains inspectable, but engine-backed previews stay unavailable.
        return null;
    }

    private string? ResolveCurrentProjectOrSessionRoot()
    {
        if (!string.IsNullOrWhiteSpace(_currentProjectMetadataRoot))
            return _currentProjectMetadataRoot;
        if (ProjectSession.Scope == WccEditorSessionScope.Project &&
            !string.IsNullOrWhiteSpace(ProjectSession.RootPath))
        {
            return ProjectSession.RootPath;
        }
        if (ScenarioSession.Scope == WccEditorSessionScope.Project &&
            !string.IsNullOrWhiteSpace(ScenarioSession.RootPath))
        {
            return ScenarioSession.RootPath;
        }
        if (UnitSession.Scope == WccEditorSessionScope.Project &&
            !string.IsNullOrWhiteSpace(UnitSession.RootPath))
        {
            return UnitSession.RootPath;
        }

        return null;
    }

    private static bool ProfileAddonsContainRoot(
        WccWesnothInstallationProfile profile,
        string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(profile.AddonsPath) ||
            string.IsNullOrWhiteSpace(projectRoot))
        {
            return false;
        }

        return PathsEqual(profile.AddonsPath, projectRoot) ||
               IsPathInsideRoot(profile.AddonsPath, projectRoot);
    }

    private static string BuildWesnothRuntimeProfileIdentity(
        WccWesnothInstallationProfile? profile)
    {
        if (profile is null)
            return "unconfigured-runtime";

        var detected = profile.LastDetectedInstallation;
        return string.Join(
            "\u001f",
            profile.Id,
            detected?.ExactVersion ?? string.Empty,
            detected?.CompatibilityTarget ?? profile.CompatibilityTarget ?? string.Empty,
            detected?.CoreContentFingerprint ?? string.Empty);
    }

    private void RefreshAfterWesnothProfileChange()
    {
        ResetLibraryDisplaysForProfileChange();
        RebuildActiveLibraryView();
        RefreshUnitCatalogEntries(selectFallbackEntry: false);
        RefreshSelectedAssetResolution();
        RestoreLibraryPickerProjection();
        RefreshUnitSidebarEntries();
        OnPropertyChanged(nameof(LibrarySnapshot));
        LibrarySnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanResetLibraryDisplaysForProfileChange()
    {
        if (!_libraryUnitEditorDirty &&
            !(UnitSession.Scope == WccEditorSessionScope.Library && _unitEditorDirty))
        {
            return true;
        }

        Status =
            "Save the edited Library unit before switching Wesnoth runtime profiles. " +
            "WCC did not change the active profile or discard the edits.";
        return false;
    }

    private void ResetLibraryDisplaysForProfileChange()
    {
        ResetUnitDisplayScopes();
        _unitEntityLoadCancellation?.Cancel();
        _unitEntityLoadCancellation?.Dispose();
        _unitEntityLoadCancellation = null;
        _libraryUnitEntityLoadCancellation?.Cancel();
        _libraryUnitEntityLoadCancellation?.Dispose();
        _libraryUnitEntityLoadCancellation = null;
        _catalogInspectorDetailLoadCancellation?.Cancel();
        _catalogInspectorDetailLoadCancellation?.Dispose();
        _catalogInspectorDetailLoadCancellation = null;

        _pendingManagedUnitSourcePathSelection = null;
        _pendingManagedInspectorUnitSourcePathSelection = null;
        SelectedUnitCatalogEntry = null;
        SelectedInspectorUnitCatalogEntry = null;
        SelectedAssetRegistryEntry = null;
        SelectedMacroCatalogEntry = null;
        SelectedMovementTypeCatalogEntry = null;
        SelectedTraitCatalogEntry = null;
        SelectedScheduleCatalogEntry = null;
        SelectedAbilityCatalogEntry = null;
        SelectedWeaponSpecialCatalogEntry = null;
        SelectedRaceCatalogEntry = null;
        SelectedResistanceDefaultsCatalogEntry = null;
        SelectedTerrainDefaultsCatalogEntry = null;
        SelectedTerrainTypeCatalogEntry = null;
        SelectedItemCatalogEntry = null;

        if (UnitSession.Scope == WccEditorSessionScope.Library)
        {
            UnitSession = WccEditorSessionContext.Empty;
            UnitEditor.LoadFromText("(no Library unit)", string.Empty);
            ClearCurrentUnitDirty();
            RaiseUnitWorkspaceStateChanged();
            OnPropertyChanged(nameof(CanSaveCurrentContext));
            OnPropertyChanged(nameof(CanDeleteCurrentUnit));
            OnPropertyChanged(nameof(CanAddCurrentUnitToLibrary));
            OnPropertyChanged(nameof(CanCloneCurrentUnit));
        }

        LibraryUnitSession = WccEditorSessionContext.Empty;
        LibraryUnitEditor.LoadFromText("(no Library unit)", string.Empty);
        ClearLibraryUnitDirty();
        RaiseLibraryUnitEditorStateChanged();
    }

    private void RebuildActiveLibraryView()
    {
        var visibleRevisionIds = new HashSet<WccPackageRevisionId>();
        if (LibrarySnapshot is { } snapshot &&
            TryFindSelectedProfileDistributionPackage(snapshot, out var distributionPackage))
        {
            visibleRevisionIds.UnionWith(
                WccStandaloneLibraryRevisionScope.GetVisibleRevisionIds(
                    snapshot,
                    distributionPackage.Id, ActiveWesnothInstallationProfile?.AddonsPath));
        }

        var registry = LibrarySnapshot is { } currentSnapshot
            ? WccLibraryAssetRegistryProjection.Build(currentSnapshot, visibleRevisionIds)
            : new WccAssetRegistry();
        ApplyActiveLibraryView(visibleRevisionIds, registry);
    }

    private void ApplyActiveLibraryView(
        IEnumerable<WccPackageRevisionId> visibleRevisionIds,
        WccAssetRegistry assetRegistry)
    {
        _activeLibraryRevisionIds.Clear();
        _activeLibraryRevisionIds.UnionWith(visibleRevisionIds);
        AssetRegistry = assetRegistry;
        SelectedAssetRegistryEntry = null;
        _allAssetRegistryEntryVms.Clear();
        CatalogInspectorAssetEntries.Clear();
        _preparedCatalogInspectorTabs.Clear();
        MarkDirectPresetCatalogsDirty();

        OnPropertyChanged(nameof(AssetRegistryEntryCount));
        OnPropertyChanged(nameof(AssetRegistrySummary));
        OnPropertyChanged(nameof(LibraryPackageCount));
        OnPropertyChanged(nameof(LibraryEntityCount));
        OnPropertyChanged(nameof(LibraryReferenceCount));
    }

    private bool TryFindSelectedProfileDistributionPackage(
        WccLibrarySnapshot snapshot,
        out WccPackageRecord package)
    {
        package = null!;
        var coreRoot = _activeWesnothInstallation?.CoreDataRoot ??
                       ActiveWesnothInstallationProfile?.LastDetectedInstallation?.CoreDataRoot;
        if (string.IsNullOrWhiteSpace(coreRoot) && !string.IsNullOrWhiteSpace(WesnothInstallPath))
        {
            try
            {
                coreRoot = WccCoreInstallPaths.GetCoreDataRoot(WesnothInstallPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(coreRoot))
            return false;

        package = snapshot.PackagesById.Values.FirstOrDefault(candidate =>
            candidate.SourceKind == WccPackageSourceKinds.WesnothMainline &&
            candidate.ContentKind == WccPackageContentKinds.ResourcePack &&
            candidate.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
            PathsEqual(candidate.SourceLocation, coreRoot))!;
        return package is not null;
    }

    private static bool TryFindProfileDistributionPackage(
        WccLibrarySnapshot snapshot,
        WccWesnothInstallationProfile? profile,
        out WccPackageRecord package)
    {
        package = null!;
        if (profile is null)
            return false;

        var coreRoot = profile.LastDetectedInstallation?.CoreDataRoot;
        if (string.IsNullOrWhiteSpace(coreRoot) &&
            !string.IsNullOrWhiteSpace(profile.InstallPath))
        {
            try
            {
                var installPath = Path.GetFullPath(profile.InstallPath);
                var installRoot = File.Exists(installPath)
                    ? Path.GetDirectoryName(installPath)
                    : installPath;
                if (!string.IsNullOrWhiteSpace(installRoot))
                    coreRoot = WccCoreInstallPaths.GetCoreDataRoot(installRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(coreRoot))
            return false;

        package = snapshot.PackagesById.Values.FirstOrDefault(candidate =>
            candidate.IsAvailable &&
            candidate.SourceKind == WccPackageSourceKinds.WesnothMainline &&
            candidate.ContentKind == WccPackageContentKinds.ResourcePack &&
            candidate.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
            PathsEqual(candidate.SourceLocation, coreRoot))!;
        return package is not null;
    }

    public bool IsVisibleInActiveLibrary(WccEntityRecord entity)
        => LibrarySnapshot is { } snapshot &&
           WccLibraryRevisionVisibility.ContainsEntity(
               snapshot,
               _activeLibraryRevisionIds,
               entity);

    private void RaiseWesnothProfilePropertiesChanged()
    {
        OnPropertyChanged(nameof(ActiveWesnothInstallationProfile));
        OnPropertyChanged(nameof(HasWesnothInstallationProfiles));
        OnPropertyChanged(nameof(ActiveWesnothInstallationProfileName));
        OnPropertyChanged(nameof(WesnothInstallPath));
        OnPropertyChanged(nameof(HasWesnothInstallPath));
        OnPropertyChanged(nameof(HasDetectedWesnothInstallation));
        OnPropertyChanged(nameof(DetectedWesnothVersion));
        OnPropertyChanged(nameof(TargetWesnothVersion));
        OnPropertyChanged(nameof(WesnothReleaseLine));
        OnPropertyChanged(nameof(DetectedCoreFingerprint));
        OnPropertyChanged(nameof(WesnothInstallationIdentityStatus));
        OnPropertyChanged(nameof(ActiveWesnothProfileRoleStatus));
        OnPropertyChanged(nameof(AddonsPath));
        OnPropertyChanged(nameof(HasAddonsPath));
        OnPropertyChanged(nameof(CanClearAddonsPath));
        RaiseProjectCompatibilityPropertiesChanged();
    }

    private WesnothInstallationDetectionResult RefreshWesnothInstallationIdentity(
        bool persistChanges)
    {
        var profile = FindActiveWesnothInstallationProfile();
        if (profile is null)
        {
            _activeWesnothInstallation = null;
            WesnothInstallationIdentityStatus = "No Wesnoth installation profile is configured.";
            RaiseWesnothProfilePropertiesChanged();
            return new WesnothInstallationDetectionResult
            {
                Error = "No Wesnoth installation profile is configured."
            };
        }

        var detection = WesnothInstallationIdentityDetector.Detect(profile.InstallPath);
        return ApplyWesnothInstallationDetection(persistChanges, profile, detection);
    }

    private WesnothInstallationDetectionResult ApplyWesnothInstallationDetection(bool persistChanges,
        WccWesnothInstallationProfile profile, WesnothInstallationDetectionResult detection)
    {
        var previous = profile.LastDetectedInstallation;
        var installPathChanged = false;
        var profileTargetChanged = false;
        WccDetectedWesnothInstallation? next = previous;

        _activeWesnothInstallation = detection.Identity;
        if (detection.Identity is { } installation)
        {
            if (!string.IsNullOrWhiteSpace(profile.CompatibilityTarget) &&
                !string.Equals(
                    profile.CompatibilityTarget,
                    installation.CompatibilityTarget,
                    StringComparison.Ordinal))
            {
                var mismatch =
                    $"Profile '{profile.Name}' targets Wesnoth {profile.CompatibilityTarget}, " +
                    $"but the configured installation now targets {installation.CompatibilityTarget}. " +
                    "Choose or create the matching installation profile.";
                _activeWesnothInstallation = null;
                WesnothInstallationIdentityStatus = mismatch;
                RaiseWesnothProfilePropertiesChanged();
                return detection with { Identity = null, Error = mismatch };
            }

            if (string.IsNullOrWhiteSpace(profile.CompatibilityTarget))
            {
                profile.CompatibilityTarget = installation.CompatibilityTarget;
                profileTargetChanged = true;
            }

            if (!PathsEqual(WesnothInstallPath, installation.InstallRoot))
            {
                WesnothInstallPath = installation.InstallRoot;
                installPathChanged = true;
            }
            next = new WccDetectedWesnothInstallation
            {
                InstallRoot = installation.InstallRoot,
                ExecutablePath = installation.ExecutablePath,
                CoreDataRoot = installation.CoreDataRoot,
                CampaignsRoot = installation.CampaignsRoot,
                SchemaRoot = installation.SchemaRoot,
                ExactVersion = installation.ExactVersion,
                CompatibilityTarget = installation.CompatibilityTarget,
                ReleaseLine = installation.Version.ReleaseLineDisplay,
                CoreContentFingerprint = CanReuseCoreFingerprint(previous, installation)
                    ? previous?.CoreContentFingerprint
                    : null
            };

            var warningSuffix = detection.Warnings.Count == 0
                ? string.Empty
                : " " + string.Join(" ", detection.Warnings);
            WesnothInstallationIdentityStatus =
                $"Detected Wesnoth {installation.ExactVersion} " +
                $"({installation.Version.ReleaseLineDisplay.ToLowerInvariant()} line; " +
                $"compatibility target {installation.CompatibilityTarget}).{warningSuffix}";
        }
        else
        {
            WesnothInstallationIdentityStatus = detection.Error ??
                                                "The configured Wesnoth installation could not be detected.";
        }

        var changed = !DetectedInstallationsEqual(previous, next);
        if (detection.Identity is not null)
            profile.LastDetectedInstallation = next;

        OnPropertyChanged(nameof(HasDetectedWesnothInstallation));
        OnPropertyChanged(nameof(DetectedWesnothVersion));
        OnPropertyChanged(nameof(TargetWesnothVersion));
        OnPropertyChanged(nameof(WesnothReleaseLine));
        OnPropertyChanged(nameof(DetectedCoreFingerprint));
        if (persistChanges && (changed || installPathChanged || profileTargetChanged))
            SaveSettings();

        return detection;
    }

    private static bool DetectedInstallationsEqual(
        WccDetectedWesnothInstallation? left,
        WccDetectedWesnothInstallation? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;

        return string.Equals(left.InstallRoot, right.InstallRoot, StringComparison.Ordinal) &&
               string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.Ordinal) &&
               string.Equals(left.CoreDataRoot, right.CoreDataRoot, StringComparison.Ordinal) &&
               string.Equals(left.CampaignsRoot, right.CampaignsRoot, StringComparison.Ordinal) &&
               string.Equals(left.SchemaRoot, right.SchemaRoot, StringComparison.Ordinal) &&
               string.Equals(left.ExactVersion, right.ExactVersion, StringComparison.Ordinal) &&
               string.Equals(
                   left.CompatibilityTarget,
                   right.CompatibilityTarget,
                   StringComparison.Ordinal) &&
               string.Equals(left.ReleaseLine, right.ReleaseLine, StringComparison.Ordinal) &&
               string.Equals(
                   left.CoreContentFingerprint,
                   right.CoreContentFingerprint,
                   StringComparison.Ordinal);
    }

    private static bool CanReuseCoreFingerprint(
        WccDetectedWesnothInstallation? previous,
        WesnothInstallationIdentity current)
        => previous is not null &&
           PathsEqual(previous.InstallRoot, current.InstallRoot) &&
           PathsEqual(previous.CoreDataRoot, current.CoreDataRoot) &&
           string.Equals(previous.ExactVersion, current.ExactVersion, StringComparison.Ordinal);

    private void UpdateDetectedCoreFingerprint(
        string profileId,
        string? contentFingerprint)
    {
        var profile = _settings.WesnothInstallationProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.Ordinal));
        var detected = profile?.LastDetectedInstallation;
        if (detected is null || string.IsNullOrWhiteSpace(contentFingerprint) ||
            string.Equals(
                detected.CoreContentFingerprint,
                contentFingerprint,
                StringComparison.Ordinal))
        {
            return;
        }

        detected.CoreContentFingerprint = contentFingerprint;
        SaveSettings();
        if (string.Equals(
                _settings.ActiveWesnothInstallationProfileId,
                profileId,
                StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(DetectedCoreFingerprint));
        }
    }

    // ---------------------------------------------------------------------
    // Lazy Library projections that are not owned by a shared manager view.
    // ---------------------------------------------------------------------
    public Task PrepareCatalogInspectorTabAsync(int tabIndex)
    {
        if (LibrarySnapshot is null || !_preparedCatalogInspectorTabs.Add(tabIndex))
            return Task.CompletedTask;

        switch (tabIndex)
        {
            case 0:
                RefreshCatalogInspectorUnitEntries();
                break;
            case 1:
                RefreshAssetRegistryEntries();
                break;
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<WccCatalogItem> QueryLibraryItems(WccEntityTypeId entityType)
        => LibrarySnapshot is null
            ? Array.Empty<WccCatalogItem>()
            : WccLibraryCatalogProjection.Query(LibrarySnapshot, new WccCatalogQuery
            {
                EntityType = entityType,
                VisibleRevisionIds = _activeLibraryRevisionIds,
                Take = int.MaxValue
            }).Items;

    private string? GetSourcePath(WccCatalogItem item)
        => LibrarySnapshot is not null &&
           LibrarySnapshot.SourceFilesById.TryGetValue(item.SourceFileId, out var sourceFile)
            ? sourceFile.StoragePath
            : null;

    private string GetSourceFileName(WccCatalogItem item)
        => Path.GetFileName(GetSourcePath(item)) is { Length: > 0 } fileName
            ? fileName
            : Path.GetFileName(item.SourceRelativePath);

    private WccSourceOrigin GetProjectedOrigin(WccCatalogItem item)
    {
        if (LibrarySnapshot is null || item.PackageId is not { } packageId ||
            !LibrarySnapshot.PackagesById.TryGetValue(packageId, out var package))
        {
            return WccSourceOrigin.ImportedExternal;
        }

        return package.SourceKind switch
        {
            var sourceKind when sourceKind == WccPackageSourceKinds.WesnothMainline =>
                WccSourceOrigin.CoreInstall,
            var sourceKind when sourceKind == WccPackageSourceKinds.WccManagedLibrary =>
                WccSourceOrigin.WccGenerated,
            _ => WccSourceOrigin.ImportedExternal
        };
    }

    private static string? GetSummary(WccCatalogItem item, string key)
        => item.Summary.TryGetValue(key, out var value) ? value : null;

    private async Task LoadSelectedInspectorEntityRawTextAsync(
        WccEntityId? entityId,
        Func<bool> isStillSelected,
        Action<string> applyRawText,
        string propertyName)
    {
        if (entityId is null || LibrarySnapshot is not { } snapshot)
            return;

        var cancellationToken = ReplaceCatalogInspectorDetailCancellation();
        try
        {
            var content = await WccLibraryEntityContentLoader.LoadAsync(
                snapshot,
                entityId.Value,
                cancellationToken);
            if (!isStillSelected() || cancellationToken.IsCancellationRequested)
                return;

            applyRawText(content.EntityRawText);
            OnPropertyChanged(propertyName);
            if (!content.MatchesIndexedContentHash)
                Status = "Inspector source changed after indexing; refresh the package index.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (isStillSelected())
                Status = $"Could not load inspector detail: {ex.Message}";
        }
    }

    private async Task LoadSelectedMacroDefinitionsAsync(MacroCatalogEntryVm? selected)
    {
        if (selected is null || LibrarySnapshot is not { } snapshot)
            return;

        var entities = snapshot.FindEntities(WccEntityTypes.MacroDefinition)
            .Where(IsVisibleInActiveLibrary)
            .Where(x => string.Equals(
                x.ExportSymbol,
                selected.Name,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entities.Length == 0)
            return;

        var cancellationToken = ReplaceCatalogInspectorDetailCancellation();
        try
        {
            var loaded = await Task.WhenAll(entities.Select(async entity =>
            {
                var content = await WccLibraryEntityContentLoader.LoadAsync(
                    snapshot,
                    entity.Id,
                    cancellationToken);
                return (Entity: entity, Content: content);
            }));
            if (!ReferenceEquals(SelectedMacroCatalogEntry, selected) ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var definitions = selected.Entry.ScopedEntries
                .SelectMany(x => x.DefinitionOccurrences)
                .Where(x => !string.IsNullOrWhiteSpace(x.RawText))
                .ToList();
            definitions.AddRange(loaded.Select(x => new WccMacroDefinitionOccurrence(
                selected.Name,
                x.Content.SourceFile.StoragePath,
                Path.GetFileName(x.Content.SourceFile.RelativePath),
                x.Content.EntityRawText,
                x.Entity.SourceSpan?.StartLine ?? 0,
                x.Entity.SourceSpan?.EndLineExclusive ?? x.Entity.SourceSpan?.StartLine ?? 0,
                ParseSummaryList(x.Entity.Summary, "direct_tags"),
                ParseSummaryList(x.Entity.Summary, "direct_macro_invocations"))));

            SelectedMacroDefinitions.Clear();
            foreach (var definition in definitions
                         .OrderBy(x => x.SourceFileName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.StartLineIndex))
            {
                SelectedMacroDefinitions.Add(new MacroDefinitionOccurrenceVm(definition));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(SelectedMacroCatalogEntry, selected))
                Status = $"Could not load macro definitions: {ex.Message}";
        }
    }

    private async Task LoadSelectedMacroRawTextAsync(
        string? macroName,
        Func<bool> isStillSelected,
        Action<string> applyRawText,
        string propertyName)
    {
        if (string.IsNullOrWhiteSpace(macroName) || LibrarySnapshot is not { } snapshot)
            return;

        var entity = snapshot.FindEntities(WccEntityTypes.MacroDefinition)
            .Where(IsVisibleInActiveLibrary)
            .Where(x => string.Equals(x.ExportSymbol, macroName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => GetMacroEntityPriority(snapshot, x))
            .ThenBy(x => x.SourceSpan?.StartOffset ?? int.MaxValue)
            .FirstOrDefault();
        if (entity is null)
            return;

        await LoadSelectedInspectorEntityRawTextAsync(
            entity.Id,
            isStillSelected,
            applyRawText,
            propertyName);
    }

    private static int GetMacroEntityPriority(
        WccLibrarySnapshot snapshot,
        WccEntityRecord entity)
    {
        if (!snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile) ||
            !snapshot.RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package))
        {
            return 5;
        }

        if (package.SourceKind == WccPackageSourceKinds.WesnothMainline)
            return package.ContentKind == WccPackageContentKinds.Campaign ? 3 : 0;
        if (package.SourceKind == WccPackageSourceKinds.WccProject)
            return 1;
        if (package.SourceKind == WccPackageSourceKinds.WesnothAddon)
            return 2;
        return 4;
    }

    private CancellationToken ReplaceCatalogInspectorDetailCancellation()
    {
        _catalogInspectorDetailLoadCancellation?.Cancel();
        _catalogInspectorDetailLoadCancellation?.Dispose();
        _catalogInspectorDetailLoadCancellation = new CancellationTokenSource();
        return _catalogInspectorDetailLoadCancellation.Token;
    }

    private static IReadOnlyList<string> ParseSummaryList(
        IReadOnlyDictionary<string, string> summary,
        string key)
        => summary.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();


    // ---------------------------------------------------------------------
    // Macro catalog rebuild and inspector refresh
    // ---------------------------------------------------------------------
    private void RefreshCatalogInspectorMacroEntries()
    {
        CatalogInspectorMacroEntries.Clear();

        var query = CatalogInspectorMacroSearchText?.Trim();

        foreach (var entry in _allMacroCatalogEntryVms)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.PurposeLabel.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.ScopeSummary.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            CatalogInspectorMacroEntries.Add(entry);
        }
    }

    private void RefreshSelectedMacroDetails()
    {
        SelectedMacroScopes.Clear();
        SelectedMacroDefinitions.Clear();
        SelectedMacroInvocations.Clear();
        SelectedMacroTemplatedPathUsages.Clear();

        if (SelectedMacroCatalogEntry is null)
            return;

        foreach (var scopeEntry in SelectedMacroCatalogEntry.Entry.ScopedEntries
                     .OrderBy(x => x.ScopeLabel, StringComparer.OrdinalIgnoreCase))
        {
            SelectedMacroScopes.Add(new MacroCatalogScopeSummaryVm(scopeEntry));
        }

        foreach (var def in SelectedMacroCatalogEntry.Entry.ScopedEntries
                     .SelectMany(x => x.DefinitionOccurrences)
                     .OrderBy(x => x.SourceFileName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.StartLineIndex))
        {
            SelectedMacroDefinitions.Add(new MacroDefinitionOccurrenceVm(def));
        }

        foreach (var inv in SelectedMacroCatalogEntry.Entry.ScopedEntries
                     .SelectMany(x => x.InvocationUsages)
                     .OrderBy(x => x.SourceFileName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.LineIndex))
        {
            SelectedMacroInvocations.Add(new MacroInvocationUsageVm(inv));
        }

        foreach (var usage in SelectedMacroCatalogEntry.Entry.ScopedEntries
                     .SelectMany(x => x.TemplatedPathUsages)
                     .OrderBy(x => x.AssetKind.ToString(), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.OwnerBlockKind, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.SourceFieldKey, StringComparer.OrdinalIgnoreCase))
        {
            SelectedMacroTemplatedPathUsages.Add(new MacroTemplatedPathUsageVm(usage));
        }
    }

    public ObservableCollection<MovementTypeCatalogEntryVm> CatalogInspectorMovementTypeEntries { get; } = new();

    private string? _catalogInspectorMovementTypeSearchText;

    public string? CatalogInspectorMovementTypeSearchText
    {
        get => _catalogInspectorMovementTypeSearchText;
        set
        {
            if (!SetField(ref _catalogInspectorMovementTypeSearchText, value))
                return;

            RefreshCatalogInspectorMovementTypeEntries();
        }
    }

    private MovementTypeCatalogEntryVm? _selectedMovementTypeCatalogEntry;

    public MovementTypeCatalogEntryVm? SelectedMovementTypeCatalogEntry
    {
        get => _selectedMovementTypeCatalogEntry;
        set
        {
            if (!SetField(ref _selectedMovementTypeCatalogEntry, value))
                return;

            OnPropertyChanged(nameof(SelectedMovementTypeName));
            OnPropertyChanged(nameof(SelectedMovementTypeOriginLabel));
            OnPropertyChanged(nameof(SelectedMovementTypeSourceFileName));
            OnPropertyChanged(nameof(SelectedMovementTypeSourcePath));
            OnPropertyChanged(nameof(SelectedMovementTypeRawText));
            _ = LoadSelectedInspectorEntityRawTextAsync(
                value?.LibraryEntityId,
                () => ReferenceEquals(SelectedMovementTypeCatalogEntry, value),
                rawText => value?.SetRawText(rawText),
                nameof(SelectedMovementTypeRawText));
        }
    }

    public string? SelectedMovementTypeName => SelectedMovementTypeCatalogEntry?.Name;
    public string? SelectedMovementTypeOriginLabel => SelectedMovementTypeCatalogEntry?.OriginLabel;
    public string? SelectedMovementTypeSourceFileName => SelectedMovementTypeCatalogEntry?.SourceFileName;
    public string? SelectedMovementTypeSourcePath => SelectedMovementTypeCatalogEntry?.SourcePath;
    public string? SelectedMovementTypeRawText => SelectedMovementTypeCatalogEntry?.RawText;

    public ObservableCollection<TraitCatalogEntryVm> CatalogInspectorTraitEntries { get; } = new();

    private string? _catalogInspectorTraitSearchText;

    public string? CatalogInspectorTraitSearchText
    {
        get => _catalogInspectorTraitSearchText;
        set
        {
            if (!SetField(ref _catalogInspectorTraitSearchText, value))
                return;

            RefreshCatalogInspectorTraitEntries();
        }
    }

    private TraitCatalogEntryVm? _selectedTraitCatalogEntry;

    public TraitCatalogEntryVm? SelectedTraitCatalogEntry
    {
        get => _selectedTraitCatalogEntry;
        set
        {
            if (!SetField(ref _selectedTraitCatalogEntry, value))
                return;

            OnPropertyChanged(nameof(SelectedTraitId));
            OnPropertyChanged(nameof(SelectedTraitDisplayName));
            OnPropertyChanged(nameof(SelectedTraitOriginLabel));
            OnPropertyChanged(nameof(SelectedTraitSourceFileName));
            OnPropertyChanged(nameof(SelectedTraitSourcePath));
            OnPropertyChanged(nameof(SelectedTraitRawText));
            _ = LoadSelectedInspectorEntityRawTextAsync(
                value?.LibraryEntityId,
                () => ReferenceEquals(SelectedTraitCatalogEntry, value),
                rawText => value?.SetRawText(rawText),
                nameof(SelectedTraitRawText));
        }
    }

    public string? SelectedTraitId => SelectedTraitCatalogEntry?.Id;
    public string? SelectedTraitDisplayName => SelectedTraitCatalogEntry?.DisplayName;
    public string? SelectedTraitOriginLabel => SelectedTraitCatalogEntry?.OriginLabel;
    public string? SelectedTraitSourceFileName => SelectedTraitCatalogEntry?.SourceFileName;
    public string? SelectedTraitSourcePath => SelectedTraitCatalogEntry?.SourcePath;
    public string? SelectedTraitRawText => SelectedTraitCatalogEntry?.RawText;

    // ---------------------------------------------------------------------
    // Direct preset catalogs and inspectors
    //
    // Important distinction:
    // - Direct WML catalogs are imported directly from files (movement types,
    //   traits, races, terrain types, items).
    // - Macro-derived preset catalogs are rebuilt from the package snapshot's
    //   lightweight macro projection.
    //   (schedules, abilities, weapon specials).
    // ---------------------------------------------------------------------
    private void MarkDirectPresetCatalogsDirty()
    {
        _areDirectPresetCatalogsDirty = true;
        _areDirectPresetCatalogsBuilt = false;
    }

    public void EnsureDirectPresetCatalogsReady()
    {
        if (_areDirectPresetCatalogsBuilt && !_areDirectPresetCatalogsDirty)
            return;

        ClearDirectPresetCatalogs();

        _projectedCatalogEntityIds.Clear();
        if (LibrarySnapshot is { } snapshot)
            BuildDirectPresetCatalogsFromSnapshot(snapshot);

        RebuildMacroDerivedPresetCatalogs();
        RestoreLibraryPickerProjection();

        _areDirectPresetCatalogsDirty = false;
        _areDirectPresetCatalogsBuilt = true;
    }

    private void ClearDirectPresetCatalogs()
    {
        MovementTypeCatalog.Clear();
        TraitCatalog.Clear();
        SchedulePresetCatalog.Clear();
        AbilityPresetCatalog.Clear();
        WeaponSpecialPresetCatalog.Clear();
        RaceCatalog.Clear();
        TerrainTypeCatalog.Clear();
        ItemCatalog.Clear();

        // Reserved for later, but intentionally not active in v0.1:
        // ResistanceDefaultsCatalog.Clear();
        // TerrainDefaultsCatalog.Clear();
    }

    private void BuildDirectPresetCatalogsFromSnapshot(WccLibrarySnapshot snapshot)
    {
        foreach (var item in QueryLibraryItems(WccEntityTypes.MovementType))
        {
            var entry = new MovementTypeCatalogEntry
            {
                InternalId = ProjectedCatalogInternalId(item.EntityId),
                Name = item.ExportSymbol ?? item.DisplayName,
                RawText = string.Empty,
                SourceFileName = GetSourceFileName(item),
                SourcePath = GetSourcePath(item),
                Origin = GetProjectedOrigin(item)
            };
            MovementTypeCatalog.Entries.Add(entry);
            RegisterProjectedCatalogEntry(entry.InternalId, item.EntityId);
        }

        foreach (var item in QueryLibraryItems(WccEntityTypes.Trait))
        {
            var entry = new TraitCatalogEntry
            {
                InternalId = ProjectedCatalogInternalId(item.EntityId),
                Id = item.ExportSymbol ?? item.DisplayName,
                DisplayName = GetSummary(item, "display_name"),
                RawText = string.Empty,
                SourceFileName = GetSourceFileName(item),
                SourcePath = GetSourcePath(item),
                Origin = GetProjectedOrigin(item)
            };
            TraitCatalog.Entries.Add(entry);
            RegisterProjectedCatalogEntry(entry.InternalId, item.EntityId);
        }

        foreach (var item in QueryLibraryItems(WccEntityTypes.Race))
        {
            var entry = new RaceCatalogEntry
            {
                InternalId = ProjectedCatalogInternalId(item.EntityId),
                Id = item.ExportSymbol ?? item.DisplayName,
                Name = GetSummary(item, "name"),
                MaleName = GetSummary(item, "male_name"),
                FemaleName = GetSummary(item, "female_name"),
                PluralName = GetSummary(item, "plural_name"),
                Description = GetSummary(item, "description"),
                NumTraits = TryParseSummaryInt(item, "num_traits"),
                IgnoreGlobalTraits = GetSummary(item, "ignore_global_traits"),
                RawText = string.Empty,
                SourceFileName = GetSourceFileName(item),
                SourcePath = GetSourcePath(item),
                Origin = GetProjectedOrigin(item)
            };
            RaceCatalog.Entries.Add(entry);
            RegisterProjectedCatalogEntry(entry.InternalId, item.EntityId);
        }

        foreach (var item in QueryLibraryItems(WccEntityTypes.TerrainType))
        {
            var entry = new TerrainTypeCatalogEntry
            {
                InternalId = ProjectedCatalogInternalId(item.EntityId),
                Id = item.ExportSymbol ?? item.DisplayName,
                Name = GetSummary(item, "name"),
                EditorName = GetSummary(item, "editor_name"),
                StringCode = GetSummary(item, "string"),
                EditorGroup = GetSummary(item, "editor_group"),
                SymbolImage = GetSummary(item, "symbol_image"),
                EditorImage = GetSummary(item, "editor_image"),
                Description = GetSummary(item, "description"),
                RawText = string.Empty,
                SourceFileName = GetSourceFileName(item),
                SourcePath = GetSourcePath(item),
                Origin = GetProjectedOrigin(item)
            };
            TerrainTypeCatalog.Entries.Add(entry);
            RegisterProjectedCatalogEntry(entry.InternalId, item.EntityId);
        }

        foreach (var item in QueryLibraryItems(WccEntityTypes.Item))
        {
            var entry = new ItemCatalogEntry
            {
                InternalId = ProjectedCatalogInternalId(item.EntityId),
                Name = GetSummary(item, "name"),
                X = GetSummary(item, "x"),
                Y = GetSummary(item, "y"),
                Image = GetSummary(item, "image"),
                Halo = GetSummary(item, "halo"),
                TeamName = GetSummary(item, "team_name"),
                VisibleInFog = GetSummary(item, "visible_in_fog"),
                Submerge = GetSummary(item, "submerge"),
                ZOrder = GetSummary(item, "z_order"),
                RawText = string.Empty,
                SourceFileName = GetSourceFileName(item),
                SourcePath = GetSourcePath(item),
                Origin = GetProjectedOrigin(item)
            };
            ItemCatalog.Entries.Add(entry);
            RegisterProjectedCatalogEntry(entry.InternalId, item.EntityId);
        }
    }

    private static string ProjectedCatalogInternalId(WccEntityId entityId)
        => "library-" + entityId;

    private void RegisterProjectedCatalogEntry(string internalId, WccEntityId entityId)
        => _projectedCatalogEntityIds[internalId] = entityId;

    private static int? TryParseSummaryInt(WccCatalogItem item, string key)
        => int.TryParse(
            GetSummary(item, key),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private void RebuildMacroDerivedPresetCatalogs()
    {
        var registry = LibrarySnapshot is { } snapshot
            ? WccLibraryMacroCatalogProjection.BuildRegistry(snapshot, _activeLibraryRevisionIds)
            : new WccMacroRegistry();

        _allMacroCatalogEntryVms.Clear();
        _allMacroCatalogEntryVms.AddRange(
            WccMacroCatalogBuilder.Build(registry)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new MacroCatalogEntryVm(x)));

        SchedulePresetCatalogBuilder.RebuildFromMacroRegistry(SchedulePresetCatalog, registry);
        AbilityPresetCatalogBuilder.RebuildFromMacroRegistry(AbilityPresetCatalog, registry);
        WeaponSpecialPresetCatalogBuilder.RebuildFromMacroRegistry(WeaponSpecialPresetCatalog, registry);

        RegisterProjectedMacroEntries(AbilityPresetCatalog.Entries);
        RegisterProjectedMacroEntries(WeaponSpecialPresetCatalog.Entries);
    }

    private void RegisterProjectedMacroEntries(IEnumerable<AbilityPresetCatalogEntry> entries)
    {
        foreach (var entry in entries)
            RegisterProjectedMacroEntry(entry.InternalId, entry.Name, entry.SourcePath);
    }

    private void RegisterProjectedMacroEntries(IEnumerable<WeaponSpecialPresetCatalogEntry> entries)
    {
        foreach (var entry in entries)
            RegisterProjectedMacroEntry(entry.InternalId, entry.Name, entry.SourcePath);
    }

    private void RegisterProjectedMacroEntry(
        string internalId,
        string macroName,
        string? sourcePath)
    {
        if (LibrarySnapshot is not { } snapshot)
            return;

        var match = snapshot.FindEntities(WccEntityTypes.MacroDefinition)
            .FirstOrDefault(x =>
                IsVisibleInActiveLibrary(x) &&
                string.Equals(x.ExportSymbol, macroName, StringComparison.Ordinal) &&
                snapshot.SourceFilesById.TryGetValue(x.SourceFileId, out var sourceFile) &&
                !string.IsNullOrWhiteSpace(sourcePath) &&
                PathsEqual(sourceFile.StoragePath, sourcePath));
        if (match is not null)
            RegisterProjectedCatalogEntry(internalId, match.Id);
    }

    public async Task<string?> LoadProjectedCatalogRawTextAsync(
        string internalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(internalId) ||
            !_projectedCatalogEntityIds.TryGetValue(internalId, out var entityId) ||
            LibrarySnapshot is not { } snapshot)
        {
            return null;
        }

        var content = await WccLibraryEntityContentLoader
            .LoadAsync(snapshot, entityId, cancellationToken)
            .ConfigureAwait(false);
        if (!content.MatchesIndexedContentHash)
        {
            ReportStaleEntitySource();
            return null;
        }
        return content.EntityRawText;
    }

    public async Task<string?> LoadLibraryEntityRawTextAsync(
        WccEntityId entityId,
        CancellationToken cancellationToken = default)
    {
        if (LibrarySnapshot is not { } snapshot ||
            !snapshot.EntitiesById.ContainsKey(entityId))
        {
            return null;
        }

        var content = await WccLibraryEntityContentLoader
            .LoadAsync(snapshot, entityId, cancellationToken)
            .ConfigureAwait(false);
        if (!content.MatchesIndexedContentHash)
        {
            ReportStaleEntitySource();
            return null;
        }
        return content.EntityRawText;
    }

    private void ReportStaleEntitySource()
        => Dispatcher.UIThread.Post(() =>
        {
            Status = "This source changed while its package index was refreshing. Reopen the entry after the refresh completes; stale source spans were not loaded.";
            _ = RefreshConfiguredLibraryIndexAsync();
        });

    private void RefreshCatalogInspectorMovementTypeEntries()
    {
        CatalogInspectorMovementTypeEntries.Clear();

        var query = CatalogInspectorMovementTypeSearchText?.Trim();

        foreach (var entry in _allMovementTypeEntryVms)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.SourceFileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            CatalogInspectorMovementTypeEntries.Add(entry);
        }
    }

    private void RefreshCatalogInspectorTraitEntries()
    {
        CatalogInspectorTraitEntries.Clear();

        var query = CatalogInspectorTraitSearchText?.Trim();

        foreach (var entry in _allTraitEntryVms)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.DisplayNameText.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.SourceFileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            CatalogInspectorTraitEntries.Add(entry);
        }
    }

    // ---------------------------------------------------------------------
    // Remaining preset inspector state (macro-derived + direct categories)
    // ---------------------------------------------------------------------
    public ObservableCollection<SchedulePresetCatalogEntryVm> CatalogInspectorScheduleEntries { get; } = new();

private string? _catalogInspectorScheduleSearchText;
public string? CatalogInspectorScheduleSearchText
{
    get => _catalogInspectorScheduleSearchText;
    set
    {
        if (!SetField(ref _catalogInspectorScheduleSearchText, value))
            return;

        RefreshCatalogInspectorScheduleEntries();
    }
}

private SchedulePresetCatalogEntryVm? _selectedScheduleCatalogEntry;
public SchedulePresetCatalogEntryVm? SelectedScheduleCatalogEntry
{
    get => _selectedScheduleCatalogEntry;
    set
    {
        if (!SetField(ref _selectedScheduleCatalogEntry, value))
            return;

        OnPropertyChanged(nameof(SelectedScheduleName));
        OnPropertyChanged(nameof(SelectedScheduleScopeSummary));
        OnPropertyChanged(nameof(SelectedSchedulePreferredDefinitionScopeLabel));
        OnPropertyChanged(nameof(SelectedScheduleTimeSlotCountText));
        OnPropertyChanged(nameof(SelectedScheduleLoopKindLabel));
        OnPropertyChanged(nameof(SelectedScheduleConfidenceLabel));
        OnPropertyChanged(nameof(SelectedScheduleEvidenceSummary));
        OnPropertyChanged(nameof(SelectedScheduleRawText));
        _ = LoadSelectedMacroRawTextAsync(
            value?.IsSnapshotBacked == true && string.IsNullOrWhiteSpace(value.RawText)
                ? value.Name
                : null,
            () => ReferenceEquals(SelectedScheduleCatalogEntry, value),
            rawText => value?.SetRawText(rawText),
            nameof(SelectedScheduleRawText));
    }
}

public string? SelectedScheduleName => SelectedScheduleCatalogEntry?.Name;
public string? SelectedScheduleScopeSummary => SelectedScheduleCatalogEntry?.ScopeSummary;
public string? SelectedSchedulePreferredDefinitionScopeLabel => SelectedScheduleCatalogEntry?.PreferredDefinitionScopeLabel;
public string SelectedScheduleTimeSlotCountText => SelectedScheduleCatalogEntry?.TimeSlotCount.ToString() ?? "0";
public string? SelectedScheduleLoopKindLabel => SelectedScheduleCatalogEntry?.LoopKindLabel;
public string? SelectedScheduleConfidenceLabel => SelectedScheduleCatalogEntry?.ConfidenceLabel;
public string? SelectedScheduleEvidenceSummary => SelectedScheduleCatalogEntry?.Entry.PurposeEvidenceSummary;
public string? SelectedScheduleRawText => SelectedScheduleCatalogEntry?.RawText;

private void RefreshCatalogInspectorScheduleEntries()
{
    CatalogInspectorScheduleEntries.Clear();

    var query = CatalogInspectorScheduleSearchText?.Trim();

    foreach (var entry in _allScheduleEntryVms)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.ScopeSummary.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        CatalogInspectorScheduleEntries.Add(entry);
    }
}
public ObservableCollection<AbilityPresetCatalogEntryVm> CatalogInspectorAbilityEntries { get; } = new();

private string? _catalogInspectorAbilitySearchText;
public string? CatalogInspectorAbilitySearchText
{
    get => _catalogInspectorAbilitySearchText;
    set
    {
        if (!SetField(ref _catalogInspectorAbilitySearchText, value))
            return;

        RefreshCatalogInspectorAbilityEntries();
    }
}

private AbilityPresetCatalogEntryVm? _selectedAbilityCatalogEntry;
public AbilityPresetCatalogEntryVm? SelectedAbilityCatalogEntry
{
    get => _selectedAbilityCatalogEntry;
    set
    {
        if (!SetField(ref _selectedAbilityCatalogEntry, value))
            return;

        OnPropertyChanged(nameof(SelectedAbilityName));
        OnPropertyChanged(nameof(SelectedAbilityScopeSummary));
        OnPropertyChanged(nameof(SelectedAbilityPreferredDefinitionScopeLabel));
        OnPropertyChanged(nameof(SelectedAbilityConfidenceLabel));
        OnPropertyChanged(nameof(SelectedAbilityEvidenceSummary));
        OnPropertyChanged(nameof(SelectedAbilityRawText));
        _ = LoadSelectedMacroRawTextAsync(
            value?.IsSnapshotBacked == true && string.IsNullOrWhiteSpace(value.RawText)
                ? value.Name
                : null,
            () => ReferenceEquals(SelectedAbilityCatalogEntry, value),
            rawText => value?.SetRawText(rawText),
            nameof(SelectedAbilityRawText));
    }
}

public string? SelectedAbilityName => SelectedAbilityCatalogEntry?.Name;
public string? SelectedAbilityScopeSummary => SelectedAbilityCatalogEntry?.ScopeSummary;
public string? SelectedAbilityPreferredDefinitionScopeLabel => SelectedAbilityCatalogEntry?.PreferredDefinitionScopeLabel;
public string? SelectedAbilityConfidenceLabel => SelectedAbilityCatalogEntry?.ConfidenceLabel;
public string? SelectedAbilityEvidenceSummary => SelectedAbilityCatalogEntry?.Entry.PurposeEvidenceSummary;
public string? SelectedAbilityRawText => SelectedAbilityCatalogEntry?.RawText;

public ObservableCollection<WeaponSpecialPresetCatalogEntryVm> CatalogInspectorWeaponSpecialEntries { get; } = new();

private string? _catalogInspectorWeaponSpecialSearchText;
public string? CatalogInspectorWeaponSpecialSearchText
{
    get => _catalogInspectorWeaponSpecialSearchText;
    set
    {
        if (!SetField(ref _catalogInspectorWeaponSpecialSearchText, value))
            return;

        RefreshCatalogInspectorWeaponSpecialEntries();
    }
}

private WeaponSpecialPresetCatalogEntryVm? _selectedWeaponSpecialCatalogEntry;
public WeaponSpecialPresetCatalogEntryVm? SelectedWeaponSpecialCatalogEntry
{
    get => _selectedWeaponSpecialCatalogEntry;
    set
    {
        if (!SetField(ref _selectedWeaponSpecialCatalogEntry, value))
            return;

        OnPropertyChanged(nameof(SelectedWeaponSpecialName));
        OnPropertyChanged(nameof(SelectedWeaponSpecialScopeSummary));
        OnPropertyChanged(nameof(SelectedWeaponSpecialPreferredDefinitionScopeLabel));
        OnPropertyChanged(nameof(SelectedWeaponSpecialConfidenceLabel));
        OnPropertyChanged(nameof(SelectedWeaponSpecialEvidenceSummary));
        OnPropertyChanged(nameof(SelectedWeaponSpecialRawText));
        _ = LoadSelectedMacroRawTextAsync(
            value?.IsSnapshotBacked == true && string.IsNullOrWhiteSpace(value.RawText)
                ? value.Name
                : null,
            () => ReferenceEquals(SelectedWeaponSpecialCatalogEntry, value),
            rawText => value?.SetRawText(rawText),
            nameof(SelectedWeaponSpecialRawText));
    }
}

public string? SelectedWeaponSpecialName => SelectedWeaponSpecialCatalogEntry?.Name;
public string? SelectedWeaponSpecialScopeSummary => SelectedWeaponSpecialCatalogEntry?.ScopeSummary;
public string? SelectedWeaponSpecialPreferredDefinitionScopeLabel => SelectedWeaponSpecialCatalogEntry?.PreferredDefinitionScopeLabel;
public string? SelectedWeaponSpecialConfidenceLabel => SelectedWeaponSpecialCatalogEntry?.ConfidenceLabel;
public string? SelectedWeaponSpecialEvidenceSummary => SelectedWeaponSpecialCatalogEntry?.Entry.PurposeEvidenceSummary;
public string? SelectedWeaponSpecialRawText => SelectedWeaponSpecialCatalogEntry?.RawText;

private void RefreshCatalogInspectorAbilityEntries()
{
    CatalogInspectorAbilityEntries.Clear();

    var query = CatalogInspectorAbilitySearchText?.Trim();

    foreach (var entry in _allAbilityEntryVms)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.ScopeSummary.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        CatalogInspectorAbilityEntries.Add(entry);
    }
}

private void RefreshCatalogInspectorWeaponSpecialEntries()
{
    CatalogInspectorWeaponSpecialEntries.Clear();

    var query = CatalogInspectorWeaponSpecialSearchText?.Trim();

    foreach (var entry in _allWeaponSpecialEntryVms)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.ScopeSummary.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        CatalogInspectorWeaponSpecialEntries.Add(entry);
    }
}

public ObservableCollection<RaceCatalogEntryVm> CatalogInspectorRaceEntries { get; } = new();

private string? _catalogInspectorRaceSearchText;
public string? CatalogInspectorRaceSearchText
{
    get => _catalogInspectorRaceSearchText;
    set
    {
        if (!SetField(ref _catalogInspectorRaceSearchText, value))
            return;

        RefreshCatalogInspectorRaceEntries();
    }
}

private RaceCatalogEntryVm? _selectedRaceCatalogEntry;
public RaceCatalogEntryVm? SelectedRaceCatalogEntry
{
    get => _selectedRaceCatalogEntry;
    set
    {
        if (!SetField(ref _selectedRaceCatalogEntry, value))
            return;

        OnPropertyChanged(nameof(SelectedRaceId));
        OnPropertyChanged(nameof(SelectedRaceName));
        OnPropertyChanged(nameof(SelectedRaceMaleName));
        OnPropertyChanged(nameof(SelectedRaceFemaleName));
        OnPropertyChanged(nameof(SelectedRacePluralName));
        OnPropertyChanged(nameof(SelectedRaceDescription));
        OnPropertyChanged(nameof(SelectedRaceNumTraitsText));
        OnPropertyChanged(nameof(SelectedRaceIgnoreGlobalTraits));
        OnPropertyChanged(nameof(SelectedRaceOriginLabel));
        OnPropertyChanged(nameof(SelectedRaceSourceFileName));
        OnPropertyChanged(nameof(SelectedRaceSourcePath));
        OnPropertyChanged(nameof(SelectedRaceRawText));
        _ = LoadSelectedInspectorEntityRawTextAsync(
            value?.LibraryEntityId,
            () => ReferenceEquals(SelectedRaceCatalogEntry, value),
            rawText => value?.SetRawText(rawText),
            nameof(SelectedRaceRawText));
    }
}

public string? SelectedRaceId => SelectedRaceCatalogEntry?.Id;
public string? SelectedRaceName => SelectedRaceCatalogEntry?.Name;
public string? SelectedRaceMaleName => SelectedRaceCatalogEntry?.MaleName;
public string? SelectedRaceFemaleName => SelectedRaceCatalogEntry?.FemaleName;
public string? SelectedRacePluralName => SelectedRaceCatalogEntry?.PluralName;
public string? SelectedRaceDescription => SelectedRaceCatalogEntry?.Description;
public string SelectedRaceNumTraitsText => SelectedRaceCatalogEntry?.NumTraits?.ToString() ?? "";
public string? SelectedRaceIgnoreGlobalTraits => SelectedRaceCatalogEntry?.IgnoreGlobalTraits;
public string? SelectedRaceOriginLabel => SelectedRaceCatalogEntry?.OriginLabel;
public string? SelectedRaceSourceFileName => SelectedRaceCatalogEntry?.SourceFileName;
public string? SelectedRaceSourcePath => SelectedRaceCatalogEntry?.SourcePath;
public string? SelectedRaceRawText => SelectedRaceCatalogEntry?.RawText;

private void RefreshCatalogInspectorRaceEntries()
{
    CatalogInspectorRaceEntries.Clear();

    var query = CatalogInspectorRaceSearchText?.Trim();

    foreach (var entry in _allRaceEntryVms)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.DisplayNameText.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !(entry.PluralName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(entry.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !entry.SourceFileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        CatalogInspectorRaceEntries.Add(entry);
    }
}
public ObservableCollection<ResistanceDefaultsCatalogEntryVm> CatalogInspectorResistanceDefaultsEntries { get; } = new();

private string? _catalogInspectorResistanceDefaultsSearchText;
public string? CatalogInspectorResistanceDefaultsSearchText
{
    get => _catalogInspectorResistanceDefaultsSearchText;
    set
    {
        if (!SetField(ref _catalogInspectorResistanceDefaultsSearchText, value))
            return;

        RefreshCatalogInspectorResistanceDefaultsEntries();
    }
}

private ResistanceDefaultsCatalogEntryVm? _selectedResistanceDefaultsCatalogEntry;
public ResistanceDefaultsCatalogEntryVm? SelectedResistanceDefaultsCatalogEntry
{
    get => _selectedResistanceDefaultsCatalogEntry;
    set
    {
        if (!SetField(ref _selectedResistanceDefaultsCatalogEntry, value))
            return;

        OnPropertyChanged(nameof(SelectedResistanceDefaultsDisplayName));
        OnPropertyChanged(nameof(SelectedResistanceDefaultsChildTagSummary));
        OnPropertyChanged(nameof(SelectedResistanceDefaultsOriginLabel));
        OnPropertyChanged(nameof(SelectedResistanceDefaultsSourceFileName));
        OnPropertyChanged(nameof(SelectedResistanceDefaultsSourcePath));
        OnPropertyChanged(nameof(SelectedResistanceDefaultsRawText));
    }
}

public string? SelectedResistanceDefaultsDisplayName => SelectedResistanceDefaultsCatalogEntry?.DisplayName;
public string? SelectedResistanceDefaultsChildTagSummary => SelectedResistanceDefaultsCatalogEntry?.ChildTagSummary;
public string? SelectedResistanceDefaultsOriginLabel => SelectedResistanceDefaultsCatalogEntry?.OriginLabel;
public string? SelectedResistanceDefaultsSourceFileName => SelectedResistanceDefaultsCatalogEntry?.SourceFileName;
public string? SelectedResistanceDefaultsSourcePath => SelectedResistanceDefaultsCatalogEntry?.SourcePath;
public string? SelectedResistanceDefaultsRawText => SelectedResistanceDefaultsCatalogEntry?.Entry.RawText;

public ObservableCollection<TerrainDefaultsCatalogEntryVm> CatalogInspectorTerrainDefaultsEntries { get; } = new();

private string? _catalogInspectorTerrainDefaultsSearchText;
public string? CatalogInspectorTerrainDefaultsSearchText
{
    get => _catalogInspectorTerrainDefaultsSearchText;
    set
    {
        if (!SetField(ref _catalogInspectorTerrainDefaultsSearchText, value))
            return;

        RefreshCatalogInspectorTerrainDefaultsEntries();
    }
}

private TerrainDefaultsCatalogEntryVm? _selectedTerrainDefaultsCatalogEntry;
public TerrainDefaultsCatalogEntryVm? SelectedTerrainDefaultsCatalogEntry
{
    get => _selectedTerrainDefaultsCatalogEntry;
    set
    {
        if (!SetField(ref _selectedTerrainDefaultsCatalogEntry, value))
            return;

        OnPropertyChanged(nameof(SelectedTerrainDefaultsDisplayName));
        OnPropertyChanged(nameof(SelectedTerrainDefaultsChildTagSummary));
        OnPropertyChanged(nameof(SelectedTerrainDefaultsOriginLabel));
        OnPropertyChanged(nameof(SelectedTerrainDefaultsSourceFileName));
        OnPropertyChanged(nameof(SelectedTerrainDefaultsSourcePath));
        OnPropertyChanged(nameof(SelectedTerrainDefaultsRawText));
    }
}

public string? SelectedTerrainDefaultsDisplayName => SelectedTerrainDefaultsCatalogEntry?.DisplayName;
public string? SelectedTerrainDefaultsChildTagSummary => SelectedTerrainDefaultsCatalogEntry?.ChildTagSummary;
public string? SelectedTerrainDefaultsOriginLabel => SelectedTerrainDefaultsCatalogEntry?.OriginLabel;
public string? SelectedTerrainDefaultsSourceFileName => SelectedTerrainDefaultsCatalogEntry?.SourceFileName;
public string? SelectedTerrainDefaultsSourcePath => SelectedTerrainDefaultsCatalogEntry?.SourcePath;
public string? SelectedTerrainDefaultsRawText => SelectedTerrainDefaultsCatalogEntry?.Entry.RawText;

private void RefreshCatalogInspectorResistanceDefaultsEntries()
{
    CatalogInspectorResistanceDefaultsEntries.Clear();

    var query = CatalogInspectorResistanceDefaultsSearchText?.Trim();

    foreach (var entry in _allResistanceDefaultsEntryVms)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.ChildTagSummary.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.SourceFileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        CatalogInspectorResistanceDefaultsEntries.Add(entry);
    }
}

private void RefreshCatalogInspectorTerrainDefaultsEntries()
{
    CatalogInspectorTerrainDefaultsEntries.Clear();

    var query = CatalogInspectorTerrainDefaultsSearchText?.Trim();

    foreach (var entry in _allTerrainDefaultsEntryVms)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.ChildTagSummary.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.SourceFileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        CatalogInspectorTerrainDefaultsEntries.Add(entry);
    }
}
public ObservableCollection<TerrainTypeCatalogEntryVm> CatalogInspectorTerrainTypeEntries { get; } = new();

private string? _catalogInspectorTerrainTypeSearchText;
public string? CatalogInspectorTerrainTypeSearchText
{
    get => _catalogInspectorTerrainTypeSearchText;
    set
    {
        if (!SetField(ref _catalogInspectorTerrainTypeSearchText, value))
            return;

        RefreshCatalogInspectorTerrainTypeEntries();
    }
}

private TerrainTypeCatalogEntryVm? _selectedTerrainTypeCatalogEntry;
public TerrainTypeCatalogEntryVm? SelectedTerrainTypeCatalogEntry
{
    get => _selectedTerrainTypeCatalogEntry;
    set
    {
        if (!SetField(ref _selectedTerrainTypeCatalogEntry, value))
            return;

        OnPropertyChanged(nameof(SelectedTerrainTypeId));
        OnPropertyChanged(nameof(SelectedTerrainTypeName));
        OnPropertyChanged(nameof(SelectedTerrainTypeEditorName));
        OnPropertyChanged(nameof(SelectedTerrainTypeStringCode));
        OnPropertyChanged(nameof(SelectedTerrainTypeEditorGroup));
        OnPropertyChanged(nameof(SelectedTerrainTypeSymbolImage));
        OnPropertyChanged(nameof(SelectedTerrainTypeEditorImage));
        OnPropertyChanged(nameof(SelectedTerrainTypeDescription));
        OnPropertyChanged(nameof(SelectedTerrainTypeOriginLabel));
        OnPropertyChanged(nameof(SelectedTerrainTypeSourceFileName));
        OnPropertyChanged(nameof(SelectedTerrainTypeSourcePath));
        OnPropertyChanged(nameof(SelectedTerrainTypeRawText));
        _ = LoadSelectedInspectorEntityRawTextAsync(
            value?.LibraryEntityId,
            () => ReferenceEquals(SelectedTerrainTypeCatalogEntry, value),
            rawText => value?.SetRawText(rawText),
            nameof(SelectedTerrainTypeRawText));
    }
}

public string? SelectedTerrainTypeId => SelectedTerrainTypeCatalogEntry?.Id;
public string? SelectedTerrainTypeName => SelectedTerrainTypeCatalogEntry?.Name;
public string? SelectedTerrainTypeEditorName => SelectedTerrainTypeCatalogEntry?.EditorName;
public string? SelectedTerrainTypeStringCode => SelectedTerrainTypeCatalogEntry?.StringCode;
public string? SelectedTerrainTypeEditorGroup => SelectedTerrainTypeCatalogEntry?.EditorGroup;
public string? SelectedTerrainTypeSymbolImage => SelectedTerrainTypeCatalogEntry?.SymbolImage;
public string? SelectedTerrainTypeEditorImage => SelectedTerrainTypeCatalogEntry?.EditorImage;
public string? SelectedTerrainTypeDescription => SelectedTerrainTypeCatalogEntry?.Description;
public string? SelectedTerrainTypeOriginLabel => SelectedTerrainTypeCatalogEntry?.OriginLabel;
public string? SelectedTerrainTypeSourceFileName => SelectedTerrainTypeCatalogEntry?.SourceFileName;
public string? SelectedTerrainTypeSourcePath => SelectedTerrainTypeCatalogEntry?.SourcePath;
public string? SelectedTerrainTypeRawText => SelectedTerrainTypeCatalogEntry?.RawText;

private void RefreshCatalogInspectorTerrainTypeEntries()
{
    CatalogInspectorTerrainTypeEntries.Clear();

    var query = CatalogInspectorTerrainTypeSearchText?.Trim();

    foreach (var entry in _allTerrainTypeEntryVms)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.DisplayNameText.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !(entry.StringCode?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(entry.EditorGroup?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !entry.SourceFileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        CatalogInspectorTerrainTypeEntries.Add(entry);
    }
}

public ObservableCollection<ItemCatalogEntryVm> CatalogInspectorItemEntries { get; } = new();

private string? _catalogInspectorItemSearchText;
public string? CatalogInspectorItemSearchText
{
    get => _catalogInspectorItemSearchText;
    set
    {
        if (!SetField(ref _catalogInspectorItemSearchText, value))
            return;

        RefreshCatalogInspectorItemEntries();
    }
}

private ItemCatalogEntryVm? _selectedItemCatalogEntry;
public ItemCatalogEntryVm? SelectedItemCatalogEntry
{
    get => _selectedItemCatalogEntry;
    set
    {
        if (!SetField(ref _selectedItemCatalogEntry, value))
            return;

        OnPropertyChanged(nameof(SelectedItemDisplayName));
        OnPropertyChanged(nameof(SelectedItemPositionText));
        OnPropertyChanged(nameof(SelectedItemName));
        OnPropertyChanged(nameof(SelectedItemX));
        OnPropertyChanged(nameof(SelectedItemY));
        OnPropertyChanged(nameof(SelectedItemImage));
        OnPropertyChanged(nameof(SelectedItemHalo));
        OnPropertyChanged(nameof(SelectedItemTeamName));
        OnPropertyChanged(nameof(SelectedItemVisibleInFog));
        OnPropertyChanged(nameof(SelectedItemSubmerge));
        OnPropertyChanged(nameof(SelectedItemZOrder));
        OnPropertyChanged(nameof(SelectedItemOriginLabel));
        OnPropertyChanged(nameof(SelectedItemSourceFileName));
        OnPropertyChanged(nameof(SelectedItemSourcePath));
        OnPropertyChanged(nameof(SelectedItemRawText));
        _ = LoadSelectedInspectorEntityRawTextAsync(
            value?.LibraryEntityId,
            () => ReferenceEquals(SelectedItemCatalogEntry, value),
            rawText => value?.SetRawText(rawText),
            nameof(SelectedItemRawText));
    }
}

public string? SelectedItemDisplayName => SelectedItemCatalogEntry?.DisplayName;
public string? SelectedItemPositionText => SelectedItemCatalogEntry?.PositionText;
public string? SelectedItemName => SelectedItemCatalogEntry?.Name;
public string? SelectedItemX => SelectedItemCatalogEntry?.X;
public string? SelectedItemY => SelectedItemCatalogEntry?.Y;
public string? SelectedItemImage => SelectedItemCatalogEntry?.Image;
public string? SelectedItemHalo => SelectedItemCatalogEntry?.Halo;
public string? SelectedItemTeamName => SelectedItemCatalogEntry?.TeamName;
public string? SelectedItemVisibleInFog => SelectedItemCatalogEntry?.VisibleInFog;
public string? SelectedItemSubmerge => SelectedItemCatalogEntry?.Submerge;
public string? SelectedItemZOrder => SelectedItemCatalogEntry?.ZOrder;
public string? SelectedItemOriginLabel => SelectedItemCatalogEntry?.OriginLabel;
public string? SelectedItemSourceFileName => SelectedItemCatalogEntry?.SourceFileName;
public string? SelectedItemSourcePath => SelectedItemCatalogEntry?.SourcePath;
public string? SelectedItemRawText => SelectedItemCatalogEntry?.RawText;

private void RefreshCatalogInspectorItemEntries()
{
    CatalogInspectorItemEntries.Clear();

    var query = CatalogInspectorItemSearchText?.Trim();

    foreach (var entry in _allItemEntryVms)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.PositionText.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !(entry.Image?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(entry.Halo?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !entry.SourceFileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        CatalogInspectorItemEntries.Add(entry);
    }
}

    // ---------------------------------------------------------------------
    // WCC-managed reusable content mutations
    // ---------------------------------------------------------------------
    public bool CanEditManagedPreset(
        string internalId,
        WccManagedLibraryFileKind kind)
        => TryGetManagedEntityFile(internalId, kind, out _, out _, out _);

    public bool CanEditManagedPreset(
        WccEntityId entityId,
        WccManagedLibraryFileKind kind)
        => TryGetManagedEntityFile(entityId, kind, out _, out _, out _);

    public bool CanEditReusableDefinition(
        WccEntityId entityId,
        WccManagedLibraryFileKind kind)
        => TryGetManagedEntityFile(entityId, kind, out _, out _, out _) ||
           TryGetCurrentProjectEntityFile(entityId, kind, out _, out _);

    public string GetProjectedCatalogPackageLabel(
        string internalId,
        WccSourceOrigin fallbackOrigin)
    {
        if (_projectedCatalogEntityIds.TryGetValue(internalId, out var entityId) &&
            LibrarySnapshot?.FindPackageForEntity(entityId) is { } package &&
            !string.IsNullOrWhiteSpace(package.Name))
        {
            return package.Name;
        }

        return fallbackOrigin switch
        {
            WccSourceOrigin.CoreInstall => "Wesnoth Core",
            WccSourceOrigin.WccGenerated => "WCC Library",
            _ => "Imported package"
        };
    }

    private bool TryGetManagedEntityFile(
        string internalId,
        WccManagedLibraryFileKind kind,
        out WccEntityRecord entity,
        out WccSourceFileRecord sourceFile,
        out WccManagedLibraryFileKind physicalKind)
    {
        entity = null!;
        sourceFile = null!;
        physicalKind = default;

        if (string.IsNullOrWhiteSpace(internalId) ||
            !_projectedCatalogEntityIds.TryGetValue(internalId, out var entityId) ||
            !TryGetManagedEntityFile(entityId, kind, out entity, out sourceFile, out physicalKind))
        {
            return false;
        }

        return true;
    }

    private bool TryGetManagedEntityFile(
        WccEntityId entityId,
        WccManagedLibraryFileKind kind,
        out WccEntityRecord entity,
        out WccSourceFileRecord sourceFile,
        out WccManagedLibraryFileKind physicalKind)
    {
        entity = null!;
        sourceFile = null!;
        physicalKind = default;

        if (LibrarySnapshot is not { } snapshot ||
            !snapshot.EntitiesById.TryGetValue(entityId, out var foundEntity) ||
            !snapshot.SourceFilesById.TryGetValue(foundEntity.SourceFileId, out var foundSourceFile) ||
            !snapshot.RevisionsById.TryGetValue(foundSourceFile.PackageRevisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) ||
            package.SourceKind != WccPackageSourceKinds.WccManagedLibrary ||
            package.StorageMode != WccPackageStorageMode.ManagedSnapshot ||
            !package.IsAvailable || package.CurrentRevisionId != revision.Id)
        {
            return false;
        }

        if (!EntityTypeMatchesManagedKind(foundEntity.EntityType, kind))
            return false;

        // Managed raw editing replaces the represented cfg. Refuse a source containing
        // multiple entities of the same kind so editing one fragment cannot erase siblings.
        if (snapshot.FindEntities(foundEntity.EntityType)
            .Count(x => x.SourceFileId == foundEntity.SourceFileId) != 1)
        {
            return false;
        }

        foreach (var candidateKind in GetCompatibleManagedPhysicalKinds(kind))
        {
            if (!WccManagedLibraryFileStore.IsPathForKind(
                    _unitCatalogLibraryRoot,
                    candidateKind,
                    foundSourceFile.StoragePath))
            {
                continue;
            }

            physicalKind = candidateKind;
            entity = foundEntity;
            sourceFile = foundSourceFile;
            return true;
        }

        return false;
    }

    private bool TryGetCurrentProjectEntityFile(
        WccEntityId entityId,
        WccManagedLibraryFileKind kind,
        out WccEntityRecord entity,
        out WccSourceFileRecord sourceFile)
    {
        entity = null!;
        sourceFile = null!;
        var projectRoot = ProjectSession.RootPath;
        if (!ProjectSession.CanWrite ||
            string.IsNullOrWhiteSpace(projectRoot) ||
            LibrarySnapshot is not { } snapshot ||
            !snapshot.EntitiesById.TryGetValue(entityId, out var foundEntity) ||
            !EntityTypeMatchesManagedKind(foundEntity.EntityType, kind) ||
            !snapshot.SourceFilesById.TryGetValue(foundEntity.SourceFileId, out var foundSourceFile) ||
            !snapshot.RevisionsById.TryGetValue(foundSourceFile.PackageRevisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) ||
            package.SourceKind != WccPackageSourceKinds.WccProject ||
            package.StorageMode != WccPackageStorageMode.WorkingCopy ||
            string.IsNullOrWhiteSpace(foundSourceFile.StoragePath) ||
            !PathsEqual(package.SourceLocation, projectRoot))
        {
            return false;
        }

        entity = foundEntity;
        sourceFile = foundSourceFile;
        return true;
    }

    private static bool EntityTypeMatchesManagedKind(
        WccEntityTypeId entityType,
        WccManagedLibraryFileKind kind)
        => kind switch
        {
            WccManagedLibraryFileKind.Race => entityType == WccEntityTypes.Race,
            WccManagedLibraryFileKind.MovementType => entityType == WccEntityTypes.MovementType,
            WccManagedLibraryFileKind.Trait => entityType == WccEntityTypes.Trait,
            WccManagedLibraryFileKind.TerrainType => entityType == WccEntityTypes.TerrainType,
            WccManagedLibraryFileKind.Item => entityType == WccEntityTypes.Item,
            WccManagedLibraryFileKind.Macro or
            WccManagedLibraryFileKind.Schedule or
            WccManagedLibraryFileKind.Ability or
            WccManagedLibraryFileKind.WeaponSpecial => entityType == WccEntityTypes.MacroDefinition,
            WccManagedLibraryFileKind.AbilityRegistry => entityType == WccEntityTypes.AbilityDefinition,
            WccManagedLibraryFileKind.WeaponSpecialRegistry => entityType == WccEntityTypes.WeaponSpecialDefinition,
            WccManagedLibraryFileKind.UnitType => entityType == WccEntityTypes.UnitType,
            _ => false
        };

    private static IReadOnlyList<WccManagedLibraryFileKind> GetCompatibleManagedPhysicalKinds(
        WccManagedLibraryFileKind requestedKind)
    {
        if (requestedKind is not (
                WccManagedLibraryFileKind.Macro or
                WccManagedLibraryFileKind.Schedule or
                WccManagedLibraryFileKind.Ability or
                WccManagedLibraryFileKind.WeaponSpecial))
        {
            return new[] { requestedKind };
        }

        return new[]
        {
            requestedKind,
            WccManagedLibraryFileKind.Macro,
            WccManagedLibraryFileKind.Schedule,
            WccManagedLibraryFileKind.Ability,
            WccManagedLibraryFileKind.WeaponSpecial
        }.Distinct().ToArray();
    }

    private bool CreateManagedPreset(
        WccManagedLibraryFileKind kind,
        string rawText,
        string itemLabel)
    {
        var result = WccManagedLibraryFileStore.Create(
            _unitCatalogLibraryRoot,
            kind,
            rawText);
        if (!result.IsSuccess)
        {
            Status = FormatManagedLibraryMutationFailure(result, $"create {itemLabel}");
            return false;
        }

        QueueManagedLibraryProjectionRefresh();
        Status = $"Created {itemLabel} in the WCC managed package; refreshing the package index.";
        return true;
    }

    private bool UpdateManagedPreset(
        string internalId,
        WccManagedLibraryFileKind kind,
        string rawText,
        string itemLabel)
    {
        if (!TryGetManagedEntityFile(internalId, kind, out _, out var sourceFile, out var physicalKind))
        {
            Status = $"Could not edit {itemLabel}: it is not an independently managed WCC file.";
            return false;
        }

        var result = WccManagedLibraryFileStore.Update(
            _unitCatalogLibraryRoot,
            physicalKind,
            sourceFile.StoragePath,
            rawText,
            sourceFile.ContentHash);
        if (!result.IsSuccess)
        {
            Status = FormatManagedLibraryMutationFailure(result, $"edit {itemLabel}");
            return false;
        }

        QueueManagedLibraryProjectionRefresh();
        Status = $"Saved {itemLabel}; refreshing the package index.";
        return true;
    }

    private bool UpdateManagedPreset(
        WccEntityId entityId,
        WccManagedLibraryFileKind kind,
        string rawText,
        string itemLabel)
    {
        if (!TryGetManagedEntityFile(entityId, kind, out _, out var sourceFile, out var physicalKind))
        {
            Status = $"Could not edit {itemLabel}: it is not an independently managed WCC file.";
            return false;
        }

        var result = WccManagedLibraryFileStore.Update(
            _unitCatalogLibraryRoot,
            physicalKind,
            sourceFile.StoragePath,
            rawText,
            sourceFile.ContentHash);
        if (!result.IsSuccess)
        {
            Status = FormatManagedLibraryMutationFailure(result, $"edit {itemLabel}");
            return false;
        }

        QueueManagedLibraryProjectionRefresh();
        Status = $"Saved {itemLabel}; refreshing the package index.";
        return true;
    }

    private bool DeleteManagedPreset(
        string internalId,
        WccManagedLibraryFileKind kind,
        string itemLabel)
    {
        if (!TryGetManagedEntityFile(internalId, kind, out _, out var sourceFile, out var physicalKind))
        {
            Status = $"Could not delete {itemLabel}: it is not an independently managed WCC file.";
            return false;
        }

        var result = WccManagedLibraryFileStore.Delete(
            _unitCatalogLibraryRoot,
            physicalKind,
            sourceFile.StoragePath,
            sourceFile.ContentHash);
        if (!result.IsSuccess)
        {
            Status = FormatManagedLibraryMutationFailure(result, $"delete {itemLabel}");
            return false;
        }

        QueueManagedLibraryProjectionRefresh();
        Status = $"Deleted {itemLabel}; refreshing the package index.";
        return true;
    }

    private bool DeleteManagedPreset(
        WccEntityId entityId,
        WccManagedLibraryFileKind kind,
        string itemLabel)
    {
        if (!TryGetManagedEntityFile(entityId, kind, out _, out var sourceFile, out var physicalKind))
        {
            Status = $"Could not delete {itemLabel}: it is not an independently managed WCC file.";
            return false;
        }

        var result = WccManagedLibraryFileStore.Delete(
            _unitCatalogLibraryRoot,
            physicalKind,
            sourceFile.StoragePath,
            sourceFile.ContentHash);
        if (!result.IsSuccess)
        {
            Status = FormatManagedLibraryMutationFailure(result, $"delete {itemLabel}");
            return false;
        }

        QueueManagedLibraryProjectionRefresh();
        Status = $"Deleted {itemLabel}; refreshing the package index.";
        return true;
    }

    private bool UpdateReusableDefinition(
        WccEntityId entityId,
        WccManagedLibraryFileKind kind,
        string replacementRawText,
        string itemLabel)
    {
        if (TryGetManagedEntityFile(entityId, kind, out _, out _, out _))
            return UpdateManagedPreset(entityId, kind, replacementRawText, itemLabel);

        return ReplaceCurrentProjectEntitySource(
            entityId,
            kind,
            replacementRawText,
            delete: false,
            itemLabel);
    }

    private bool DeleteReusableDefinition(
        WccEntityId entityId,
        WccManagedLibraryFileKind kind,
        string itemLabel)
    {
        if (TryGetManagedEntityFile(entityId, kind, out _, out _, out _))
            return DeleteManagedPreset(entityId, kind, itemLabel);

        return ReplaceCurrentProjectEntitySource(
            entityId,
            kind,
            string.Empty,
            delete: true,
            itemLabel);
    }

    private bool ReplaceCurrentProjectEntitySource(
        WccEntityId entityId,
        WccManagedLibraryFileKind kind,
        string replacementRawText,
        bool delete,
        string itemLabel)
    {
        if (!TryGetCurrentProjectEntityFile(entityId, kind, out var entity, out var sourceFile))
        {
            Status = $"Could not {(delete ? "delete" : "edit")} {itemLabel}: it is not an editable WCC or current-project definition.";
            return false;
        }
        if (!SaveCurrentEditorsIfDirty(refreshCatalogEntries: true))
            return false;
        var result = WccWorkingCopyEntitySourceEditor.Replace(
            ProjectSession.RootPath!,
            sourceFile,
            entity,
            replacementRawText);
        if (!result.IsSuccess)
        {
            Status = FormatWorkingCopySaveFailure(result);
            return false;
        }

        QueueManagedLibraryProjectionRefresh(managedLibrary: false);
        Status = delete
            ? $"Deleted {itemLabel} from the current project; refreshing the package index."
            : $"Saved {itemLabel} in the current project; refreshing the package index.";
        return true;
    }

    private void QueueManagedLibraryProjectionRefresh(bool managedLibrary = true)
    {
        MarkDirectPresetCatalogsDirty();
        _ = managedLibrary ? RefreshOwnedLibraryPackageIndexAsync() : RefreshOwnedProjectPackageIndexAsync();
    }

public string CreateUniqueRaceIdSuggestion(string? baseId)
{
    return WccOwnedAssetIdFactory.CreateUniqueId(
        baseId,
        RaceCatalog.Entries.Select(x => x.Id),
        fallbackStem: "custom_race");
}

public void UpsertWccRaceFromRawText(string rawText, string? existingInternalId = null)
{
    var preset = RacePresetImporter.ImportAllFromText(rawText).FirstOrDefault();
    if (preset is null)
    {
        Status = "Could not parse a [race] block from the provided text.";
        return;
    }

    if (string.IsNullOrWhiteSpace(existingInternalId))
        CreateManagedPreset(WccManagedLibraryFileKind.Race, rawText, $"race {preset.Id}");
    else
        UpdateManagedPreset(existingInternalId, WccManagedLibraryFileKind.Race, rawText, $"race {preset.Id}");
}

public void DeleteWccRaceByInternalId(string internalId)
{
    if (string.IsNullOrWhiteSpace(internalId))
        return;

    DeleteManagedPreset(internalId, WccManagedLibraryFileKind.Race, "WCC-owned race");
}
public string CreateUniqueAbilityNameSuggestion(string? baseName)
{
    return WccOwnedAssetIdFactory.CreateUniqueId(
        baseName,
        AbilityPresetCatalog.Entries.Select(x => x.Name),
        fallbackStem: "ABILITY_CUSTOM");
}

public void UpsertWccAbilityFromRawText(string rawText, string? existingInternalId = null)
{
    var def = WmlMacroDefinitionScanner.Scan(rawText).FirstOrDefault();
    if (def is null)
    {
        Status = "Could not parse a #define ... #enddef ability macro from the provided text.";
        return;
    }

    if (string.IsNullOrWhiteSpace(existingInternalId))
        CreateManagedPreset(WccManagedLibraryFileKind.Ability, rawText, $"ability {def.Name}");
    else
        UpdateManagedPreset(existingInternalId, WccManagedLibraryFileKind.Ability, rawText, $"ability {def.Name}");
}

public void DeleteWccAbilityByInternalId(string internalId)
{
    if (string.IsNullOrWhiteSpace(internalId))
        return;

    DeleteManagedPreset(internalId, WccManagedLibraryFileKind.Ability, "WCC-owned ability");
}

public string CreateUniqueAbilityRegistryIdSuggestion(string? baseId)
    => CreateUniqueRegistryIdSuggestion(
        baseId,
        WesnothAbilitySpecialRegistryKind.Ability,
        "wcc_custom_ability");

public void UpsertWccAbilityRegistryFromRawText(
    string rawText,
    WccEntityId? existingEntityId = null)
    => UpsertWccRegistryDefinition(
        WesnothAbilitySpecialRegistryKind.Ability,
        WccManagedLibraryFileKind.AbilityRegistry,
        rawText,
        existingEntityId);

public void DeleteWccAbilityRegistryByEntityId(WccEntityId entityId)
    => DeleteReusableDefinition(
        entityId,
        WccManagedLibraryFileKind.AbilityRegistry,
        "ability registry definition");
public string CreateUniqueMovementTypeNameSuggestion(string? baseName)
{
    return WccOwnedAssetIdFactory.CreateUniqueId(
        baseName,
        MovementTypeCatalog.Entries.Select(x => x.Name),
        fallbackStem: "custom_movement");
}

public void UpsertWccMovementTypeFromRawText(string rawText, string? existingInternalId = null)
{
    var preset = MovementTypePresetImporter.ImportAllFromText(rawText).FirstOrDefault();
    if (preset is null)
    {
        Status = "Could not parse a [movetype] block from the provided text.";
        return;
    }

    if (string.IsNullOrWhiteSpace(existingInternalId))
        CreateManagedPreset(WccManagedLibraryFileKind.MovementType, rawText, $"movement type {preset.Name}");
    else
        UpdateManagedPreset(existingInternalId, WccManagedLibraryFileKind.MovementType, rawText, $"movement type {preset.Name}");
}

public void DeleteWccMovementTypeByInternalId(string internalId)
{
    if (string.IsNullOrWhiteSpace(internalId))
        return;

    DeleteManagedPreset(internalId, WccManagedLibraryFileKind.MovementType, "WCC-owned movement type");
}

public string CreateUniqueWeaponSpecialNameSuggestion(string? baseName)
{
    return WccOwnedAssetIdFactory.CreateUniqueId(
        baseName,
        WeaponSpecialPresetCatalog.Entries.Select(x => x.Name),
        fallbackStem: "WEAPON_SPECIAL_CUSTOM");
}

public void UpsertWccWeaponSpecialFromRawText(string rawText, string? existingInternalId = null)
{
    var def = WmlMacroDefinitionScanner.Scan(rawText).FirstOrDefault();
    if (def is null)
    {
        Status = "Could not parse a #define ... #enddef weapon special macro from the provided text.";
        return;
    }

    if (string.IsNullOrWhiteSpace(existingInternalId))
        CreateManagedPreset(WccManagedLibraryFileKind.WeaponSpecial, rawText, $"weapon special {def.Name}");
    else
        UpdateManagedPreset(existingInternalId, WccManagedLibraryFileKind.WeaponSpecial, rawText, $"weapon special {def.Name}");
}

public void DeleteWccWeaponSpecialByInternalId(string internalId)
{
    if (string.IsNullOrWhiteSpace(internalId))
        return;

    DeleteManagedPreset(internalId, WccManagedLibraryFileKind.WeaponSpecial, "WCC-owned weapon special");
}

public string CreateUniqueWeaponSpecialRegistryIdSuggestion(string? baseId)
    => CreateUniqueRegistryIdSuggestion(
        baseId,
        WesnothAbilitySpecialRegistryKind.WeaponSpecial,
        "wcc_custom_weapon_special");

public void UpsertWccWeaponSpecialRegistryFromRawText(
    string rawText,
    WccEntityId? existingEntityId = null)
    => UpsertWccRegistryDefinition(
        WesnothAbilitySpecialRegistryKind.WeaponSpecial,
        WccManagedLibraryFileKind.WeaponSpecialRegistry,
        rawText,
        existingEntityId);

public void DeleteWccWeaponSpecialRegistryByEntityId(WccEntityId entityId)
    => DeleteReusableDefinition(
        entityId,
        WccManagedLibraryFileKind.WeaponSpecialRegistry,
        "weapon-special registry definition");

private string CreateUniqueRegistryIdSuggestion(
    string? baseId,
    WesnothAbilitySpecialRegistryKind kind,
    string fallbackStem)
{
    var stem = string.IsNullOrWhiteSpace(baseId)
        ? fallbackStem
        : baseId.StartsWith("wcc_", StringComparison.OrdinalIgnoreCase)
            ? baseId
            : "wcc_" + baseId;
    var existingIds = GetActiveAbilitySpecialRegistry()?
        .GetDefinitions(kind)
        .Select(x => x.RegistryId)
        ?? Enumerable.Empty<string>();
    return WccOwnedAssetIdFactory.CreateUniqueId(stem, existingIds, fallbackStem);
}

private void UpsertWccRegistryDefinition(
    WesnothAbilitySpecialRegistryKind registryKind,
    WccManagedLibraryFileKind managedKind,
    string rawText,
    WccEntityId? existingEntityId)
{
    try
    {
        var definition = AbilitySpecialRegistryWmlTemplateHelper.ParseSingleDefinition(
            registryKind,
            rawText);
        var managedSource = AbilitySpecialRegistryWmlTemplateHelper.CreateManagedRegistrySource(
            registryKind,
            rawText);
        var label = registryKind == WesnothAbilitySpecialRegistryKind.Ability
            ? $"ability registry definition {definition.RegistryId}"
            : $"weapon-special registry definition {definition.RegistryId}";
        if (existingEntityId is null)
            CreateManagedPreset(managedKind, managedSource, label);
        else
        {
            var replacement = TryGetManagedEntityFile(
                existingEntityId.Value,
                managedKind,
                out _,
                out _,
                out _)
                ? managedSource
                : AbilitySpecialRegistryWmlTemplateHelper.ExtractSingleDefinitionRawText(
                    registryKind,
                    rawText);
            UpdateReusableDefinition(
                existingEntityId.Value,
                managedKind,
                replacement,
                label);
        }
    }
    catch (InvalidOperationException ex)
    {
        Status = $"Could not save registry definition: {ex.Message}";
    }
}

public string CreateUniqueTraitIdSuggestion(string? baseId)
    => WccOwnedAssetIdFactory.CreateUniqueId(
        baseId,
        TraitCatalog.Entries.Select(x => x.Id),
        fallbackStem: "custom_trait");

public void UpsertWccTraitFromRawText(string rawText, string? existingInternalId = null)
{
    var presets = TraitPresetImporter.ImportAllFromText(rawText);
    if (presets.Count != 1)
    {
        Status = "Provide exactly one [trait] block for a managed trait entry.";
        return;
    }

    if (string.IsNullOrWhiteSpace(existingInternalId))
        CreateManagedPreset(WccManagedLibraryFileKind.Trait, rawText, $"trait {presets[0].Id}");
    else
        UpdateManagedPreset(existingInternalId, WccManagedLibraryFileKind.Trait, rawText, $"trait {presets[0].Id}");
}

public void DeleteWccTraitByInternalId(string internalId)
    => DeleteManagedPreset(internalId, WccManagedLibraryFileKind.Trait, "WCC-owned trait");

public string CreateUniqueTerrainTypeIdSuggestion(string? baseId)
    => WccOwnedAssetIdFactory.CreateUniqueId(
        baseId,
        TerrainTypeCatalog.Entries.Select(x => x.Id),
        fallbackStem: "custom_terrain");

public void UpsertWccTerrainTypeFromRawText(string rawText, string? existingInternalId = null)
{
    var presets = TerrainTypePresetImporter.ImportAllFromText(rawText);
    if (presets.Count != 1)
    {
        Status = "Provide exactly one [terrain_type] block for a managed terrain type entry.";
        return;
    }

    if (string.IsNullOrWhiteSpace(existingInternalId))
        CreateManagedPreset(WccManagedLibraryFileKind.TerrainType, rawText, $"terrain type {presets[0].Id}");
    else
        UpdateManagedPreset(existingInternalId, WccManagedLibraryFileKind.TerrainType, rawText, $"terrain type {presets[0].Id}");
}

public void DeleteWccTerrainTypeByInternalId(string internalId)
    => DeleteManagedPreset(internalId, WccManagedLibraryFileKind.TerrainType, "WCC-owned terrain type");

public string CreateUniqueItemNameSuggestion(string? baseName)
    => WccOwnedAssetIdFactory.CreateUniqueId(
        baseName,
        ItemCatalog.Entries.Select(x => x.Name ?? x.Image ?? x.Halo ?? string.Empty),
        fallbackStem: "custom_item");

public void UpsertWccItemFromRawText(string rawText, string? existingInternalId = null)
{
    var presets = ItemPresetImporter.ImportAllFromText(rawText);
    if (presets.Count != 1)
    {
        Status = "Provide exactly one identifiable [item] block for a managed item entry.";
        return;
    }

    var label = presets[0].Name ?? presets[0].Image ?? presets[0].Halo ?? "item";
    if (string.IsNullOrWhiteSpace(existingInternalId))
        CreateManagedPreset(WccManagedLibraryFileKind.Item, rawText, $"item {label}");
    else
        UpdateManagedPreset(existingInternalId, WccManagedLibraryFileKind.Item, rawText, $"item {label}");
}

public void DeleteWccItemByInternalId(string internalId)
    => DeleteManagedPreset(internalId, WccManagedLibraryFileKind.Item, "WCC-owned item");

public string CreateUniqueMacroNameSuggestion(string? baseName)
    => WccOwnedAssetIdFactory.CreateUniqueId(
        baseName,
        LibrarySnapshot?.FindEntities(WccEntityTypes.MacroDefinition)
            .Where(IsVisibleInActiveLibrary)
            .Select(x => x.ExportSymbol ?? string.Empty)
            ?? Enumerable.Empty<string>(),
        fallbackStem: "WCC_CUSTOM_MACRO");

public WccMacroCatalogEntry? FindMacroCatalogEntry(string macroName)
    => _allMacroCatalogEntryVms
        .Select(x => x.Entry)
        .FirstOrDefault(x => string.Equals(x.Name, macroName, StringComparison.Ordinal));

public void UpsertWccMacroFromRawText(
    string rawText,
    WccEntityId? existingEntityId = null,
    WccManagedLibraryFileKind newKind = WccManagedLibraryFileKind.Macro)
{
    var definitions = WmlMacroDefinitionScanner.Scan(rawText);
    if (definitions.Count != 1)
    {
        Status = "Provide exactly one #define ... #enddef block for a managed macro entry.";
        return;
    }

    var label = $"macro {definitions[0].Name}";
    if (existingEntityId is null)
        CreateManagedPreset(newKind, rawText, label);
    else
        UpdateReusableDefinition(existingEntityId.Value, newKind, rawText, label);
}

public void DeleteWccMacroByEntityId(
    WccEntityId entityId,
    WccManagedLibraryFileKind semanticKind = WccManagedLibraryFileKind.Macro)
    => DeleteReusableDefinition(entityId, semanticKind, "macro definition");

}
