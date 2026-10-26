using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Binds a request to one campaign entry in its real package. The menu selection
/// is an expected captured identity, not persistence authority; preparation authenticates it again.
/// The ordinary collected workflow checks supported recipes in this campaign's declared contexts.
/// </summary>
public sealed class WccCampaignEntryReviewIntent
{
    public WccCampaignEntryReviewIntent(WmlStaticSource campaignMainSource, string selectedEntryIdentity)
    {
        CampaignMainSource = campaignMainSource ?? throw new ArgumentNullException(nameof(campaignMainSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedEntryIdentity);
        SelectedEntryIdentity = selectedEntryIdentity;
    }
    public WmlStaticSource CampaignMainSource { get; }
    public string SelectedEntryIdentity { get; }
    public bool AuthorizesPersistence => false;
}
