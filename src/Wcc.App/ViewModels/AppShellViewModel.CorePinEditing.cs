using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Wcc.Core.Compatibility;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private bool _isProjectCorePinChanging;
    public bool IsProjectCorePinChanging => _isProjectCorePinChanging;

    private bool CanChangeProjectCorePin => !IsProjectCorePinChanging && !IsShellBusy &&
        !_isCurrentProjectMetadataLoadPending && _currentProjectMetadata is not null &&
        CampaignEditor.CurrentProject is { Kind: WccProjectKind.Campaign } &&
        !string.IsNullOrWhiteSpace(_currentProjectMetadataRoot) && ProjectSession.CanWrite &&
        PathsEqual(ProjectSession.RootPath, _currentProjectMetadataRoot!) && LibrarySnapshot is not null;

    public bool CanUnpinCurrentProjectCore => CanChangeProjectCorePin &&
        _currentProjectMetadata?.Compatibility?.CoreRevision is not null;
    public string ProjectCorePinActionLabel => _currentProjectMetadata?.Compatibility?.CoreRevision is null
        ? "Pin to Active Wesnoth" : "Re-pin to Active Wesnoth";
    public string ActiveProjectCoreTargetLabel => _activeWesnothInstallation is { } installation
        ? $"Active installation: Wesnoth {installation.ExactVersion} (target {installation.CompatibilityTarget})."
        : "Select and index a Wesnoth installation to pin this project.";

    private bool CanPinProjectCore => CanChangeProjectCorePin && TryGetActiveProjectCorePin(out var pin) &&
        (!Equals(_currentProjectMetadata?.Compatibility?.CoreRevision, pin) ||
         _currentProjectMetadata?.Compatibility?.TargetWesnothVersion != pin!.TargetWesnothVersion);

    private bool TryGetActiveProjectCorePin(out WccProjectCoreRevisionPin? pin)
    {
        pin = null;
        if (_activeWesnothInstallation is not { } installation ||
            !WccCompatibilityPolicy.CanCreateFor(installation.CompatibilityTarget) ||
            LibrarySnapshot is not { } snapshot ||
            !TryFindActiveCoreRevision(snapshot, out var package, out var revision) || !package.IsAvailable ||
            package.CurrentRevisionId != revision.Id || revision.ContentFingerprint is not { Length: 64 } fingerprint ||
            !fingerprint.All(char.IsAsciiHexDigit)) return false;
        pin = new(package.Id, revision.Id, revision.ContentFingerprint, installation.ExactVersion, installation.CompatibilityTarget);
        return true;
    }

    public Task UnpinCurrentProjectCoreAsync() => ChangeCurrentProjectCorePinAsync(unpin: true);

    private async Task ChangeCurrentProjectCorePinAsync(bool unpin)
    {
        if (!CanChangeProjectCorePin || !unpin && !TryGetActiveProjectCorePin(out _))
        {
            Status = "The project Core setting cannot be changed in its present state. Select and index an authoring installation, and check project access.";
            return;
        }
        var project = CampaignEditor.CurrentProject;
        var requestedMainPath = project?.SourcePath;
        var root = _currentProjectMetadataRoot!;
        var requestedProjectId = _currentProjectMetadata!.ProjectId;
        var originalPin = _currentProjectMetadata!.Compatibility?.CoreRevision;
        var profile = ActiveWesnothInstallationProfile;
        var profileIdentity = BuildWesnothRuntimeProfileIdentity(profile);
        var installPath = profile?.InstallPath;
        var addonsPath = profile?.AddonsPath;
        var installation = _activeWesnothInstallation;
        WccProjectCoreRevisionPin? desiredPin = null;
        if (!unpin && !TryGetActiveProjectCorePin(out desiredPin)) return;
        if (Equals(originalPin, desiredPin) && (unpin ||
            _currentProjectMetadata.Compatibility?.TargetWesnothVersion == desiredPin!.TargetWesnothVersion)) return;
        if (!await ConfirmSourceNavigationAsync()) return;
        bool SameRequest() => ReferenceEquals(project, CampaignEditor.CurrentProject) &&
            requestedMainPath == CampaignEditor.CurrentProject?.SourcePath &&
            PathsEqual(_currentProjectMetadataRoot, root) && ProjectSession.CanWrite &&
            !_isCurrentProjectMetadataLoadPending && _currentProjectMetadata?.ProjectId == requestedProjectId &&
            Equals(originalPin, _currentProjectMetadata?.Compatibility?.CoreRevision) &&
            ReferenceEquals(profile, ActiveWesnothInstallationProfile) &&
            installPath == profile?.InstallPath && addonsPath == profile?.AddonsPath &&
            profileIdentity == BuildWesnothRuntimeProfileIdentity(profile) &&
            Equals(installation, _activeWesnothInstallation);
        if (!SameRequest())
        {
            Status = "The project or active installation changed. Choose the Core setting again.";
            return;
        }
        // Discard restores saved editor baselines and can queue fresh campaign
        // and scenario projections. Join that same owner's publication before
        // requiring an idle host; identity and draft guards are checked again.
        try
        {
            await WaitForCampaignEntriesAsync();
            await ScenarioEditor.WaitForScenarioUiLoadingAsync();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or ArgumentException)
        {
            Status = "Could not finish refreshing the project before changing its Core setting: " + error.Message;
            return;
        }
        if (!SameRequest() || !CanChangeProjectCorePin ||
            !unpin && (!TryGetActiveProjectCorePin(out var currentPin) || !Equals(currentPin, desiredPin)))
        {
            Status = "The project or active installation changed. Choose the Core setting again.";
            return;
        }
        var metadata = _currentProjectMetadata!;
        var snapshot = LibrarySnapshot;
        var session = ProjectSession;
        var mainText = CampaignEditor.GetCurrentMainSourceRawWml();
        bool Current() => ReferenceEquals(project, CampaignEditor.CurrentProject) &&
            requestedMainPath == CampaignEditor.CurrentProject?.SourcePath &&
            PathsEqual(_currentProjectMetadataRoot, root) && ReferenceEquals(metadata, _currentProjectMetadata) &&
            ProjectSession == session && ProjectSession.CanWrite && ReferenceEquals(snapshot, LibrarySnapshot) &&
            !_isCurrentProjectMetadataLoadPending && !HasPendingEditorChanges && !IsLibraryIndexRefreshing &&
            !IsCampaignEntryNavigationBusy && !IsGlobalSaveBusy && !IsPendingNavigationDecision &&
            !CampaignEditor.IsCampaignFieldSaveBusy && !CampaignEditor.IsCampaignSourceLoading &&
            !CampaignEditor.IsCampaignUiLoading && !ScenarioEditor.IsScenarioUiLoading && !IsEditorSourceLoading &&
            mainText == CampaignEditor.GetCurrentMainSourceRawWml() &&
            Equals(originalPin, _currentProjectMetadata?.Compatibility?.CoreRevision) &&
            ReferenceEquals(profile, ActiveWesnothInstallationProfile) &&
            installPath == profile?.InstallPath && addonsPath == profile?.AddonsPath &&
            profileIdentity == BuildWesnothRuntimeProfileIdentity(profile) &&
            Equals(installation, _activeWesnothInstallation);
        var saved = false;
        SetProjectCorePinChanging(true);
        try
        {
            await _projectMetadataWriteLock.WaitAsync();
            try
            {
                if (!Current()) throw new InvalidOperationException("The project or editing context changed before its Core setting could be saved.");
                var updated = await new JsonWccProjectMetadataStore(root).UpdateCoreRevisionPinAsync(
                    metadata.ProjectId, originalPin, desiredPin, Current);
                saved = true;
                if (!Current())
                {
                    Status = "The original project's Core setting was saved. Reopen that project to use its updated setting.";
                    return;
                }
                ApplyProjectMetadata(root, updated);
                _readNoticeSession = new();
                _readNoticeShown = false;
            }
            finally { _projectMetadataWriteLock.Release(); }
            await RefreshConfiguredLibraryIndexAsync();
            await CampaignEditor.WaitForCampaignFieldProjectionAsync();
            await WaitForCampaignEntriesAsync();
            if (ReferenceEquals(project, CampaignEditor.CurrentProject) && PathsEqual(_currentProjectMetadataRoot, root))
                Status = unpin ? "The project now follows the active Wesnoth installation."
                    : $"Pinned the project to exact Core build {desiredPin!.ExactWesnothVersion} (target {desiredPin.TargetWesnothVersion}).";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            Status = saved ? "The Core setting was saved, but the campaign could not finish refreshing: " + error.Message
                : "Could not change the project Core setting: " + error.Message;
        }
        finally
        {
            SetProjectCorePinChanging(false);
            QueueCampaignDependencySourceAvailabilityNotification();
        }
    }

    private void SetProjectCorePinChanging(bool changing)
    {
        if (_isProjectCorePinChanging == changing) return;
        _isProjectCorePinChanging = changing;
        OnPropertyChanged(nameof(IsProjectCorePinChanging));
        RaiseProjectCompatibilityPropertiesChanged();
        RaiseShellPresentation();
    }
}
