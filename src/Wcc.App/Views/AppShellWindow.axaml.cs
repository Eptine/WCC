using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Services;

namespace Wcc.App.Views;

public partial class AppShellWindow : Window
{
    private void EditPackageScenarioRawClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not AppShellViewModel vm || vm.BeginPackageScenarioRawEdit() is not { } draft) return;
        var window = new ValidatedRawCodeEditorWindow("Scenario package RAW - " + draft.SourcePath, draft.Text,
            "Apply stages the complete authored file without changing its loaded preview. Save checks the selected campaign and its difficulties before writing. Cancel leaves the existing pending text unchanged.",
            text => ReferenceEquals(DataContext, vm) ? vm.ValidatePackageScenarioRawEdit(draft, text) : "The editor changed. Keep this text and reopen RAW.",
            text => { if (ReferenceEquals(DataContext, vm)) vm.ApplyPackageScenarioRawEdit(draft, text); })
        {
            InitialSourceSelection = WmlEditorInitialSelection.CapturedRange(draft.Text, draft.SourcePath,
                draft.Text, 0, draft.SourcePath, draft.Text, draft.InitialSelectionStart, 0)
        };
        window.Show(this);
    }

    private async void CreateProjectUnitTypeClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not AppShellViewModel vm || vm.CreateProjectUnitTypeWizardModel() is not { } model) return;
        var window = new UnitTypeCreationWindow(model);
        var request = await window.ShowDialog<UnitTypeCreationRequest?>(this);
        if (request is not null) await vm.CreateProjectUnitTypeAsync(request);
    }

    private readonly CancellationTokenSource _libraryRefreshCancellation = new();
    private bool _closingAfterSave;
    private bool _closingSavePending;
    private WmlTesterWindow? _wmlTester;

    private void OpenWmlTesterClicked(object? sender, RoutedEventArgs args)
    {
        var window = GetOrCreateWmlTester();
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private WmlTesterWindow GetOrCreateWmlTester()
    {
        if (_wmlTester is { } existing) return existing;
        var tester = new WmlTesterViewModel(captureCampaignRun: options =>
        {
            // Capture the currently selected profile for each Run, not when this
            // reusable window was opened and not from a project-pinned Core revision.
            var profile = (DataContext as AppShellViewModel)?.ActiveWesnothInstallationProfile;
            var label = profile is null ? "No selected installation" : profile.Name +
                (profile.LastDetectedInstallation?.ExactVersion is { } version ? $" ({version})" : string.Empty);
            return new WmlTesterCampaignContextRunner(profile?.InstallPath, label, options).CheckCaptured;
        });
        tester.AddonsNamespacePath = (DataContext as AppShellViewModel)?.ActiveWesnothInstallationProfile?.AddonsPath ?? string.Empty;
        var window = new WmlTesterWindow(tester);
        _wmlTester = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_wmlTester, window)) _wmlTester = null; };
        return window;
    }

    public AppShellWindow() : this(new AppShellViewModel()) { }

    internal AppShellWindow(AppShellViewModel shell)
    {
        InitializeComponent();
        shell.PendingNavigationDecisionAsync = request =>
            new PendingChangesWindow(request).ShowDialog<PendingNavigationDecision>(this);
        Opened += (_, _) => shell.CampaignDependencyNoticeAsync = async request =>
        {
            if (!IsVisible || !ReferenceEquals(DataContext, shell) || _libraryRefreshCancellation.IsCancellationRequested)
                return CampaignDependencyNoticeDecision.Continue;
            var decision = await new CampaignDependencyNoticeWindow(request)
                .ShowDialog<CampaignDependencyNoticeDecision?>(this);
            return ReferenceEquals(DataContext, shell) && !_libraryRefreshCancellation.IsCancellationRequested
                ? decision ?? CampaignDependencyNoticeDecision.Continue : CampaignDependencyNoticeDecision.Continue;
        };
        DataContext = shell;
        shell.PropertyChanged += CampaignMetadataScopeChanged;
        Opened += AppShellWindowOpened;
        Closing += AppShellWindowClosing;
        Closed += (_, _) =>
        {
            shell.PropertyChanged -= CampaignMetadataScopeChanged;
            _libraryRefreshCancellation.Cancel();
            shell.CampaignDependencyNoticeAsync = null;
            if (DataContext is AppShellViewModel vm)
            {
                vm.PendingNavigationDecisionAsync = null;
                vm.ScenarioEditor.CancelBackgroundOperations();
                vm.CampaignEditor.CancelCampaignFieldEditing();
            }
        };
    }

    private async void AppShellWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterSave || DataContext is not AppShellViewModel vm ||
            (!vm.HasPendingEditorChanges && !vm.IsGlobalSaveBusy &&
             !vm.ScenarioEditor.IsWccInlineSaveBusy && !vm.CampaignEditor.IsCampaignFieldSaveBusy))
            return;
        e.Cancel = true;
        if (_closingSavePending) return;
        _closingSavePending = true;
        try
        {
            if (!await vm.ConfirmSourceNavigationAsync()) return;
            _closingAfterSave = true;
            Close();
        }
        finally
        {
            _closingSavePending = false;
        }
    }

    private async void AppShellWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            await vm.RefreshConfiguredLibraryIndexAsync(_libraryRefreshCancellation.Token);
    }

    private void OpenGeneralSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var window = new SettingsGeneralWindow(vm);
        window.Show(this);
    }

    private async void OpenStandaloneWmlEditorClicked(object? sender, RoutedEventArgs e)
    {
        try { await App.GetFileOpenController().OpenEditorAsync(); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (DataContext is AppShellViewModel vm) vm.Status = "Could not open the WML Editor: " + failure.Message;
        }
    }

    private async void CreateCampaignClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm || vm.CreateCampaignWizardModel() is not { } model) return;
        var window = new CampaignCreationWindow(model);
        var request = await window.ShowDialog<Wcc.Core.Projects.Creation.WccCampaignCreationRequest?>(this);
        if (request is not null) await vm.CreateCampaignWithDependencySourcesAsync(request, model.DependencySourceFolders, _libraryRefreshCancellation.Token);
    }

    private async void CreateScenarioClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm || await vm.CreateScenarioWizardModelAsync(_libraryRefreshCancellation.Token) is not { } model) return;
        var window = new ScenarioCreationWindow(model);
        var request = await window.ShowDialog<ScenarioCreationRequest?>(this);
        if (request is not null) await vm.CreateScenarioAsync(request, _libraryRefreshCancellation.Token);
    }

    private async void PinCurrentProjectToActiveWesnothClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            await vm.PinCurrentProjectToActiveWesnothAsync();
    }

    private async void ViewCurrentSourceWmlClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        try
        {
            var source = await vm.BuildCurrentSourceWmlViewAsync(_libraryRefreshCancellation.Token);
            if (source is null)
            {
                vm.Status = "No WML source is selected in the current view.";
                return;
            }

            var window = new RawCodeEditorWindow(
                source.Title,
                source.RawText,
                isReadOnly: true,
                descriptionText: source.DescriptionText +
                                 (string.IsNullOrWhiteSpace(source.SourcePath)
                                     ? string.Empty
                                     : $" Source: {source.SourcePath}"));
            window.ReferenceContext = await vm.BuildRawEditorReferenceContextAsync(
                source.RawText, source.SourcePath, cancellationToken: _libraryRefreshCancellation.Token);
            window.Show(this);
        }
        catch (OperationCanceledException)
        {
            // The application is closing or a caller cancelled the diagnostic load.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            vm.Status = $"Could not open source WML: {ex.Message}";
        }
    }

    private async void ViewCurrentMacroConnectionsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        try
        {
            vm.Status = "Building connected macro source graph...";
            var source = await vm.BuildCurrentMacroConnectionsViewAsync(
                _libraryRefreshCancellation.Token);
            if (source is null)
            {
                vm.Status = "No WML source is selected in the current view.";
                return;
            }

            var window = new RawCodeEditorWindow(
                source.Title,
                source.RawText,
                isReadOnly: true,
                descriptionText: source.DescriptionText +
                                 (string.IsNullOrWhiteSpace(source.SourcePath)
                                     ? string.Empty
                                     : $" Root source: {source.SourcePath}"));
            window.Show(this);
            vm.Status = "Connected macro graph ready.";
        }
        catch (OperationCanceledException)
        {
            // The application is closing or a caller cancelled the diagnostic load.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            vm.Status = $"Could not build connected macro graph: {ex.Message}";
        }
    }

    private void ShowProjectWorkspaceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.SelectedWorkspaceIndex = 0;
    }

    private void ShowLibraryWorkspaceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.SelectedWorkspaceIndex = 1;
    }

    private void ShowCampaignMetadataClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
        {
            vm.OpenCampaignMetadataInCampaignTab();
            CampaignMetadataPanel.IsVisible = vm.HasOpenCampaignProject;
        }
    }

    private void CampaignMetadataRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel { HasOpenCampaignProject: true })
            CampaignMetadataPanel.IsVisible = !CampaignMetadataPanel.IsVisible;
    }

    private void CampaignMetadataScopeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppShellViewModel.HasOpenCampaignProject) &&
            DataContext is AppShellViewModel { HasOpenCampaignProject: false })
            CampaignMetadataPanel.IsVisible = false;
    }

    private void RemovePendingChangeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PendingEditorChangeRemovalVm row }) row.Remove();
    }

    private async void AddCampaignScenarioFromFileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach scenario cfg to campaign",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Wesnoth cfg") { Patterns = ["*.cfg"] },
                FilePickerFileTypes.All
            ]
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        if (!await vm.ConfirmSourceNavigationAsync())
            return;

        var path = file.Path.LocalPath;
        var text = await File.ReadAllTextAsync(path);

        vm.CampaignEditor.AddScenarioFile(path, text);

        vm.SelectedCampaignScenarioFile = vm.CampaignEditor.SelectedScenarioFile;

        if (vm.SelectedCampaignScenarioFile is not null)
            vm.OpenScenarioAssetInProjectScenarioTab(vm.SelectedCampaignScenarioFile.Asset);
    }

    private async void AddCampaignUnitFromFileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach unit cfg to campaign",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Wesnoth cfg") { Patterns = ["*.cfg"] },
                FilePickerFileTypes.All
            ]
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        if (!await vm.ConfirmSourceNavigationAsync())
            return;

        var path = file.Path.LocalPath;
        var text = await File.ReadAllTextAsync(path);

        vm.CampaignEditor.AddUnitFile(path, text);
        vm.RefreshProjectUnitWorkspaceEntries();
        var addedAsset = vm.CampaignEditor.SelectedUnitFile?.Asset;
        var addedEntry = vm.ProjectOwnedUnitEntries.FirstOrDefault(entry =>
            ReferenceEquals(entry.OwnedAsset, addedAsset));
        if (addedEntry is not null)
            await vm.OpenProjectUnitEntryAsync(addedEntry);
    }

    private async void RemoveCampaignScenarioClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var selected = vm.SelectedCampaignScenarioFile;
        if (!await vm.ConfirmSourceNavigationAsync())
            return;

        vm.CampaignEditor.SelectedScenarioFile = selected;
        vm.CampaignEditor.RemoveSelectedScenarioFile();
        vm.SelectedCampaignScenarioFile = vm.CampaignEditor.SelectedScenarioFile;

        vm.ShowProjectScenarioWorkspace();
    }

    private async void MoveCampaignScenarioUpClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var selected = vm.SelectedCampaignScenarioFile;
        if (!await vm.ConfirmSourceNavigationAsync())
            return;

        vm.CampaignEditor.SelectedScenarioFile = selected;
        vm.CampaignEditor.MoveSelectedScenarioUp();
        vm.SelectedCampaignScenarioFile = vm.CampaignEditor.SelectedScenarioFile;
    }

    private async void MoveCampaignScenarioDownClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var selected = vm.SelectedCampaignScenarioFile;
        if (!await vm.ConfirmSourceNavigationAsync())
            return;

        vm.CampaignEditor.SelectedScenarioFile = selected;
        vm.CampaignEditor.MoveSelectedScenarioDown();
        vm.SelectedCampaignScenarioFile = vm.CampaignEditor.SelectedScenarioFile;
    }

    private void ProjectScenarioTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm ||
            vm.SelectedTabIndex != 1 ||
            !vm.IsProjectScenarioWorkspace ||
            vm.SelectedCampaignScenarioFile is null)
            return;

        vm.OpenScenarioAssetInProjectScenarioTab(vm.SelectedCampaignScenarioFile.Asset);
    }

    private void StandaloneScenarioSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm || vm.SelectedStandaloneScenarioFile is null)
            return;

        if (vm.ScenarioEditor.IsShowingAsset(vm.SelectedStandaloneScenarioFile.Asset) &&
            vm.ScenarioSession.Scope == WccEditorSessionScope.LooseFile)
        {
            return;
        }

        vm.OpenScenarioAssetInShell(vm.SelectedStandaloneScenarioFile.Asset);
    }

    private void ShowProjectScenarioWorkspaceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.ShowProjectScenarioWorkspace();
    }

    private async void ProjectUnitTileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm ||
            vm.SelectedTabIndex != 2 ||
            !vm.IsProjectUnitWorkspace ||
            sender is not Button button ||
            button.Tag is not ProjectUnitWorkspaceEntryVm selected)
            return;

        if (selected.OwnedAsset is not null &&
            vm.UnitEditor.IsShowingAsset(selected.OwnedAsset) &&
            vm.UnitEditor.SelectedUnit?.Document.InternalId == selected.OwnedDocumentInternalId &&
            vm.UnitSession.Scope == WccEditorSessionScope.Project)
        {
            return;
        }

        await vm.OpenProjectUnitEntryAsync(selected);
        ScrollCurrentUnitEditorToTop();
    }

    private void StandaloneUnitSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm || vm.SelectedStandaloneUnitFile is null)
            return;

        if (vm.UnitEditor.IsShowingAsset(vm.SelectedStandaloneUnitFile.Asset) &&
            vm.UnitSession.Scope == WccEditorSessionScope.LooseFile)
        {
            return;
        }

        vm.OpenUnitAssetInShell(vm.SelectedStandaloneUnitFile.Asset);
        ScrollCurrentUnitEditorToTop();
    }

    private void ShowProjectUnitWorkspaceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.ShowProjectUnitWorkspace();
    }

    private async void OpenStandaloneScenarioClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open scenario cfg",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Wesnoth cfg") { Patterns = ["*.cfg"] },
                FilePickerFileTypes.All
            ]
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        var path = file.Path.LocalPath;
        var text = await File.ReadAllTextAsync(path);
        vm.AddStandaloneScenarioFromText(path, text);
    }

    private async void OpenUnitCfgFromShellClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open unit cfg",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Wesnoth cfg") { Patterns = ["*.cfg"] },
                FilePickerFileTypes.All
            ]
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        var path = file.Path.LocalPath;
        var text = await File.ReadAllTextAsync(path);

        if (!vm.AddStandaloneUnitFromText(path, text))
            return;

        ScrollCurrentUnitEditorToTop();
    }

    private async void SaveCurrentUnitFromShellClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var patched = vm.UnitEditor.RenderPatchedFile();
        if (patched is null)
        {
            vm.Status = "Nothing to save.";
            return;
        }

        var suggested = vm.UnitEditor.LoadedPath is not null
            ? Path.GetFileNameWithoutExtension(vm.UnitEditor.LoadedPath) + ".patched.cfg"
            : "patched.cfg";

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save patched cfg",
            SuggestedFileName = suggested,
            FileTypeChoices =
            [
                new FilePickerFileType("Wesnoth cfg") { Patterns = ["*.cfg"] }
            ]
        });

        if (file is null)
            return;

        await File.WriteAllTextAsync(file.Path.LocalPath, patched);
        vm.Status = "Saved patched file.";
    }

    private void AddCurrentUnitToLibraryClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.AddCurrentUnitToLibrary();
    }

    private void RemoveStandaloneScenarioClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.RemoveSelectedStandaloneScenario();
    }

    private void RemoveStandaloneUnitClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.RemoveSelectedStandaloneUnit();
    }

    private async void UnitCatalogTileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm || sender is not Button button || button.DataContext is not UnitTypeCatalogEntryVm entryVm)
            return;

        await vm.OpenUnitCatalogEntryInShellAsync(entryVm);
        ScrollCurrentUnitEditorToTop();
    }
    private async void CloneCurrentUnitClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm) return;
        var model = await vm.CreateSelectedUnitCopyWizardModelAsync(libraryInspector: false);
        if (model is null) return;
        var request = await new UnitTypeCreationWindow(model).ShowDialog<UnitTypeCreationRequest?>(this);
        if (request is not null) await vm.CreateLibraryUnitTypeAsync(request);
    }
    private void OpenRaceManagerClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        vm.EnsureDirectPresetCatalogsReady();

        var window = new PresetManagerWindow(vm, new RacePresetManagerAdapter());
        window.Show(this);
    }
    private async void SaveCurrentContextClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            await vm.SaveCurrentContextAsync();
    }

    private void DeleteCurrentUnitClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.DeleteCurrentUnitFromLibrary();
    }
    private void ScrollCurrentUnitEditorToTop()
    {
        if (StandaloneUnitEditorView?.IsVisible == true)
            StandaloneUnitEditorView.ScrollToTop();
    }
}
