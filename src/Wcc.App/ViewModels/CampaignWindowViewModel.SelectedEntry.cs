using System;
using Wcc.Core.Campaigns;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private WccSelectedCampaignEntryAdapter? _selectedCampaignAdapter;

    // The adapter supplies ordinary editor inputs. Fresh coordinator review is
    // still required for every operation and physical file before persistence.
    internal bool HasSelectedCampaignEntryBinding => _selectedCampaignAdapter is { } adapter &&
        ReferenceEquals(adapter.Catalog, _campaignEntryCatalog) &&
        ReferenceEquals(adapter.Entry, _selectedCampaignEntry) &&
        _campaignProjectionBinding is { } binding && IsCurrentCampaignBinding(binding) &&
        ReferenceEquals(adapter.Menu, _campaignProjection) &&
        adapter.RootSource.Text == GetCurrentMainSourceRawWml() &&
        adapter.Plan.IsContextDiscoveryComplete;

    internal bool HasSelectedCampaignEntryDrafts => HasCampaignSourceDrafts && _selectedCampaignAdapter is not null;

    private WccCampaignEntryReviewIntent? CurrentCampaignEntryReview =>
        HasSelectedCampaignEntryBinding ? new(_selectedCampaignAdapter!.RootSource, _selectedCampaignAdapter.Entry.Identity) : null;

    private bool IsCurrentCampaignMenuSource(WmlStaticSource source) =>
        HasSelectedCampaignEntryBinding
            ? SamePresentationSource(source, _selectedCampaignAdapter!.OwnerSource)
            : source.Text == GetCurrentMainSourceRawWml() &&
                _project?.SourcePath is { } path && SamePresentationPath(source.Path, path);

    private static bool SamePresentationPath(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void PublishSavedCampaignMenuSource(string text, string path)
    {
        // Included menu text is never the package root's baseline. The shared
        // index refresh captures its new physical bytes after the checked Save.
        if (_project is not { } project || project.SourcePath is not { } rootPath || !SamePresentationPath(path, rootPath)) return;
        var document = CampaignImporter.ImportFromText(text, path);
        project.Campaign = document;
        project.CampaignRawTextSnapshot = text;
    }
}