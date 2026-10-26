using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public static class CampaignTextDomainPatcher
{
    public static void Apply(CampaignTextDomain textDomain, WmlEditableBlock block)
    {
        Set(block, textDomain.FieldStates, "name", textDomain.Name);
        Set(block, textDomain.FieldStates, "path", textDomain.Path);
    }

    private static void Set(WmlEditableBlock b, IReadOnlyDictionary<string, WmlFieldSafetyState> states, string key, string? value)
    {
        if (value is null) return;
        if (!WmlFieldSafetyWriteGuard.CanWrite(states, key)) return;
        b.SetKnownValue(key, value);
    }
}