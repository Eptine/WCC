using Wcc.Core.Campaigns;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateCampaignCreditsBinding(WccCampaignCreditsEdit edit,
        WccScenarioFieldEditSessionState state, WmlStaticScalarOwner anchor, WmlCampaignEvaluationPlan? plan)
    {
        if (state.ReviewScope != WccScenarioFieldReviewScope.CampaignMenu || plan is null
            || !ReferenceEquals(edit.Source, state.Source) || !IsCampaignMenuSource(state, plan, anchor))
            throw new InvalidOperationException("Credits require their exact captured main-file campaign menu session and complete declared gameplay contexts.");
        ValidateCampaignCreditsOwner(edit.Snapshot, anchor);
        _ = WccCampaignCreditsPlanner.BuildEdit(edit.Snapshot, edit.GroupRows);
    }

    private static void ValidateCampaignCreditsOwner(WccCampaignCreditsSnapshot snapshot, WmlStaticScalarOwner owner)
    {
        var fresh = WccCampaignCreditsPlanner.Read(snapshot.Source, owner);
        if (!fresh.CanEdit || fresh.CampaignRange != snapshot.CampaignRange
            || !WccCampaignCreditsPlanner.EqualRows(fresh.GroupRows, snapshot.GroupRows))
            throw new InvalidOperationException(fresh.CanEdit ? "Credits no longer match their exact captured local source shape." : fresh.Diagnostic);
    }

    private static void ValidateCandidateCampaignCredits(WccProjectComposedFile file, WmlStaticScalarOwner owner)
    {
        var edit = file.Request.CampaignCredits!;
        var current = WccCampaignCreditsPlanner.Read(file.Candidate.CandidateSource, owner);
        if (!current.CanEdit || !WccCampaignCreditsPlanner.SamePresentation(current.GroupRows, edit.GroupRows))
            throw new InvalidOperationException(current.CanEdit ? "The candidate campaign does not reproduce all intended credits groups and entries." : current.Diagnostic);
    }
}
