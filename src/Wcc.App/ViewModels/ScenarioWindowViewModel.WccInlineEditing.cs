using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private WccScenarioFieldEditSessionState? _wccInlineDraftState;
    private ScenarioFileAsset? _wccInlineDraftAsset;
    private string? _wccInlineInstallPath;
    private bool _isWccInlineSaveBusy;
    private string? _wccInlineDraftStatus;
    private string? _wccInlineReviewSummary;
    private readonly ObservableCollection<WccScenarioInlineDraft> _wccInlineDrafts = new();
    private ReadOnlyObservableCollection<WccScenarioInlineDraft>? _wccInlineDraftView;
    private Task<WccScenarioFieldBatchEditResult>? _wccInlineSaveTask;
    private long _wccInlineSaveProgressVersion;
    private string? _wccInlineSaveProgressStatus;
    private WccInlinePreviewProgress? _wccInlinePreviewProgress;
    public ReadOnlyObservableCollection<WccScenarioInlineDraft> WccInlineDrafts =>
        _wccInlineDraftView ??= new(_wccInlineDrafts);
    public bool HasWccInlineDrafts => WccInlineDrafts.Count != 0;
    public bool IsWccInlineSaveBusy => _isWccInlineSaveBusy;
    public bool IsWccInlineSaveProgressVisible => IsWccInlineSaveBusy ||
        _wccInlinePreviewProgress is { } preview && preview.Version == _wccInlineSaveProgressVersion &&
        preview.Generation == _staticInitialValuesGeneration && ReferenceEquals(preview.Task, _staticInitialValuesTask) &&
        ReferenceEquals(preview.Asset, _currentScenarioAsset) && PathsEqual(preview.SourcePath, _currentSourcePath ?? LoadedPath);
    public string? WccInlineSaveProgressStatus => IsWccInlineSaveProgressVisible ? _wccInlineSaveProgressStatus : null;
    public bool CanDiscardWccInlineDrafts => HasWccInlineDrafts && !IsWccInlineSaveBusy && !_changingWccDifficultyDrafts;
    public bool CanSaveWccInlineDrafts => !HasSelectedCampaignFieldDrafts && HasWccInlineDrafts && !IsWccInlineSaveBusy && !_changingWccDifficultyDrafts &&
        WccInlineDrafts.All(draft => draft.ValidationError is null) && GetWccInlineBindingProblem() is null;
    public string? WccInlineDraftStatus => !IsWccInlineSaveBusy
        ? GetWccInlineBindingProblem() ?? _wccInlineDraftStatus : _wccInlineDraftStatus;
    public string? WccInlineReviewSummary => _wccInlineReviewSummary;
    public bool HasWccInlineReview => !string.IsNullOrWhiteSpace(_wccInlineReviewSummary);
    public string WccInlineDraftSummary => WccInlineDrafts.Count + " pending source changes. Save checks them together in every campaign difficulty.";
    public Func<Action, bool>? BeforeWccInlineSourceNavigation { get; set; }
    public bool ScenarioTurnsHasWccInlineDraft => FindWccInlineDraft("turns") is not null;
    public bool SideGoldHasWccInlineDraft => FindWccInlineDraft("gold") is not null;
    public bool SideIncomeHasWccInlineDraft => FindWccInlineDraft("income") is not null;
    public bool ScenarioTurnsCanRemoveWccOverride => CanRemoveWccInlineOverride("turns");
    public bool SideGoldCanRemoveWccOverride => CanRemoveWccInlineOverride("gold");
    public bool SideIncomeCanRemoveWccOverride => CanRemoveWccInlineOverride("income");

    private bool CanRemoveWccInlineOverride(string key) => !IsWccInlineSaveBusy &&
        CanReviewWccInitialField(key) && GetWccInitialFieldRoute(key)?.CanRemove == true;

    private WccScenarioInlineDraft? FindWccInlineDraft(string key)
    {
        return FindWccInlineDraft(GetWccInitialFieldRoute(key));
    }

    internal WccScenarioInlineDraft? FindWccInlineDraft(WccScenarioFieldRoute? route)
    {
        if (FindWccPendingLeaderField(route) is { } pendingLeader) return pendingLeader;
        if (!HasWccInlineDrafts) return null;
        if (route is null || route.Kind == WccScenarioFieldRouteKind.RawOnly || !(route.CanSet || route.CanInsert)) return null;
        if (FindWccDifficultyValueDraft(route) is { } difficultySlot) return difficultySlot;
        var identity = WccInlineGroupIdentity(route);
        return WccInlineDrafts.FirstOrDefault(draft => draft.GroupIdentity == identity);
    }

    private string? WccInlineDisplay(string key, string? resolved)
    {
        var draft = FindWccInlineDraft(key);
        return draft is { Operation: not WmlFieldOperation.Remove } ? draft.Text : resolved;
    }

    private string WccInlineFieldStatus(string key, string resolvedStatus)
    {
        var draft = FindWccInlineDraft(key);
        if (draft is null) return resolvedStatus;
        return draft.ValidationError ?? (draft.Operation == WmlFieldOperation.Remove
            ? "The local macro override will be removed on Save. The resolved value shown here is unchanged until saving."
            : "Pending value. Save checks all drafts together; difficulties using the same source value change together.");
    }

    private bool TrySetWccInlineValue(string key, string? value)
    {
        var route = GetWccInitialFieldRoute(key);
        if (route is null || !CanReviewWccInitialField(key)) return false;
        SetWccInlineDraft(route, route.CanSet ? WmlFieldOperation.Set : WmlFieldOperation.Insert, value);
        return true;
    }

    public bool RemoveWccInlineOverride(string key)
    {
        var route = GetWccInitialFieldRoute(key);
        if (!CanRemoveWccInlineOverride(key) || route is null) return false;
        SetWccInlineDraft(route, WmlFieldOperation.Remove, null);
        return true;
    }

    public void RevertWccInlineField(string key)
    {
        if (IsWccInlineSaveBusy) return;
        if (FindWccInlineDraft(key) is { } draft) RevertWccInlineDraft(draft);
    }

    public void RevertWccInlineDraft(WccScenarioInlineDraft draft)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_fieldDisplayDeferralDepth > 0 || _flushingFieldDisplay || _changingWccDifficultyDrafts ||
                IsWccInlineSaveBusy) return;
            var actual = _wccInlineDrafts.Contains(draft) ? draft : _wccInlineDrafts.FirstOrDefault(marker =>
                marker.Difficulty is { } difficulty && difficulty.Slots.Values.Any(slot => ReferenceEquals(slot, draft)));
            if (actual is null || !_wccInlineDrafts.Remove(actual)) return;
            FinishWccInlineDraftMutation(actual.Route, hadDrafts: true);
        }
    }

    public void DiscardWccInlineDrafts()
    {
        if (IsWccInlineSaveBusy) return;
        lock (_wccInitialFieldRefreshGate)
        {
            if (_changingWccDifficultyDrafts) return;
            foreach (var row in WccScenarioFieldEditors) row.ResetInputState();
            foreach (var row in WccSideFieldEditors) row.ResetInputState();
            foreach (var row in WccLeaderFieldEditors) row.ResetInputState();
            _wccInlineDrafts.Clear();
            FinishWccInlineDraftMutation();
        }
    }

    private void SetWccInlineDraft(WccScenarioFieldRoute route, WmlFieldOperation operation, string? text,
        WccScenarioFieldValueInputKind inputKind = WccScenarioFieldValueInputKind.Literal)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_fieldDisplayDeferralDepth > 0 || _flushingFieldDisplay) return;
            SetWccInlineDraftCore(route, operation, text, inputKind);
        }
    }

    private void SetWccInlineDraftCore(WccScenarioFieldRoute route, WmlFieldOperation operation, string? text,
        WccScenarioFieldValueInputKind inputKind, bool publish = true)
    {
        if (IsWccInlineSaveBusy) return;
        if (TrySetWccDifficultyValue(route, operation, text, inputKind, publish)) return;
        var hadDrafts = HasWccInlineDrafts;
        if (_wccInlineDraftState is null)
        {
            _wccInlineDraftState = CaptureWccFieldSessionState(route);
            _wccInlineDraftAsset = _currentScenarioAsset;
            _wccInlineInstallPath = _wesnothSourceInstallPath;
        }
        var identity = WccInlineGroupIdentity(route);
        var current = WccInlineDrafts.FirstOrDefault(draft => draft.GroupIdentity == identity);
        var value = text;
        var definition = WccInitialFieldDefinition(route);
        var same = inputKind == WccScenarioFieldValueInputKind.WmlExpression
            ? string.Equals(value?.Trim(), (current?.Route ?? route).SourceValue?.Trim(), StringComparison.Ordinal)
            : definition is not null
                ? WccScenarioFieldValueCodec.InferInputKind((current?.Route ?? route).SourceValue) != WccScenarioFieldValueInputKind.WmlExpression &&
                  WccScenarioFieldValueCodec.Equivalent(definition, current?.Route.Value ?? route.Value, value)
                : SameWccInlineValue(route.Key, value?.Trim(), current?.Route.Value ?? route.Value);
        if (operation != WmlFieldOperation.Remove && same)
        {
            if (current is not null) _wccInlineDrafts.Remove(current);
        }
        else
        {
            var error = operation == WmlFieldOperation.Remove ? null : definition is not null
                ? WccScenarioFieldValueCodec.ValidateInput(definition, value, inputKind)
                : ValidateWccInlineValue(route.Key, value?.Trim());
            var ownerLabel = route.Key == "turns" || definition?.OwnerScope == ScenarioInitialFieldOwnerScope.Scenario ? SelectedScenario?.DisplayName :
                definition?.OwnerScope == ScenarioInitialFieldOwnerScope.Leader &&
                ReferenceEquals(SelectedWccInitialLeader?.Owner, route.Owner)
                    ? SelectedSide?.DisplayName + " / " + SelectedWccInitialLeader.DisplayName : SelectedSide?.DisplayName;
            var label = ownerLabel + " / " + route.Key + " (" + _evaluationContext.DisplayName + ")";
            var draft = new WccScenarioInlineDraft(identity, current?.Route ?? route, operation, text,
                current?.Label ?? label, error) { ValueInputKind = inputKind };
            if (current is null) _wccInlineDrafts.Add(draft);
            else _wccInlineDrafts[_wccInlineDrafts.IndexOf(current)] = draft;
        }
        if (publish) FinishWccInlineDraftMutation(route, hadDrafts);
    }

    private void FinishWccInlineDraftMutation(WccScenarioFieldRoute? changedRoute = null, bool? hadDrafts = null)
    {
        using var display = DeferFieldDisplayRefresh();
        if (!HasWccInlineDrafts)
        {
            _wccInlineDraftState = null;
            _wccInlineDraftAsset = null;
            _wccInlineInstallPath = null;
            _wccInlineDraftStatus = null;
        }
        else
            _wccInlineDraftStatus = WccInlineDrafts.FirstOrDefault(draft => draft.ValidationError is not null)?.ValidationError
                ?? GetWccInlineBindingProblem()
                ?? (WccInlineDrafts.Any(draft => draft.Difficulty is not null)
                    ? "Difficulty conversion and values are pending. Save checks every declared difficulty together."
                    : "Draft values use the existing source groups. Every difficulty using the same source value will change together.");
        var targeted = CanRefreshWccInlineDraftDisplay(changedRoute, hadDrafts);
        // A continued draft changes neither RAW editability nor save-busy state.
        // Do not publish their temporary guarded state to unrelated workspaces.
        RaiseWccInlineDraftsChanged(editingCapabilitiesChanged: !targeted);
        // Observers can change source, profile or selection. A targeted display
        // remains valid only if its source binding is still current afterwards.
        if (targeted && CanRefreshWccInlineDraftDisplay(changedRoute, hadDrafts))
            RequestWccInlineDraftDisplayRefresh(changedRoute!);
        else
        {
            if (targeted) OnPropertyChanged(nameof(CanEditDocument));
            RaiseEditingStateChanged();
            RaiseInitialScalarFieldsChanged();
        }
    }

    private bool CanRefreshWccInlineDraftDisplay(WccScenarioFieldRoute? route, bool? hadDrafts) =>
        route is not null && !IsWccInlineSaveBusy && !WccFieldEditSession.IsBusy &&
        hadDrafts == HasWccInlineDrafts && GetWccInlineBindingProblem() is null &&
        IsCurrentWccInitialFieldOwner(route.Owner) && route.ContextSemanticKey == _evaluationContext.SemanticKey &&
        route.Source.Text == _pristineLoadedSourceText && PathsEqual(route.Source.Path, _currentSourcePath ?? LoadedPath);

    private void RaiseWccInlineDraftsChanged(bool editingCapabilitiesChanged = true)
    {
        foreach (var name in new[]
        {
            nameof(HasWccInlineDrafts), nameof(IsWccInlineSaveBusy), nameof(CanSaveWccInlineDrafts),
            nameof(IsWccInlineSaveProgressVisible), nameof(WccInlineSaveProgressStatus),
            nameof(CanDiscardWccInlineDrafts), nameof(WccInlineReviewSummary), nameof(HasWccInlineReview),
            nameof(WccInlineDraftStatus), nameof(WccInlineDraftSummary), nameof(CanEditDocument),
            nameof(ScenarioTurnsHasWccInlineDraft), nameof(SideGoldHasWccInlineDraft), nameof(SideIncomeHasWccInlineDraft),
            nameof(ScenarioTurnsCanRemoveWccOverride), nameof(SideGoldCanRemoveWccOverride), nameof(SideIncomeCanRemoveWccOverride)
        })
            if (editingCapabilitiesChanged || name is not (nameof(IsWccInlineSaveBusy) or nameof(CanEditDocument)))
                OnPropertyChanged(name);
    }

    private string? GetWccInlineBindingProblem()
    {
        if (!HasWccInlineDrafts) return null;
        var state = _wccInlineDraftState;
        if (state is null || !ReferenceEquals(_wccInlineDraftAsset, _currentScenarioAsset) ||
            !PathsEqual(state.Source.Path, _currentSourcePath ?? LoadedPath) ||
            !string.Equals(state.Source.Text, _pristineLoadedSourceText, StringComparison.Ordinal) ||
            !string.Equals(state.Source.Text, _currentScenarioAsset?.RawTextSnapshot, StringComparison.Ordinal))
            return "The loaded source changed. Drafts are retained; discard them or return to their original source.";
        if (IsReadOnly && !CanUseSelectedScenarioDrafts || !_sourceHostSession.CanWrite || _sourceHostSession.Scope != WccEditorSessionScope.Project ||
            !string.Equals(state.ProjectIdentity, CurrentWccFieldProjectIdentity, StringComparison.Ordinal) ||
            !PathsEqual(state.ProjectRootPath, _sourceHostSession.RootPath) ||
            !ReferenceEquals(state.Snapshot, _macroLibrarySnapshot) || _staticRootRevisionSelection is not { } root ||
            root.RootRevisionId != state.RootRevisionId || !ReferenceEquals(state.Plan, _sourceEvaluationPlan) ||
            !string.Equals(state.ProfileIdentity, _wesnothSourceProfileIdentity, StringComparison.Ordinal) ||
            !SameOptionalWccInlinePath(_wccInlineInstallPath, _wesnothSourceInstallPath) ||
            !SameOptionalWccInlinePath(state.ConfiguredAddonsPath, _wesnothSourceAddonsPath))
            return "The campaign or runtime binding changed. Your drafts are retained; discard them before rebinding the source.";
        if (_hasAuthoredSourceChanges || _externalSourceChangesPending)
            return "Other source changes are pending. Your inline drafts are retained; resolve the source changes before saving.";
        if (GetWccDifficultyDraftBindingProblem() is { } difficultyProblem) return difficultyProblem;
        if (WccFieldEditSession.Recovery is not null)
            return "A previous save requires source recovery. Your drafts are retained.";
        return null;
    }

    public Task<WccScenarioFieldBatchEditResult> SaveWccInlineDraftsAsync(CancellationToken cancellationToken = default)
    {
        if (IsWccInlineSaveBusy)
            return Task.FromResult(new WccScenarioFieldBatchEditResult(WccScenarioFieldEditStatus.InvalidRequest,
                "The collected save is already running."));
        return _wccInlineSaveTask = SaveWccInlineDraftsCoreAsync(cancellationToken);
    }

    public Task WaitForWccInlineSaveAsync(CancellationToken cancellationToken = default) =>
        _wccInlineSaveTask?.WaitAsync(cancellationToken) ?? Task.CompletedTask;

    private async Task<WccScenarioFieldBatchEditResult> SaveWccInlineDraftsCoreAsync(CancellationToken cancellationToken)
    {
        if (HasSelectedCampaignFieldDrafts)
            return new(WccScenarioFieldEditStatus.InvalidRequest, "Use the shared Save to check these changes in the selected campaign's declared contexts.");
        if (WccInlineDrafts.Any(draft => draft.Difficulty is not null))
            return new(WccScenarioFieldEditStatus.InvalidRequest,
                "Use the shared Save for pending difficulty conversions and field changes.");
        if ((HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft))
            return new(WccScenarioFieldEditStatus.InvalidRequest,
                "Use the shared Save for the pending flag preset, event and field changes.");
        if (IsWccInlineSaveBusy)
            return new(WccScenarioFieldEditStatus.InvalidRequest, "The collected save is already running.");
        if (!HasWccInlineDrafts || _wccInlineDraftState is null)
            return new(WccScenarioFieldEditStatus.InvalidRequest, "There are no inline source changes to save.");
        var problem = WccInlineDrafts.FirstOrDefault(draft => draft.ValidationError is not null)?.ValidationError
            ?? GetWccInlineBindingProblem();
        if (problem is not null)
        {
            _wccInlineDraftStatus = Status = problem;
            RaiseWccInlineDraftsChanged();
            return new(WccScenarioFieldEditStatus.InvalidRequest, problem);
        }
        var state = _wccInlineDraftState;
        var proposals = WccInlineDrafts.Select(draft => new WccScenarioFieldEditProposal(
            draft.Route, draft.Operation, draft.Route.Key == "turns" ? NormalizeWccInlineRequestedValue(draft.Text) : draft.Text)
            { ValueInputKind = draft.ValueInputKind }).ToArray();
        var asset = _currentScenarioAsset!;
        var generation = _sourceHostGeneration;
        var scenarioIndex = SelectedScenario is null ? 0 : Scenarios.IndexOf(SelectedScenario);
        var sideIndex = GetAuthoredSideIndex(SelectedSide);
        var selectedOwner = SelectedSide?.IsGenerated == true ? SelectedStaticSideOwner : null;
        var selectedLeader = SelectedWccInitialLeader;
        _wccInlineSaveProgressVersion++;
        _wccInlinePreviewProgress = null;
        _isWccInlineSaveBusy = true;
        _wccInlineSaveProgressStatus = "Checking and saving changes in every campaign difficulty…";
        _wccInlineDraftStatus = Status = _wccInlineSaveProgressStatus;
        RaiseWccInlineDraftsChanged();
        RaiseInitialScalarFieldsChanged();
        try
        {
            var result = await WccFieldEditSession.SaveBatchAsync(state, proposals, cancellationToken);
            if (result.Review is { } review)
                _wccInlineReviewSummary = string.Join("\n", review.Fields.SelectMany(field => field.Contexts.Select(context =>
                    field.Route.Key + " / " + context.DisplayName + ": " + (context.OldValue ?? "(absent)") + " → " +
                    (context.NewValue ?? "(absent)"))).Concat(review.Contexts.SelectMany(context => context.CoupledEffects.Select(effect =>
                        context.DisplayName + ": " + effect))));
            if (!result.IsSuccess)
            {
                _wccInlineDraftStatus = Status = result.Diagnostic;
                return result;
            }
            _wccInlineSaveProgressStatus = "Changes saved. Refreshing the source and preview…";
            RaiseWccInlineDraftsChanged();
            // Persistence succeeded. Do not allow navigation or a spent caller
            // cancellation to omit the fresh physical-file reimport.
            var text = await File.ReadAllTextAsync(state.Source.Path);
            if (generation != _sourceHostGeneration || !ReferenceEquals(asset, _currentScenarioAsset))
            {
                _wccInlineDraftStatus = Status = "The changes were saved. Reload their source before further editing.";
                return result;
            }
            _wccInlineDrafts.Clear();
            _wccInlineDraftState = null;
            _wccInlineDraftAsset = null;
            _wccInlineInstallPath = null;
            if (selectedOwner is not null && result.Candidate is { } candidate)
            {
                _pendingWccSideSelection = owner => SameMappedOwner(selectedOwner, owner, candidate);
                _pendingWccSideSelectionPath = candidate.CandidateSource.Path;
            }
            if (result.Candidate is { } leaderCandidate)
                RememberWccInitialLeaderSelection(selectedLeader, leaderCandidate);
            _wccInlineDraftStatus = null;
            RaiseWccInlineDraftsChanged();
            await PublishSavedScenarioSourceAsync(asset, text, scenarioIndex, sideIndex,
                result.Status == WccScenarioFieldEditStatus.SucceededWithCleanupWarning
                    ? "Changes saved. " + result.Diagnostic : "Changes saved; refreshing the WCC preview.");
            if (ReferenceEquals(asset, _currentScenarioAsset) && PathsEqual(state.Source.Path, _currentSourcePath ?? LoadedPath) &&
                !_staticInitialValuesTask.IsCompleted)
            {
                // Only the indicator follows the final background preview. Save,
                // navigation and editing retain their existing task lifetimes.
                var preview = new WccInlinePreviewProgress(_staticInitialValuesTask, _staticInitialValuesGeneration,
                    _wccInlineSaveProgressVersion, asset, state.Source.Path);
                _wccInlinePreviewProgress = preview;
                _ = ObserveWccInlinePreviewProgressAsync(preview);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            _wccInlineDraftStatus = Status = "The check was cancelled. Your drafts are retained.";
            return new(WccScenarioFieldEditStatus.InvalidRequest, _wccInlineDraftStatus);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _wccInlineDraftStatus = Status = "Could not finish the collected save: " + exception.Message;
            return new(WccScenarioFieldEditStatus.InvalidRequest, _wccInlineDraftStatus);
        }
        finally
        {
            _isWccInlineSaveBusy = false;
            RaiseWccInlineDraftsChanged();
            RaiseEditingStateChanged();
            RaiseInitialScalarFieldsChanged();
        }
    }

    private async Task ObserveWccInlinePreviewProgressAsync(WccInlinePreviewProgress preview)
    {
        try { await preview.Task; }
        catch (Exception)
        {
            // The preview owns its diagnostics. This observer only controls the
            // activity indicator and cannot change publication or save results.
        }
        finally
        {
            if (ReferenceEquals(_wccInlinePreviewProgress, preview) && preview.Version == _wccInlineSaveProgressVersion)
            {
                _wccInlinePreviewProgress = null;
                RaiseWccInlineSaveProgressChanged();
            }
        }
    }

    private sealed record WccInlinePreviewProgress(Task Task, long Generation, long Version,
        ScenarioFileAsset Asset, string SourcePath);

    private void RaiseWccInlineSaveProgressChanged()
    {
        OnPropertyChanged(nameof(IsWccInlineSaveProgressVisible));
        OnPropertyChanged(nameof(WccInlineSaveProgressStatus));
    }

    private static bool SameMappedOwner(WmlStaticScalarOwner before, WmlStaticScalarOwner after,
        WccScenarioFieldBatchCandidate candidate)
    {
        bool SameRange(WmlStaticSourceRange? old, WmlStaticSourceRange? fresh)
        {
            if (old is null || fresh is null) return false;
            var mapped = PathsEqual(old.Source.Path, candidate.Source.Path) ? candidate.MapRange(old) : old;
            return mapped is not null && PathsEqual(mapped.Source.Path, fresh.Source.Path) &&
                mapped.StartOffset == fresh.StartOffset && mapped.Length == fresh.Length;
        }
        if (!SameRange(before.OpeningOrigin, after.OpeningOrigin) ||
            before.OpeningExpansionStack.Count != after.OpeningExpansionStack.Count) return false;
        for (var index = 0; index < before.OpeningExpansionStack.Count; index++)
        {
            var old = before.OpeningExpansionStack[index];
            var fresh = after.OpeningExpansionStack[index];
            if (old.MacroName != fresh.MacroName || !SameRange(old.Definition, fresh.Definition) ||
                old.InvocationOrigins.Count != fresh.InvocationOrigins.Count) return false;
            for (var origin = 0; origin < old.InvocationOrigins.Count; origin++)
                if (!SameRange(old.InvocationOrigins[origin], fresh.InvocationOrigins[origin])) return false;
        }
        return true;
    }

    private static string WccInlineGroupIdentity(WccScenarioFieldRoute route) =>
        WccScenarioFieldBatchCandidatePlanner.GetSourceEditIdentity(route);

    private static bool SameOptionalWccInlinePath(string? before, string? after) =>
        string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after) || PathsEqual(before, after);

    private static string? ValidateWccInlineValue(string key, string? text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ||
        key == "turns" && text == "unlimited" ? null :
            key == "turns" ? "Enter a whole number or unlimited for maximum turns." : "Enter a whole number for " + key + ".";

    private static string? NormalizeWccInlineRequestedValue(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value.ToString(CultureInfo.InvariantCulture) : text?.Trim();

    private static bool SameWccInlineValue(string key, string? left, string? right) =>
        SameInteger(left, right) || key == "turns" && left == "unlimited" && right == "unlimited";
}
