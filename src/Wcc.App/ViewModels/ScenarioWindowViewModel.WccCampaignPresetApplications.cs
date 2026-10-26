using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private CampaignWindowViewModel? _campaignPresetHost;
    internal void ConfigureWccCampaignPresetHost(CampaignWindowViewModel campaign)
    {
        if (ReferenceEquals(campaign, _campaignPresetHost)) { RaiseWccPresetApplicationCapabilities(); return; }
        if (_campaignPresetHost is { } old) { old.PropertyChanged -= CampaignPresetHostChanged; old.CampaignFieldDraftsChanged -= CampaignPresetCatalogChanged; }
        _campaignPresetHost = campaign;
        campaign.PropertyChanged += CampaignPresetHostChanged; campaign.CampaignFieldDraftsChanged += CampaignPresetCatalogChanged;
        RefreshInitialUnitPresetCatalog();
        RaiseWccPresetApplicationCapabilities();
    }
    private void CampaignPresetHostChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(CampaignWindowViewModel.CampaignLocalPresets) or nameof(CampaignWindowViewModel.CurrentProject))
            RefreshInitialUnitPresetCatalog();
        if (args.PropertyName is nameof(CampaignWindowViewModel.IsCampaignUiLoading) or nameof(CampaignWindowViewModel.IsCampaignFieldSaveBusy)
            or nameof(CampaignWindowViewModel.IsReadOnly) or nameof(CampaignWindowViewModel.CampaignLocalPresets)
            or nameof(CampaignWindowViewModel.HasCampaignPresetDrafts) or nameof(CampaignWindowViewModel.CurrentProject)) RaiseWccPresetApplicationCapabilities();
    }
    private void CampaignPresetCatalogChanged(object? sender, EventArgs args) => RaiseWccPresetApplicationCapabilities();
    private void RaiseWccPresetApplicationCapabilities()
    {
        RaiseWccStoryCapabilitiesChanged();
        OnPropertyChanged(nameof(CanAddWccLeaderFromPreset)); OnPropertyChanged(nameof(CanAddWccSideFromPreset));
        OnPropertyChanged(nameof(WccPresetApplicationGuidance));
        OnPropertyChanged(nameof(CanAddWccEventFromPreset)); OnPropertyChanged(nameof(CanManageWccEventPresets));
    }
    public string WccPresetApplicationGuidance => _campaignPresetHost?.CampaignPresetApplicationProblem
        ?? "Choose a saved campaign preset. Instance IDs are retained for carryover; duplicates in this scenario need distinct identities. Save before editing local overrides.";
    private WccCampaignPresetSelectionBinding? CapturePresetSelection(WccCampaignLocalPresetRole role) => _campaignPresetHost?.CaptureCampaignPresetSelection(role);
    private bool HasSavedPreset(WccCampaignLocalPresetRole role) => _campaignPresetHost is { CampaignPresetApplicationProblem: null } host
        && host.AvailableCampaignPresets(role).Count != 0;
    public bool CanAddWccLeaderFromPreset => !IsScenarioUiLoading && UsesCheckedLeaderAuthoring && CanStageWccLeaderAddition && HasSavedPreset(WccCampaignLocalPresetRole.Leader);
    public bool CanAddWccSideFromPreset => !IsScenarioUiLoading && CanOpenWccSideManagement && HasSavedPreset(WccCampaignLocalPresetRole.Side);

    public async Task<bool> PickWccLeaderPresetAsync(Func<WccCampaignPresetPickerVm, Task<WccCampaignPresetSelection?>> pick)
    {
        WccCampaignPresetSelectionBinding selectionBinding; WccMusicScheduleSourceBinding sourceBinding;
        WmlStaticScalarOwner side; long leaderVersion; long topologyVersion;
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanAddWccLeaderFromPreset || CapturePresetSelection(WccCampaignLocalPresetRole.Leader) is not { } current)
            { Status = WccPresetApplicationGuidance; return false; }
            selectionBinding = current; sourceBinding = _leaderAdditionSourceBinding ?? CaptureEventSourceBinding();
            side = SelectedStaticSideOwner!; leaderVersion = _leaderAdditionVersion; topologyVersion = _sideTopologyDraftVersion;
        }
        var selected = await pick(new(selectionBinding));
        if (selected is null) return false;
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(selected.Binding, selectionBinding) || !selectionBinding.ContainsCurrent(selected.Entry)
                || selected.Entry.Role != WccCampaignLocalPresetRole.Leader || leaderVersion != _leaderAdditionVersion
                || topologyVersion != _sideTopologyDraftVersion || !ReferenceEquals(side, SelectedStaticSideOwner)
                || GetWccMusicScheduleBindingProblem(sourceBinding) is not null || !CanAddWccLeaderFromPreset)
            { Status = "The scenario, preset catalog or pending additions changed. Reopen the picker; newer changes remain."; return false; }
            return StagePresetLeader(selected, sourceBinding, side);
        }
    }

    internal async Task<bool> PickWccSidePresetAsync(WccSideTopologyManagementToken token,
        Func<WccCampaignPresetPickerVm, Task<WccCampaignPresetSelection?>> pick)
    {
        WccCampaignPresetSelectionBinding selectionBinding;
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanAddWccSideFromPreset || SideTopologyTokenProblem(token) is not null || !token.Snapshot.CanAdd
                || CapturePresetSelection(WccCampaignLocalPresetRole.Side) is not { } current)
            { Status = WccPresetApplicationGuidance; return false; }
            selectionBinding = current;
        }
        var selected = await pick(new(selectionBinding));
        if (selected is null) return false;
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(selected.Binding, selectionBinding) || !selectionBinding.ContainsCurrent(selected.Entry) || selected.Entry.Role != WccCampaignLocalPresetRole.Side
                || !CanAddWccSideFromPreset || SideTopologyTokenProblem(token) is not null)
            { Status = "The scenario, preset catalog or pending sides changed. Reopen the picker; newer changes remain."; return false; }
            try { return StageSideTopologyIntent(token, "add", null, selected); }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { Status = refusal.Message; return false; }
        }
    }

    private bool StagePresetLeader(WccCampaignPresetSelection selection, WccMusicScheduleSourceBinding binding, WmlStaticScalarOwner side)
    {
        try
        {
            var additions = selection.IdentityOverride is { } id ? new[] { new KeyValuePair<string, string>("id", id) } : [];
            var payload = WccScenarioLeaderAddition.FromPreset(selection.Entry, additions);
            var snapshot = WccScenarioLeaderAdditionPlanner.Read(binding.Range.Source, side, binding.Plan!, _staticScenarioTopologyProjection);
            var siblings = _pendingLeaders.Where(leader => leader.Snapshot.SameOwner(side)).Select(leader => BuildPendingLeaderAddition(leader, binding.Plan!)).Append(payload).ToArray();
            WccScenarioLeaderAdditionPlanner.CreateEdit(snapshot, siblings);
            if (PresetIdentityClash(selection.Entry.Payload!, selection.IdentityOverride, WccCampaignLocalPresetRole.Leader) is { } clash)
            { Status = clash; return false; }
            var version = _leaderAdditionVersion;
            WccFieldEditSession.Invalidate();
            if (version != _leaderAdditionVersion || !ReferenceEquals(side, SelectedStaticSideOwner) || !selection.Binding.IsCurrent
                || !CanAddWccLeaderFromPreset || GetWccMusicScheduleBindingProblem(binding) is not null) return false;
            var identity = Guid.NewGuid().ToString("N");
            var text = "[scenario]\n[side]\n[leader]\n[/leader]\n[/side]\n[/scenario]\n";
            var source = new WmlStaticSource(binding.Range.Source.Path + ".pending-leader-" + identity + ".cfg", text);
            var projection = WmlStaticScalarProjection.Create(new(text, [new(0, text, new(source, 0, text.Length), [])], [], [source], [], true));
            var owner = projection.Owners.Single(candidate => candidate.TagName == "leader");
            var row = new WccInitialLeaderVm(owner, false, selection.Entry.Name,
                "Campaign Leader preset " + selection.Entry.Name + " (pending). Save before editing local overrides. The shared definition is unchanged.");
            var fields = selection.Entry.Payload!.Values.Concat(payload.Values).GroupBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
            WccScenarioInlineDraft Field(string key, string value, WmlEvaluationContext context) => new("preset-leader:" + identity + ":" + key,
                WccScenarioFieldRouteAnalyzer.Analyze(owner, key, source, context), WmlFieldOperation.Insert, value, "Preset leader / " + key, null);
            var shared = fields.ToDictionary(pair => pair.Key, pair => Field(pair.Key, pair.Value, _evaluationContext), StringComparer.Ordinal);
            var slots = selection.Entry.Payload.DifficultyValues.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyDictionary<string, WccScenarioInlineDraft>)new ReadOnlyDictionary<string, WccScenarioInlineDraft>(binding.Plan!.Contexts.ToDictionary(context => context.SemanticKey,
                    context => Field(pair.Key, pair.Value.Single(slot => slot.Define == context.DifficultyDefine).ValueText!, context), StringComparer.Ordinal)), StringComparer.Ordinal);
            var pending = new PendingLeader(identity, snapshot, _staticScenarioTopologyProjection!, row,
                new ReadOnlyDictionary<string, WccScenarioInlineDraft>(shared), new ReadOnlyDictionary<string, IReadOnlyDictionary<string, WccScenarioInlineDraft>>(slots), payload, selection.Binding);
            _leaderAdditionSourceBinding = binding; _pendingLeaders = Array.AsReadOnly(_pendingLeaders.Append(pending).ToArray()); _leaderAdditionVersion++;
            WccInitialLeaders.Add(row); SelectedWccInitialLeader = row;
            Status = "Leader preset invocation staged. Save before editing local overrides."; RaiseWccLeaderAdditionDraftsChanged(); return true;
        }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { Status = refusal.Message; return false; }
    }

    private string? PresetIdentityClash(WccCampaignLocalPresetPayload payload, string? identityOverride, WccCampaignLocalPresetRole role)
    {
        // Early feedback only. Core checks fresh every-context effective identities, including generated initial units.
        var ids = role == WccCampaignLocalPresetRole.Leader
            ? new[] { identityOverride ?? payload.Values.GetValueOrDefault("id") }
            : payload.Leaders.Select(leader => leader.Values.GetValueOrDefault("id"));
        var initialSides = _staticScenarioOwner?.DirectChildren.Where(owner => owner.TagName == "side"
            && !_sideTopologyRemovals.Any(removal => removal.SameOwner(owner))).ToArray() ?? [];
        var initialUnits = initialSides.SelectMany(side => side.DirectChildren.Where(owner => owner.TagName is "leader" or "unit"))
            .Concat(initialSides.Where(side => WccScenarioFieldValueCodec.DecodeLiteral(side.GetKnownValue("no_leader")) is not ("yes" or "true")
                && WccScenarioFieldValueCodec.DecodeLiteral(side.GetKnownValue("type")) is { Length: > 0 } type && type != "null"))
            .Concat(_staticScenarioOwner?.DirectChildren.Where(owner => owner.TagName == "unit") ?? []);
        // Pending ID changes are checked on the complete candidate. A pristine ID
        // cannot give useful early collision feedback for an owner being edited.
        bool HasPendingIdentity(WmlStaticScalarOwner owner) => _wccInlineDrafts.Any(draft => draft.Route.Key == "id"
            && (ReferenceEquals(draft.Route.Owner, owner) || draft.Route.Owner.TagName == owner.TagName
                && draft.Route.Owner.OpeningOrigin is { } left && owner.OpeningOrigin is { } right
                && SameAuthoredFlagSource(left.Source, right.Source) && left.StartOffset == right.StartOffset && left.Length == right.Length));
        var existing = initialUnits.Where(owner => !HasPendingIdentity(owner))
            .Select(owner => WccScenarioFieldValueCodec.DecodeLiteral(owner.GetKnownValue("id"))).Where(value => !string.IsNullOrEmpty(value))
            .Concat(_pendingLeaders.Select(leader => leader.Fields.GetValueOrDefault("id")?.Text))
            .Concat(_sideTopologyAdditions.Where(addition => addition.Recipe.Preset?.Payload is not null)
                .SelectMany(addition => addition.Recipe.Preset!.Payload!.Leaders.Select(leader => leader.Values.GetValueOrDefault("id")))).ToHashSet(StringComparer.Ordinal);
        var clash = ids.FirstOrDefault(id => !string.IsNullOrEmpty(id) && existing.Contains(id));
        return clash is null ? null : "Unit ID '" + clash + "' already exists in this scenario. Choose a distinct Instance unit ID; a Side preset with repeated nested IDs needs another preset or RAW.";
    }
}
