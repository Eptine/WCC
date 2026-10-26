using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.Core.Campaigns;

namespace Wcc.App.Views;

public partial class AppShellWindow
{
    private async void AddCampaignChapterClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not AppShellViewModel shell || !shell.CampaignEditor.CanAddCampaignChapter) return;
        if (!await shell.ConfirmSourceNavigationAsync() || !ReferenceEquals(DataContext, shell) ||
            shell.CampaignEditor.BeginCampaignChapterAdd() is not { } draft) return;
        var window = new CampaignChapterCreationWindow(new() { InstallPath = draft.InstallPath, AllowImageFiles = true });
        var spec = await window.ShowDialog<WccCampaignChapterSpec?>(this);
        if (spec is not null && ReferenceEquals(DataContext, shell)) shell.CampaignEditor.ApplyCampaignChapterAdd(draft, spec);
    }
    private async void RemoveCampaignChapterClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not AppShellViewModel shell || !shell.CampaignEditor.CanRemoveCampaignChapter) return;
        if (await shell.ConfirmSourceNavigationAsync() && ReferenceEquals(DataContext, shell))
            shell.CampaignEditor.StageCampaignChapterRemove();
    }
}
