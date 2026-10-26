using System;
using System.IO;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Preview;
using Wcc.Core.Library;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private WccCapturedScenarioSchedulePreview? _capturedSchedulePreview;
    private CapturedScheduleDisplayContext? _capturedScheduleDisplayContext;
    private bool _usesCapturedScheduleDisplay;
    private sealed record CapturedScheduleDisplayContext(ScenarioDocument Document, WmlStaticScalarProjection Projection,
        WmlStaticScalarOwner Owner, WmlScenarioInspectionResult? Inspection, long Generation, string Context,
        string Path, string Text, Lazy<WccCapturedImageResourceContext>? Images);

    private ScenarioInitialScheduleProjection? DisplayedWccSchedule => _usesCapturedScheduleDisplay
        ? _capturedSchedulePreview?.Schedule : _wccSchedulePreview;

    private bool HasPendingWccScheduleEdit => _wccScheduleEdit.Kind != ScenarioInitialScheduleEditKind.Preserve ||
        _wccScheduleEdit.Start is { Kind: not ScenarioInitialScheduleStartKind.Preserve };

    private CapturedScheduleDisplayContext? CaptureScheduleDisplayContext() => !HasPendingWccScheduleEdit &&
        !_hasAuthoredSourceChanges && !_externalSourceChangesPending && HasCurrentStaticScalarProjection &&
        SelectedScenario is { } selected && _staticScenarioTopologyProjection is { } projection &&
        _staticScenarioOwner is { } owner && (_currentSourcePath ?? LoadedPath) is { } path &&
        _pristineLoadedSourceText is { } text && selected.Document.OriginalText == text &&
        _currentScenarioAsset?.RawTextSnapshot == text
        ? new(selected.Document, projection, owner, _publishedScenarioInspection, _staticInitialValuesGeneration,
            _evaluationContext.SemanticKey, path, text, _wccCapturedImageResources) : null;

    private void RefreshCapturedScheduleDisplay()
    {
        var context = CaptureScheduleDisplayContext();
        if (_capturedScheduleDisplayContext == context) return;
        var oldDisplay = DisplayedWccSchedule;
        _capturedScheduleDisplayContext = context;
        _capturedSchedulePreview = null;
        _usesCapturedScheduleDisplay = false;
        if (context is not null)
        {
            var range = new WmlStaticSourceRange(new(context.Path, context.Text), context.Document.BlockSpan.StartIndex,
                context.Document.BlockSpan.EndIndexExclusive - context.Document.BlockSpan.StartIndex);
            _capturedSchedulePreview = context.Inspection is { IsComplete: true } inspection
                ? WccCapturedScenarioSchedulePreview.Create(range, inspection)
                : WccCapturedScenarioSchedulePreview.Create(range, context.Projection);
            // Ordinary complete, matching authored schedules keep all existing
            // editing and draft behavior. Different captured topology is display only.
            _usesCapturedScheduleDisplay = _capturedSchedulePreview is { } captured &&
                (_wccSchedulePreview?.CanCompose != true || !SameScheduleTimeline(captured.Schedule, _wccSchedulePreview));
        }
        if (!HasPendingWccScheduleEdit && !ReferenceEquals(oldDisplay, DisplayedWccSchedule)) RebuildWccScheduleTimeRows();
    }

    private static bool SameScheduleTimeline(ScenarioInitialScheduleProjection left, ScenarioInitialScheduleProjection right) =>
        left.StartIsKnown == right.StartIsKnown && left.StartIndex == right.StartIndex && left.IsRandomStart == right.IsRandomStart &&
        left.RandomStartIndices.SequenceEqual(right.RandomStartIndices) && left.Times.Select(TimeIdentity).SequenceEqual(right.Times.Select(TimeIdentity));

    private static (string?, string?, string?, string?, string?, string?, string?) TimeIdentity(ScenarioInitialTimeSlot time) =>
        (time.Id, time.Name, time.Image, time.LawfulBonus, time.Red, time.Green, time.Blue);

    private CatalogPickerOptionVm? CapturedScheduleImage(ScenarioInitialTimeSlot time)
    {
        if (_capturedSchedulePreview is not { } captured || _capturedScheduleDisplayContext is not { Images: { } images } context ||
            captured.Schedule.Times.ElementAtOrDefault(time.Index) is not { } slot || TimeIdentity(slot) != TimeIdentity(time)) return null;
        return new()
        {
            ValueId = slot.Id ?? "", DisplayName = slot.Name ?? slot.Id ?? "",
            PreviewImageExpression = slot.Image,
            PreviewHint = "Initial time in the selected captured context.\n" + slot.Image +
                BitmapFileLoader.ImageExpressionPreviewHint(slot.Image),
            PreviewPathResolver = () =>
            {
                if (!IsCurrentCapturedScheduleImageContext(context)) return null;
                try
                {
                    var sources = captured.ImageSources.ElementAtOrDefault(time.Index);
                    if (sources is null || sources.Count == 0 || sources.Any(source => !source.IsAuthored ||
                        WccContentHash.ComputeText(File.ReadAllText(source.Path)) != source.ContentHash)) return null;
                    return IsCurrentCapturedScheduleImageContext(context) ? images.Value.Resolve(slot.Image).ResolvedAbsolutePath : null;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                { return null; }
            }
        };
    }
}
