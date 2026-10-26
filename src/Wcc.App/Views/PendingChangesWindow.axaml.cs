using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class PendingChangesWindow : Window
{
    public PendingChangesWindow() => InitializeComponent();
    public PendingChangesWindow(PendingNavigationRequest request) : this() => DataContext = request;
    private void SaveClicked(object? sender, RoutedEventArgs e) => Close(PendingNavigationDecision.Save);
    private void DiscardClicked(object? sender, RoutedEventArgs e) => Close(PendingNavigationDecision.Discard);
    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(PendingNavigationDecision.Cancel);
}
