using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;
using Wcc.Core.Campaigns;

namespace Wcc.App.Views.Controls;

public partial class CampaignCreationPackageOptionsView : UserControl
{
    public CampaignCreationPackageOptionsView() => InitializeComponent();
    private async void AddDependencyClicked(object? sender, RoutedEventArgs args) => await ShowDependency(null);
    private async void EditDependencyClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignCreationViewModel { SelectedCreationDependency: { } dependency }) await ShowDependency(dependency); }
    private async Task ShowDependency(CampaignCreationDependencyRow? previous)
    {
        if (DataContext is not CampaignCreationViewModel vm || TopLevel.GetTopLevel(this) is not Window owner ||
            vm.CreateDependencyWizardModel(previous) is not { } model) return;
        if (previous is not null) await model.SelectSourceFolderAsync(previous.SourceFolder);
        var dialog = new CampaignDependencyCreationWindow(model);
        var spec = await dialog.ShowDialog<WccCampaignDependencySpec?>(owner);
        if (spec is not null && ReferenceEquals(DataContext, vm)) vm.SetCreationDependency(model, spec);
    }
    private void RemoveDependencyClicked(object? sender, RoutedEventArgs args)
    { if (DataContext is CampaignCreationViewModel vm) vm.RemoveSelectedCreationDependency(); }
}