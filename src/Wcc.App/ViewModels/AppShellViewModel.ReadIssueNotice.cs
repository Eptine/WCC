using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.Services;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private Func<CampaignReadIssueNotice, Task<CampaignReadIssueNoticeDecision>>? _campaignReadIssueNoticeAsync;
    private WccProject? _readNoticeProject;
    private object _readNoticeSession = new();
    private bool _readNoticeShown;
    private bool _readNoticeInFlight;
    private bool _isCampaignReadAssessmentBusy;
    private bool _readNoticeSubscriptionsAttached;
    private (string MainPath, string? ProfileId, string[] Folders)? _campaignReadReloadSourceSeed;

    public bool IsCampaignReadAssessmentBusy => _isCampaignReadAssessmentBusy;

    private void SetCampaignReadAssessmentBusy(bool busy)
    {
        if (_isCampaignReadAssessmentBusy == busy) return;
        _isCampaignReadAssessmentBusy = busy;
        OnPropertyChanged(nameof(IsCampaignReadAssessmentBusy));
        RaiseShellPresentation();
    }

    public Func<CampaignReadIssueNotice, Task<CampaignReadIssueNoticeDecision>>? CampaignReadIssueNoticeAsync
    {
        get => _campaignReadIssueNoticeAsync;
        set
        {
            _campaignReadIssueNoticeAsync = value;
            if (value is not null && !_readNoticeSubscriptionsAttached)
            {
                _readNoticeSubscriptionsAttached = true;
                CampaignEditor.CampaignFieldProjectionPublished += (_, _) => QueueCampaignDependencySourceAvailabilityNotification();
                CampaignEditor.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName == nameof(CampaignWindowViewModel.IsCampaignUiLoading) && !CampaignEditor.IsCampaignUiLoading)
                        QueueCampaignDependencySourceAvailabilityNotification();
                };
                PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName == nameof(IsCampaignEntryNavigationBusy) && !IsCampaignEntryNavigationBusy)
                        QueueCampaignDependencySourceAvailabilityNotification();
                };
            }
            QueueCampaignDependencySourceAvailabilityNotification();
        }
    }

    private async Task ShowCampaignOpeningNoticesAsync()
    {
        await ShowCampaignDependencyNoticeAsync();
        await ShowCampaignReadIssueNoticeAsync();
    }

    private async Task ShowCampaignReadIssueNoticeAsync()
    {
        var project = CampaignEditor.CurrentProject;
        if (!ReferenceEquals(project, _readNoticeProject))
        {
            _readNoticeProject = project;
            _readNoticeSession = new();
            _readNoticeShown = false;
        }
        // No desktop host means no extra gameplay evaluation for independent
        // headless/editor consumers. Refreshes do not repeat an opening notice.
        if (_readNoticeShown || _readNoticeInFlight || CampaignReadIssueNoticeAsync is not { } show ||
            project is not { Kind: WccProjectKind.Campaign, SourcePath: { } entryPath } ||
            _dependencyNoticeInFlight || IsLibraryIndexRefreshing || IsShellBusy || HasPendingEditorChanges ||
            _isCurrentProjectMetadataLoadPending || !_currentProjectOpeningTask.IsCompleted ||
            LibrarySnapshot is not { } snapshot || IsCampaignEntryNavigationBusy) return;
        if (CampaignDependencyNoticeAsync is not null && CampaignEditor.CanAttachCampaignDependencySource && !_dependencyNoticeShown) return;
        var pinnedCore = _currentProjectMetadata?.Compatibility?.CoreRevision;
        var unavailableCorePin = pinnedCore is not null && !IsPinnedCoreRevisionAvailable(snapshot, pinnedCore)
            ? CurrentProjectCorePinStatus : null;
        var hasCurrentSource = CampaignEditor.TryCaptureCampaignReadNoticeSource(snapshot, out var catalog, out var unavailable);
        if (!hasCurrentSource)
        {
            // An unavailable exact pin can prevent the field host from ever
            // publishing a capture. It is already a conclusive current reading
            // failure, not a reason to wait indefinitely for that capture.
            if (unavailableCorePin is null) return;
            catalog = null;
            unavailable = "The campaign fields cannot be resolved with the unavailable pinned Core context.";
        }
        var profile = CampaignDependencyProfile();
        var activeProfile = ActiveWesnothInstallationProfile;
        var installationPath = profile?.InstallPath;
        var addonsPath = profile?.AddonsPath;
        var activeInstallationPath = activeProfile?.InstallPath;
        var activeAddonsPath = activeProfile?.AddonsPath;
        var profileIdentity = BuildWesnothSourceResourceProfileIdentity(profile);
        var activeIdentity = BuildWesnothSourceResourceProfileIdentity(activeProfile);
        var mainText = CampaignEditor.GetCurrentMainSourceRawWml();
        var sourceSession = _dependencySourceSessionVersion;
        var session = _readNoticeSession;
        bool SameOpening() => ReferenceEquals(session, _readNoticeSession) && ReferenceEquals(project, CampaignEditor.CurrentProject)
            && Equals(pinnedCore, _currentProjectMetadata?.Compatibility?.CoreRevision)
            && ReferenceEquals(profile, CampaignDependencyProfile()) && ReferenceEquals(activeProfile, ActiveWesnothInstallationProfile)
            && installationPath == profile?.InstallPath && addonsPath == profile?.AddonsPath
            && activeInstallationPath == activeProfile?.InstallPath && activeAddonsPath == activeProfile?.AddonsPath
            && profileIdentity == BuildWesnothSourceResourceProfileIdentity(profile)
            && activeIdentity == BuildWesnothSourceResourceProfileIdentity(activeProfile)
            && ReferenceEquals(show, CampaignReadIssueNoticeAsync) && sourceSession == _dependencySourceSessionVersion
            && mainText == CampaignEditor.GetCurrentMainSourceRawWml();
        var selectedEntry = CampaignEditor.SelectedCampaignEntry;
        bool CurrentAnalysis()
        {
            if (!SameOpening() || !ReferenceEquals(snapshot, LibrarySnapshot) ||
                !ReferenceEquals(selectedEntry, CampaignEditor.SelectedCampaignEntry) || HasPendingEditorChanges ||
                IsLibraryIndexRefreshing || _dependencyNoticeInFlight) return false;
            var ready = CampaignEditor.TryCaptureCampaignReadNoticeSource(snapshot, out var currentCatalog, out var currentUnavailable);
            return hasCurrentSource
                ? ready && ReferenceEquals(catalog, currentCatalog) && unavailable == currentUnavailable
                : !ready && pinnedCore is not null && !IsPinnedCoreRevisionAvailable(snapshot, pinnedCore);
        }
        var memberships = _campaignEntryMemberships.Values.ToArray();
        var folders = CampaignDependencySourcePaths(profile).ToArray();
        _readNoticeInFlight = true;
        try
        {
            // Keep the ordinary opening veil until the retained-context check
            // finishes. The modal host takes over before the veil is released.
            SetCampaignReadAssessmentBusy(true);
            var assessment = unavailableCorePin is not null || catalog is null
                ? new CampaignReadIssueAssessment(true, "WCC could not fully read this campaign.",
                    (unavailableCorePin is not null
                        ? "The campaign's exact pinned Core context is unavailable. WCC needs that Core to validate this campaign."
                        : unavailable ?? "The campaign source could not be captured.") +
                    "\n\nWorking fields and RAW source remain available. Run a full WCC Tester scan before editing, then reload after saving any fixes.")
                : await Task.Run(() => CampaignReadIssueAssessment.Inspect(catalog, selectedEntry, memberships));
            if (!CurrentAnalysis()) return;
            _readNoticeShown = true;
            if (!assessment.HasBlockingIssues) return;
            var request = new CampaignReadIssueNotice(CampaignEditor.CampaignId ?? "Campaign", entryPath,
                assessment.Summary, unavailableCorePin is null ? assessment.Details :
                    unavailableCorePin + "\n\n" + assessment.Details)
            {
                DependencyRoots = Array.AsReadOnly(folders), AddonsNamespacePath = addonsPath,
                InstallationPath = installationPath, ExactVersion = pinnedCore?.ExactWesnothVersion ?? catalog?.Capture.CoreVersion,
                IsCurrent = SameOpening
            };
            var response = show(request);
            SetCampaignReadAssessmentBusy(false);
            var decision = await response;
            if (!SameOpening() || decision != CampaignReadIssueNoticeDecision.Reload) return;
            await ReloadCampaignFromReadIssueNoticeAsync(request);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (SameOpening()) Status = "Could not finish the campaign reading check: " + error.Message;
        }
        finally
        {
            SetCampaignReadAssessmentBusy(false);
            _readNoticeInFlight = false;
            QueueCampaignDependencySourceAvailabilityNotification();
        }
    }

    private async Task ReloadCampaignFromReadIssueNoticeAsync(CampaignReadIssueNotice request)
    {
        if (request.IsCurrent?.Invoke() == false || !await ConfirmSourceNavigationAsync() || request.IsCurrent?.Invoke() == false) return;
        var text = await File.ReadAllTextAsync(request.EntryPath);
        if (request.IsCurrent?.Invoke() == false || HasPendingEditorChanges) return;
        _campaignReadReloadSourceSeed = (Path.GetFullPath(request.EntryPath), ActiveWesnothInstallationProfile?.Id,
            request.DependencyRoots.ToArray());
        try
        {
            await CampaignEditor.LoadFromTextAsync(request.EntryPath, text);
            await _currentProjectOpeningTask;
            await WaitForCampaignEntriesAsync();
        }
        finally { _campaignReadReloadSourceSeed = null; }
    }
}
