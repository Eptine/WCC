using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private readonly List<WccCampaignLocalPresetMutation> _campaignPresetMutations = [];
    internal IReadOnlyList<WccCampaignLocalPresetMutation> CampaignPresetMutations => _campaignPresetMutations.ToArray();
    internal WmlCampaignEvaluationPlan? CampaignPresetEditingPlan => EffectiveCampaignPlan;

    private static CatalogPickerOptionVm[] PresetUnitChoices(WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId> visible, string? install) => snapshot.FindEntities(WccEntityTypes.UnitType)
        .Where(entity => entity.ExportSymbol is not null && snapshot.FindPackageForEntity(entity.Id)?.IsAvailable == true
            && WccLibraryRevisionVisibility.ContainsEntity(snapshot, visible, entity))
        .Select(entity => new CatalogPickerOptionVm
        {
            ValueId = WccScenarioFieldValueCodec.DecodeLiteral(entity.ExportSymbol), DisplayName = entity.DisplayName ?? entity.ExportSymbol!,
            LibraryEntityId = entity.Id, LibraryPackageId = snapshot.FindPackageForEntity(entity.Id)?.Id,
            RaceId = entity.Summary.GetValueOrDefault("race") ?? "",
            PreviewPathResolver = snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file)
                ? global::Wcc.App.Preview.WccUnitCatalogPreview.CreateResolver(entity, file, snapshot.FindPackageForEntity(entity.Id), install) : null
        }).ToArray();

    public string? CampaignPresetEditProblem(WccCampaignLocalPresetEntry entry)
    {
        lock (_campaignFieldGate)
        {
            if (HasCampaignPackageEditDraft) return "Save or discard the pending dependency or chapter changes before editing presets.";
            if (HasCampaignScenarioCreationDraft) return "Save or discard the new scenario before editing presets.";
            if (_campaignPresetMigrationPending) return "Save or revert the pending preset move before editing a saved definition.";

            if (!CampaignLocalPresets.Any(current => ReferenceEquals(current, entry)) || !entry.IsSupported)
                return "Choose one current supported campaign-owned preset. Reload stale or unsupported definitions.";
            if (!entry.IsAvailableIn(EffectiveCampaignPlan)) return "Select the matching campaign before editing this scoped preset. Source inspection remains available.";
            if (_campaignPresetMutations.Any(row => row.Target.Id == entry.Id && row.IsDeletion))
                return "This preset is pending deletion. Revert that deletion to edit it.";
            if (IsReadOnly || _campaignFieldConfiguration?.Session.CanWrite != true) return "This campaign is open read-only.";
            if (IsCampaignUiLoading || IsCampaignFieldSaveBusy || IsSynchronizingUi || _publishingCampaignProjection
                || _changingCampaignPresets || _changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation
                || _publishingCampaignStructure || _changingCampaignStructure || _publishingCampaignCredits || _changingCampaignCredits)
                return "Wait for the current campaign update before editing a preset.";
            if (_campaignHasRawSourceChanges || _collectedSaveRecoveryPending || _campaignFieldSession.Recovery is not null)
                return "Save RAW changes or finish source recovery before editing a preset.";
            if (_campaignProjectionBinding is not { } binding || !IsCurrentCampaignBinding(binding)
                || !IsCurrentCampaignPresetPackageSource(_campaignPresetCatalog?.MainSource) || EffectiveCampaignPlan is null)
                return "Wait for the current campaign source and difficulties to resolve.";
            if (GetCampaignDraftBindingProblem() is { } problem) return problem;
            try
            {
                var storage = ReadCampaignPresetStorage();
                if (!storage.CanStore) return storage.Diagnostic;
                return storage.Entries.Any(current => current.Id == entry.Id && current.Name == entry.Name
                    && current.SourcePath == entry.SourcePath && current.SourceHash == entry.SourceHash
                    && current.DefinitionRange.StartOffset == entry.DefinitionRange.StartOffset && current.DefinitionRange.Length == entry.DefinitionRange.Length)
                    ? null : "Structured editing needs one exact managed definition in the campaign main file or utils/presets.cfg. Use shared RAW for other sources.";
            }
            catch (InvalidOperationException issue) { return issue.Message; }
        }
    }

    public string? CampaignPresetDeleteProblem(WccCampaignLocalPresetEntry entry)
    {
        if (CampaignPresetEditProblem(entry) is { } problem) return problem;
        try
        {
            var callers = CampaignPresetCallers(entry);
            return callers.Count == 0 ? null : "Preset " + entry.Name + " is still used by: " + string.Join(", ", callers);
        }
        catch (InvalidOperationException issue) { return issue.Message; }
    }

    public CampaignPresetCreationForm? BeginCampaignPresetEdit(WccCampaignLocalPresetEntry entry)
    {
        lock (_campaignFieldGate)
        {
            if (CampaignPresetEditProblem(entry) is { } problem) { Status = problem; return null; }
            var binding = _campaignProjectionBinding!; var source = CampaignPresetPackageSource!;
            var plan = EffectiveCampaignPlan!; var authoring = _campaignAuthoring;
            var sources = _campaignPresetCapturedSources; var capture = _campaignPresetCapture; var install = _campaignPresetInstallPath;
            var previous = _campaignPresetMutations.SingleOrDefault(row => row.Target.Id == entry.Id);
            bool Current() => IsCurrentCampaignBinding(binding) && ReferenceEquals(plan, EffectiveCampaignPlan)
                && ReferenceEquals(authoring, _campaignAuthoring) && ReferenceEquals(sources, _campaignPresetCapturedSources) && ReferenceEquals(capture, _campaignPresetCapture)
                && IsCurrentCampaignPresetPackageSource(source) && ReferenceEquals(previous, _campaignPresetMutations.SingleOrDefault(row => row.Target.Id == entry.Id))
                && CampaignLocalPresets.Any(current => ReferenceEquals(current, entry)) && install == _campaignPresetInstallPath
                && PresetMutationPublicationReady();
            var snapshot = binding.Configuration.Snapshot!;
            var visible = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, binding.Configuration.RootRevision!.Value);
            if (entry.Role is WccCampaignLocalPresetRole.Unit or WccCampaignLocalPresetRole.Event)
                return CreateContentPresetForm(entry.Role, entry.Id, ReadCampaignPresetStorage(),
                    PresetUnitChoices(snapshot, visible, install), binding, Current, entry,
                    _campaignPresetMutations.SingleOrDefault(row => row.Target.Id == entry.Id)?.Replacement ?? entry.Payload!);
            var previews = new WccPresetFieldImagePreviews(snapshot, binding.Configuration.RootRevision.Value, entry.SourcePath, install, Current);
            var fields = new CampaignPresetFieldSession(entry.Role, plan, PresetUnitChoices(snapshot, visible, install), Current, previews.Get);
            fields.LoadEditablePayload(_campaignPresetMutations.SingleOrDefault(row => row.Target.Id == entry.Id)?.Replacement ?? entry.Payload!);
            fields.LockSavedIdentities();
            var form = new CampaignPresetCreationForm(fields, previews, entry.Id, entry.SourcePath, (name, payload) =>
            {
                lock (_campaignFieldGate)
                {
                    if (!Current() || name != entry.Name) { Status = "The shared preset form changed. Reopen it; current drafts remain."; return false; }
                    return StageCampaignPresetMutation(new(entry, payload), binding, Current);
                }
            }) { Name = entry.Name, IsNameReadOnly = true, StageProblemProvider = () => Status,
                ScopeChoices = [new(entry.Scope, entry.Scope.DisplayName, entry.SourcePath)],
                StorageNotice = "The saved definition keeps its macro name, scope, named-unit IDs, source file and translation domain. Global Save checks its declared contexts and callers." };
            form.SelectedScope = form.ScopeChoices[0];
            return form;
        }
    }

    public bool StageCampaignPresetDeletion(WccCampaignLocalPresetEntry entry)
    {
        lock (_campaignFieldGate)
        {
            if (CampaignPresetDeleteProblem(entry) is { } problem) { Status = problem; return false; }
            var binding = _campaignProjectionBinding!; var sources = _campaignPresetCapturedSources;
            var plan = EffectiveCampaignPlan; var catalog = _campaignPresetCatalog;
            var version = _campaignPresetDraftVersion;
            bool Current() => IsCurrentCampaignBinding(binding) && ReferenceEquals(sources, _campaignPresetCapturedSources)
                && ReferenceEquals(plan, EffectiveCampaignPlan) && ReferenceEquals(catalog, _campaignPresetCatalog)
                && version == _campaignPresetDraftVersion && PresetMutationPublicationReady();
            return StageCampaignPresetMutation(new(entry, null), binding, Current);
        }
    }

    private bool PresetMutationPublicationReady() => !IsReadOnly && !IsCampaignUiLoading && !IsCampaignFieldSaveBusy
        && !_campaignFieldSession.IsBusy && _campaignFieldSession.Recovery is null && !IsSynchronizingUi
        && !_campaignHasRawSourceChanges && !_collectedSaveRecoveryPending && !_publishingCampaignProjection
        && !_changingCampaignScenarioCreation && !_changingCampaignUnitTypeCreation && !_publishingCampaignStructure
        && !_changingCampaignStructure && !_publishingCampaignCredits && !_changingCampaignCredits;

    private bool StageCampaignPresetMutation(WccCampaignLocalPresetMutation mutation, CampaignDraftBinding binding, Func<bool> current)
    {
        if (!current() || CampaignPresetEditProblem(mutation.Target) is not null)
        { Status = "The preset edit is stale or busy. Reopen it; current drafts remain."; return false; }
        try
        {
            var unchanged = !mutation.IsDeletion && WccCampaignLocalPresetStoragePlanner.RenderMutationPreview(mutation, EffectiveCampaignPlan)
                == mutation.Target.DefinitionRange.Text;
            var others = _campaignPresetMutations.Where(row => row.Target.Id != mutation.Target.Id).ToArray();
            if (unchanged && others.Length == _campaignPresetMutations.Count)
            { Status = "The shared preset is unchanged. No source change was staged."; return true; }
            var next = unchanged ? others : others.Append(mutation).ToArray();
            if (next.Length != 0 || _campaignPresetDrafts.Count != 0 || _campaignPresetMigrationPending)
                WccCampaignLocalPresetStoragePlanner.ValidatePendingEdits(ReadCampaignPresetStorage(), _campaignPresetDrafts, next, _campaignPresetCapturedSources!);
            var version = _campaignPresetDraftVersion;
            _changingCampaignPresets = true;
            try
            {
                _campaignFieldSession.Invalidate(); CollectedSaveAuthorityChanging?.Invoke();
                if (!current() || version != _campaignPresetDraftVersion)
                { Status = "The shared preset form changed while staging. Reopen it; existing drafts remain."; return false; }
                _campaignDraftBinding ??= binding;
                _campaignPresetMutations.Clear(); _campaignPresetMutations.AddRange(next); _campaignPresetDraftVersion++;
            }
            finally { _changingCampaignPresets = false; }
            FinishCampaignDraftMutation(); Status = unchanged ? "The preset edit was reverted to its saved definition. Other pending changes remain."
                : mutation.IsDeletion ? "Preset deletion staged. Global Save refuses remaining callers."
                : "Shared preset changes staged. Global Save checks every difficulty and caller.";
            return true;
        }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
        { Status = refusal.Message; return false; }
    }
}
