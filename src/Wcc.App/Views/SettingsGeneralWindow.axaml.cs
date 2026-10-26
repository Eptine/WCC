using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class SettingsGeneralWindow : Window
{
    public SettingsGeneralWindow(AppShellViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void AddWesnothProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.AddWesnothInstallationProfile();
    }

    private void ApplyWesnothProfileNameClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.ApplyActiveWesnothInstallationProfileName();
    }

    private async void SetWesnothInstallClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = System.OperatingSystem.IsLinux()
                ? "Select Wesnoth game data folder (containing data/core)"
                : "Select Wesnoth install folder",
            AllowMultiple = false
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder is null)
            return;

        if (vm.SetWesnothInstallPath(folder.Path.LocalPath))
            await vm.RefreshConfiguredLibraryIndexAsync();
    }

    private async void SetAddonsPathClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Wesnoth add-ons folder",
            AllowMultiple = false
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder is null)
            return;

        if (vm.SetAddonsPath(folder.Path.LocalPath))
            await vm.RefreshConfiguredLibraryIndexAsync();
    }

    private async void RefreshPackageIndexClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            await vm.RefreshConfiguredLibraryIndexAsync();
    }

    private async void ClearAddonsPathClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm)
            return;

        if (vm.ClearAddonsPath())
            await vm.RefreshConfiguredLibraryIndexAsync();
    }
}
