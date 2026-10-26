using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Wcc.App.Preview;
using Wcc.Core;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>Detached forms reuse the indexed unit loader, path resolver and Wesnoth renderer.</summary>
internal sealed class WccPresetFieldImagePreviews(WccLibrarySnapshot snapshot, WccPackageRevisionId root,
    string mainPath, string? installPath, Func<bool> isCurrent, WmlStaticSource? capturedSource = null,
    WccCapturedImageResourceContext? capturedResources = null) : IDisposable
{
    private sealed record DependencyIconRequest(WccStaticSourceCaptureResult? Capture,
        WccPackageRevisionId? Provider, string AddonId, bool Core);
    private sealed record Request(string Key, string? Value, string? Type)
    { public DependencyIconRequest? DependencyIcon { get; init; } }
    private sealed record Result(WccUnitPreviewResolution Resolution, Bitmap? Bitmap, bool Inherited, bool CompositeIsUnproved)
    { public string? Status { get; init; } }
    private readonly Dictionary<(WmlStaticScalarOwner, string), WccFieldImagePreviewVm> _previews = new();
    private readonly IReadOnlySet<WccPackageRevisionId> _visible = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root);
    private static readonly WccImageExpressionRenderer Renderer = new(WccAppPaths.GetImageRenderCacheRoot());
    private bool _disposed;

    // The host captures immutable source evidence, but the first preview worker
    // builds its resource projection. A property getter must not do that work.
    internal Lazy<WccCapturedImageResourceContext?>? CapturedResourceContext { private get; init; } = new(() => capturedResources);
    public WccFieldImagePreviewVm Get(WmlStaticScalarOwner owner, string key, Func<string, string?> value)
    {
        if (!_previews.TryGetValue((owner, key), out var preview)) _previews[(owner, key)] = preview = new();
        if (_disposed || !isCurrent()) { preview.Dispose(); return preview; }
        var request = new Request(key, key == "unit_sprite" ? null : value(key), value("type"));
        if (!Equals(request, preview.RequestIdentity))
        {
            var work = preview.Begin(request);
            _ = Load(preview, request, work.Version, work.Token, work.Completion);
        }
        return preview;
    }
    internal WccFieldImagePreviewVm GetDependencyIcon(WmlStaticScalarOwner owner, string key,
        WccStaticSourceCaptureResult? capture, WccPackageRevisionId? provider, string addonId,
        string? declaredIcon, bool core)
    {
        if (!_previews.TryGetValue((owner, key), out var preview)) _previews[(owner, key)] = preview = new();
        if (_disposed || !isCurrent()) { preview.Dispose(); return preview; }
        var request = new Request(key, declaredIcon, null) { DependencyIcon = new(capture, provider, addonId, core) };
        if (!Equals(request, preview.RequestIdentity))
        {
            var work = preview.Begin(request);
            _ = Load(preview, request, work.Version, work.Token, work.Completion);
        }
        return preview;
    }
    private async Task Load(WccFieldImagePreviewVm preview, Request request, long version,
        CancellationToken token, TaskCompletionSource completion)
    {
        Bitmap? bitmap = null;
        try
        {
            // Keep host predicates and notifications on the calling UI context;
            // only immutable request data crosses into the preview worker.
            await Task.Yield();
            if (_disposed || token.IsCancellationRequested || !Equals(request, preview.RequestIdentity) || !isCurrent()) return;
            var result = await Task.Run(() => ResolveAndRenderAsync(request, token), token);
            bitmap = result.Bitmap;
            var resolution = result.Resolution;
            if (!_disposed && isCurrent() && !token.IsCancellationRequested && Equals(request, preview.RequestIdentity))
            {
                preview.Publish(version, bitmap, resolution.ResolvedAbsolutePath, resolution.RawExpression,
                    result.Status ?? (resolution.IsSuppressed ? "Portrait disabled." : resolution.IsResolved ? result.CompositeIsUnproved
                        ? "Base image preview only; composite modifier resources are not proven in this captured package."
                        : result.Inherited ? "Preview from the exact visible unit type." : capturedSource is null ? "Preset image preview." : "Captured campaign image preview."
                    : "No image was resolved in this campaign context."), result.Inherited);
                bitmap = null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        { if (!_disposed && isCurrent() && Equals(request, preview.RequestIdentity)) preview.Publish(version, null, null, request.Value, "Image preview unavailable: " + exception.Message, false); }
        finally { bitmap?.Dispose(); completion.TrySetResult(); }
    }

    private async Task<Result> ResolveAndRenderAsync(Request request, CancellationToken token)
    {
        Bitmap? bitmap = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var portrait = request.Key is "profile" or "small_profile";
            var kind = portrait ? WccAssetReferenceKind.Portrait : WccAssetReferenceKind.Image;
            var inherited = false;
            WccUnitPreviewResolution resolution;
            var dependency = request.DependencyIcon is { } icon
                ? WccDependencyIconPreview.Resolve(icon.Capture, icon.Provider, icon.AddonId, request.Value, icon.Core) : null;
            if (dependency is not null) resolution = dependency.Resolution;
            else if (portrait && request.Value == "none") resolution = new() { IsSuppressed = true, HasReference = true, RawExpression = "none" };
            else if (!string.IsNullOrWhiteSpace(request.Value) && !(portrait && request.Value == "unit_image"))
                resolution = capturedSource is null
                    ? WccUnitPreviewPathResolver.ResolveRawImageExpression(mainPath, WccSourceOrigin.ImportedExternal, request.Value, kind, installPath)
                    : WccCapturedImagePreviewPathResolver.Resolve(snapshot, root, capturedSource, request.Value, kind);
            else
            {
                if (portrait && request.Value == "unit_image") kind = WccAssetReferenceKind.Image;
                var entities = snapshot.FindEntities(WccEntityTypes.UnitType).Where(entity => entity.ExportSymbol == request.Type &&
                    snapshot.FindPackageForEntity(entity.Id)?.IsAvailable == true && WccLibraryRevisionVisibility.ContainsEntity(snapshot, _visible, entity)).Take(2).ToArray();
                resolution = new();
                if (entities.Length == 1)
                {
                    var entity = entities[0];
                    var loaded = await WccLibraryUnitEntityLoader.LoadAsync(snapshot, entity.Id, token).ConfigureAwait(false);
                    var documents = loaded.SourceFile.Documents.Where(document => entity.ContentFingerprint == WccContentHash.ComputeText(document.BlockSpan.RawText)).Take(2).ToArray();
                    if (loaded.MatchesIndexedContentHash && documents.Length == 1)
                    { resolution = WccUnitPreviewPathResolver.ResolveKind(loaded.SourceFile, documents[0], kind, installPath); inherited = resolution.IsResolved || resolution.IsSuppressed; }
                }
            }
            // Explicit game-data paths need their captured installation resource
            // aliases, including assets in another mainline campaign. Relative
            // private preset art retains the established package-local preview.
            // Dependency identity art uses its exact provider inventory instead.
            if (dependency is null && !string.IsNullOrWhiteSpace(request.Value) && CapturedResourceContext?.Value is { } resources)
            {
                var explicitResolution = WccCapturedImagePreviewPathResolver.Resolve(resources, request.Value);
                if (WccImageExpressionParser.TryParse(explicitResolution.RawExpression)?.BaseReference
                        .StartsWith("data/", StringComparison.Ordinal) == true)
                    resolution = explicitResolution;
            }
            token.ThrowIfCancellationRequested();
            if (dependency?.Asset is { } asset)
            {
                // Match ordinary bitmap loading: a decoder unavailable in a
                // model-only host does not discard the resolved source identity.
                try { using var stream = AssetLoader.Open(new Uri(asset)); bitmap = new Bitmap(stream); }
                catch { }
            }
            else bitmap = BitmapFileLoader.TryLoad(resolution.ResolvedAbsolutePath);
            var expression = WccImageExpressionParser.TryParse(resolution.RawExpression);
            // The existing renderer may stage nested assets through its broader
            // display route. An exact captured owner does not prove that closure.
            var compositeIsUnproved = capturedSource is not null && expression?.MayContainNestedImageReferences == true;
            if (resolution.IsResolved && expression?.HasModifiers == true && !compositeIsUnproved)
            {
                var rendered = await Renderer.RenderAsync(installPath, resolution.RawExpression, resolution.ResolvedAbsolutePath, token).ConfigureAwait(false);
                if (rendered.IsSuccess && rendered.RenderedPath is { } path)
                { var image = BitmapFileLoader.TryLoad(path); if (image is not null) { bitmap?.Dispose(); bitmap = image; } }
            }
            token.ThrowIfCancellationRequested();
            var result = new Result(resolution, bitmap, inherited, compositeIsUnproved)
            { Status = dependency is null ? null : compositeIsUnproved
                ? dependency.Status + " Base image only; composite modifier resources are not proven in this captured package."
                : dependency.Status };
            bitmap = null;
            return result;
        }
        finally { bitmap?.Dispose(); }
    }
    public void Dispose() { _disposed = true; foreach (var preview in _previews.Values) preview.Dispose(); _previews.Clear(); }
}
