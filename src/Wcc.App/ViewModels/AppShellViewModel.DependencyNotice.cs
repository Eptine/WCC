using System;
using System.Linq;
using System.Threading.Tasks;
using Wcc.Core.Library;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private Func<CampaignDependencyNotice, Task<CampaignDependencyNoticeDecision>>? _campaignDependencyNoticeAsync;
    private CampaignDependencyEvidence? _dependencyNoticeEvidence;
    private object _dependencyNoticeSession = new();
    private bool _dependencyNoticeShown;
    private bool _dependencyNoticeInFlight;

    /// <summary>Optional desktop host. Missing sources never prevent opening or RAW access.</summary>
    public Func<CampaignDependencyNotice, Task<CampaignDependencyNoticeDecision>>? CampaignDependencyNoticeAsync
    {
        get => _campaignDependencyNoticeAsync;
        set
        {
            _campaignDependencyNoticeAsync = value;
            QueueCampaignDependencySourceAvailabilityNotification();
        }
    }

    private async Task ShowCampaignDependencyNoticeAsync()
    {
        EnsureCampaignDependencySourceSession();
        if (_dependencyNoticeShown || _dependencyNoticeInFlight ||
            CampaignDependencyNoticeAsync is not { } show || !CanChangeCampaignDependencySources() ||
            !CampaignEditor.CanAttachCampaignDependencySource ||
            _dependencyNoticeEvidence is not { } evidence ||
            !ReferenceEquals(evidence.Project, CampaignEditor.CurrentProject) ||
            !ReferenceEquals(evidence.Snapshot, LibrarySnapshot)) return;

        // Reference candidates can be optional/inactive. Only declared requirements
        // belong in the opening notice; the full inventory remains in Dependencies.
        var missing = evidence.Report.Entries.Where(entry => entry.IsDeclared &&
            entry.Status != WccWorkingProjectDependencyStatus.Resolved).ToArray();
        _dependencyNoticeShown = true;
        if (missing.Length == 0) return;
        var selection = CampaignEditor.BeginCampaignDependencySourceSelection();
        if (selection is null) return;
        var session = _dependencyNoticeSession;
        var version = _dependencySourceSessionVersion;
        var profile = CampaignDependencyProfile();
        var source = CampaignEditor.GetCurrentMainSourceRawWml();
        bool IsSameSession() => ReferenceEquals(session, _dependencyNoticeSession) &&
            ReferenceEquals(show, CampaignDependencyNoticeAsync) &&
            ReferenceEquals(evidence.Project, CampaignEditor.CurrentProject) &&
            ReferenceEquals(profile, CampaignDependencyProfile()) &&
            ReferenceEquals(profile, ActiveWesnothInstallationProfile) &&
            source == CampaignEditor.GetCurrentMainSourceRawWml();
        bool IsCurrent() => IsSameSession() && CanChangeCampaignDependencySources();

        _dependencyNoticeInFlight = true;
        try
        {
            var rows = missing.Select(entry => new CampaignDeclaredDependencyRow(entry.AddonId,
                entry.Status.ToString(), entry.Message)
            { RequiredBy = "Declared by " + entry.RequiredByAddonId, LogicalMount = entry.LogicalMount,
                SourcePath = entry.RootPath }).ToArray();
            var name = CampaignEditor.CampaignId ?? "Campaign";
            var decision = await show(new CampaignDependencyNotice(name, Array.AsReadOnly(rows))
            { SourceSelectionStatus = _dependencySourceStatus });
            // A dialog/picker is a request, never authority. Navigation, refresh,
            // new drafts and another source operation revoke its captured context.
            if (!IsCurrent() || version != _dependencySourceSessionVersion ||
                !ReferenceEquals(evidence.Snapshot, LibrarySnapshot)) return;
            foreach (var folder in decision.SourceFolders.Distinct(OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                if (!IsCurrent()) return;
                var attached = await CampaignEditor.AttachCampaignDependencySourceAsync(selection, folder);
                // Index refresh can still be publishing scenario/campaign previews.
                // Remember the same opening sequence without granting any operation
                // while those transient busy flags are set. A fresh queued notice
                // must pass the complete eligibility/snapshot guards above.
                if (!IsSameSession()) return;
                // An Add response is not Continue. Keep the opening sequence available
                // even for an invalid/already-attached folder; the next presentation
                // awaits a fresh user choice and exposes the source-operation status.
                // Each distinct folder is attempted once per response, never retried.
                _dependencyNoticeShown = false;
                if (!attached || !IsCurrent()) return;
                selection = CampaignEditor.BeginCampaignDependencySourceSelection();
                if (selection is null) return;
            }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or
                                          InvalidOperationException or ArgumentException)
        {
            if (ReferenceEquals(session, _dependencyNoticeSession))
                Status = "Could not open dependency source selection: " + error.Message;
        }
        finally
        {
            _dependencyNoticeInFlight = false;
            // A different campaign may have finished opening while this dialog
            // was awaiting a response. Offer that session its own notice later.
            QueueCampaignDependencySourceAvailabilityNotification();
        }
    }
}
