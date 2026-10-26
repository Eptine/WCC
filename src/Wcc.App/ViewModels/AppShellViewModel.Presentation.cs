using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Media;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    public ObservableCollection<ProjectScenarioFileVm> SortedProjectScenarios { get; } = new();
    public ObservableCollection<ProjectScenarioFileVm> SortedLooseScenarios { get; } = new();
    private bool _raisingShellPresentation;
    private string _windowTitle = "WCC";

    public string WindowTitle => _windowTitle;
    private void RefreshCachedCampaignTitle()
    {
        if (CampaignEditor.CurrentProject is null) { _windowTitle = "WCC"; return; }
        var name = CampaignEditor.CaptureCampaignFieldsSnapshot().FirstOrDefault(row => row.Key == "name")?.Value
                   ?? CampaignEditor.CampaignNameWml;
        name = WccScenarioFieldValueCodec.DecodeLiteral(name).Trim();
        if (string.IsNullOrWhiteSpace(name)) name = CampaignEditor.CampaignId ?? "Campaign";
        _windowTitle = "WCC - " + name.Replace('\r', ' ').Replace('\n', ' ');
    }

    // Interaction state is display only; persistence authority remains in the existing coordinators.
    public bool IsShellBusy => IsEditorSourceLoading || IsCampaignEntryNavigationBusy || IsCampaignCreationBusy || IsScenarioCreationBusy || IsUnitTypeCreationBusy || IsGlobalSaveBusy || IsPendingNavigationDecision ||
        ScenarioEditor.IsWccInlineSaveProgressVisible || ScenarioEditor.IsScenarioUiLoading ||
        CampaignEditor.IsCampaignFieldSaveBusy || CampaignEditor.IsCampaignUiLoading || CampaignEditor.IsCampaignSourceLoading ||
        IsLibraryIndexRefreshing;
    public bool CanInteractWithEditors => !IsShellBusy;
    public double EditorContentOpacity => IsShellBusy ? 0.5 : 1;
    public IBrush ShellStatusBrush => IsShellBusy ? Brush.Parse("#D6AB55") :
        HasShellStatusError ? Brush.Parse("#D57166") : Brush.Parse("#76A96D");
    private bool HasShellStatusError => HasGlobalSaveError ||
        ScenarioEditor.WccInlineDrafts.Any(draft => draft.ValidationError is not null) ||
        CampaignEditor.CampaignFieldDrafts.Any(draft => draft.ValidationError is not null) ||
        !string.IsNullOrWhiteSpace(Status) &&
        (Status.StartsWith("Could not", StringComparison.OrdinalIgnoreCase) ||
         Status.StartsWith("Did not", StringComparison.OrdinalIgnoreCase) ||
         Status.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) ||
         Status.Contains("failed", StringComparison.OrdinalIgnoreCase));
    public string ShellStatusText =>
        IsCampaignEntryNavigationBusy ? "Reading campaign entries and scenario contexts..." :
        IsLibraryIndexRefreshing && (IsUnitTypeCreationBusy || IsGlobalSaveBusy) ? LibraryIndexStatus ?? "Updating the saved package..." :
        IsCampaignCreationBusy ? Status ?? "Creating campaign..." :
        IsScenarioCreationBusy && !IsGlobalSaveBusy ? Status ?? "Creating scenario..." :
        IsUnitTypeCreationBusy && !IsGlobalSaveBusy ? Status ?? "Creating unit type..." :
        ScenarioEditor.IsWccInlineSaveProgressVisible ? ScenarioEditor.WccInlineSaveProgressStatus ?? "Saving scenario…" :
        CampaignEditor.IsCampaignFieldSaveBusy ? CampaignEditor.CampaignFieldSaveProgress ?? "Saving campaign…" :
        IsGlobalSaveBusy ? GlobalSaveStatus :
        CampaignEditor.IsCampaignSourceLoading ? CampaignEditor.CampaignSourceLoadingStatus ?? "Reading campaign files..." :
        ScenarioEditor.IsScenarioUiLoading ? ScenarioEditor.ScenarioUiLoadingStatus ?? "Reading scenario…" :
        CampaignEditor.IsCampaignUiLoading ? CampaignEditor.CampaignUiLoadingStatus ?? "Reading campaign…" :
        IsEditorSourceLoading ? EditorSourceLoadingStatus ?? "Reading editor source..." :
        IsPendingNavigationDecision ? "Unsaved changes need a decision." :
        IsLibraryIndexRefreshing ? LibraryIndexStatus ?? "Refreshing library…" : Status ?? "Ready";
    public string ShellStatusDetail => ShellStatusText +
        (IsLibraryIndexRefreshing || string.IsNullOrWhiteSpace(LibraryIndexStatus) ? string.Empty : "\n" + LibraryIndexStatus) +
        (LastSavePerformance is null ? string.Empty : "\n\n" + LastSavePerformance.Summary);

    private void InitializeShellPresentation()
    {
        CampaignEditor.ScenarioFiles.CollectionChanged += ScenarioPresentationCollectionChanged;
        StandaloneScenarioFiles.CollectionChanged += ScenarioPresentationCollectionChanged;
        CampaignEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(CampaignWindowViewModel.Status) or nameof(CampaignWindowViewModel.CurrentProject) or
                nameof(CampaignWindowViewModel.CampaignNameWml) or nameof(CampaignWindowViewModel.EditVersion) or
                nameof(CampaignWindowViewModel.IsCampaignFieldSaveBusy) or nameof(CampaignWindowViewModel.IsCampaignUiLoading) or
                nameof(CampaignWindowViewModel.CampaignUiLoadingStatus) or nameof(CampaignWindowViewModel.CampaignFieldSaveProgress) or
                nameof(CampaignWindowViewModel.CanCreateCampaignScenario) or nameof(CampaignWindowViewModel.CampaignScenarioCreationProblem) or
                nameof(CampaignWindowViewModel.CanCreateCampaignUnitType) or nameof(CampaignWindowViewModel.CampaignUnitTypeCreationProblem) or
                nameof(CampaignWindowViewModel.IsCampaignSourceLoading) or nameof(CampaignWindowViewModel.CampaignSourceLoadingStatus))
            {
                RefreshCachedCampaignTitle();
                RaiseShellPresentation();
            }
        };
        ScenarioEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ScenarioWindowViewModel.Status) or nameof(ScenarioWindowViewModel.EditVersion) or
                nameof(ScenarioWindowViewModel.IsWccInlineSaveProgressVisible) or nameof(ScenarioWindowViewModel.WccInlineSaveProgressStatus) or
                nameof(ScenarioWindowViewModel.IsScenarioUiLoading) or nameof(ScenarioWindowViewModel.ScenarioUiLoadingStatus))
                RaiseShellPresentation();
        };
        CampaignEditor.CampaignFieldDraftsChanged += (_, _) =>
        {
            RefreshCachedCampaignTitle();
            RaiseShellPresentation(refreshPending: true);
        };
        ((INotifyCollectionChanged)ScenarioEditor.WccInlineDrafts).CollectionChanged += (_, _) => RaiseShellPresentation(refreshPending: true);
        ScenarioEditor.WccMusicScheduleDraftsChanged += (_, _) => RaiseShellPresentation(refreshPending: true);
        ScenarioEditor.WccSideFlagDraftsChanged += (_, _) => RaiseShellPresentation(refreshPending: true);
        ScenarioEditor.WccEventDraftsChanged += (_, _) => RaiseShellPresentation(refreshPending: true);
        ScenarioEditor.WccSideTopologyDraftsChanged += (_, _) => RaiseShellPresentation(refreshPending: true);
        ScenarioEditor.WccLeaderAdditionDraftsChanged += (_, _) => RaiseShellPresentation(refreshPending: true);
        ScenarioEditor.WccInitialUnitDraftsChanged += (_, _) => RaiseShellPresentation(refreshPending: true);
        ScenarioEditor.WccStoryDraftChanged += (_, _) => RaiseShellPresentation(refreshPending: true);
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LibraryIndexStatus))
            {
                // File progress changes display text, not source or editing eligibility.
                OnPropertyChanged(nameof(ShellStatusText));
                OnPropertyChanged(nameof(ShellStatusDetail));
                OnPropertyChanged(nameof(ShellStatusBrush));
                return;
            }
            if (args.PropertyName is nameof(Status) or nameof(IsLibraryIndexRefreshing) or
                nameof(CanSaveCurrentContext) or nameof(CanSaveLibraryUnit) or nameof(IsGlobalSaveBusy) or nameof(IsPendingNavigationDecision) or
                nameof(GlobalSaveStatus) or nameof(HasGlobalSaveError))
                RaiseShellPresentation(refreshPending: args.PropertyName is nameof(CanSaveCurrentContext) or nameof(CanSaveLibraryUnit));
        };
        UpdateScenarioPresentationLists();
        RefreshCachedCampaignTitle();
        RaiseShellPresentation(refreshPending: true);
    }

    private void ScenarioPresentationCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
            foreach (ProjectScenarioFileVm item in args.OldItems) item.PropertyChanged -= ScenarioPresentationItemChanged;
        if (args.NewItems is not null)
            foreach (ProjectScenarioFileVm item in args.NewItems) item.PropertyChanged += ScenarioPresentationItemChanged;
        UpdateScenarioPresentationLists();
    }
    private void ScenarioPresentationItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ProjectScenarioFileVm.DisplayName)) UpdateScenarioPresentationLists();
    }
    private void UpdateScenarioPresentationLists()
    {
        SortScenarioPresentation(CampaignEditor.ScenarioFiles, SortedProjectScenarios);
        SortScenarioPresentation(StandaloneScenarioFiles, SortedLooseScenarios);
    }
    private static void SortScenarioPresentation(ObservableCollection<ProjectScenarioFileVm> source,
        ObservableCollection<ProjectScenarioFileVm> destination)
    {
        var ordered = source.Any(item => item.CampaignEntry is not null) ? source.ToArray() :
            source.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = destination.Count - 1; index >= 0; index--)
            if (!ordered.Contains(destination[index])) destination.RemoveAt(index);
        for (var index = 0; index < ordered.Length; index++)
        {
            var oldIndex = destination.IndexOf(ordered[index]);
            if (oldIndex < 0) destination.Insert(index, ordered[index]);
            else if (oldIndex != index) destination.Move(oldIndex, index);
        }
    }
    private void RaiseShellPresentation(bool refreshPending = false)
    {
        if (_raisingShellPresentation) return;
        _raisingShellPresentation = true;
        try
        {
            long loadingEpoch;
            do
            {
                loadingEpoch = EditorSourceLoadingEpoch;
                if (refreshPending) RefreshPendingEditorChanges();
                refreshPending = false;
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(IsEditorSourceLoading));
                OnPropertyChanged(nameof(IsShellBusy));
                OnPropertyChanged(nameof(CanInteractWithEditors));
                OnPropertyChanged(nameof(EditorContentOpacity));
                OnPropertyChanged(nameof(ShellStatusBrush));
                OnPropertyChanged(nameof(ShellStatusText));
                OnPropertyChanged(nameof(ShellStatusDetail));
                OnPropertyChanged(nameof(CanSaveCurrentContext));
                OnPropertyChanged(nameof(CanEditPackageScenarioRaw));
                OnPropertyChanged(nameof(IsPackageScenarioRawToolbarVisible));
                OnPropertyChanged(nameof(CanCreateScenario));
                OnPropertyChanged(nameof(ScenarioCreationGuidance));
                OnPropertyChanged(nameof(CanCreateProjectUnitType));
                OnPropertyChanged(nameof(CanCreateLibraryUnitType));
                OnPropertyChanged(nameof(UnitTypeCreationGuidance));
                // A reentrant callback may start/finish another read after earlier
                // busy bindings were notified. Replay only a changed loading epoch;
                // ordinary self-notifications must not schedule another publication.
            } while (loadingEpoch != EditorSourceLoadingEpoch);
        }
        finally
        {
            _raisingShellPresentation = false;
            // Loading/save completion changes the live source guard even when
            // no pending draft changed. Publish eligibility after source locks
            // unwind, keeping the existing folder-picker host and its identity.
            QueueCampaignDependencySourceAvailabilityNotification();
        }
    }
}
