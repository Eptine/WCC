using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public static class CampaignPatcher
{
    public static void Apply(CampaignDefinition campaign, WmlEditableBlock block)
    {
        Set(block, campaign.FieldStates, "id", campaign.Id);
        Set(block, campaign.FieldStates, "name", campaign.NameWml);
        Set(block, campaign.FieldStates, "abbrev", campaign.AbbrevWml);
        Set(block, campaign.FieldStates, "define", campaign.Define);
        Set(block, campaign.FieldStates, "first_scenario", campaign.FirstScenario);
        Set(block, campaign.FieldStates, "icon", campaign.Icon);
        Set(block, campaign.FieldStates, "image", campaign.Image);
        Set(block, campaign.FieldStates, "background", campaign.Background);
        Set(block, campaign.FieldStates, "description", campaign.DescriptionWml);
        SetInt(block, campaign.FieldStates, "rank", campaign.Rank);
        Set(block, campaign.FieldStates, "year", campaign.Year);
    }

    private static void Set(WmlEditableBlock b, IReadOnlyDictionary<string, WmlFieldSafetyState> states, string key, string? value)
    {
        if (value is null) return;
        if (!WmlFieldSafetyWriteGuard.CanWrite(states, key)) return;
        b.SetKnownValue(key, value);
    }

    private static void SetInt(WmlEditableBlock b, IReadOnlyDictionary<string, WmlFieldSafetyState> states, string key, int? value)
    {
        if (value is null) return;
        if (!WmlFieldSafetyWriteGuard.CanWrite(states, key)) return;
        b.SetKnownValue(key, value.Value.ToString(CultureInfo.InvariantCulture));
    }
}