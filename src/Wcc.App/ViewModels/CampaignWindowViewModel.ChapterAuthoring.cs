using System;
using System.Linq;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public sealed class CampaignChapterAddDraft
{
    internal CampaignChapterAddDraft(CampaignWindowViewModel owner, WccProject project,
        long generation, WccCampaignChapterAuthoringSnapshot snapshot)
    { Owner = owner; Project = project; Generation = generation; Snapshot = snapshot; }
    internal CampaignWindowViewModel Owner { get; }
    internal WccProject Project { get; }
    internal long Generation { get; }
    internal WccCampaignChapterAuthoringSnapshot Snapshot { get; }
    internal string? InstallPath { get; init; }
}

public partial class CampaignWindowViewModel
{
    // A surviving managed chapter can be the package's only campaign. Its
    // scenario include still defines navigation; retained root/scenarios files
    // are inventory, not proof that they belong to that selected chapter.
    internal bool RequiresManagedChapterScenarioNavigation =>
        SelectedCampaignEntry?.Plan?.CampaignDefine is { } define &&
        ReadCampaignChapterAuthoringSnapshot() is { CanEdit: true } snapshot &&
        snapshot.Chapters.SingleOrDefault(chapter => chapter.CampaignDefine == define) is { } chapter &&
        (CampaignPackageAuthoringMetadata?.LayoutVersion == 2 || !chapter.IsOriginal);

    public bool CanAddCampaignChapter => CampaignPackageAuthoringMetadata is { LayoutVersion: 2 } &&
        !IsReadOnly && CanSelectCampaignEntry && !HasCampaignPackageEditDraft;
    public bool CanRemoveCampaignChapter => CampaignPackageAuthoringMetadata is not null && !IsReadOnly &&
        CanSelectCampaignEntry && !HasCampaignPackageEditDraft && CampaignEntries.Count > 1 && SelectedCampaignEntry is not null;
    public string CampaignChapterGuidance => CampaignPackageAuthoringProblem ?? (CampaignPackageAuthoringMetadata is null
        ? "Imported campaign entries can be selected and edited. Add or remove chapters through RAW WML."
        : CampaignPackageAuthoringMetadata.LayoutVersion != 2
            ? "This package uses the earlier WCC folder layout. Its existing chapters remain editable; adding chapters needs the layout used by newly created packages. Use RAW WML to change an older layout."
        : HasCampaignPackageEditDraft ? "Save or discard the pending package change before changing chapters."
        : "Chapters share package units and helpers. Removing a chapter cleans up its proven owned, unshared files. Shared units and helpers remain.");
    public string CampaignChapterRemovalGuidance => CampaignPackageAuthoringProblem ??
        (CampaignPackageAuthoringMetadata is null ? CampaignChapterGuidance : CampaignEntries.Count <= 1
            ? "Keep at least one campaign or chapter in the package." : "Remove the selected chapter and its proven owned, unshared files.");

    public CampaignChapterAddDraft? BeginCampaignChapterAdd()
    {
        lock (_campaignFieldGate)
        {
            if (!CanAddCampaignChapter || CampaignPackageAuthoringProblem is { }) return null;
            var snapshot = ReadCampaignChapterAuthoringSnapshot();
            if (snapshot is not { CanEdit: true }) { Status = snapshot?.Diagnostic ?? "The package source is not ready."; return null; }
            return new(this, _project!, _campaignFieldGeneration, snapshot) { InstallPath = _campaignPresetInstallPath };
        }
    }
    public bool ApplyCampaignChapterAdd(CampaignChapterAddDraft draft, WccCampaignChapterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(spec);
        lock (_campaignFieldGate)
        {
            if (!ReferenceEquals(draft.Owner, this) || !ReferenceEquals(draft.Project, _project) || draft.Generation != _campaignFieldGeneration ||
                _campaignEntryCatalog?.Capture.EntrySource is not { } source || !SamePresentationSource(source, draft.Snapshot.Source) || source.Text != draft.Snapshot.Source.Text)
            { Status = "The package source changed. Reopen the chapter wizard; existing drafts were retained."; return false; }
            try
            {
                var mutation = WccCampaignChapterAuthoringPlanner.Add(draft.Snapshot, spec);
                return StageCampaignPackageEdit(WccCampaignPackageEdit.ForChapter(mutation, _campaignEntryCatalog!.Capture.Sources.Where(item => item.IsAuthored && IsPathInsideCampaignRoot(item.Path)).ToArray()), "Add chapter " + spec.Name);
            }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
            { Status = refusal.Message; return false; }
        }
    }
    public bool StageCampaignChapterRemove()
    {
        lock (_campaignFieldGate)
        {
            if (!CanRemoveCampaignChapter || CampaignPackageAuthoringProblem is { }) return false;
            var snapshot = ReadCampaignChapterAuthoringSnapshot();
            if (snapshot is not { CanEdit: true }) { Status = snapshot?.Diagnostic ?? "The package source is not ready."; return false; }
            var selected = snapshot.Chapters.SingleOrDefault(chapter => chapter.CampaignDefine == SelectedCampaignEntry!.Plan?.CampaignDefine);
            if (selected is null) { Status = "The selected chapter no longer has one exact managed owner."; return false; }
            try
            {
                var mutation = WccCampaignChapterAuthoringPlanner.Remove(snapshot, selected.Id);
                return StageCampaignPackageEdit(WccCampaignPackageEdit.ForChapter(mutation, _campaignEntryCatalog!.Capture.Sources.Where(item => item.IsAuthored && IsPathInsideCampaignRoot(item.Path)).ToArray()), "Remove chapter " + selected.Name);
            }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
            { Status = refusal.Message; return false; }
        }
    }
    private WccCampaignChapterAuthoringSnapshot? ReadCampaignChapterAuthoringSnapshot() =>
        _campaignEntryCatalog?.Capture.EntrySource is { } source
            ? WccCampaignChapterAuthoringPlanner.Read(source, CampaignPackageAuthoringMetadata, _campaignEntryCatalog!.Capture.Sources.Where(item => item.IsAuthored && IsPathInsideCampaignRoot(item.Path)).ToArray()) : null;
    internal void RaiseCampaignChapterAuthoringChanged()
    {
        OnPropertyChanged(nameof(CanAddCampaignChapter));
        OnPropertyChanged(nameof(CanRemoveCampaignChapter));
        OnPropertyChanged(nameof(CampaignChapterGuidance));
        OnPropertyChanged(nameof(CampaignChapterRemovalGuidance));
    }
}
