using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

/// <summary>A separate file workspace; its panes have lexical references only.</summary>
public partial class StandaloneWmlEditorWindow : Window
{
    private readonly Dictionary<StandaloneWmlFileTab, WmlCodeEditorView> _panes
        = new(ReferenceEqualityComparer.Instance);
    private readonly Func<StandaloneWmlFileTab, Task<StandaloneWmlCloseDecision>> _closeDecision;
    private Task<bool>? _windowCloseTask;
    private long _externalOpenVersion;
    private bool _closeApproved;
    private bool _nativeClosePending;

    public StandaloneWmlEditorViewModel ViewModel { get; }

    public StandaloneWmlEditorWindow() : this(new StandaloneWmlEditorViewModel()) { }

    public StandaloneWmlEditorWindow(StandaloneWmlEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        ViewModel = viewModel;
        _closeDecision = ShowUnsavedChangesAsync;
        ViewModel.CloseDecisionAsync ??= _closeDecision;
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ModelChanged;
        ViewModel.Tabs.CollectionChanged += TabsChanged;
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        Closing += WindowClosing;
        Closed += WindowClosed;
        ShowSelectedPane();
    }

    public async Task OpenFilesAsync(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _externalOpenVersion++;
        _closeApproved = false;
        // A forwarded Open must keep the window alive, then populate it after
        // the already pending close decision instead of losing the request.
        if (_windowCloseTask is { } pendingClose) await pendingClose;
        _closeApproved = false;
        await ViewModel.OpenFilesAsync(files);
    }

    private WmlCodeEditorView GetPane(StandaloneWmlFileTab tab)
    {
        if (!_panes.TryGetValue(tab, out var pane))
        {
            pane = new WmlCodeEditorView { DataContext = tab.Document, ReferenceContext = null };
            _panes.Add(tab, pane);
        }
        return pane;
    }

    private void ShowSelectedPane()
    {
        var tab = ViewModel.SelectedTab;
        PaneHost.Content = tab is not null && ViewModel.Tabs.Contains(tab) ? GetPane(tab) : null;
        EmptyState.IsVisible = PaneHost.Content is null;
        FileTabs.IsVisible = ViewModel.Tabs.Count > 0;
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(StandaloneWmlEditorViewModel.SelectedTab)) ShowSelectedPane();
    }

    private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var tab in _panes.Keys.Where(tab => !ViewModel.Tabs.Contains(tab)).ToArray()) _panes.Remove(tab);
        ShowSelectedPane();
    }

    private void TabSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        // Selection binding may still be publishing during the routed event.
        if (ViewModel is not null) ShowSelectedPane();
    }

    private async Task ShowOpenPickerAsync()
    {
        if (ViewModel.IsBusy || _nativeClosePending) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open WML files",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Wesnoth WML") { Patterns = ["*.cfg"] }]
        });
        if (_closeApproved || _nativeClosePending || ViewModel.IsBusy) return;
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0) await OpenFilesAsync(paths);
    }

    private async void OpenClicked(object? sender, RoutedEventArgs args) => await RunUiActionAsync(ShowOpenPickerAsync);

    private async void SaveClicked(object? sender, RoutedEventArgs args)
        => await RunUiActionAsync(async () => { await ViewModel.SaveSelectedAsync(); });

    private async void CloseFileClicked(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedTab is { } tab)
            await RunUiActionAsync(async () => { await ViewModel.RequestCloseTabAsync(tab); });
    }

    private async void CloseTabClicked(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is Control { DataContext: StandaloneWmlFileTab tab })
            await RunUiActionAsync(async () => { await ViewModel.RequestCloseTabAsync(tab); });
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled) return;
        if ((args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (args.Key == Key.O)
            { args.Handled = true; await RunUiActionAsync(ShowOpenPickerAsync); return; }
            if (args.Key == Key.S)
            { args.Handled = true; await RunUiActionAsync(async () => { await ViewModel.SaveSelectedAsync(); }); return; }
            if (args.Key == Key.W)
            {
                args.Handled = true;
                if (ViewModel.SelectedTab is { } tab)
                    await RunUiActionAsync(async () => { await ViewModel.RequestCloseTabAsync(tab); });
                return;
            }
        }
        if (ViewModel.SelectedTab is { } selected && _panes.TryGetValue(selected, out var pane))
            pane.HandleSearchKey(args);
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        if (_nativeClosePending || _closeApproved) return;
        WindowErrorText.Text = null;
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { WindowErrorText.Text = error.Message; }
    }

    private async Task<StandaloneWmlCloseDecision> ShowUnsavedChangesAsync(StandaloneWmlFileTab tab)
    {
        var prompt = new StandaloneWmlUnsavedChangesWindow(tab);
        return await prompt.ShowDialog<StandaloneWmlCloseDecision?>(this) ?? StandaloneWmlCloseDecision.Cancel;
    }

    private void WindowClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closeApproved) return;
        args.Cancel = true;
        if (_nativeClosePending) return;
        _nativeClosePending = true;
        var openVersion = _externalOpenVersion;
        var pendingClose = PrepareWindowCloseAsync();
        // Reclose after this synchronous Closing event has unwound.
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (await pendingClose && openVersion == _externalOpenVersion) Close();
                else _closeApproved = false;
            }
            finally { _nativeClosePending = false; }
        });
    }

    internal Task<bool> PrepareWindowCloseAsync()
    {
        if (_closeApproved) return Task.FromResult(true);
        if (_windowCloseTask is not null) return _windowCloseTask;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _windowCloseTask = completion.Task;
        _ = CompleteWindowCloseAsync(completion);
        return completion.Task;
    }

    private async Task CompleteWindowCloseAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            var openVersion = _externalOpenVersion;
            var accepted = await ViewModel.RequestCloseAllAsync();
            accepted &= openVersion == _externalOpenVersion;
            _closeApproved = accepted;
            completion.TrySetResult(accepted);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            WindowErrorText.Text = error.Message;
            completion.TrySetResult(false);
        }
        finally
        {
            if (ReferenceEquals(_windowCloseTask, completion.Task)) _windowCloseTask = null;
        }
    }

    private void WindowClosed(object? sender, EventArgs args)
    {
        ViewModel.PropertyChanged -= ModelChanged;
        ViewModel.Tabs.CollectionChanged -= TabsChanged;
        if (ViewModel.CloseDecisionAsync == _closeDecision) ViewModel.CloseDecisionAsync = null;
        PaneHost.Content = null;
        _panes.Clear();
    }
}
