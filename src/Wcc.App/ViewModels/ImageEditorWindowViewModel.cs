using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

public sealed record ImageEditorEngineResult(bool IsSuccess, string? RenderedPath, string? Diagnostic)
{ public IReadOnlyList<WccImageRenderResourcePin> ReviewedResources { get; init; } = []; }
public sealed record ImageEditorComparison(long Generation, string Expression, Bitmap Local, Bitmap Engine,
    string Notes, string BaseSha256) : IDisposable
{
    public byte[] EnginePngBytes { get; init; } = [];
    public IReadOnlyList<WccImageRenderResourcePin> ReviewedResources { get; init; } = [];
    private readonly BitmapLifetime _lifetime = new(Local, Engine);
    public bool TryAcquireLease(out IDisposable? lease) => _lifetime.TryAcquire(out lease);
    public void Dispose() => _lifetime.ReleaseOwner();
    private sealed class BitmapLifetime(Bitmap local, Bitmap engine)
    {
        private int _references = 1;
        private int _ownerReleased;
        public bool TryAcquire(out IDisposable? lease)
        {
            for (var count = Volatile.Read(ref _references); count > 0; count = Volatile.Read(ref _references))
                if (Interlocked.CompareExchange(ref _references, count + 1, count) == count)
                { lease = new Lease(this); return true; }
            lease = null; return false;
        }
        public void ReleaseOwner() { if (Interlocked.Exchange(ref _ownerReleased, 1) == 0) Release(); }
        private void Release() { if (Interlocked.Decrement(ref _references) == 0) { local.Dispose(); engine.Dispose(); } }
        private sealed class Lease(BitmapLifetime owner) : IDisposable
        {
            private BitmapLifetime? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}

/// <summary>Detached image-expression draft; acceptance returns text, never writes source or images.</summary>
public sealed partial class ImageEditorWindowViewModel : ViewModelBase, IDisposable
{
    private readonly Func<bool> _isCurrent;
    private readonly Func<string, CancellationToken, Task<ImageEditorEngineResult>>? _engineRenderer;
    private WccImageFunctionDocument _document;
    private WccRgbaImage? _baseImage;
    private string _baseReference;
    private string? _resolvedImagePath;
    private string? _baseSha256;
    private ImageEditorModifierVm? _selected;
    private Bitmap? _preview;
    private Bitmap? _inputPreview;
    private ImageEditorComparison? _comparison;
    private string _status = "Load an image to start.";
    private string _previewNotes = "";
    private bool _previewComplete;
    private bool _inputComplete;
    private readonly List<Task> _previewWork = [];
    private WccImageFunctionPreviewContext? _previewContext;
    private Func<string, string?>? _previewImageResolver;
    private Func<string, string, string?>? _previewImageResolverWithBase;
    private IReadOnlyList<(string Path, string Hash)> _nestedPins = [];
    private bool _busy;
    private bool _disposed;
    private bool _replacingRows;
    private bool _useLayerPriority;
    private long _generation;
    private long _previewGeneration = -1;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _comparisonCancellation;
    private int _inputWidth, _inputHeight;

    public ImageEditorWindowViewModel(string expression, string? resolvedImagePath = null,
        Func<bool>? isCurrent = null,
        Func<string, CancellationToken, Task<ImageEditorEngineResult>>? engineRenderer = null,
        bool standalone = false)
    {
        _document = WccImageFunctionDocument.Parse(expression ?? "");
        _baseReference = _document.BaseReference;
        _resolvedImagePath = resolvedImagePath;
        _isCurrent = isCurrent ?? (() => true); _engineRenderer = engineRenderer; IsStandalone = standalone;
        Modifiers.CollectionChanged += (_, _) => { if (!_replacingRows) Changed(); };
        LoadRows();
        ResetHistory();
    }
    public ObservableCollection<ImageEditorModifierVm> Modifiers { get; } = [];
    public bool IsStandalone { get; }
    public bool CanEdit => !_disposed && !_busy && _isCurrent();
    public bool IsBusy => _busy;
    public bool CanCompare => CanEdit && !IsPreviewPaused && _previewGeneration == _generation && _preview is not null && _document.IsComplete &&
        _baseReference == _document.BaseReference && _engineRenderer is not null && _baseImage is not null;
    public string ApplyLabel => IsStandalone ? "Review and copy" : "Review and apply";
    public string ModeHint => IsStandalone
        ? "Review with Wesnoth, then copy the expression. This tool keeps the original image unchanged."
        : "Review with Wesnoth, then return the expression to the field. Save there to write WML.";
    public string Expression => EffectiveDocument().BuildExpression();
    public long DraftGeneration => _generation;
    public bool IsComparisonCurrent(ImageEditorComparison comparison) => ReferenceEquals(comparison, _comparison) && Current(comparison.Generation) && Expression == comparison.Expression;
    public string? ResolvedImagePath => _resolvedImagePath;
    public string? BaseSha256 => _baseSha256;
    public string BaseLabel => _resolvedImagePath ?? "No base image loaded";
    public Bitmap? Preview => _preview;
    public Bitmap? InputPreview => _inputPreview;
    public string Status => _status;
    public string PreviewNotes => _previewNotes;
    public bool PreviewIsComplete => _previewComplete;
    public string PreviewQuality => _baseImage is null ? "Open an image to preview." : IsPreviewPaused ? "Adjusting — preview updates on release" : _previewGeneration != _generation ? "Updating local preview…" : _previewComplete ? "Local preview" : "Partial local preview — see notes";
    public ImageEditorComparison? Comparison => _comparison;
    public string DimensionLabel => _preview is { } bitmap ? $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} px" : "";
    public string ComparisonUnavailableReason => _engineRenderer is null
        ? "A configured Wesnoth installation and exact image context are required for review."
        : _baseReference != _document.BaseReference ? "The expression's base changed. Load its matching image before review."
        : !CanEdit ? "The source field or context changed. Reopen the image editor." : HasTemporaryResources ? "Temporary effect images are available for preview and PNG export only." : "";
    public ImageEditorModifierVm? SelectedModifier
    {
        get => _selected;
        set
        {
            if (value is not null && !Modifiers.Contains(value) || ReferenceEquals(value, _selected)) return;
            FinishEditGesture();
            if (SetField(ref _selected, value))
            { RefreshHistorySelection(); _previewGeneration = -1; Raise(); OnPropertyChanged(nameof(HasSelection)); SchedulePreview(); }
        }
    }
    public bool HasSelection => _selected is not null;
    public IReadOnlyList<WccImageFunctionDefinition> FunctionChoices => WccImageFunctionCatalog.Definitions;
    public IReadOnlyList<ImageEditorEffectGroup> EffectChoices => ImageEditorEffectCatalog.CommonEffects;
    public IReadOnlyList<WccImageFunctionDefinition> OtherEffectChoices => ImageEditorEffectCatalog.OtherEffects;
    public bool CanChooseColor => CanEdit && _selected?.Name is "BLEND" or "BG";
    public string SelectedHelp => _selected is { } step && WccImageFunctionCatalog.Find(step.Name) is { } definition
        ? definition.ParameterHint + "\n" + definition.PreviewNote : "The original function is preserved. Edit its arguments explicitly to change it.";
    public async Task InitializeAsync()
    { if (_baseImage is null && _resolvedImagePath is { } path) await LoadImageAsync(path, Expression); }

    public async Task LoadImageAsync(string path, string? expression = null)
    {
        if (!CanEdit || !IsStandalone && _baseImage is not null) return;
        var generation = Invalidate(); SetBusy(true);
        try
        {
            var loaded = await Task.Run(() => DecodeSource(path));
            if (!Current(generation)) return;
            _baseImage = loaded.Image; _baseSha256 = loaded.Hash; _resolvedImagePath = Path.GetFullPath(path);
            _document = WccImageFunctionDocument.Parse(expression ?? Path.GetFileName(path));
            _baseReference = _document.BaseReference; _useLayerPriority = false;
            _nestedPins = []; _inputWidth = _inputHeight = 0; _inputComplete = false; _geometryPreview = null;
            _preview?.Dispose(); _inputPreview?.Dispose(); _preview = _inputPreview = null;
            if (expression is null) _temporaryImages.Clear();
            LoadRows(); ResetHistory(); _status = "Image loaded.";
            OnPropertyChanged(nameof(BaseLabel)); OnPropertyChanged(nameof(ResolvedImagePath)); OnPropertyChanged(nameof(BaseSha256));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (Current(generation)) _status = error.Message; }
        finally { if (Current(generation)) { SetBusy(false); Raise(); SchedulePreview(); } }
    }
    public void SetPreviewContext(WccImageFunctionPreviewContext? context)
    { if (_disposed) return; FinishEditGesture(); _geometryPreview = null; _inputComplete = false; _previewContext = context; Changed(false, false); }
    public void SetPreviewImageResolver(Func<string, string?> resolveBasePath)
    { if (_disposed) return; FinishEditGesture(); _geometryPreview = null; _inputComplete = false; _previewImageResolverWithBase = null; _previewImageResolver = resolveBasePath; Changed(false, false); }
    public void SetPreviewImageResolver(Func<string, string, string?> resolveBasePath)
    { if (_disposed) return; FinishEditGesture(); _geometryPreview = null; _inputComplete = false; _previewImageResolver = null; _previewImageResolverWithBase = resolveBasePath; Changed(false, false); }
    public void LoadExpression(string expression)
    {
        if (!CanEdit) return;
        FinishEditGesture();
        Invalidate(); _useLayerPriority = false; _document = WccImageFunctionDocument.Parse(expression); LoadRows(); Changed(false);
    }
    private void LoadRows(int selectedIndex = 0)
    {
        _replacingRows = true;
        try
        {
            foreach (var row in Modifiers) row.PropertyChanged -= RowChanged;
            Modifiers.Clear();
            foreach (var step in _document.Steps.Reverse())
            { var row = new ImageEditorModifierVm(step); row.PropertyChanged += RowChanged; Modifiers.Add(row); }
            _selected = Modifiers.Count == 0 ? null : Modifiers[Math.Clamp(selectedIndex, 0, Modifiers.Count - 1)];
        }
        finally { _replacingRows = false; }
        UpdateLayerPriority();
        OnPropertyChanged(nameof(SelectedModifier)); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SelectedHelp));
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(ImageEditorModifierVm.Arguments) or nameof(ImageEditorModifierVm.IsEnabled) or nameof(ImageEditorModifierVm.Name))) return;
        if (IsPreviewPaused && !ReferenceEquals(sender, _gestureOwner))
        {
            // A newer edit elsewhere changes the selected layer's input. Keep that edit,
            // retire the frozen geometry proof, and recompute before accepting another drag.
            _inputComplete = false; _geometryPreview = null; FinishEditGesture();
        }
        Changed();
    }
    public void AddEffect(ImageEditorEffectGroup effect) => AddFunction(effect.DefaultFlavor.Name);
    public void AddFunction(string name, string? arguments = null)
    {
        if (!CanEdit) return;
        var definition = WccImageFunctionCatalog.Find(name);
        var args = arguments ?? definition?.DefaultArguments ?? "";
        if (arguments is null && name is "CROP" or "SCALE" or "SCALE_INTO" or "SCALE_SHARP" or "SCALE_INTO_SHARP" && _baseImage is not null)
        {
            // Top visual layers execute after the layers below them.
            var size = _previewGeneration == _generation && _preview is not null
                ? _preview.PixelSize : new PixelSize(_baseImage.Width, _baseImage.Height);
            args = name == "CROP" ? $"0,0,{size.Width},{size.Height}" : $"{size.Width},{size.Height}";
        }
        var row = new ImageEditorModifierVm(WccImageFunctionStep.Create(name, args)); row.PropertyChanged += RowChanged;
        Modifiers.Insert(0, row); SelectedModifier = row;
    }
    public void RemoveSelected()
    {
        if (!CanEdit || _selected is not { } selected) return;
        var index = Modifiers.IndexOf(selected); selected.PropertyChanged -= RowChanged; Modifiers.Remove(selected);
        SelectedModifier = Modifiers.ElementAtOrDefault(Math.Min(index, Modifiers.Count - 1));
    }
    public void RemoveModifier(ImageEditorModifierVm row)
    {
        if (!CanEdit || !Modifiers.Contains(row)) return;
        SelectedModifier = row; RemoveSelected();
    }
    public void MoveModifier(ImageEditorModifierVm row, int targetIndex)
    {
        if (!CanEdit || !Modifiers.Contains(row) || targetIndex < 0 || targetIndex >= Modifiers.Count) return;
        SelectedModifier = row;
        var index = Modifiers.IndexOf(row);
        if (index != targetIndex) Modifiers.Move(index, targetIndex);
    }
    public void MoveSelected(int direction)
    {
        if (!CanEdit || _selected is null) return;
        var index = Modifiers.IndexOf(_selected); var next = index + direction;
        if (next >= 0 && next < Modifiers.Count) Modifiers.Move(index, next);
    }
    private void Changed(bool layerEdit = true, bool recordHistory = true)
    {
        if (_disposed || !_isCurrent()) return;
        if (layerEdit) _useLayerPriority = true;
        UpdateLayerPriority();
        _document = _document.WithSteps(Modifiers.Reverse().Select(row => row.ToStep()));
        if (recordHistory) RecordHistory();
        Invalidate(); Raise(); SchedulePreview();
    }
    private void UpdateLayerPriority()
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Modifiers)
        {
            var key = ImageEditorEffectCatalog.PriorityKey(row);
            row.IsOverridden = _useLayerPriority && row.IsEnabled && key is not null && !claimed.Add(key);
        }
    }
    private WccImageFunctionDocument EffectiveDocument() => _useLayerPriority
        ? _document.WithSteps(Modifiers.Reverse().Select(row => row.ToStep() with { IsEnabled = row.IsEnabled && !row.IsOverridden }))
        : _document;
    private long Invalidate()
    {
        _comparisonCancellation?.Cancel(); _previewCancellation?.Cancel();
        _comparison?.Dispose(); _comparison = null;
        OnPropertyChanged(nameof(Comparison)); return ++_generation;
    }
    private void SchedulePreview()
    { if (_disposed || IsPreviewPaused) return; _previewWork.RemoveAll(task => task.IsCompleted); _previewWork.Add(RefreshPreviewAsync()); }
    public Task WaitForPreviewAsync() => Task.WhenAll(_previewWork.ToArray());
    public void ReportStatus(string message) { _status = message; OnPropertyChanged(nameof(Status)); }
    public async Task RefreshPreviewAsync()
    {
        if (_disposed || IsPreviewPaused) return;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = new();
        var token = _previewCancellation.Token; var generation = _generation;
        var source = _baseImage; var document = EffectiveDocument(); var selected = _selected;
        var previewContext = _previewContext; var imageResolver = _previewImageResolver;
        var temporaryImages = new Dictionary<string, string>(_temporaryImages, StringComparer.Ordinal);
        var imageResolverWithBase = _previewImageResolverWithBase; var capturedBasePath = _resolvedImagePath;
        if (source is null || !CanEdit) { Raise(); return; }
        if (_baseReference != document.BaseReference)
        {
            _preview?.Dispose(); _inputPreview?.Dispose(); _preview = _inputPreview = null;
            _status = ComparisonUnavailableReason; Raise(); return;
        }
        var index = selected is null ? document.Steps.Count : Modifiers.Count - 1 - Modifiers.IndexOf(selected);
        var input = document.WithSteps(document.Steps.Take(index).Concat(document.Steps.Skip(index)
            .Where(step => step.Name is "RC" or "TC" or "PAL")));
        Bitmap? outputBitmap = null, inputBitmap = null;
        try
        {
            var rendered = await Task.Run(() =>
            {
                var resolved = new Dictionary<string, WccRgbaImage?>(StringComparer.Ordinal);
                var pins = new List<(string Path, string Hash)>();
                if (previewContext?.SourcePath is { } contextPath && previewContext.SourceSha256 is { } contextHash) pins.Add((contextPath, contextHash));
                long nestedPixels = 0;
                WccRgbaImage? Resolve(string reference)
                {
                    token.ThrowIfCancellationRequested();
                    if (resolved.TryGetValue(reference, out var cached)) return cached;
                    var path = temporaryImages.GetValueOrDefault(reference) ?? (imageResolverWithBase is not null && capturedBasePath is not null
                        ? imageResolverWithBase(capturedBasePath, reference) : imageResolver?.Invoke(reference));
                    if (path is null || resolved.Count >= 32) return resolved[reference] = null;
                    try
                    {
                        var loaded = DecodeSource(path);
                        var pixels = (long)loaded.Image.Width * loaded.Image.Height;
                        if (nestedPixels + pixels > 16 * 1024 * 1024) return resolved[reference] = null;
                        nestedPixels += pixels; pins.Add((path, loaded.Hash)); return resolved[reference] = loaded.Image;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                    { return resolved[reference] = null; }
                }
                var output = WccImageFunctionPreview.Render(source, document, Resolve, token, previewContext);
                var before = WccImageFunctionPreview.Render(source, input, Resolve, token, previewContext);
                var geometry = PrepareGeometryPreview(document, index, before, output, Resolve, token, previewContext);
                return (Output: output, Input: before, Pins: pins.ToArray(), Geometry: geometry);
            }, token);
            outputBitmap = await Task.Run(() => Encode(rendered.Output.Image), token);
            inputBitmap = await Task.Run(() => Encode(rendered.Input.Image), token);
            if (!Current(generation) || token.IsCancellationRequested || !ReferenceEquals(selected, _selected)) return;
            _preview?.Dispose(); _inputPreview?.Dispose(); _preview = outputBitmap; _inputPreview = inputBitmap;
            outputBitmap = inputBitmap = null; _inputWidth = rendered.Input.Image.Width; _inputHeight = rendered.Input.Image.Height;
            _geometryPreview = rendered.Geometry;
            _nestedPins = rendered.Pins; _inputComplete = rendered.Input.HasKnownGeometry; _previewGeneration = generation; _previewComplete = rendered.Output.IsComplete;
            _previewNotes = string.Join("\n", rendered.Output.Notes);
            _status = document.IsComplete ? "Adjust functions and review the result with Wesnoth when ready." : document.Diagnostic ?? "The expression is incomplete.";
            if (_baseReference != document.BaseReference) _status = ComparisonUnavailableReason;
            Raise();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
        { if (Current(generation)) { _status = error.Message; Raise(); } }
        finally { outputBitmap?.Dispose(); inputBitmap?.Dispose(); }
    }
    public async Task<ImageEditorComparison?> CompareWithEngineAsync()
    {
        if (IsPreviewPaused) return null;
        FinishEditGesture();
        if (!CanCompare || _engineRenderer is null || _preview is null || _baseSha256 is null) return null;
        var generation = _generation; var expression = Expression; var hash = _baseSha256;
        _previewCancellation?.Cancel();
        _comparisonCancellation?.Cancel(); _comparisonCancellation?.Dispose(); _comparisonCancellation = new();
        var token = _comparisonCancellation.Token; SetBusy(true);
        Bitmap? engine = null, local = null;
        try
        {
            if (!await SourceMatchesAsync(hash, token)) throw new InvalidOperationException("The base image changed. Reload it before review.");
            if (!Current(generation) || token.IsCancellationRequested || Expression != expression) return null;
            var result = await RenderEditorEngineAsync(expression, token);
            if (!Current(generation) || token.IsCancellationRequested || Expression != expression) return null;
            if (!result.IsSuccess || result.RenderedPath is null) throw new InvalidOperationException(result.Diagnostic ?? "Wesnoth could not render this expression.");
            var reviewedResources = Array.AsReadOnly(result.ReviewedResources.Select(pin => pin with { }).ToArray());
            var png = await Task.Run(() => ReadBoundedImage(result.RenderedPath), token);
            engine = await Task.Run(() => Encode(DecodeImage(png).Image), token);
            if (!await SourceMatchesAsync(hash, token) || !await ReviewedResourcesMatchAsync(reviewedResources, token))
                throw new InvalidOperationException("A reviewed image resource changed. Render the current expression again.");
            if (!Current(generation) || Expression != expression) return null;
            local = await Task.Run(() => Clone(_preview), token);
            if (!Current(generation) || token.IsCancellationRequested || Expression != expression) return null;
            var notes = string.Join("\n", new[] { _previewNotes, result.Diagnostic }.Where(note => !string.IsNullOrWhiteSpace(note)));
            if (!Current(generation) || token.IsCancellationRequested || Expression != expression) return null;
            var comparison = new ImageEditorComparison(generation, expression, local, engine, notes, hash) { ReviewedResources = reviewedResources, EnginePngBytes = png };
            _comparison?.Dispose(); _comparison = comparison; engine = local = null;
            _status = "Compare the local result with Wesnoth, then confirm the exact expression."; Raise(); return comparison;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (Current(generation)) { _status = error.Message; Raise(); } return null; }
        finally { engine?.Dispose(); local?.Dispose(); if (!_disposed) SetBusy(false); }
    }
    public async Task<string?> AcceptComparisonAsync(ImageEditorComparison comparison, bool forImageExport = false)
    {
        if (!forImageExport && !CanReturnExpression)
        { ReportStatus("Temporary effect images are available for PNG export only. Campaign resource paths must be imported before applying."); return null; }
        if (!ReferenceEquals(comparison, _comparison) || !Current(comparison.Generation) || Expression != comparison.Expression ||
            !await SourceMatchesAsync(comparison.BaseSha256, CancellationToken.None) ||
            !await ReviewedResourcesMatchAsync(comparison.ReviewedResources, CancellationToken.None) || !Current(comparison.Generation))
        { _status = "The image or expression changed. Review the current result again."; Raise(); return null; }
        return comparison.Expression;
    }
    private async Task<bool> SourceMatchesAsync(string hash, CancellationToken token)
    {
        try
        {
            var path = _resolvedImagePath; var nested = _nestedPins.ToArray();
            return path is not null && await Task.Run(() =>
            {
                using (var stream = File.OpenRead(path))
                { if (stream.Length > 64 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(stream)) != hash) return false; }
                foreach (var pin in nested)
                { token.ThrowIfCancellationRequested(); using var stream = File.OpenRead(pin.Path); if (stream.Length > 64 * 1024 * 1024 || !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), pin.Hash, StringComparison.OrdinalIgnoreCase)) return false; }
                return true;
            }, token);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static async Task<bool> ReviewedResourcesMatchAsync(IReadOnlyList<WccImageRenderResourcePin> resources, CancellationToken token)
    {
        try
        {
            return await Task.Run(() =>
            {
                foreach (var pin in resources)
                {
                    token.ThrowIfCancellationRequested(); using var stream = File.OpenRead(pin.AbsolutePath);
                    if (stream.Length > 64 * 1024 * 1024 || !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), pin.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }, token);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private bool Current(long generation) => !_disposed && generation == _generation && _isCurrent();
    private void SetBusy(bool value)
    { _busy = value; OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanEditGeometry)); OnPropertyChanged(nameof(CanChooseColor)); RaiseHistory();
      if (!value && _baseImage is not null && _previewGeneration != _generation) SchedulePreview(); }
    private void Raise()
    {
        foreach (var key in new[] { nameof(Expression), nameof(Preview), nameof(InputPreview), nameof(Status), nameof(PreviewNotes),
            nameof(PreviewQuality), nameof(PreviewIsComplete), nameof(DimensionLabel), nameof(CanCompare), nameof(CanEdit),
            nameof(SelectionRectangle), nameof(GeometryMode), nameof(HasGeometry), nameof(UsesInputCanvas), nameof(CanEditGeometry), nameof(CanvasHint), nameof(CanChooseColor), nameof(SelectedHelp), nameof(ComparisonUnavailableReason), nameof(HasTemporaryResources), nameof(CanReturnExpression) }) OnPropertyChanged(key);
    }
    private static (WccRgbaImage Image, string Hash) DecodeSource(string path)
        => DecodeImage(ReadBoundedImage(path));
    private static byte[] ReadBoundedImage(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 64 * 1024 * 1024) throw new InvalidOperationException("Choose an image file smaller than 64 MiB.");
        using var file = File.OpenRead(path);
        if (file.Length > 64 * 1024 * 1024) throw new InvalidOperationException("Choose an image file smaller than 64 MiB.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        if (file.Position != file.Length) throw new IOException("The image changed while loading. Load it again.");
        return bytes;
    }
    private static (WccRgbaImage Image, string Hash) DecodeImage(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width < 1 || codec.Info.Height < 1 || (long)codec.Info.Width * codec.Info.Height > 16 * 1024 * 1024)
            throw new InvalidOperationException("This image cannot be decoded within the preview's 16 million pixel limit.");
        using var source = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(source.Info, source.GetPixels()) != SKCodecResult.Success)
            throw new InvalidOperationException("This image could not be completely decoded.");
        var rgba = new byte[checked(source.Width * source.Height * 4)];
        for (var y = 0; y < source.Height; y++)
            Marshal.Copy(IntPtr.Add(source.GetPixels(), y * source.RowBytes), rgba, y * source.Width * 4, source.Width * 4);
        return (new(source.Width, source.Height, rgba), Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static Bitmap Encode(WccRgbaImage image)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = encoded.AsStream(); return new Bitmap(stream);
    }
    private static Bitmap Clone(Bitmap bitmap) { using var stream = new MemoryStream(); bitmap.Save(stream); stream.Position = 0; return new Bitmap(stream); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Invalidate();
        _previewCancellation?.Dispose(); _comparisonCancellation?.Dispose(); _preview?.Dispose(); _inputPreview?.Dispose();
        foreach (var row in Modifiers) row.PropertyChanged -= RowChanged;
    }
}

public sealed class ImageEditorModifierVm : ViewModelBase
{
    private readonly WccImageFunctionStep _original;
    private string _name;
    private string _arguments;
    private bool _enabled;
    private bool _overridden;
    public ImageEditorModifierVm(WccImageFunctionStep step) { _original = step; _name = step.Name; _arguments = step.Arguments; _enabled = step.IsEnabled; }
    public string Name => _name;
    public ImageEditorEffectGroup? Effect => ImageEditorEffectCatalog.Find(Name);
    public string EffectLabel => Effect?.Label ?? WccImageFunctionCatalog.Find(Name)?.Label ?? Name;
    public IReadOnlyList<WccImageFunctionDefinition> FlavorChoices => Effect?.Flavors ?? [];
    public WccImageFunctionDefinition? Flavor
    {
        get => WccImageFunctionCatalog.Find(Name);
        set
        {
            if (value is null || value.Name == Name || !FlavorChoices.Contains(value)) return;
            var preserveSize = Effect?.Id == "resize" && Name != "XBRZ" && value.Name != "XBRZ";
            _arguments = preserveSize ? _arguments : value.DefaultArguments;
            _name = value.Name;
            OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Arguments)); OnPropertyChanged(nameof(Flavor));
            OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(Effect)); OnPropertyChanged(nameof(EffectLabel));
            OnPropertyChanged(nameof(FlavorChoices)); OnPropertyChanged(nameof(CanEditArguments));
        }
    }
    public bool IsOverridden { get => _overridden; internal set { if (SetField(ref _overridden, value)) { OnPropertyChanged(nameof(LayerHint)); OnPropertyChanged(nameof(LayerOpacity)); } } }
    public string LayerHint => IsOverridden ? "Overridden by an enabled layer above with the same effect/flavor or an exclusive resize mode. Drag this layer above it to give it priority." : "Drag to reorder. The top enabled layer takes priority for duplicate effects/flavors.";
    public double LayerOpacity => IsOverridden || !IsEnabled ? 0.48 : 1;
    public bool CanEditArguments => Name.Length > 0;
    public string Arguments { get => _arguments; set { if (CanEditArguments && SetField(ref _arguments, value ?? "")) OnPropertyChanged(nameof(Label)); } }
    public string VisibilityIcon => "avares://Wcc.App/Assets/Wcc/Icons/ImageEditor/visibility_" + (IsEnabled ? "visible" : "hidden") + ".png";
    public bool IsEnabled { get => _enabled; set { if (SetField(ref _enabled, value)) { OnPropertyChanged(nameof(LayerOpacity)); OnPropertyChanged(nameof(VisibilityIcon)); } } }
    public string Label => Name + "(" + Arguments + ")";
    public WccImageFunctionStep ToStep() => (_name == _original.Name && _arguments == _original.Arguments ? _original : WccImageFunctionStep.Create(Name, _arguments)) with { IsEnabled = _enabled };
}

