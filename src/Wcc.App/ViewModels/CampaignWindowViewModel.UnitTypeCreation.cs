using System;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

internal sealed record CampaignUnitTypeCreationBinding(WccUnitTypeCreationSnapshot Snapshot,
    WccLibrarySnapshot LibrarySnapshot, WccPackageRevisionId RootRevision, System.Collections.Generic.IReadOnlyList<WmlStaticSource> Sources,
    Func<bool> IsCurrent, Func<bool> IsSameContext)
{
    public System.Collections.Generic.IReadOnlyList<WmlEvaluationContext> Contexts { get; init; } = [];
}

public partial class CampaignWindowViewModel
{
    private WccUnitTypeCreationSpec? _campaignUnitTypeCreationDraft;
    private long _campaignUnitTypeCreationVersion;
    private bool _changingCampaignUnitTypeCreation;
    public bool HasCampaignUnitTypeCreationDraft => _campaignUnitTypeCreationDraft is not null;
    public bool CanCreateCampaignUnitType => CampaignUnitTypeCreationProblem is null;
    public string? CampaignUnitTypeCreationProblem
    {
        get
        {
            lock (_campaignFieldGate)
            {
                if (_changingCampaignUnitTypeCreation || HasCampaignUnitTypeCreationDraft)
                    return "Save or discard the pending new unit type before creating another.";
                if (CampaignScenarioCreationProblem is { } problem) return problem;
                var snapshot = WccUnitTypeCreationPlanner.Read(_campaignProjection!.Source!, _campaignAuthoring, EffectiveCampaignPlan, _campaignEntryCatalog?.Capture.Sources);
                return snapshot.CanCreate ? null : snapshot.Diagnostic;
            }
        }
    }
    public System.Collections.Generic.IReadOnlyList<PendingEditorChange> CampaignUnitTypeCreationPendingChanges =>
        _campaignUnitTypeCreationDraft is { } spec ? [new("Campaign", "New unit type: " + spec.Id,
            "units/" + (spec.FileNameStem ?? spec.Id) + ".cfg")] : [];

    internal CampaignUnitTypeCreationBinding? CaptureCampaignUnitTypeCreationBinding()
    {
        lock (_campaignFieldGate)
        {
            if (CampaignUnitTypeCreationProblem is { } problem) { Status = problem; return null; }
            if (_campaignPresetCapturedSources is not { } sources || _campaignFieldConfiguration?.Snapshot is not { } library)
            { Status = "Wait for the captured campaign and Core source catalog."; return null; }
            var binding = new CampaignDraftBinding(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
            var source = _campaignProjection!.Source!; var plan = EffectiveCampaignPlan!;
            var authoring = AuthoringKey(_campaignAuthoring);
            var unitVersion = _campaignUnitTypeCreationVersion;
            bool SameContext() => ReferenceEquals(binding.Project, _project) && !IsReadOnly && !_collectedSaveRecoveryPending &&
                _campaignFieldConfiguration is { } current && current.Session == binding.Configuration.Session &&
                current.RootRevision == binding.Configuration.RootRevision && current.ProjectIdentity == binding.Configuration.ProjectIdentity &&
                current.RuntimeIdentity == binding.Configuration.RuntimeIdentity && current.AddonsRoot == binding.Configuration.AddonsRoot &&
                authoring == AuthoringKey(_campaignAuthoring);
            bool Current() => SameContext() && IsCurrentCampaignBinding(binding) && ReferenceEquals(plan, EffectiveCampaignPlan) &&
                unitVersion == _campaignUnitTypeCreationVersion &&
                ReferenceEquals(sources, _campaignPresetCapturedSources) && ReferenceEquals(library, _campaignFieldConfiguration?.Snapshot) &&
                source.Text == GetCurrentMainSourceRawWml() && !_campaignHasRawSourceChanges && !IsCampaignUiLoading &&
                !IsCampaignFieldSaveBusy && !IsSynchronizingUi && !HasCampaignUnitTypeCreationDraft && !HasCampaignScenarioCreationDraft;
            return new(WccUnitTypeCreationPlanner.Read(source, _campaignAuthoring, plan, _campaignEntryCatalog?.Capture.Sources), library,
                binding.Configuration.RootRevision!.Value, sources, Current, SameContext) { Contexts = plan.Contexts };
        }
    }

    public bool StageCampaignUnitTypeCreation(WccUnitTypeCreationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_campaignFieldGate)
        {
            if (CampaignUnitTypeCreationProblem is { } problem) { Status = problem; return false; }
            var binding = new CampaignDraftBinding(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
            var source = _campaignProjection!.Source!; var plan = EffectiveCampaignPlan!;
            var stamp = GetPendingSourceChangeStamp();
            WccUnitTypeCreationEdit edit;
            try
            {
                edit = WccUnitTypeCreationPlanner.CreateEdit(WccUnitTypeCreationPlanner.Read(source, _campaignAuthoring, plan, _campaignEntryCatalog?.Capture.Sources), spec);
                _ = WccUnitTypeCreationPlanner.PlanSources(edit);
                if (spec.Base is { } basis)
                    _ = WccUnitTypeCreationPlanner.CaptureCoreBase(binding.Configuration.Snapshot!, basis.CoreRevisionId, basis.EntityId, basis.Source);
                if (spec.Copy is { } copy)
                {
                    WccUnitTypeAuthoredCopyPlanner.ValidateSourceEvidence(binding.Configuration.Snapshot!, copy);
                    if (WccUnitTypeAuthoredCopyPlanner.VerifySourceFreshness(copy) is { } copyProblem)
                        throw new InvalidOperationException(copyProblem);
                }
                if (spec.Materialization is { } materialization)
                {
                    WccUnitTypeMaterializationPlanner.ValidateSourceEvidence(binding.Configuration.Snapshot!, materialization);
                    if (WccUnitTypeMaterializationPlanner.VerifySourceFreshness(materialization) is { } materializationProblem)
                        throw new InvalidOperationException(materializationProblem);
                }
            }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
            { Status = refusal.Message; return false; }
            _changingCampaignUnitTypeCreation = true;
            try
            {
                _campaignFieldSession.Invalidate(); CollectedSaveAuthorityChanging?.Invoke();
                if (!IsCurrentCampaignBinding(binding) || IsReadOnly || IsCampaignFieldSaveBusy || _campaignHasRawSourceChanges ||
                    _collectedSaveRecoveryPending || IsSynchronizingUi || !ReferenceEquals(plan, EffectiveCampaignPlan) ||
                    source.Text != GetCurrentMainSourceRawWml() || stamp != GetPendingSourceChangeStamp())
                { Status = "The editing context changed. Reopen the unit type wizard; current drafts remain."; return false; }
                _campaignDraftBinding ??= binding; _campaignUnitTypeCreationDraft = edit.Spec; _campaignUnitTypeCreationVersion++;
            }
            finally { _changingCampaignUnitTypeCreation = false; }
            RaiseCampaignFieldsChanged(); Status = "New unit type staged. Global Save checks and creates its file.";
            return HasCampaignUnitTypeCreationDraft;
        }
    }
    public void RevertCampaignUnitTypeCreation()
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignFieldSaveBusy || _changingCampaignUnitTypeCreation || _publishingCampaignProjection || IsSynchronizingUi ||
                !HasCampaignUnitTypeCreationDraft) return;
            ClearCampaignUnitTypeCreationDraft(); CollectedSaveAuthorityChanging?.Invoke(); FinishCampaignDraftMutation();
        }
    }
    private void ClearCampaignUnitTypeCreationDraft()
    { _campaignUnitTypeCreationDraft = null; _campaignUnitTypeCreationVersion++; }
    private WccUnitTypeCreationEdit? CaptureCampaignUnitTypeCreation() => _campaignUnitTypeCreationDraft is { } spec
        ? WccUnitTypeCreationPlanner.CreateEdit(WccUnitTypeCreationPlanner.Read(_campaignProjection!.Source!, _campaignAuthoring, EffectiveCampaignPlan, _campaignEntryCatalog?.Capture.Sources), spec) : null;
}
