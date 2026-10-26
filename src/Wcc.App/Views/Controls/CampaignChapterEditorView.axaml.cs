using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;

namespace Wcc.App.Views.Controls;

public partial class CampaignChapterEditorView : UserControl
{
    public CampaignChapterEditorView() => InitializeComponent();
    private async void BrowseImageClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignChapterCreationViewModel { AllowImageFiles: true } chapter ||
            ImageFor(sender) is not { } image || TopLevel.GetTopLevel(this) is not { } owner) return;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose " + image.Title.ToLowerInvariant() + " to copy on Create", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Wesnoth images") { Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg", "*.bmp", "*.gif"] }]
            });
            if (ReferenceEquals(DataContext, chapter) && IsEffectivelyVisible && files.FirstOrDefault()?.TryGetLocalPath() is { } path) image.SelectFile(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { if (TopLevel.GetTopLevel(this) is Window window) ShowError(window, error.Message); }
    }
    private async void PreviewImageClicked(object? sender, RoutedEventArgs args)
    { if (ImageFor(sender) is { } image) await image.RenderPreviewAsync(); }
    private static CampaignCreationImageViewModel? ImageFor(object? sender) => sender is Control control ? control.DataContext switch
    { CampaignCreationImageViewModel image => image, CampaignCreationDifficultyViewModel difficulty => difficulty.Image, _ => null } : null;
    private static void ShowError(Window owner, string text)
    {
        var notice = new Window { Title = "Image selection", Classes = { "wccShell" }, Width = 500, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(18) } };
        _ = notice.ShowDialog(owner);
    }
}
