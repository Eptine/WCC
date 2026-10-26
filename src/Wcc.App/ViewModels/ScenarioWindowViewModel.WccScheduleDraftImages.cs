using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private IReadOnlyDictionary<int, CatalogPickerOptionVm> _pendingScheduleImagePreviews =
        new Dictionary<int, CatalogPickerOptionVm>();

    // The operation supplies the original slot positions. Repeated IDs and
    // identical time macros never establish an image's physical origin.
    private bool StageWccScheduleEditKeepingImages(ScenarioInitialScheduleEdit edit,
        IReadOnlyList<CatalogPickerOptionVm?> images, int? selectedIndex = null)
    {
        if (!StageWccScheduleEdit(edit, selectedIndex)) return false;
        _pendingScheduleImagePreviews = (_scheduleUsesCapturedAuthoring ? _wccSchedulePreview?.Times ?? [] : [])
            .Where(time => time.Index >= 0 && time.Index < images.Count && images[time.Index] is { } image &&
                image.PreviewImageExpression is { } expression && expression == time.Image)
            .ToDictionary(time => time.Index, time => images[time.Index]!);
        _scheduleImagePreviewContext = null;
        RefreshWccScheduleImagePreviews();
        return true;
    }

    private CatalogPickerOptionVm? ScheduleImageAtSourceIndex(int index) =>
        _wccScheduleTimes.FirstOrDefault(row => row.Time.Index == index)?.Preview;

    private bool IsCurrentCapturedScheduleImageContext(CapturedScheduleDisplayContext context) =>
        context == CaptureScheduleDisplayContext() || HasPendingWccScheduleEdit && _scheduleUsesCapturedAuthoring &&
        context.Inspection is null && context.Generation == _staticInitialValuesGeneration &&
        context.Context == _evaluationContext.SemanticKey &&
        ReferenceEquals(context.Projection, _musicScheduleCapturedProjection) &&
        ReferenceEquals(context.Projection, CurrentMusicScheduleAuthoringProjection) &&
        ReferenceEquals(context.Images, _wccCapturedImageResources) &&
        _musicScheduleSourceBinding is { } binding && GetWccMusicScheduleBindingProblem(binding) is null;
}
