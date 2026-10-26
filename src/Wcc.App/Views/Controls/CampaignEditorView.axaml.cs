using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.References;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.App.Views;
using Avalonia.Input;
using Wcc.Core.Campaigns;

namespace Wcc.App.Views.Controls;

public partial class CampaignEditorView : UserControl
{
    // Standalone views keep local progress; the shell supplies one shared overlay.
    public static readonly StyledProperty<bool> ShowLoadingIndicatorsProperty =
        AvaloniaProperty.Register<CampaignEditorView, bool>(nameof(ShowLoadingIndicators), true);

    public bool ShowLoadingIndicators
    {
        get => GetValue(ShowLoadingIndicatorsProperty);
        set => SetValue(ShowLoadingIndicatorsProperty, value);
    }

    public static readonly RoutedEvent<RoutedEventArgs> MetadataRequestedEvent =
        RoutedEvent.Register<CampaignEditorView, RoutedEventArgs>(nameof(MetadataRequested), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs> MetadataRequested
    {
        add => AddHandler(MetadataRequestedEvent, value);
        remove => RemoveHandler(MetadataRequestedEvent, value);
    }

    public static readonly StyledProperty<bool> ShowSaveAsButtonProperty =
        AvaloniaProperty.Register<CampaignEditorView, bool>(nameof(ShowSaveAsButton), true);

    public bool ShowSaveAsButton
    {
        get => GetValue(ShowSaveAsButtonProperty);
        set => SetValue(ShowSaveAsButtonProperty, value);
    }

    public static readonly StyledProperty<bool> ShowProjectListsProperty =
        AvaloniaProperty.Register<CampaignEditorView, bool>(nameof(ShowProjectLists), true);

    public bool ShowProjectLists
    {
        get => GetValue(ShowProjectListsProperty);
        set => SetValue(ShowProjectListsProperty, value);
    }

    public CampaignEditorView()
    {
        InitializeComponent();
    }

    private readonly Dictionary<TextBox, object> _campaignTextOwners = new();
    private void CampaignRowTextGotFocus(object? sender, GotFocusEventArgs args)
    {
        if (sender is TextBox { DataContext: CampaignDifficultyDisplayRow or CampaignPathDisplayRow } box)
            _campaignTextOwners[box] = box.DataContext;
    }
    private void CampaignRowTextLostFocus(object? sender, RoutedEventArgs args)
    {
        if (sender is not TextBox box || !_campaignTextOwners.Remove(box, out var owner)) return;
        // Commit the owner that received focus, even if a later publication has
        // rebound this control; the shared buffer rejects a stale source owner.
        if (owner is CampaignDifficultyDisplayRow difficulty) difficulty.CommitTextInput();
        else if (owner is CampaignPathDisplayRow path) path.CommitTextInput();
    }
    private void MetadataClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignWindowViewModel { HasCampaignMainSource: true })
            RaiseEvent(new RoutedEventArgs(MetadataRequestedEvent));
    }

    private void ViewDependencyMetadataClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignWindowViewModel { CampaignDependencyMetadataLocation: { } source })
            ShowSource(source);
    }

    private async void AttachDependencySourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm ||
            vm.BeginCampaignDependencySourceSelection() is not { } selection ||
            TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose an add-on source folder containing _main.cfg", AllowMultiple = false });
        if (folders.Count == 0 || !ReferenceEquals(DataContext, vm) ||
            !ReferenceEquals(TopLevel.GetTopLevel(this), top)) return;
        await vm.AttachCampaignDependencySourceAsync(selection, folders[0].Path.LocalPath);
    }

    private async void ForgetDependencySourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignWindowViewModel vm && sender is Button { Tag: CampaignAdditionalDependencySourceRow row })
            await vm.ForgetCampaignDependencySourceAsync(row);
    }

    private void EditDependencyLoadingRawClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm) return;
        if (vm.BeginCampaignMainRawEdit() is { } draft) { ShowRaw(vm, draft); return; }
        if (vm.CurrentProject?.SourcePath is not { } path) return;
        try { App.GetFileOpenController().EnqueueFiles([path]); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        { vm.Status = "Could not open dependency loading WML: " + failure.Message; }
    }
    private async void AddManagedDependencyClicked(object? sender, RoutedEventArgs args) => await ShowDependencyWizard(null);
    private async void EditManagedDependencyClicked(object? sender, RoutedEventArgs args)
    { if (sender is Button { Tag: CampaignManagedDependencyRow row }) await ShowDependencyWizard(row); }
    private async System.Threading.Tasks.Task ShowDependencyWizard(CampaignManagedDependencyRow? row)
    {
        if (DataContext is not CampaignWindowViewModel vm || TopLevel.GetTopLevel(this) is not Window owner ||
            await vm.BeginCampaignDependencyWizardAsync(row) is not { } model) return;
        var dialog = new CampaignDependencyCreationWindow(model);
        var spec = await dialog.ShowDialog<WccCampaignDependencySpec?>(owner);
        if (spec is not null && ReferenceEquals(DataContext, vm)) await vm.StageCampaignDependencyAsync(model, spec);
    }
    private async void RemoveManagedDependencyClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm || sender is not Button { Tag: CampaignManagedDependencyRow row }) return;
        if (!await vm.PrepareCampaignDependencyAuthoringAsync()) return;
        if (ReferenceEquals(DataContext, vm)) vm.RemoveManagedCampaignDependency(row);
    }
    private async void EditDependencyCardClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CampaignDependencyCard card }) return;
        if (card.Managed is { } managed) await ShowDependencyWizard(managed);
        else EditDependencyLoadingRawClicked(sender, args);
    }
    private async void RemoveDependencyCardClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm || sender is not Button { Tag: CampaignDependencyCard { Managed: { } row } }) return;
        if (await vm.PrepareCampaignDependencyAuthoringAsync() && ReferenceEquals(DataContext, vm)) vm.RemoveManagedCampaignDependency(row);
    }
    private async void ForgetDependencyCardSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignWindowViewModel vm && sender is Button { Tag: CampaignDependencyCard { Manual: { } row } })
            await vm.ForgetCampaignDependencySourceAsync(row);
    }
    private async void PickCampaignImageClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm || sender is not Button { Tag: WccCampaignFieldEditorVm field } || !field.CanEdit || !field.CommitTextInput()) return;
        var project = vm.CurrentProject; var source = vm.GetCurrentMainSourceRawWml(); var selected = vm.SelectedCampaignEntry;
        var previous = field.TextValue;
        var reference = await PickExistingImage(vm);
        if (reference is null || !ReferenceEquals(DataContext, vm) || !ReferenceEquals(project, vm.CurrentProject) ||
            !ReferenceEquals(selected, vm.SelectedCampaignEntry) || source != vm.GetCurrentMainSourceRawWml() ||
            !vm.CampaignFields.Contains(field) || !field.CanEdit || previous != field.TextValue) return;
        field.Value = reference + (WccImageExpressionParser.TryParse(previous)?.ModifierSuffix ?? "");
    }
    private void FitDifficultyImageClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignWindowViewModel vm && sender is Button { Tag: CampaignDifficultyDisplayRow row } &&
            vm.CampaignDifficultyPresentationRows.Contains(row)) row.FitImage();
    }
    private async void PickDifficultyImageClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm || sender is not Button { Tag: CampaignDifficultyDisplayRow row } || !row.CanEdit || !row.CommitTextInput()) return;
        var project = vm.CurrentProject; var source = vm.GetCurrentMainSourceRawWml(); var selected = vm.SelectedCampaignEntry;
        var previous = row.Image;
        var reference = await PickExistingImage(vm);
        if (reference is null || !ReferenceEquals(DataContext, vm) || !ReferenceEquals(project, vm.CurrentProject) ||
            !ReferenceEquals(selected, vm.SelectedCampaignEntry) || source != vm.GetCurrentMainSourceRawWml() ||
            !vm.CampaignDifficultyPresentationRows.Contains(row) || !row.CanEdit || previous != row.Image) return;
        row.Image = reference + (WccImageExpressionParser.TryParse(previous)?.ModifierSuffix ?? "");
    }
    private async System.Threading.Tasks.Task<string?> PickExistingImage(CampaignWindowViewModel vm)
    {
        if (TopLevel.GetTopLevel(this) is not { } owner) return null;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose an existing Core, campaign or dependency image", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Wesnoth images") { Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg", "*.bmp", "*.gif"] }]
            });
            if (!ReferenceEquals(DataContext, vm) || !ReferenceEquals(TopLevel.GetTopLevel(this), owner) || files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return null;
            var reference = vm.CampaignPickedImageReference(path);
            if (reference is null) vm.Status = "Choose an image in this campaign's images folder, the selected Core installation, or a visible dependency. Put custom artwork in the campaign images folder before selecting it.";
            return reference;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { if (ReferenceEquals(DataContext, vm)) vm.Status = "The image could not be selected: " + error.Message; return null; }
    }

    private async void CampaignPresetsClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignWindowViewModel { CanOpenCampaignPresets: true } vm && TopLevel.GetTopLevel(this) is Window owner)
            await new CampaignLocalPresetManagerWindow(new CampaignLocalPresetManagerViewModel(vm)).ShowDialog(owner);
    }

    private void AddCreditGroupClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignWindowViewModel vm) vm.AddCampaignCreditGroup(); }
    private void RemoveCreditGroupClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignWindowViewModel vm && sender is Button { Tag: CampaignCreditGroupRow group }) vm.RemoveCampaignCreditGroup(group); }
    private void AddCreditEntryClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignWindowViewModel vm && sender is Button { Tag: CampaignCreditGroupRow group }) vm.AddCampaignCreditEntry(group); }
    private void RemoveCreditEntryClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignWindowViewModel vm && sender is Button { Tag: CampaignCreditEntryRow entry }) vm.RemoveCampaignCreditEntry(entry); }
    private void ResetCreditsClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignWindowViewModel vm) vm.ResetCampaignCreditsDrafts(); }

    private void AddCampaignPathClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignWindowViewModel vm) vm.AddCampaignPath(""); }

    private void RemoveCampaignPathClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignWindowViewModel vm && sender is Button { Tag: CampaignPathDisplayRow row }) vm.RemoveCampaignPath(row.EditorRow); }
    private void ResetCampaignStructureClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignWindowViewModel vm) vm.ResetCampaignStructureDrafts(); }
    private async void PickCampaignPathClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm || !vm.CanEditCampaignStructure ||
            TopLevel.GetTopLevel(this) is not { } top) return;
        var project = vm.CurrentProject;
        var source = vm.GetCurrentMainSourceRawWml();
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose a folder in this campaign", AllowMultiple = false });
        if (folders.Count == 0 || !ReferenceEquals(DataContext, vm) || !ReferenceEquals(project, vm.CurrentProject) ||
            source != vm.GetCurrentMainSourceRawWml() || !vm.CanEditCampaignStructure) return;
        vm.AddCampaignPathFromFolder(folders[0].Path.LocalPath);
    }

    private async void OpenClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm)
            return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open campaign _main.cfg",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Wesnoth cfg") { Patterns = ["*.cfg"] },
                FilePickerFileTypes.All
            ]
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        if (top.DataContext is AppShellViewModel shellVm &&
            !await shellVm.ConfirmSourceNavigationAsync())
        {
            return;
        }

        var path = file.Path.LocalPath;
        var text = await File.ReadAllTextAsync(path);
        try
        {
            await vm.LoadFromTextAsync(path, text);
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not load campaign: {ex.Message}";
        }
    }

    private async void SaveAsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm)
            return;

        if (vm.CaptureCampaignSourceExport() is not { } snapshot) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var suggested = vm.LoadedPath is not null
            ? Path.GetFileNameWithoutExtension(vm.LoadedPath) + ".patched.cfg"
            : "_main.patched.cfg";

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save patched campaign cfg",
            SuggestedFileName = suggested,
            FileTypeChoices =
            [
                new FilePickerFileType("Wesnoth cfg") { Patterns = ["*.cfg"] }
            ]
        });

        if (file is null) return;
        if (!ReferenceEquals(DataContext, vm) || !vm.IsCurrentCampaignSourceExport(snapshot)) return;
        await File.WriteAllTextAsync(file.Path.LocalPath, snapshot.SourceText);
        vm.Status = "Saved patched campaign file.";
    }

    private async void AttachScenarioClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm)
            return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach scenario cfg",
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
        vm.AddScenarioFile(path, text);
    }

    private void RemoveScenarioClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CampaignWindowViewModel vm)
            vm.RemoveSelectedScenarioFile();
    }

    private void MoveScenarioUpClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CampaignWindowViewModel vm)
            vm.MoveSelectedScenarioUp();
    }

    private void MoveScenarioDownClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CampaignWindowViewModel vm)
            vm.MoveSelectedScenarioDown();
    }

    private void SetFirstScenarioClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CampaignWindowViewModel vm)
            vm.SetSelectedScenarioAsFirst();
    }

    private async void AttachUnitClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm)
            return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach unit cfg",
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
        vm.AddUnitFile(path, text);
    }

    private void RemoveUnitClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CampaignWindowViewModel vm)
            vm.RemoveSelectedUnitFile();
    }

    private async void ExportProjectClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm)
            return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;
        var shell = top.DataContext as AppShellViewModel;
        if (shell is not null && (!ReferenceEquals(shell.CampaignEditor, vm) ||
            !await shell.PrepareCampaignProjectExportAsync())) return;
        if (!ReferenceEquals(DataContext, vm) || !ReferenceEquals(TopLevel.GetTopLevel(this), top) ||
            !ReferenceEquals(top.DataContext as AppShellViewModel, shell)) return;
        if (vm.CaptureCampaignSourceExport() is not { } snapshot) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose export folder",
            AllowMultiple = false
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder is null)
            return;
        if (!ReferenceEquals(DataContext, vm) || !ReferenceEquals(TopLevel.GetTopLevel(this), top) ||
            !ReferenceEquals(top.DataContext as AppShellViewModel, shell) || !vm.IsCurrentCampaignSourceExport(snapshot)) return;
        if (shell is not null && !await shell.PrepareCampaignProjectExportAsync()) return;
        if (!ReferenceEquals(DataContext, vm) || !ReferenceEquals(TopLevel.GetTopLevel(this), top) ||
            !ReferenceEquals(top.DataContext as AppShellViewModel, shell) || !vm.IsCurrentCampaignSourceExport(snapshot)) return;

        var result = vm.ExportProject(folder.Path.LocalPath);
        if (result is null)
        {
            vm.Status = "Nothing to export.";
            return;
        }

        vm.Status = $"Exported {result.WrittenFiles.Count} file(s).";
    }

    private void DiscardFieldsClicked(object? sender, RoutedEventArgs args) =>
        (DataContext as CampaignWindowViewModel)?.DiscardCampaignFieldDrafts();

    private void ViewMainSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm || vm.GetCurrentMainSourceRawWml() is not { } text) return;
        ShowSource(new(vm.LoadedPath ?? "_main.cfg", text, 0, 0, vm.CampaignSourceGuidance));
    }
    private void ViewCodeSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel { SelectedCampaignAdditionalCode: { } entry } vm ||
            vm.GetCurrentMainSourceRawWml() is not { } text) return;
        ShowSource(new(vm.LoadedPath ?? "_main.cfg", text, entry.StartOffset, entry.EndOffsetExclusive - entry.StartOffset,
            "Exact source for " + entry.Name + ". " + vm.CampaignSourceGuidance));
    }
    private void ViewDifficultySourceClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: CampaignDifficultyDisplayRow { SourceLocation: { } source } }) ShowSource(source);
    }
    private void ViewCampaignPathSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: CampaignPathDisplayRow { SourceLocation: { } source } }) ShowSource(source);
    }
    private void EditCampaignPathSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm || sender is not Button { Tag: CampaignPathDisplayRow row } ||
            !row.CanEditRaw) return;
        var draft = row.RawEntry is { } entry ? vm.BeginCampaignSourceEntryRawEdit(entry) : vm.BeginCampaignMainRawEdit();
        if (draft is not null) ShowRaw(vm, draft, row.SourceLocation);
    }
    private void EditMainSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignWindowViewModel vm && vm.BeginCampaignMainRawEdit() is { } draft) ShowRaw(vm, draft);
    }
    private void EditCodeSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignWindowViewModel { SelectedCampaignAdditionalCode: { } entry } vm &&
            vm.BeginCampaignSourceEntryRawEdit(entry) is { } draft) ShowRaw(vm, draft);
    }
    private void EditDifficultySourceClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignWindowViewModel vm || sender is not Button { Tag: CampaignDifficultyDisplayRow difficulty } ||
            !difficulty.CanEditRaw) return;
        var draft = difficulty.RawEntry is { } entry ? vm.BeginCampaignSourceEntryRawEdit(entry) : vm.BeginCampaignMainRawEdit();
        if (draft is not null) ShowRaw(vm, draft, difficulty.SourceLocation);
    }
    private void ShowRaw(CampaignWindowViewModel vm, CampaignMainRawEditDraft draft) => ShowRaw(vm, draft, null);

    private void ShowRaw(CampaignWindowViewModel vm, CampaignMainRawEditDraft draft, CampaignFieldSourceLocation? source)
    {
        var window = new ValidatedRawCodeEditorWindow("Campaign RAW — " + draft.SourcePath, draft.Text,
            "Edit the exact containing source. Save in this window updates the working draft; ordinary Save writes the file.",
            text => ReferenceEquals(DataContext, vm) ? vm.ValidateCampaignMainRawEdit(draft, text) : "The editor changed. Reopen RAW source.",
            text => { if (ReferenceEquals(DataContext, vm)) vm.ApplyCampaignMainRawEdit(draft, text); })
        {
            InitialSourceSelection = WmlEditorInitialSelection.CapturedRange(draft.Text, draft.SourcePath,
                draft.SourceText, draft.Start, source?.SourcePath, source?.SourceText,
                source?.SelectionStart ?? 0, source?.SelectionLength ?? 0)
        };
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }
    private void ShowSource(CampaignFieldSourceLocation source)
    {
        var window = new RawCodeEditorWindow("Campaign source — " + source.SourcePath, source.SourceText, isReadOnly: true,
            descriptionText: source.Description, initialSelectionStart: source.SelectionStart, initialSelectionLength: source.SelectionLength);
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }

    private void OpenSelectedScenarioClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm || vm.SelectedScenarioFile is null)
            return;

        if (TryOpenScenarioInShell(vm.SelectedScenarioFile.Asset))
        {
            vm.Status = $"Opened scenario in shell: {vm.SelectedScenarioFile.DisplayName}";
            return;
        }

        var window = new ScenarioWindow();
        var scenarioVm = new ScenarioWindowViewModel();
        scenarioVm.LoadFromExistingScenarioAsset(vm.SelectedScenarioFile.Asset);

        window.DataContext = scenarioVm;
        window.Title = $"WCC Scenario Editor - {vm.SelectedScenarioFile.DisplayName}";
        window.Show();
    }

    private void OpenSelectedUnitClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm || vm.SelectedUnitFile is null)
            return;

        if (TryOpenUnitInShell(vm.SelectedUnitFile.Asset))
        {
            vm.Status = $"Opened unit type in shell: {vm.SelectedUnitFile.DisplayName}";
            return;
        }

        var window = new MainWindow();
        var unitVm = new MainWindowViewModel();
        unitVm.LoadFromExistingUnitAsset(vm.SelectedUnitFile.Asset);

        window.DataContext = unitVm;
        window.Title = $"WCC Unit Editor - {vm.SelectedUnitFile.DisplayName}";
        window.Show();
    }

    private bool TryOpenScenarioInShell(Wcc.Core.Projects.ScenarioFileAsset asset)
    {
        if (TopLevel.GetTopLevel(this) is Window topWindow &&
            topWindow.DataContext is AppShellViewModel shellVm)
        {
            shellVm.OpenScenarioAssetInShell(asset);
            return true;
        }

        return false;
    }

    private bool TryOpenUnitInShell(Wcc.Core.Projects.UnitFileAsset asset)
    {
        if (TopLevel.GetTopLevel(this) is Window topWindow &&
            topWindow.DataContext is AppShellViewModel shellVm)
        {
            shellVm.OpenUnitAssetInShell(asset);
            return true;
        }

        return false;
    }
    private void TextDomainFieldRawPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        ToolTip.SetTip(button, vm.GetTextDomainFieldReason(key) ?? "This field is read-only in v0.1.");
    }

    private void CampaignFieldRawPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        ToolTip.SetTip(button, vm.GetCampaignFieldReason(key) ?? "This field is read-only in v0.1.");
    }

    private void OpenTextDomainFieldRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        var text = vm.GetTextDomainFieldRawText(key);
        if (text is null)
            return;

        var window = new RawCodeEditorWindow(
            title: $"Raw code - textdomain field '{key}'",
            initialText: text,
            onSave: edited => vm.ApplyTextDomainFieldRawText(key, edited))
        { InitialSourceSelection = WmlEditorInitialSelection.FieldFragment(text, key) };

        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    private void OpenCampaignFieldRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        var text = vm.GetCampaignFieldRawText(key);
        if (text is null)
            return;

        var window = new RawCodeEditorWindow(
            title: $"Raw code - campaign field '{key}'",
            initialText: text,
            onSave: edited => vm.ApplyCampaignFieldRawText(key, edited))
        { InitialSourceSelection = WmlEditorInitialSelection.FieldFragment(text, key) };

        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }
}
