using System;
using System.ComponentModel;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private sealed class CheckedPackageWorkspacePublication(string editIdentity, WccProject project,
        WccEditorSessionContext campaignSession, string profileIdentity, WmlStaticSource expectedRoot,
        long requestVersion, ScenarioFileAsset? originalScenario, WccEditorSessionContext originalSession)
    {
        internal string EditIdentity { get; } = editIdentity;
        internal WccProject Project { get; } = project;
        internal WccEditorSessionContext CampaignSession { get; } = campaignSession;
        internal string ProfileIdentity { get; } = profileIdentity;
        internal WmlStaticSource ExpectedRoot { get; } = expectedRoot;
        internal long RequestVersion { get; } = requestVersion;
        internal ScenarioFileAsset? OriginalScenario { get; } = originalScenario;
        internal WccEditorSessionContext OriginalSession { get; } = originalSession;
        internal ScenarioFileAsset? PublishedScenario { get; set; } = originalScenario;
        internal WccEditorSessionContext PublishedSession { get; set; } = originalSession;
        internal bool HasPublication { get; set; }
    }

    private CheckedPackageWorkspacePublication? _checkedPackageWorkspacePublication;
    private CheckedPackageWorkspaceTransition? _checkedPackageWorkspaceTransition;
    private bool _observingCheckedPackageWorkspaceSession;
    private string? _revokedCheckedPackageWorkspaceEditIdentity;
    private string? _activeCheckedPackageWorkspaceEditIdentity;

    // This begins only after Commit and exact candidate-byte checks. It is a
    // receipt for the resulting view transition, never source-write authority.
    private void RememberCheckedPackageWorkspacePublication(WccCampaignPackageEdit edit,
        WccProjectSourceEditCandidate candidate)
    {
        if (CampaignEditor.CurrentProject is not { } project ||
            CampaignEditor.CampaignPackageEditDraftIdentity != edit.Identity) return;
        var root = candidate.Files.SingleOrDefault(file => SourcePathEquals(file.Source.Path, edit.Source.Path))?.CandidateSource
            ?? edit.Source;
        _revokedCheckedPackageWorkspaceEditIdentity = null;
        _activeCheckedPackageWorkspaceEditIdentity = edit.Identity;
        _checkedPackageWorkspacePublication = new(edit.Identity, project, ProjectSession,
            BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile()), root,
            _campaignEntryNavigationRequestVersion, ScenarioEditor.CurrentScenarioAsset, ScenarioSession);
        if (_observingCheckedPackageWorkspaceSession) return;
        _observingCheckedPackageWorkspaceSession = true;
        PropertyChanged += CheckedPackageWorkspaceSessionChanged;
    }

    private void RetireCheckedPackageWorkspacePublication(bool rememberRevocation = false)
    {
        _revokedCheckedPackageWorkspaceEditIdentity = rememberRevocation ? _checkedPackageWorkspacePublication?.EditIdentity : null;
        _checkedPackageWorkspacePublication = null;
        _checkedPackageWorkspaceTransition = null;
        _activeCheckedPackageWorkspaceEditIdentity = null;
    }

    private void CheckedPackageWorkspaceSessionChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (_checkedPackageWorkspacePublication is not { } receipt) return;
        if (!ReferenceEquals(receipt.Project, CampaignEditor.CurrentProject) || receipt.CampaignSession != ProjectSession ||
            receipt.RequestVersion != _campaignEntryNavigationRequestVersion ||
            (change.PropertyName is nameof(ActiveWesnothInstallationProfile) or nameof(WesnothInstallPath)) &&
                receipt.ProfileIdentity != BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile()))
        { RetireCheckedPackageWorkspacePublication(rememberRevocation: true); return; }
        if (change.PropertyName == nameof(ScenarioSession) &&
            (_checkedPackageWorkspaceTransition is not { } transition || !ReferenceEquals(transition.Receipt, receipt) ||
                transition.ExpectedSession != ScenarioSession))
            RetireCheckedPackageWorkspacePublication(rememberRevocation: true);
    }

    private bool IsCurrentCheckedPackageWorkspacePublication(CheckedPackageWorkspacePublication receipt)
    {
        if (!ReferenceEquals(receipt, _checkedPackageWorkspacePublication)) return false;
        if (ReferenceEquals(receipt.Project, CampaignEditor.CurrentProject) && receipt.CampaignSession == ProjectSession &&
            receipt.RequestVersion == _campaignEntryNavigationRequestVersion &&
            receipt.ProfileIdentity == BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile()) &&
            CampaignEditor.CurrentProject?.SourcePath is { } path && SourcePathEquals(path, receipt.ExpectedRoot.Path) &&
            CampaignEditor.GetCurrentMainSourceRawWml() == receipt.ExpectedRoot.Text &&
            CampaignEditor.MatchesCheckedPackageAuthoringPublication(receipt.EditIdentity)) return true;
        RetireCheckedPackageWorkspacePublication(rememberRevocation: true);
        return false;
    }

    // Only this collected package Save may publish a topology-driven workspace
    // change. Keep its receipt after End for the outer navigation confirmation.
    private bool NeedsCheckedPackageWorkspacePublication => IsGlobalSaveBusy &&
        _checkedPackageWorkspacePublication is { } receipt && _activeCheckedPackageWorkspaceEditIdentity == receipt.EditIdentity &&
        IsCurrentCheckedPackageWorkspacePublication(receipt);

    private void EndCheckedPackageWorkspacePublication(string editIdentity)
    {
        if (_activeCheckedPackageWorkspaceEditIdentity == editIdentity)
            _activeCheckedPackageWorkspaceEditIdentity = null;
    }

    private CheckedPackageWorkspaceTransition? BeginCheckedPackageWorkspacePublication(WccCampaignEntry? entry,
        ProjectScenarioFileVm? row)
    {
        if (!IsGlobalSaveBusy || _checkedPackageWorkspacePublication is not { } receipt ||
            _activeCheckedPackageWorkspaceEditIdentity != receipt.EditIdentity ||
            !IsCurrentCheckedPackageWorkspacePublication(receipt) ||
            !ReferenceEquals(entry, CampaignEditor.SelectedCampaignEntry) ||
            !ReferenceEquals(receipt.PublishedScenario, ScenarioEditor.CurrentScenarioAsset) || receipt.PublishedSession != ScenarioSession)
            return null;
        if (row is not null && (!ReferenceEquals(row.CampaignEntry, entry) || !CampaignEditor.ScenarioFiles.Contains(row) ||
                !receipt.Project.ProjectScenarioFiles.Any(asset => ReferenceEquals(asset, row.Asset)) ||
                row.Asset.SourcePath is not { } sourcePath || row.Asset.RawTextSnapshot is not { } text ||
                CampaignEditor.CampaignEntryCatalog?.Capture.Sources.Count(source => SourcePathEquals(source.Path, sourcePath) && source.Text == text) != 1))
            return null;
        var expectedSession = row is null ? WccEditorSessionContext.Empty :
            CreateProjectEditorSession("Project scenario: " + row.DisplayName);
        var transition = new CheckedPackageWorkspaceTransition(this, receipt, row?.Asset, expectedSession);
        _checkedPackageWorkspaceTransition = transition;
        return transition;
    }

    private sealed class CheckedPackageWorkspaceTransition(AppShellViewModel owner,
        CheckedPackageWorkspacePublication receipt, ScenarioFileAsset? expectedScenario,
        WccEditorSessionContext expectedSession) : IDisposable
    {
        internal CheckedPackageWorkspacePublication Receipt { get; } = receipt;
        internal WccEditorSessionContext ExpectedSession { get; } = expectedSession;
        internal void Complete()
        {
            if (!ReferenceEquals(owner._checkedPackageWorkspacePublication, Receipt) ||
                !ReferenceEquals(owner._checkedPackageWorkspaceTransition, this) ||
                !owner.IsCurrentCheckedPackageWorkspacePublication(Receipt) ||
                !ReferenceEquals(owner.ScenarioEditor.CurrentScenarioAsset, expectedScenario) || owner.ScenarioSession != ExpectedSession)
                return;
            Receipt.PublishedScenario = expectedScenario;
            Receipt.PublishedSession = ExpectedSession;
            Receipt.HasPublication = true;
        }
        public void Dispose()
        {
            if (ReferenceEquals(owner._checkedPackageWorkspaceTransition, this))
                owner._checkedPackageWorkspaceTransition = null;
        }
    }

    private bool IsCheckedPackageWorkspacePublicationRevoked(string editIdentity) =>
        _revokedCheckedPackageWorkspaceEditIdentity == editIdentity;

    private bool MatchesCheckedPackageWorkspacePublication(string editIdentity, ScenarioFileAsset? originalScenario,
        WccEditorSessionContext originalSession) =>
        _checkedPackageWorkspacePublication is { HasPublication: true } receipt && receipt.EditIdentity == editIdentity &&
        IsCurrentCheckedPackageWorkspacePublication(receipt) && ReferenceEquals(receipt.OriginalScenario, originalScenario) &&
        receipt.OriginalSession == originalSession && ReferenceEquals(receipt.PublishedScenario, ScenarioEditor.CurrentScenarioAsset) &&
        receipt.PublishedSession == ScenarioSession;
}
