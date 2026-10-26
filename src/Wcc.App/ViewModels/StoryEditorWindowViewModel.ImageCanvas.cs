using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed partial class StoryEditorWindowViewModel
{
    private static readonly string[] ImagePlacementKeys = ["file", "x", "y", "centered", "resize_with_background"];
    private readonly Dictionary<StoryChildVm, Dictionary<string, string>> _imagePlacementFingerprints = [];
    private bool _addingImageCanvas;
    private readonly Dictionary<StoryPartVm, HashSet<string>> _unavailableImageCanvases = [];
    private string? _imageCanvasProblem => _unavailableImageCanvases.Any(pair => Parts.Contains(pair.Key) && pair.Value.Any(define =>
        pair.Key.AvailableDefines.Contains(define) && pair.Key.Images.Any(image => image.AvailableDefines.Contains(define)) &&
        NewImageBase(pair.Key, define) is { IsProven: true } basis && string.IsNullOrWhiteSpace(basis.Expression)))
        ? "Choose a background layer as the image base. This captured slide cannot receive a local transparent background." : null;

    private static string ImagePlacementFingerprint(StoryChildVm image, string define) =>
        string.Join("\0", ImagePlacementKeys.Select(key => image.Field(key).Raw(define) ?? "\u0001"));

    private void InitializeImageCanvasTracking()
    {
        foreach (var image in Parts.SelectMany(part => part.Images))
            _imagePlacementFingerprints[image] = image.AvailableDefines.ToDictionary(define => define,
                define => ImagePlacementFingerprint(image, define), StringComparer.Ordinal);
    }

    private void EnsureImageCoordinateCanvases()
    {
        if (_addingImageCanvas || !CanEdit) return;
        _addingImageCanvas = true;
        try
        {
            var current = Parts.SelectMany(part => part.Images).ToHashSet();
            foreach (var removed in _imagePlacementFingerprints.Keys.Where(image => !current.Contains(image)).ToArray()) _imagePlacementFingerprints.Remove(removed);
            foreach (var part in Parts.Where(part => part.CanEdit && !part.IsExpandedPreview).ToArray())
            {
                var needed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var image in part.Images.Where(image => image.CanEdit && !image.IsExpandedPreview).ToArray())
                {
                    if (!_imagePlacementFingerprints.TryGetValue(image, out var previous)) _imagePlacementFingerprints[image] = previous = new(StringComparer.Ordinal);
                    foreach (var define in image.AvailableDefines.Where(part.AvailableDefines.Contains).ToArray())
                    {
                        var fingerprint = ImagePlacementFingerprint(image, define);
                        var changed = !previous.TryGetValue(define, out var old) || old != fingerprint;
                        previous[define] = fingerprint; // Consume before callbacks; deleting the layer does not recreate it.
                        if (!changed || image.MacroName is null && string.IsNullOrWhiteSpace(image.Field("file").Decoded(define))) continue;
                        var basis = NewImageBase(part, define);
                        if (basis.IsProven && string.IsNullOrWhiteSpace(basis.Expression)) needed.Add(define);
                    }
                }
                if (needed.Count == 0) continue;
                if (!CanAddLocalBackgroundLayers)
                { if (!_unavailableImageCanvases.TryGetValue(part, out var blocked)) _unavailableImageCanvases[part] = blocked = []; blocked.UnionWith(needed); continue; }
                if (!CanEdit || !Parts.Contains(part)) break;
                var layer = new StoryChildVm("background_layer", null, Session.Snapshot.Contexts.Select(context => context.Define), () => Scope,
                    () => { part.Refresh(); Changed(); }, pending: new WccScenarioStoryChildSpec
                    {
                        NewChildId = Guid.NewGuid().ToString("N"), AvailableDefines = needed.ToArray(),
                        Fields = new Dictionary<string, WccScenarioStoryFieldSpec>
                        {
                            ["image"] = new() { IsTouched = true, SharedValue = "\"" + WccStoryImageCanvas.ImageReference + "\"" },
                            ["base_layer"] = new() { IsTouched = true, SharedValue = "yes" },
                            ["scale"] = new() { IsTouched = true, SharedValue = "yes" },
                            ["keep_aspect_ratio"] = new() { IsTouched = true, SharedValue = "no" }
                        }
                    }, displayContext: () => PreviewDefine);
                foreach (var define in needed.Where(define => part.BackgroundLayers.Any(old => old.IsExpandedPreview && old.AvailableDefines.Contains(define))))
                    layer.OrderByDefine[define] = part.BackgroundLayers.Count(old => old.AvailableDefines.Contains(define));
                part.BackgroundLayers.Add(layer); part.Refresh();
            }
        }
        finally { _addingImageCanvas = false; }
    }
}
