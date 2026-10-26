using System;
using Avalonia.Controls;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class PresetManagerWindow : Window
{
    public PresetManagerWindow(AppShellViewModel shellVm, IPresetManagerAdapter adapter)
    {
        InitializeComponent();
        DataContext = new PresetManagerWindowViewModel(shellVm, adapter);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

}
