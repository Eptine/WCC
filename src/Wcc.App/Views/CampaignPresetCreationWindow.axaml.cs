using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class CampaignPresetCreationWindow : Window
{
    public CampaignPresetCreationWindow(CampaignPresetCreationForm form)
    { InitializeComponent(); DataContext = form; Closed += (_, _) => form.Dispose(); }
    private void StageClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignPresetCreationForm form) return;
        try { if (form.Stage()) Close(); else ProblemText.Text = form.Problem; }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { ProblemText.Text = refusal.Message; }
    }
    private async void ConfigureUnitClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignPresetCreationForm form || form.BeginUnitEditor() is not { } model) return;
        using (model)
        {
            var window = new UnitPlacementWindow(model);
            await window.ShowDialog(this);
            if (window.ResultTemplate is { } template && !form.ApplyUnitTemplate(template)) ProblemText.Text = form.Problem;
        }
    }
    private async void ConfigureEventClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CampaignPresetCreationForm form || form.BeginEventEditor(out var spawnUnitEditor) is not { } model) return;
        using (model)
        {
            var window = new EventNodeGraphWindow(model);
            EventGraphCoordinatePicker.Attach(model, window, form.EventMapProvider ?? (() => null), () => form.CanConfigureContent);
            EventGraphUnitPicker.Attach(model, window, spawnUnitEditor!, () => model.CanEdit, form.EventMapProvider ?? (() => null));
            await window.ShowDialog(this);
            if (window.ResultWml is { } text && !form.ApplyEventText(text)) ProblemText.Text = form.Problem;
        }
    }
    private void CancelClicked(object? sender, RoutedEventArgs args) => Close();
    private void AddLeaderClicked(object? sender, RoutedEventArgs args) => (DataContext as CampaignPresetCreationForm)?.ScalarFields?.AddLeader();
    private void RemoveLeaderClicked(object? sender, RoutedEventArgs args) => (DataContext as CampaignPresetCreationForm)?.ScalarFields?.RemoveLeader();
    private async void VisibilityClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is CampaignPresetCreationForm { ScalarFields: { } fields }) await new WccFieldVisibilityWindow(fields.CreateVisibilityDialog()).ShowDialog(this);
    }
}
