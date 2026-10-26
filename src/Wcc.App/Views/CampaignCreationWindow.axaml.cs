using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.Core.Projects.Creation;

namespace Wcc.App.Views;

public partial class CampaignCreationWindow : Window
{
    private bool _closed;
    public CampaignCreationViewModel ViewModel { get; }

    public CampaignCreationWindow() : this(new CampaignCreationViewModel()) { }

    public CampaignCreationWindow(CampaignCreationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = ViewModel = viewModel;
        Closed += (_, _) => { _closed = true; ViewModel.Dispose(); };
    }

    private void CancelClicked(object? sender, RoutedEventArgs args) => Close((WccCampaignCreationRequest?)null);
    private void BackClicked(object? sender, RoutedEventArgs args) => ViewModel.MoveBack();
    private void NextClicked(object? sender, RoutedEventArgs args) => ViewModel.MoveNext();
    private void AutomaticDomainClicked(object? sender, RoutedEventArgs args) => ViewModel.ResetTextDomain();
    private void CreateClicked(object? sender, RoutedEventArgs args)
    {
        if (!_closed && ViewModel.TryCreateRequest(out var request)) Close(request);
    }

    private async void BrowseParentClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose the new campaign's parent folder", AllowMultiple = false });
            if (!_closed && folders.FirstOrDefault()?.TryGetLocalPath() is { } path) ViewModel.ParentDirectory = path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_closed) ShowPickerError(failure.Message); }
    }

    private void AddChapterClicked(object? sender, RoutedEventArgs args) => ViewModel.AddCreationChapter();
    private void RemoveChapterClicked(object? sender, RoutedEventArgs args) => ViewModel.RemoveSelectedCreationChapter();

    private void ShowPickerError(string text)
    {
        // Picker failures do not change any draft or issue a creation request.
        var notice = new Window
        {
            Title = "File selection", Classes = { "wccShell" }, Width = 500, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(18) }
        };
        _ = notice.ShowDialog(this);
    }
}
