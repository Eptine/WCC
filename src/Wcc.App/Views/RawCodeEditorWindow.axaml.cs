using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class RawCodeEditorWindow : Window
{
    private readonly WmlEditorDocumentViewModel _document;
    private readonly int? _initialSelectionStart;
    private readonly int _initialSelectionLength;
    private readonly CancellationTokenSource _sourceContextCancellation = new();
    private readonly Func<string, Task>? _onSaveAsync;
    private Task<bool>? _asyncCloseTask;
    private bool _asyncCloseApproved;
    private bool _closed;
    internal Func<Task<StandaloneWmlCloseDecision>>? AsyncCloseDecision { get; set; }
    public WmlEditorInitialSelection? InitialSourceSelection { get; init; }

    public WmlEditorReferenceContext? ReferenceContext
    {
        get => CodeView.ReferenceContext;
        set => CodeView.ReferenceContext = value;
    }

    public RawCodeEditorWindow(
        string title,
        string initialText,
        Action<string>? onSave = null,
        bool isReadOnly = false,
        string? descriptionText = null,
        int? initialSelectionStart = null,
        int initialSelectionLength = 0,
        Func<string, Task>? onSaveAsync = null)
    {
        InitializeComponent();
        _initialSelectionStart = initialSelectionStart;
        _initialSelectionLength = Math.Max(0, initialSelectionLength);
        _onSaveAsync = onSaveAsync;
        AsyncApplyButton.IsVisible = !isReadOnly && onSaveAsync is not null;
        Title = isReadOnly ? title + " — Read mode" : title;
        _document = new WmlEditorDocumentViewModel(title, initialText, onSave, isReadOnly,
            descriptionText ?? (isReadOnly
                ? "Read-only view of the raw code. Search, folding and references remain available."
                : "Edit the preserved raw code below. Closing applies changes through the host; Discard leaves it unchanged."),
            closePolicy: onSaveAsync is null ? WmlEditorClosePolicy.ApplyOnClose : WmlEditorClosePolicy.ExplicitApply);
        DataContext = _document;
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        Closing += OnClosingSave;
        Opened += OnOpenedSelectInitialText;
        Opened += async (_, _) => await WmlEditorHostConfiguration.ConfigureAsync(
            this, CodeView, _document, _sourceContextCancellation.Token);
        Closed += (_, _) => { _closed = true; _sourceContextCancellation.Cancel(); if (_onSaveAsync is not null) _document.Cancel(); };
    }

    /// <summary>Uses the existing document's host-owned shared-source save policy.</summary>
    public RawCodeEditorWindow(WmlEditorDocumentViewModel document)
        : this(document?.Title ?? throw new ArgumentNullException(nameof(document)), document.Text,
            isReadOnly: document.IsReadOnly, descriptionText: document.DescriptionText)
    { _document = document; DataContext = document; }

    private void OnOpenedSelectInitialText(object? sender, EventArgs e)
    {
        if (InitialSourceSelection is { } selection)
        {
            selection.TryGetSelection(_document.Text, out var start, out var length);
            CodeView.SelectSource(start, length);
            return;
        }
        if (_initialSelectionStart is { } requestedStart)
            CodeView.SelectSource(requestedStart, _initialSelectionLength);
    }

    private void OnClosingSave(object? sender, WindowClosingEventArgs e)
    {
        if (_onSaveAsync is not null)
        {
            if (_document.IsApplying) { e.Cancel = true; return; }
            RevokeStaleCloseApproval();
            if (_asyncCloseApproved || _document.IsCancelled || !_document.IsModified)
            { e.Cancel = !CodeView.TryCloseSourceEdits(); return; }
            e.Cancel = true;
            if (_asyncCloseTask is not null) return;
            var pending = PrepareAsyncCloseAsync();
            Dispatcher.UIThread.Post(async () => { if (await pending && !_closed) Close(); });
            return;
        }
        if (!CodeView.TryCloseSourceEdits()) { e.Cancel = true; return; }
        e.Cancel = !_document.TryClose();
    }

    private void DiscardClicked(object? sender, RoutedEventArgs e)
    {
        if (_document.IsApplying) return;
        CodeView.DiscardSourceEdits();
        _document.Cancel();
        Close();
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();

    private async void WindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (_onSaveAsync is not null && !args.Handled && args.Key == Key.S &&
            (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            args.Handled = true;
            await ApplyAndCloseAsync();
            return;
        }
        CodeView.HandleSearchKey(args);
    }

    internal Task<bool> ApplyAsync() => _closed || _onSaveAsync is null || !CodeView.RequireAppliedSourceEdits()
        ? Task.FromResult(false) : _document.TryApplyAsync(_onSaveAsync);

    private async void AsyncApplyClicked(object? sender, RoutedEventArgs args) => await ApplyAndCloseAsync();
    private async Task ApplyAndCloseAsync()
    {
        if (await ApplyAsync() && !_closed) { _asyncCloseApproved = true; Close(); }
    }

    internal Task<bool> PrepareAsyncCloseAsync()
    {
        RevokeStaleCloseApproval();
        if (_closed || _asyncCloseApproved || _document.IsCancelled || !_document.IsModified) return Task.FromResult(true);
        if (_document.IsApplying) return Task.FromResult(false);
        if (_asyncCloseTask is not null) return _asyncCloseTask;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _asyncCloseTask = completion.Task;
        _ = CompleteAsyncCloseAsync(completion);
        return completion.Task;
    }

    private void RevokeStaleCloseApproval()
    {
        // A save approval belongs to its accepted buffer. Newer edits made
        // before the queued Close must receive their own unsaved-change prompt.
        if (_asyncCloseApproved && _document.IsModified && !_document.IsCancelled)
            _asyncCloseApproved = false;
    }

    private async Task CompleteAsyncCloseAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            var capturedText = _document.Text;
            var decision = AsyncCloseDecision is { } decide ? await decide() :
                await new StandaloneWmlUnsavedChangesWindow(_document.Title).ShowDialog<StandaloneWmlCloseDecision?>(this)
                    ?? StandaloneWmlCloseDecision.Cancel;
            if (_closed || _document.IsApplying || capturedText != _document.Text)
            { completion.TrySetResult(false); return; }
            var accepted = decision switch
            {
                StandaloneWmlCloseDecision.Save => await ApplyAsync(),
                StandaloneWmlCloseDecision.Discard => true,
                _ => false
            };
            if (accepted && decision == StandaloneWmlCloseDecision.Discard)
            { CodeView.DiscardSourceEdits(); _document.Cancel(); }
            _asyncCloseApproved = accepted;
            completion.TrySetResult(accepted);
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        { completion.TrySetResult(false); }
        finally { if (ReferenceEquals(_asyncCloseTask, completion.Task)) _asyncCloseTask = null; }
    }
}
