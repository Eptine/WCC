using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Library;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

internal sealed record CampaignUnitTypeUpdateBinding(WccScenarioFieldEditSessionState State,
    string TextDomain, IReadOnlyList<WmlEvaluationContext> Contexts, Func<bool> IsCurrent);

public partial class CampaignWindowViewModel
{
    internal CampaignUnitTypeUpdateBinding? CaptureCampaignUnitTypeUpdateBinding(out string? problem)
    {
        lock (_campaignFieldGate)
        {
            problem = _campaignMenuOwner is null ? "Wait for the captured campaign menu before saving this unit."
                : GetCampaignFieldBlockReason(GetCampaignFieldRoute(_campaignMenuOwner, "rank"));
            if (problem is not null) return null;
            if (HasCampaignSourceDrafts || _collectedSaveRecoveryPending)
            { problem = "Resolve pending campaign source changes before saving this unit."; return null; }
            if (_campaignFieldConfiguration is not { Snapshot: { } snapshot, RootRevision: { } revision } configuration ||
                _campaignProjection?.Source is not { } source || EffectiveCampaignPlan is not { } plan ||
                _project is null || _project.Campaign is null && !HasSelectedCampaignEntryBinding)
            { problem = "The unit Save needs the campaign's exact captured source and declared difficulties."; return null; }
            var binding = new CampaignDraftBinding(_project, _project.Campaign, configuration, _campaignFieldGeneration);
            var state = new WccScenarioFieldEditSessionState(configuration.ProjectIdentity!, configuration.Session.RootPath!,
                revision.Value.ToString(), true, snapshot, revision, source, plan, _campaignProjection.Context, _campaignMenuOwner!,
                configuration.AddonsRoot, configuration.RuntimeIdentity) { ReviewScope = WccScenarioFieldReviewScope.CampaignMenu, EntryReview = CurrentCampaignEntryReview };
            bool Current()
            {
                lock (_campaignFieldGate)
                    return IsCurrentCampaignBinding(binding) && !IsReadOnly && configuration.Session.CanWrite &&
                        !_campaignHasRawSourceChanges && !HasCampaignSourceDrafts && !_collectedSaveRecoveryPending &&
                        ReferenceEquals(plan, EffectiveCampaignPlan) && ReferenceEquals(source, _campaignProjection?.Source) &&
                        IsCurrentCampaignMenuSource(source);
            }
            var opening = _campaignMenuOwner?.OpeningOrigin;
            var domain = opening is not null &&
                string.Equals(opening.Source.Path, source.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                opening.Source.ContentHash == source.ContentHash
                    ? WmlTextDomainCapture.BeforeOffset(source.Text, opening.StartOffset) : null;
            if (WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is { } domainProblem)
            { problem = "The campaign needs its explicit authoring textdomain before copying unit dependencies: " + domainProblem; return null; }
            return new(state, domain!, plan.Contexts, Current);
        }
    }

    internal async Task<WccProjectSourceEditResult?> SaveCampaignUnitTypeUpdateAsync(
        WccProjectSourceEditCoordinator coordinator, CampaignUnitTypeUpdateBinding binding,
        WccUnitTypeUpdateEdit edit, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        if (!binding.IsCurrent() || !isCurrent())
        { Status = "The unit or campaign context changed before checking. Your edits remain open."; return null; }
        coordinator.SetSession([binding.State]);
        void Invalidate() => coordinator.InvalidateSession();
        CollectedSaveAuthorityChanging += Invalidate;
        try
        {
            lock (_campaignFieldGate)
            {
                _campaignFieldSession.Invalidate();
                _campaignSaveBusy = true;
                _campaignFieldStatus = Status = "Checking the unit and its dependencies in every declared context…";
                RaiseCampaignFieldsChanged();
            }
            if (!binding.IsCurrent() || !isCurrent()) return null;
            var request = new WccProjectSourceEditRequest(binding.State.Source) { UnitTypeUpdate = edit };
            var prepared = await Task.Run(async () => await coordinator.PrepareAsync([request], cancellationToken), cancellationToken);
            if (!prepared.IsPrepared || prepared.Ticket is null) return prepared;
            if (!binding.IsCurrent() || !isCurrent())
            { Status = "The unit changed during checking. Your edits remain open."; return null; }
            lock (_campaignFieldGate)
            {
                _campaignFieldStatus = Status = "Writing the checked unit and shared dependencies…";
                RaiseCampaignFieldsChanged();
            }
            var saved = await Task.Run(async () => await coordinator.CommitAsync(prepared.Ticket, cancellationToken), cancellationToken);
            if (saved.Status == WccProjectSourceEditStatus.RecoveryRequired) BlockCollectedSaveForRecovery();
            return saved;
        }
        finally
        {
            CollectedSaveAuthorityChanging -= Invalidate;
            coordinator.InvalidateSession();
            lock (_campaignFieldGate)
            {
                _campaignSaveBusy = false;
                RaiseCampaignFieldsChanged();
            }
        }
    }
}
