using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

public sealed partial class ImageEditorWindowViewModel
{
    private readonly Dictionary<string, string> _temporaryImages = new(StringComparer.Ordinal);
    private string? _attachmentInstallPath;
    // Conservative even for custom functions or whitespace spelling that an engine scanner cannot prove.
    public bool HasTemporaryResources => _temporaryImages.Keys.Any(reference => Expression.Contains(reference, StringComparison.Ordinal));
    public bool CanReturnExpression => CanEdit && !HasTemporaryResources;

    public void ConfigureAttachmentRendering(string? installPath) => _attachmentInstallPath = installPath;

    public bool IsSourceImagePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return new[] { _resolvedImagePath, _previewContext?.SourcePath }.Concat(_temporaryImages.Values).Concat(_nestedPins.Select(pin => pin.Path))
            .Concat(_comparison?.ReviewedResources.Select(pin => pin.AbsolutePath) ?? [])
            .Any(source => source is not null && string.Equals(fullPath, Path.GetFullPath(source), comparison));
    }

    public void AttachEffectImage(string path)
    {
        if (!CanEdit || _selected is not { Name: "BLIT" or "MASK" or "L" } row) return;
        var loaded = DecodeSource(path); // Same byte/pixel bounds as the base image.
        var reference = "wcc-editor-attachment-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path).ToLowerInvariant();
        _temporaryImages.Add(reference, Path.GetFullPath(path));
        var arguments = WccImageFunctionDocument.SplitArguments(row.Arguments).ToArray();
        if (arguments.Length == 0) arguments = [reference]; else arguments[0] = reference;
        row.Arguments = string.Join(",", arguments);
        ReportStatus("Effect image attached for preview and PNG export only. Campaign resource import/path handling is still pending.");
    }
    private async Task<ImageEditorEngineResult> RenderEditorEngineAsync(string expression, CancellationToken token)
    {
        if (!HasTemporaryResources) return await _engineRenderer!(expression, token);
        if (_resolvedImagePath is null) return new(false, null, "Open the base image first.");
        var basePath = _resolvedImagePath;
        var temporary = new Dictionary<string, string>(_temporaryImages, StringComparer.Ordinal);
        var resolver = _previewImageResolver;
        var resolverWithBase = _previewImageResolverWithBase;
        return await Task.Run(async () =>
        {
            // An isolated explicit resource bundle: no campaign writes or resolver search expansion.
            var stage = Path.Combine(WccAppPaths.GetImageRenderCacheRoot(), "editor-attachments", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var mappings = new Dictionary<string, (string File, WccImageRenderResourcePin Pin)>(StringComparer.Ordinal);
            long totalBytes = 0;
            string StageReference(string reference, bool isBase)
            {
                token.ThrowIfCancellationRequested();
                var key = (isBase ? "base:" : "nested:") + reference;
                if (mappings.TryGetValue(key, out var prior)) return prior.File;
                if (mappings.Count >= 33) throw new InvalidOperationException("Too many nested image resources for export.");
                var path = isBase ? basePath : temporary.GetValueOrDefault(reference) ??
                    (resolverWithBase is not null ? resolverWithBase(basePath, reference) : resolver?.Invoke(reference));
                if (path is null) throw new InvalidOperationException("This resource is unavailable in the current image context: " + reference);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 64L * 1024 * 1024 || (totalBytes += info.Length) > 128L * 1024 * 1024)
                    throw new InvalidOperationException("Effect resources exceed the bounded export size.");
                var bytes = ReadBoundedImage(path);
                if (bytes.LongLength != info.Length) throw new IOException("An image changed during export staging.");
                var filename = "image-" + mappings.Count + Path.GetExtension(path).ToLowerInvariant();
                File.WriteAllBytes(Path.Combine(stage, filename), bytes);
                mappings.Add(key, (filename, new(reference, Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(bytes)))));
                return filename;
            }
            string Rewrite(string value, bool isBase, int depth)
            {
                if (depth > 32) throw new InvalidOperationException("Nested image expression exceeds the export depth limit.");
                var document = WccImageFunctionDocument.Parse(value);
                if (!document.IsComplete) throw new InvalidOperationException("Fix the image expression before exporting it.");
                var steps = document.Steps.Select(step =>
                {
                    if (step.Name is not ("BLIT" or "MASK" or "L" or "LAYER")) return step;
                    var arguments = WccImageFunctionDocument.SplitArguments(step.Arguments).ToArray();
                    if (arguments.Length == 0 || string.IsNullOrWhiteSpace(arguments[0])) throw new InvalidOperationException("Choose an image for " + step.Name + ".");
                    arguments[0] = Rewrite(arguments[0], false, depth + 1);
                    return WccImageFunctionStep.Create(step.Name, string.Join(",", arguments));
                }).ToArray();
                return document.WithBaseReference(StageReference(document.BaseReference, isBase)).WithSteps(steps).BuildExpression();
            }
            var stagedExpression = Rewrite(expression, true, 0);
            var stagedBase = Path.Combine(stage, mappings["base:" + WccImageFunctionDocument.Parse(expression).BaseReference].File);
            var renderer = new WccImageExpressionRenderer(WccAppPaths.GetImageRenderCacheRoot(), renderContextRoot: WccAppPaths.GetWesnothImageRenderContextRoot());
            var result = await renderer.RenderForReviewAsync(_attachmentInstallPath, stagedExpression, stagedBase, token);
            var reviewed = ImageEditorFieldContext.MapEngineReview(result, ImageEditorFieldContext.CorePaletteContextNote(expression));
            if (!reviewed.IsSuccess) return reviewed;
            if (result.ReviewedResources.Count != mappings.Count || result.ReviewedResources.Any(pin => !mappings.Values.Any(mapping => mapping.File == pin.LogicalReference && string.Equals(mapping.Pin.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))))
                return new ImageEditorEngineResult(false, null, "The attachment export resource receipts did not match the selected images.");
            return reviewed with { Diagnostic = "Temporary effect images: PNG export only. Campaign path handling is pending.\n" + reviewed.Diagnostic,
                ReviewedResources = mappings.Values.Select(mapping => mapping.Pin).ToArray() };
        }, token);
    }
}
