using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;

namespace Wcc.App.Views;

public partial class CatalogPickerWindow : Window
{
    public bool WasAccepted { get; private set; }
    public string? SelectedCsv { get; private set; }
    public IReadOnlyList<WccEntityId> SelectedLibraryEntityIds { get; private set; } =
        Array.Empty<WccEntityId>();
    public IReadOnlyList<CatalogPickerSelection> SelectedSelections { get; private set; } =
        Array.Empty<CatalogPickerSelection>();

    public CatalogPickerWindow(CatalogPickerWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void ClearClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is CatalogPickerWindowViewModel vm)
            vm.ClearSelection();
    }

    private void CancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WasAccepted = false;
        SelectedCsv = null;
        Close();
    }

    private void UseSelectionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is CatalogPickerWindowViewModel vm)
        {
            WasAccepted = true;
            SelectedCsv = vm.GetSelectedCsv();
            SelectedLibraryEntityIds = vm.GetSelectedLibraryEntityIds();
            SelectedSelections = vm.GetSelectedSelections();
        }

        Close();
    }
}
