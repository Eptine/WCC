using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed partial class StoryEditorWindowViewModel
{
    public bool CanDuplicatePart => DuplicatePartProblem is null;
    public string? DuplicatePartProblem
    {
        get
        {
            if (!CanEdit) return Session.CurrentProblem ?? Session.Snapshot.Diagnostic ?? "This story is read-only.";
            if (!CanEditTopology) return "This story contains macro-supplied content whose slide topology is read-only.";
            if (SelectedPart is not { } selected || !Parts.Contains(selected)
                || Scope is { } define && !selected.AvailableDefines.Contains(define)) return "Select an available slide to duplicate.";
            if (!selected.CanEdit || selected.IsExpandedPreview) return "This inherited slide must retain its authored macro source.";
            if (selected.Fields.Concat(selected.Images.Concat(selected.BackgroundLayers).SelectMany(child => child.Fields))
                .Select(@field => @field.InputProblem).FirstOrDefault(problem => problem is not null) is { } inputProblem) return inputProblem;
            if (selected.Fields.Any(@field => !@field.CanEdit) || selected.Images.Concat(selected.BackgroundLayers).Any(child =>
                !child.CanEdit || child.IsExpandedPreview || child.Fields.Any(@field => (child.MacroName is null || @field.Key is "x" or "y") && !@field.CanEdit)))
                return "This slide contains inherited fields or images which cannot be copied as local settings.";
            return selected.Id is { } id ? WccScenarioStoryPlanner.PartDuplicationProblem(Session.Snapshot, id) : null;
        }
    }

    public void DuplicateSelectedPart()
    {
        if (DuplicatePartProblem is { } problem) { Status = problem; return; }
        var original = SelectedPart!;
        var defines = Session.Snapshot.Contexts.Select(context => context.Define).ToArray();
        var available = original.AvailableDefines.Where(define => Scope is null || Scope == define).ToArray();
        var before = available.ToDictionary(define => define, PartsForDefine, StringComparer.Ordinal);
        var newPartId = Guid.NewGuid().ToString("N");
        var childIds = new Dictionary<string, string>(StringComparer.Ordinal);
        WccScenarioStoryChildSpec[] CopyChildren(IEnumerable<StoryChildVm> children) => children
            .Where(child => child.AvailableDefines.Intersect(available, StringComparer.Ordinal).Any())
            .Select(child =>
            {
                var id = Guid.NewGuid().ToString("N"); childIds.Add(child.LogicalId, id);
                var childDefines = child.AvailableDefines.Intersect(available, StringComparer.Ordinal).ToArray();
                return new WccScenarioStoryChildSpec
                {
                    NewChildId = id, TravelMacroName = child.MacroName, AvailableDefines = childDefines,
                    OrderByDefine = childDefines.ToDictionary(define => define,
                        define => child.OrderByDefine.GetValueOrDefault(define, children.ToList().IndexOf(child)), StringComparer.Ordinal),
                    Fields = child.Fields.Where(field => child.MacroName is null || field.Key is "x" or "y")
                        .ToDictionary(field => field.Key, field => field.ToSpec() with { IsTouched = true }, StringComparer.Ordinal)
                };
            }).ToArray();
        var spec = new WccScenarioStoryPartSpec
        {
            NewPartId = newPartId, AvailableDefines = available,
            Fields = original.Fields.ToDictionary(field => field.Key, field => field.ToSpec() with { IsTouched = true }, StringComparer.Ordinal),
            Images = CopyChildren(original.Images), BackgroundLayers = CopyChildren(original.BackgroundLayers)
        };
        var duplicate = new StoryPartVm(defines, () => Scope, Changed, pending: spec, displayContext: () => PreviewDefine);
        var copiedUploads = new List<WccScenarioStoryUploadReceipt>();
        foreach (var upload in _uploads.Where(upload => upload.Slot.PartId == original.LogicalId))
        {
            var child = upload.Slot.ChildId is { } childId
                ? original.Images.Concat(original.BackgroundLayers).FirstOrDefault(child => child.LogicalId == childId) : null;
            if (upload.Slot.ChildId is not null && (child is null || !childIds.ContainsKey(child.LogicalId))) continue;
            var field = child is null ? original.Field(upload.Slot.FieldKey) : child.Field(upload.Slot.FieldKey);
            if (!available.Any(define => (upload.Slot.DifficultyDefine is null || upload.Slot.DifficultyDefine == define)
                && (child is null || child.AvailableDefines.Contains(define))
                && WccScenarioStoryAudioValues.ReferencesUpload(field.Raw(define), upload))) continue;
            var slot = upload.Slot with
            {
                PartId = newPartId, ChildId = child is null ? null : childIds[child.LogicalId],
                DifficultyDefine = Scope ?? upload.Slot.DifficultyDefine,
                EntryId = upload.Slot.EntryId is null ? null : Guid.NewGuid().ToString("N")
            };
            copiedUploads.Add(WccScenarioStoryPlanner.CopyUploadForSlot(upload, slot));
        }

        _uploads.AddRange(copiedUploads);
        // A duplicate begins with its copied coordinates; it is not a newly
        // placed image requiring a different canvas or automatic recentering.
        foreach (var image in duplicate.Images)
            _imagePlacementFingerprints[image] = image.AvailableDefines.ToDictionary(define => define,
                define => ImagePlacementFingerprint(image, define), StringComparer.Ordinal);
        Parts.Insert(Parts.IndexOf(original) + 1, duplicate);
        foreach (var define in available)
        {
            var ordered = before[define].ToList(); ordered.Insert(ordered.IndexOf(original) + 1, duplicate);
            SetPartOrders(define, ordered);
        }
        foreach (var source in original.Images.Where(image => _newImageCenters.ContainsKey(image)))
        {
            if (!childIds.TryGetValue(source.LogicalId, out var id) || duplicate.Images.FirstOrDefault(image => image.LogicalId == id) is not { } image) continue;
            var pendingDefines = _newImageCenters[source].Defines.ToArray();
            TrackNewImageCenter(duplicate, image);
            if (_newImageCenters.TryGetValue(image, out var seed))
            {
                seed.Defines.IntersectWith(pendingDefines);
                if (seed.Defines.Count == 0) ForgetNewImageCenter(seed);
            }
        }
        SelectedPart = duplicate; Changed();
    }
}
