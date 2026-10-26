using System;
using System.Linq;
using System.Threading.Tasks;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    public bool CanAddWccEventFromPreset => !IsScenarioUiLoading && CanStageWccEvents && HasSavedPreset(WccCampaignLocalPresetRole.Event);
    public bool CanManageWccEventPresets => _campaignPresetHost?.CanOpenCampaignPresets == true;

    public CampaignLocalPresetManagerViewModel? CreateWccEventPresetManager() => CanManageWccEventPresets
        ? new(_campaignPresetHost!) { RoleFilter = "Event", ScopeFilter = "This campaign" } : null;

    public async Task<bool> PickWccEventPresetAsync(Func<WccCampaignPresetPickerVm, Task<WccCampaignPresetSelection?>> pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        WccCampaignPresetSelectionBinding selection;
        WccEventEditDraft token;
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanAddWccEventFromPreset || CapturePresetSelection(WccCampaignLocalPresetRole.Event) is not { } binding
                || BeginWccNewEventDraft() is not { CanEdit: true } opening)
            { Status = WccPresetApplicationGuidance; return false; }
            selection = binding; token = opening;
        }
        var chosen = await pick(new(selection));
        if (chosen is null) return false;
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(chosen.Binding, selection) || !selection.ContainsCurrent(chosen.Entry)
                || chosen.Entry.Role != WccCampaignLocalPresetRole.Event || chosen.IdentityOverride is not null
                || !CanAddWccEventFromPreset || EventTokenProblem(token) is not null)
            { Status = "The event, scenario or saved preset catalog changed. Reopen the picker; existing drafts remain."; return false; }
            var requested = chosen.Entry.Payload?.EventSeed?.Graph;
            if (requested is null) { Status = "Choose one supported saved Event preset."; return false; }
            if (!string.IsNullOrWhiteSpace(requested.EventId) &&
                EventWorkspacePreview?.EventProjection.Events.Any(entry => entry.WmlId == requested.EventId) == true)
            { Status = "Another event in this scenario context already uses the preset's event ID. Choose another preset or inspect its shared source."; return false; }
            var guarded = token.WithAdditionalCurrencyCheck(() => selection.ContainsCurrent(chosen.Entry)
                ? null : "The selected Event preset changed before staging; existing drafts remain.");
            var binding = (WccMusicScheduleSourceBinding)guarded.Binding;
            var recipe = new WccScenarioEventSourceEdit(WccScenarioEventSourceEditKind.AddMacroInvocation,
                EventText: "{" + chosen.Entry.Name + "}") { Preset = chosen.Entry };
            if (WccScenarioEventSourcePlanner.Plan(binding.Range, recipe).Diagnostic is { } problem)
            { Status = problem; return false; }
            if (EventTokenDefinitionProblem(guarded, recipe.EventText) is { } helperProblem)
            { Status = helperProblem; return false; }
            var identity = Guid.NewGuid().ToString("N");
            var next = _eventDrafts.Append(new WccScenarioEventDraft(identity, "Event preset: " + chosen.Entry.Name, recipe)).ToList();
            return PublishEventDraftChange(guarded, binding, next, identity);
        }
    }
}