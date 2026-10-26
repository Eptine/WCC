using Avalonia.Controls;
using System;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class ScenarioWindow : Window
{
    private bool _closingAfterSave;
    private bool _closingSavePending;
    public ScenarioWindow()
    {
        InitializeComponent();
        DataContext = new ScenarioWindowViewModel();
        Closing += ScenarioWindowClosing;
        Closed += (_, _) =>
        {
            if (DataContext is ScenarioWindowViewModel vm)
                vm.CancelBackgroundOperations();
        };
    }

    private async void ScenarioWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterSave || DataContext is not ScenarioWindowViewModel vm ||
            (!vm.HasWccInlineDrafts && !vm.IsWccInlineSaveBusy)) return;
        e.Cancel = true;
        if (_closingSavePending) return;
        _closingSavePending = true;
        try
        {
            if (vm.IsWccInlineSaveBusy)
            {
                await vm.WaitForWccInlineSaveAsync();
                if (vm.HasWccInlineDrafts) return;
            }
            else if (!(await vm.SaveWccInlineDraftsAsync()).IsSuccess) return;
            _closingAfterSave = true;
            Close();
        }
        catch (Exception exception)
        {
            vm.Status = "Could not save the pending edits: " + exception.Message;
        }
        finally
        {
            _closingSavePending = false;
        }
    }
}
