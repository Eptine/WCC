using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Wml;
using Wcc.Core.Maps;

namespace Wcc.App.Views.Controls;

public partial class WccScenarioEventWorkspaceView : UserControl
{
    private WccScenarioEventWorkspaceVm? _workspace;
    private long _selectionScrollVersion;

    public WccScenarioEventWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindWorkspace();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        BindWorkspace();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        _selectionScrollVersion++;
        _workspace?.Dispose();
        _workspace = null;
        WorkspaceContent.DataContext = null;
        base.OnDetachedFromVisualTree(args);
    }

    private void BindWorkspace()
    {
        if (DataContext is ScenarioWindowViewModel editor && ReferenceEquals(_workspace?.Editor, editor)) return;
        _selectionScrollVersion++;
        _workspace?.Dispose();
        _workspace = DataContext is ScenarioWindowViewModel vm ? new WccScenarioEventWorkspaceVm(vm) : null;
        WorkspaceContent.DataContext = _workspace;
    }

    private async void ApplyEventPresetClicked(object? sender, RoutedEventArgs args)
    {
        if (_workspace is not { } workspace || TopLevel.GetTopLevel(this) is not Window owner) return;
        await workspace.Editor.PickWccEventPresetAsync(async model =>
        {
            var picker = new WccCampaignPresetPickerWindow(model);
            await picker.ShowDialog(owner);
            return picker.WasAccepted ? model.GetPresetSelection() : null;
        });
    }

    private async void ManageEventPresetsClicked(object? sender, RoutedEventArgs args)
    {
        if (_workspace?.Editor.CreateWccEventPresetManager() is not { } manager) return;
        using (manager)
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
                await new CampaignLocalPresetManagerWindow(manager).ShowDialog(owner);
        }
    }
    private async void AddEventClicked(object? sender, RoutedEventArgs args)
    {
        if (_workspace is not { } workspace || workspace.Editor.BeginWccNewEventDraft() is not { CanEdit: true } draft) return;
        await OpenDraftAsync(workspace.Editor, draft, "Add event RAW", "Add one local [event] block. It joins the collected changes; the shared Save writes the file.");
    }

    private async void AddDialogueGraphClicked(object? sender, RoutedEventArgs args)
    {
        if (_workspace?.BeginNewDialogue() is not { } draft) return;
        await OpenDialogueGraphAsync(_workspace.Editor, draft);
    }

    private async void EditDialogueGraphClicked(object? sender, RoutedEventArgs args)
    {
        if (_workspace?.BeginSelectedDialogue() is not { } draft) return;
        await OpenDialogueGraphAsync(_workspace.Editor, draft);
    }

    private async Task OpenDialogueGraphAsync(ScenarioWindowViewModel editor, WccEventDialogueGraphDraft draft)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            using var model = new EventNodeGraphWindowViewModel(draft.Graph, draft.SpeakerIds,
                () => draft.IsCurrent, draft.Validate, draft.Seed, declaredDifficultyDefines: draft.DeclaredDifficultyDefines, knownUnitPresets: draft.UnitPresets);
            var window = new EventNodeGraphWindow(model);
            EventGraphCoordinatePicker.Attach(model, window, () => editor.MapPreview, () => draft.IsCurrent);
            EventGraphUnitPicker.Attach(model, window, draft.BeginSpawnUnitEditor, () => draft.IsCurrent, () => editor.MapPreview);
            await window.ShowDialog(owner);
            if (window.ResultWml is { } text && !draft.TryStage(text))
                editor.Status = draft.Validate(text) ?? editor.Status ?? "The event changed before it could be queued. Reopen its editor.";
        }
        finally { draft.Cancel(); }
    }

    private async void EditEventClicked(object? sender, RoutedEventArgs args)
    {
        if (_workspace is not { SelectedRow: { } row } workspace) return;
        var draft = workspace.BeginSelectedDraft();
        if (draft is { CanEdit: true })
            await OpenDraftAsync(workspace.Editor, draft,
                draft.IsMacroGroup ? "Edit event macro invocation RAW" : "Edit event RAW",
                draft.IsMacroGroup
                    ? "Edit this one local macro invocation. It changes the whole event group, keeps the shared definition unchanged, and joins the global Save. Save checks its event count in every context."
                    : "Edit this local event. The captured source is checked again when staging and on the shared Save.");
        else
        {
            var text = draft?.Text ?? row.Entry.EffectiveRawWml;
            var window = new RawCodeEditorWindow(draft?.IsMacroGroup == true ? "View event macro invocation" : "View event RAW",
                text, isReadOnly: true,
                descriptionText: draft?.IsMacroGroup == true
                    ? "This whole source invocation is read-only in the current source context. Its expanded output and shared definition are not editable here."
                    : row.Editability);
            await ConfigureReferencesAsync(window, text, draft?.Target);
            await ShowAsync(window);
        }
    }

    private void RemoveEventClicked(object? sender, RoutedEventArgs args)
    {
        if (_workspace is not { } workspace || sender is not Control { DataContext: WccScenarioEventRowVm row }) return;
        workspace.SelectedRow = row;
        if (workspace.BeginDraft(row) is not { CanRemove: true } draft) return;
        workspace.Editor.RemoveWccEventDraft(draft);
    }

    private void EventSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        var version = ++_selectionScrollVersion;
        if (_workspace is not { } workspace || EventsList.SelectedItem is not WccScenarioEventRowVm row) return;
        var map = workspace.Map;
        // Selection can change while ItemsSource/DataContext and expanded row sizes are
        // being rebound. Realize the current row only after that layout work settles.
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _selectionScrollVersion || !ReferenceEquals(_workspace, workspace) ||
                !ReferenceEquals(workspace.Map, map) || !ReferenceEquals(WorkspaceContent.DataContext, workspace) ||
                !ReferenceEquals(EventsList.SelectedItem, row) || !ReferenceEquals(workspace.SelectedRow, row) ||
                !workspace.Rows.Any(current => ReferenceEquals(current, row)) || this.GetVisualRoot() is null || !EventsList.IsEffectivelyVisible) return;
            EventsList.ScrollIntoView(row);
        }, DispatcherPriority.Loaded);
    }

    private async Task OpenDraftAsync(ScenarioWindowViewModel editor, WccEventEditDraft draft, string title, string description)
    {
        var window = new ValidatedRawCodeEditorWindow(title, draft.Text, description,
            text => editor.ValidateWccEventDraft(draft, text),
            text => { if (!editor.StageWccEventDraft(draft, text)) throw new InvalidOperationException(editor.Status); });
        await ConfigureReferencesAsync(window, draft.Text, draft.Target);
        await ShowAsync(window);
    }

    private async Task ConfigureReferencesAsync(Window window, string text, WmlStaticSourceRange? origin)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is not AppShellViewModel shell) return;
        try
        {
            var context = await shell.BuildRawEditorReferenceContextAsync(text, origin?.Source.Path, origin);
            if (window is ValidatedRawCodeEditorWindow edit) edit.ReferenceContext = context;
            else if (window is RawCodeEditorWindow read) read.ReferenceContext = context;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            shell.Status = $"Reference source preview unavailable: {failure.Message}";
        }
    }

    private async Task ShowAsync(Window window)
    {
        if (TopLevel.GetTopLevel(this) is Window owner) await window.ShowDialog(owner);
    }
}
