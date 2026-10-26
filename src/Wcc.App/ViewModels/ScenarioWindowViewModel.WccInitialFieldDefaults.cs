using System;
using System.Globalization;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    /// <summary>Display inheritance from exact current owners, never source or persistence authority.</summary>
    internal string? GetWccInitialFieldDefaultValue(WmlStaticScalarOwner owner,
        ScenarioInitialFieldDefinition definition)
    {
        if (!IsCurrentWccInitialFieldOwner(owner) ||
            owner.ResolveField(definition.Key).State != WmlStaticScalarResolutionState.ProvenAbsent ||
            definition.Default.Kind != ScenarioInitialFieldDefaultKind.Derived)
            return null;

        if (definition.OwnerScope == ScenarioInitialFieldOwnerScope.Scenario)
        {
            // An explicit start time disables randomness; without one the
            // scheduler uses the first slot only when no random choice is active.
            return definition.Key == "current_time" &&
                   TryGetWccDefaultDependency(owner, "random_start_time", out var randomStart) &&
                   randomStart is null or "no" or "false"
                ? "0"
                : null;
        }

        if (definition.OwnerScope == ScenarioInitialFieldOwnerScope.Leader)
        {
            if (definition.Key == "invulnerable")
            {
                // A truthy status sets this flag. False status does not undo traits/modifications.
                var status = owner is { OpeningOutputOffset: not null, HasUnresolvedContributions: false }
                    ? owner.DirectChildren.FirstOrDefault(child => child.TagName == "status")
                    : null;
                return status is { OpeningOutputOffset: not null } &&
                       TryGetWccDefaultDependency(status, "invulnerable", out var flag) &&
                       flag is "yes" or "true"
                    ? "yes"
                    : null;
            }

            var maximum = definition.Key switch
            {
                "hitpoints" => "max_hitpoints", "moves" => "max_moves", "attacks_left" => "max_attacks", _ => null
            };
            if (maximum is null || !TryGetWccDefaultDependency(owner, maximum, out var text) ||
                !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                return null;
            return Math.Max(definition.Key == "hitpoints" ? 1 : 0, number).ToString(CultureInfo.InvariantCulture);
        }

        switch (definition.Key)
        {
            case "team_name":
                return GetWccDefaultTeamName(owner);
            case "user_team_name":
                return TryGetWccDefaultDependency(owner, "team_name", out var team)
                    ? string.IsNullOrEmpty(team) ? GetWccDefaultTeamName(owner) : team
                    : null;
            case "save_id":
                if (!TryGetWccDefaultDependency(owner, "id", out var id)) return null;
                if (!string.IsNullOrEmpty(id)) return id;
                var first = GetWccDefaultFirstNestedLeader(owner);
                return first is not null && TryGetWccDefaultDependency(first, "id", out var nestedId)
                    ? nestedId
                    : null;
            case "side_name":
                return GetWccDefaultSideName(owner);
            case "persistent":
                if (!TryGetWccDefaultDependency(owner, "controller", out var controller)) return null;
                return controller == "human" ? "yes" : controller is "ai" or "null" ? "no" : null;
            case "share_vision":
                return GetWccDefaultShareVision(owner);
            case "carryover_percentage":
                if (!TryGetWccDefaultDependency(_staticScenarioOwner!, "carryover_percentage", out var percentage))
                    return null;
                return percentage is null
                    ? "80"
                    : int.TryParse(percentage, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                        out var value)
                        ? value.ToString(CultureInfo.InvariantCulture)
                        : null;
            case "carryover_add":
                if (!TryGetWccDefaultDependency(_staticScenarioOwner!, "carryover_add", out var add)) return null;
                return add is null or "no" or "false" ? "no" : add is "yes" or "true" ? "yes" : null;
            case "objectives":
                return TryGetWccDefaultDependency(_staticScenarioOwner!, "objectives", out var objectives)
                    ? objectives ?? ""
                    : null;
            default:
                return null;
        }
    }

    // Keep draft invalidation beside the derived-value rules. These are display
    // dependencies only; they do not grant source ownership or Save authority.
    private static bool WccInitialFieldDefaultDependsOn(WmlStaticScalarOwner owner,
        ScenarioInitialFieldDefinition definition, WmlStaticScalarOwner changedOwner, string changedKey)
    {
        if (ReferenceEquals(owner, changedOwner))
            return (changedKey, definition.Key) is
                ("controller", "persistent") or ("canrecruit", "dismissable") or
                ("random_start_time", "current_time") or ("team_name", "user_team_name") or
                ("id", "save_id") or ("name" or "type" or "no_leader", "side_name") or
                ("share_view" or "share_maps", "share_vision") or
                ("max_hitpoints", "hitpoints") or ("max_moves", "moves") or ("max_attacks", "attacks_left");
        if (ReferenceEquals(owner.Parent, changedOwner) && changedOwner.TagName == "scenario")
            return changedKey == definition.Key && changedKey is "carryover_percentage" or "carryover_add" or "objectives";
        return ReferenceEquals(changedOwner.Parent, owner) && changedOwner.TagName == "leader" &&
            (changedKey, definition.Key) is ("id", "save_id") or ("name", "side_name");
    }

    private string? GetWccDefaultShareVision(WmlStaticScalarOwner owner)
    {
        if (!TryGetWccDefaultDependency(owner, "share_view", out var view)) return null;
        if (view is "yes" or "true") return "all";
        if (view is not (null or "no" or "false") ||
            !TryGetWccDefaultDependency(owner, "share_maps", out var maps)) return null;
        if (view is null && maps is null) return "all";
        return maps is null or "yes" or "true" ? "shroud" : maps is "no" or "false" ? "none" : null;
    }

    private string? GetWccDefaultTeamName(WmlStaticScalarOwner owner)
    {
        // Singleplayer connect_engine assigns team IDs from ordered side_count, not an authored side number.
        if (_staticScenarioOwner is not { OpeningOutputOffset: not null, HasUnresolvedContributions: false } scenario)
            return null;
        var sides = scenario.DirectChildren.Where(child => child.TagName == "side").ToArray();
        if (sides.Any(side => side.OpeningOutputOffset is null)) return null;
        var index = Array.FindIndex(sides, side => ReferenceEquals(side, owner));
        return index < 0 ? null : (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    private WmlStaticScalarOwner? GetWccDefaultFirstNestedLeader(WmlStaticScalarOwner owner)
    {
        if (owner is not { OpeningOutputOffset: not null, HasUnresolvedContributions: false }) return null;
        var first = owner.DirectChildren.FirstOrDefault(child => child.TagName == "leader");
        return first?.OpeningOutputOffset is not null ? first : null;
    }

    private string? GetWccDefaultSideName(WmlStaticScalarOwner owner)
    {
        if (!TryGetWccDefaultDependency(owner, "type", out var type) ||
            !TryGetWccDefaultDependency(owner, "no_leader", out var noLeader) ||
            noLeader is not (null or "yes" or "true" or "no" or "false")) return null;
        var inlineActive = !string.IsNullOrEmpty(type) && type != "null" && noLeader is not ("yes" or "true");
        if (!TryGetWccDefaultDependency(owner, "name", out var name)) return null;
        if (name is not null || inlineActive) return name;
        var first = GetWccDefaultFirstNestedLeader(owner);
        return first is not null && TryGetWccDefaultDependency(first, "name", out var nestedName) ? nestedName : null;
    }

    private bool TryGetWccDefaultDependency(WmlStaticScalarOwner owner, string key, out string? value)
    {
        value = null;
        if (!IsCurrentWccInitialFieldOwner(owner)) return false;
        if (FindWccInlineDraft(GetWccInitialFieldRoute(owner, key)) is { } draft)
        {
            if (draft.Operation == WmlFieldOperation.Remove || draft.ValidationError is not null ||
                draft.ValueInputKind != WccScenarioFieldValueInputKind.Literal) return false;
            var definition = WccInitialFieldDefinition(draft.Route);
            value = definition?.Kind is ScenarioInitialFieldKind.Text or ScenarioInitialFieldKind.TranslatableText
                or ScenarioInitialFieldKind.Multiline
                ? draft.Text
                : draft.Text?.Trim();
            return true;
        }

        var resolution = owner.ResolveField(key);
        if (resolution.State == WmlStaticScalarResolutionState.ProvenAbsent) return true;
        if (resolution.State != WmlStaticScalarResolutionState.Known || resolution.ValueText is null ||
            WccScenarioFieldValueCodec.InferInputKind(resolution.ValueText) != WccScenarioFieldValueInputKind.Literal)
            return false;
        value = WccScenarioFieldValueCodec.DecodeLiteral(resolution.ValueText);
        return true;
    }
}