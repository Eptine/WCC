using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.App.Views.Controls;

namespace Wcc.App.Views;

public partial class ImageEditorWindow
{
    private async void BrowseEffectImageClicked(object? sender, RoutedEventArgs args)
    {
        if (!_model.CanEdit) return;
        var selected = _model.SelectedModifier; var generation = _model.DraftGeneration;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Choose effect image (preview and PNG export only)", AllowMultiple = false,
            FileTypeFilter = [new("Images") { Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg", "*.bmp"] }] });
        if (!_model.CanEdit || generation != _model.DraftGeneration || !ReferenceEquals(selected, _model.SelectedModifier) ||
            files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try { _model.AttachEffectImage(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { _model.ReportStatus(error.Message); }
    }
    private async Task SaveRenderedImageAsync(ImageEditorComparison comparison)
    {
        if (await _model.AcceptComparisonAsync(comparison, forImageExport: true) is null) return;
        if (!comparison.TryAcquireLease(out var lease)) return;
        using (lease)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Save Wesnoth rendered image", SuggestedFileName = "rendered-image.png", DefaultExtension = "png",
                FileTypeChoices = [new("PNG image") { Patterns = ["*.png"] }], ShowOverwritePrompt = true });
            if (file is null || await _model.AcceptComparisonAsync(comparison, forImageExport: true) is null) return;
            // Encode before opening the destination; cancellation or a stale draft cannot truncate it.
            var png = comparison.EnginePngBytes;
            if (png.Length == 0) throw new InvalidOperationException("The reviewed PNG snapshot is unavailable. Preview again before saving.");
            if (file.TryGetLocalPath() is { } path && (_model.IsSourceImagePath(path) || IsProtectedLocalPath(path)))
            { _model.ReportStatus("Choose another destination to keep source images, project metadata and application files unchanged."); return; }
            await using var output = await file.OpenWriteAsync();
            await output.WriteAsync(png); if (output.CanSeek) output.SetLength(output.Position);
            _model.ReportStatus("Wesnoth rendered PNG saved.");
        }
    }
    private static bool IsProtectedLocalPath(string path)
    {
        var name = Path.GetFileName(path);
        return !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".", StringComparison.Ordinal) || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals(".wcc", StringComparison.OrdinalIgnoreCase));
    }
}
