using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.Views.Controls;

public partial class WccSideFlagPresetView : UserControl
{
    private ScenarioWindowViewModel? _editor;

    public WccSideFlagPresetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindEditor();
        AttachedToVisualTree += (_, _) => BindEditor();
        DetachedFromVisualTree += (_, _) => UnbindEditor();
    }

    private void UnbindEditor()
    {
        if (_editor is not null) _editor.PropertyChanged -= EditorChanged;
        _editor = null;
    }

    private void BindEditor()
    {
        UnbindEditor();
        _editor = DataContext as ScenarioWindowViewModel;
        if (_editor is not null) _editor.PropertyChanged += EditorChanged;
        RefreshPreview();
    }

    private void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScenarioWindowViewModel.WccSideFlagPresetInvocation) or
            nameof(ScenarioWindowViewModel.WccSideFieldGroups)) RefreshPreview();
    }

    private void RefreshPreview()
    {
        var row = _editor?.WccSideFieldEditors.FirstOrDefault(field => field.Key == "flag_icon");
        if (this.FindControl<WccFieldImagePreviewView>("FlagPreview") is { } preview)
            preview.DataContext = row?.ImagePreview;
    }

    private async void ChooseFlagClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScenarioWindowViewModel editor || !editor.CanPickWccSideFlagPreset) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var host = owner.DataContext as AppShellViewModel;
        if (host?.GetFlagPresetStageBlockReason(editor) is { } pendingProblem)
        { host.Status = pendingProblem; return; }
        var opening = editor.BeginWccSideFlagPresetSelection();
        if (opening is null) return;
        string? HostProblem()
        {
            if (!ReferenceEquals(editor, DataContext) || !ReferenceEquals(host, owner.DataContext as AppShellViewModel))
                return "The editor host changed. Reopen the flag picker.";
            return host?.GetFlagPresetStageBlockReason(editor) ?? editor.ValidateWccSideFlagPresetSelection(opening);
        }
        if (HostProblem() is { } openingProblem)
        { if (host is not null) host.Status = openingProblem; return; }
        var picker = new CatalogPickerWindow(new CatalogPickerWindowViewModel(
            editor.GetWccSideFlagPresetOptions(), editor.WccSideFlagPresetInvocation, false,
            "Choose flag preset", "Core and current campaign image families. The macro controls flag animation and icon together. Your choice is staged alongside other drafts for Save."));
        await picker.ShowDialog(owner);
        if (HostProblem() is { } returnedProblem)
        { if (host is not null) host.Status = returnedProblem; return; }
        if (!picker.WasAccepted || picker.SelectedSelections.SingleOrDefault() is not { } selection) return;
        if (host is null) editor.StageWccSideFlagPreset(opening, selection);
        else host.StageFlagPreset(editor, opening, selection);
    }
}
