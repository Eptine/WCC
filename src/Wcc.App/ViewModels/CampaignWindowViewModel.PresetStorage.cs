using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private WccStaticSourceCaptureResult? _campaignPresetCapture;
    private IReadOnlyList<WmlStaticSource>? _campaignPresetCapturedSources;
    private bool _campaignPresetCaptureComplete;
    private bool _campaignPresetMigrationPending;
    private CampaignPresetStorageCache? _campaignPresetStorageCache;
    private sealed record CampaignPresetStorageCache(WccStaticSourceCaptureResult Capture, WmlStaticSource Source,
        WccCampaignAuthoringMetadata? Authoring, string AuthoringStamp, WmlCampaignEvaluationPlan? Plan, WccCampaignEntryCatalogProjection? Entries,
        WccCampaignLocalPresetCatalogSnapshot? Catalog, WccCampaignLocalPresetStorageSnapshot Snapshot)
    {
        public Dictionary<WccCampaignLocalPresetEntry, IReadOnlyList<string>> Callers { get; } = new(ReferenceEqualityComparer.Instance);
    }
    internal Func<WccCampaignLocalPresetEntry, bool>? CampaignPresetSharedRawCapability { get; set; }
    internal Func<WccCampaignLocalPresetEntry, WmlEditorDocumentViewModel?>? CampaignPresetSharedRawFactory { get; set; }

    // The selected menu may live in an included menu.cfg. Storage and companion
    // paths are anchored to the captured package root, never that physical owner.
    private WmlStaticSource? CampaignPresetPackageSource
    {
        get
        {
            var root = _campaignEntryCatalog?.Capture.EntrySource ?? _campaignProjection?.EvaluationEntrySource ?? _campaignProjection?.Source;
            // Keep the exact published request source when it is the root.
            // An included menu keeps its separate physical identity.
            return root is not null && _campaignProjection?.Source is { } published && SamePresentationSource(root, published)
                ? published : root;
        }
    }
    private bool IsCurrentCampaignPresetPackageSource(WmlStaticSource? source) => source is not null &&
        _project?.SourcePath is { } path && SamePresentationPath(source.Path, path) &&
        source.Text == GetCurrentMainSourceRawWml();
    private IReadOnlyList<WmlCampaignEvaluationPlan> CampaignPresetPackagePlans => _campaignEntryCatalog?.Entries
        .Select(entry => entry.Plan).OfType<WmlCampaignEvaluationPlan>().ToArray() ??
        (EffectiveCampaignPlan is { } plan ? [plan] : []);
    private IReadOnlyList<CampaignPresetScopeChoice> CampaignPresetScopeChoices(WccCampaignLocalPresetStorageSnapshot storage) =>
        storage.AvailableScopes.Where(scope => scope.IsPackage || scope.Targets(EffectiveCampaignPlan)).Select(scope =>
            new CampaignPresetScopeChoice(scope, scope.IsPackage ? "Whole package (all chapters)" : "This campaign: " + scope.DisplayName,
                CampaignPresetScopeStoragePath(storage, scope), scope.IsPackage && CampaignPresetPackagePlans.Count > 1
                    ? "Whole-package presets use shared values. Select this campaign for difficulty-specific values." : null)).ToArray();
    private static string CampaignPresetScopeStoragePath(WccCampaignLocalPresetStorageSnapshot storage, WccCampaignPresetScope scope)
    {
        var location = storage.Locations.Single(candidate => candidate.Scope == scope);
        return Path.Combine(Path.GetDirectoryName(storage.Source.Path)!, location.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }
    internal string CampaignPresetDraftStoragePath(WccCampaignLocalPresetCreationSpec spec) =>
        CampaignPresetScopeStoragePath(ReadCampaignPresetStorage(), spec.Scope);

    public bool HasCampaignPresetMigrationDraft => _campaignPresetMigrationPending;
    public string CampaignPresetStoragePath => _project?.SourcePath is { } main
        ? Path.Combine(Path.GetDirectoryName(main)!, "utils", "presets.cfg") : "utils/presets.cfg";
    public string? CampaignPresetMigrationProblem
    {
        get
        {
            lock (_campaignFieldGate)
            {
                if (_campaignPresetMigrationPending) return "The move is already pending in global Save.";
                if (_campaignPresetMutations.Count != 0) return "Save or revert pending preset edits before moving their definitions.";
                if (CampaignPresetCreationProblem is { } problem) return problem;
                var snapshot = ReadCampaignPresetStorage();
                return snapshot.CanMigrate ? null : "There are no supported managed presets in the main file to move.";
            }
        }
    }
    public bool CanMigrateCampaignPresets => CampaignPresetMigrationProblem is null;
    public bool CanRevertCampaignPresetMigration => _campaignPresetMigrationPending && _campaignPresetDrafts.Count == 0 &&
        !IsCampaignFieldSaveBusy && !_changingCampaignPresets && !(_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation) &&
        !_publishingCampaignProjection && !IsSynchronizingUi;
    private IReadOnlyList<PendingEditorChange> CampaignPresetMigrationPendingChanges
    {
        get
        {
            var moving = _campaignPresetMigrationPending || _campaignPresetMutations.Count == 0 && _campaignPresetDrafts.Count != 0 &&
                _campaignPresetCatalog?.Entries.Any(entry => entry.IsSupported && entry.SourcePath == _project?.SourcePath) == true;
            return moving ? [new("Campaign", "Move managed presets", "Main file -> " + CampaignPresetStoragePath)] : [];
        }
    }
    private WccCampaignLocalPresetStorageSnapshot ReadCampaignPresetStorage()
    {
        if (!_campaignPresetCaptureComplete || _campaignPresetCapture is not { } capture || _campaignPresetCapturedSources is null || CampaignPresetPackageSource is not { } source)
            throw new InvalidOperationException("Wait for the complete current campaign source inventory before storing presets.");
        var authoring = CampaignPackageAuthoringMetadata;
        var plan = EffectiveCampaignPlan;
        if (_campaignPresetStorageCache is { } cache && ReferenceEquals(cache.Capture, capture) && ReferenceEquals(cache.Source, source)
            && ReferenceEquals(cache.Authoring, authoring) && cache.AuthoringStamp == AuthoringKey(authoring) && ReferenceEquals(cache.Plan, plan) && ReferenceEquals(cache.Entries, _campaignEntryCatalog)
            && ReferenceEquals(cache.Catalog, _campaignPresetCatalog))
            return cache.Snapshot;
        // Reuse only the immutable capture that produced the current source and
        // catalog. Collected Save still rebuilds and reviews fresh source evidence.
        var snapshot = WccCampaignLocalPresetStoragePlanner.Read(capture, authoring, plan, CampaignPresetPackagePlans, source);
        _campaignPresetStorageCache = new(capture, source, authoring, AuthoringKey(authoring), plan, _campaignEntryCatalog, _campaignPresetCatalog, snapshot);
        return snapshot;
    }
    private IReadOnlyList<string> CampaignPresetCallers(WccCampaignLocalPresetEntry entry)
    {
        var snapshot = ReadCampaignPresetStorage();
        var cache = _campaignPresetStorageCache!;
        if (cache.Callers.TryGetValue(entry, out var callers)) return callers;
        callers = WccCampaignLocalPresetStoragePlanner.FindCallers(entry, snapshot, _campaignPresetCapturedSources!);
        cache.Callers.Add(entry, callers);
        return callers;
    }
    public bool StageCampaignPresetMigration()
    {
        lock (_campaignFieldGate)
        {
            if (CampaignPresetMigrationProblem is { } problem) { Status = problem; return false; }
            var binding = _campaignProjectionBinding!;
            var sources = _campaignPresetCapturedSources;
            var authoring = _campaignAuthoring;
            var plan = EffectiveCampaignPlan;
            var source = CampaignPresetPackageSource!;
            var version = _campaignPresetDraftVersion;
            bool Current() => IsCurrentCampaignBinding(binding) && IsCurrentCampaignPresetPackageSource(source) &&
                ReferenceEquals(sources, _campaignPresetCapturedSources) && ReferenceEquals(authoring, _campaignAuthoring) &&
                ReferenceEquals(plan, EffectiveCampaignPlan) && version == _campaignPresetDraftVersion && !IsReadOnly &&
                !IsCampaignFieldSaveBusy && !IsSynchronizingUi && !_publishingCampaignProjection &&
                !(_changingCampaignScenarioCreation || _changingCampaignUnitTypeCreation) && !_publishingCampaignStructure && !_changingCampaignStructure &&
                !_publishingCampaignCredits && !_changingCampaignCredits &&
                !_campaignHasRawSourceChanges && !_collectedSaveRecoveryPending;
            try
            {
                WccCampaignLocalPresetStoragePlanner.ValidatePendingEdits(ReadCampaignPresetStorage(), _campaignPresetDrafts, _campaignPresetMutations, _campaignPresetCapturedSources!);
                _changingCampaignPresets = true;
                try
                {
                    _campaignFieldSession.Invalidate(); CollectedSaveAuthorityChanging?.Invoke();
                    if (!Current()) return false;
                    _campaignDraftBinding ??= binding;
                    _campaignPresetMigrationPending = true; _campaignPresetDraftVersion++;
                }
                finally { _changingCampaignPresets = false; }
                RaiseCampaignFieldsChanged(); Status = "Managed preset move staged. Global Save updates both files together."; return true;
            }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
            { Status = refusal.Message; return false; }
        }
    }
    public void RevertCampaignPresetMigration()
    {
        lock (_campaignFieldGate)
        {
            if (!CanRevertCampaignPresetMigration) return;
            _campaignPresetMigrationPending = false; _campaignPresetDraftVersion++;
            CollectedSaveAuthorityChanging?.Invoke(); FinishCampaignDraftMutation();
        }
    }
    public bool CanEditCampaignPresetSharedRaw(WccCampaignLocalPresetEntry entry) =>
        CampaignLocalPresets.Any(current => ReferenceEquals(current, entry)) &&
        CampaignPresetSharedRawCapability?.Invoke(entry) == true;
    public WmlEditorDocumentViewModel? BeginCampaignPresetSharedRaw(WccCampaignLocalPresetEntry entry) =>
        CanEditCampaignPresetSharedRaw(entry) ? CampaignPresetSharedRawFactory?.Invoke(entry) : null;
}
