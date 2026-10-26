using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed class CampaignManagedDependencyRow : ViewModelBase
{
    private readonly CampaignWindowViewModel _owner;
    internal CampaignManagedDependencyRow(CampaignWindowViewModel owner, WccCampaignDependencySpec spec, string pendingState, bool removed)
    { _owner = owner; Spec = spec; PendingState = pendingState; IsRemoved = removed; }
    internal WccCampaignDependencySpec Spec { get; }
    internal bool IsRemoved { get; }
    public string AddonId => Spec.AddonId;
    public string Targets => string.Join(", ", Spec.TargetCampaignDefines);
    public string PendingState { get; }
    public string Summary => (PendingState.Length == 0 ? "" : PendingState + " · ") + Spec.Steps.Count + " loading steps · " + Targets;
    public bool CanEdit => !IsRemoved && _owner.CanModifyCampaignDependencies;
    public bool CanRemove => _owner.CanModifyCampaignDependencies;
    public string RemoveLabel => IsRemoved ? "Restore dependency" : PendingState == "Pending addition" ? "Cancel addition" : "Remove dependency";
    public string EditTooltip => CanEdit ? "Edit this WCC loading recipe." : _owner.ManagedCampaignDependencyGuidance;
    public string RemoveTooltip => CanRemove ? IsRemoved ? "Cancel the pending removal." : "Remove this managed recipe and its unchanged owned loader." : _owner.ManagedCampaignDependencyGuidance;
    internal void RefreshAvailability()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(EditTooltip)); OnPropertyChanged(nameof(RemoveTooltip));
    }
}

public partial class CampaignWindowViewModel
{
    // Always replan the complete desired set against its saved captured baseline.
    // Generated previews are never fed back as authoring authority.
    private sealed record DependencyBatch(WccCampaignDependencySnapshot Snapshot,
        IReadOnlyList<WccCampaignDependencySpec> Desired, IReadOnlyList<WccCampaignDependencySourcePin> Pins);
    private DependencyBatch? _campaignDependencyBatch;
    private object? _dependencyAuthoringCatalog;
    private string? _dependencyAuthoringAddonId;
    private WccCampaignDependencySnapshot? _dependencyAuthoringSnapshot;
    private long _dependencyAuthoringRowsVersion = -1;
    private IReadOnlyList<CampaignManagedDependencyRow> _managedDependencyRows = [];
    private readonly ConditionalWeakTable<CampaignDependencyCreationViewModel, DependencyWizardBinding> _dependencyWizards = new();
    private sealed record DependencyWizardBinding(WccProject Project, string MainText, long Version,
        Func<bool> IsCurrent, Guid? EditingId)
    {
        internal long? RefreshedVersion { get; set; }
    }
    public bool CanModifyCampaignDependencies => GetCampaignPackageAuthoringProblem(true) is null && ReadDependencyAuthoringSnapshot() is { CanAdd: true };
    public IReadOnlyList<CampaignManagedDependencyRow> ManagedCampaignDependencies
    { get { lock (_campaignFieldGate) { _ = ReadDependencyAuthoringSnapshot(); return _managedDependencyRows; } } }
    public string ManagedCampaignDependencyGuidance => GetCampaignPackageAuthoringProblem(true) ?? ReadDependencyAuthoringSnapshot()?.Diagnostic ??
        "Wait for the current package source capture. Existing unmarked dependency loading code remains available in RAW WML.";

    private void RaiseDependencyAuthoringChanged()
    {
        foreach (var row in _managedDependencyRows) row.RefreshAvailability();
        foreach (var property in new[] {nameof(CanModifyCampaignDependencies), nameof(ManagedCampaignDependencies), nameof(ManagedCampaignDependencyGuidance), nameof(IsCampaignDependencySourceRefreshing)}) OnPropertyChanged(property);
    }
    private WccCampaignDependencySnapshot? ReadDependencyAuthoringSnapshot()
    {
        lock (_campaignFieldGate)
        {
            var catalog = _campaignEntryCatalog;
            if (catalog?.Capture is not { IsCaptureComplete: true, EntrySource: { } main } || _project?.SourcePath is not { } path ||
                !SamePresentationPath(main.Path, path) || main.Text != GetCurrentMainSourceRawWml())
            { _dependencyAuthoringCatalog = null; _dependencyAuthoringSnapshot = null; _managedDependencyRows = []; return null; }
            var addonId = CampaignPackageAuthoringMetadata?.AddonId ?? Path.GetFileName(Path.GetDirectoryName(path)!);
            if (!ReferenceEquals(_dependencyAuthoringCatalog, catalog) || _dependencyAuthoringAddonId != addonId)
            {
                var sources = catalog.Capture.Sources.Where(source => source.IsAuthored && IsPathInsideCampaignRoot(source.Path)).ToArray();
                _dependencyAuthoringSnapshot = WccCampaignDependencyAuthoringPlanner.Read(main, addonId, sources);
                _dependencyAuthoringCatalog = catalog; _dependencyAuthoringAddonId = addonId; _dependencyAuthoringRowsVersion = -1;
            }
            if (_dependencyAuthoringRowsVersion != _campaignPackageDraftVersion)
            {
                var baseline = _campaignDependencyBatch?.Snapshot ?? _dependencyAuthoringSnapshot!;
                var desired = _campaignDependencyBatch?.Desired ?? baseline.Entries.Select(entry => entry.Spec).ToArray();
                var original = baseline.Entries.Select(entry => entry.Spec).ToArray();
                _managedDependencyRows = Array.AsReadOnly(original.Select(spec => desired.SingleOrDefault(value => value.Id == spec.Id) ?? spec)
                    .Concat(desired.Where(spec => original.All(value => value.Id != spec.Id)))
                    .Select(spec => new CampaignManagedDependencyRow(this, spec, DependencyChangeLabel(baseline, desired, spec.Id), desired.All(value => value.Id != spec.Id))).ToArray());
                _dependencyAuthoringRowsVersion = _campaignPackageDraftVersion;
            }
            return _campaignDependencyBatch?.Snapshot ?? _dependencyAuthoringSnapshot;
        }
    }
    private static bool SameDependencySpec(WccCampaignDependencySpec left, WccCampaignDependencySpec right) =>
        left.Id == right.Id && left.AddonId == right.AddonId && left.DeclareDependency == right.DeclareDependency &&
        left.AppliesToAllCampaigns == right.AppliesToAllCampaigns && left.TargetCampaignDefines.SequenceEqual(right.TargetCampaignDefines) && left.Steps.SequenceEqual(right.Steps);
    private static string DependencyChangeLabel(WccCampaignDependencySnapshot baseline, IReadOnlyList<WccCampaignDependencySpec> desired, Guid id)
    {
        var old = baseline.Entries.SingleOrDefault(entry => entry.Spec.Id == id)?.Spec;
        var current = desired.SingleOrDefault(spec => spec.Id == id);
        return old is null ? "Pending addition" : current is null ? "Pending removal" : SameDependencySpec(old, current) ? "" : "Pending edit";
    }
    private static IReadOnlyList<PendingEditorChange> DependencyPendingChanges(DependencyBatch batch) =>
        Array.AsReadOnly(batch.Snapshot.Entries.Select(entry => entry.Spec.Id).Concat(batch.Desired.Select(spec => spec.Id)).Distinct()
            .Select(id => (Id: id, Label: DependencyChangeLabel(batch.Snapshot, batch.Desired, id)))
            .Where(change => change.Label.Length != 0)
            .Select(change => new PendingEditorChange("Campaign", change.Label + ": " +
                (batch.Desired.SingleOrDefault(spec => spec.Id == change.Id) ?? batch.Snapshot.Entries.Single(entry => entry.Spec.Id == change.Id).Spec).AddonId,
                batch.Snapshot.MainSource.Path)).ToArray());

    internal async Task<bool> PrepareCampaignDependencyAuthoringAsync()
    {
        lock (_campaignFieldGate)
            if (_campaignDependencyBatch is not null) return CanModifyCampaignDependencies;
        return PrepareCampaignPackageAuthoringAsync is not { } prepare || await prepare();
    }
    public async Task<CampaignDependencyCreationViewModel?> BeginCampaignDependencyWizardAsync(CampaignManagedDependencyRow? edited = null)
    {
        var project = _project;
        if (!await PrepareCampaignDependencyAuthoringAsync() || !ReferenceEquals(project, _project)) return null;
        CampaignDependencyCreationViewModel model;
        lock (_campaignFieldGate)
        {
            if (!CanModifyCampaignDependencies || ReadDependencyAuthoringSnapshot() is not { } snapshot)
            { Status = ManagedCampaignDependencyGuidance; return null; }
            if (edited is not null && (!edited.CanEdit || !_managedDependencyRows.Any(row => ReferenceEquals(row, edited)))) return null;
            var configuration = _campaignFieldConfiguration;
            var catalog = _campaignEntryCatalog;
            var generation = _campaignFieldGeneration;
            var version = _campaignPackageDraftVersion;
            var mainText = GetCurrentMainSourceRawWml()!;
            bool Current() => ReferenceEquals(project, _project) && ReferenceEquals(configuration, _campaignFieldConfiguration) &&
                ReferenceEquals(catalog, _campaignEntryCatalog) && generation == _campaignFieldGeneration && version == _campaignPackageDraftVersion &&
                mainText == GetCurrentMainSourceRawWml() && CanModifyCampaignDependencies;
            var targets = catalog!.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Plan?.CampaignDefine))
                .GroupBy(entry => entry.Plan!.CampaignDefine!, StringComparer.Ordinal)
                .Select(group => new CampaignDependencyTargetChoice(string.Join(" / ", group.Select(entry => entry.Name)), group.Key)).ToArray();
            model = new(targets, Current, snapshot.ProjectAddonId, existingSpec: edited?.Spec);
            _dependencyWizards.Add(model, new(project!, mainText, version, Current, edited?.Spec.Id));
        }
        var previousProvider = edited is null ? null : FindDependencyProvider(edited.AddonId, null);
        if (previousProvider.HasValue && previousProvider.Value.Package.SourceLocation is { } folder)
            await model.SelectSourceFolderAsync(folder);
        return model;
    }
    public async Task<bool> StageCampaignDependencyAsync(CampaignDependencyCreationViewModel model, WccCampaignDependencySpec spec)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(spec);
        if (!_dependencyWizards.TryGetValue(model, out var binding) || !binding.IsCurrent() || spec.Id != model.DependencyId)
        { Status = "The dependency wizard's campaign context changed. Reopen it."; return false; }
        var folder = model.SelectedSourceFolder;
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var provider = FindDependencyProvider(spec.AddonId, folder);
            var version = binding.Version;
            if (provider is null)
            {
                if (!await RefreshDependencySourceForBatchAsync(binding, folder)) return false;
                if (binding.RefreshedVersion is not { } refreshedVersion) return false;
                version = refreshedVersion;
                provider = FindDependencyProvider(spec.AddonId, folder);
            }
            lock (_campaignFieldGate)
            {
                if (!ReferenceEquals(binding.Project, _project) || binding.MainText != GetCurrentMainSourceRawWml() || version != _campaignPackageDraftVersion ||
                    !CanModifyCampaignDependencies || provider is null || ReadDependencyAuthoringSnapshot() is not { } snapshot)
                { Status = "The dependency source or package context changed. Reopen the wizard."; return false; }
                var current = _campaignDependencyBatch?.Desired ?? snapshot.Entries.Select(entry => entry.Spec).ToArray();
                if (binding.EditingId is { } id && current.All(value => value.Id != id))
                { Status = "The managed dependency being edited is no longer present."; return false; }
                var desired = binding.EditingId is { } editingId
                    ? current.Select(value => value.Id == editingId ? spec : value).ToArray()
                    : current.Append(spec).ToArray();
                var pins = (_campaignDependencyBatch?.Pins ?? []).Where(pin => pin.AddonId != spec.AddonId && desired.Any(value => value.AddonId == pin.AddonId))
                    .Append(new WccCampaignDependencySourcePin(spec.AddonId, provider.Value.Revision.Id)).ToArray();
                return StageCampaignDependencyBatch(snapshot, desired, pins);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Status = "The dependency could not be staged: " + failure.Message; return false; }
    }
    internal bool StageCampaignDependencyBatch(WccCampaignDependencySnapshot baseline, IReadOnlyList<WccCampaignDependencySpec> desired,
        IReadOnlyList<WccCampaignDependencySourcePin> pins)
    {
        lock (_campaignFieldGate)
        {
            if (!CanModifyCampaignDependencies || !ReferenceEquals(ReadDependencyAuthoringSnapshot(), baseline) ||
                _campaignDependencyBatch is { } previous && !ReferenceEquals(previous.Snapshot, baseline) ||
                _campaignEntryCatalog?.Capture.EntrySource is not { } main || !SamePresentationSource(main, baseline.MainSource)) return false;
            // Editing or restoring a recipe must not silently change its load order.
            var byId = desired.ToDictionary(spec => spec.Id);
            var originalIds = baseline.Entries.Select(entry => entry.Spec.Id).ToHashSet();
            desired = baseline.Entries.Where(entry => byId.ContainsKey(entry.Spec.Id)).Select(entry => byId[entry.Spec.Id])
                .Concat(desired.Where(spec => !originalIds.Contains(spec.Id))).ToArray();
            var version = _campaignPackageDraftVersion;
            var edit = WccCampaignPackageEdit.ForDependencies(WccCampaignDependencyAuthoringPlanner.Plan(baseline, desired), pins);
            var next = new DependencyBatch(baseline, Array.AsReadOnly(desired.ToArray()), Array.AsReadOnly(pins.ToArray()));
            var changes = DependencyPendingChanges(next);
            _campaignFieldSession.Invalidate(); CollectedSaveAuthorityChanging?.Invoke();
            if (!CanModifyCampaignDependencies || version != _campaignPackageDraftVersion) return false;
            if (changes.Count == 0)
            {
                ClearCampaignPackageEditDraft(); FinishCampaignDraftMutation(); RaiseCampaignPackageAuthoringChanged();
                Status = "The dependency changes were reverted. Other pending changes remain."; return true;
            }
            _campaignDraftBinding = new(_project!, _project!.Campaign!, _campaignFieldConfiguration!, _campaignFieldGeneration);
            _campaignDependencyBatch = next; _campaignPackageEditDraft = edit;
            _campaignPackageDraftSummary = changes.Count + " dependency changes"; _campaignPackageDraftVersion++;
            RaiseCampaignFieldsChanged(); RaiseCampaignPackageAuthoringChanged();
            Status = changes.Count + " dependency changes staged. Global Save checks and writes the complete batch."; return true;
        }
    }
    public bool RemoveManagedCampaignDependency(CampaignManagedDependencyRow row)
    {
        lock (_campaignFieldGate)
        {
            if (!CanModifyCampaignDependencies || !_managedDependencyRows.Any(current => ReferenceEquals(current, row)) ||
                ReadDependencyAuthoringSnapshot() is not { } snapshot) return false;
            try
            {
                if (row.IsRemoved) return RevertCampaignDependency(row.Spec.Id);
                var desired = (_campaignDependencyBatch?.Desired ?? snapshot.Entries.Select(entry => entry.Spec).ToArray()).Where(spec => spec.Id != row.Spec.Id).ToArray();
                return StageCampaignDependencyBatch(snapshot, desired, (_campaignDependencyBatch?.Pins ?? []).Where(pin => desired.Any(spec => spec.AddonId == pin.AddonId)).ToArray());
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
            { Status = failure.Message; return false; }
        }
    }
    internal bool RevertCampaignDependency(Guid id)
    {
        lock (_campaignFieldGate)
        {
            if (_campaignDependencyBatch is not { } batch || !CanModifyCampaignDependencies) return false;
            var original = batch.Snapshot.Entries.SingleOrDefault(entry => entry.Spec.Id == id)?.Spec;
            var desired = batch.Desired.Where(spec => spec.Id != id).Concat(original is null ? [] : new[] { original }).ToArray();
            return StageCampaignDependencyBatch(batch.Snapshot, desired, batch.Pins.Where(pin =>
                desired.Any(spec => spec.AddonId == pin.AddonId) && (original is null || pin.AddonId != original.AddonId)).ToArray());
        }
    }
    private IReadOnlyList<PendingChangeRemovalProposal> CaptureDependencyPendingRemovals()
    {
        if (_campaignDependencyBatch is not { } batch) return [];
        var version = _campaignPackageDraftVersion;
        return batch.Snapshot.Entries.Select(entry => entry.Spec.Id).Concat(batch.Desired.Select(spec => spec.Id)).Distinct()
            .Where(id => DependencyChangeLabel(batch.Snapshot, batch.Desired, id).Length != 0)
            .Select(id => new PendingChangeRemovalProposal(
                new("Campaign", DependencyChangeLabel(batch.Snapshot, batch.Desired, id) + ": " +
                    (batch.Desired.SingleOrDefault(spec => spec.Id == id) ?? batch.Snapshot.Entries.Single(entry => entry.Spec.Id == id).Spec).AddonId,
                    batch.Snapshot.MainSource.Path),
                () => CampaignPendingRemovalProblem() ?? (!ReferenceEquals(batch, _campaignDependencyBatch) || version != _campaignPackageDraftVersion
                    ? "This dependency proposal changed. Select its current pending row." : null),
                () => ReferenceEquals(batch, _campaignDependencyBatch) && version == _campaignPackageDraftVersion && RevertCampaignDependency(id)))
            .ToArray();
    }

    private (WccPackageRecord Package, WccPackageRevisionRecord Revision)? FindDependencyProvider(string addonId, string? folder)
    {
        var snapshot = _campaignFieldConfiguration?.Snapshot;
        if (snapshot is null) return null;
        var candidates = snapshot.PackagesById.Values.Where(package => package.IsAvailable && package.SourceKind == WccPackageSourceKinds.WesnothAddon &&
                (folder is null || SameDependencyMetadataRoot(package.SourceLocation, folder)))
            .Select(package => package.CurrentRevisionId is { } id && snapshot.RevisionsById.TryGetValue(id, out var revision)
                && revision.PackageId == package.Id && (package.SourceIdentifier ?? revision.AddonMetadata?.AddonId) == addonId
                ? (Package: package, Revision: revision) : default)
            .Where(candidate => candidate.Package is not null).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
