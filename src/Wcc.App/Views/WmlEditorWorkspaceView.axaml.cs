using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

/// <summary>Host-scoped tabs; inspection navigation never changes the root Apply/Close callback.</summary>
public partial class WmlEditorWorkspaceView : UserControl
{
    private sealed class WorkspaceSession(WmlEditorDocumentViewModel root)
    {
        public WmlEditorWorkspaceViewModel Workspace { get; } = new(root);
        public Dictionary<WmlEditorTabViewModel, WmlCodeEditorView> Panes { get; } = new();
        public WmlEditorReferenceContext? ReferenceContext { get; set; }
    }

    private readonly ConditionalWeakTable<WmlEditorDocumentViewModel, WorkspaceSession> _sessions = new();
    private Dictionary<WmlEditorTabViewModel, WmlCodeEditorView> _panes = new();
    private WorkspaceSession? _session;
    private WmlEditorWorkspaceViewModel? _workspace;
    private WmlEditorReferenceContext? _referenceContext;

    public WmlEditorWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindWorkspace();
        BindWorkspace();
    }

    internal WmlEditorWorkspaceViewModel? Workspace => _workspace;
    public WmlCodeEditorView RootCodeView => GetPane(_workspace!.RootTab);
    public Func<WmlEditorSourceTarget, WmlEditorDocumentViewModel, WmlEditorDocumentViewModel?>? SourceDocumentFactory { get; set; }

    public WmlEditorReferenceContext? ReferenceContext
    {
        get => _referenceContext;
        set
        {
            _referenceContext = value;
            if (_session is not null) _session.ReferenceContext = value;
            if (_workspace is not null) GetPane(_workspace.RootTab).ReferenceContext = value;
        }
    }

    private void BindWorkspace()
    {
        if (DataContext is WmlEditorDocumentViewModel document &&
            ReferenceEquals(_workspace?.RootDocument, document)) return;
        if (_workspace is not null) _workspace.PropertyChanged -= WorkspaceChanged;
        PaneHost.Content = null;
        foreach (var pane in _panes.Values) pane.SourceOpenRequested -= SourceOpenRequested;
        // Inherited DataContext may temporarily disappear when the host is detached. Keep the
        // workspace and its exact panes per document so returning does not lose drafts or undo.
        _session = DataContext is WmlEditorDocumentViewModel root
            ? _sessions.GetValue(root, document => new WorkspaceSession(document)) : null;
        _workspace = _session?.Workspace;
        _panes = _session?.Panes ?? new();
        _referenceContext = _session?.ReferenceContext;
        foreach (var pane in _panes.Values) pane.SourceOpenRequested += SourceOpenRequested;
        WorkspaceContent.DataContext = _workspace;
        if (_workspace is not null)
        {
            _workspace.PropertyChanged += WorkspaceChanged;
            ShowSelectedPane();
        }
    }

    private WmlCodeEditorView GetPane(WmlEditorTabViewModel tab)
    {
        if (!_panes.TryGetValue(tab, out var pane))
        {
            pane = new WmlCodeEditorView { DataContext = tab.Document,
                ReferenceContext = tab.IsRoot ? _referenceContext : null };
            pane.SourceOpenRequested += SourceOpenRequested;
            _panes.Add(tab, pane);
        }
        return pane;
    }

    private void ShowSelectedPane()
    {
        if (_workspace is not null) PaneHost.Content = GetPane(_workspace.SelectedTab);
    }

    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WmlEditorWorkspaceViewModel.SelectedTab)) ShowSelectedPane();
    }

    private void TabSelectionChanged(object? sender, SelectionChangedEventArgs args) => ShowSelectedPane();

    private void CloseTabClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Control { DataContext: WmlEditorTabViewModel tab } || _workspace is null) return;
        CloseSourceTab(tab);
        args.Handled = true;
    }

    internal bool CloseSourceTab(WmlEditorTabViewModel tab)
    {
        if (_workspace is null || tab.IsRoot || !_workspace.Tabs.Contains(tab)) return false;
        if (tab.Document.IsModified && !tab.Document.TryClose())
        { _workspace.SelectedTab = tab; return false; }
        if (!_workspace.CloseTab(tab)) return false;
        if (_panes.Remove(tab, out var pane)) pane.SourceOpenRequested -= SourceOpenRequested;
        WorkspaceMessageText.Text = null;
        return true;
    }

    private void ApplySourceClicked(object? sender, RoutedEventArgs args)
    {
        if (_workspace?.SelectedTab is not { IsRoot: false, IsReadOnly: false } tab) return;
        if (tab.Document.TryApply()) CloseSourceTab(tab);
    }

    private void DiscardSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (_workspace?.SelectedTab is not { IsRoot: false } tab) return;
        tab.Document.Cancel();
        CloseSourceTab(tab);
    }

    public void DiscardSourceEdits()
    {
        if (_workspace is null) return;
        foreach (var tab in _workspace.Tabs.Where(tab => !tab.IsRoot)) tab.Document.Cancel();
    }

    public bool RequireAppliedSourceEdits()
    {
        if (_workspace?.Tabs.FirstOrDefault(tab => !tab.IsRoot && tab.Document.IsModified) is not { } dirty) return true;
        _workspace.SelectedTab = dirty;
        WorkspaceMessageText.Text = "Apply or discard this definition's edits before applying the original draft.";
        return false;
    }

    public bool TryCloseSourceEdits()
    {
        if (_workspace is not { } workspace) return true;
        foreach (var tab in workspace.Tabs.Where(tab => !tab.IsRoot && tab.Document.IsModified).ToArray())
        {
            if (!tab.Document.TryClose()) { workspace.SelectedTab = tab; return false; }
            if (!ReferenceEquals(workspace, _workspace)) return false;
        }
        return true;
    }

    private void SourceOpenRequested(object? sender, WmlEditorSourceTarget target)
    {
        if (sender is WmlCodeEditorView pane && _panes.ContainsValue(pane)) OpenSource(target);
    }

    internal void OpenSource(WmlEditorSourceTarget target)
    {
        if (_workspace is null) return;
        var existing = _workspace.Tabs.FirstOrDefault(tab => tab.Identity == target.Identity);
        var document = existing?.Document ?? (!_workspace.RootDocument.IsReadOnly
            ? SourceDocumentFactory?.Invoke(target, _workspace.RootDocument) : null);
        var tab = document is null
            ? _workspace.OpenReadOnlySource(target.Identity, target.Title, target.Text, target.Description)
            : _workspace.OpenSourceDocument(target.Identity, document);
        GetPane(tab).ReferenceContext = tab.Document.Text == target.Text ? target.NestedContext
            : target.NestedContext.WithInspectionText(tab.Document.Text);
    }

    public void SelectSource(int start, int length)
    {
        if (_workspace is null) return;
        _workspace.SelectedTab = _workspace.RootTab;
        RootCodeView.SelectSource(start, length);
    }

    public bool HandleSearchKey(KeyEventArgs args)
        => _workspace is not null && GetPane(_workspace.SelectedTab).HandleSearchKey(args);
}
