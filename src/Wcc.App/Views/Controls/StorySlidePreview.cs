using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Wcc.App.Preview;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Scenarios;

namespace Wcc.App.Views.Controls;

/// <summary>Static preview. Logical canvas pixels are distinct from original base-image coordinates.</summary>
public sealed class StorySlidePreview : Control, IDisposable
{
    private static readonly StoryPreviewSize CanvasSize = new(1920, 1080);
    private static readonly FontFamily StoryFont = new("avares://Wcc.App/Assets/Wesnoth/Story/Fonts#Lato");
    private static readonly FontFamily TitleFont = new("avares://Wcc.App/Assets/Wesnoth/Story/Fonts#WesScript");
    private const string GuiPrefix = "@wcc-story-gui/";
    private static readonly string[] GuiTextures = ["dialogs/translucent65-background.png", "dialogs/translucent65-border-top.png", "dialogs/translucent65-border-bottom.png", "dialogs/story_title_decor.png", "misc/ornate_big_arrow_decor_left.png", "misc/ornate_big_arrow_decor_right.png", "misc/dots.png"];
    // GUI chrome lives at <install>/images, separately from Core unit artwork.
    // This display-only lookup accepts only the fixed Story viewer texture names.
    public static string? ResolveGuiTexturePath(string? installPath, string reference)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !GuiTextures.Contains(reference, StringComparer.Ordinal)) return null;
        var core = WccCoreInstallPaths.GetCoreDataRoot(installPath);
        var data = System.IO.Directory.GetParent(core);
        var install = data?.Parent;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(data?.Name, "data", comparison) || !string.Equals(System.IO.Path.GetFileName(core), "core", comparison) || install is null) return null;
        var path = System.IO.Path.Combine(install.FullName, "images", reference.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return System.IO.File.Exists(path) ? path : null;
    }
    private readonly Dictionary<string, Bitmap?> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap?> _thumbnailImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _imageIdentities = new(StringComparer.Ordinal);
    private readonly List<(StoryChildVm Child, StoryPreviewRect Bounds)> _hitTargets = [];
    private StoryEditorWindowViewModel? _vm;
    private CancellationTokenSource? _loading;
    private bool _disposed;
    private long _version;
    private StoryPreviewBaseTransform _base = StoryPreviewBaseTransform.Identity;
    private bool _baseReady;
    private string? _baseExpression;
    private StoryChildVm? _drag;
    private StoryPreviewPoint _grab;
    private StoryPartVm? _renderedPart;
    private string? _renderedDefine;
    private long _renderedVersion;
    private StoryPartVm? _dragPart;
    private string? _dragDefine;
    private StoryPreviewBaseTransform _dragBase;
    public StoryChildVm? SelectedImage { get; private set; }
    public event EventHandler? ImageSelected;
    public event EventHandler? AssetsLoaded;
    public string PreviewDiagnostic { get; private set; } = "";
    public bool CanDragImages => _baseReady && _vm?.CanEdit == true && _renderedVersion == _version &&
        ReferenceEquals(_renderedPart, _vm.SelectedPart) && _renderedDefine == _vm.PreviewDefine &&
        (WccStoryImageCanvas.IsReference(_baseExpression) || WccImageExpressionParser.TryParse(_baseExpression)?.HasModifiers != true);
    public void Attach(StoryEditorWindowViewModel vm)
    {
        if (_vm is not null) _vm.DraftChanged -= Changed;
        _vm = vm; vm.DraftChanged += Changed; vm.Disposed += (_, _) => Dispose();
        Refresh();
    }
    private void Changed(object? sender, EventArgs e) => Refresh();
    public Bitmap? Thumbnail(StoryPartVm part)
    {
        var define = ThumbnailDefine(part);
        Bitmap? Thumb(string? expression) => expression is null ? null : _thumbnailImages.GetValueOrDefault(expression);
        return Thumb(part.Field("background").Decoded(define)) ?? part.BackgroundLayers.Where(c => c.AvailableDefines.Contains(define)).Select(c => Thumb(c.Field("image").Decoded(define))).FirstOrDefault(b => b is not null);
    }
    private string ThumbnailDefine(StoryPartVm part) => _vm is { } model
        ? model.Scope is { } scope && part.AvailableDefines.Contains(scope) ? scope
            : part.AvailableDefines.Contains(model.PreviewDefine) ? model.PreviewDefine
            : model.Session.Snapshot.Contexts.FirstOrDefault(context => part.AvailableDefines.Contains(context.Define))?.Define ?? ""
        : "";
    public Bitmap? TravelThumbnail(string macro) => Get(WccScenarioStoryPlanner.TravelPreviewImage(macro));
    private static string? ImageExpression(StoryChildVm image, string define) => image.Field("file").Decoded(define)
        ?? WccScenarioStoryPlanner.TravelPreviewImage(image.MacroName);
    public void SelectImage(StoryChildVm? image) { SelectedImage = image; InvalidateVisual(); }
    public void Refresh()
    {
        _baseReady = false;
        if (_drag is not null && (!ReferenceEquals(_dragPart, _vm?.SelectedPart) || _dragDefine != _vm?.PreviewDefine)) _drag = null;
        foreach (var expression in _images.Keys.Concat(_thumbnailImages.Keys).Distinct().ToArray())
            if (_imageIdentities.GetValueOrDefault(expression) != _vm?.PreviewUploadIdentity(expression))
            { _images.GetValueOrDefault(expression)?.Dispose(); _images.Remove(expression); _thumbnailImages.GetValueOrDefault(expression)?.Dispose(); _thumbnailImages.Remove(expression); _imageIdentities.Remove(expression); }
        AssetsLoaded?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        _loading?.Cancel(); _loading?.Dispose(); _loading = new();
        _ = LoadImagesAsync(++_version, _loading.Token);
    }
    private async Task LoadImagesAsync(long version, CancellationToken token)
    {
        if (_vm is not { } vm || !vm.Session.IsCurrent || _disposed) return;
        var define = vm.PreviewDefine;
        var expressions = new[] { vm.SelectedPart?.Field("background").Decoded(define) }
            .Concat(vm.SelectedPart?.Images.Select(i => ImageExpression(i, define)) ?? [])
            .Concat(WccScenarioStoryPlanner.SupportedTravelMacros.Select(WccScenarioStoryPlanner.TravelPreviewImage))
            .Concat(vm.SelectedPart?.BackgroundLayers.Select(i => i.Field("image").Decoded(define)) ?? [])
            .Concat(vm.PendingNewImageBackgroundReferences)
            .Concat(GuiTextures.Select(reference => GuiPrefix + reference))
            .Where(e => !string.IsNullOrWhiteSpace(e)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        var thumbs = vm.Parts.Select(p => p.Field("background").Decoded(ThumbnailDefine(p)))
            .Concat(vm.Parts.SelectMany(p => p.BackgroundLayers.Where(l => l.AvailableDefines.Contains(ThumbnailDefine(p))).Select(l => l.Field("image").Decoded(ThumbnailDefine(p)))))
            .Where(e => !string.IsNullOrWhiteSpace(e)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        foreach (var previous in _images.Keys.Except(expressions, StringComparer.Ordinal).ToArray()) { _images[previous]?.Dispose(); _images.Remove(previous); }
        try
        {
            foreach (var expression in expressions)
            {
                if (_images.ContainsKey(expression)) continue;
                string? path;
                if (expression.StartsWith(GuiPrefix, StringComparison.Ordinal))
                    path = ResolveGuiTexturePath(vm.Session.SourceInstallPath, expression[GuiPrefix.Length..]) ??
                        ResolveGuiTexturePath(vm.Session.RuntimeInstallPath, expression[GuiPrefix.Length..]);
                else
                {
                    path = vm.PreviewUploadPath(expression);
                    path ??= await Task.Run(() => WccUnitPreviewPathResolver.ResolveRawImageExpression(vm.Session.SourcePath,
                        WccSourceOrigin.ImportedExternal, expression, WccAssetReferenceKind.Image, vm.Session.SourceInstallPath).ResolvedAbsolutePath, token);
                }
                var capturedBytes = vm.PreviewUploadBytes(expression);
                var bitmap = await Task.Run(() => expression.StartsWith(GuiPrefix, StringComparison.Ordinal)
                    ? LoadGuiBitmap(expression[GuiPrefix.Length..], path) : LoadBitmap(path, capturedBytes), token);
                if (_disposed || token.IsCancellationRequested || version != _version || !vm.Session.IsCurrent)
                { bitmap?.Dispose(); return; }
                _images[expression] = bitmap;
                _imageIdentities[expression] = vm.PreviewUploadIdentity(expression);
            }
            if (_disposed || token.IsCancellationRequested || version != _version || !ReferenceEquals(vm, _vm) || !vm.Session.IsCurrent) return;
            if (vm.TryCenterNewImages(expression => Size(Get(expression)))) return;
            if (_disposed || token.IsCancellationRequested || version != _version || !ReferenceEquals(vm, _vm) || !vm.Session.IsCurrent) return;
            InvalidateVisual(); AssetsLoaded?.Invoke(this, EventArgs.Empty);
            foreach (var expression in thumbs)
            {
                if (_thumbnailImages.ContainsKey(expression)) continue;
                var path = vm.PreviewUploadPath(expression);
                path ??= await Task.Run(() => WccUnitPreviewPathResolver.ResolveRawImageExpression(vm.Session.SourcePath, WccSourceOrigin.ImportedExternal,
                    expression, WccAssetReferenceKind.Image, vm.Session.SourceInstallPath).ResolvedAbsolutePath, token);
                var capturedBytes = vm.PreviewUploadBytes(expression);
                var thumbnail = await Task.Run(() => LoadThumbnail(path, capturedBytes), token);
                if (_disposed || token.IsCancellationRequested || version != _version || !vm.Session.IsCurrent) { thumbnail?.Dispose(); return; }
                _thumbnailImages[expression] = thumbnail; _imageIdentities[expression] = vm.PreviewUploadIdentity(expression);
            }
            if (_disposed || version != _version) return;
            var missing = expressions.Where(e => Get(e) is null).Count();
            var modified = expressions.Any(e => !WccStoryImageCanvas.IsReference(e) && WccImageExpressionParser.TryParse(e)?.HasModifiers == true);
            PreviewDiagnostic = missing > 0 ? $"{missing} image(s) could not be resolved. " : "";
            if (modified) PreviewDiagnostic += "Image modifiers are shown using their base image in this static preview. ";
            InvalidateVisual(); AssetsLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        { if (!_disposed && version == _version) { PreviewDiagnostic = ex.Message; AssetsLoaded?.Invoke(this, EventArgs.Empty); } }
    }
    private Bitmap? Get(string? expression) => expression is not null ? _images.GetValueOrDefault(expression) : null;
    private Bitmap? GetGui(string reference) => Get(GuiPrefix + reference);
    private static Bitmap? LoadGuiBitmap(string reference, string? fallbackPath)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Wcc.App/Assets/Wesnoth/Story/" + reference));
            return new Bitmap(stream);
        }
        catch (Exception error) when (error is System.IO.IOException or ArgumentException or InvalidOperationException)
        { return BitmapFileLoader.TryLoad(fallbackPath); }
    }
    private static Bitmap? LoadBitmap(string? path, byte[]? capturedBytes)
    {
        if (capturedBytes is null) return BitmapFileLoader.TryLoad(path);
        try { using var stream = new System.IO.MemoryStream(capturedBytes, writable: false); return new Bitmap(stream); }
        catch { return null; }
    }
    private static Bitmap? LoadThumbnail(string? path, byte[]? capturedBytes)
    {
        if (capturedBytes is null && (path is null || !System.IO.File.Exists(path))) return null;
        try { using System.IO.Stream stream = capturedBytes is not null ? new System.IO.MemoryStream(capturedBytes, writable: false) : System.IO.File.OpenRead(path!); return Bitmap.DecodeToWidth(stream, 192, BitmapInterpolationMode.MediumQuality); }
        catch { return null; }
    }
    private static StoryPreviewSize? Size(Bitmap? bitmap) => bitmap is null ? null : new(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
    private (double Scale, double X, double Y) Fit()
    {
        var scale = Math.Min(Bounds.Width / CanvasSize.Width, Bounds.Height / CanvasSize.Height);
        return (Math.Max(scale, 0.001), (Bounds.Width - CanvasSize.Width * scale) / 2, (Bounds.Height - CanvasSize.Height * scale) / 2);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(new SolidColorBrush(Color.Parse("#121A25")), new Rect(Bounds.Size));
        _hitTargets.Clear(); _baseReady = false; if (_vm?.SelectedPart is not { } part) return;
        var define = _vm.PreviewDefine; var fit = Fit();
        _renderedPart = part; _renderedDefine = define; _renderedVersion = _version;
        using var transform = context.PushTransform(Matrix.CreateScale(fit.Scale, fit.Scale) * Matrix.CreateTranslation(fit.X, fit.Y));
        using var clip = context.PushClip(new Rect(0, 0, CanvasSize.Width, CanvasSize.Height));
        context.FillRectangle(Brushes.Black, new Rect(0, 0, CanvasSize.Width, CanvasSize.Height));
        void DrawSlideBorder()
        {
            var stroke = 1 / fit.Scale;
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#82938F")), stroke),
                new Rect(stroke / 2, stroke / 2, CanvasSize.Width - stroke, CanvasSize.Height - stroke));
        }
        string? V(string key) => PreviewValue(part.Field(key), define);
        if (!part.AvailableDefines.Contains(define)) { DrawText(context, "This slide is hidden in this difficulty.", new Rect(40, 300, 1200, 100), 28, TextAlignment.Center); DrawSlideBorder(); return; }
        var layers = new List<StoryPreviewBackgroundLayout>();
        _baseExpression = V("background");
        var bg = Get(V("background"));
        var implicitLayer = StoryPreviewLayout.LayoutBackground(CanvasSize, Size(bg), new()
        {
            Scale = part.Field("scale_background").Raw(define) is null ? null : Bool(V("scale_background"), true), ScaleHorizontally = Bool(V("scale_background_horizontally"), true), ScaleVertically = Bool(V("scale_background_vertically"), true),
            Tile = part.Field("tile_background").Raw(define) is null ? null : Bool(V("tile_background")), TileHorizontally = false,
            // 1.19.28's horizontal shortcut writes the vertical flag; preserve engine behavior here.
            TileVertically = part.Field("tile_background_horizontally").Raw(define) is null ? Bool(V("tile_background_vertically")) : Bool(V("tile_background_horizontally")), KeepAspectRatio = Bool(V("keep_aspect_ratio"), true)
        });
        layers.Add(implicitLayer); DrawLayer(context, bg, implicitLayer);
        foreach (var child in part.BackgroundLayers.Where(c => c.AvailableDefines.Contains(define)).OrderBy(c => c.OrderByDefine.GetValueOrDefault(define, part.BackgroundLayers.IndexOf(c))))
        {
            string? C(string key) => PreviewValue(child.Field(key), define);
            var bitmap = Get(C("image"));
            var layer = StoryPreviewLayout.LayoutBackground(CanvasSize, WccStoryImageCanvas.IsReference(C("image")) ? WccStoryImageCanvas.IntrinsicSize : Size(bitmap), new()
            {
                Scale = child.Field("scale").Raw(define) is null ? null : Bool(C("scale"), true), ScaleHorizontally = Bool(C("scale_horizontally"), true), ScaleVertically = Bool(C("scale_vertically"), true),
                Tile = child.Field("tile").Raw(define) is null ? null : Bool(C("tile")), TileHorizontally = Bool(C("tile_horizontally")), TileVertically = Bool(C("tile_vertically")), KeepAspectRatio = Bool(C("keep_aspect_ratio"), true), BaseLayer = Bool(C("base_layer"))
            });
            layers.Add(layer); DrawLayer(context, bitmap, layer);
            if (layer.BaseLayer) _baseExpression = C("image");
        }
        _base = StoryPreviewLayout.SelectBase(layers);
        _baseReady = WccStoryImageCanvas.IsReference(_baseExpression) || string.IsNullOrWhiteSpace(_baseExpression) || Get(_baseExpression) is not null;
        using (context.PushOpacity(0.75)) DrawBitmap(context, GetGui("dialogs/story_title_decor.png"), new(0, 0, CanvasSize.Width, 100));
        var layout = StoryPreviewLayout.DecodeTextLayout(V("text_layout"));
        var title = V("title"); var showTitle = part.Field("show_title").Raw(define) is null ? !string.IsNullOrEmpty(title) : Bool(V("show_title"));
        // An explicit unresolved title is not an omitted title with the scenario-name default.
        if (title is null && part.Field("title").Raw(define) is not null) showTitle = false;
        if (showTitle)
        {
            var titleText = Format(string.IsNullOrEmpty(title) ? _vm.Session.GetScenarioTitle(define) : title, 32, CanvasSize.Width - 20, TextAlignment.Left, title: true);
            // Position uses the natural glyph width. Alignment applies within it.
            titleText.MaxTextWidth = Math.Max(1, titleText.Width);
            titleText.TextAlignment = Align(V("title_alignment"), TextAlignment.Left);
            var r = StoryPreviewLayout.TitleBounds(CanvasSize, new(titleText.Width, titleText.Height), V("title_position"), layout);
            context.DrawText(titleText, new(r.X, r.Y));
        }
        foreach (var image in part.Images.Where(c => c.AvailableDefines.Contains(define)).OrderBy(c => c.OrderByDefine.GetValueOrDefault(define, part.Images.IndexOf(c))))
        {
            string? I(string key) => key == "file" ? ImageExpression(image, define) : PreviewValue(image.Field(key), define)
                ?? (WccScenarioStoryPlanner.TravelPreviewImage(image.MacroName) is not null ? key switch { "centered" => "yes", "resize_with_background" => "no", _ => null } : null);
            var bitmap = Get(I("file")); if (bitmap is null) continue;
            if (!int.TryParse(I("x") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) || !int.TryParse(I("y") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) continue;
            var r = StoryPreviewLayout.OverlayBounds(_base, Size(bitmap)!.Value, x, y, Bool(I("centered")), Bool(I("resize_with_background")));
            DrawBitmap(context, bitmap, r); _hitTargets.Add((image, r));
            if (ReferenceEquals(image, SelectedImage)) context.DrawRectangle(null, new Pen(Brushes.Gold, 2), Rect(r));
        }
        var story = V("story") ?? "";
        var contentBounds = StoryPreviewLayout.TextContentBounds(CanvasSize, 0, layout);
        var text = Format(story, 17, contentBounds.Width, Align(V("text_alignment"), TextAlignment.Left));
        var panel = StoryPreviewLayout.TextPanelBounds(CanvasSize, Math.Max(StoryPreviewLayout.MinimumTextPanelHeight(CanvasSize), text.Height + 113), layout);
        if (!string.IsNullOrWhiteSpace(story) && layers.Any(l => l.HasImage))
        {
            var panelBitmap = GetGui("dialogs/translucent65-background.png");
            if (panelBitmap is null) context.FillRectangle(new SolidColorBrush(Color.FromArgb(190, 25, 24, 22)), Rect(panel)); else DrawBitmap(context, panelBitmap, panel);
            var topBorder = GetGui("dialogs/translucent65-border-top.png"); var bottomBorder = GetGui("dialogs/translucent65-border-bottom.png");
            if (panel.Y > 0) DrawBitmap(context, topBorder, new(0, panel.Y, CanvasSize.Width, topBorder?.PixelSize.Height ?? 2));
            if (panel.Bottom < CanvasSize.Height) DrawBitmap(context, bottomBorder, new(0, panel.Bottom - (bottomBorder?.PixelSize.Height ?? 2), CanvasSize.Width, bottomBorder?.PixelSize.Height ?? 2));
        }
        context.DrawText(text, new(contentBounds.X, panel.Y + 20));
        var leftArrow = StoryPreviewLayout.ArrowBounds(CanvasSize, panel, right: false);
        var rightArrow = StoryPreviewLayout.ArrowBounds(CanvasSize, panel, right: true);
        DrawBitmap(context, GetGui("misc/ornate_big_arrow_decor_left.png"), new(leftArrow.X, leftArrow.Y, 115, 99));
        DrawBitmap(context, GetGui("misc/ornate_big_arrow_decor_right.png"), new(rightArrow.X, rightArrow.Y, 115, 99));
        var skip = StoryPreviewLayout.SkipBounds(CanvasSize, panel);
        DrawBitmap(context, GetGui("misc/dots.png"), new(skip.X, skip.Y + 14, 44, 22));
        DrawText(context, "Skip", new Rect(skip.X + 50, skip.Y + 9, skip.Width - 100, 32), 20, TextAlignment.Center);
        DrawBitmap(context, GetGui("misc/dots.png"), new(skip.Right - 44, skip.Y + 14, 44, 22));
        DrawSlideBorder();
    }
    private static void DrawLayer(DrawingContext context, Bitmap? bitmap, StoryPreviewBackgroundLayout layer)
    {
        if (bitmap is null) return;
        if (!layer.IsTiled) { DrawBitmap(context, bitmap, layer.Bounds); return; }
        using var clip = context.PushClip(Rect(layer.Bounds)); var tile = StoryPreviewLayout.FirstTileBounds(layer);
        // Normalize the first visible tile, then bound iterations to the visible viewport.
        var x0 = tile.X + Math.Floor((layer.Bounds.X - tile.X) / tile.Width) * tile.Width;
        var y0 = tile.Y + Math.Floor((layer.Bounds.Y - tile.Y) / tile.Height) * tile.Height;
        var count = 0;
        for (var y = y0; y < layer.Bounds.Bottom && count < 20000; y += tile.Height)
            for (var x = x0; x < layer.Bounds.Right && count++ < 20000; x += tile.Width)
                DrawBitmap(context, bitmap, new(x, y, tile.Width, tile.Height));
    }
    private static readonly HashSet<string> BooleanKeys = new(StringComparer.Ordinal)
    { "show_title", "scale_background", "scale_background_vertically", "scale_background_horizontally", "tile_background", "tile_background_vertically", "tile_background_horizontally", "keep_aspect_ratio", "centered", "resize_with_background", "scale", "scale_vertically", "scale_horizontally", "tile", "tile_vertically", "tile_horizontally", "base_layer" };
    private static string? PreviewValue(StoryFieldVm field, string define) => BooleanKeys.Contains(field.Key) && field.Raw(define)?.TrimStart().StartsWith('_') == true ? null : field.Decoded(define);
    private static bool? BoolNullable(string? value) => value switch { "yes" or "true" => true, "no" or "false" => false, _ => null };
    private static bool Bool(string? value, bool fallback = false) => BoolNullable(value) ?? fallback;
    private static TextAlignment Align(string? value, TextAlignment fallback) => value switch { "left" => TextAlignment.Left, "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => fallback };
    private static FormattedText Format(string value, double size, double width, TextAlignment align, bool title = false) => new(Regex.Replace(value, "<[^>]+>", ""), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(title ? TitleFont : StoryFont), size,
        new SolidColorBrush(Color.Parse(title ? "#EEE8D5" : "#CCCCCC"))) { MaxTextWidth = width, TextAlignment = align, LineHeight = size * 1.3 };
    private static void DrawText(DrawingContext context, string text, Rect bounds, double size, TextAlignment align) => context.DrawText(Format(text, size, bounds.Width, align), bounds.Position);
    private static Rect Rect(StoryPreviewRect r) => new(r.X, r.Y, r.Width, r.Height);
    private static void DrawBitmap(DrawingContext context, Bitmap? bitmap, StoryPreviewRect r) { if (bitmap is not null && r.Width > 0 && r.Height > 0) context.DrawImage(bitmap, new Rect(bitmap.Size), Rect(r)); }
    private StoryPreviewPoint CanvasPoint(Point p) { var f = Fit(); return new((p.X - f.X) / f.Scale, (p.Y - f.Y) / f.Scale); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CanDragImages || _vm is null) return;
        var define = _vm.PreviewDefine;
        var p = CanvasPoint(e.GetPosition(this));
        var hit = _hitTargets.LastOrDefault(h => p.X >= h.Bounds.X && p.X <= h.Bounds.Right && p.Y >= h.Bounds.Y && p.Y <= h.Bounds.Bottom);
        if (hit.Child is null || !hit.Child.CanEdit || WccImageExpressionParser.TryParse(hit.Child.Field("file").Decoded(define))?.HasModifiers == true) return;
        if (!int.TryParse(hit.Child.Field("x").Decoded(define) ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) || !int.TryParse(hit.Child.Field("y").Decoded(define) ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) return;
        SelectedImage = _drag = hit.Child; ImageSelected?.Invoke(this, EventArgs.Empty);
        _dragPart = _vm.SelectedPart; _dragDefine = define; _dragBase = _base;
        var original = _dragBase.ToOriginal(p);
        _grab = new(original.X - x, original.Y - y); e.Pointer.Capture(this); e.Handled = true; InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (_drag is null || _vm?.CanEdit != true || !ReferenceEquals(_dragPart, _vm.SelectedPart) || _dragDefine != _vm.PreviewDefine) return;
        var p = _dragBase.ToOriginal(CanvasPoint(e.GetPosition(this)));
        _drag.SetCoordinates(Math.Round(p.X - _grab.X).ToString(CultureInfo.InvariantCulture), Math.Round(p.Y - _grab.Y).ToString(CultureInfo.InvariantCulture));
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _drag = null; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _drag = null; }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _loading?.Cancel(); _loading?.Dispose();
        if (_vm is not null) _vm.DraftChanged -= Changed;
        foreach (var bitmap in _images.Values) bitmap?.Dispose(); _images.Clear();
        foreach (var bitmap in _thumbnailImages.Values) bitmap?.Dispose(); _thumbnailImages.Clear(); _imageIdentities.Clear();
    }
}
