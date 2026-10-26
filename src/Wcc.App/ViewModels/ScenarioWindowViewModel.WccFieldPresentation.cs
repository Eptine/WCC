using System;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>Source provenance and edit availability are independent field properties.</summary>
public sealed record WccInitialFieldPresentation(string OriginLabel, string Glyph, string Tooltip,
    bool CanEdit, bool HasSource, bool IsBlocked);

public partial class ScenarioWindowViewModel
{
    public WccInitialFieldPresentation ScenarioTurnsWccField => BuildWccInitialFieldPresentation("turns", ScenarioTurnsField.CanEditAuthoredValue);
    public WccInitialFieldPresentation SideNumberWccField => BuildWccInitialFieldPresentation("side", !SideNumberDisplayIsReadOnly);
    public WccInitialFieldPresentation SideGoldWccField => BuildWccInitialFieldPresentation("gold", SideGoldField.CanEditAuthoredValue);
    public WccInitialFieldPresentation SideIncomeWccField => BuildWccInitialFieldPresentation("income", SideIncomeField.CanEditAuthoredValue);
    public WccInitialFieldPresentation SideControllerWccField => BuildWccInitialFieldPresentation("controller",
        !SideControllerDisplayIsReadOnly);

    public string? SideControllerDisplay
    {
        get
        {
            if (_hasAuthoredSourceChanges || _externalSourceChangesPending || !HasCurrentStaticScalarProjection)
                return SelectedSide?.IsGenerated == true ? null : SideController;
            var @field = SelectedStaticSideOwner?.ResolveField("controller");
            return @field?.State == WmlStaticScalarResolutionState.Known
                ? NormalizeInitialScalarLiteral(@field.ValueText) : null;
        }
        set { if (!SideControllerDisplayIsReadOnly) SideController = value; }
    }

    public bool SideControllerDisplayIsReadOnly => SelectedSide?.IsGenerated == true ||
        !SideControllerIsEditable || HasCurrentStaticScalarProjection &&
        !string.Equals(SideControllerDisplay, NormalizeInitialScalarLiteral(SideController), StringComparison.Ordinal);

    public string SideControllerDisplayStatus => SideControllerWccField.Tooltip;

    private WccInitialFieldPresentation BuildWccInitialFieldPresentation(string key, bool canEditLegacy = false)
    {
        var route = GetWccInitialFieldRoute(key);
        var (label, glyph) = route?.Origin switch
        {
            WccScenarioFieldOrigin.Scenario => ("Resolved in Scenario", "S"),
            WccScenarioFieldOrigin.Macro => ("Resolved in Macro", "M"),
            WccScenarioFieldOrigin.Override => ("Resolved as override", "O"),
            WccScenarioFieldOrigin.Absent => ("No explicit value", "+"),
            _ => ("Unresolved", "?")
        };
        var canReview = CanReviewWccInitialField(key);
        var canEdit = canEditLegacy || canReview;
        var hasSource = key == "turns" ? SelectedScenario is not null : SelectedSide is not null;
        var explanation = GetWccInitialFieldEditStatus(key);
        var tooltip = label + ". " + explanation;
        if (canReview)
            tooltip += " Type a draft value directly and Save all changes together. Difficulties using this source value change together.";
        if (canEditLegacy)
            tooltip += " The existing authored field editor is available.";
        if (!canEdit && hasSource)
            tooltip += " Use this icon to open actual source. The resolved preview is read-only.";
        return new(label, glyph + (!canEdit && hasSource ? " ⊘" : ""), tooltip,
            canReview, hasSource, !canEdit);
    }

    /// <summary>Opens captured authored source; this location grants no write permission.</summary>
    public ScenarioSideSourceLocation? GetWccInitialFieldSourceLocation(string key)
    {
        if (_fieldDisplayDeferralDepth > 0) return null;
        var route = GetWccInitialFieldRoute(key);
        var range = route?.Kind is WccScenarioFieldRouteKind.OptionalArgument or WccScenarioFieldRouteKind.ScalarArgument ||
            route?.Origin is WccScenarioFieldOrigin.Macro or WccScenarioFieldOrigin.Override
            ? route?.Invocation ?? route?.Assignment ?? route?.Target
            : route?.Assignment ?? route?.Target ?? route?.Invocation;
        if (range is not null && IsNavigableSourceRange(range))
            return new(range.Source.Path, range.Source.Text, range.StartOffset, range.Length,
                "Captured authored source for " + key + ". This view is read-only. Shared macro definitions can affect other scenarios.");
        if (key != "turns")
            return GetSelectedSideSourceLocation();
        if (!HasCurrentStaticScalarProjection || _hasAuthoredSourceChanges || _externalSourceChangesPending ||
            _staticScenarioOwner is not { OpeningOutputOffset: not null } owner)
            return null;
        var origins = owner.OpeningExpansionStack.Reverse().Where(frame => frame.InvocationOrigins.Count == 1)
            .Select(frame => frame.InvocationOrigins[0]).Where(IsNavigableSourceRange).ToArray();
        range = origins.FirstOrDefault(origin => PathsEqual(origin.Source.Path, _currentSourcePath ?? LoadedPath) &&
            SelectedScenario is { } scenario && origin.StartOffset >= scenario.Document.BlockSpan.StartIndex &&
            origin.StartOffset + origin.Length <= scenario.Document.BlockSpan.EndIndexExclusive) ??
            (owner.OpeningOrigin is { } opening && IsNavigableSourceRange(opening) ? opening : null);
        return range is null ? null : new(range.Source.Path, range.Source.Text, range.StartOffset, range.Length,
            "Captured authored source for maximum turns. This view is read-only.");
    }

    private void RaiseWccInitialFieldPresentationChanged()
    {
        foreach (var property in new[]
        {
            nameof(ScenarioTurnsWccField), nameof(SideNumberWccField), nameof(SideGoldWccField),
            nameof(SideIncomeWccField), nameof(SideControllerWccField), nameof(SideControllerDisplay),
            nameof(SideControllerDisplayIsReadOnly), nameof(SideControllerDisplayStatus)
        })
            OnPropertyChanged(property);
    }
}
