using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.Services;

namespace Wcc.App.ViewModels;

public enum StandaloneWmlCloseDecision { Save, Discard, Cancel }

public sealed class StandaloneWmlFileTab : ViewModelBase
{
    internal StandaloneWmlFileTab(StandaloneWmlFileSnapshot file)
    {
        File = file;
        Path = file.Path;
        Title = System.IO.Path.GetFileName(file.Path);
        Document = new WmlEditorDocumentViewModel(Title, file.Text,
            descriptionText: "Standalone RAW file. Save overwrites this file without WML or project checks.");
        Document.PropertyChanged += DocumentChanged;
    }

    internal StandaloneWmlFileSnapshot File { get; }
    internal long BufferRevision { get; private set; }
    public string Path { get; }
    public string Title { get; }
    public string DisplayTitle => Title + (Document.IsModified ? " *" : string.Empty);
    public WmlEditorDocumentViewModel Document { get; }
    public bool IsSaving => Document.IsApplying;
    public string? ErrorMessage => Document.ValidationMessage;

    private void DocumentChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WmlEditorDocumentViewModel.Text) or nameof(WmlEditorDocumentViewModel.IsCancelled))
            BufferRevision++;
        if (args.PropertyName is nameof(WmlEditorDocumentViewModel.Text) or nameof(WmlEditorDocumentViewModel.IsModified))
            OnPropertyChanged(nameof(DisplayTitle));
        if (args.PropertyName == nameof(WmlEditorDocumentViewModel.IsApplying))
            OnPropertyChanged(nameof(IsSaving));
        if (args.PropertyName == nameof(WmlEditorDocumentViewModel.ValidationMessage))
            OnPropertyChanged(nameof(ErrorMessage));
    }
}

/// <summary>
/// Independent RAW file tabs. The file store owns portable persistence; project
/// validation, resolution contexts and guarded campaign writers are not used.
/// </summary>
public sealed class StandaloneWmlEditorViewModel : ViewModelBase
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly StandaloneWmlFileStore _store;
    private readonly object _openGate = new();
    private readonly Dictionary<string, Task<StandaloneWmlFileTab?>> _opening = new(PathComparer);
    private readonly HashSet<StandaloneWmlFileTab> _observedTabs = new();
    private StandaloneWmlFileTab? _selectedTab;
    private int _pendingOpens;
    private long _openIntentVersion;
    private long _membershipVersion;
    private bool _isClosing;
    private string? _status;
    private string? _errorMessage;

    public StandaloneWmlEditorViewModel(StandaloneWmlFileStore? store = null)
    {
        _store = store ?? new StandaloneWmlFileStore();
        Tabs.CollectionChanged += TabsChanged;
    }

    public ObservableCollection<StandaloneWmlFileTab> Tabs { get; } = new();
    public StandaloneWmlFileTab? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is not null && !Tabs.Contains(value)) return;
            if (SetField(ref _selectedTab, value)) OnPropertyChanged(nameof(CanSaveSelected));
        }
    }
    public bool IsBusy => _pendingOpens != 0 || IsClosing || Tabs.Any(tab => tab.IsSaving);
    public bool IsClosing => _isClosing;
    public bool CanSaveSelected => !IsClosing && SelectedTab is { } tab && Tabs.Contains(tab) &&
                                  !tab.Document.IsCancelled && !tab.IsSaving;
    public string? Status => _status;
    public string? ErrorMessage => _errorMessage;
    public Func<StandaloneWmlFileTab, Task<StandaloneWmlCloseDecision>>? CloseDecisionAsync { get; set; }

    public Task<StandaloneWmlFileTab?> OpenAsync(string path)
    {
        string fullPath;
        try { fullPath = StandaloneWmlFileStore.NormalizePath(path); }
        catch (Exception failure) when (IsFileFailure(failure))
        {
            ReportError(failure.Message);
            return Task.FromResult<StandaloneWmlFileTab?>(null);
        }
        TaskCompletionSource<StandaloneWmlFileTab?> completion;
        lock (_openGate)
        {
            // Reopening an existing/pending path is still newer navigation
            // intent, even though it must retain the original buffer/read.
            _openIntentVersion++;
            var existing = Tabs.FirstOrDefault(tab => PathComparer.Equals(tab.Path, fullPath));
            if (existing is not null)
            {
                SelectedTab = existing;
                return Task.FromResult<StandaloneWmlFileTab?>(existing);
            }
            if (_opening.TryGetValue(fullPath, out var pending)) return pending;
            completion = new TaskCompletionSource<StandaloneWmlFileTab?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _opening.Add(fullPath, completion.Task);
            _pendingOpens++;
        }
        _ = OpenRegisteredAsync(fullPath, completion);
        RaiseBusyChanged();
        return completion.Task;
    }

    public async Task<bool> OpenFilesAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var requestedPaths = paths.ToArray();
        var failures = new List<string>();
        foreach (var path in requestedPaths)
            if (await OpenAsync(path) is null)
                failures.Add(path + ": " + (ErrorMessage ?? "The file could not be opened."));
        if (failures.Count != 0) ReportError(string.Join(Environment.NewLine, failures));
        return failures.Count == 0;
    }

    private async Task OpenRegisteredAsync(string fullPath, TaskCompletionSource<StandaloneWmlFileTab?> completion)
    {
        // Register the shared operation before any synchronous completion or
        // publication callback can open the same file again.
        await Task.Yield();
        StandaloneWmlFileTab? result = null;
        Exception? unexpectedFailure = null;
        try
        {
            var file = await _store.OpenAsync(fullPath);
            if (!PathComparer.Equals(StandaloneWmlFileStore.NormalizePath(file.Path), fullPath))
                throw new InvalidOperationException("The opened file does not match the requested path.");
            lock (_openGate)
            {
                result = Tabs.FirstOrDefault(tab => PathComparer.Equals(tab.Path, fullPath));
                if (result is null)
                {
                    result = new StandaloneWmlFileTab(file with { Path = fullPath });
                    Tabs.Add(result);
                }
                if (Tabs.Contains(result)) SelectedTab = result;
                else result = null;
            }
            if (result is not null) ReportStatus("Opened " + result.Path);
        }
        catch (Exception failure)
        {
            if (IsFileFailure(failure))
            {
                try { ReportError(failure.Message); }
                catch (Exception notificationFailure) { unexpectedFailure = notificationFailure; }
            }
            else unexpectedFailure = failure;
        }
        finally
        {
            lock (_openGate)
            {
                _opening.Remove(fullPath);
                _pendingOpens--;
            }
            try { RaiseBusyChanged(); }
            catch (Exception notificationFailure) { unexpectedFailure ??= notificationFailure; }
        }
        if (result is not null && !Tabs.Contains(result)) result = null;
        if (unexpectedFailure is null) completion.TrySetResult(result);
        else completion.TrySetException(unexpectedFailure);
    }

    public Task<bool> SaveSelectedAsync() => SelectedTab is { } tab
        ? SaveAsync(tab) : Task.FromResult(false);

    public Task<bool> SaveAsync(StandaloneWmlFileTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return IsClosing ? Task.FromResult(false) : SaveCoreAsync(tab);
    }

    private async Task<bool> SaveCoreAsync(StandaloneWmlFileTab tab)
    {
        if (!Tabs.Contains(tab) || tab.IsSaving || tab.Document.IsCancelled) return false;
        var succeeded = await tab.Document.TryApplyAsync(text => _store.SaveAsync(tab.File, text));
        if (succeeded && Tabs.Contains(tab) && !tab.Document.IsCancelled && !tab.Document.IsModified)
        {
            ReportStatus("Saved " + tab.Path);
            // Status callbacks can create newer intent after the document's
            // final notification fence. Never close over that newer buffer.
            return Tabs.Contains(tab) && !tab.Document.IsCancelled && !tab.Document.IsModified;
        }
        ReportError(tab.Document.ValidationMessage ?? "The file was not saved. Review the current buffer and try again.");
        return false;
    }

    public async Task<bool> RequestCloseTabAsync(StandaloneWmlFileTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (IsClosing || !Tabs.Contains(tab) || tab.IsSaving) return false;
        var intent = Capture(tab);
        var openVersion = _openIntentVersion;
        var succeeded = false;
        try
        {
            SetClosing(true);
            succeeded = await CloseCurrentTabAsync();
        }
        catch (Exception failure) when (IsFileFailure(failure)) { ReportError(failure.Message); }
        finally { SetClosing(false); }
        return succeeded && !Tabs.Contains(tab) && _openIntentVersion == openVersion;

        async Task<bool> CloseCurrentTabAsync()
        {
            if (!IsCurrent(intent)) return false;
            var decision = tab.Document.IsModified ? await DecideAsync(tab) : StandaloneWmlCloseDecision.Discard;
            if (!IsCurrent(intent) || _openIntentVersion != openVersion) return false;
            if (decision == StandaloneWmlCloseDecision.Cancel) return false;
            if (decision == StandaloneWmlCloseDecision.Save && !await SaveCoreAsync(tab)) return false;
            if (!Enum.IsDefined(decision) || !IsCurrent(intent) || _openIntentVersion != openVersion) return false;
            RemoveTab(tab);
            return !Tabs.Contains(tab) && _openIntentVersion == openVersion;
        }
    }

    public async Task<bool> RequestCloseAllAsync()
    {
        if (IsBusy) return false;
        var intents = Tabs.Select(Capture).ToArray();
        var membershipVersion = _membershipVersion;
        var openVersion = _openIntentVersion;
        var decisions = new StandaloneWmlCloseDecision[intents.Length];
        var succeeded = false;
        try
        {
            SetClosing(true);
            succeeded = await CloseCapturedTabsAsync();
        }
        catch (Exception failure) when (IsFileFailure(failure)) { ReportError(failure.Message); }
        finally { SetClosing(false); }
        return succeeded && Tabs.Count == 0 && _pendingOpens == 0 && _openIntentVersion == openVersion;

        async Task<bool> CloseCapturedTabsAsync()
        {
            bool Current() => _membershipVersion == membershipVersion && _openIntentVersion == openVersion &&
                              _pendingOpens == 0 && intents.All(IsCurrent);
            // Gather every decision before saving or removing any tab. Cancel
            // therefore preserves all buffers, including earlier Save choices.
            for (var i = 0; i < intents.Length; i++)
            {
                if (!Current()) return false;
                decisions[i] = intents[i].Tab.Document.IsModified
                    ? await DecideAsync(intents[i].Tab) : StandaloneWmlCloseDecision.Discard;
                if (!Current() || decisions[i] == StandaloneWmlCloseDecision.Cancel || !Enum.IsDefined(decisions[i]))
                    return false;
            }
            for (var i = 0; i < intents.Length; i++)
            {
                if (!Current()) return false;
                if (decisions[i] == StandaloneWmlCloseDecision.Save && !await SaveCoreAsync(intents[i].Tab)) return false;
            }
            if (!Current()) return false;
            foreach (var intent in intents)
            {
                if (_openIntentVersion != openVersion || !IsCurrent(intent)) return false;
                RemoveTab(intent.Tab);
            }
            return Tabs.Count == 0 && _pendingOpens == 0 && _openIntentVersion == openVersion;
        }
    }

    private Task<StandaloneWmlCloseDecision> DecideAsync(StandaloneWmlFileTab tab) =>
        CloseDecisionAsync?.Invoke(tab) ?? Task.FromResult(StandaloneWmlCloseDecision.Cancel);

    private sealed record CloseIntent(StandaloneWmlFileTab Tab, long Revision, string Text);
    private static CloseIntent Capture(StandaloneWmlFileTab tab) => new(tab, tab.BufferRevision, tab.Document.Text);
    private bool IsCurrent(CloseIntent intent) => Tabs.Contains(intent.Tab) && !intent.Tab.IsSaving &&
        !intent.Tab.Document.IsCancelled && intent.Tab.BufferRevision == intent.Revision &&
        string.Equals(intent.Tab.Document.Text, intent.Text, StringComparison.Ordinal);

    private void RemoveTab(StandaloneWmlFileTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        var wasSelected = ReferenceEquals(SelectedTab, tab);
        Tabs.Remove(tab);
        tab.Document.Cancel();
        if (wasSelected && Tabs.Count != 0 && SelectedTab is null)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        _membershipVersion++;
        foreach (var old in _observedTabs.Where(tab => !Tabs.Contains(tab)).ToArray())
        {
            old.PropertyChanged -= TabChanged;
            _observedTabs.Remove(old);
        }
        foreach (var tab in Tabs.Where(tab => !_observedTabs.Contains(tab)))
        {
            _observedTabs.Add(tab);
            tab.PropertyChanged += TabChanged;
        }
        if (SelectedTab is { } selected && !Tabs.Contains(selected)) SelectedTab = null;
        RaiseBusyChanged();
    }

    private void TabChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(StandaloneWmlFileTab.IsSaving)) RaiseBusyChanged();
    }

    private void SetClosing(bool value)
    {
        if (!SetField(ref _isClosing, value, nameof(IsClosing))) return;
        RaiseBusyChanged();
    }

    private void RaiseBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanSaveSelected));
    }

    public void ReportStatus(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        SetField(ref _errorMessage, null, nameof(ErrorMessage));
        SetField(ref _status, message, nameof(Status));
    }

    private void ReportError(string message)
    {
        SetField(ref _errorMessage, message, nameof(ErrorMessage));
        SetField(ref _status, null, nameof(Status));
    }

    private static bool IsFileFailure(Exception failure) => failure is IOException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or NotSupportedException;
}
