using Avalonia.Controls;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}