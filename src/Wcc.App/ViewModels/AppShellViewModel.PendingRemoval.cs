using System;
using System.Collections.Generic;
using System.Linq;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    public IReadOnlyList<PendingEditorChangeRemovalVm> PendingChanges
    {
        get
        {
            var identity = CapturePendingEditorIdentity();
            var stamp = CapturePendingEditorStamp();
            var proposals = CampaignEditor.CapturePendingChangeRemovals()
                .Concat(ScenarioEditor.CapturePendingChangeRemovals()).ToList();
            if (_packageScenarioRawDraft is { } packageRaw)
                proposals.Add(new(new("Scenario", "Package RAW source", packageRaw.Draft.SourcePath),
                    () => ReferenceEquals(packageRaw, _packageScenarioRawDraft) ? null : "The pending RAW text changed. Select its current row.",
                    DiscardPackageScenarioRawDraft));
            if (_campaignEditorDirty || CampaignEditor.HasCampaignSourceChanges)
                Applied("Campaign", "Main source and structured changes", CampaignEditor.LoadedPath ?? "");
            if (_scenarioEditorDirty) Applied("Scenario", "Source and structured changes", ScenarioEditor.LoadedPath ?? "");
            if (_unitEditorDirty) Applied("Unit", "Source and structured changes", UnitEditor.LoadedPath ?? "");
            if (_libraryUnitEditorDirty) Applied("Library unit", "Source and structured changes", LibraryUnitEditor.LoadedPath ?? "");
            return proposals.Select(proposal =>
            {
                string? Problem() => PendingRowRemovalProblem(identity, stamp) ?? proposal.Problem();
                return new PendingEditorChangeRemovalVm(proposal.Change, Problem, () =>
                {
                    if (Problem() is { } problem) { Status = problem; RefreshPendingEditorChanges(); return false; }
                    if (!proposal.Remove())
                    { Status = "The pending change could not be removed. Current edits stay open."; RefreshPendingEditorChanges(); return false; }
                    Status = "Removed the selected pending change. Saved files were not changed.";
                    SetGlobalSaveError(false);
                    RefreshPendingEditorChanges();
                    return true;
                });
            }).ToArray();

            void Applied(string scope, string label, string detail) => proposals.Add(new(new(scope, label, detail),
                () => "Applied source edits form one saved-baseline change. Use the editor's reset or the global Discard action to restore them together.",
                () => false));
        }
    }

    private string? PendingRowRemovalProblem(PendingEditorIdentity identity, string stamp)
    {
        if (IsGlobalSaveBusy || _globalSaveTask is { IsCompleted: false } || IsPendingNavigationDecision ||
            _isDiscardingPendingEditorChanges || _collectedSaveCoordinator.Recovery is not null)
            return "Finish the current Save, navigation decision or source recovery before removing pending changes.";
        if (!identity.IsCurrent(this) || stamp != CapturePendingEditorStamp())
            return "The source, difficulty or pending edits changed. Select the current pending row before removing it.";
        return null;
    }
}
