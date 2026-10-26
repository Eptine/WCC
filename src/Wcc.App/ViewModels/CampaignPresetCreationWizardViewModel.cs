using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed record CampaignPresetTypeChoice(WccCampaignLocalPresetRole Role, string Label, string Description);

/// <summary>The type step dispatches to the existing detached preset form; it does not own another editor or save route.</summary>
public sealed class CampaignPresetCreationWizardViewModel : ViewModelBase
{
    private readonly Func<WccCampaignLocalPresetRole, CampaignPresetCreationForm?> _beginFields;
    private readonly Func<string?> _currentProblem;
    private CampaignPresetTypeChoice? _selectedType;
    private bool _opened;
    private string? _problem;
    internal CampaignPresetCreationWizardViewModel(Func<WccCampaignLocalPresetRole, CampaignPresetCreationForm?> beginFields,
        Func<string?> currentProblem, WccCampaignLocalPresetRole? preselectedRole = null)
    {
        _beginFields = beginFields; _currentProblem = currentProblem;
        RequiresTypeSelection = preselectedRole is null;
        if (preselectedRole is { } role)
            _selectedType = Types.SingleOrDefault(choice => choice.Role == role)
                ?? throw new ArgumentException("Choose a supported preset type.", nameof(preselectedRole));
    }
    public IReadOnlyList<CampaignPresetTypeChoice> Types { get; } =
    [
        new(WccCampaignLocalPresetRole.Side, "Side", "Reusable side settings, with optional nested leaders."),
        new(WccCampaignLocalPresetRole.Leader, "Leader", "Reusable recruiting leader settings for a side."),
        new(WccCampaignLocalPresetRole.Unit, "Unit", "Reusable named unit settings; the scenario supplies placement."),
        new(WccCampaignLocalPresetRole.Event, "Event", "Reusable trigger and actions configured with the event editor.")
    ];
    public CampaignPresetTypeChoice? SelectedType
    {
        get => _selectedType;
        set
        {
            if (_opened || value is not null && !Types.Contains(value) || !SetField(ref _selectedType, value)) return;
            Problem = null;
            OnPropertyChanged(nameof(CanContinue));
        }
    }
    public bool RequiresTypeSelection { get; }
    public bool CanContinue => !_opened && SelectedType is not null && _currentProblem() is null;
    public string? Problem { get => _problem; private set => SetField(ref _problem, value); }
    public CampaignPresetCreationForm? BeginFields()
    {
        if (_opened) { Problem = "This wizard already opened its preset form."; return null; }
        if (_currentProblem() is { } stale) { Problem = stale; OnPropertyChanged(nameof(CanContinue)); return null; }
        if (SelectedType is not { } type) { Problem = "Choose a preset type to continue."; return null; }
        var form = _beginFields(type.Role);
        if (form is null) { Problem = _currentProblem() ?? "The preset form could not be opened. Reopen Add preset in the current campaign."; return null; }
        _opened = true; Problem = null; OnPropertyChanged(nameof(CanContinue));
        return form;
    }
}