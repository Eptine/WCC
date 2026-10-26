using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Views;
using Avalonia.Input;
using Wcc.Core.Scenarios;

namespace Wcc.App.Views.Controls;

public partial class ScenarioEditorView : UserControl
{
    // Standalone views keep local progress; the shell supplies one shared overlay.
    public static readonly StyledProperty<bool> ShowLoadingIndicatorsProperty =
        AvaloniaProperty.Register<ScenarioEditorView, bool>(nameof(ShowLoadingIndicators), true);

    public bool ShowLoadingIndicators
    {
        get => GetValue(ShowLoadingIndicatorsProperty);
        set => SetValue(ShowLoadingIndicatorsProperty, value);
    }

    public static readonly StyledProperty<bool> ShowNavigationPaneProperty =
        AvaloniaProperty.Register<ScenarioEditorView, bool>(nameof(ShowNavigationPane), true);

    public static readonly StyledProperty<bool> ShowFileButtonsProperty =
        AvaloniaProperty.Register<ScenarioEditorView, bool>(nameof(ShowFileButtons), true);

    public static readonly StyledProperty<bool> ShowOpenFileButtonProperty =
        AvaloniaProperty.Register<ScenarioEditorView, bool>(nameof(ShowOpenFileButton), true);

    public static readonly StyledProperty<bool> ShowSaveAsFileButtonProperty =
        AvaloniaProperty.Register<ScenarioEditorView, bool>(nameof(ShowSaveAsFileButton), true);

    public static readonly StyledProperty<bool> ShowEditorChromeProperty =
        AvaloniaProperty.Register<ScenarioEditorView, bool>(nameof(ShowEditorChrome), true);

    public bool ShowNavigationPane
    {
        get => GetValue(ShowNavigationPaneProperty);
        set => SetValue(ShowNavigationPaneProperty, value);
    }

    public bool ShowFileButtons
    {
        get => GetValue(ShowFileButtonsProperty);
        set => SetValue(ShowFileButtonsProperty, value);
    }

    public bool ShowOpenFileButton
    {
        get => GetValue(ShowOpenFileButtonProperty);
        set => SetValue(ShowOpenFileButtonProperty, value);
    }

    public bool ShowSaveAsFileButton
    {
        get => GetValue(ShowSaveAsFileButtonProperty);
        set => SetValue(ShowSaveAsFileButtonProperty, value);
    }

    public bool ShowEditorChrome
    {
        get => GetValue(ShowEditorChromeProperty);
        set => SetValue(ShowEditorChromeProperty, value);
    }

    public ScenarioEditorView()
    {
        InitializeComponent();
    }

    private async void ScenarioFieldVisibilityClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is ScenarioWindowViewModel vm && TopLevel.GetTopLevel(this) is Window owner)
            await new WccFieldVisibilityWindow(vm.CreateWccFieldVisibilityDialog(ScenarioInitialFieldOwnerScope.Scenario)).ShowDialog(owner);
    }

    private async void SideFieldVisibilityClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is ScenarioWindowViewModel vm && TopLevel.GetTopLevel(this) is Window owner)
            await new WccFieldVisibilityWindow(vm.CreateWccFieldVisibilityDialog(ScenarioInitialFieldOwnerScope.Side)).ShowDialog(owner);
    }

    private async void EditSidesClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is ScenarioWindowViewModel vm && TopLevel.GetTopLevel(this) is Window owner &&
            vm.BeginWccSideTopologyManagement() is { } management)
            await new WccSideManagementWindow(management).ShowDialog(owner);
    }

    private async void AddLeaderPresetClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ScenarioWindowViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        await vm.PickWccLeaderPresetAsync(async picker =>
        {
            var window = new WccCampaignPresetPickerWindow(picker); await window.ShowDialog(owner);
            return window.WasAccepted ? picker.GetPresetSelection() : null;
        });
    }

    private void ScenarioRailTabClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm &&
            sender is Button { DataContext: ScenarioEditorNavigationTabVm tab })
            vm.SelectedScenarioUiTab = tab;
    }

    private async void OpenClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm)
            return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Wesnoth scenario .cfg",
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

        vm.LoadFromText(path, text);
    }

    private async void SaveAsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm)
            return;

        if (vm.HasWccInlineDrafts || vm.IsWccInlineSaveBusy || vm.HasWccMusicScheduleDrafts ||
            vm.HasWccSideFlagDrafts || vm.HasWccEventDrafts || vm.HasWccSideTopologyDrafts || vm.HasWccLeaderAdditionDrafts || vm.HasWccStoryDraft)
        {
            vm.Status = "Save or discard the inline field drafts before exporting a copy.";
            return;
        }

        var patched = vm.RenderPatchedFile();
        if (patched is null)
        {
            vm.Status ??= "Nothing to save.";
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var suggested = vm.LoadedPath is not null
            ? Path.GetFileNameWithoutExtension(vm.LoadedPath) + ".patched.cfg"
            : "scenario.patched.cfg";

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save patched scenario cfg",
            SuggestedFileName = suggested,
            FileTypeChoices =
            [
                new FilePickerFileType("Wesnoth cfg") { Patterns = ["*.cfg"] }
            ]
        });

        if (file is null) return;

        if (!vm.CanSaveScenarioCopyTo(file.Path.LocalPath) ||
            (top.DataContext is AppShellViewModel shell && !shell.CanSaveScenarioCopyTo(file.Path.LocalPath)))
            return;
        try
        {
            // Save As exports to a new entry; never truncate a current source,
            // protected original or hard-linked existing destination.
            await using var output = new FileStream(file.Path.LocalPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(output);
            await writer.WriteAsync(patched);
            vm.Status = "Saved patched scenario copy.";
        }
        catch (System.Exception ex) when (ex is IOException or System.UnauthorizedAccessException)
        {
            vm.Status = "Could not save the scenario copy: " + ex.Message;
        }
    }

    private async void SaveWccInlineDraftsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.DataContext is AppShellViewModel shell)
                await shell.SavePendingEditorChangesAsync();
            else
                await vm.SaveWccInlineDraftsAsync();
        }
        catch (System.Exception exception)
        {
            vm.Status = "The collected save could not finish: " + exception.Message;
        }
    }

    private void DiscardWccInlineDraftsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm) vm.DiscardWccInlineDrafts();
    }

    private void RevertWccInlineDraftClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm && sender is Button { DataContext: WccScenarioInlineDraft draft })
            vm.RevertWccInlineDraft(draft);
    }

    private void RevertWccInlineFieldClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm && sender is Button { Tag: string key })
            vm.RevertWccInlineField(key);
    }

    private void RemoveWccInlineOverrideClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm && sender is Button { Tag: string key })
            vm.RemoveWccInlineOverride(key);
    }

    private async void OpenSelectedSideRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm ||
            vm.GetSelectedSideRawWml() is not { } rawWml)
        {
            return;
        }

        Window window = vm.CanEditSelectedSideRaw
            ? new ValidatedRawCodeEditorWindow(
                "Edit side WML",
                rawWml,
                "Edit exactly one authoritative [side] block. Nested leaders and unsupported [ai], [unit], [village], [variables], macros, and comments remain in this source.",
                vm.ValidateSelectedSideRawWml,
                vm.ApplySelectedSideRawWml)
            : new RawCodeEditorWindow(
                "View side WML",
                rawWml,
                isReadOnly: true,
                descriptionText: vm.SelectedSide?.IsGenerated == true
                    ? "WCC resolved attributes and child blocks, reconstructed for inspection. This read-only projection is not authored WML and is never saved. Use View side source for the captured invocation."
                    : "Conditional or read-only side source is shown without rewriting its surrounding context.");
        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    private void AddLeaderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            vm.AddLeader();
    }

    private void RemoveLeaderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            vm.RemoveSelectedLeader();
    }

    private void MoveLeaderUpClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            vm.MoveSelectedLeader(-1);
    }

    private void MoveLeaderDownClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            vm.MoveSelectedLeader(1);
    }

    private async void OpenSelectedLeaderRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm ||
            vm.GetSelectedLeaderRawWml() is not { } rawWml)
        {
            return;
        }

        Window window = vm.CanEditSelectedLeaderRaw
            ? new ValidatedRawCodeEditorWindow(
                "Edit nested leader WML",
                rawWml,
                "Edit exactly one authoritative [leader] block. Unknown unit fields and nested children are preserved.",
                vm.ValidateSelectedLeaderRawWml,
                vm.ApplySelectedLeaderRawWml)
            : new RawCodeEditorWindow(
                "View leader source",
                rawWml,
                isReadOnly: true,
                descriptionText: vm.SelectedLeader?.IsLegacyInline == true
                    ? "This leader is represented by fields on its containing [side]. Use the typed fields or their ? raw editors; WCC will not silently convert it to [leader]."
                    : "Conditional leader source is shown read-only rather than rewritten outside its preprocessor context.");

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    private void ReloadMapClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            vm.RefreshMapPreview();
    }

    private async void PickSideCoordinateClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            await vm.PickSelectedSideCoordinateAsync();
    }

    private void ConfirmCoordinateSelectionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            vm.MapPreview?.ConfirmCoordinateSelection();
    }

    private void CancelCoordinateSelectionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            vm.MapPreview?.CancelCoordinateSelection();
    }

    private async void OpenEventScenarioSourceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            await WccScenarioContentView.OpenScenarioRawAsync(this, vm);
    }

    private async void OpenSelectedEventRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm ||
            vm.GetSelectedEventRawWml() is not { } rawWml)
        {
            return;
        }

        Window window = vm.CanEditSelectedEventRaw
            ? new ValidatedRawCodeEditorWindow(
                $"Edit event - {vm.MapPreview?.SelectedEvent?.TriggerDisplay}",
                rawWml,
                "Edit exactly one authoritative [event] block. Macro-generated and nested effective events remain read-only.",
                vm.ValidateSelectedEventRawWml,
                vm.ApplySelectedEventRawWml)
            : new RawCodeEditorWindow(
                $"View event - {vm.MapPreview?.SelectedEvent?.TriggerDisplay}",
                rawWml,
                isReadOnly: true,
                descriptionText: vm.SelectedEventEditabilityText ?? "Read-only effective event WML.");

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    private async void AddRawEventClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm ||
            vm.GetNewEventRawTemplate() is not { } template)
        {
            return;
        }

        var window = new ValidatedRawCodeEditorWindow(
            "Add raw scenario event",
            template,
            "Add exactly one direct [event] block. A non-empty direct name= trigger is required.",
            vm.ValidateNewEventRawWml,
            vm.AddRawEvent);
        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    private async void EditIntroClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm)
            return;

        var closed = false;
        var hostEpoch = 0L;
        EventHandler hostChanged = (_, _) => hostEpoch++;
        StoryEditorWindow? editor = null;
        StoryEditorWindowViewModel? storyVm = null;
        DataContextChanged += hostChanged;
        try
        {
            using var session = vm.BeginWccStoryEditingSession(() => !closed && hostEpoch == 0 && ReferenceEquals(DataContext, vm) &&
                (editor is null || ReferenceEquals(editor.DataContext, storyVm)));
            if (session is null)
                return;
            storyVm = new StoryEditorWindowViewModel(session);
            editor = new StoryEditorWindow { DataContext = storyVm };
            editor.DataContextChanged += hostChanged;
            editor.Closed += (_, _) => closed = true;
            if (TopLevel.GetTopLevel(this) is not Window owner)
            { vm.Status = "Open the Story editor from its scenario window."; return; }
            await editor.ShowDialog(owner);
        }
        finally
        {
            closed = true;
            DataContextChanged -= hostChanged;
            if (editor is not null) editor.DataContextChanged -= hostChanged;
            storyVm?.Dispose();
        }
    }

    private async void OpenScalarSourceBreadcrumbClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm ||
            sender is not Button { Tag: ScenarioScalarSourceBreadcrumbVm breadcrumb } ||
            !vm.IsCurrentScalarBreadcrumb(breadcrumb))
            return;

        var window = new RawCodeEditorWindow(
            title: $"Rendered authored block - {breadcrumb.ValueLabel}",
            initialText: breadcrumb.AuthoredScenarioWml,
            isReadOnly: true,
            descriptionText:
                $"{breadcrumb.LocationLabel}. {breadcrumb.Explanation} " +
                "This is a read-only navigation breadcrumb in rendered authored WML, not Wesnoth's normalized output.",
            initialSelectionStart: breadcrumb.SelectionStart,
            initialSelectionLength: breadcrumb.SelectionLength);

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    private async void OpenSelectedSideSourceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScenarioWindowViewModel vm)
            await OpenSelectedSideSourceAsync(vm);
    }

    private async Task OpenSelectedSideSourceAsync(ScenarioWindowViewModel vm)
    {
        if (vm.GetSelectedSideSourceLocation() is not { } location)
            return;
        var window = new RawCodeEditorWindow("Side source - " + location.SourcePath,
            location.SourceText, isReadOnly: true, descriptionText: location.Description,
            initialSelectionStart: location.SelectionStart, initialSelectionLength: location.SelectionLength);
        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }
    private async void OpenInitialScalarSourceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm || sender is not Button { Tag: string key })
            return;
        if (key is "side" or "gold" or "income")
        {
            if (vm.SelectedSide?.IsGenerated == true)
            {
                await OpenSelectedSideSourceAsync(vm);
                return;
            }
            var hasFieldEditor = key switch
            {
                "side" => vm.SideNumberHasRawEditor,
                "gold" => vm.SideGoldHasRawEditor,
                _ => vm.SideIncomeHasRawEditor
            };
            if (hasFieldEditor)
                OpenSideFieldRawClicked(sender, e);
            else
                OpenSelectedSideRawClicked(sender, e);
            return;
        }
        if (key != "turns" || vm.SelectedScenario is null)
            return;
        if (vm.ScenarioTurnsHasRawEditor)
        {
            OpenScenarioFieldRawClicked(sender, e);
            return;
        }
        // No whole-scenario replacement authority is inferred from a resolved
        // scalar. Existing field-region and reviewed editors remain available.
        var document = vm.SelectedScenario.Document;
        var raw = document.EditableBlock.ToRawText();
        var source = vm.GetWccInitialFieldSourceLocation("turns");
        var window = new RawCodeEditorWindow("Scenario source for maximum turns",
            raw, isReadOnly: true,
            descriptionText: "Containing scenario source. Edit supported fields in the scenario or side settings, then use Save.")
        {
            InitialSourceSelection = WmlEditorInitialSelection.CapturedRange(raw, vm.CurrentScenarioAsset?.SourcePath ?? vm.LoadedPath ?? "",
                document.OriginalText, document.BlockSpan.StartIndex, source?.SourcePath, source?.SourceText,
                source?.SelectionStart ?? 0, source?.SelectionLength ?? 0)
        };
        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    private async void OpenWccInitialFieldSourceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm || sender is not Button { Tag: string key })
            return;
        if (vm.BeginWccInitialFieldRawEdit(key) is { } rawDraft)
        {
            var editor = new ValidatedRawCodeEditorWindow("Edit scenario source for " + key,
                rawDraft.Text,
                "Manual RAW fallback for the current authored scenario. Shared macro definitions and expanded previews are not edited. " +
                "Changes stay in the working draft until you save the scenario file. " + vm.GetWccInitialFieldEditStatus(key),
                raw => vm.ValidateWccInitialFieldRawEdit(rawDraft, raw),
                raw =>
                {
                    if (!vm.ApplyWccInitialFieldRawEdit(rawDraft, raw))
                        vm.Status = "The source or editing context changed. Reopen its RAW editor.";
                })
            {
                InitialSourceSelection = WmlEditorInitialSelection.CapturedRange(rawDraft.Text, rawDraft.SourcePath,
                    rawDraft.SourceText, rawDraft.Document.BlockSpan.StartIndex, rawDraft.SourceLocation?.SourcePath,
                    rawDraft.SourceLocation?.SourceText, rawDraft.SourceLocation?.SelectionStart ?? 0,
                    rawDraft.SourceLocation?.SelectionLength ?? 0)
            };
            if (TopLevel.GetTopLevel(this) is Window rawEditorOwner)
                await editor.ShowDialog(rawEditorOwner);
            else
                editor.Show();
            return;
        }
        if (vm.GetWccInitialFieldSourceLocation(key) is not { } location)
        {
            vm.Status = "No exact captured field source is available for inspection in this context.";
            return;
        }
        var window = new RawCodeEditorWindow("Field source - " + location.SourcePath,
            location.SourceText, isReadOnly: true, descriptionText: location.Description + " " + vm.GetWccInitialFieldEditStatus(key),
            initialSelectionStart: location.SelectionStart, initialSelectionLength: location.SelectionLength);
        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    private async void OpenScenarioFieldRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        var text = vm.GetScenarioFieldRawText(key);
        if (text is null)
            return;

        var window = new RawCodeEditorWindow(
            title: $"Raw code - scenario field '{key}'",
            initialText: text,
            onSave: edited => vm.ApplyScenarioFieldRawText(key, edited))
        { InitialSourceSelection = WmlEditorInitialSelection.FieldFragment(text, key) };

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    private async void OpenSideFieldRawClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        var text = vm.GetSideFieldRawText(key);
        if (text is null)
            return;

        var window = new RawCodeEditorWindow(
            title: $"Raw code - side field '{key}'",
            initialText: text,
            onSave: edited => vm.ApplySideFieldRawText(key, edited))
        { InitialSourceSelection = WmlEditorInitialSelection.FieldFragment(text, key) };

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }
    private void ScenarioFieldRawPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        ToolTip.SetTip(button, vm.GetScenarioFieldReason(key) ?? "This field is read-only in v0.1.");
    }

    private void SideFieldRawPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm)
            return;

        if (sender is not Button button || button.Tag is not string key)
            return;

        ToolTip.SetTip(button, vm.GetSideFieldReason(key) ?? "This field is read-only in v0.1.");
    }
    private async void OpenSideTypePickerClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm)
            return;

        var pickerVm = new CatalogPickerWindowViewModel(
            vm.GetUnitTypeCatalogOptions(),
            vm.SideType,
            allowMultiple: false,
            titleText: "Pick Leader Type",
            descriptionText: "Select one unit type for the side leader.",
            currentLibraryEntityIds: vm.GetCurrentSideTypeBindingEntityIds());

        var window = new CatalogPickerWindow(pickerVm);

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            await window.ShowDialog((Window?)null);

        if (window.SelectedCsv is not null)
            vm.SetSideTypeFromPicker(window.SelectedCsv, window.SelectedSelections);
    }

    private async void OpenSideRecruitPickerClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel vm)
            return;

        var pickerVm = new CatalogPickerWindowViewModel(
            vm.GetUnitTypeCatalogOptions(),
            vm.SideRecruit,
            allowMultiple: true,
            titleText: "Pick Recruit List",
            descriptionText: "Select one or more unit types for the recruit list.",
            currentLibraryEntityIds: vm.GetCurrentSideRecruitBindingEntityIds());

        var window = new CatalogPickerWindow(pickerVm);

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            await window.ShowDialog((Window?)null);

        if (window.SelectedCsv is not null)
            vm.SetSideRecruitFromPicker(window.SelectedCsv, window.SelectedSelections);
    }
}
