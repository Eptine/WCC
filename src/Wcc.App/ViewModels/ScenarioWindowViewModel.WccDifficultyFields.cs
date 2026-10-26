using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private WmlStaticSource? _wccDifficultyMainSource;
    private WccCampaignAuthoringMetadata? _wccDifficultyAuthoring;
    private long _wccDifficultyBindingVersion;
    private bool _changingWccDifficultyDrafts;
    private readonly Dictionary<(WmlStaticScalarOwner Owner, string Key, WmlStaticSource Source,
        WmlStaticSource Main, WmlCampaignEvaluationPlan Plan, long Version), WccScenarioDifficultyFieldSnapshot>
        _wccDifficultySnapshots = new();
    private readonly Dictionary<(WmlStaticScalarOwner Owner, string Key, WmlStaticSource Source,
        WmlStaticSource Main, WmlCampaignEvaluationPlan Plan, long Version), WccScenarioDifficultyFieldSnapshot>
        _wccDifficultyInsertionSnapshots = new();

    /// <summary>Captured display inputs only. Metadata never authorizes a source write.</summary>
    public void ConfigureWccDifficultyFields(WmlStaticSource? capturedCampaignMain,
        WccCampaignAuthoringMetadata? authoring)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var copy = authoring is null ? null : new WccCampaignAuthoringMetadata
            {
                LayoutVersion = authoring.LayoutVersion, AddonId = authoring.AddonId,
                CampaignDefine = authoring.CampaignDefine, MainRelativePath = authoring.MainRelativePath,
                UsesExplicitDifficultyValues = authoring.UsesExplicitDifficultyValues,
                FixedDifficultyDefines = (authoring.FixedDifficultyDefines ?? []).ToList()
            };
            if (SameDifficultySource(_wccDifficultyMainSource, capturedCampaignMain) &&
                SameDifficultyAuthoring(_wccDifficultyAuthoring, copy)) return;
            using var display = DeferFieldDisplayRefresh();
            _wccDifficultyMainSource = capturedCampaignMain;
            _wccDifficultyAuthoring = copy;
            _wccDifficultyBindingVersion++;
            _wccDifficultySnapshots.Clear();
            _wccDifficultyInsertionSnapshots.Clear();
            InvalidateSourceEditingSession();
            RaiseWccInlineDraftsChanged();
            RefreshWccInitialFieldEditors();
        }
    }

    private static bool SameDifficultySource(WmlStaticSource? left, WmlStaticSource? right) =>
        left is null || right is null ? left is null && right is null :
        PathsEqual(left.Path, right.Path) && left.ContentHash == right.ContentHash &&
        left.IsAuthored == right.IsAuthored && left.Text == right.Text;

    private static bool SameDifficultyAuthoring(WccCampaignAuthoringMetadata? left, WccCampaignAuthoringMetadata? right) =>
        left is null || right is null ? left is null && right is null :
        left.LayoutVersion == right.LayoutVersion && left.AddonId == right.AddonId &&
        left.CampaignDefine == right.CampaignDefine && left.MainRelativePath == right.MainRelativePath &&
        left.UsesExplicitDifficultyValues == right.UsesExplicitDifficultyValues &&
        left.FixedDifficultyDefines.SequenceEqual(right.FixedDifficultyDefines, StringComparer.Ordinal);

    private WccScenarioDifficultyFieldSnapshot? GetWccDifficultySnapshot(WmlStaticScalarOwner owner, string key)
    {
        var route = GetWccInitialFieldRoute(owner, key);
        if (route is null || _sourceEvaluationPlan is not { } plan || _wccDifficultyMainSource is not { } main ||
            !PathsEqual(main.Path, plan.CampaignMainSourcePath) || main.ContentHash != plan.CampaignMainTextHash)
            return null;
        var cacheKey = (owner, key, route.Source, main, plan, _wccDifficultyBindingVersion);
        if (!_wccDifficultySnapshots.TryGetValue(cacheKey, out var result))
        {
            if (_wccDifficultySnapshots.Count >= 512) _wccDifficultySnapshots.Clear();
            result = WccScenarioDifficultyFieldPlanner.Read(main, route.Source, owner, key, plan, _wccDifficultyAuthoring);
            _wccDifficultySnapshots[cacheKey] = result;
        }
        if (result.CanEdit || _wccDifficultyAuthoring is not null) return result;
        // The marker keeps the original explicit insertion intent after it absorbs
        // the ordinary draft. No displayed/inherited default grants this permission.
        if (FindWccDifficultyMarker(route)?.Difficulty is { Snapshot.InsertionIntent: not null } difficulty)
            return difficulty.Snapshot;
        var insertion = _wccInlineDrafts.FirstOrDefault(draft => draft.Difficulty is null
            && draft.Operation == WmlFieldOperation.Insert && draft.ValidationError is null
            && draft.ValueInputKind == WccScenarioFieldValueInputKind.Literal && draft.Text is not null
            && draft.Route.Key == key && SameDifficultySource(draft.Route.Source, route.Source)
            && result.SameOwner(draft.Route.Owner));
        if (insertion is null || WccInitialFieldDefinition(route) is not { } field
            || WccScenarioFieldValueCodec.InferInputKind(insertion.Text!) != WccScenarioFieldValueInputKind.Literal
            || WccScenarioFieldValueCodec.ValidateInput(field, insertion.Text) is not null) return result;
        // Cached source topology is reusable while a valid ordinary Insert draft
        // exists. Changing its literal does not need another scan; Save re-proves
        // the entire recipe and every context using fresh captured sources.
        if (_wccDifficultyInsertionSnapshots.TryGetValue(cacheKey, out var promoted)
            && promoted.InsertionIntent?.Route == insertion.Route) return promoted;
        try
        {
            var intent = WccScenarioDifficultyFieldInsertionIntent.Create(new(insertion.Route,
                insertion.Operation, insertion.Text) { ValueInputKind = insertion.ValueInputKind });
            promoted = WccScenarioDifficultyFieldPlanner.Read(main, route.Source, owner, key, plan, null, intent);
            if (_wccDifficultyInsertionSnapshots.Count >= 512) _wccDifficultyInsertionSnapshots.Clear();
            _wccDifficultyInsertionSnapshots[cacheKey] = promoted;
            return promoted;
        }
        catch (InvalidOperationException) { return result; }
    }

    private WccScenarioInlineDraft? FindWccDifficultyMarker(WccScenarioFieldRoute? route) => route is null ? null :
        _wccInlineDrafts.FirstOrDefault(draft => draft.Difficulty is { } difficulty &&
            difficulty.Snapshot.Key == route.Key && SameDifficultySource(difficulty.Snapshot.Source, route.Source) &&
            difficulty.Snapshot.SameOwner(route.Owner));

    private WccScenarioInlineDraft? FindWccDifficultyValueDraft(WccScenarioFieldRoute? route) =>
        route is not null && FindWccDifficultyMarker(route)?.Difficulty is { } difficulty &&
        difficulty.Slots.TryGetValue(route.ContextSemanticKey, out var slot) ? slot : null;

    internal bool HasWccDifficultyDraft(WmlStaticScalarOwner owner, string key)
    {
        lock (_wccInitialFieldRefreshGate) return IsWccPendingLeaderOwner(owner)
            ? HasWccPendingLeaderDifficultyDraft(owner, key)
            : FindWccDifficultyMarker(GetWccInitialFieldRoute(owner, key)) is not null;
    }

    internal bool IsWccDifficultyIndependent(WmlStaticScalarOwner owner, string key)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccPendingLeaderOwner(owner)) return IsWccPendingLeaderDifficultyIndependent(owner, key);
            if (FindWccDifficultyMarker(GetWccInitialFieldRoute(owner, key))?.Difficulty is { } difficulty)
                return difficulty.Mode == WccScenarioDifficultyValueMode.Independent;
            var snapshot = GetWccDifficultySnapshot(owner, key);
            return snapshot?.CanEdit == true ? snapshot.Mode == WccScenarioDifficultyValueMode.Independent
                : HasExistingWccDifficultyFieldSource(owner, key);
        }
    }

    internal string? WccDifficultyFieldLockReason(WmlStaticScalarOwner owner, string key, string? displayedValue)
    {
        lock (_wccInitialFieldRefreshGate)
        {

            if (key is "id" or "side" or "save_id")
                return "Identity-field difficulty conversion is locked because it can affect scenario links, side references or carryover.";
            if (IsWccPendingLeaderOwner(owner)) return WccPendingLeaderDifficultyLockReason(owner, key, displayedValue);
            if (!IsCurrentWccInitialFieldOwner(owner)) return "This field no longer belongs to the selected captured scenario.";
            if (WccSideTopologyOwnerProblem(owner) is { } topologyProblem) return topologyProblem;
            if (GetWccDifficultyDraftBindingProblem() is { } bindingProblem) return bindingProblem;
            var snapshot = GetWccDifficultySnapshot(owner, key);
            if (snapshot is null)
                return "Difficulty conversion needs the exact campaign main source and declared campaign contexts.";
            if (!snapshot.CanEdit) return snapshot.Diagnostic;
            if (displayedValue is null) return "Set a literal value before creating separate difficulty values.";
            if (WccScenarioFieldValueCodec.InferInputKind(displayedValue) != WccScenarioFieldValueInputKind.Literal)
                return "This conversion supports literal values. Revert the conversion to edit a macro or WML expression.";
            if (GetWccInitialFieldRoute(owner, key) is { } route && WccInitialFieldDefinition(route) is { } definition &&
                WccScenarioFieldValueCodec.ValidateInput(definition, displayedValue) is { } invalid)
                return invalid;
            return null;
        }
    }

    internal bool CanToggleWccDifficultyField(WmlStaticScalarOwner owner, string key, string? value)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccPendingLeaderOwner(owner)) return key is not ("id" or "side" or "save_id")
                && !_changingWccDifficultyDrafts && !IsWccInlineSaveBusy
                && CanToggleWccPendingLeaderDifficultyField(owner, key, value);
            return !_changingWccDifficultyDrafts && !IsWccInlineSaveBusy &&
                GetWccInitialFieldRoute(owner, key) is { } route && WccInitialFieldDefinition(route) is { IsEditable: true } definition &&
                IsSelectedWccInitialFieldOwner(owner, definition.OwnerScope) &&
                WccDifficultyFieldLockReason(owner, key, value) is null && CanReviewWccInitialField(owner, key);
        }
    }

    internal bool ToggleWccDifficultyField(WmlStaticScalarOwner owner, string key,
        string? selectedValue, string? baselineDisplayValue)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_fieldDisplayDeferralDepth > 0 || _flushingFieldDisplay || _changingWccDifficultyDrafts) return false;
            if (IsWccPendingLeaderOwner(owner)) return key is not ("id" or "side" or "save_id")
                && ToggleWccPendingLeaderDifficultyField(owner, key, selectedValue);
            if (!CanToggleWccDifficultyField(owner, key, selectedValue))
            {
                Status = WccDifficultyFieldLockReason(owner, key, selectedValue)
                    ?? "Finish the current scenario operation before changing difficulty dependence.";
                return false;
            }
            var route = GetWccInitialFieldRoute(owner, key)!;
            var existing = FindWccDifficultyMarker(route);
            var snapshot = existing?.Difficulty?.Snapshot ?? GetWccDifficultySnapshot(owner, key)!;
            var mode = (existing?.Difficulty?.Mode ?? snapshot.Mode) == WccScenarioDifficultyValueMode.Shared
                ? WccScenarioDifficultyValueMode.Independent : WccScenarioDifficultyValueMode.Shared;
            var originalValues = existing?.Difficulty?.OriginalDisplayValues ?? snapshot.Values
                .Select(value => value with { ValueText = value.ValueText ?? baselineDisplayValue }).ToArray();
            var identity = "difficulty:" + snapshot.FieldIdentity;
            var label = (WccInitialFieldDefinition(route)?.OwnerScope == ScenarioInitialFieldOwnerScope.Scenario
                ? SelectedScenario?.DisplayName : SelectedSide?.DisplayName) + " / " + key;
            var slots = snapshot.Values.Select(value => new KeyValuePair<string, WccScenarioInlineDraft>(
                value.ContextSemanticKey, new(identity, route with { ContextSemanticKey = value.ContextSemanticKey },
                    route.CanSet ? WmlFieldOperation.Set : WmlFieldOperation.Insert, selectedValue,
                    label + " (" + value.Define + ")", null))).ToArray();
            var difficulty = new WccScenarioDifficultyDraft(snapshot, mode, _wccDifficultyBindingVersion, slots, originalValues);
            if (_wccInlineDraftState is null)
            {
                _wccInlineDraftState = CaptureWccFieldSessionState(route);
                _wccInlineDraftAsset = _currentScenarioAsset;
                _wccInlineInstallPath = _wesnothSourceInstallPath;
            }
            using var display = DeferFieldDisplayRefresh();
            _changingWccDifficultyDrafts = true;
            try
            {
                // Register one marker before notifying removals. Reentrant Save,
                // input and Discard refuse throughout this atomic list mutation.
                var marker = DifficultyMarker(existing?.Route ?? route, identity, label, difficulty);
                if (existing is null) _wccInlineDrafts.Add(marker);
                else _wccInlineDrafts[_wccInlineDrafts.IndexOf(existing)] = marker;
                foreach (var ordinary in _wccInlineDrafts.Where(draft => draft.Difficulty is null &&
                    draft.Route.Key == key && SameDifficultySource(draft.Route.Source, snapshot.Source) &&
                    snapshot.SameOwner(draft.Route.Owner)).ToArray()) _wccInlineDrafts.Remove(ordinary);
                NormalizeWccDifficultyMarker(marker);
                FinishWccInlineDraftMutation();
            }
            finally { _changingWccDifficultyDrafts = false; }
            return true;
        }
    }

    private bool TrySetWccDifficultyValue(WccScenarioFieldRoute route, WmlFieldOperation operation,
        string? value, WccScenarioFieldValueInputKind inputKind, bool publish = true)
    {
        if (FindWccDifficultyMarker(route) is not { Difficulty: { } difficulty } marker) return false;
        if (_changingWccDifficultyDrafts || IsWccInlineSaveBusy) return true;
        if (operation == WmlFieldOperation.Remove)
        {
            Status = "Revert the pending difficulty conversion before removing the source assignment.";
            return true;
        }
        var hadDrafts = HasWccInlineDrafts;
        var definition = WccInitialFieldDefinition(route)!;
        var error = inputKind == WccScenarioFieldValueInputKind.Literal
            ? WccScenarioFieldValueCodec.ValidateInput(definition, value)
            : "Difficulty conversions need literal values. Revert this conversion to enter a WML expression.";
        var slots = difficulty.Slots.Select(pair => new KeyValuePair<string, WccScenarioInlineDraft>(pair.Key,
            difficulty.Mode == WccScenarioDifficultyValueMode.Shared || pair.Key == route.ContextSemanticKey
                ? pair.Value with { Text = value, ValidationError = error, ValueInputKind = inputKind }
                : pair.Value)).ToArray();
        var updated = new WccScenarioDifficultyDraft(difficulty.Snapshot, difficulty.Mode,
            difficulty.BindingVersion, slots, difficulty.OriginalDisplayValues);
        using var display = DeferFieldDisplayRefresh();
        _changingWccDifficultyDrafts = true;
        try
        {
            var replacement = DifficultyMarker(marker.Route, marker.GroupIdentity, marker.Label, updated);
            _wccInlineDrafts[_wccInlineDrafts.IndexOf(marker)] = replacement;
            NormalizeWccDifficultyMarker(replacement);
            if (publish) FinishWccInlineDraftMutation(route, hadDrafts);
        }
        finally { _changingWccDifficultyDrafts = false; }
        return true;
    }

    private static WccScenarioInlineDraft DifficultyMarker(WccScenarioFieldRoute route, string identity,
        string label, WccScenarioDifficultyDraft difficulty) => new(identity, route, WmlFieldOperation.Set,
            null, label, difficulty.Slots.Values.FirstOrDefault(slot => slot.ValidationError is not null)?.ValidationError)
        { Difficulty = difficulty };

    private void NormalizeWccDifficultyMarker(WccScenarioInlineDraft marker)
    {
        var difficulty = marker.Difficulty!;
        // Absence can inherit a different value from each context's unit type.
        // Returning to Shared retains an explicit requested value until Reset.
        if (difficulty.Mode != difficulty.Snapshot.Mode || marker.ValidationError is not null ||
            difficulty.Snapshot.Values.Any(value => value.ValueText is null)) return;
        var definition = WccInitialFieldDefinition(marker.Route)!;
        if (difficulty.OriginalDisplayValues.All(original => original.ValueText is { } text &&
            difficulty.Slots.TryGetValue(original.ContextSemanticKey, out var slot) &&
            slot.ValueInputKind == WccScenarioFieldValueInputKind.Literal &&
            WccScenarioFieldValueCodec.Equivalent(definition,
                WccScenarioFieldValueCodec.EncodeLiteral(definition, text), slot.Text))) _wccInlineDrafts.Remove(marker);
    }

    private bool TryCaptureWccDifficultyFields(WmlStaticSource source,
        out IReadOnlyList<WccScenarioDifficultyFieldEdit> edits, out string? problem)
    {
        edits = []; problem = GetWccDifficultyDraftBindingProblem();
        if (_changingWccDifficultyDrafts)
        { problem = "The pending difficulty fields are changing; finish the current input before saving."; return false; }
        if (problem is not null) return false;
        var captured = new List<WccScenarioDifficultyFieldEdit>();
        try
        {
            foreach (var marker in _wccInlineDrafts.Where(draft => draft.Difficulty is not null))
            {
                var difficulty = marker.Difficulty!;
                // All recipes use the exact immutable request Source object.
                var fresh = WccScenarioDifficultyFieldPlanner.Read(_wccDifficultyMainSource!, source,
                    difficulty.Snapshot.Owner, difficulty.Snapshot.Key, _sourceEvaluationPlan, _wccDifficultyAuthoring,
                    difficulty.Snapshot.InsertionIntent);
                if (!fresh.CanEdit || fresh.FieldIdentity != difficulty.Snapshot.FieldIdentity ||
                    fresh.Mode != difficulty.Snapshot.Mode || !fresh.Values.SequenceEqual(difficulty.Snapshot.Values))
                    throw new InvalidOperationException(fresh.CanEdit ? "The captured difficulty field structure changed." : fresh.Diagnostic);
                var values = fresh.Values.Select(original => original with
                {
                    ValueText = difficulty.Slots.TryGetValue(original.ContextSemanticKey, out var slot) &&
                        slot.ValidationError is null && slot.ValueInputKind == WccScenarioFieldValueInputKind.Literal
                        ? fresh.Key == "turns" ? NormalizeWccInlineRequestedValue(slot.Text) : slot.Text : null
                }).ToArray();
                var selected = values.Single(value => value.ContextSemanticKey == _evaluationContext.SemanticKey).ValueText;
                if (selected is null) throw new InvalidOperationException("Every pending difficulty needs a supported literal value.");
                captured.Add(WccScenarioDifficultyFieldPlanner.BuildEdit(fresh, difficulty.Mode,
                    _evaluationContext.SemanticKey, selected, values));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        { problem = exception.Message; return false; }
        edits = Array.AsReadOnly(captured.ToArray());
        return true;
    }

    private string? GetWccDifficultyDraftBindingProblem()
    {
        foreach (var marker in _wccInlineDrafts)
            if (marker.Difficulty is { } difficulty &&
                (difficulty.BindingVersion != _wccDifficultyBindingVersion || _sourceEvaluationPlan is not { } plan ||
                 !SameDifficultySource(difficulty.Snapshot.MainSource, _wccDifficultyMainSource) ||
                 !PathsEqual(_wccDifficultyMainSource?.Path, plan.CampaignMainSourcePath) ||
                 _wccDifficultyMainSource?.ContentHash != plan.CampaignMainTextHash))
                return "The campaign difficulty authoring or main-source binding changed. Difficulty drafts are retained; discard them before rebinding.";
        return null;
    }
}
