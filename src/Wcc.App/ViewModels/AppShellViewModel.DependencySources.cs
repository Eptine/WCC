using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Settings;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private bool _dependencySourceOperation;
    private string? _dependencySourceStatus;
    private CampaignDependencyEvidence? _campaignDependencyEvidence;
    private WccProject? _dependencySourceHostProject;
    private int _dependencySourceAvailabilityNotificationQueued;
    private WccProject? _dependencySourceSessionProject;
    private string? _dependencySourceSessionMain;
    private string? _dependencySourceSessionProfileId;
    private long _dependencySourceSessionVersion;
    private string[] _campaignDependencySourcePaths = [];
    private sealed record CampaignDependencyEvidence(WccProject Project, WccLibrarySnapshot Snapshot,
        WccWorkingProjectDependencyReport Report);

    private void EnsureCampaignDependencySourceSession()
    {
        var project = CampaignEditor.CurrentProject;
        var profileId = ActiveWesnothInstallationProfile?.Id;
        if (ReferenceEquals(project, _dependencySourceSessionProject) &&
            project?.SourcePath == _dependencySourceSessionMain && profileId == _dependencySourceSessionProfileId) return;
        _dependencySourceSessionProject = project;
        _dependencySourceSessionMain = project?.SourcePath;
        _dependencySourceSessionProfileId = profileId;
        _campaignDependencySourcePaths = _campaignCreationSourceSeed is { } seed && profileId == seed.ProfileId &&
            project?.SourcePath is { } seedPath && PathsEqual(seedPath, seed.MainPath) ? seed.Folders.ToArray() : [];
        _dependencySourceSessionVersion++;
        _dependencySourceStatus = null;
        _campaignDependencyEvidence = null;
        _dependencyNoticeSession = new();
        _dependencyNoticeShown = false;
        _dependencyNoticeEvidence = null;
    }

    private IReadOnlyList<string> CampaignDependencySourcePaths(WccWesnothInstallationProfile? profile)
    {
        EnsureCampaignDependencySourceSession();
        return profile?.Id == _dependencySourceSessionProfileId ? _campaignDependencySourcePaths : [];
    }

    private string DependencySourceSignature(WccWesnothInstallationProfile? profile)
    {
        var paths = CampaignDependencySourcePaths(profile);
        return _dependencySourceSessionVersion + "\u001f" + string.Join("\u001f", paths);
    }

    private void QueueCampaignDependencySourceAvailabilityNotification()
    {
        // Pending notifications may hold the scenario lock. Campaign Save takes
        // the campaign lock before invalidating the scenario: never enter the
        // other editor synchronously here. The queued callback reads current
        // eligibility only; source reports and picker hosts keep their identity.
        if (Interlocked.Exchange(ref _dependencySourceAvailabilityNotificationQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _dependencySourceAvailabilityNotificationQueued, 0);
            CampaignEditor.NotifyCampaignDependencySourceAvailabilityChanged();
            _ = ShowCampaignDependencyNoticeAsync();
        });
    }

    private WccWesnothInstallationProfile? CampaignDependencyProfile() =>
        _currentProjectMetadata?.Compatibility?.CoreRevision is not null
            ? ResolvePinnedProjectSourceResourceProfile() : ActiveWesnothInstallationProfile;

    private bool CanChangeCampaignDependencySources() => !_dependencySourceOperation &&
        !IsLibraryIndexRefreshing && !IsShellBusy && !HasPendingEditorChanges &&
        !_isCurrentProjectMetadataLoadPending && LibrarySnapshot is not null &&
        CampaignEditor.CurrentProject is { Kind: WccProjectKind.Campaign, SourcePath: not null } &&
        CampaignDependencyProfile() is { } profile && ReferenceEquals(profile, ActiveWesnothInstallationProfile);

    private void ConfigureCampaignDependencySourceHost()
    {
        EnsureCampaignDependencySourceSession();
        var project = CampaignEditor.CurrentProject;
        if (!ReferenceEquals(project, _dependencySourceHostProject))
        {
            _dependencySourceHostProject = project;
            _dependencySourceStatus = null;
        }
        var profile = CampaignDependencyProfile();
        var snapshot = LibrarySnapshot;
        WccWorkingProjectDependencyReport? report = null;
        if (project is not null && snapshot is not null && project.SourcePath is { } main)
        {
            if (_campaignDependencyEvidence is { } evidence && ReferenceEquals(evidence.Project, project) &&
                ReferenceEquals(evidence.Snapshot, snapshot)) report = evidence.Report;
            else if (FindCurrentProjectRevisionId(snapshot) is { } root && snapshot.RevisionsById.TryGetValue(root, out var revision))
            {
                var descriptor = new WccWorkingProjectDescriptor(project.Campaign?.Campaign.NameWml ?? "Campaign",
                    Path.GetDirectoryName(Path.GetFullPath(main))!, WccPackageContentKinds.Project);
                var coreId = _currentProjectMetadata?.Compatibility?.CoreRevision is { } pin
                    ? WccProjectCorePinResolver.Resolve(snapshot.Manifest, pin).Revision?.Id
                    : TryFindActiveCoreRevision(snapshot, out _, out var core) ? core.Id : (WccPackageRevisionId?)null;
                report = WccWorkingProjectDependencyResolver.Inspect(snapshot, descriptor, coreId,
                    revision.DependencyRevisionIds);
            }
        }
        var status = _dependencySourceStatus;
        if (HasPendingEditorChanges) status = "Save or discard pending edits before changing the source folders.";
        else if (profile is not null && !ReferenceEquals(profile, ActiveWesnothInstallationProfile))
            status = "Select the campaign's matching Wesnoth installation profile before changing its source folders.";
        CampaignEditor.ConfigureCampaignDependencySources(report, CampaignDependencySourcePaths(profile),
            path => ChangeCampaignDependencySourceFromHostAsync(path, true),
            path => ChangeCampaignDependencySourceFromHostAsync(path, false),
            CanChangeCampaignDependencySources, status);
        if (project is not null && snapshot is not null && report is not null &&
            ReferenceEquals(project, CampaignEditor.CurrentProject) && ReferenceEquals(snapshot, LibrarySnapshot))
            _dependencyNoticeEvidence = new(project, snapshot, report);
        QueueCampaignDependencySourceAvailabilityNotification();
    }

    private async Task ChangeCampaignDependencySourceFromHostAsync(string folder, bool attach)
    {
        if (!await ChangeCampaignDependencySourceAsync(folder, attach, null))
            throw new InvalidOperationException(_dependencySourceStatus ?? Status);
    }

    /// <summary>Adds a read-only source location for the current campaign session.
    /// This does not modify campaign WML, declarations or the selected add-on files.</summary>
    public Task<bool> AttachCampaignDependencySourceAsync(string folderPath) => ChangeCampaignDependencySourceAsync(folderPath, attach: true, dependencyBatchToken: null);

    /// <summary>Forgets an additional Library location without deleting files or declarations.</summary>
    public Task<bool> DetachCampaignDependencySourceAsync(string folderPath) => ChangeCampaignDependencySourceAsync(folderPath, attach: false, dependencyBatchToken: null);

    private bool HasOnlyCampaignDependencyBatchRefresh(object token) =>
        CampaignEditor.IsCurrentCampaignDependencySourceRefresh(token) && !HasPackageScenarioRawDraft &&
        !_campaignEditorDirty && !_scenarioEditorDirty && !_unitEditorDirty && !_libraryUnitEditorDirty &&
        !CampaignEditor.HasCampaignSourceChanges && !ScenarioEditor.HasWccInlineDrafts &&
        !ScenarioEditor.HasWccMusicScheduleDrafts && !ScenarioEditor.HasWccSourceRecipeDrafts &&
        !_isDiscardingPendingEditorChanges && !_isGlobalSaveBusy && !IsPendingNavigationDecision;

    private async Task<bool> ChangeCampaignDependencySourceAsync(string folderPath, bool attach,
        object? dependencyBatchToken)
    {
        if (!SourceOperationAllowed())
        { _dependencySourceStatus = Status = "Wait for loading and save or discard pending edits before changing dependency sources."; return false; }
        bool SourceOperationAllowed() => dependencyBatchToken is null ? CanChangeCampaignDependencySources() :
            !_dependencySourceOperation && !IsLibraryIndexRefreshing && !IsShellBusy && !_isCurrentProjectMetadataLoadPending &&
            LibrarySnapshot is not null && CampaignEditor.CurrentProject is { Kind: WccProjectKind.Campaign, SourcePath: not null } &&
            CampaignDependencyProfile() is { } selectedProfile && ReferenceEquals(selectedProfile, ActiveWesnothInstallationProfile) &&
            attach && HasOnlyCampaignDependencyBatchRefresh(dependencyBatchToken);
        var project = CampaignEditor.CurrentProject!;
        var profile = CampaignDependencyProfile()!;
        var originalPaths = CampaignDependencySourcePaths(profile).ToArray();
        var signature = DependencySourceSignature(profile);
        string folder;
        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            if (attach)
            {
                if (File.Exists(folder) && Path.GetFileName(folder) == "_main.cfg") folder = Path.GetDirectoryName(folder)!;
                if (!Directory.Exists(folder) || Directory.GetParent(folder) is null)
                    throw new ArgumentException("Select an add-on folder rather than a filesystem root.");
                // Picker validation only; the index/capture still verifies its own routes.
                for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
                        throw new IOException("Choose a source folder with ordinary directories rather than symbolic links.");
                var projectRoot = Path.GetDirectoryName(Path.GetFullPath(project.SourcePath!))!;
                if (PathsEqual(folder, projectRoot) || IsPathInsideRoot(projectRoot, folder) || IsPathInsideRoot(folder, projectRoot))
                    throw new ArgumentException("This folder overlaps the campaign. Embedded libraries already belong to its project files.");
                if (ResolveEditorAccess(folder) == WccEditorAccessMode.ReadOnly)
                    throw new ArgumentException("The protected Core installation is already provided by the installation profile.");
                if (originalPaths.Length >= 64) throw new ArgumentException("This campaign session already has 64 additional source folders.");
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { _dependencySourceStatus = Status = error.Message; return false; }

        var existing = originalPaths.FirstOrDefault(path => PathsEqual(path, folder));
        if (attach && existing is not null) { _dependencySourceStatus = Status = "This source folder is already attached to the campaign session."; return true; }
        if (!attach && existing is null) { _dependencySourceStatus = Status = "Only an explicitly attached source folder can be forgotten."; return false; }
        if (!SourceOperationAllowed() || !ReferenceEquals(project, CampaignEditor.CurrentProject) ||
            !ReferenceEquals(profile, CampaignDependencyProfile()) || signature != DependencySourceSignature(profile)) return false;

        _dependencySourceOperation = true;
        _dependencySourceStatus = attach ? "Indexing the additional source folder…" : "Refreshing sources after forgetting the folder…";
        try
        {
            ConfigureCampaignDependencySourceHost();
            // Notifications can reenter navigation. Recheck before session or index changes.
            if (!ReferenceEquals(project, CampaignEditor.CurrentProject) || !ReferenceEquals(profile, CampaignDependencyProfile()) ||
                !ReferenceEquals(profile, ActiveWesnothInstallationProfile) || signature != DependencySourceSignature(profile) || HasPendingEditorChanges &&
                (dependencyBatchToken is null || !HasOnlyCampaignDependencyBatchRefresh(dependencyBatchToken)))
                return false;
            _campaignDependencySourcePaths = attach ? originalPaths.Append(folder).ToArray()
                : originalPaths.Where(path => !PathsEqual(path, folder)).ToArray();
            _dependencySourceSessionVersion++;
            var previousSnapshot = LibrarySnapshot;
            await RefreshConfiguredLibraryIndexAsync();
            if (!ReferenceEquals(project, CampaignEditor.CurrentProject) || !ReferenceEquals(profile, CampaignDependencyProfile()) ||
                dependencyBatchToken is not null && !HasOnlyCampaignDependencyBatchRefresh(dependencyBatchToken)) return false;
            if (ReferenceEquals(previousSnapshot, LibrarySnapshot))
            { _dependencySourceStatus = Status = "The folder list changed for this session, but the source inventory could not be refreshed. " + LibraryIndexStatus; return false; }
            _dependencySourceStatus = attach
                ? "Additional source folder attached for this campaign session. The campaign's WML decides what it loads."
                : "Additional source folder forgotten. No add-on files or campaign declarations were deleted.";
            if (attach && (LibrarySnapshot is null || !LibrarySnapshot.PackagesById.Values.Any(package => package.IsAvailable &&
                    package.SourceKind == WccPackageSourceKinds.WesnothAddon && PathsEqual(package.SourceLocation, folder))))
            { _dependencySourceStatus = Status = "The folder is attached for this session, but its source could not be indexed. " + LibraryIndexStatus; return false; }
            Status = _dependencySourceStatus;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { _dependencySourceStatus = Status = "Could not update dependency sources: " + error.Message; return false; }
        finally { _dependencySourceOperation = false; ConfigureCampaignDependencySourceHost(); }
    }
}
