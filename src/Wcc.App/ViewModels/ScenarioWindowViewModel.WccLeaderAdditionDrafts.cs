using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed record WccScenarioLeaderAdditionDraft(string Identity, string Label, string Summary);

public sealed class WccLeaderAdditionDraftContribution
{
    internal WccLeaderAdditionDraftContribution(object editor, object binding, long version,
        IEnumerable<WccScenarioLeaderAdditionEdit> edits, IEnumerable<string> summaries)
    { Editor = editor; Binding = binding; Version = version; Edits = Array.AsReadOnly(edits.ToArray()); Summaries = Array.AsReadOnly(summaries.ToArray()); }
    internal object Editor { get; }
    internal object Binding { get; }
    internal long Version { get; }
    public IReadOnlyList<WccScenarioLeaderAdditionEdit> Edits { get; }
    public IReadOnlyList<string> Summaries { get; }
    public bool AuthorizesPersistence => false;
}

public partial class ScenarioWindowViewModel
{
    private sealed record PendingLeader(string Identity, WccScenarioLeaderAdditionSnapshot Snapshot,
        WmlStaticScalarProjection Proof, WccInitialLeaderVm Row,
        IReadOnlyDictionary<string, WccScenarioInlineDraft> Fields,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, WccScenarioInlineDraft>> Slots,
        WccScenarioLeaderAddition? PresetAddition = null, WccCampaignPresetSelectionBinding? PresetBinding = null);
    private WccMusicScheduleSourceBinding? _leaderAdditionSourceBinding;
    private IReadOnlyList<PendingLeader> _pendingLeaders = [];
    private long _leaderAdditionVersion;
    private sealed record LeaderAdditionCapability(WmlStaticScalarOwner Owner, WmlStaticScalarProjection Projection,
        WmlCampaignEvaluationPlan Plan, string Text, string Path, WccScenarioLeaderAdditionSnapshot Snapshot);
    private LeaderAdditionCapability? _leaderAdditionCapability;
    public IReadOnlyList<WccScenarioLeaderAdditionDraft> WccLeaderAdditionDrafts => _pendingLeaders.Select(leader =>
        new WccScenarioLeaderAdditionDraft(leader.Identity, "New leader / " + leader.Row.DisplayName,
            (leader.PresetAddition?.Preset is { } preset ? "Use {" + preset.Name + "}: " : "Add nested leader: ") + string.Join(", ", leader.Fields.Select(entry => entry.Key + "=" + entry.Value.Text)
                .Concat(leader.Slots.Select(entry => entry.Key + " by difficulty: " + string.Join(" / ", entry.Value.Values.Select(slot => slot.Text))))))).ToArray();
    public bool HasWccLeaderAdditionDrafts => _pendingLeaders.Count != 0;
    public event EventHandler? WccLeaderAdditionDraftsChanged;
    private bool UsesCheckedLeaderAuthoring => _sourceHostSession.Scope == WccEditorSessionScope.Project && _currentScenarioAsset is not null;
    internal bool IsWccPendingLeaderOwner(WmlStaticScalarOwner owner) => _pendingLeaders.Any(leader => ReferenceEquals(leader.Row.Owner, owner));
    internal bool IsWccPendingPresetLeaderOwner(WmlStaticScalarOwner owner) => FindPendingLeader(owner)?.PresetAddition is not null;
    private PendingLeader? FindPendingLeader(WmlStaticScalarOwner owner) => _pendingLeaders.FirstOrDefault(leader => ReferenceEquals(leader.Row.Owner, owner));
    private string? LeaderAdditionBindingProblem() => _leaderAdditionSourceBinding is { } binding
        ? GetWccMusicScheduleBindingProblem(binding)?.Replace("Music and schedule", "New leader", StringComparison.Ordinal)
            ?? (_pendingLeaders.Any(leader => leader.PresetBinding?.IsCurrent == false) ? "The campaign preset source or context changed. Pending leader invocations remain; discard them and reopen the picker." : null)
            ?? (_collectedSaveRecoveryPending ? "A previous save requires recovery. New leader changes are retained." : null)
        : HasWccLeaderAdditionDrafts ? "The new leader has no captured source binding." : null;
    private bool CanStageWccLeaderAddition => !IsSynchronizingUi && CanCaptureWccMusicScheduleSource() && !_collectedSaveRecoveryPending
        && !_changingWccDifficultyDrafts && HasCurrentStaticScalarProjection && SelectedStaticSideOwner is not null
        && _staticScenarioTopologyProjection is not null && _sourceEvaluationPlan is not null
        && _pendingLeaders.Count < 256 && LeaderAdditionBindingProblem() is null && WccSideTopologyOwnerProblem(SelectedStaticSideOwner) is null
        && GetLeaderAdditionCapability().CanAdd;

    private WccScenarioLeaderAdditionSnapshot GetLeaderAdditionCapability()
    {
        var side = SelectedStaticSideOwner!;
        var text = _pristineLoadedSourceText!;
        var path = (_currentSourcePath ?? LoadedPath)!;
        if (_leaderAdditionCapability is { } cached && ReferenceEquals(cached.Owner, side)
            && ReferenceEquals(cached.Projection, _staticScenarioTopologyProjection) && ReferenceEquals(cached.Plan, _sourceEvaluationPlan)
            && cached.Text == text && PathsEqual(cached.Path, path)) return cached.Snapshot;
        var binding = _leaderAdditionSourceBinding ?? CaptureEventSourceBinding();
        var snapshot = WccScenarioLeaderAdditionPlanner.Read(binding.Range.Source, side, _sourceEvaluationPlan!, _staticScenarioTopologyProjection);
        _leaderAdditionCapability = new(side, _staticScenarioTopologyProjection!, _sourceEvaluationPlan!, text, path, snapshot);
        return snapshot;
    }

    private bool StageWccLeaderAddition()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanStageWccLeaderAddition) { Status = LeaderAdditionBindingProblem() ?? "This side needs a current writable literal source before adding a leader."; return false; }
            var binding = _leaderAdditionSourceBinding ?? CaptureEventSourceBinding();
            var side = SelectedStaticSideOwner!;
            var snapshot = WccScenarioLeaderAdditionPlanner.Read(binding.Range.Source, side, _sourceEvaluationPlan!, _staticScenarioTopologyProjection);
            if (!snapshot.CanAdd) { Status = snapshot.Diagnostic; return false; }
            var version = _leaderAdditionVersion;
            WccFieldEditSession.Invalidate();
            if (version != _leaderAdditionVersion || !CanStageWccLeaderAddition || GetWccMusicScheduleBindingProblem(binding) is not null
                || !ReferenceEquals(side, SelectedStaticSideOwner)) return false;
            var identity = Guid.NewGuid().ToString("N");
            // This tiny isolated owner binds presentation only. It is never submitted as an authored scalar owner.
            var text = "[scenario]\n[side]\n[leader]\n[/leader]\n[/side]\n[/scenario]\n";
            var source = new WmlStaticSource(binding.Range.Source.Path + ".pending-leader-" + identity + ".cfg", text);
            var projection = WmlStaticScalarProjection.Create(new(text, [new(0, text, new(source, 0, text.Length), [])], [], [source], [], true));
            var owner = projection.Owners.Single(candidate => candidate.TagName == "leader");
            var row = new WccInitialLeaderVm(owner, false, "New leader", "New nested leader (pending). Save writes this leader and its chosen fields together.");
            _leaderAdditionSourceBinding = binding;
            _pendingLeaders = Array.AsReadOnly(_pendingLeaders.Append(new PendingLeader(identity, snapshot, _staticScenarioTopologyProjection!, row,
                new ReadOnlyDictionary<string, WccScenarioInlineDraft>(new Dictionary<string, WccScenarioInlineDraft>()),
                new ReadOnlyDictionary<string, IReadOnlyDictionary<string, WccScenarioInlineDraft>>(new Dictionary<string, IReadOnlyDictionary<string, WccScenarioInlineDraft>>()))).ToArray());
            _leaderAdditionVersion++;
            WccInitialLeaders.Add(row);
            SelectedWccInitialLeader = row;
            // Explicitly keep the leader role when materializing its initial nested block.
            SetPendingLeaderField(owner, "canrecruit", "yes", WccScenarioFieldValueInputKind.Literal);
            Status = "New nested leader staged. Choose its fields; Save writes them together.";
            RaiseWccLeaderAdditionDraftsChanged();
            return true;
        }
    }

    private IEnumerable<WccInitialLeaderVm> GetWccPendingLeaderRows(WmlStaticScalarOwner side) =>
        _pendingLeaders.Where(leader => leader.Snapshot.SameOwner(side)).Select(leader => leader.Row);

    internal WccScenarioInlineDraft? FindWccPendingLeaderField(WccScenarioFieldRoute? route) =>
        route is not null && FindPendingLeader(route.Owner) is { } leader
            ? leader.Slots.TryGetValue(route.Key, out var slots) ? slots.GetValueOrDefault(route.ContextSemanticKey) : leader.Fields.GetValueOrDefault(route.Key) : null;
    private WccScenarioFieldRoute? GetPendingLeaderRoute(WmlStaticScalarOwner owner, string key) =>
        FindPendingLeader(owner) is not null && owner.OpeningOrigin is { } opening
            ? WccScenarioFieldRouteAnalyzer.Analyze(owner, key, opening.Source, _evaluationContext) : null;
    private string? PendingLeaderFieldProblem(WmlStaticScalarOwner owner) => !IsWccPendingLeaderOwner(owner)
        ? "This new leader is no longer pending." : LeaderAdditionBindingProblem()
          ?? (_leaderAdditionSourceBinding?.Plan?.Contexts.Any(context => context.SemanticKey == _evaluationContext.SemanticKey) != true
              ? "Select one of the campaign's fixed difficulties before editing this new leader." : null)
          ?? (IsWccPendingPresetLeaderOwner(owner) ? "Save the pending preset leader before editing local overrides. Remove leader cancels the invocation." : null)
          ?? (IsReadOnly || !CanCaptureWccMusicScheduleSource() || _changingWccDifficultyDrafts
              ? "Finish the current scenario operation before editing the new leader." : null);

    private bool SetPendingLeaderField(WmlStaticScalarOwner owner, string key, string? value, WccScenarioFieldValueInputKind inputKind)
    {
        if (PendingLeaderFieldProblem(owner) is { } problem) { Status = problem; return false; }
        var leader = FindPendingLeader(owner)!;
        if (!ScenarioInitialFieldCatalog.TryGet("leader", key, out var definition) || !definition.IsEditable) return false;
        var error = inputKind != WccScenarioFieldValueInputKind.Literal ? "New leader fields currently require literal values. Save first to edit expressions."
            : WccScenarioFieldValueCodec.ValidateInput(definition, value, inputKind);
        var fields = leader.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var route = GetPendingLeaderRoute(owner, key)!;
        var field = new WccScenarioInlineDraft("new-leader:" + leader.Identity + ":" + key, route, WmlFieldOperation.Insert,
            value, "New leader / " + key, error) { ValueInputKind = inputKind };
        if (leader.Slots.TryGetValue(key, out var oldSlots))
        {
            var slots = leader.Slots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var contextSlots = oldSlots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            contextSlots[_evaluationContext.SemanticKey] = field;
            slots[key] = new ReadOnlyDictionary<string, WccScenarioInlineDraft>(contextSlots);
            ReplacePendingLeader(leader, leader with { Slots = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, WccScenarioInlineDraft>>(slots) });
        }
        else
        {
            fields[key] = field;
            ReplacePendingLeader(leader, leader with { Fields = new ReadOnlyDictionary<string, WccScenarioInlineDraft>(fields) });
        }
        return true;
    }

    private void ResetPendingLeaderField(WmlStaticScalarOwner owner, string key)
    {
        if (PendingLeaderFieldProblem(owner) is not null || FindPendingLeader(owner) is not { } leader) return;
        var fields = leader.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var slots = leader.Slots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var hadShared = fields.Remove(key);
        if (!slots.Remove(key) && !hadShared) return;
        ReplacePendingLeader(leader, leader with { Fields = new ReadOnlyDictionary<string, WccScenarioInlineDraft>(fields),
            Slots = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, WccScenarioInlineDraft>>(slots) });
    }
    private bool SetPendingLeaderCoordinates(WmlStaticScalarOwner owner, ScenarioMapCoordinate coordinate)
    {
        if (PendingLeaderFieldProblem(owner) is not null || FindPendingLeader(owner) is not { } leader) return false;
        var fields = leader.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var slots = leader.Slots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var component in new[] { (Key: "x", Value: coordinate.X), (Key: "y", Value: coordinate.Y) })
        {
            var field = new WccScenarioInlineDraft("new-leader:" + leader.Identity + ":" + component.Key,
                GetPendingLeaderRoute(owner, component.Key)!, WmlFieldOperation.Insert,
                component.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "New leader / " + component.Key, null);
            if (slots.TryGetValue(component.Key, out var oldSlots))
            {
                var values = oldSlots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                values[_evaluationContext.SemanticKey] = field;
                slots[component.Key] = new ReadOnlyDictionary<string, WccScenarioInlineDraft>(values);
            }
            else fields[component.Key] = field;
        }
        ReplacePendingLeader(leader, leader with
        {
            Fields = new ReadOnlyDictionary<string, WccScenarioInlineDraft>(fields),
            Slots = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, WccScenarioInlineDraft>>(slots)
        });
        return true;
    }
    private void ReplacePendingLeader(PendingLeader old, PendingLeader replacement)
    {
        _pendingLeaders = Array.AsReadOnly(_pendingLeaders.Select(leader => ReferenceEquals(leader, old) ? replacement : leader).ToArray());
        _leaderAdditionVersion++; RaiseWccLeaderAdditionDraftsChanged();
    }
    internal bool HasWccPendingLeaderDifficultyDraft(WmlStaticScalarOwner owner, string key) =>
        FindPendingLeader(owner)?.Slots.ContainsKey(key) == true;
    internal bool IsWccPendingLeaderDifficultyIndependent(WmlStaticScalarOwner owner, string key) =>
        HasWccPendingLeaderDifficultyDraft(owner, key);
    internal string? WccPendingLeaderDifficultyLockReason(WmlStaticScalarOwner owner, string key, string? displayedValue)
    {
        if (PendingLeaderFieldProblem(owner) is { } problem) return problem;
        if (key is "id" or "side" or "save_id") return "Identity fields cannot use separate difficulty values.";
        if (_leaderAdditionSourceBinding?.Plan is not { } plan || plan.Contexts.Count == 0
            || plan.Contexts.Any(context => string.IsNullOrWhiteSpace(context.DifficultyDefine)))
            return "Separate values need the campaign's fixed declared difficulties.";
        if (!ScenarioInitialFieldCatalog.TryGet("leader", key, out var definition) || !definition.IsEditable)
            return "This leader field cannot be edited here.";
        if (displayedValue is null) return "Set a literal value before creating separate difficulty values.";
        if (WccScenarioFieldValueCodec.InferInputKind(displayedValue) != WccScenarioFieldValueInputKind.Literal)
            return "New leader difficulty fields require literal values.";
        return WccScenarioFieldValueCodec.ValidateInput(definition, displayedValue);
    }
    internal bool CanToggleWccPendingLeaderDifficultyField(WmlStaticScalarOwner owner, string key, string? displayedValue) =>
        ReferenceEquals(SelectedWccInitialLeader?.Owner, owner) && WccPendingLeaderDifficultyLockReason(owner, key, displayedValue) is null;
    internal bool ToggleWccPendingLeaderDifficultyField(WmlStaticScalarOwner owner, string key, string? selectedValue)
    {
        if (!CanToggleWccPendingLeaderDifficultyField(owner, key, selectedValue)) return false;
        var leader = FindPendingLeader(owner)!;
        var fields = leader.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var slots = leader.Slots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var context = _evaluationContext;
        WccScenarioInlineDraft Field(WmlEvaluationContext slotContext) => new("new-leader:" + leader.Identity + ":" + key,
            WccScenarioFieldRouteAnalyzer.Analyze(owner, key, owner.OpeningOrigin!.Source, slotContext), WmlFieldOperation.Insert,
            selectedValue, "New leader / " + key, null);
        if (slots.Remove(key)) fields[key] = Field(context);
        else
        {
            fields.Remove(key);
            slots[key] = new ReadOnlyDictionary<string, WccScenarioInlineDraft>(_leaderAdditionSourceBinding!.Plan!.Contexts
                .ToDictionary(slot => slot.SemanticKey, Field, StringComparer.Ordinal));
        }
        ReplacePendingLeader(leader, leader with { Fields = new ReadOnlyDictionary<string, WccScenarioInlineDraft>(fields),
            Slots = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, WccScenarioInlineDraft>>(slots) });
        return true;
    }
    public void RevertWccLeaderAdditionDraft(string identity)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy || _changingWccDifficultyDrafts) return;
            var remaining = _pendingLeaders.Where(leader => leader.Identity != identity).ToArray();
            if (remaining.Length == _pendingLeaders.Count) return;
            _pendingLeaders = Array.AsReadOnly(remaining); _leaderAdditionVersion++;
            if (!HasWccLeaderAdditionDrafts) _leaderAdditionSourceBinding = null;
            RaiseWccLeaderAdditionDraftsChanged();
        }
    }
    public void DiscardWccLeaderAdditionDrafts()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy || _changingWccDifficultyDrafts) return;
            ClearPendingLeaderAdditions(); RaiseWccLeaderAdditionDraftsChanged();
        }
    }
    private void ClearPendingLeaderAdditions() { _pendingLeaders = []; _leaderAdditionSourceBinding = null; _leaderAdditionVersion++; }
    private void RaiseWccLeaderAdditionDraftsChanged()
    {
        CollectedSaveAuthorityChanging?.Invoke();
        OnPropertyChanged(nameof(HasWccLeaderAdditionDrafts)); OnPropertyChanged(nameof(WccLeaderAdditionDrafts));
        RaiseEditingStateChanged(); RaiseInitialScalarFieldsChanged();
        WccLeaderAdditionDraftsChanged?.Invoke(this, EventArgs.Empty);
    }
    private string GetPendingWccLeaderAdditionStamp() => _leaderAdditionVersion + "\u001f" + string.Join("\u001f",
        _pendingLeaders.Select(leader => leader.Identity + ":" + string.Join(";", leader.Fields.Select(field => field.Key + "=" + field.Value.Text)
            .Concat(leader.Slots.Select(field => field.Key + ":" + string.Join(";", field.Value.Select(slot => slot.Key + "=" + slot.Value.Text)))))));

    private bool TryCaptureWccLeaderAdditionContribution(WmlStaticSource source,
        out WccLeaderAdditionDraftContribution? contribution, out string? problem)
    {
        contribution = null; problem = LeaderAdditionBindingProblem();
        if (problem is not null || _leaderAdditionSourceBinding is not { } binding || !SameAuthoredFlagSource(source, binding.Range.Source))
        { problem ??= "The collected source differs from the new leader source."; return false; }
        problem = _pendingLeaders.SelectMany(leader => leader.Fields.Values.Concat(leader.Slots.Values.SelectMany(slots => slots.Values)))
            .FirstOrDefault(field => field.ValidationError is not null)?.ValidationError;
        if (problem is not null) return false;
        try
        {
            var edits = new List<WccScenarioLeaderAdditionEdit>();
            foreach (var group in _pendingLeaders.GroupBy(leader => leader.Snapshot.SideRange!.StartOffset))
            {
                var first = group.First();
                var snapshot = WccScenarioLeaderAdditionPlanner.Read(source, first.Snapshot.SideOwner, binding.Plan!, first.Proof);
                edits.Add(WccScenarioLeaderAdditionPlanner.CreateEdit(snapshot, group.Select(leader => BuildPendingLeaderAddition(leader, binding.Plan!))));
            }
            contribution = new(this, binding, _leaderAdditionVersion, edits, WccLeaderAdditionDrafts.Select(draft => draft.Label + ": " + draft.Summary));
            return true;
        }
        catch (Exception refusal) when (refusal is InvalidOperationException or ArgumentException) { problem = refusal.Message; return false; }
    }
    private static WccScenarioLeaderAddition BuildPendingLeaderAddition(PendingLeader leader, WmlCampaignEvaluationPlan plan) =>
        leader.PresetAddition ?? new WccScenarioLeaderAddition(
            leader.Fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value.Text ?? "")),
            leader.Slots.Select(field => new KeyValuePair<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(field.Key,
                plan.Contexts.Select(context => new WccScenarioDifficultyFieldValue(context.DifficultyDefine!, context.SemanticKey,
                    field.Value[context.SemanticKey].Text)).ToArray())));
    private bool IsCurrentWccLeaderAdditionContribution(WccLeaderAdditionDraftContribution contribution) =>
        HasWccLeaderAdditionDrafts && ReferenceEquals(contribution.Editor, this) && ReferenceEquals(contribution.Binding, _leaderAdditionSourceBinding)
        && contribution.Version == _leaderAdditionVersion && LeaderAdditionBindingProblem() is null;
    private bool CompleteWccLeaderAdditionContribution(WccLeaderAdditionDraftContribution contribution)
    {
        if (!IsCurrentWccLeaderAdditionContribution(contribution)) return false;
        ClearPendingLeaderAdditions(); RaiseWccLeaderAdditionDraftsChanged(); return true;
    }
}
