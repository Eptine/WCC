using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

public sealed partial class ImageEditorWindowViewModel
{
    private const int MaximumHistoryEntries = 100;
    private readonly List<EditorHistoryState> _undoHistory = [];
    private readonly List<EditorHistoryState> _redoHistory = [];
    private EditorHistoryState? _historyCurrent;
    private EditorHistoryState? _gestureBaseline;
    private ImageEditorModifierVm? _gestureOwner;
    private bool _gestureHadCurrentInput;
    private int _gestureDepth;
    private readonly List<bool> _gesturePauseRequests = [];
    private int _gesturePauseDepth;
    private bool _restoringHistory;

    public bool IsInteracting => _gestureDepth > 0;
    public bool IsPreviewPaused => _gesturePauseDepth > 0;
    private bool HasPendingGestureChange => IsInteracting && _gestureBaseline is { } baseline && !SameHistoryState(baseline, CaptureHistoryState());
    public bool CanUndo => CanEdit && !IsPreviewPaused && (_undoHistory.Count > 0 || HasPendingGestureChange);
    public bool CanRedo => CanEdit && !IsPreviewPaused && !HasPendingGestureChange && _redoHistory.Count > 0;
    internal bool IsInteractionCurrent => IsPreviewPaused && _gestureHadCurrentInput && CanEdit && ReferenceEquals(_gestureOwner, _selected);

    private sealed record EditorHistoryState(WccImageFunctionDocument Document, WccImageFunctionStep[] Rows,
        bool UsePriority, KeyValuePair<string, string>[] TemporaryImages, int SelectedIndex);

    private EditorHistoryState CaptureHistoryState() => new(_document, Modifiers.Select(row => row.ToStep()).ToArray(),
        _useLayerPriority, _temporaryImages.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        _selected is null ? -1 : Modifiers.IndexOf(_selected));

    private static bool SameHistoryState(EditorHistoryState left, EditorHistoryState right) =>
        left.Document.WithSteps([]).BuildExpression() == right.Document.WithSteps([]).BuildExpression() &&
        left.UsePriority == right.UsePriority && left.Rows.SequenceEqual(right.Rows) &&
        left.TemporaryImages.SequenceEqual(right.TemporaryImages);

    private void ResetHistory()
    {
        _undoHistory.Clear(); _redoHistory.Clear(); _gestureDepth = 0; _gesturePauseDepth = 0; _gesturePauseRequests.Clear(); _gestureBaseline = null; _gestureOwner = null;
        _historyCurrent = CaptureHistoryState(); RaiseHistory();
    }
    private void RefreshHistorySelection()
    { if (_historyCurrent is not null) _historyCurrent = _historyCurrent with { SelectedIndex = _selected is null ? -1 : Modifiers.IndexOf(_selected) }; }
    private void RecordHistory()
    {
        if (_restoringHistory) return;
        var next = CaptureHistoryState();
        if (_historyCurrent is not { } previous) { _historyCurrent = next; return; }
        if (!SameHistoryState(previous, next))
        {
            if (!IsInteracting) { PushHistory(_undoHistory, previous); _redoHistory.Clear(); }
            _historyCurrent = next;
        }
        RaiseHistory();
    }
    private static void PushHistory(List<EditorHistoryState> stack, EditorHistoryState state)
    {
        stack.Add(state);
        if (stack.Count > MaximumHistoryEntries) stack.RemoveAt(0);
    }
    private void RaiseHistory()
    {
        OnPropertyChanged(nameof(IsInteracting)); OnPropertyChanged(nameof(IsPreviewPaused)); OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));
    }

    public void BeginEditGesture(bool pausePreview = true)
    {
        if (!CanEdit || _restoringHistory) return;
        var alreadyInteracting = IsInteracting;
        _gesturePauseRequests.Add(pausePreview); _gestureDepth++;
        if (pausePreview && _gesturePauseDepth++ == 0)
            _gestureHadCurrentInput = _previewGeneration == _generation && _inputComplete;
        if (!alreadyInteracting)
        { _gestureBaseline = _historyCurrent ?? CaptureHistoryState(); _gestureOwner = _selected; }
        // Stop old workers and revoke review immediately, while keeping displayed pixels.
        Invalidate(); RaiseHistory(); Raise();
        if (!IsPreviewPaused) SchedulePreview();
    }
    public void EndEditGesture()
    {
        if (_gestureDepth == 0) return;
        var wasPaused = IsPreviewPaused;
        if (_gesturePauseRequests[^1]) _gesturePauseDepth--;
        _gesturePauseRequests.RemoveAt(_gesturePauseRequests.Count - 1);
        if (--_gestureDepth > 0)
        { RaiseHistory(); if (wasPaused && !IsPreviewPaused) { Raise(); SchedulePreview(); } return; }
        CompleteGesture();
    }
    private void FinishEditGesture()
    {
        if (!IsInteracting) return;
        _gestureDepth = 0; _gesturePauseDepth = 0; _gesturePauseRequests.Clear(); CompleteGesture();
    }
    private void CompleteGesture()
    {
        var current = CaptureHistoryState();
        if (_gestureBaseline is { } baseline && !SameHistoryState(baseline, current))
        { PushHistory(_undoHistory, baseline); _redoHistory.Clear(); }
        _historyCurrent = current; _gestureBaseline = null; _gestureOwner = null; _gestureHadCurrentInput = false;
        RaiseHistory(); Raise(); SchedulePreview();
    }
    public void CancelEditGesture()
    {
        if (!IsInteracting) return;
        var baseline = _gestureBaseline; _gestureDepth = 0; _gesturePauseDepth = 0; _gesturePauseRequests.Clear(); _gestureBaseline = null; _gestureOwner = null; _gestureHadCurrentInput = false;
        if (baseline is not null) RestoreHistoryState(baseline);
        else { RaiseHistory(); Raise(); SchedulePreview(); }
    }
    public void Undo()
    {
        if (!CanUndo) return;
        FinishEditGesture();
        var state = _undoHistory[^1]; _undoHistory.RemoveAt(_undoHistory.Count - 1);
        PushHistory(_redoHistory, CaptureHistoryState()); RestoreHistoryState(state);
    }
    public void Redo()
    {
        if (!CanRedo) return;
        FinishEditGesture();
        var state = _redoHistory[^1]; _redoHistory.RemoveAt(_redoHistory.Count - 1);
        PushHistory(_undoHistory, CaptureHistoryState()); RestoreHistoryState(state);
    }
    private void RestoreHistoryState(EditorHistoryState state)
    {
        _restoringHistory = true;
        try
        {
            Invalidate(); _useLayerPriority = state.UsePriority;
            _temporaryImages.Clear(); foreach (var pair in state.TemporaryImages) _temporaryImages.Add(pair.Key, pair.Value);
            _document = state.Document.WithSteps(state.Rows.Reverse());
            LoadRows(state.SelectedIndex);
            _historyCurrent = CaptureHistoryState();
        }
        finally { _restoringHistory = false; }
        RaiseHistory(); Raise(); SchedulePreview();
    }
}
