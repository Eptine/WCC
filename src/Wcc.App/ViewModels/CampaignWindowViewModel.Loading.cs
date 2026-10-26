namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    // Optional host fence for an asynchronous source-opening operation. It is consulted
    // before parsing and after callbacks, without granting persistence authority.
    public System.Func<bool>? CampaignSourcePublicationGuard { get; set; }
    private sealed class CampaignSourceLoadingActivity { }
    private CampaignSourceLoadingActivity? _campaignSourceLoadingActivity;

    // This describes source discovery only; it grants no editing or persistence authority.
    public bool IsCampaignSourceLoading => _campaignSourceLoadingActivity is not null;
    public string? CampaignSourceLoadingStatus => IsCampaignSourceLoading
        ? "Discovering and parsing campaign files..." : null;

    private void BeginCampaignSourceLoading(CampaignSourceLoadingActivity activity)
    {
        _campaignSourceLoadingActivity = activity;
        RaiseCampaignSourceLoadingChanged();
    }

    private void EndCampaignSourceLoading(CampaignSourceLoadingActivity activity)
    {
        if (!ReferenceEquals(activity, _campaignSourceLoadingActivity)) return;
        _campaignSourceLoadingActivity = null;
        RaiseCampaignSourceLoadingChanged();
    }

    private void RaiseCampaignSourceLoadingChanged()
    {
        OnPropertyChanged(nameof(IsCampaignSourceLoading));
        OnPropertyChanged(nameof(CampaignSourceLoadingStatus));
        OnPropertyChanged(nameof(CanSelectCampaignEntry));
        RaiseCampaignPackageAuthoringChanged();
    }
}
