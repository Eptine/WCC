using Avalonia.Controls;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.Views.Controls;

public partial class WccFieldImagePreviewView : UserControl
{
    public WccFieldImagePreviewView()
    {
        InitializeComponent();
        IsVisible = false;
        DataContextChanged += (_, _) => SetCurrentValue(IsVisibleProperty, DataContext is WccFieldImagePreviewVm);
    }
}
