using System.Linq;
using Wcc.App.ViewModels;
using Wcc.Core.Projects;
using Wcc.Core.Campaigns;

namespace Wcc.App.ViewModels.Wcc;

public sealed class ProjectScenarioFileVm : ViewModelBase
{
    public ProjectScenarioFileVm(ScenarioFileAsset asset)
    {
        Asset = asset;
    }

    public ScenarioFileAsset Asset { get; }

    // A row is a view of one reached owner, not a second copy of its source.
    public WccCampaignEntry? CampaignEntry { get; init; }
    public WccCampaignScenarioMembership? Membership { get; init; }
    // Unselected entries keep only reached navigation metadata. Full owners
    // belong to the selected entry and are never needed to label a sidebar row.
    public WccCampaignScenarioNavigationMembership? Navigation { get; init; }
    public string? ScenarioId => Membership?.ScenarioId ?? Navigation?.ScenarioId;
    public string? MembershipIdentity => Membership?.Identity ?? Navigation?.Identity ?? PendingMembershipIdentity;
    public string? ScenarioDocumentInternalId { get; init; }
    public string? CampaignGroupName { get; init; }
    public bool ShowCampaignGroupHeading { get; init; }
    public bool IsUnresolvedCampaignMembership { get; init; }
    // After a selected-campaign Save, other entries keep navigation labels only.
    // Their old membership is never reused as display or editing authority.
    internal bool NeedsCampaignMembershipRefresh { get; init; }
    internal string? PendingDisplayName { get; init; }
    internal string? PendingMembershipIdentity { get; init; }
    // Captured authored ordinal, also kept on pending labels after a checked
    // Save replaces Asset.Documents and their IDs before rows can be refreshed.
    internal int? PendingScenarioDocumentIndex { get; init; }

    public string InternalId => Asset.InternalId;

    public string DisplayName
    {
        get
        {
            if (NeedsCampaignMembershipRefresh && PendingDisplayName is { } pending) return pending;
            if (Membership is { } membership)
                return $"{membership.ScenarioId ?? membership.Name}  ({Asset.FileName})";
            if (Navigation is { } navigation)
                return $"{navigation.ScenarioId ?? navigation.Name}  ({Asset.FileName})";
            var firstScenarioId = Asset.Documents.FirstOrDefault()?.Scenario.Id;
            if (!string.IsNullOrWhiteSpace(firstScenarioId))
                return $"{firstScenarioId}  ({Asset.FileName})";

            return Asset.FileName;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayName));
    }
}