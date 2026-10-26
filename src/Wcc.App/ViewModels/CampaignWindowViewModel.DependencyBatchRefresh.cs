using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private DependencySourceRefresh? _dependencyBatchSourceRefresh;
    public bool IsCampaignDependencySourceRefreshing => _dependencyBatchSourceRefresh is not null;
    // Only the shell's existing source-indexing operation accepts this opaque token.
    // Ordinary manual attach/detach and unrelated pending scopes retain their guards.
    internal Func<object, string, Task<bool>>? RefreshCampaignDependencyBatchSourceAsync { get; set; }
    private sealed record DependencySourceRefresh(WccProject Project, CampaignDocument? Document,
        CampaignFieldConfiguration Configuration, WccCampaignDependencySnapshot Baseline,
        DependencyBatch? Batch, WccCampaignPackageEdit? Edit, long Version, string[] CampaignDefines);

    internal bool IsCurrentCampaignDependencySourceRefresh(object token)
    {
        lock (_campaignFieldGate)
            return token is DependencySourceRefresh refresh && ReferenceEquals(refresh, _dependencyBatchSourceRefresh) &&
                ReferenceEquals(refresh.Project, _project) && ReferenceEquals(refresh.Document, _project?.Campaign) &&
                ReferenceEquals(refresh.Edit, _campaignPackageEditDraft) && ReferenceEquals(refresh.Batch, _campaignDependencyBatch) &&
                refresh.Version == _campaignPackageDraftVersion && !HasNonPackageCampaignDrafts && !_campaignHasRawSourceChanges &&
                refresh.Baseline.MainSource.Text == GetCurrentMainSourceRawWml() &&
                _campaignFieldConfiguration is { } configuration && configuration.Session == refresh.Configuration.Session &&
                configuration.ProjectIdentity == refresh.Configuration.ProjectIdentity && configuration.RuntimeIdentity == refresh.Configuration.RuntimeIdentity;
    }
    private async Task<bool> RefreshDependencySourceForBatchAsync(DependencyWizardBinding binding, string folder)
    {
        DependencySourceRefresh refresh;
        lock (_campaignFieldGate)
        {
            if (!binding.IsCurrent() || RefreshCampaignDependencyBatchSourceAsync is null ||
                ReadDependencyAuthoringSnapshot() is not { } baseline) return false;
            refresh = new(_project!, _project!.Campaign, _campaignFieldConfiguration!, baseline,
                _campaignDependencyBatch, _campaignPackageEditDraft, ++_campaignPackageDraftVersion,
                _campaignEntryCatalog!.Entries.Select(entry => entry.Plan?.CampaignDefine ?? "").ToArray());
            _dependencyBatchSourceRefresh = refresh;
            CollectedSaveAuthorityChanging?.Invoke();
            RaiseCampaignFieldsChanged(); RaiseCampaignPackageAuthoringChanged();
        }
        try
        {
            if (!IsCurrentCampaignDependencySourceRefresh(refresh) ||
                !await RefreshCampaignDependencyBatchSourceAsync!(refresh, folder)) return false;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var task = _campaignProjectionTask;
                await task;
                if (!IsCurrentCampaignDependencySourceRefresh(refresh)) return false;
                if (ReferenceEquals(task, _campaignProjectionTask) && !IsCampaignUiLoading) break;
            }
            lock (_campaignFieldGate)
            {
                if (!IsCurrentCampaignDependencySourceRefresh(refresh) ||
                    _campaignEntryCatalog?.Capture is not { IsCaptureComplete: true, EntrySource: { } main } capture ||
                    _campaignProjectionBinding is not { } freshBinding || !IsCurrentCampaignBinding(freshBinding) ||
                    !SamePresentationSource(main, refresh.Baseline.MainSource) ||
                    !_campaignEntryCatalog.Entries.Select(entry => entry.Plan?.CampaignDefine ?? "").SequenceEqual(refresh.CampaignDefines))
                    throw new InvalidOperationException("The package or campaign context changed while indexing the dependency source.");
                var sources = capture.Sources.Where(source => source.IsAuthored && IsPathInsideCampaignRoot(source.Path)).ToArray();
                var originalSources = refresh.Baseline.Sources.ToDictionary(source => source.Path,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                if (sources.Length != originalSources.Count || sources.Any(source => !originalSources.TryGetValue(source.Path, out var old) ||
                        !SamePresentationSource(source, old) || source.Text != old.Text))
                    throw new InvalidOperationException("The package files changed while indexing the dependency source.");
                var fresh = WccCampaignDependencyAuthoringPlanner.Read(main, refresh.Baseline.ProjectAddonId, sources);
                if (!fresh.CanAdd) throw new InvalidOperationException(fresh.Diagnostic);
                WccCampaignPackageEdit? rebuilt = null;
                if (refresh.Batch is { } batch)
                {
                    foreach (var pin in batch.Pins)
                        if (FindDependencyProvider(pin.AddonId, null) is not { } provider || provider.Revision.Id != pin.RevisionId)
                            throw new InvalidOperationException("An existing pending dependency provider changed while indexing another source.");
                    rebuilt = WccCampaignPackageEdit.ForDependencies(WccCampaignDependencyAuthoringPlanner.Plan(fresh, batch.Desired), batch.Pins);
                    if (rebuilt.Identity != refresh.Edit!.Identity)
                        throw new InvalidOperationException("The pending dependency batch no longer reproduces the same package changes.");
                }
                CollectedSaveAuthorityChanging?.Invoke();
                if (!IsCurrentCampaignDependencySourceRefresh(refresh)) return false;
                _campaignDependencyBatch = refresh.Batch is { } oldBatch ? new(fresh, oldBatch.Desired, oldBatch.Pins) : null;
                _campaignPackageEditDraft = rebuilt;
                _campaignDraftBinding = rebuilt is null ? null : new(_project!, _project!.Campaign, _campaignFieldConfiguration!, _campaignFieldGeneration);
                _dependencyAuthoringCatalog = null; _dependencyAuthoringRowsVersion = -1;
                _dependencyBatchSourceRefresh = null; _campaignPackageDraftVersion++;
                binding.RefreshedVersion = _campaignPackageDraftVersion;
                RaiseCampaignFieldsChanged(); RaiseCampaignPackageAuthoringChanged();
                return true;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Status = "The dependency source could not be attached to this batch: " + failure.Message + " Pending changes were retained.";
            return false;
        }
        finally
        {
            lock (_campaignFieldGate)
                if (ReferenceEquals(_dependencyBatchSourceRefresh, refresh))
                {
                    // Keep the old recipe and binding. If the refresh changed its
                    // generation it is deliberately stale, available for Discard.
                    _dependencyBatchSourceRefresh = null; _campaignPackageDraftVersion++;
                    RaiseCampaignFieldsChanged(); RaiseCampaignPackageAuthoringChanged();
                }
        }
    }
}
