using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class CampaignDependencyNoticeWindow : Window
{
    private bool _closed;
    private bool _picking;

    public CampaignDependencyNoticeWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _closed = true;
    }

    public CampaignDependencyNoticeWindow(CampaignDependencyNotice notice) : this()
    {
        DataContext = notice;
        SelectionStatus.Text = notice.SourceSelectionStatus;
    }

    private void ContinueClicked(object? sender, RoutedEventArgs args) =>
        Close(CampaignDependencyNoticeDecision.Continue);

    private async void AddSourceFoldersClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || _picking || DataContext is not CampaignDependencyNotice notice) return;
        _picking = true;
        AddSourceFoldersButton.IsEnabled = false;
        SelectionStatus.Text = null;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose the required add-on source folders", AllowMultiple = true });
            if (_closed || !ReferenceEquals(DataContext, notice)) return;
            var paths = folders.Select(folder => folder.Path.LocalPath).Distinct(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
            // Cancelling the folder picker returns to the unresolved-source notice.
            // Continue or closing that notice remains the explicit dismissal.
            if (paths.Length == 0) return;
            Close(new CampaignDependencyNoticeDecision(Array.AsReadOnly(paths)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        { if (!_closed) SelectionStatus.Text = "The source folders could not be selected: " + error.Message; }
        finally
        {
            _picking = false;
            if (!_closed) AddSourceFoldersButton.IsEnabled = true;
        }
    }
}
