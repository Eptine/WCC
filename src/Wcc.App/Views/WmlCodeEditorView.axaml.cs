using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Indentation;
using AvaloniaEdit.Rendering;
using Wcc.App.ViewModels;
using Wcc.Core.Wml;

namespace Wcc.App.Views;

/// <summary>
/// Reusable presentation surface. Its document host remains responsible for
/// validation, source identity, staging and persistence.
/// </summary>
public partial class WmlCodeEditorView : UserControl
{
    private readonly ConditionalWeakTable<WmlEditorDocumentViewModel, TextDocument> _buffers = new();
    private readonly WmlTokenColorizer _colorizer = new();
    private readonly WmlTagPairRenderer _tagPairRenderer = new();
    private readonly List<RememberedFold> _rememberedFolds = new();
    private WmlEditorDocumentViewModel? _document;
    private TextDocument? _observedBuffer;
    private FoldingManager? _foldingManager;
    private CancellationTokenSource? _analysisCancellation;
    private WmlEditorReferenceContext? _referenceContext;
    private ContextMenu? _sourceChoiceMenu;
    private IPointer? _pressedPointer;
    private PendingPointerSourceOpen? _pendingPointerSourceOpen;
    private IReadOnlyList<int> _findMatches = Array.Empty<int>();
    private int _currentFindMatch = -1;
    private long _analysisRevision;
    private bool _ready;
    private bool _isAttached;
    private bool _modelSubscribed;
    private bool _synchronizing;

    internal WmlEditorAnalysis? CurrentAnalysis { get; private set; }
    internal WmlEditorTagOccurrence? CurrentCaretTag { get; private set; }
    internal WmlEditorTagOccurrence? CurrentMatchingTag { get; private set; }
    internal IReadOnlyList<WmlEditorReferenceGroup> CurrentReferenceGroups { get; private set; }
        = Array.Empty<WmlEditorReferenceGroup>();
    internal bool IsAnalysisPending { get; private set; }
    internal Task AnalysisCompletion { get; private set; } = Task.CompletedTask;

    public WmlEditorReferenceContext? ReferenceContext
    {
        get => _referenceContext;
        set
        {
            if (ReferenceEquals(value, _referenceContext)) return;
            CloseReferencePopups();
            _referenceContext = value;
            if (_ready) RefreshReferenceRows();
        }
    }

    public event EventHandler<WmlEditorSourceTarget>? SourceOpenRequested;

    public WmlCodeEditorView()
    {
        InitializeComponent();
        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_tagPairRenderer);
        Editor.Options.IndentationSize = 4;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.TextArea.IndentationStrategy = new WmlIndentationStrategy(this);
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#4A6480"));
        Editor.TextArea.SelectionForeground = new SolidColorBrush(Color.Parse("#FFF3D7"));
        Editor.TextArea.CaretBrush = new SolidColorBrush(Color.Parse("#E6DDC5"));
        Editor.TextChanged += EditorTextChanged;
        Editor.AddHandler(PointerPressedEvent, EditorPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        Editor.AddHandler(PointerReleasedEvent, EditorPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Editor.DoubleTapped += EditorDoubleTapped;
        Editor.PointerHover += EditorPointerHover;
        Editor.PointerHoverStopped += (_, _) => CloseReferencePreview();
        Editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        DataContextChanged += (_, _) => BindDocument();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        AddHandler(KeyDownEvent, (_, args) => HandleSearchKey(args), RoutingStrategies.Tunnel);
        _ready = true;
        BindDocument();
    }

    /// <summary>Select an exact source span, unfolding any region that hides it.</summary>
    public void SelectSource(int start, int length) => SelectSpan(start, length, focusEditor: true);

    /// <summary>Hosts may forward window-level search shortcuts, even with a footer focused.</summary>
    public bool HandleSearchKey(KeyEventArgs args)
    {
        if (args.Handled || !_ready) return false;
        if (args.Key == Key.F && (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            FindTextBox.Focus();
            FindTextBox.SelectAll();
            args.Handled = true;
            return true;
        }
        if (args.Key == Key.H && (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (CanReplaceBuffer) ShowReplacementRow();
            else FindTextBox.Focus();
            args.Handled = true;
            return true;
        }
        if (args.Key != Key.F3) return false;
        MoveToFindMatch((args.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
        args.Handled = true;
        return true;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        _isAttached = true;
        BindDocument();
        _foldingManager ??= FoldingManager.Install(Editor.TextArea);
        ScheduleAnalysis();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        _isAttached = false;
        UnsubscribeModel();
        ObserveBuffer(null);
        CancelAnalysis();
        ClearPresentation();
        RemoveFoldingManager();
        IsAnalysisPending = false;
    }

    private void RemoveFoldingManager()
    {
        if (_foldingManager is null) return;
        FoldingManager.Uninstall(_foldingManager);
        _foldingManager = null;
    }

    private void BindDocument()
    {
        if (!_ready) return;
        var next = DataContext as WmlEditorDocumentViewModel;
        if (!ReferenceEquals(next, _document))
        {
            UnsubscribeModel();
            ObserveBuffer(null);
            CancelAnalysis();
            ClearPresentation(rememberFolds: false);
            RemoveFoldingManager();
            _document = next;
            _synchronizing = true;
            try
            {
                Editor.Document = next is null
                    ? new TextDocument()
                    : _buffers.GetValue(next, model => new TextDocument(model.Text));
            }
            finally { _synchronizing = false; }
            if (_isAttached) _foldingManager = FoldingManager.Install(Editor.TextArea);
        }
        ObserveBuffer(_isAttached ? Editor.Document : null);
        if (_document is not null)
        {
            SynchronizeFromModel();
            if (_isAttached && !_modelSubscribed)
            {
                _document.PropertyChanged += ModelPropertyChanged;
                _modelSubscribed = true;
            }
        }
        UpdateEditorPolicy();
        UpdateDocumentStatus();
        UpdateCaretStatus();
        RebuildFindMatches(selectMatch: false);
        ScheduleAnalysis();
    }

    private void UnsubscribeModel()
    {
        if (_modelSubscribed && _document is not null)
            _document.PropertyChanged -= ModelPropertyChanged;
        _modelSubscribed = false;
    }

    private void ObserveBuffer(TextDocument? buffer)
    {
        if (ReferenceEquals(buffer, _observedBuffer)) return;
        if (_observedBuffer is not null) _observedBuffer.Changed -= BufferChanged;
        _observedBuffer = buffer;
        if (_observedBuffer is not null) _observedBuffer.Changed += BufferChanged;
    }

    private void BufferChanged(object? sender, DocumentChangeEventArgs args)
    {
        if (!ReferenceEquals(sender, Editor.Document)) return;
        // References are exact source positions and must become unusable immediately. Display
        // colors are only hints: keep their surviving spans aligned while fresh analysis runs.
        CancelAnalysis();
        InvalidateReferences();
        _colorizer.ApplyDocumentChange(args);
        // FoldingManager tracks its own sections through the same change. Keeping it installed
        // avoids briefly expanding every collapsed region on each keystroke.
    }

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _document)) return;
        if (args.PropertyName == nameof(WmlEditorDocumentViewModel.Text) && !_synchronizing)
            SynchronizeFromModel();
        UpdateEditorPolicy();
        UpdateDocumentStatus();
    }

    private void SynchronizeFromModel()
    {
        if (_document is null || string.Equals(Editor.Document.Text, _document.Text, StringComparison.Ordinal))
            return;
        var selectionStart = Editor.SelectionStart;
        var selectionLength = Editor.SelectionLength;
        _synchronizing = true;
        try
        {
            // Keep this buffer and its undo history. TextEditor.Text would reset both the caret and undo.
            Editor.Document.Replace(0, Editor.Document.TextLength, _document.Text);
            var start = Math.Clamp(selectionStart, 0, Editor.Document.TextLength);
            Editor.Select(start, Math.Clamp(selectionLength, 0, Editor.Document.TextLength - start));
        }
        finally { _synchronizing = false; }
        RebuildFindMatches(selectMatch: false);
        ScheduleAnalysis();
    }

    private void EditorTextChanged(object? sender, EventArgs args)
    {
        if (!_ready || _synchronizing) return;
        if (_document is not null)
        {
            _synchronizing = true;
            try { _document.Text = Editor.Document.Text; }
            finally { _synchronizing = false; }
            // Read-only/cancelled models also refuse programmatic document changes.
            SynchronizeFromModel();
        }
        UpdateDocumentStatus();
        ClearReplacementStatus();
        RebuildFindMatches(selectMatch: false);
        ScheduleAnalysis();
    }

    private void UpdateEditorPolicy()
    {
        Editor.IsReadOnly = _document is null || _document.IsReadOnly || _document.IsApplying || _document.IsCancelled;
        ShowReplaceButton.IsEnabled = !Editor.IsReadOnly;
        ReplaceTextBox.IsEnabled = !Editor.IsReadOnly;
        FormatWmlButton.IsEnabled = !Editor.IsReadOnly;
        UpdateReplacementButtons();
    }

    private void UpdateDocumentStatus()
    {
        DocumentStatusText.Text = _document is null ? "No document"
            : _document.IsReadOnly ? "Read mode"
            : _document.IsCancelled ? "Discarded"
            : _document.IsApplying ? "Applying..."
            : _document.IsModified ? "Modified - not applied" : "Unchanged";
        DocumentStatusText.Foreground = new SolidColorBrush(Color.Parse(
            _document?.IsModified == true ? "#E1C58B" : "#B8B09C"));
    }

    private void CaretPositionChanged(object? sender, EventArgs args)
    {
        UpdateCaretStatus();
        UpdateTagPairHighlight();
    }
    private void UpdateCaretStatus()
    {
        if (!_ready) return;
        CaretStatusText.Text = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";
    }

    private void UpdateTagPairHighlight()
    {
        if (!_ready) return;
        var tag = CurrentAnalysis?.FindTagAtCaret(Editor.CaretOffset);
        var matching = tag?.MatchingTagIndex is { } index && CurrentAnalysis is { } analysis &&
            index >= 0 && index < analysis.TagOccurrences.Count ? analysis.TagOccurrences[index] : null;
        if (Equals(tag, CurrentCaretTag) && Equals(matching, CurrentMatchingTag)) return;
        CurrentCaretTag = tag;
        CurrentMatchingTag = matching;
        _tagPairRenderer.SetTags(tag, matching);
        ToolTip.SetTip(CaretStatusText, tag is null ? null : matching is not null
            ? $"Matching {(matching.IsClosing ? "closing" : "opening")} [{matching.Name}] tag at line " +
                Editor.Document.GetLineByOffset(matching.StartOffset).LineNumber + "."
            : "No matching literal tag in this source scope. Macros or conditional branches may supply one.");
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void ScheduleAnalysis()
    {
        CancelAnalysis();
        InvalidateReferences();
        if (!_isAttached || _document is null)
        {
            IsAnalysisPending = false;
            ReferenceStatusText.Text = "No references";
            AnalysisCompletion = Task.CompletedTask;
            return;
        }
        var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        var token = cancellation.Token;
        var owner = _document;
        var buffer = Editor.Document;
        var text = buffer.Text;
        var revision = _analysisRevision;
        IsAnalysisPending = true;
        ReferenceStatusText.Text = "Updating references...";
        AnalysisCompletion = AnalyzeAsync(owner, buffer, text, revision, token);
    }

    private async Task AnalyzeAsync(WmlEditorDocumentViewModel owner, TextDocument buffer,
        string text, long revision, CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token).ConfigureAwait(false);
            var analysis = await Task.Run(() => WmlEditorAnalysis.Build(text, token), token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!CanPublish(owner, buffer, text, revision, token)) return;
                CurrentAnalysis = analysis;
                UpdateTagPairHighlight();
                CurrentReferenceGroups = WmlEditorNavigation.GroupReferences(text, analysis);
                _colorizer.SetTokens(analysis.Tokens);
                CaptureFoldState();
                _foldingManager?.UpdateFoldings(analysis.Folds.Select(fold => new NewFolding(
                    fold.StartOffset, fold.EndOffsetExclusive) { Name = fold.Label }), -1);
                RestoreFoldState();
                RefreshReferenceRows();
                var occurrences = CurrentReferenceGroups.Sum(group => group.Count);
                ReferenceStatusText.Text = CurrentReferenceGroups.Count == 0 ? "No references"
                    : $"{CurrentReferenceGroups.Count} references / {occurrences} uses";
                ToolTip.SetTip(ReferenceStatusText, null);
                IsAnalysisPending = false;
                Editor.TextArea.TextView.Redraw();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception failure)
        {
            // Presentation failure must not discard or prevent editing the authoritative source buffer.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!CanPublish(owner, buffer, text, revision, token)) return;
                IsAnalysisPending = false;
                ReferenceStatusText.Text = "Reference preview unavailable";
                ToolTip.SetTip(ReferenceStatusText, failure.Message);
            });
        }
    }

    private bool CanPublish(WmlEditorDocumentViewModel owner, TextDocument buffer,
        string text, long revision, CancellationToken token)
        => !token.IsCancellationRequested && _isAttached && revision == _analysisRevision &&
           ReferenceEquals(owner, _document) && ReferenceEquals(buffer, Editor.Document) &&
           string.Equals(text, buffer.Text, StringComparison.Ordinal);

    private void CancelAnalysis()
    {
        _analysisRevision++;
        var cancellation = _analysisCancellation;
        _analysisCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void ClearPresentation(bool rememberFolds = true)
    {
        if (rememberFolds) CaptureFoldState();
        else _rememberedFolds.Clear();
        InvalidateReferences();
        _colorizer.SetTokens(Array.Empty<WmlEditorToken>());
        _foldingManager?.UpdateFoldings(Array.Empty<NewFolding>(), -1);
        Editor.TextArea.TextView.Redraw();
    }

    private void InvalidateReferences()
    {
        CloseReferencePopups();
        CurrentAnalysis = null;
        UpdateTagPairHighlight();
        CurrentReferenceGroups = Array.Empty<WmlEditorReferenceGroup>();
        ReferenceList.ItemsSource = Array.Empty<WmlEditorReferenceRow>();
        ToolTip.SetTip(ReferenceStatusText, null);
    }

    private void RefreshReferenceRows()
        => ReferenceList.ItemsSource = CurrentReferenceGroups.Select(group =>
            new WmlEditorReferenceRow(group, _referenceContext is not null)).ToArray();

    private void CloseReferencePreview()
    {
        ToolTip.SetIsOpen(Editor, false);
        ToolTip.SetTip(Editor, null);
    }

    private void CloseReferencePopups()
    {
        _pressedPointer = null;
        _pendingPointerSourceOpen = null;
        CloseReferencePreview();
        _sourceChoiceMenu?.Close();
        _sourceChoiceMenu = null;
    }

    private void CaptureFoldState()
    {
        if (_foldingManager is null) return;
        var folds = _foldingManager.AllFoldings.ToArray();
        // Detached views retain anchors until a complete fresh snapshot recreates their manager.
        if (folds.Length == 0) return;
        _rememberedFolds.Clear();
        foreach (var fold in folds.Where(fold => fold.IsFolded))
        {
            var start = Editor.Document.CreateAnchor(fold.StartOffset);
            start.MovementType = AnchorMovementType.AfterInsertion;
            var end = Editor.Document.CreateAnchor(fold.EndOffset);
            end.MovementType = AnchorMovementType.BeforeInsertion;
            _rememberedFolds.Add(new(start, end, fold.Title?.ToString() ?? string.Empty));
        }
    }

    private void RestoreFoldState()
    {
        if (_foldingManager is null) return;
        foreach (var fold in _foldingManager.AllFoldings)
        {
            var openingLineEnd = Editor.Document.GetLineByOffset(fold.StartOffset).EndOffset;
            var caret = Editor.CaretOffset;
            var remembered = _rememberedFolds.Any(remembered => !remembered.Start.IsDeleted && !remembered.End.IsDeleted &&
                remembered.Start.Offset == fold.StartOffset && remembered.End.Offset == fold.EndOffset &&
                string.Equals(remembered.Label, fold.Title?.ToString(), StringComparison.Ordinal));
            // A changed tag or preprocessor pair must not inherit a previous region's collapse
            // merely because FoldingManager reused a section at the same starting offset.
            fold.IsFolded = remembered && !(caret > openingLineEnd && caret < fold.EndOffset);
        }
        // Discard anchors for regions that no longer exist or no longer form the same balanced pair.
        _rememberedFolds.Clear();
        CaptureFoldState();
    }

    private void ReferenceClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: WmlEditorReferenceRow row }) NavigateReference(row.Group);
    }

    private void ReferenceSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: WmlEditorReferenceRow row } button ||
            CurrentAnalysis is null || !CurrentReferenceGroups.Contains(row.Group)) return;
        var occurrences = row.Group.Occurrences;
        var selected = occurrences.FirstOrDefault(reference => reference.StartOffset == Editor.SelectionStart &&
            reference.Length == Editor.SelectionLength);
        selected ??= occurrences.Where(reference => reference.StartOffset <= Editor.CaretOffset &&
                Editor.CaretOffset < reference.StartOffset + reference.Length)
            .OrderBy(reference => reference.Length).FirstOrDefault();
        if (selected is null)
        {
            var index = WmlEditorNavigation.FindNextReferenceIndex(row.Group,
                Editor.SelectionStart, Editor.SelectionLength);
            if (index >= 0) selected = occurrences[index];
        }
        if (selected is not null) OpenReferenceSource(selected, button);
    }

    private WmlEditorReference? ReferenceAtPoint(Point point)
    {
        if (CurrentAnalysis is null) return null;
        var position = Editor.GetPositionFromPoint(point);
        if (position is null) return null;
        var offset = Editor.Document.GetOffset(position.Value.Line, position.Value.Column);
        return CurrentAnalysis.References.Where(reference => reference.StartOffset <= offset &&
                offset < reference.StartOffset + reference.Length)
            .OrderBy(reference => reference.Length).FirstOrDefault();
    }

    private void EditorDoubleTapped(object? sender, TappedEventArgs args)
    {
        var reference = ReferenceAtPoint(args.GetPosition(Editor));
        if (reference is null || _pressedPointer is null) return;
        QueuePointerSourceOpen(reference, _pressedPointer);
        args.Handled = true;
    }

    private void EditorPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        _pendingPointerSourceOpen = null;
        _pressedPointer = args.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed ? args.Pointer : null;
    }

    private void EditorPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (args.InitialPressMouseButton == MouseButton.Left) ReleasePointerSourceOpen(args.Pointer);
        if (ReferenceEquals(args.Pointer, _pressedPointer)) _pressedPointer = null;
    }

    internal void QueuePointerSourceOpen(WmlEditorReference reference, IPointer pointer)
    {
        if (!_isAttached || _document is null || CurrentAnalysis is null ||
            !CurrentAnalysis.References.Contains(reference)) return;
        CloseReferencePopups();
        // Avalonia raises DoubleTapped on the second press, while AvaloniaEdit still holds
        // its whole-word selection gesture. Keep the pane attached until the matching release.
        _pendingPointerSourceOpen = new(reference, pointer, _document, Editor.Document,
            _referenceContext, _analysisRevision, Editor.Document.Text);
    }

    internal void ReleasePointerSourceOpen(IPointer pointer)
    {
        var pending = _pendingPointerSourceOpen;
        if (pending is null || !ReferenceEquals(pointer, pending.Pointer)) return;
        // This handler bubbles through Editor after TextArea's selection handler has consumed
        // PointerReleased and reset its mode/capture. Posting also lets the complete input route
        // finish before a host switches tabs and detaches this pane.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(pending, _pendingPointerSourceOpen)) return;
            _pendingPointerSourceOpen = null;
            if (!_isAttached || !ReferenceEquals(pending.Owner, _document) ||
                !ReferenceEquals(pending.Buffer, Editor.Document) || pending.Revision != _analysisRevision ||
                !ReferenceEquals(pending.Context, _referenceContext) ||
                !string.Equals(pending.Text, Editor.Document.Text, StringComparison.Ordinal)) return;
            OpenReferenceSource(pending.Reference);
        }, DispatcherPriority.Background);
    }

    private void EditorPointerHover(object? sender, PointerEventArgs args)
    {
        var reference = ReferenceAtPoint(args.GetPosition(Editor));
        var preview = reference is null ? null : PreviewReference(reference);
        if (preview is null)
        {
            CloseReferencePreview();
            return;
        }
        ToolTip.SetTip(Editor, new TextBlock
        {
            Text = preview, TextWrapping = TextWrapping.Wrap, MaxWidth = 720,
            FontFamily = Editor.FontFamily, FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#E6DDC5"))
        });
        ToolTip.SetIsOpen(Editor, true);
        args.Handled = true;
    }

    private IReadOnlyList<WmlEditorSourceTarget> ResolveReference(WmlEditorReference reference)
    {
        if (_referenceContext is null || CurrentAnalysis is null ||
            !CurrentAnalysis.References.Contains(reference)) return Array.Empty<WmlEditorSourceTarget>();
        return _referenceContext.Resolve(Editor.Document.Text, reference);
    }

    internal void OpenReferenceSource(WmlEditorReference reference) => OpenReferenceSource(reference, Editor);

    private void OpenReferenceSource(WmlEditorReference reference, Control anchor)
    {
        if (CurrentAnalysis is null || !CurrentAnalysis.References.Contains(reference)) return;
        CloseReferencePopups();
        var targets = ResolveReference(reference);
        if (targets.Count == 0)
        {
            ReferenceStatusText.Text = _referenceContext is null
                ? "No campaign source context is available for this reference."
                : $"No captured source found for {reference.Name}. It may require preprocessing or runtime context.";
            return;
        }
        if (targets.Count == 1)
        {
            SourceOpenRequested?.Invoke(this, targets[0]);
            return;
        }
        var context = _referenceContext;
        var buffer = Editor.Document;
        var revision = _analysisRevision;
        var text = buffer.Text;
        var menu = new ContextMenu();
        var choices = new List<MenuItem>();
        foreach (var target in targets)
        {
            var choice = new MenuItem
            {
                Header = $"{target.Title} - {EvidenceLabel(target.EvidenceKind)}\n{SourceLocation(target)}",
            };
            ToolTip.SetTip(choice, target.Description + "\n\nAuthored source preview (not expanded WML)\n" +
                SourceSnippet(target.Text, 8, 350));
            choice.Click += (_, _) =>
            {
                if (revision != _analysisRevision || !ReferenceEquals(context, _referenceContext) ||
                    !ReferenceEquals(buffer, Editor.Document) || !string.Equals(text, buffer.Text, StringComparison.Ordinal)) return;
                // A host session may change while this menu is open. Resolve again and require
                // the same source identity and evidence; a former active source cannot be guessed.
                var current = ResolveReference(reference).FirstOrDefault(candidate => candidate.Identity == target.Identity &&
                    candidate.EvidenceKind == target.EvidenceKind && string.Equals(candidate.Text, target.Text, StringComparison.Ordinal));
                if (current is not null) SourceOpenRequested?.Invoke(this, current);
            };
            choices.Add(choice);
        }
        menu.ItemsSource = choices;
        _sourceChoiceMenu = menu;
        ReferenceStatusText.Text = $"{targets.Count} source candidates. Choose the source to inspect.";
        menu.Open(anchor);
    }

    internal string? PreviewReference(WmlEditorReference reference)
    {
        var targets = ResolveReference(reference);
        if (targets.Count == 0) return null;
        if (targets.Count == 1)
        {
            var target = targets[0];
            return $"{target.Title} - {EvidenceLabel(target.EvidenceKind)}\n{SourceLocation(target)}\n{target.Description}\n\n" +
                "Authored source preview (not expanded WML)\n" + SourceSnippet(target.Text, 30, 1800);
        }
        return $"{targets.Count} source candidates; Open lets you choose. No unique definition is selected.\n" +
            "Authored source previews (not expanded WML)\n\n" + string.Join("\n\n", targets.Take(3).Select(target =>
                $"{EvidenceLabel(target.EvidenceKind)} - {SourceLocation(target)}\n{SourceSnippet(target.Text, 8, 350)}")) +
            (targets.Count > 3 ? "\n\nMore candidates are available through Open." : string.Empty);
    }

    private static string EvidenceLabel(WmlEditorReferenceEvidenceKind kind) => kind switch
    {
        WmlEditorReferenceEvidenceKind.CapturedActiveDefinition => "Captured active definition",
        WmlEditorReferenceEvidenceKind.CapturedAmbiguousDefinition => "Captured ambiguous definition",
        WmlEditorReferenceEvidenceKind.CapturedInclude => "Captured include",
        _ => "Static definition candidate"
    };

    private static string SourceLocation(WmlEditorSourceTarget target)
    {
        var source = target.Origin.Source;
        var hash = source.ContentHash[..Math.Min(8, source.ContentHash.Length)];
        if (!source.IsAuthored) return $"{source.Path} (definition body snapshot {hash})";
        var line = 1;
        for (var offset = 0; offset < target.Origin.StartOffset; offset++)
            if (source.Text[offset] == '\n') line++;
        return $"{source.Path}:{line} (snapshot {hash})";
    }

    private static string SourceSnippet(string text, int maxLines, int maxCharacters)
    {
        var end = Math.Min(text.Length, maxCharacters);
        var lines = 1;
        for (var offset = 0; offset < end; offset++)
            if (text[offset] == '\n' && ++lines > maxLines)
            {
                end = offset;
                break;
            }
        return text[..end] + (end < text.Length ? "\n..." : string.Empty);
    }

    internal void NavigateReference(WmlEditorReferenceGroup group)
    {
        if (CurrentAnalysis is null || !CurrentReferenceGroups.Contains(group)) return;
        var index = WmlEditorNavigation.FindNextReferenceIndex(group, Editor.SelectionStart, Editor.SelectionLength);
        if (index < 0) return;
        var occurrence = group.Occurrences[index];
        SelectSpan(occurrence.StartOffset, occurrence.Length, focusEditor: true);
    }

    private void SelectSpan(int requestedStart, int requestedLength, bool focusEditor)
    {
        var document = Editor.Document;
        var start = Math.Clamp(requestedStart, 0, document.TextLength);
        var length = Math.Clamp(requestedLength, 0, document.TextLength - start);
        var selectionEnd = start + length;
        _rememberedFolds.RemoveAll(fold => fold.Start.IsDeleted || fold.End.IsDeleted ||
            (fold.Start.Offset <= selectionEnd && fold.End.Offset > start));
        if (_foldingManager is not null)
        {
            var end = start + length;
            foreach (var fold in _foldingManager.AllFoldings)
                if (fold.StartOffset <= end && fold.EndOffset > start)
                    fold.IsFolded = false;
        }
        Editor.Select(start, length);
        Editor.CaretOffset = start + length;
        var position = document.GetLocation(start);
        Editor.ScrollTo(position.Line, position.Column);
        if (focusEditor) Editor.Focus();
        UpdateCaretStatus();
    }

    private void FindTextChanged(object? sender, TextChangedEventArgs args)
    {
        if (!_ready) return;
        ClearReplacementStatus();
        RebuildFindMatches(selectMatch: true);
    }

    private bool CanReplaceBuffer => _isAttached && _document is { IsReadOnly: false, IsApplying: false, IsCancelled: false }
        && !Editor.IsReadOnly && string.Equals(_document.Text, Editor.Document.Text, StringComparison.Ordinal);

    private void ShowReplaceClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanReplaceBuffer) return;
        if (ReplaceRow.IsVisible)
        {
            ReplaceRow.IsVisible = false;
            ShowReplaceButton.Content = "Replace...";
            ClearReplacementStatus();
        }
        else ShowReplacementRow();
    }

    private void ShowReplacementRow()
    {
        ReplaceRow.IsVisible = true;
        ShowReplaceButton.Content = "Hide replace";
        FindTextBox.Focus();
        FindTextBox.SelectAll();
        UpdateReplacementButtons();
    }

    private void ReplacementTextChanged(object? sender, TextChangedEventArgs args)
    {
        if (!_ready) return;
        ClearReplacementStatus();
        UpdateReplacementButtons();
    }

    private void ReplaceTextBoxKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            ReplaceMatches((args.KeyModifiers & KeyModifiers.Control) != 0);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            Editor.Focus();
            args.Handled = true;
        }
    }

    private void ReplaceOneClicked(object? sender, RoutedEventArgs args) => ReplaceMatches(all: false);
    private void ReplaceAllClicked(object? sender, RoutedEventArgs args) => ReplaceMatches(all: true);

    // Buffer editing only: each host still owns Save/Apply, context and admission.
    internal bool ReplaceMatches(bool all)
    {
        if (!CanReplaceBuffer) return false;
        var owner = _document!;
        var buffer = Editor.Document;
        var query = FindTextBox.Text ?? string.Empty;
        var replacement = ReplaceTextBox.Text ?? string.Empty;
        var plan = all ? WmlEditorReplacement.BuildAll(buffer.Text, query, replacement)
            : WmlEditorReplacement.BuildOne(buffer.Text, query, replacement, Editor.SelectionStart, Editor.SelectionLength);
        if (plan.ChangedCount == 0)
        {
            SetReplacementStatus(string.IsNullOrEmpty(query) ? "Enter text to find first."
                : _findMatches.Count == 0 ? "No matches in this tab." : "No text changes needed.");
            if (!all && plan.TargetStartOffset is { } unchanged)
            {
                _currentFindMatch = WmlEditorNavigation.FindMatchIndex(_findMatches, unchanged + plan.TargetLength);
                SelectCurrentFindMatch();
            }
            return false;
        }
        var caret = buffer.CreateAnchor(Editor.CaretOffset);
        caret.MovementType = AnchorMovementType.AfterInsertion;
        caret.SurviveDeletion = true;
        if (!ApplyPlannedChanges(owner, buffer, plan.SourceText, plan.Changes, () =>
                string.Equals(FindTextBox.Text ?? string.Empty, query, StringComparison.Ordinal) &&
                string.Equals(ReplaceTextBox.Text ?? string.Empty, replacement, StringComparison.Ordinal))) return false;
        if (!all && plan.TargetStartOffset is { } target)
        {
            _currentFindMatch = WmlEditorNavigation.FindMatchIndex(_findMatches, target + replacement.Length);
            SelectCurrentFindMatch();
        }
        else SelectSpan(caret.Offset, 0, focusEditor: false);
        SetReplacementStatus($"Replaced {plan.ChangedCount} {(plan.ChangedCount == 1 ? "match" : "matches")} in this tab. Undo restores this edit.");
        return true;
    }

    private void FormatWmlClicked(object? sender, RoutedEventArgs args) => FormatCurrentDocument();

    internal bool FormatCurrentDocument()
    {
        if (!CanReplaceBuffer) return false;
        var owner = _document!;
        var buffer = Editor.Document;
        var plan = WmlEditorFormatting.Build(buffer.Text);
        if (plan.Changes.Count == 0)
        {
            SetReplacementStatus("WML indentation is already formatted. Literal contents are preserved.");
            return false;
        }
        var caret = buffer.CreateAnchor(Editor.CaretOffset);
        caret.MovementType = AnchorMovementType.AfterInsertion;
        caret.SurviveDeletion = true;
        if (!ApplyPlannedChanges(owner, buffer, plan.SourceText, plan.Changes, () => true)) return false;
        SelectSpan(caret.Offset, 0, focusEditor: false);
        SetReplacementStatus($"Formatted {plan.ChangedLineCount} {(plan.ChangedLineCount == 1 ? "line" : "lines")}. Undo restores this edit.");
        return true;
    }

    // One mutation boundary for buffer tools. Neither tool can save or grant source access.
    private bool ApplyPlannedChanges(WmlEditorDocumentViewModel owner, TextDocument buffer, string sourceText,
        IReadOnlyList<WmlEditorReplacementChange> changes, Func<bool> intentIsCurrent)
    {
        var version = buffer.Version;
        var lastVersion = version;
        bool SameOwner() => _isAttached && ReferenceEquals(owner, _document) && ReferenceEquals(buffer, Editor.Document) &&
            !owner.IsReadOnly && !owner.IsApplying && !owner.IsCancelled && !Editor.IsReadOnly &&
            string.Equals(owner.Text, sourceText, StringComparison.Ordinal) && intentIsCurrent();
        using (buffer.RunUpdate())
        {
            if (!CanReplaceBuffer || !SameOwner() || !ReferenceEquals(version, buffer.Version) ||
                !string.Equals(buffer.Text, sourceText, StringComparison.Ordinal)) return false;
            for (var index = changes.Count - 1; index >= 0; index--)
            {
                // The model publishes once at EndUpdate; its original text stays captured here.
                if (!SameOwner() || !ReferenceEquals(lastVersion, buffer.Version)) return false;
                var change = changes[index];
                buffer.Replace(change.StartOffset, change.Length, change.ReplacementText);
                lastVersion = buffer.Version;
                if (!SameOwner()) return false;
            }
        }
        return CanReplaceBuffer && ReferenceEquals(owner, _document) && ReferenceEquals(buffer, Editor.Document) &&
               ReferenceEquals(lastVersion, buffer.Version) && intentIsCurrent();
    }

    private sealed class WmlIndentationStrategy(WmlCodeEditorView owner) : IIndentationStrategy
    {
        public void IndentLine(TextDocument document, DocumentLine line)
        {
            if (!owner._isAttached || !ReferenceEquals(document, owner.Editor.Document) || owner.Editor.IsReadOnly ||
                owner._document is not { IsReadOnly: false, IsApplying: false, IsCancelled: false }) return;
            var indentation = WmlEditorFormatting.GetLineIndent(document.Text, line.Offset);
            var leading = 0;
            while (leading < line.Length && document.GetCharAt(line.Offset + leading) is ' ' or '\t') leading++;
            if (indentation is null)
            {
                // Inside values/Lua/arguments, leave any existing literal text untouched.
                if (line.Length != 0 || line.PreviousLine is not { } previous) return;
                var previousLeading = 0;
                while (previousLeading < previous.Length && document.GetCharAt(previous.Offset + previousLeading) is ' ' or '\t') previousLeading++;
                indentation = document.GetText(previous.Offset, previousLeading);
            }
            if (document.GetText(line.Offset, leading) != indentation)
                document.Replace(line.Offset, leading, indentation);
        }

        public void IndentLines(TextDocument document, int beginLine, int endLine)
        {
            using (document.RunUpdate())
                for (var number = beginLine; number <= endLine; number++)
                    IndentLine(document, document.GetLineByNumber(number));
        }
    }

    private void UpdateReplacementButtons()
    {
        var enabled = CanReplaceBuffer && _findMatches.Count > 0;
        ReplaceOneButton.IsEnabled = ReplaceAllButton.IsEnabled = enabled;
        ReplaceAllButton.Content = _findMatches.Count == 0 ? "Replace all" : $"Replace all ({_findMatches.Count})";
    }

    private void ClearReplacementStatus() => ReplacementStatusText.IsVisible = false;
    private void SetReplacementStatus(string message)
    {
        ReplacementStatusText.Text = message;
        ReplacementStatusText.IsVisible = true;
    }

    private void FindPreviousClicked(object? sender, RoutedEventArgs args) => MoveToFindMatch(-1);
    private void FindNextClicked(object? sender, RoutedEventArgs args) => MoveToFindMatch(1);

    private void FindTextBoxKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            MoveToFindMatch((args.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            Editor.Focus();
            args.Handled = true;
        }
    }

    private void RebuildFindMatches(bool selectMatch)
    {
        var query = FindTextBox.Text ?? string.Empty;
        _findMatches = WmlEditorNavigation.FindMatches(Editor.Document.Text, query);
        _currentFindMatch = selectMatch
            ? WmlEditorNavigation.FindMatchIndex(_findMatches, Editor.SelectionStart) : -1;
        if (selectMatch && _currentFindMatch >= 0)
            SelectCurrentFindMatch();
        else
            UpdateFindStatus();
    }

    private void MoveToFindMatch(int direction)
    {
        _currentFindMatch = WmlEditorNavigation.MoveMatchIndex(_findMatches, _currentFindMatch,
            Editor.SelectionStart, direction);
        SelectCurrentFindMatch();
    }

    private void SelectCurrentFindMatch()
    {
        if (_currentFindMatch >= 0 && _currentFindMatch < _findMatches.Count)
            SelectSpan(_findMatches[_currentFindMatch], FindTextBox.Text?.Length ?? 0, focusEditor: false);
        UpdateFindStatus();
    }

    private void UpdateFindStatus()
    {
        var hasQuery = !string.IsNullOrEmpty(FindTextBox.Text);
        FindPreviousButton.IsEnabled = FindNextButton.IsEnabled = _findMatches.Count > 0;
        FindStatusTextBlock.Text = !hasQuery ? "Type to find"
            : _findMatches.Count == 0 ? "No matches"
            : _currentFindMatch < 0 ? $"{_findMatches.Count} matches"
            : $"{_currentFindMatch + 1} of {_findMatches.Count}";
        UpdateReplacementButtons();
    }

    private sealed class WmlTagPairRenderer : IBackgroundRenderer
    {
        private WmlEditorTagOccurrence? _tag;
        private WmlEditorTagOccurrence? _matching;
        private static readonly IBrush MatchedFill = new SolidColorBrush(Color.Parse("#544735"));
        private static readonly IBrush UnmatchedFill = new SolidColorBrush(Color.Parse("#4B292C"));
        private static readonly IPen MatchedOutline = new Pen(new SolidColorBrush(Color.Parse("#BD9756")), 1);
        private static readonly IPen UnmatchedOutline = new Pen(new SolidColorBrush(Color.Parse("#C27474")), 1);
        public KnownLayer Layer => KnownLayer.Background;

        public void SetTags(WmlEditorTagOccurrence? tag, WmlEditorTagOccurrence? matching)
        { _tag = tag; _matching = matching; }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid || _tag is null || textView.Document is null) return;
            DrawTag(_tag);
            if (_matching is not null) DrawTag(_matching);
            void DrawTag(WmlEditorTagOccurrence tag)
            {
                if (tag.StartOffset < 0 || tag.Length <= 0 ||
                    tag.StartOffset > textView.Document.TextLength - tag.Length) return;
                var geometry = new BackgroundGeometryBuilder
                { CornerRadius = 2, AlignToWholePixels = true, BorderThickness = 1 };
                geometry.AddSegment(textView, new SimpleSegment(tag.StartOffset, tag.Length));
                if (geometry.CreateGeometry() is { } shape)
                    drawingContext.DrawGeometry(_matching is null ? UnmatchedFill : MatchedFill,
                        _matching is null ? UnmatchedOutline : MatchedOutline, shape);
            }
        }
    }

    private sealed class WmlTokenColorizer : DocumentColorizingTransformer
    {
        private IReadOnlyList<WmlEditorToken> _tokens = Array.Empty<WmlEditorToken>();
        private static readonly IBrush MajorTagBrush = new SolidColorBrush(Color.Parse("#F2D28C"));
        private static readonly HashSet<string> MajorTags = new(StringComparer.Ordinal)
        {
            "campaign", "scenario", "side", "unit_type", "event", "unit", "leader"
        };
        private static readonly IReadOnlyDictionary<WmlEditorTokenKind, IBrush> Brushes =
            new Dictionary<WmlEditorTokenKind, IBrush>
            {
                [WmlEditorTokenKind.Tag] = new SolidColorBrush(Color.Parse("#D5AE70")),
                [WmlEditorTokenKind.AttributeName] = new SolidColorBrush(Color.Parse("#B8D0E2")),
                [WmlEditorTokenKind.String] = new SolidColorBrush(Color.Parse("#AACBA0")),
                [WmlEditorTokenKind.Comment] = new SolidColorBrush(Color.Parse("#7D8D99")),
                [WmlEditorTokenKind.Directive] = new SolidColorBrush(Color.Parse("#B6A0DF")),
                [WmlEditorTokenKind.MacroInvocation] = new SolidColorBrush(Color.Parse("#F0CF86")),
                [WmlEditorTokenKind.Number] = new SolidColorBrush(Color.Parse("#DFB78F")),
                [WmlEditorTokenKind.RawCode] = new SolidColorBrush(Color.Parse("#AFB3C9"))
            };

        public void SetTokens(IReadOnlyList<WmlEditorToken> tokens) => _tokens = tokens;

        public void ApplyDocumentChange(DocumentChangeEventArgs change)
        {
            if (_tokens.Count == 0) return;
            var removedEnd = change.Offset + change.RemovalLength;
            var delta = change.InsertionLength - change.RemovalLength;
            var updated = new List<WmlEditorToken>(_tokens.Count);
            foreach (var token in _tokens)
            {
                var tokenEnd = token.StartOffset + token.Length;
                if (tokenEnd <= change.Offset)
                    updated.Add(token);
                else if (token.StartOffset >= removedEnd)
                    updated.Add(token with { StartOffset = token.StartOffset + delta });
                else
                {
                    // Keep surviving parts of a touched token. A wholly replaced token has no
                    // reliable display classification and waits for the debounced fresh snapshot.
                    var start = token.StartOffset < change.Offset
                        ? token.StartOffset : change.Offset + change.InsertionLength;
                    var end = tokenEnd > removedEnd ? tokenEnd + delta : change.Offset;
                    if (end > start) updated.Add(token with { StartOffset = start, Length = end - start });
                }
            }
            _tokens = updated;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var start = line.Offset;
            var end = line.EndOffset;
            var low = 0;
            var high = _tokens.Count;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                var token = _tokens[middle];
                if (token.StartOffset + token.Length <= start) low = middle + 1;
                else high = middle;
            }
            for (var index = low; index < _tokens.Count && _tokens[index].StartOffset < end; index++)
            {
                var token = _tokens[index];
                var tokenStart = Math.Max(start, token.StartOffset);
                var tokenEnd = Math.Min(end, token.StartOffset + token.Length);
                if (tokenStart < tokenEnd && Brushes.TryGetValue(token.Kind, out var brush))
                {
                    var majorTag = token.Kind == WmlEditorTokenKind.Tag && IsMajorTag(token);
                    ChangeLinePart(tokenStart, tokenEnd, element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(majorTag ? MajorTagBrush : brush);
                        if (majorTag)
                        {
                            var typeface = element.TextRunProperties.Typeface;
                            element.TextRunProperties.SetTypeface(new Typeface(typeface.FontFamily,
                                typeface.Style, FontWeight.Bold, typeface.Stretch));
                        }
                    });
                }
            }
        }

        private bool IsMajorTag(WmlEditorToken token)
        {
            if (token.Length < 3 || token.StartOffset < 0 ||
                token.StartOffset > CurrentContext.Document.TextLength - token.Length) return false;
            var text = CurrentContext.Document.GetText(token.StartOffset, token.Length);
            return text[0] == '[' && text[^1] == ']' && MajorTags.Contains(text[1..^1].TrimStart('/', '+'));
        }
    }

    private sealed record RememberedFold(TextAnchor Start, TextAnchor End, string Label);

    private sealed record PendingPointerSourceOpen(WmlEditorReference Reference, IPointer Pointer,
        WmlEditorDocumentViewModel Owner, TextDocument Buffer, WmlEditorReferenceContext? Context,
        long Revision, string Text);
}

public sealed record WmlEditorReferenceRow(WmlEditorReferenceGroup Group, bool CanOpenSource = false)
{
    public string Name => Group.Name;
    public string KindMark => Group.Kind switch
    {
        WmlEditorReferenceKind.Macro => "M",
        WmlEditorReferenceKind.Include => "I",
        _ => "?"
    };
    public string CountText => Group.Count.ToString();
    public string ToolTipText
    {
        get
        {
            var kind = Group.Kind switch
            {
                WmlEditorReferenceKind.Macro => "Macro invocation",
                WmlEditorReferenceKind.Include => "External include",
                _ => "Computed reference"
            };
            var raw = Group.Occurrences[0].RawText;
            if (raw.Length > 1200) raw = raw[..1200] + "...";
            return $"{kind}\n{Group.Name}\n{Group.Count} occurrence(s) in this text\n\n{raw}";
        }
    }
}
