using System;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private bool IsCurrentCampaignFieldOwner(WmlStaticScalarOwner owner, string key) =>
        ReferenceEquals(owner, _campaignMenuOwner) || key == "image" && owner.TagName == "difficulty" &&
        ReferenceEquals(owner.Parent, _campaignMenuOwner) && _campaignMenuOwner!.DirectChildren.Contains(owner);

    private static bool TryGetCampaignFieldDefinition(WmlStaticScalarOwner owner, string key,
        out ScenarioInitialFieldDefinition? definition) =>
        WccScenarioFieldValueCodec.TryGetDefinition(owner.TagName, key, out definition);

    private WmlStaticScalarOwner? CampaignDifficultyImageOwner(CampaignDifficultyDisplayRow row)
    {
        if (!IsCurrentCampaignDifficultyDisplayRow(row) || _campaignMenuOwner is not { } menu) return null;
        var matches = menu.DirectChildren.Where(child => child.TagName == "difficulty" &&
            WmlResourceValueTextDecoder.TryDecode(child.GetKnownValue("define"), out var define, out _) &&
            define == row.Define).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal bool CanEditCampaignDifficultyImage(CampaignDifficultyDisplayRow row)
    {
        lock (_campaignFieldGate) return !_publishingCampaignStructure && !_changingCampaignStructure &&
            CampaignDifficultyImageOwner(row) is { } owner && CanReviewCampaignField(owner, "image");
    }

    internal string CampaignDifficultyImageEditStatus(CampaignDifficultyDisplayRow row)
    {
        lock (_campaignFieldGate) return CampaignDifficultyImageOwner(row) is { } owner
            ? GetCampaignFieldEditStatus(owner, "image")
            : "This difficulty icon has no unique captured modern difficulty owner. Its source remains available through View or Edit source.";
    }

    internal string CampaignDifficultyImageValue(CampaignDifficultyDisplayRow row, string fallback)
    {
        lock (_campaignFieldGate)
            return CampaignDifficultyImageOwner(row) is { } owner &&
                FindCampaignFieldDraft(GetCampaignFieldRoute(owner, "image")) is { Operation: not WmlFieldOperation.Remove } draft
                    ? draft.Text ?? "" : fallback;
    }

    internal void SetCampaignDifficultyImageValue(CampaignDifficultyDisplayRow row, string value)
    {
        lock (_campaignFieldGate)
            if (CanEditCampaignDifficultyImage(row) && CampaignDifficultyImageOwner(row) is { } owner)
                SetCampaignFieldValue(owner, "image", value, WccScenarioFieldValueCodec.InferInputKind(value));
    }
}
