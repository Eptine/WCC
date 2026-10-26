using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class PresetManagerView : UserControl
{
    private long _duplicateHostEpoch;
    public PresetManagerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => _duplicateHostEpoch++;
    }

    private void NewClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PresetManagerWindowViewModel vm)
            return;

        ShowEditor(new RawCodeEditorWindow(
            "Create WCC Entry",
            vm.BuildNewTemplate(),
            vm.SaveNew,
            isReadOnly: false,
            descriptionText: "Edit the WML below. This creates a new entry in the WCC managed package."));
    }

    private async void DuplicateClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PresetManagerWindowViewModel vm)
        {
            return;
        }

        var hostEpoch = _duplicateHostEpoch;
        var request = await vm.CreateDuplicateRequestAsync();
        if (request is null || hostEpoch != _duplicateHostEpoch || !ReferenceEquals(DataContext, vm))
            return;

        if (!request.CopiesManagedMacro)
        {
            ShowEditor(new RawCodeEditorWindow("Clone to WCC", request.InitialText, request.LegacySave,
                isReadOnly: false, descriptionText: "This creates an editable WCC-owned copy; the source package remains unchanged."));
            return;
        }

        RawCodeEditorWindow? editor = null;
        long draftEpoch = 0;
        var closed = false;
        editor = new RawCodeEditorWindow(
            "Clone to WCC",
            request.InitialText,
            isReadOnly: false,
            descriptionText: "Create an editable WCC-owned copy with its required helpers and resources.",
            onSaveAsync: text =>
            {
                var epoch = draftEpoch;
                return request.SaveAsync(text, () => !closed && hostEpoch == _duplicateHostEpoch && ReferenceEquals(DataContext, vm)
                    && epoch == draftEpoch && editor?.DataContext is WmlEditorDocumentViewModel document && document.Text == text);
            });
        var document = (WmlEditorDocumentViewModel)editor.DataContext!;
        void DraftChanged(object? source, PropertyChangedEventArgs args)
        { if (args.PropertyName == nameof(WmlEditorDocumentViewModel.Text)) draftEpoch++; }
        document.PropertyChanged += DraftChanged;
        editor.Closed += (_, _) => { closed = true; document.PropertyChanged -= DraftChanged; };
        ShowEditor(editor);
    }

    private async void ViewOrEditClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PresetManagerWindowViewModel vm ||
            vm.SelectedEntry is not { } selected ||
            !await vm.EnsureSelectedRawTextAsync() ||
            !ReferenceEquals(vm.SelectedEntry, selected))
        {
            return;
        }

        var isReadOnly = !selected.CanEdit;
        ShowEditor(new RawCodeEditorWindow(
            isReadOnly ? "View Package Entry" : "Edit Package Entry",
            selected.RawText,
            isReadOnly ? null : text => vm.SaveEntry(selected, text),
            isReadOnly,
            isReadOnly
                ? "This package entry is read-only here. Clone it to WCC or explicitly open its package for editing."
                : "Edit the definition WML below. Changes are saved to its owning editable package when this window closes."));
    }

    private void DeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PresetManagerWindowViewModel vm)
            vm.DeleteSelected();
    }

    private void ShowEditor(Window editor)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            editor.Show(owner);
        else
            editor.Show();
    }
}
