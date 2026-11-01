using System;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    /// <summary>Opening diagnostics observe only the current published capture, never a stale index.</summary>
    internal bool TryCaptureCampaignReadNoticeSource(WccLibrarySnapshot snapshot,
        out WccCampaignEntryCatalogProjection? catalog, out string? unavailable)
    {
        lock (_campaignFieldGate)
        {
            catalog = null; unavailable = null;
            if (_campaignUiLoading || _campaignFieldConfiguration is not { } configuration ||
                !ReferenceEquals(configuration.Snapshot, snapshot)) return false;
            if (_campaignEntryCatalog is { } captured && ReferenceEquals(_campaignEntryCatalogConfiguration, configuration)
                && (_campaignProjectionBinding is not { } binding || IsCurrentCampaignBinding(binding))
                && (captured.Capture.EntrySource is null || captured.Capture.EntrySource.Text == GetCurrentMainSourceRawWml()))
            { catalog = captured; return true; }
            if (configuration.RootRevision is null || _campaignProjectionTask.IsCompleted &&
                _campaignFieldStatus?.StartsWith("Campaign menu fields could not be resolved:", StringComparison.Ordinal) == true)
            {
                unavailable = _campaignFieldStatus ?? "The indexed campaign source and pinned Core context are unavailable.";
                return true;
            }
            return false;
        }
    }
}
