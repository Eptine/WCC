using System;
using System.Threading;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Wcc.Core;
using Wcc.Core.References;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

/// <summary>A temporary display route. Applying the editor still uses the input's existing draft binding.</summary>
public sealed record ImageEditorFieldContext(string? InstallPath, Func<string, WccUnitPreviewResolution> Resolve,
    Func<bool> IsCurrent, bool AllowsNestedReferences = false)
{
    private static readonly WccImageExpressionRenderer Renderer = new(WccAppPaths.GetImageRenderCacheRoot(),
        renderContextRoot: WccAppPaths.GetWesnothImageRenderContextRoot());

    // The editor's native comparison uses the configured installation's Core palettes.
    // Match that explicit context locally too; this does not resolve image paths or
    // use Core as a fallback for captured campaign resources.
    internal WccImageFunctionPreviewContext? CapturePreviewContext() => string.IsNullOrWhiteSpace(InstallPath)
        ? null : WccImageFunctionPreviewContextLoader.TryLoadCore(WccCoreInstallPaths.GetCoreDataRoot(InstallPath));

    internal static string? CorePaletteContextNote(string expression) =>
        WccImageFunctionDocument.Parse(expression).Steps.Any(step => step.IsEnabled && step.Name is "RC" or "TC" or "PAL")
            ? "Wesnoth renders with Core palettes only; campaign palette overrides are not included."
            : null;

    internal static ImageEditorEngineResult MapEngineReview(WccImageExpressionRenderResult result, string? contextNote = null)
    {
        var success = result.Status == WccImageExpressionRenderStatus.Rendered && !result.HasNestedReferenceWarning;
        var diagnostic = result.Diagnostic;
        if (result.HasNestedReferenceWarning)
            diagnostic = "Nested image resources are not fully resolved in this Wesnoth render context. " + diagnostic;
        else if (!success && string.IsNullOrWhiteSpace(diagnostic)) diagnostic = "A fresh Wesnoth render is required before applying the expression.";
        if (contextNote is not null) diagnostic = string.IsNullOrWhiteSpace(diagnostic) ? contextNote : diagnostic + "\n" + contextNote;
        return new(success, success ? result.RenderedPath : null, diagnostic)
        { ReviewedResources = success ? result.ReviewedResources.ToArray() : [] };
    }
    internal static WccUnitPreviewResolution ResolveOpenedImageResource(string logicalReference, string? openedPath,
        string expression, Func<string, WccUnitPreviewResolution> resolveOther)
    {
        if (openedPath is not null && logicalReference == WccImageExpressionParser.TryParse(expression)?.BaseReference)
            return new() { HasReference = true, RawExpression = logicalReference, ResolvedAbsolutePath = openedPath,
                SourceLabel = "Explicitly opened image" };
        return resolveOther(logicalReference);
    }
    internal static ImageEditorEngineResult ValidateEngineReview(WccImageExpressionRenderResult result,
        Func<string, WccUnitPreviewResolution> resolve, string? contextNote = null)
    {
        var mapped = MapEngineReview(result, contextNote);
        if (!mapped.IsSuccess) return mapped;
        if (result.ReviewedResources.Count == 0)
            return new(false, null, "The Wesnoth render has no exact resource receipt. Review the current image again.");
        try
        {
            foreach (var pin in result.ReviewedResources)
            {
                var current = resolve(pin.LogicalReference);
                if (current.ResolvedAbsolutePath is not { } path ||
                    !string.Equals(Path.GetFullPath(path), Path.GetFullPath(pin.AbsolutePath),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return new(false, null, "Wesnoth rendered a different resource for '" + pin.LogicalReference +
                        "' than this field's image context. The expression was not applied.");
                using var stream = File.OpenRead(path);
                if (stream.Length > 64L * 1024 * 1024 ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), pin.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(false, null, "The image resource '" + pin.LogicalReference + "' changed after the Wesnoth render. Review it again.");
            }
            return mapped;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return new(false, null, "The rendered image resources are no longer current: " + error.Message); }
    }
    internal async Task<ImageEditorEngineResult> RenderForReviewAsync(string expression, CancellationToken token)
    {
        if (!IsCurrent()) return new(false, null, "The image field or resource context changed. Reopen the editor.");
        var parsed = WccImageExpressionParser.TryParse(expression);
        if (parsed is null) return new(false, null, "The image expression is not resolved.");
        if (!AllowsNestedReferences && parsed.MayContainNestedImageReferences)
            return new(false, null, "Nested image resources are not proven in this captured context. Keep the expression as RAW text.");
        var resolution = Resolve(expression);
        if (!resolution.IsResolved || resolution.ResolvedAbsolutePath is null)
            return new(false, null, resolution.UnavailableReason ?? "The image is not available in this exact resource context.");
        var result = await Renderer.RenderForReviewAsync(InstallPath, expression, resolution.ResolvedAbsolutePath, token);
        if (!IsCurrent()) return new(false, null, "The image field or resource context changed during review.");
        var reviewed = ValidateEngineReview(result, Resolve, CorePaletteContextNote(expression));
        return IsCurrent() ? reviewed : new(false, null, "The image context changed while checking the rendered resources.");
    }
}
