using System;
using System.Collections.Generic;
using System.Linq;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    internal IReadOnlyList<PendingChangeRemovalProposal> CapturePendingChangeRemovals()
    {
        lock (_campaignFieldGate)
        {
            var proposals = new List<PendingChangeRemovalProposal>();
            foreach (var draft in _campaignDrafts.ToArray())
            {
                // The first-scenario assignment and its new files are one creation proposal.
                if (ReferenceEquals(draft, _campaignScenarioFirstDraft)) continue;
                proposals.Add(new(new("Campaign", draft.Label, AppShellViewModel.DraftDetail(draft)),
                    () => CampaignPendingRemovalProblem() ?? (!_campaignDrafts.Any(current => ReferenceEquals(current, draft))
                        ? "This campaign field draft changed. Select its current pending row." : null),
                    () =>
                    {
                        lock (_campaignFieldGate)
                        {
                            if (CampaignPendingRemovalProblem() is not null || !_campaignDrafts.Any(current => ReferenceEquals(current, draft))) return false;
                            foreach (var row in _campaignFields.Where(row => ReferenceEquals(FindCampaignFieldDraft(GetCampaignFieldRoute(row.Owner, row.Key)), draft)))
                                row.ResetInputState();
                            _campaignDrafts.Remove(draft);
                            _campaignReviewSummary = null;
                            FinishCampaignDraftMutation();
                            return true;
                        }
                    }));
            }
            if (HasCampaignStructureDrafts)
                Group("Difficulty and path changes", CampaignStructurePendingChanges, ResetCampaignStructureDrafts,
                    () => HasCampaignStructureDrafts);
            if (HasCampaignCreditsDrafts)
                Group("Credits changes", CampaignCreditsPendingChanges, ResetCampaignCreditsDrafts,
                    () => HasCampaignCreditsDrafts);
            foreach (var preset in _campaignPresetDrafts.ToArray())
                proposals.Add(new(new("Campaign", "New " + preset.Payload.Role + " preset", preset.Name + " → " + CampaignPresetStoragePath),
                    () => CampaignPendingRemovalProblem() ?? (!_campaignPresetDrafts.Any(current => ReferenceEquals(current, preset))
                        ? "This preset proposal changed. Select its current pending row." : null),
                    () => { RevertCampaignPreset(preset.Id); return !_campaignPresetDrafts.Any(current => ReferenceEquals(current, preset)); }));
            foreach (var migration in CampaignPresetMigrationPendingChanges)
                proposals.Add(new(migration,
                    () => CampaignPendingRemovalProblem() ?? (!CanRevertCampaignPresetMigration
                        ? "This helper-file move belongs to the pending new presets. Remove those presets first to cancel the move." : null),
                    () => { RevertCampaignPresetMigration(); return !_campaignPresetMigrationPending; }));
            foreach (var mutation in _campaignPresetMutations.ToArray())
                proposals.Add(new(new("Campaign", (mutation.IsDeletion ? "Delete " : "Edit ") + mutation.Target.Role + " preset",
                    mutation.Target.Name + " -> " + mutation.Target.SourcePath),
                    () => CampaignPendingRemovalProblem() ?? (!_campaignPresetMutations.Any(current => ReferenceEquals(current, mutation))
                        ? "This shared preset proposal changed. Select its current pending row." : null),
                    () => { RevertCampaignPreset(mutation.Target.Id); return !_campaignPresetMutations.Any(current => ReferenceEquals(current, mutation)); }));
            if (_campaignScenarioCreationDraft is { } scenario)
                Group("New scenario", CampaignScenarioCreationPendingChanges,
                    RevertCampaignScenarioCreation, () => ReferenceEquals(scenario, _campaignScenarioCreationDraft));
            if (_campaignUnitTypeCreationDraft is { } unit)
                Group("New unit type", CampaignUnitTypeCreationPendingChanges,
                    RevertCampaignUnitTypeCreation, () => ReferenceEquals(unit, _campaignUnitTypeCreationDraft));
            if (_campaignDependencyBatch is not null) proposals.AddRange(CaptureDependencyPendingRemovals());
            else if (HasCampaignPackageEditDraft)
                Group("Package structure", CampaignPackagePendingChanges, RevertCampaignPackageEdit, () => HasCampaignPackageEditDraft);
            return proposals;

            void Group(string label, IReadOnlyList<PendingEditorChange> changes, Action reset, Func<bool> exists)
            {
                var detail = string.Join(Environment.NewLine, changes.Select(change => change.Summary));
                proposals.Add(new(new("Campaign", label, detail),
                    () => CampaignPendingRemovalProblem() ?? (!exists() ? "This grouped proposal changed. Select its current pending row." : null),
                    () => { reset(); return !exists(); }));
            }
        }
    }

    private string? CampaignPendingRemovalProblem()
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignDependencySourceRefreshing) return "Wait for the dependency source refresh before removing pending changes.";
            if (IsCampaignFieldSaveBusy || _campaignFieldSession.IsBusy || _campaignFieldSession.Recovery is not null ||
                _collectedSaveRecoveryPending)
                return "Finish campaign Save or source recovery before removing a pending change.";
            if (IsSynchronizingUi || _publishingCampaignProjection || _publishingCampaignStructure || _changingCampaignStructure ||
                _publishingCampaignCredits || _changingCampaignCredits || _changingCampaignPresets ||
                _changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation)
                return "Finish the current campaign update before removing a pending change.";
            return HasCampaignSourceDrafts && _campaignDraftBinding is null
                ? "These campaign proposals have no captured source binding. Use global Discard to reload their saved baseline."
                : GetCampaignDraftBindingProblem();
        }
    }
}
