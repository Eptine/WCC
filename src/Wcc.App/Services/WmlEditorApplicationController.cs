using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Wcc.App.Views;

namespace Wcc.App.Services;

/// <summary>
/// Owns only the independent file editor. Campaign RAW windows never enter this
/// registry. Requests can be accepted before Avalonia is initialized.
/// </summary>
public sealed class WmlEditorApplicationController : IDisposable
{
    private sealed record OpenRequest(IReadOnlyList<string> Files, TaskCompletionSource Completion);
    private readonly object _gate = new();
    private readonly Queue<OpenRequest> _requests = new();
    private readonly bool _launchStandalone;
    private readonly Func<StandaloneWmlEditorWindow> _createEditor;
    private readonly Func<AppShellWindow> _createShell;
    private readonly Action<Window> _show;
    private readonly Action<Window> _activate;
    private readonly Action<Action> _post;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private StandaloneWmlEditorWindow? _editor;
    private AppShellWindow? _shell;
    private Window? _initialWindow;
    private IDisposable? _closedSubscription;
    private OpenRequest? _activeRequest;
    private bool _ready;
    private bool _draining;
    private bool _disposed;
    private bool _shutdownAttempt;
    private bool _shutdownCommitted;
    private Task _pendingRequests = Task.CompletedTask;

    public WmlEditorApplicationController(IReadOnlyList<string>? initialFiles = null,
        Func<StandaloneWmlEditorWindow>? createEditor = null, Func<AppShellWindow>? createShell = null,
        Action<Window>? show = null, Action<Window>? activate = null, Action<Action>? post = null)
    {
        _createEditor = createEditor ?? (() => new StandaloneWmlEditorWindow());
        _createShell = createShell ?? (() => new AppShellWindow());
        _show = show ?? (window => window.Show());
        _activate = activate ?? (window =>
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        });
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _launchStandalone = initialFiles?.Count > 0;
        if (_launchStandalone) EnqueueFiles(initialFiles!);
    }

    public StandaloneWmlEditorWindow? EditorWindow => _editor;
    public Task PendingRequestsCompletion { get { lock (_gate) return _pendingRequests; } }

    public void Initialize(IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_desktop is not null) throw new InvalidOperationException("WCC file routing is already initialized.");
            _desktop = desktop;
        }
        // The final empty-window transition and pipe acceptance share one
        // fence. Avalonia's automatic last-window shutdown could otherwise
        // discard an acknowledged request before its UI post executes.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _closedSubscription = Window.WindowClosedEvent.AddClassHandler(typeof(Window), (_, _) => ScheduleFinalShutdown());
        _initialWindow = _launchStandalone ? GetEditor() : (_shell = _createShell());
        if (_shell is not null) _shell.Closed += ShellClosed;
        _initialWindow.Opened += InitialWindowOpened;
        desktop.ShutdownRequested += DesktopShutdownRequested;
        desktop.Exit += DesktopExit;
        desktop.MainWindow = _initialWindow;
    }

    // Called by the pipe listener on its own thread. Do not touch Avalonia until
    // the main window has opened and the application dispatcher is ready.
    public void EnqueueFiles(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool ready;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shutdownAttempt || _shutdownCommitted)
                throw new InvalidOperationException("WCC is finishing shutdown. Open the file again after it closes.");
            _requests.Enqueue(new(Array.AsReadOnly(files.ToArray()), completion));
            _pendingRequests = completion.Task;
            ready = _ready;
        }
        if (ready) _post(DrainRequests);
    }

    public async Task OpenEditorAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shutdownAttempt || _shutdownCommitted)
                throw new InvalidOperationException("WCC is finishing shutdown.");
        }
        var window = GetEditor();
        ShowOrActivate(window);
        // Even an empty explicit Open Editor request keeps a window alive if its
        // asynchronous close prompt was already in progress.
        await window.OpenFilesAsync(Array.Empty<string>());
    }

    private StandaloneWmlEditorWindow GetEditor()
    {
        if (_editor is not null) return _editor;
        var window = _createEditor();
        _editor = window;
        window.Closed += EditorClosed;
        return window;
    }

    private void EditorClosed(object? sender, EventArgs args)
    { if (ReferenceEquals(_editor, sender)) _editor = null; }

    private void ShellClosed(object? sender, EventArgs args)
    { if (ReferenceEquals(_shell, sender)) _shell = null; }

    private void ShowOrActivate(Window window)
    {
        if (!window.IsVisible) _show(window);
        else _activate(window);
    }

    private void InitialWindowOpened(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _ready = true;
        }
        DrainRequests();
    }

    private async void DrainRequests()
    {
        lock (_gate)
        {
            if (_disposed || !_ready || _draining) return;
            _draining = true;
        }
        try
        {
            while (true)
            {
                var request = TakeNextRequest();
                if (request is null) break;
                try
                {
                    if (request.Files.Count == 0)
                    {
                        var existing = (Window?)_editor ?? _shell ?? _desktop?.Windows.FirstOrDefault(window => window.IsVisible);
                        if (existing is not null) ShowOrActivate(existing);
                    }
                    else
                    {
                        var editor = GetEditor();
                        ShowOrActivate(editor);
                        await editor.OpenFilesAsync(request.Files);
                    }
                    request.Completion.TrySetResult();
                }
                catch (Exception failure)
                {
                    Trace.TraceError("WCC file-open request failed: {0}", failure);
                    request.Completion.TrySetException(failure);
                }
                finally
                {
                    lock (_gate)
                    { if (ReferenceEquals(_activeRequest, request)) _activeRequest = null; }
                }
            }
        }
        finally
        {
            bool pending;
            lock (_gate) { _draining = false; pending = !_disposed && _requests.Count > 0; }
            if (pending) _post(DrainRequests);
            else ScheduleFinalShutdown();
        }
    }

    private OpenRequest? TakeNextRequest()
    {
        lock (_gate)
        {
            if (_disposed || _requests.Count == 0) return null;
            return _activeRequest = _requests.Dequeue();
        }
    }

    internal bool TryCommitFinalShutdown()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_requests.Count > 0 || _activeRequest is not null) return false;
            _shutdownCommitted = true;
            return true;
        }
    }

    internal bool TryBeginShutdown()
    {
        lock (_gate)
        {
            if (_disposed || _requests.Count > 0 || _activeRequest is not null) return false;
            _shutdownAttempt = true;
            return true;
        }
    }

    internal void CancelShutdownAttempt()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _shutdownAttempt = false;
            _shutdownCommitted = false;
        }
    }

    private void DesktopShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    {
        if (!TryBeginShutdown())
        {
            args.Cancel = true;
            _post(DrainRequests);
            return;
        }
        // An unsaved-window prompt can cancel this synchronous native attempt.
        // Reopen acceptance after it unwinds; successful Exit disposes us first.
        _post(() =>
        {
            lock (_gate) { if (_disposed) return; }
            if (args.Cancel || _desktop?.Windows.Count > 0) CancelShutdownAttempt();
        });
    }

    private void ScheduleFinalShutdown()
    {
        lock (_gate) { if (_disposed) return; }
        if (_desktop is not { } desktop || desktop.Windows.Count != 0) return;
        if (!TryCommitFinalShutdown()) { _post(DrainRequests); return; }
        // Post after all native Closed callbacks. Every owned and RAW window
        // participates through the lifetime's complete window list.
        _post(() =>
        {
            lock (_gate) { if (_disposed) return; }
            if (desktop.Windows.Count != 0) { CancelShutdownAttempt(); return; }
            if (!desktop.TryShutdown()) CancelShutdownAttempt();
        });
    }

    private void DesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs args) => Dispose();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            _activeRequest?.Completion.TrySetCanceled();
            while (_requests.TryDequeue(out var request)) request.Completion.TrySetCanceled();
        }
        if (_initialWindow is not null) _initialWindow.Opened -= InitialWindowOpened;
        if (_editor is not null) _editor.Closed -= EditorClosed;
        if (_shell is not null) _shell.Closed -= ShellClosed;
        _closedSubscription?.Dispose();
        _closedSubscription = null;
        if (_desktop is not null)
        {
            _desktop.ShutdownRequested -= DesktopShutdownRequested;
            _desktop.Exit -= DesktopExit;
        }
    }
}
