using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static class ScenarioSidePatcher
{
    public static void Apply(ScenarioSide side, WmlEditableBlock block)
    {
        SetInt(block, side, "side", side.SideNumber);
        SetInt(block, side, "gold", side.Gold);
        SetInt(block, side, "income", side.Income);
        SetInt(block, side, "village_gold", side.VillageGold);
        SetInt(block, side, "village_support", side.VillageSupport);
        SetInt(block, side, "recall_cost", side.RecallCost);

        Set(block, side, "controller", side.Controller);
        Set(block, side, "team_name", side.TeamName);
        Set(block, side, "user_team_name", side.UserTeamName);
        Set(block, side, "side_name", side.SideNameWml);
        Set(block, side, "color", side.Color);
        Set(block, side, "recruit", side.Recruit);
        Set(block, side, "persistent", side.Persistent);
        Set(block, side, "save_id", side.SaveId);
        Set(block, side, "previous_save_id", side.PreviousSaveId);
        Set(block, side, "fog", side.Fog);
        Set(block, side, "shroud", side.Shroud);
        Set(block, side, "share_vision", side.ShareVision);
        Set(block, side, "hidden", side.Hidden);
        Set(block, side, "scroll_to_leader", side.ScrollToLeader);
        Set(block, side, "suppress_end_turn_confirmation", side.SuppressEndTurnConfirmation);
        Set(block, side, "defeat_condition", side.DefeatCondition);
        Set(block, side, "no_leader", side.NoLeader);
        Set(block, side, "recruitment_pattern", side.RecruitmentPattern);

        var legacy = side.LegacyLeader;
        if (legacy is not null)
        {
            ScenarioLeaderPatcher.Apply(legacy, block);
        }
    }

    private static void Set(WmlEditableBlock b, ScenarioSide side, string key, string? value)
    {
        if (!WmlFieldSafetyWriteGuard.CanWrite(side.FieldStates, key)) return;
        if (side.FieldsToRemove.Contains(key))
        {
            b.RemoveKnownValue(key);
            return;
        }
        if (value is not null)
            b.SetKnownValue(key, value);
    }

    private static void SetInt(WmlEditableBlock b, ScenarioSide side, string key, int? value)
        => Set(b, side, key, value?.ToString(CultureInfo.InvariantCulture));
}
