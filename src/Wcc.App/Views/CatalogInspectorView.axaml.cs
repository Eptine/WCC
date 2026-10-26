using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.Views;

public partial class CatalogInspectorView : UserControl
{
    private async void CreateLibraryUnitTypeClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not AppShellViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var model = await vm.CreateLibraryUnitTypeWizardModelAsync();
        if (model is null) return;
        var window = new UnitTypeCreationWindow(model);
        var request = await window.ShowDialog<UnitTypeCreationRequest?>(owner);
        if (request is not null) await vm.CreateLibraryUnitTypeAsync(request);
    }

    private AppShellViewModel? _subscribedViewModel;

    public CatalogInspectorView()
    {
        InitializeComponent();
        Loaded += ViewLoaded;
        Unloaded += ViewUnloaded;
    }

    private async void ViewLoaded(object? sender, RoutedEventArgs e)
    {
        SubscribeToViewModel();

        if (DataContext is AppShellViewModel vm)
            await vm.PrepareCatalogInspectorTabAsync(CatalogTabs.SelectedIndex);
    }

    private void ViewUnloaded(object? sender, RoutedEventArgs e) =>
        UnsubscribeFromViewModel();

    private void SubscribeToViewModel()
    {
        UnsubscribeFromViewModel();
        _subscribedViewModel = DataContext as AppShellViewModel;

        if (_subscribedViewModel is not null)
            _subscribedViewModel.PropertyChanged += ViewModelPropertyChanged;
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.PropertyChanged -= ViewModelPropertyChanged;

        _subscribedViewModel = null;
    }

    private async void CatalogTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl || DataContext is not AppShellViewModel vm)
            return;

        await vm.PrepareCatalogInspectorTabAsync(CatalogTabs.SelectedIndex);
    }

    private async void LibraryUnitTileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm ||
            sender is not Button button ||
            button.Tag is not UnitTypeCatalogEntryVm selected)
            return;

        await vm.OpenLibraryUnitCatalogEntryAsync(selected);
    }

    private void SaveLibraryUnitClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.SaveLibraryUnit();
    }

    private async void CloneLibraryUnitClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppShellViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var model = await vm.CreateSelectedUnitCopyWizardModelAsync(libraryInspector: true);
        if (model is null) return;
        var request = await new UnitTypeCreationWindow(model).ShowDialog<UnitTypeCreationRequest?>(owner);
        if (request is not null) await vm.CreateLibraryUnitTypeAsync(request);
    }

    private void DeleteLibraryUnitClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppShellViewModel vm)
            vm.DeleteSelectedLibraryUnit();
    }

    private async void ViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppShellViewModel.LibrarySnapshot) &&
            DataContext is AppShellViewModel vm)
        {
            await vm.PrepareCatalogInspectorTabAsync(CatalogTabs.SelectedIndex);
        }
    }
}
