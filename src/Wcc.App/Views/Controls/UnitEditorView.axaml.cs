using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Avalonia.Input;
using Wcc.App.Views;
using Avalonia.Threading;

namespace Wcc.App.Views.Controls;

public partial class UnitEditorView : UserControl
{
    public static readonly StyledProperty<bool> ShowNavigationPaneProperty =
        AvaloniaProperty.Register<UnitEditorView, bool>(nameof(ShowNavigationPane), true);

    public bool ShowNavigationPane
    {
        get => GetValue(ShowNavigationPaneProperty);
        set => SetValue(ShowNavigationPaneProperty, value);
    }
    public static readonly StyledProperty<bool> ShowFileButtonsProperty =
        AvaloniaProperty.Register<UnitEditorView, bool>(nameof(ShowFileButtons), true);

    public bool ShowFileButtons
    {
        get => GetValue(ShowFileButtonsProperty);
        set => SetValue(ShowFileButtonsProperty, value);
    }

    public UnitEditorView()
    {
        InitializeComponent();
    }

    private async void OpenClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Wesnoth .cfg",
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

        vm.LoadFromText(path, text);
    }

    private async void SaveAsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var patched = vm.RenderPatchedFile();
        if (patched is null)
        {
            vm.Status = "Nothing to save.";
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var suggested = vm.LoadedPath is not null
            ? Path.GetFileNameWithoutExtension(vm.LoadedPath) + ".patched.cfg"
            : "patched.cfg";

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save patched cfg",
            SuggestedFileName = suggested,
            FileTypeChoices =
            [
                new FilePickerFileType("Wesnoth cfg") { Patterns = ["*.cfg"] }
            ]
        });

        if (file is null) return;

        await File.WriteAllTextAsync(file.Path.LocalPath, patched);
        vm.Status = "Saved patched file.";
    }

    private void AddAbilityClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.AddAbilityMacro();
    }

    private void RemoveAbilityClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.RemoveSelectedAbilityMacro();
    }

    private void RemoveAbilityApplicationClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.RemoveSelectedAbilityApplication();
    }

    private void RemoveAbilityRegistryIdClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.RemoveSelectedAbilityRegistryId();
    }

    private void AddAttackSpecialClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.AddAttackSpecialMacro();
    }

    private void RemoveAttackSpecialClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.RemoveSelectedAttackSpecialMacro();
    }

    private void RemoveAttackSpecialApplicationClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.RemoveSelectedAttackSpecialApplication();
    }

    private void RemoveAttackSpecialRegistryIdClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.RemoveSelectedAttackSpecialRegistryId();
    }
    private void UnitFieldRawPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        ToolTip.SetTip(button, vm.GetUnitFieldReason(key) ?? "This field is read-only in v0.1.");
    }

    private void AttackFieldRawPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        ToolTip.SetTip(button, vm.GetAttackFieldReason(key) ?? "This field is read-only in v0.1.");
    }

    private void OpenUnitFieldRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        var text = vm.GetUnitFieldRawText(key);
        if (text is null)
            return;

        var isCurrent = vm.CaptureRawEditorApplyGuard();
        var window = new RawCodeEditorWindow(
            title: $"Raw code - unit field '{key}'",
            initialText: text,
            onSave: MainWindowViewModel.GuardRawEditorApply(
                () => ReferenceEquals(DataContext, vm) && isCurrent(), text,
                edited => vm.ApplyUnitFieldRawText(key, edited)))
        { InitialSourceSelection = WmlEditorInitialSelection.FieldFragment(text, key) };

        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    private void OpenAttackFieldRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        var text = vm.GetAttackFieldRawText(key);
        if (text is null)
            return;

        var isCurrent = vm.CaptureRawEditorApplyGuard();
        var window = new RawCodeEditorWindow(
            title: $"Raw code - attack field '{key}'",
            initialText: text,
            onSave: MainWindowViewModel.GuardRawEditorApply(
                () => ReferenceEquals(DataContext, vm) && isCurrent(), text,
                edited => vm.ApplyAttackFieldRawText(key, edited)))
        { InitialSourceSelection = WmlEditorInitialSelection.FieldFragment(text, key) };

        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }
    private async void OpenAdvancesToPickerClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var pickerVm = new CatalogPickerWindowViewModel(
            vm.GetAdvanceToCatalogOptions(),
            vm.UnitAdvancesTo,
            allowMultiple: true,
            titleText: "Pick Advancement Targets",
            descriptionText: "Select one or more unit types for advances_to.",
            currentLibraryEntityIds: vm.GetCurrentAdvancesToBindingEntityIds());

        var window = new CatalogPickerWindow(pickerVm);

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            await window.ShowDialog((Window?)null);

        if (window.SelectedCsv is not null)
            vm.SetUnitAdvancesToFromPicker(window.SelectedCsv, window.SelectedSelections);
    }
    public void ScrollToTop()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (EditorScrollViewer is not null)
                EditorScrollViewer.Offset = new Vector(0, 0);
        }, DispatcherPriority.Background);
    }
    private void UnitSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
            ScrollToTop();
    }
    private async void OpenRacePickerClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var pickerVm = new CatalogPickerWindowViewModel(
            vm.GetRaceCatalogOptions(),
            vm.UnitRace,
            allowMultiple: false,
            titleText: "Pick Race",
            descriptionText: "Select a race for this unit.",
            currentLibraryEntityIds: vm.GetCurrentRaceBindingEntityIds());

        var window = new CatalogPickerWindow(pickerVm);

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            await window.ShowDialog((Window?)null);

        if (window.SelectedCsv is not null)
            vm.SetUnitRaceFromPicker(window.SelectedCsv, window.SelectedSelections);
    }
    private async void OpenMovementTypePickerClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm)
        return;

    var pickerVm = new CatalogPickerWindowViewModel(
        vm.GetMovementTypeCatalogOptions(),
        vm.UnitMovementType,
        allowMultiple: false,
        titleText: "Pick Movement Type",
        descriptionText: "Select a movement type for this unit.",
        currentLibraryEntityIds: vm.GetCurrentMovementTypeBindingEntityIds());

    var window = new CatalogPickerWindow(pickerVm);

    if (TopLevel.GetTopLevel(this) is Window owner)
        await window.ShowDialog(owner);
    else
        await window.ShowDialog((Window?)null);

    if (window.SelectedCsv is not null)
        vm.SetUnitMovementTypeFromPicker(window.SelectedCsv, window.SelectedSelections);
}

private async void OpenAbilityPickerClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm)
        return;

    var pickerVm = new CatalogPickerWindowViewModel(
        vm.GetAbilityCatalogOptions(),
        currentValue: null,
        allowMultiple: true,
        titleText: "Pick Abilities",
        descriptionText: "Select registry IDs or macro invocations. WCC keeps the chosen source representation when writing the unit.",
        currentSelections: vm.GetCurrentAbilityCatalogSelections());

    var window = new CatalogPickerWindow(pickerVm);

    if (TopLevel.GetTopLevel(this) is Window owner)
        await window.ShowDialog(owner);
    else
        await window.ShowDialog((Window?)null);

    if (window.WasAccepted)
        vm.SetAbilityApplicationsFromPicker(window.SelectedSelections);
}

private async void OpenAbilityRegistryPickerClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm)
        return;

    var pickerVm = new CatalogPickerWindowViewModel(
        vm.GetAbilityRegistryCatalogOptions(),
        vm.GetCurrentAbilityRegistryIdsCsv(),
        allowMultiple: true,
        titleText: "Pick Wesnoth 1.20 Abilities",
        descriptionText: "Select registry ids written to abilities_list. Unknown existing ids remain available and are never discarded automatically.",
        currentLibraryEntityIds: vm.GetCurrentAbilityIdBindingEntityIds());

    var window = new CatalogPickerWindow(pickerVm);
    if (TopLevel.GetTopLevel(this) is Window owner)
        await window.ShowDialog(owner);
    else
        await window.ShowDialog((Window)null!);

    if (window.SelectedCsv is not null)
        vm.SetAbilityRegistryIdsFromPicker(window.SelectedCsv, window.SelectedSelections);
}

private async void OpenAttackSpecialPickerClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm)
        return;

    var pickerVm = new CatalogPickerWindowViewModel(
        vm.GetWeaponSpecialCatalogOptions(),
        currentValue: null,
        allowMultiple: true,
        titleText: "Pick Weapon Specials",
        descriptionText: "Select registry IDs or macro invocations. WCC keeps the chosen source representation when writing the attack.",
        currentSelections: vm.GetCurrentAttackSpecialCatalogSelections());

    var window = new CatalogPickerWindow(pickerVm);

    if (TopLevel.GetTopLevel(this) is Window owner)
        await window.ShowDialog(owner);
    else
        await window.ShowDialog((Window?)null);

    if (window.WasAccepted)
        vm.SetSelectedAttackSpecialApplicationsFromPicker(window.SelectedSelections);
}

private async void OpenAttackSpecialRegistryPickerClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm)
        return;

    var pickerVm = new CatalogPickerWindowViewModel(
        vm.GetWeaponSpecialRegistryCatalogOptions(),
        vm.GetCurrentAttackSpecialRegistryIdsCsv(),
        allowMultiple: true,
        titleText: "Pick Wesnoth 1.20 Weapon Specials",
        descriptionText: "Select registry ids written to specials_list on this attack. Unknown existing ids remain available.",
        currentLibraryEntityIds: vm.GetCurrentAttackSpecialIdBindingEntityIds());

    var window = new CatalogPickerWindow(pickerVm);
    if (TopLevel.GetTopLevel(this) is Window owner)
        await window.ShowDialog(owner);
    else
        await window.ShowDialog((Window)null!);

    if (window.SelectedCsv is not null)
        vm.SetSelectedAttackSpecialRegistryIdsFromPicker(
            window.SelectedCsv,
            window.SelectedSelections);
}
private AppShellViewModel? TryGetShellViewModel()
{
    if (TopLevel.GetTopLevel(this) is Window owner)
        return owner.DataContext as AppShellViewModel;

    return null;
}

private void OpenRacePresetManagerClicked(object? sender, RoutedEventArgs e)
{
    var shell = TryGetShellViewModel();
    if (shell is null)
        return;

    shell.EnsureDirectPresetCatalogsReady();

    if (TopLevel.GetTopLevel(this) is Window owner)
    {
        var window = new PresetManagerWindow(shell, new RacePresetManagerAdapter(DataContext as MainWindowViewModel));
        window.Show(owner);
    }
}

private void OpenAbilityPresetManagerClicked(object? sender, RoutedEventArgs e)
{
    var shell = TryGetShellViewModel();
    if (shell is null)
        return;

    shell.EnsureDirectPresetCatalogsReady();

    if (TopLevel.GetTopLevel(this) is Window owner)
    {
        var window = new PresetManagerWindow(shell, new AbilityPresetManagerAdapter(DataContext as MainWindowViewModel));
        window.Show(owner);
    }
}
private void OpenMovementTypePresetManagerClicked(object? sender, RoutedEventArgs e)
{
    var shell = TryGetShellViewModel();
    if (shell is null)
        return;

    shell.EnsureDirectPresetCatalogsReady();

    if (TopLevel.GetTopLevel(this) is Window owner)
    {
        var window = new PresetManagerWindow(shell, new MovementTypePresetManagerAdapter(DataContext as MainWindowViewModel));
        window.Show(owner);
    }
}

private void OpenWeaponSpecialPresetManagerClicked(object? sender, RoutedEventArgs e)
{
    var shell = TryGetShellViewModel();
    if (shell is null)
        return;

    shell.EnsureDirectPresetCatalogsReady();

    if (TopLevel.GetTopLevel(this) is Window owner)
    {
        var window = new PresetManagerWindow(shell, new WeaponSpecialPresetManagerAdapter(DataContext as MainWindowViewModel));
        window.Show(owner);
    }
}
private void AddAttackClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm)
        vm.AddNewAttack();
}

private void RemoveSelectedAttackClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm)
        vm.RemoveSelectedAttack();
}

private void EditSelectedAttackRawClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm)
        return;

    var text = vm.GetSelectedAttackRawText();
    if (text is null)
        return;

    var isCurrent = vm.CaptureRawEditorApplyGuard();
    var window = new RawCodeEditorWindow(
        title: "Raw code - selected attack",
        initialText: text,
        onSave: MainWindowViewModel.GuardRawEditorApply(
            () => ReferenceEquals(DataContext, vm) && isCurrent(), text,
            vm.ApplySelectedAttackRawText));

    if (TopLevel.GetTopLevel(this) is Window owner)
        window.Show(owner);
    else
        window.Show();
}

private void AddAnimationClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm)
        vm.AddNewAnimation();
}

private void RemoveSelectedAnimationClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm)
        vm.RemoveSelectedAnimation();
}

private void EditSelectedAnimationRawClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm)
        return;

    var text = vm.GetSelectedAnimationRawText();
    if (text is null)
        return;

    var isCurrent = vm.CaptureRawEditorApplyGuard();
    var window = new RawCodeEditorWindow(
        title: "Raw code - selected animation",
        initialText: text,
        onSave: MainWindowViewModel.GuardRawEditorApply(
            () => ReferenceEquals(DataContext, vm) && isCurrent(), text,
            vm.ApplySelectedAnimationRawText));

    if (TopLevel.GetTopLevel(this) is Window owner)
        window.Show(owner);
    else
        window.Show();
}

private void ToggleAnimationPreviewPlaybackClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm)
        vm.ToggleAnimationPreviewPlayback();
}

private void RestartAnimationPreviewClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm)
        vm.RestartAnimationPreview();
}

private void PreviousAnimationPreviewFrameClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm)
        vm.StepAnimationPreviewPrevious();
}

private void NextAnimationPreviewFrameClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm)
        vm.StepAnimationPreviewNext();
}

private void EditSelectedGenderVariantRawClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm)
        return;

    var text = vm.GetSelectedGenderVariantRawText();
    if (text is null)
        return;

    var isCurrent = vm.CaptureRawEditorApplyGuard();
    var window = new RawCodeEditorWindow(
        title: "Raw code - selected gender variant",
        initialText: text,
        onSave: MainWindowViewModel.GuardRawEditorApply(
            () => ReferenceEquals(DataContext, vm) && isCurrent(), text,
            vm.ApplySelectedGenderVariantRawText));

    if (TopLevel.GetTopLevel(this) is Window owner)
        window.Show(owner);
    else
        window.Show();
}

private void RefreshVisualPreviewsClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is MainWindowViewModel vm)
        vm.RefreshVisualPreviews();
}

private async void PickAttackIconClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
    await vm.PickAttackIconAsync(async picker =>
    {
        var window = new CatalogPickerWindow(picker);
        await window.ShowDialog(owner);
        return window.SelectedSelections.Count == 1 ? window.SelectedSelections[0] : null;
    }, () => ReferenceEquals(DataContext, vm));
}

private async void UploadAttackIconClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
    var request = vm.CaptureAttackIconImportRequest(out var problem, () => ReferenceEquals(DataContext, vm));
    if (request is null) { vm.Status = problem; return; }
    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
    {
        Title = "Upload attack icon", AllowMultiple = false,
        FileTypeFilter = [new FilePickerFileType("Wesnoth images") { Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg"] }]
    });
    if (files.Count != 0) await vm.ImportVisualAssetAsync(request, files[0].Path.LocalPath);
}

private async void PickVisualAssetClicked(object? sender, RoutedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm ||
        sender is not Button { Tag: string fieldKey })
    {
        return;
    }

    var request = vm.CaptureVisualAssetImportRequest(fieldKey, out var problem,
        () => ReferenceEquals(DataContext, vm));
    if (request is null)
    {
        vm.Status = problem;
        return;
    }

    var top = TopLevel.GetTopLevel(this);
    if (top is null)
        return;

    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
    {
        Title = fieldKey == "image" ? "Choose Unit Image" : "Choose Unit Portrait",
        AllowMultiple = false,
        FileTypeFilter =
        [
            new FilePickerFileType("Wesnoth images")
            {
                Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg"]
            }
        ]
    });
    var file = files.Count > 0 ? files[0] : null;
    if (file is null)
        return;

    await vm.ImportVisualAssetAsync(request, file.Path.LocalPath);
}
}
