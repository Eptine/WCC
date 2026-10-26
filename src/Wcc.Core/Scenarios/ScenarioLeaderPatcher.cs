using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static class ScenarioLeaderPatcher
{
    public static void Apply(ScenarioLeader leader, WmlEditableBlock block)
    {
        Set(block, leader, "type", leader.Type);
        Set(block, leader, "id", leader.Id);
        Set(block, leader, "name", leader.NameWml);
        Set(block, leader, "gender", leader.Gender);
        Set(block, leader, "variation", leader.Variation);
        Set(block, leader, "canrecruit", leader.CanRecruit);
        ApplyCoordinates(block, leader);
        Set(block, leader, "facing", leader.Facing);
        Set(block, leader, "unrenamable", leader.Unrenamable);
        Set(block, leader, "random_traits", leader.RandomTraits);
        Set(block, leader, "random_gender", leader.RandomGender);
        Set(block, leader, "placement", leader.Placement);
        Set(block, leader, "location_id", leader.LocationId);
        Set(block, leader, "extra_recruit", leader.ExtraRecruit);
    }

    private static void ApplyCoordinates(WmlEditableBlock block, ScenarioLeader leader)
    {
        if (leader.CoordinatesAreAmbiguous)
            return;
        if (!leader.UsesCombinedCoordinates)
        {
            SetInt(block, leader, "x", leader.X);
            SetInt(block, leader, "y", leader.Y);
            return;
        }

        var removesX = leader.FieldsToRemove.Contains("x");
        var removesY = leader.FieldsToRemove.Contains("y");
        if (!removesX && !removesY && leader.X is not null && leader.Y is not null)
        {
            Set(
                block,
                leader,
                "x,y",
                $"{leader.X.Value.ToString(CultureInfo.InvariantCulture)},{leader.Y.Value.ToString(CultureInfo.InvariantCulture)}");
            return;
        }

        if (!WmlFieldSafetyWriteGuard.CanWrite(leader.FieldStates, "x") ||
            !WmlFieldSafetyWriteGuard.CanWrite(leader.FieldStates, "y"))
        {
            return;
        }

        block.RemoveKnownValue("x,y");
        if (!removesX && leader.X is not null)
            block.SetKnownValue("x", leader.X.Value.ToString(CultureInfo.InvariantCulture));
        if (!removesY && leader.Y is not null)
            block.SetKnownValue("y", leader.Y.Value.ToString(CultureInfo.InvariantCulture));
        leader.UsesCombinedCoordinates = false;
    }

    private static void Set(
        WmlEditableBlock block,
        ScenarioLeader leader,
        string key,
        string? value)
    {
        if (!WmlFieldSafetyWriteGuard.CanWrite(leader.FieldStates, key))
            return;
        if (leader.FieldsToRemove.Contains(key))
        {
            block.RemoveKnownValue(key);
            return;
        }
        if (value is not null)
            block.SetKnownValue(key, value);
    }

    private static void SetInt(
        WmlEditableBlock block,
        ScenarioLeader leader,
        string key,
        int? value)
        => Set(
            block,
            leader,
            key,
            value?.ToString(CultureInfo.InvariantCulture));
}
