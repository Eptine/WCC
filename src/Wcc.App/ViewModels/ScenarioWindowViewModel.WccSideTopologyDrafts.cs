using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed record WccScenarioSideTopologyDraft(string Identity, string Label, string Summary);

/// <summary>Immutable collected intent. The global reviewer supplies separate write authority.</summary>
public sealed class WccSideTopologyDraftContribution
{
    internal WccSideTopologyDraftContribution(object editor, object binding, long version,
        WccScenarioSideTopologyEdit edit, IEnumerable<string> summaries)
    { Editor = editor; Binding = binding; Version = version; Edit = edit; Summaries = Array.AsReadOnly(summaries.ToArray()); }
    internal object Editor { get; }
    internal object Binding { get; }
    internal long Version { get; }
    public WccScenarioSideTopologyEdit Edit { get; }
    public IReadOnlyList<string> Summaries { get; }
    public bool AuthorizesPersistence => false;
}

internal sealed record WccSideTopologyManagementToken(object Binding, WccScenarioSideTopologySnapshot Snapshot,
    WmlStaticScalarProjection Projection, long Version);

public partial class ScenarioWindowViewModel
{
    private WccMusicScheduleSourceBinding? _sideTopologySourceBinding;
    private WccScenarioSideTopologySnapshot? _sideTopologySnapshot;
    private WmlStaticScalarProjection? _sideTopologyProofProjection;
    private IReadOnlyList<WccScenarioSideTopologyEntry> _sideTopologyRemovals = [];
    private sealed record PendingSideAddition(WccScenarioSideAddition Recipe, WccCampaignPresetSelectionBinding? PresetBinding = null);
    private IReadOnlyList<PendingSideAddition> _sideTopologyAdditions = [];
    private int _sideTopologyAddedCount => _sideTopologyAdditions.Count;
    private long _sideTopologyDraftVersion;
    private IReadOnlyList<WccScenarioSideTopologyDraft> _sideTopologyDrafts = [];
    public IReadOnlyList<WccScenarioSideTopologyDraft> WccSideTopologyDrafts => _sideTopologyDrafts;
    public bool HasWccSideTopologyDrafts => _sideTopologyRemovals.Count != 0 || _sideTopologyAddedCount != 0;
    public event EventHandler? WccSideTopologyDraftsChanged;

    public bool CanOpenWccSideManagement => CanCaptureWccMusicScheduleSource() && !_collectedSaveRecoveryPending
        && !_changingWccDifficultyDrafts && HasCurrentStaticScalarProjection && _staticScenarioOwner is not null
        && _staticScenarioTopologyProjection is not null && _sourceEvaluationPlan is not null
        && (_sideTopologySourceBinding is null || SideTopologyBindingProblem(_sideTopologySourceBinding) is null);

    public WccSideTopologyManagementVm? BeginWccSideTopologyManagement()
    {
        lock (_wccInitialFieldRefreshGate)
            return CaptureSideTopologyManagementToken() is { } token ? new(this, token) : null;
    }

    internal WccSideTopologyManagementToken? CaptureSideTopologyManagementToken()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanOpenWccSideManagement) return null;
            var binding = _sideTopologySourceBinding ?? CaptureEventSourceBinding();
            var projection = _sideTopologyProofProjection ?? _staticScenarioTopologyProjection!;
            var snapshot = _sideTopologySnapshot ?? WccScenarioSideTopologyPlanner.Read(
                binding.Range.Source, _staticScenarioOwner!, _sourceEvaluationPlan!, projection);
            return new(binding, snapshot, projection, _sideTopologyDraftVersion);
        }
    }

    private string? SideTopologyBindingProblem(WccMusicScheduleSourceBinding binding) =>
        GetWccMusicScheduleBindingProblem(binding)?.Replace("Music and schedule", "Side", StringComparison.Ordinal)
            .Replace("music and schedule", "side", StringComparison.Ordinal)
        ?? (_sideTopologyAdditions.Any(addition => addition.PresetBinding?.IsCurrent == false)
            ? "The campaign preset source or context changed. Pending side invocations remain; discard them and reopen the picker." : null)
        ?? (_collectedSaveRecoveryPending ? "A previous save requires recovery. Side changes are retained." : null);

    internal string? SideTopologyTokenProblem(WccSideTopologyManagementToken token)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (token.Binding is not WccMusicScheduleSourceBinding binding || token.Version != _sideTopologyDraftVersion
                || _sideTopologySourceBinding is not null && !ReferenceEquals(binding, _sideTopologySourceBinding))
                return "The pending side changes changed. Reopen Edit sides; newer changes are retained.";
            return SideTopologyBindingProblem(binding)
                ?? (IsWccInlineSaveBusy || WccFieldEditSession.IsBusy || _changingWccDifficultyDrafts
                    ? "Finish the current scenario operation before changing sides." : null);
        }
    }

    private string? SideRemovalDraftProblem(WccScenarioSideTopologyEntry entry)
    {
        if (_pendingLeaders.Any(leader => leader.Snapshot.SameOwner(entry.SideOwner)))
            return "Revert the pending new leaders for this side before removing it.";
        if (_initialUnitDrafts.Any(unit => unit.Snapshot.SameOwner(entry.SideOwner)))
            return "Revert the pending initial units for this side before removing it.";
        bool Inside(WmlStaticScalarOwner owner)
        {
            while (owner.Parent is not null && owner.TagName != "side") owner = owner.Parent;
            return owner.TagName == "side" && entry.SameOwner(owner);
        }
        if (_wccInlineDrafts.Any(draft => Inside(draft.Route.Owner))
            || _sideFlagDrafts.Any(draft => Inside(draft.Recipe.SideOwner)))
            return "Reset the pending field, difficulty or flag changes for this side before removing it.";
        var ranges = new[] { entry.TargetRange }.OfType<WmlStaticSourceRange>().Concat(entry.AmendmentRanges);
        if (_eventDrafts.Any(draft => draft.Recipe.Target is { } target && ranges.Any(range =>
            SameAuthoredFlagSource(range.Source, target.Source)
            && target.StartOffset < range.StartOffset + range.Length && range.StartOffset < target.StartOffset + target.Length)))
            return "Revert the overlapping pending event change before removing this side amendment.";
        return null;
    }

    internal IReadOnlyList<WccSideManagementRowVm> SideTopologyManagementRows(WccSideTopologyManagementToken token)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var rows = new List<WccSideManagementRowVm>();
            var retainedCount = token.Snapshot.Entries.Count - _sideTopologyRemovals.Count;
            foreach (var entry in token.Snapshot.Entries)
            {
                var removed = _sideTopologyRemovals.Contains(entry);
                var conflict = SideRemovalDraftProblem(entry);
                var canRemove = !removed && _sideTopologyAddedCount == 0 && entry.Number == retainedCount
                    && entry.CanRemove && conflict is null;
                var canRestore = removed && _sideTopologyAddedCount == 0 && entry.Number == retainedCount + 1;
                var reason = conflict ?? (!entry.CanRemove ? entry.Diagnostic
                    : removed ? canRestore ? "Restore this pending removal." : "Restore the earlier removed side first."
                    : !canRemove ? "Remove the final side first. Middle removal needs reference and map-start remapping."
                    : entry.Diagnostic);
                var name = entry.SideOwner.GetKnownValue("side_name") ?? entry.SideOwner.GetKnownValue("user_team_name")
                    ?? entry.SideOwner.GetKnownValue("name") ?? entry.SideOwner.GetKnownValue("id") ?? "Side " + entry.Number;
                rows.Add(new("remove:" + entry.Number, entry.Number, WccScenarioFieldValueCodec.DecodeLiteral(name),
                    removed ? "Pending removal" : "Saved", entry.TargetRange is null, canRemove, canRestore, reason, entry));
            }
            for (var index = 1; index <= _sideTopologyAddedCount; index++)
            {
                var number = retainedCount + index;
                var preset = _sideTopologyAdditions[index - 1].Recipe.Preset;
                rows.Add(new("add:" + number, number, preset?.Name ?? "New side", "Pending addition", preset is not null,
                    index == _sideTopologyAddedCount, false,
                    "Save this " + (preset is null ? "basic side" : "preset invocation") + " before editing its fields. Remove cancels the pending addition.", null));
            }
            return rows.AsReadOnly();
        }
    }

    internal bool StageSideTopologyManagementChange(WccSideTopologyManagementToken token, string operation,
        WccSideManagementRowVm? row)
        => StageSideTopologyIntent(token, operation, row, null);

    private bool StageSideTopologyIntent(WccSideTopologyManagementToken token, string operation,
        WccSideManagementRowVm? row, WccCampaignPresetSelection? preset)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (SideTopologyTokenProblem(token) is { } refusal) { Status = refusal; return false; }
            var removals = _sideTopologyRemovals.ToList();
            var additions = _sideTopologyAdditions.ToList();
            var currentRow = row is null ? null : SideTopologyManagementRows(token).SingleOrDefault(item => item.Identity == row.Identity);
            if (operation == "add") additions.Add(preset is null ? new(new WccScenarioSideAddition())
                : new(new WccScenarioSideAddition(preset.Entry, preset.IdentityOverride), preset.Binding));
            else if (operation == "remove" && currentRow is { CanRemove: true })
            {
                if (currentRow.Entry is { } entry) removals.Add(entry); else additions.RemoveAt(additions.Count - 1);
            }
            else if (operation == "restore" && currentRow is { CanRestore: true, Entry: { } restored }) removals.Remove(restored);
            else { Status = currentRow?.Reason ?? "Select the final side to remove or the first pending removal to restore."; return false; }
            try { _ = WccScenarioSideTopologyPlanner.CreateEdit(token.Snapshot, removals, additions.Select(addition => addition.Recipe)); }
            catch (Exception problem) when (problem is InvalidOperationException or ArgumentException) { Status = problem.Message; return false; }
            if (preset is not null && (!preset.Binding.IsCurrent || PresetIdentityClash(preset.Entry.Payload!, preset.IdentityOverride, WccCampaignLocalPresetRole.Side) is { }))
            { Status = PresetIdentityClash(preset.Entry.Payload!, preset.IdentityOverride, WccCampaignLocalPresetRole.Side) ?? "The preset catalog changed. Reopen the picker."; return false; }
            foreach (var entry in removals)
                if (SideRemovalDraftProblem(entry) is { } conflict) { Status = conflict; return false; }
            if (SideTopologyTokenProblem(token) is { } scalarRefusal) { Status = scalarRefusal; return false; }
            WccFieldEditSession.Invalidate();
            if (SideTopologyTokenProblem(token) is { } fieldRefusal) { Status = fieldRefusal; return false; }
            foreach (var entry in removals)
                if (SideRemovalDraftProblem(entry) is { } lateConflict) { Status = lateConflict; return false; }
            if (preset?.Binding.IsCurrent == false) { Status = "The preset catalog changed. Reopen the picker."; return false; }
            var hasChanges = removals.Count != 0 || additions.Count != 0;
            _sideTopologySourceBinding = hasChanges ? (WccMusicScheduleSourceBinding)token.Binding : null;
            _sideTopologySnapshot = hasChanges ? token.Snapshot : null;
            _sideTopologyProofProjection = hasChanges ? token.Projection : null;
            _sideTopologyRemovals = Array.AsReadOnly(removals.OrderBy(entry => entry.Number).ToArray());
            _sideTopologyAdditions = Array.AsReadOnly(additions.ToArray());
            _sideTopologyDraftVersion++;
            RebuildSideTopologyDraftRows();
            var version = _sideTopologyDraftVersion;
            Status = hasChanges ? "Side changes staged. Save checks all pending changes together." : "Sides match the saved source.";
            RaiseWccSideTopologyDraftsChanged();
            return version == _sideTopologyDraftVersion && SideTopologyBindingProblem((WccMusicScheduleSourceBinding)token.Binding) is null;
        }
    }

    internal string? WccSideTopologyOwnerProblem(WmlStaticScalarOwner owner)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!HasWccSideTopologyDrafts) return null;
            if (_sideTopologySourceBinding is not { } binding) return "The pending side changes have no current source binding.";
            if (SideTopologyBindingProblem(binding) is { } problem) return problem;
            while (owner.Parent is not null && owner.TagName != "side") owner = owner.Parent;
            return owner.TagName == "side" && _sideTopologyRemovals.Any(entry => entry.SameOwner(owner))
                ? "This side is pending removal. Restore it in Edit sides before changing its fields." : null;
        }
    }

    internal bool TryCaptureWccSideTopologyContribution(WmlStaticSource source,
        out WccSideTopologyDraftContribution? contribution, out string? problem)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            contribution = null;
            if (!HasWccSideTopologyDrafts || _sideTopologySourceBinding is not { } binding
                || _sideTopologySnapshot is not { } snapshot)
            { problem = "There are no pending side changes."; return false; }
            problem = SideTopologyBindingProblem(binding);
            if (problem is not null) return false;
            if (!SameAuthoredFlagSource(source, binding.Range.Source))
            { problem = "The collected source differs from the pending side changes."; return false; }
            foreach (var entry in _sideTopologyRemovals)
                if ((problem = SideRemovalDraftProblem(entry)) is not null) return false;
            try
            {
                var fresh = WccScenarioSideTopologyPlanner.Read(source, snapshot.Owner, binding.Plan!, _sideTopologyProofProjection);
                var removals = _sideTopologyRemovals.Select(entry => fresh.Entries.Single(current => current.SameOwner(entry.SideOwner))).ToArray();
                var edit = WccScenarioSideTopologyPlanner.CreateEdit(fresh, removals, _sideTopologyAdditions.Select(addition => addition.Recipe));
                contribution = new(this, binding, _sideTopologyDraftVersion, edit,
                    _sideTopologyDrafts.Select(draft => draft.Label + ": " + draft.Summary));
                problem = null; return true;
            }
            catch (Exception refusal) when (refusal is InvalidOperationException or ArgumentException) { problem = refusal.Message; return false; }
        }
    }

    internal bool IsCurrentWccSideTopologyContribution(WccSideTopologyDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
            return HasWccSideTopologyDrafts && ReferenceEquals(contribution.Editor, this)
                && ReferenceEquals(contribution.Binding, _sideTopologySourceBinding) && contribution.Version == _sideTopologyDraftVersion
                && _sideTopologySourceBinding is { } binding && SideTopologyBindingProblem(binding) is null
                && _sideTopologyRemovals.All(entry => SideRemovalDraftProblem(entry) is null);
    }

    internal bool CompleteWccSideTopologyContribution(WccSideTopologyDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!IsCurrentWccSideTopologyContribution(contribution)) return false;
            ClearWccSideTopologyDrafts(); RaiseWccSideTopologyDraftsChanged(); return true;
        }
    }

    public void RevertWccSideTopologyDraft(string identity)
    {
        using var management = BeginWccSideTopologyManagement();
        if (management is null) return;
        management.SelectedRow = management.Rows.SingleOrDefault(row => row.Identity == identity);
        if (identity.StartsWith("add:", StringComparison.Ordinal)) management.RemoveSelected(); else management.RestoreSelected();
    }

    public void DiscardWccSideTopologyDrafts()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy || _changingWccDifficultyDrafts) return;
            ClearWccSideTopologyDrafts(); RaiseWccSideTopologyDraftsChanged();
        }
    }

    private void ClearWccSideTopologyDrafts()
    {
        _sideTopologySourceBinding = null; _sideTopologySnapshot = null; _sideTopologyProofProjection = null;
        _sideTopologyRemovals = []; _sideTopologyAdditions = []; _sideTopologyDrafts = []; _sideTopologyDraftVersion++;
    }

    private void RebuildSideTopologyDraftRows()
    {
        var rows = _sideTopologyRemovals.Select(entry => new WccScenarioSideTopologyDraft("remove:" + entry.Number,
            "Side " + entry.Number, "Remove side" + (entry.AmendmentRanges.Count == 0 ? "" : " and " + entry.AmendmentRanges.Count + " initial amendment(s)"))).ToList();
        var retainedCount = (_sideTopologySnapshot?.Entries.Count ?? 0) - _sideTopologyRemovals.Count;
        for (var index = 1; index <= _sideTopologyAddedCount; index++)
            rows.Add(new("add:" + (retainedCount + index), "Side " + (retainedCount + index),
                _sideTopologyAdditions[index - 1].Recipe.Preset is { } preset ? "Add side with {" + preset.Name + "}" : "Add basic literal side"));
        _sideTopologyDrafts = rows.AsReadOnly();
    }

    internal string GetPendingWccSideTopologyChangeStamp() => _sideTopologyDraftVersion + "\u001f"
        + string.Join("\u001f", _sideTopologyDrafts.Select(draft => draft.Identity + "=" + draft.Summary));

    internal void RaiseWccSideTopologyCapabilitiesChanged()
    { OnPropertyChanged(nameof(CanOpenWccSideManagement)); RaiseWccPresetApplicationCapabilities(); }

    private void RaiseWccSideTopologyDraftsChanged()
    {
        CollectedSaveAuthorityChanging?.Invoke();
        OnPropertyChanged(nameof(HasWccSideTopologyDrafts)); OnPropertyChanged(nameof(WccSideTopologyDrafts));
        OnPropertyChanged(nameof(CanEditDocument)); RaiseWccSideTopologyCapabilitiesChanged();
        RaiseInitialScalarFieldsChanged(); WccSideTopologyDraftsChanged?.Invoke(this, EventArgs.Empty);
    }
}
