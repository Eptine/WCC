using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.Core.Campaigns;

namespace Wcc.App.Views;

public partial class CampaignDependencyCreationWindow : Window
{
    private bool _closed;
    public CampaignDependencyCreationViewModel ViewModel { get; }
    public CampaignDependencyCreationWindow(CampaignDependencyCreationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent(); DataContext = ViewModel = viewModel;
        Closed += (_, _) => { _closed = true; ViewModel.Dispose(); };
    }
    private void CancelClicked(object? sender, RoutedEventArgs args) => Close((WccCampaignDependencySpec?)null);
    private void BackClicked(object? sender, RoutedEventArgs args) => ViewModel.MoveBack();
    private void NextClicked(object? sender, RoutedEventArgs args) => ViewModel.MoveNext();
    private void UseRecommendationClicked(object? sender, RoutedEventArgs args) => ViewModel.UseRecommendation();
    private void AddStepClicked(object? sender, RoutedEventArgs args) => ViewModel.AddStep();
    private void RemoveStepClicked(object? sender, RoutedEventArgs args) => ViewModel.RemoveSelectedStep();
    private void MoveStepUpClicked(object? sender, RoutedEventArgs args) => ViewModel.MoveSelectedStep(-1);
    private void MoveStepDownClicked(object? sender, RoutedEventArgs args) => ViewModel.MoveSelectedStep(1);
    private void CreateClicked(object? sender, RoutedEventArgs args)
    {
        if (!_closed && ViewModel.TryCreateSpec(out var spec)) Close(spec);
    }
    private async void ChooseSourceClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || !ViewModel.CanChooseSource) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {Title = "Choose the resource add-on's source folder", AllowMultiple = false});
            if (!_closed && folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
                await ViewModel.SelectSourceFolderAsync(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        { if (!_closed) ViewModel.ReportSourceSelectionFailure(failure.Message); }
    }
}