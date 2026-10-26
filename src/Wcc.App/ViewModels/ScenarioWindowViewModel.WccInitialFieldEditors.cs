using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private bool _refreshingWccInitialFieldEditors;
    public ObservableCollection<WccInitialFieldEditorVm> WccScenarioFieldEditors { get; } = new();
    public ObservableCollection<WccInitialFieldEditorVm> WccSideFieldEditors { get; } = new();
    public ObservableCollection<WccInitialFieldEditorVm> WccLeaderFieldEditors { get; } = new();

    public IReadOnlyList<WccInitialFieldEditorGroup> WccScenarioFieldGroups { get; private set; } =
        Array.Empty<WccInitialFieldEditorGroup>();

    public IReadOnlyList<WccInitialFieldEditorGroup> WccSideFieldGroups { get; private set; } =
        Array.Empty<WccInitialFieldEditorGroup>();

    public IReadOnlyList<WccInitialFieldEditorGroup> WccLeaderFieldGroups { get; private set; } =
        Array.Empty<WccInitialFieldEditorGroup>();

    public bool HasWccInitialFieldEditorsStatus => SelectedSide is not null &&
                                                   (_hasAuthoredSourceChanges || _externalSourceChangesPending ||
                                                    !HasCurrentStaticScalarProjection ||
                                                    SelectedStaticSideOwner is null);

    public bool HasWccScenarioFieldEditorsStatus => SelectedScenario is not null &&
        (_hasAuthoredSourceChanges || _externalSourceChangesPending ||
         !HasCurrentStaticScalarProjection || _staticScenarioOwner is null);

    public string? WccScenarioFieldEditorsStatus => _externalSourceChangesPending
        ? "Reload the pending external source changes to resolve these fields."
        : _hasAuthoredSourceChanges
            ? "Save source changes to resolve and edit these fields."
            : StaticInitialValuesStatus ?? "Resolving the exact scenario source for this campaign context…";

    public string? WccInitialFieldEditorsStatus => _externalSourceChangesPending
        ? "Reload the pending external source changes to resolve these fields."
        : _hasAuthoredSourceChanges
            ? "Save source changes to resolve and edit these fields."
            : StaticInitialValuesStatus ?? "Resolving the exact side source for this campaign context…";

    internal void RefreshWccInitialFieldEditors()
        => RequestFieldDisplayRefresh(full: false);

    private void RefreshWccInitialFieldEditorsNow()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_refreshingWccInitialFieldEditors)
            {
                _fieldEditorRefreshPending = true;
                return;
            }
            _refreshingWccInitialFieldEditors = true;
            try
            {
                RefreshWccInitialFieldEditorsCore();
            }
            finally
            {
                _refreshingWccInitialFieldEditors = false;
            }
        }
    }

    private void RefreshWccInitialFieldEditorsCore()
    {
        OnPropertyChanged(nameof(HasWccScenarioFieldEditorsStatus));
        OnPropertyChanged(nameof(WccScenarioFieldEditorsStatus));
        OnPropertyChanged(nameof(HasWccInitialFieldEditorsStatus));
        OnPropertyChanged(nameof(WccInitialFieldEditorsStatus));
        var scenarioChanged = RefreshWccFieldRows(WccScenarioFieldEditors,
            HasCurrentStaticScalarProjection ? _staticScenarioOwner : null,
            ScenarioInitialFieldCatalog.ScenarioFields);
        var sideChanged = RefreshWccFieldRows(WccSideFieldEditors, SelectedStaticSideOwner,
            ScenarioInitialFieldCatalog.SideFields);
        var leader = SelectedWccInitialLeader;
        var leaderChanged = RefreshWccFieldRows(WccLeaderFieldEditors, leader?.Owner,
            leader?.IsLegacyInline == true
                ? ScenarioInitialFieldCatalog.LegacyInlineFields
                : ScenarioInitialFieldCatalog.LeaderFields);
        if (scenarioChanged)
        {
            WccScenarioFieldGroups = GroupWccFieldRows(WccScenarioFieldEditors);
            OnPropertyChanged(nameof(WccScenarioFieldGroups));
        }

        if (sideChanged)
        {
            WccSideFieldGroups = GroupWccFieldRows(WccSideFieldEditors);
            OnPropertyChanged(nameof(WccSideFieldGroups));
        }

        if (leaderChanged)
        {
            WccLeaderFieldGroups = GroupWccFieldRows(WccLeaderFieldEditors);
            OnPropertyChanged(nameof(WccLeaderFieldGroups));
        }
        RefreshWccFieldPresentation();
        RefreshWccFieldImagePreviews();
    }

    private void RefreshWccInlineDraftFieldEditorsNow(IReadOnlyList<WccScenarioFieldRoute> routes)
    {
        if (routes.Count == 0) return;
        if (GetWccInlineBindingProblem() is not null || routes.Any(route => !IsCurrentWccInitialFieldOwner(route.Owner) ||
            route.ContextSemanticKey != _evaluationContext.SemanticKey || route.Source.Text != _pristineLoadedSourceText ||
            !PathsEqual(route.Source.Path, _currentSourcePath ?? LoadedPath)))
        {
            RaiseEditingStateChanged();
            RaiseInitialScalarFieldsChanged();
            return;
        }
        var identities = routes.Select(WccInlineGroupIdentity).ToHashSet(StringComparer.Ordinal);
        var rows = CurrentWccPresentationRows.ToArray();
        var affected = rows.Where(row => GetWccInitialFieldRoute(row.Owner, row.Key) is { } route &&
            route.Kind != WccScenarioFieldRouteKind.RawOnly && (route.CanSet || route.CanInsert) &&
            identities.Contains(WccInlineGroupIdentity(route))).ToHashSet();
        var changedFields = affected.Select(row => (row.Owner, row.Key))
            .Concat(routes.Select(route => (route.Owner, route.Key))).Distinct().ToArray();
        foreach (var row in rows)
            if (changedFields.Any(changed => WccInitialFieldDefaultDependsOn(row.Owner, row.Definition, changed.Owner, changed.Key)))
                affected.Add(row);
        foreach (var row in affected)
        {
            // A callback may replace source, profile or selection. That operation
            // queues the ordinary full publication; never continue with its old rows.
            if (!IsCurrentWccInitialFieldOwner(row.Owner) ||
                !IsSelectedWccInitialFieldOwner(row.Owner, row.Definition.OwnerScope)) return;
            row.Refresh();
        }
        foreach (var group in WccScenarioFieldGroups.Concat(WccSideFieldGroups).Concat(WccLeaderFieldGroups))
            if (group.Fields.Any(affected.Contains)) group.RefreshVisibility();
        WccFieldVisibilityChanged?.Invoke(this, EventArgs.Empty);
        if (affected.Any(row => row.Definition.Kind == ScenarioInitialFieldKind.Image ||
            row.Key is "type" or "id" or "name" or "no_leader" or "x" or "y"))
            RefreshWccFieldImagePreviews();
        // Retain the older scalar display API without refreshing unrelated rows.
        foreach (var key in changedFields.Select(field => field.Key).Distinct(StringComparer.Ordinal))
        {
            var prefix = key switch { "turns" => "ScenarioTurns", "gold" => "SideGold", "income" => "SideIncome", _ => null };
            if (prefix is null) continue;
            foreach (var suffix in new[] { "Display", "DisplayIsReadOnly", "DisplayStatus", "Presentation", "WccField" })
                OnPropertyChanged(prefix + suffix);
        }
    }

    private bool RefreshWccFieldRows(ObservableCollection<WccInitialFieldEditorVm> rows,
        WmlStaticScalarOwner? owner, IReadOnlyList<ScenarioInitialFieldDefinition> definitions)
    {
        var changed = owner is null
            ? rows.Count != 0
            : rows.Count != definitions.Count ||
              rows.Any(row => !ReferenceEquals(row.Owner, owner));
        if (changed)
        {
            rows.Clear();
            if (owner is not null)
                foreach (var definition in definitions)
                    rows.Add(new(this, owner, definition));
        }

        foreach (var row in rows) row.Refresh();
        return changed;
    }

    private IReadOnlyList<WccInitialFieldEditorGroup> GroupWccFieldRows(
        IEnumerable<WccInitialFieldEditorVm> rows) =>
        rows.GroupBy(row => row.Definition.Group).OrderBy(group => group.Key == "Economy" ? 0 : 1)
            .Select(group => GetWccFieldPresentationGroup(group.Key, group.ToArray())).ToArray();

    internal bool SetWccInitialFieldValue(WmlStaticScalarOwner owner, string key, string? value,
        WccScenarioFieldValueInputKind inputKind)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            // A headless background publication finishes before a normal input;
            // a reentrant input during its callbacks cannot mutate stale rows.
            if (_fieldDisplayDeferralDepth > 0 || _flushingFieldDisplay) return false;
            if (IsWccPendingLeaderOwner(owner)) return SetPendingLeaderField(owner, key, value, inputKind);
            var route = GetWccInitialFieldRoute(owner, key);
            if (route is null || !CanReviewWccInitialField(owner, key)) return false;
            SetWccInlineDraft(route, route.CanSet ? WmlFieldOperation.Set : WmlFieldOperation.Insert, value, inputKind);
            return true;
        }
    }

    internal bool IsSelectedWccInitialFieldOwner(WmlStaticScalarOwner owner, ScenarioInitialFieldOwnerScope scope) =>
        ReferenceEquals(owner, scope switch
        {
            ScenarioInitialFieldOwnerScope.Scenario => HasCurrentStaticScalarProjection ? _staticScenarioOwner : null,
            ScenarioInitialFieldOwnerScope.Side => SelectedStaticSideOwner,
            ScenarioInitialFieldOwnerScope.Leader => SelectedWccInitialLeader?.Owner,
            _ => null
        });

    internal void RevertWccInitialField(WmlStaticScalarOwner owner, string key)
    {
        if (IsWccPendingLeaderOwner(owner)) { ResetPendingLeaderField(owner, key); return; }
        if (FindWccInlineDraft(GetWccInitialFieldRoute(owner, key)) is { } draft) RevertWccInlineDraft(draft);
    }

    internal void RemoveWccInitialFieldOverride(WmlStaticScalarOwner owner, string key)
    {
        if (IsWccPendingLeaderOwner(owner)) { ResetPendingLeaderField(owner, key); return; }
        var route = GetWccInitialFieldRoute(owner, key);
        if (route is { CanRemove: true } && CanReviewWccInitialField(owner, key))
            SetWccInlineDraft(route, WmlFieldOperation.Remove, null);
    }

    internal ScenarioSideSourceLocation? GetWccInitialFieldSourceLocation(WmlStaticScalarOwner owner, string key)
    {
        if (IsWccPendingLeaderOwner(owner)) return null;
        if (_fieldDisplayDeferralDepth > 0) return null;
        var route = GetWccInitialFieldRoute(owner, key);
        var range = route?.Kind is WccScenarioFieldRouteKind.OptionalArgument
                        or WccScenarioFieldRouteKind.ScalarArgument ||
                    route?.Origin is WccScenarioFieldOrigin.Macro or WccScenarioFieldOrigin.Override
            ? route?.Invocation ?? route?.Assignment ?? route?.Target
            : route?.Assignment ?? route?.Target ?? route?.Invocation;
        range ??= owner.OpeningExpansionStack.Reverse().Where(frame => frame.InvocationOrigins.Count == 1)
            .Select(frame => frame.InvocationOrigins[0]).FirstOrDefault(IsNavigableSourceRange);
        range ??= owner.OpeningOrigin;
        return range is not null && IsCurrentWccInitialFieldOwner(owner) && IsNavigableSourceRange(range)
            ? new(range.Source.Path, range.Source.Text, range.StartOffset, range.Length,
                "Captured authored source for " + key +
                ". This view is read-only. Shared definitions can affect other scenarios.")
            : null;
    }

    internal async Task PickWccInitialLeaderCoordinateAsync(WmlStaticScalarOwner owner, ScenarioMapCoordinatePicker? picker = null)
    {
        if (!HasMapPreview || !CanReviewWccInitialField(owner, "x") || !CanReviewWccInitialField(owner, "y")) return;
        var targetScenario = SelectedScenario;
        var targetSide = SelectedSide;
        var targetMap = MapPreview;
        var targetContext = _evaluationContext.SemanticKey;
        var targetVersion = EditVersion;
        var targetLeaderVersion = _leaderAdditionVersion;
        var targetDrafts = WccInlineDrafts.ToArray();
        var xDraft = FindWccInlineDraft(GetWccInitialFieldRoute(owner, "x"));
        var yDraft = FindWccInlineDraft(GetWccInitialFieldRoute(owner, "y"));
        var x = FindWccInlineDraft(GetWccInitialFieldRoute(owner, "x"))?.Text ?? owner.GetKnownValue("x");
        var y = FindWccInlineDraft(GetWccInitialFieldRoute(owner, "y"))?.Text ?? owner.GetKnownValue("y");
        ScenarioMapCoordinate? initial =
            int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var initialX) &&
            int.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out var initialY)
                ? new(initialX, initialY)
                : null;
        var request = new ScenarioMapCoordinateSelectionRequest("Choose the map hex for the selected leader.", initial);
        var result = await (picker is null ? BeginMapCoordinateSelectionAsync(request) : picker(targetMap!, request));
        if (!ReferenceEquals(SelectedScenario, targetScenario) || !ReferenceEquals(SelectedSide, targetSide) ||
            !ReferenceEquals(SelectedWccInitialLeader?.Owner, owner) || !IsCurrentWccInitialFieldOwner(owner) ||
            !ReferenceEquals(MapPreview, targetMap) || targetContext != _evaluationContext.SemanticKey || EditVersion != targetVersion ||
            targetLeaderVersion != _leaderAdditionVersion ||
            !targetDrafts.SequenceEqual(WccInlineDrafts) ||
            !ReferenceEquals(xDraft, FindWccInlineDraft(GetWccInitialFieldRoute(owner, "x"))) ||
            !ReferenceEquals(yDraft, FindWccInlineDraft(GetWccInitialFieldRoute(owner, "y"))) ||
            result.Outcome == ScenarioMapCoordinateSelectionOutcome.MapChanged) return;
        if (result.IsConfirmed && result.Selection is { Coordinate: var coordinate } &&
            ReferenceEquals(SelectedWccInitialLeader?.Owner, owner) && CanReviewWccInitialField(owner, "x") &&
            CanReviewWccInitialField(owner, "y"))
        {
            if (SetWccInitialLeaderCoordinates(owner, coordinate))
                Status = "Leader coordinate selected. Save checks it with the other draft values.";
        }

        if (picker is null && (result.IsConfirmed || result.Outcome == ScenarioMapCoordinateSelectionOutcome.Cancelled))
            SelectedEditorSectionIndex = 1;
    }

    private bool SetWccInitialLeaderCoordinates(WmlStaticScalarOwner owner, ScenarioMapCoordinate coordinate)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_fieldDisplayDeferralDepth > 0 || _flushingFieldDisplay ||
                !CanReviewWccInitialField(owner, "x") || !CanReviewWccInitialField(owner, "y")) return false;
            if (IsWccPendingLeaderOwner(owner)) return SetPendingLeaderCoordinates(owner, coordinate);
            var x = GetWccInitialFieldRoute(owner, "x"); var y = GetWccInitialFieldRoute(owner, "y");
            if (x is null || y is null) return false;
            using var display = DeferFieldDisplayRefresh();
            SetWccInlineDraftCore(x, x.CanSet ? WmlFieldOperation.Set : WmlFieldOperation.Insert,
                coordinate.X.ToString(CultureInfo.InvariantCulture), WccScenarioFieldValueInputKind.Literal, publish: false);
            SetWccInlineDraftCore(y, y.CanSet ? WmlFieldOperation.Set : WmlFieldOperation.Insert,
                coordinate.Y.ToString(CultureInfo.InvariantCulture), WccScenarioFieldValueInputKind.Literal, publish: false);
            FinishWccInlineDraftMutation();
            return true;
        }
    }
}