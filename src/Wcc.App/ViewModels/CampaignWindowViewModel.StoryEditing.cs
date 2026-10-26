using System;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>Existing exact MENU evidence for Story editing or preview; fresh Save supplies write authority.</summary>
internal sealed record WccScenarioStoryMenuBinding(WmlStaticSource Source, WmlCampaignEvaluationPlan Plan,
    WccLibrarySnapshot Snapshot, WccPackageRevisionId RootRevision, WccEditorSessionContext Host,
    string? ProfileIdentity, Func<bool> IsCurrent)
{
    public bool CanWrite { get; init; }
    public string? DestinationTextDomain { get; init; }
}

public partial class CampaignWindowViewModel
{
    internal WccScenarioStoryMenuBinding? CaptureWccStoryMenuBinding(out string? problem)
    {
        lock (_campaignFieldGate)
        {
            problem = null;
            if (_publishingCampaignProjection || _campaignProjectionBinding is not { } binding ||
                !IsCurrentCampaignBinding(binding) || _campaignProjection is not
                { IsComplete: true, Source: { } source } projection || _campaignMenuOwner is null ||
                _campaignFieldConfiguration is not { Snapshot: { } snapshot, RootRevision: { } root } configuration ||
                _campaignHasRawSourceChanges ||
                IsCampaignFieldSaveBusy || _campaignFieldSession.IsBusy || _collectedSaveRecoveryPending || _campaignFieldSession.Recovery is not null)
            { problem = "Story preview needs the current captured campaign menu and project source."; return null; }
            if (GetCampaignDraftBindingProblem() is { } stale)
            { problem = stale; return null; }
            if (!IsPathInsideCampaignRoot(source.Path) || !IsCurrentCampaignMenuSource(source) ||
                (projection.EvaluationEntrySource ?? source).Text != GetCurrentMainSourceRawWml())
            { problem = "The captured campaign main no longer matches the current project source."; return null; }
            var selectedEntry = _selectedCampaignEntry;
            var plan = RequiresSelectedCampaignAdapter && selectedEntry?.IsContextComplete == true ? selectedEntry.Plan : null;
            if (plan is null && (!CampaignInitialFieldProjector.TryGetCapturedContextPlan(projection, EffectiveCampaignPlan,
                    out plan, out problem) || plan is not { IsContextDiscoveryComplete: true })) return null;
            var readOnly = IsReadOnly;
            bool Current()
            {
                lock (_campaignFieldGate)
                    return IsCurrentCampaignBinding(binding) && ReferenceEquals(projection, _campaignProjection) &&
                        ReferenceEquals(configuration, _campaignFieldConfiguration) && IsReadOnly == readOnly &&
                        !_campaignHasRawSourceChanges && !_collectedSaveRecoveryPending &&
                        _campaignFieldSession.Recovery is null && GetCampaignDraftBindingProblem() is null &&
                        ReferenceEquals(selectedEntry, _selectedCampaignEntry) &&
                        IsCurrentCampaignMenuSource(source) && (projection.EvaluationEntrySource ?? source).Text == GetCurrentMainSourceRawWml();
            }
            var domainOpening = _campaignMenuOwner.OpeningOrigin;
            return new(projection.EvaluationEntrySource ?? source, plan, snapshot, root, configuration.Session, configuration.RuntimeIdentity, Current)
            {
                CanWrite = !readOnly && (!RequiresSelectedCampaignAdapter || HasSelectedCampaignEntryBinding) && configuration.Session.CanWrite,
                DestinationTextDomain = domainOpening is not null && SamePresentationSource(domainOpening.Source, source)
                    ? WmlTextDomainCapture.BeforeOffset(source.Text, domainOpening.StartOffset) : null
            };
        }
    }
}
