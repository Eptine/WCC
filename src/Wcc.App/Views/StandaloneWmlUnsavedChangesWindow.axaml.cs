using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class StandaloneWmlUnsavedChangesWindow : Window
{
    public StandaloneWmlUnsavedChangesWindow() => InitializeComponent();

    public StandaloneWmlUnsavedChangesWindow(StandaloneWmlFileTab tab) : this()
    {
        ArgumentNullException.ThrowIfNull(tab);
        DataContext = tab;
    }

    public StandaloneWmlUnsavedChangesWindow(string fileTitle) : this()
    {
        ArgumentNullException.ThrowIfNull(fileTitle);
        FilePathText.Text = fileTitle;
    }

    private void SaveClicked(object? sender, RoutedEventArgs args) => Close(StandaloneWmlCloseDecision.Save);
    private void DiscardClicked(object? sender, RoutedEventArgs args) => Close(StandaloneWmlCloseDecision.Discard);
    private void CancelClicked(object? sender, RoutedEventArgs args) => Close(StandaloneWmlCloseDecision.Cancel);
}
