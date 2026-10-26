using Avalonia.Controls;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class CampaignWindow : Window
{
    public CampaignWindow()
    {
        InitializeComponent();
        DataContext = new CampaignWindowViewModel();
    }
}