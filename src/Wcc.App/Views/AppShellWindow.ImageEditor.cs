using System;
using System.IO;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.Core;
using Wcc.Core.References;
using Wcc.Core.Projects;

namespace Wcc.App.Views;

public partial class AppShellWindow
{
    private ImageEditorWindow? _imageEditor;
    private void OpenImageEditorClicked(object? sender, RoutedEventArgs args)
    {
        if (_imageEditor is { } existing)
        {
            if (!existing.IsVisible) existing.Show(this);
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var profile = (DataContext as AppShellViewModel)?.ActiveWesnothInstallationProfile;
        var install = profile?.InstallPath;
        var coreRoot = string.IsNullOrWhiteSpace(install) ? null : WccCoreInstallPaths.GetCoreDataRoot(install);
        var colorsPath = coreRoot is null ? null : Path.Combine(coreRoot, "team-colors.cfg");
        string? ColorsHash()
        {
            if (colorsPath is null || !File.Exists(colorsPath)) return null;
            try { using var stream = File.OpenRead(colorsPath); return Convert.ToHexString(SHA256.HashData(stream)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
        }
        var colorsHash = ColorsHash();
        bool Current() => ReferenceEquals(profile, (DataContext as AppShellViewModel)?.ActiveWesnothInstallationProfile) &&
            install == profile?.InstallPath && colorsHash == ColorsHash();
        ImageEditorWindowViewModel? model = null;
        var renderer = new WccImageExpressionRenderer(WccAppPaths.GetImageRenderCacheRoot(),
            renderContextRoot: WccAppPaths.GetWesnothImageRenderContextRoot());
        model = new ImageEditorWindowViewModel("", isCurrent: Current, engineRenderer: async (expression, token) =>
        {
            var openedPath = model?.ResolvedImagePath;
            var result = await renderer.RenderForReviewAsync(install, expression, openedPath, token);
            if (!Current())
                return new(false, null, "The selected Wesnoth installation changed during review.");
            var reviewed = ImageEditorFieldContext.ValidateEngineReview(result, value =>
                ImageEditorFieldContext.ResolveOpenedImageResource(value, openedPath, expression, nested =>
                    WccUnitPreviewPathResolver.ResolveRawImageExpression(openedPath, WccSourceOrigin.ImportedExternal,
                        nested, WccAssetReferenceKind.Image, install)));
            return Current() ? reviewed : new(false, null, "The selected image context changed while checking rendered resources.");
        }, standalone: true);
        model.ConfigureAttachmentRendering(install);
        model.SetPreviewContext(coreRoot is null ? null : WccImageFunctionPreviewContextLoader.TryLoadCore(coreRoot));
        model.SetPreviewImageResolver((basePath, value) => WccUnitPreviewPathResolver.ResolveRawImageExpression(
            basePath, WccSourceOrigin.ImportedExternal, value, WccAssetReferenceKind.Image, install).ResolvedAbsolutePath);
        var window = new ImageEditorWindow(model);
        _imageEditor = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_imageEditor, window)) _imageEditor = null; };
        window.Show(this);
    }
}
