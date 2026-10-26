using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Compatibility;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private string[]? _campaignCreationSourceFolders;
    private (string MainPath, string? ProfileId, string[] Folders)? _campaignCreationSourceSeed;

    internal async Task<WccCampaignCreationResult?> CreateCampaignWithDependencySourcesAsync(
        WccCampaignCreationRequest request, System.Collections.Generic.IEnumerable<string> sourceFolders,
        CancellationToken cancellationToken = default)
    {
        if (_campaignCreationSourceFolders is not null) return null;
        _campaignCreationSourceFolders = sourceFolders.Select(Path.GetFullPath).Distinct(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        try { return await CreateCampaignAsync(request, cancellationToken); }
        finally { _campaignCreationSourceFolders = null; _campaignCreationSourceSeed = null; }
    }
    private bool _isCampaignCreationBusy;
    private Task _currentProjectOpeningTask = Task.CompletedTask;
    public bool IsCampaignCreationBusy => _isCampaignCreationBusy;

    public CampaignCreationViewModel? CreateCampaignWizardModel()
    {
        if (!CanInteractWithEditors || !TryGetCampaignCreationCorePin(out var pin)) return null;
        return new CampaignCreationViewModel
        {
            CoreRevision = pin,
            InstallPath = WesnothInstallPath,
            ParentDirectory = ActiveWesnothInstallationProfile?.AddonsPath ?? string.Empty
        };
    }

    private bool TryGetCampaignCreationCorePin(out WccProjectCoreRevisionPin? pin)
    {
        pin = null;
        var installation = _activeWesnothInstallation;
        if (installation is null || !WccCompatibilityPolicy.CanCreateFor(installation.CompatibilityTarget))
        {
            Status = "Select a Wesnoth 1.19/1.20 installation in Settings before creating a campaign.";
            return false;
        }
        if (LibrarySnapshot is not { } snapshot ||
            !TryFindActiveCoreRevision(snapshot, out var package, out var revision) ||
            string.IsNullOrWhiteSpace(revision.ContentFingerprint))
        {
            Status = "Refresh the selected installation's library before creating a campaign.";
            return false;
        }
        pin = new(package.Id, revision.Id, revision.ContentFingerprint,
            installation.ExactVersion, installation.CompatibilityTarget);
        return true;
    }

    public async Task<WccCampaignCreationResult?> CreateCampaignAsync(
        WccCampaignCreationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_isCampaignCreationBusy || !CanInteractWithEditors) return null;
        if (!await ConfirmSourceNavigationAsync(cancellationToken)) return null;
        if (_isCampaignCreationBusy || !CanInteractWithEditors) return null;
        if (!TryGetCampaignCreationCorePin(out var pin)) return null;
        if (request.CoreRevision != pin)
        {
            Status = "The selected Core installation changed. Reopen the campaign wizard.";
            return null;
        }
        if (LibrarySnapshot is { } snapshot && snapshot.FindEntities(WccEntityTypes.Campaign).Any(entity =>
            string.Equals(entity.ExportSymbol, request.Id, StringComparison.OrdinalIgnoreCase) &&
            snapshot.FindPackageForEntity(entity.Id)?.IsAvailable == true &&
            WccLibraryRevisionVisibility.ContainsEntity(snapshot, _activeLibraryRevisionIds, entity)))
        {
            Status = "Cannot create: this campaign ID is already used in the active installation. Choose another ID.";
            return null;
        }
        var previousProject = CampaignEditor.CurrentProject;
        var previousInstallation = _activeWesnothInstallation;
        WccCampaignCreationResult? created = null;
        _isCampaignCreationBusy = true;
        OnPropertyChanged(nameof(IsCampaignCreationBusy));
        RaiseShellPresentation();
        try
        {
            Status = "Creating campaign files...";
            if (!ReferenceEquals(previousProject, CampaignEditor.CurrentProject) || HasPendingEditorChanges ||
                !ReferenceEquals(previousInstallation, _activeWesnothInstallation))
                return null;
            created = await Task.Run(() => WccCampaignCreator.CreateAsync(request, cancellationToken), cancellationToken);
            // Keep published files if a programmatic host changes the editor during creation.
            if (!ReferenceEquals(previousProject, CampaignEditor.CurrentProject) || HasPendingEditorChanges ||
                !ReferenceEquals(previousInstallation, _activeWesnothInstallation))
            {
                Status = "Campaign created at " + created.RootPath + ". Open its _main.cfg to continue.";
                return created;
            }
            Status = "Opening the new campaign...";
            var text = await File.ReadAllTextAsync(created.MainFilePath, cancellationToken);
            var previousGuard = CampaignEditor.CampaignSourcePublicationGuard;
            System.Func<bool> guard = () => ReferenceEquals(previousProject, CampaignEditor.CurrentProject) &&
                !HasPendingEditorChanges && ReferenceEquals(previousInstallation, _activeWesnothInstallation) &&
                previousGuard?.Invoke() != false;
            CampaignEditor.CampaignSourcePublicationGuard = guard;
            if (_campaignCreationSourceFolders is { Length: > 0 } folders)
                _campaignCreationSourceSeed = (created.MainFilePath, ActiveWesnothInstallationProfile?.Id, folders);
            try { await CampaignEditor.LoadFromTextAsync(created.MainFilePath, text, cancellationToken); }
            finally
            {
                if (ReferenceEquals(CampaignEditor.CampaignSourcePublicationGuard, guard))
                    CampaignEditor.CampaignSourcePublicationGuard = previousGuard;
            }
            if (PathsEqual(CampaignEditor.CurrentProject?.SourcePath, created.MainFilePath))
            {
                await _currentProjectOpeningTask.WaitAsync(cancellationToken);
                if (!PathsEqual(CampaignEditor.CurrentProject?.SourcePath, created.MainFilePath)) return created;
                OpenCampaignMetadataInCampaignTab();
                Status = request.Mode == WccCampaignCreationMode.Empty || !request.CreateStarterScenario
                    ? "Campaign created. Add a first scenario before playing it in Wesnoth."
                    : "Campaign created with a playable starter scenario.";
            }
            else Status = "Campaign created at " + created.RootPath + ". Open its _main.cfg to continue.";
            return created;
        }
        catch (OperationCanceledException)
        {
            Status = created is null ? "Campaign creation cancelled." :
                "Campaign created at " + created.RootPath + ". Opening was cancelled.";
            return created;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Status = created is null ? "Could not create the campaign: " + exception.Message :
                "Campaign created at " + created.RootPath + ", but could not open it: " + exception.Message;
            return created;
        }
        finally
        {
            _isCampaignCreationBusy = false;
            OnPropertyChanged(nameof(IsCampaignCreationBusy));
            RaiseShellPresentation();
        }
    }
}
