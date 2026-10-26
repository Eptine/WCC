using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.Core.Campaigns;

namespace Wcc.App.Views;

public partial class CampaignChapterCreationWindow : Window
{
    public CampaignChapterCreationWindow(CampaignChapterCreationViewModel draft)
    { InitializeComponent(); DataContext = draft; Closed += (_, _) => draft.Dispose(); }
    public WccCampaignChapterSpec? ResultSpec { get; private set; }
    private void BackClicked(object? sender, RoutedEventArgs args) => (DataContext as CampaignChapterCreationViewModel)?.MoveBack();
    private void NextClicked(object? sender, RoutedEventArgs args) => (DataContext as CampaignChapterCreationViewModel)?.MoveNext();
    private void AcceptClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignChapterCreationViewModel draft || !draft.CanCreate || !draft.TryCreateSpec(out var spec)) return;
        ResultSpec = spec; Close(spec);
    }
    private void CancelClicked(object? sender, RoutedEventArgs args) => Close(null);
}
