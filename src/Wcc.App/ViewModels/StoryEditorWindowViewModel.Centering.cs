using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed partial class StoryEditorWindowViewModel
{
    private static readonly StoryPreviewSize NewImageCanvas = new(1920, 1080);
    private sealed class NewImageCenterSeed
    {
        public required StoryPartVm Part { get; init; }
        public required StoryChildVm Image { get; init; }
        public HashSet<string> Defines { get; } = new(StringComparer.Ordinal);
        public required PropertyChangedEventHandler FieldChanged { get; init; }
        public required NotifyCollectionChangedEventHandler MembershipChanged { get; init; }
    }
    private readonly Dictionary<StoryChildVm, NewImageCenterSeed> _newImageCenters = [];
    private bool _centeringNewImages;
    private bool _imageCenterHooks;

    private WccScenarioStoryChildSpec NewCenteredImageDefaults() => new()
    {
        NewChildId = Guid.NewGuid().ToString("N"),
        Fields = new Dictionary<string, WccScenarioStoryFieldSpec>
        {
            ["x"] = new() { IsTouched = true, SharedValue = "960" },
            ["y"] = new() { IsTouched = true, SharedValue = "540" },
            ["centered"] = new() { IsTouched = true, SharedValue = "yes" }
        }
    };

    private void TrackNewImageCenter(StoryPartVm part, StoryChildVm image)
    {
        if (image.Id is not null || image.Tag != "image" || image.MacroName is not null && !WccScenarioStoryPlanner.SupportedTravelMacros.Contains(image.MacroName, StringComparer.Ordinal)) return;
        var pending = image.AvailableDefines.Where(define => NewImageBase(part, define) is var basis &&
            (!basis.IsProven || !string.IsNullOrWhiteSpace(basis.Expression) && !WccStoryImageCanvas.IsReference(basis.Expression))).ToArray();
        if (pending.Length == 0) return; // A missing base gets a visible fixed canvas when the image receives its file or placement.
        if (!_imageCenterHooks)
        {
            _imageCenterHooks = true;
            DraftChanged += (_, _) => PruneNewImageCenters();
            Parts.CollectionChanged += (_, _) => PruneNewImageCenters();
            Disposed += (_, _) => { foreach (var seed in _newImageCenters.Values.ToArray()) ForgetNewImageCenter(seed); };
        }
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName != nameof(StoryFieldVm.Text) || !_newImageCenters.TryGetValue(image, out var current)) return;
            // Observe the first manual edit, so restoring the displayed seed cannot revive an old placement request.
            foreach (var define in current.Defines.ToArray()) if (!HasNewImageSeed(image, define)) current.Defines.Remove(define);
            if (current.Defines.Count == 0) ForgetNewImageCenter(current);
        };
        NotifyCollectionChangedEventHandler membershipChanged = (_, _) =>
        {
            if (!part.Images.Contains(image) && _newImageCenters.TryGetValue(image, out var current)) ForgetNewImageCenter(current);
        };
        var seed = new NewImageCenterSeed { Part = part, Image = image, FieldChanged = changed, MembershipChanged = membershipChanged };
        seed.Defines.UnionWith(pending); _newImageCenters.Add(image, seed);
        foreach (var key in new[] { "x", "y", "centered" }) image.Field(key).PropertyChanged += changed;
        part.Images.CollectionChanged += membershipChanged;
    }

    private static bool HasNewImageSeed(StoryChildVm image, string define) => image.Field("x").Raw(define) == "960" &&
        image.Field("y").Raw(define) == "540" && image.Field("centered").Raw(define) == "yes";
    private void ForgetNewImageCenter(NewImageCenterSeed seed)
    {
        _newImageCenters.Remove(seed.Image);
        seed.Part.Images.CollectionChanged -= seed.MembershipChanged;
        foreach (var key in new[] { "x", "y", "centered" }) seed.Image.Field(key).PropertyChanged -= seed.FieldChanged;
    }
    private void PruneNewImageCenters()
    {
        foreach (var seed in _newImageCenters.Values.ToArray())
        {
            if (!Parts.Contains(seed.Part) || !seed.Part.Images.Contains(seed.Image)) { ForgetNewImageCenter(seed); continue; }
            seed.Defines.RemoveWhere(define => !seed.Part.AvailableDefines.Contains(define) || !seed.Image.AvailableDefines.Contains(define) || !HasNewImageSeed(seed.Image, define));
            if (seed.Defines.Count == 0) ForgetNewImageCenter(seed);
        }
    }

    /// <summary>References the existing preview loader must decode before centering fresh images in every requested difficulty.</summary>
    public IReadOnlyList<string> PendingNewImageBackgroundReferences
    {
        get
        {
            PruneNewImageCenters();
            return _newImageCenters.Values.SelectMany(seed => seed.Defines.Select(define => NewImageBase(seed.Part, define).Expression))
                .Where(expression => !string.IsNullOrWhiteSpace(expression)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        }
    }
    public string? PendingNewImagePlacementProblem
    {
        get
        {
            PruneNewImageCenters();
            var contexts = _newImageCenters.Values.SelectMany(seed => seed.Defines.Where(define =>
                seed.Image.MacroName is not null || !string.IsNullOrWhiteSpace(seed.Image.Field("file").Decoded(define)))).Distinct(StringComparer.Ordinal).ToArray();
            return contexts.Length == 0 ? null : "A new image is waiting for resolved background geometry in " +
                string.Join(", ", contexts.Select(define => Session.Snapshot.Contexts.First(context => context.Define == define).DisplayName)) +
                ". Let the preview finish loading, or set its X/Y coordinates manually. Image modifiers need explicit coordinates.";
        }
    }

    /// <summary>Uses already-decoded preview metadata. It neither resolves files nor grants source persistence authority.</summary>
    public bool TryCenterNewImages(Func<string, StoryPreviewSize?> cachedDimensions)
    {
        ArgumentNullException.ThrowIfNull(cachedDimensions);
        if (_centeringNewImages || !CanEdit) return false;
        _centeringNewImages = true;
        try
        {
            PruneNewImageCenters();
            var version = DraftVersion;
            var sizes = new Dictionary<string, StoryPreviewSize?>(StringComparer.Ordinal);
            var placements = new List<(NewImageCenterSeed Seed, string Define, string X, string Y)>();
            foreach (var seed in _newImageCenters.Values.ToArray()) foreach (var define in seed.Defines.ToArray())
            {
                var basis = NewImageBase(seed.Part, define);
                if (!basis.IsProven) continue;
                StoryPreviewBaseTransform transform;
                if (string.IsNullOrWhiteSpace(basis.Expression)) transform = StoryPreviewBaseTransform.Identity;
                else if (WccStoryImageCanvas.IsReference(basis.Expression)) transform = StoryPreviewLayout.LayoutBackground(NewImageCanvas, WccStoryImageCanvas.IntrinsicSize, basis.Options).BaseTransform;
                else
                {
                    if (WccImageExpressionParser.TryParse(basis.Expression) is not { HasModifiers: false } ||
                        !WmlResourceReferenceSyntax.ParseImage(basis.Expression, false).IsComplete) continue;
                    if (!sizes.TryGetValue(basis.Expression, out var size)) sizes[basis.Expression] = size = cachedDimensions(basis.Expression);
                    if (DraftVersion != version || !CanEdit) return false;
                    if (size is null) continue;
                    try { transform = StoryPreviewLayout.LayoutBackground(NewImageCanvas, size, basis.Options).BaseTransform; }
                    catch (ArgumentException) { continue; }
                }
                var center = transform.ToOriginal(new(NewImageCanvas.Width / 2, NewImageCanvas.Height / 2));
                if (center.X < int.MinValue || center.X > int.MaxValue || center.Y < int.MinValue || center.Y > int.MaxValue) continue;
                placements.Add((seed, define, Math.Round(center.X).ToString(CultureInfo.InvariantCulture), Math.Round(center.Y).ToString(CultureInfo.InvariantCulture)));
            }
            if (DraftVersion != version || !CanEdit) return false;
            var consumed = false;
            foreach (var placement in placements)
            {
                var seed = placement.Seed;
                if (!CanEdit || !_newImageCenters.TryGetValue(seed.Image, out var current) || !ReferenceEquals(current, seed) ||
                    !seed.Defines.Contains(placement.Define) || !Parts.Contains(seed.Part) || !seed.Part.Images.Contains(seed.Image) ||
                    !seed.Part.AvailableDefines.Contains(placement.Define) || !seed.Image.AvailableDefines.Contains(placement.Define) || !HasNewImageSeed(seed.Image, placement.Define)) continue;
                // Consume before field notifications: Changed may synchronously start another preview refresh.
                seed.Defines.Remove(placement.Define);
                if (seed.Defines.Count == 0) ForgetNewImageCenter(seed);
                var before = DraftVersion;
                var removed = false;
                NotifyCollectionChangedEventHandler membershipChanged = (_, _) => { if (!seed.Part.Images.Contains(seed.Image)) removed = true; };
                seed.Part.Images.CollectionChanged += membershipChanged;
                try
                {
                    seed.Image.SetCoordinatesForDefine(placement.Define, placement.X, placement.Y, () => !removed && CanEdit &&
                        Parts.Contains(seed.Part) && seed.Part.Images.Contains(seed.Image) && DraftVersion == before);
                }
                finally { seed.Part.Images.CollectionChanged -= membershipChanged; }
                consumed = true;
                // A public Changed observer can edit another target or the background. Do not
                // continue that old geometry plan; remaining untouched seeds retry with fresh metadata.
                if (DraftVersion != before + 1 || !CanEdit) break;
            }
            if (consumed) { OnPropertyChanged(nameof(PendingNewImagePlacementProblem)); Changed(); }
            return consumed;
        }
        finally { _centeringNewImages = false; }
    }

    private static (string? Expression, StoryPreviewBackgroundOptions Options, bool IsProven) NewImageBase(StoryPartVm part, string define)
    {
        string? Value(StoryFieldVm field) => field.Raw(define)?.TrimStart().StartsWith('_') == true ? null : field.Decoded(define);
        bool Flag(StoryFieldVm field, bool fallback = false) => Value(field) switch { "yes" or "true" => true, "no" or "false" => false, _ => fallback };
        bool ProvedFlags(IEnumerable<StoryFieldVm> fields) => fields.All(field => field.Raw(define) is null ||
            field.Raw(define)?.TrimStart().StartsWith('_') != true && Value(field) is ("" or "yes" or "no" or "true" or "false"));
        var expression = part.Field("background").Decoded(define);
        var missingExpandedBackground = part.BackgroundLayers.Any(layer => layer.IsExpandedPreview) &&
            !part.BackgroundLayers.Any(layer => layer.IsExpandedPreview && layer.AvailableDefines.Contains(define));
        var selectionProven = true;
        var proven = ProvedFlags(part.Fields.Where(field => field.Key is "scale_background" or "scale_background_horizontally" or "scale_background_vertically" or
            "tile_background" or "tile_background_horizontally" or "tile_background_vertically" or "keep_aspect_ratio"));
        var options = new StoryPreviewBackgroundOptions
        {
            Scale = part.Field("scale_background").Raw(define) is null ? null : Flag(part.Field("scale_background"), true),
            ScaleHorizontally = Flag(part.Field("scale_background_horizontally"), true), ScaleVertically = Flag(part.Field("scale_background_vertically"), true),
            Tile = part.Field("tile_background").Raw(define) is null ? null : Flag(part.Field("tile_background")), TileHorizontally = false,
            TileVertically = part.Field("tile_background_horizontally").Raw(define) is null ? Flag(part.Field("tile_background_vertically")) : Flag(part.Field("tile_background_horizontally")),
            KeepAspectRatio = Flag(part.Field("keep_aspect_ratio"), true)
        };
        foreach (var layer in part.BackgroundLayers.Where(layer => layer.AvailableDefines.Contains(define))
            .OrderBy(layer => layer.OrderByDefine.GetValueOrDefault(define, part.BackgroundLayers.IndexOf(layer))))
        {
            if (!ProvedFlags([layer.Field("base_layer")])) { proven = false; selectionProven = false; continue; }
            if (!Flag(layer.Field("base_layer"))) continue;
            selectionProven = true;
            expression = layer.Field("image").Decoded(define);
            proven = ProvedFlags(layer.Fields.Where(field => field.Key != "image"));
            options = new()
            {
                Scale = layer.Field("scale").Raw(define) is null ? null : Flag(layer.Field("scale"), true),
                ScaleHorizontally = Flag(layer.Field("scale_horizontally"), true), ScaleVertically = Flag(layer.Field("scale_vertically"), true),
                Tile = layer.Field("tile").Raw(define) is null ? null : Flag(layer.Field("tile")),
                TileHorizontally = Flag(layer.Field("tile_horizontally")), TileVertically = Flag(layer.Field("tile_vertically")),
                KeepAspectRatio = Flag(layer.Field("keep_aspect_ratio"), true), BaseLayer = true
            };
        }
        return (expression, options, !missingExpandedBackground && (string.IsNullOrWhiteSpace(expression) ? selectionProven : proven));
    }
}
