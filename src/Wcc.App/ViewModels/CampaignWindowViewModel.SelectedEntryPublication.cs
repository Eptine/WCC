using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private sealed record SavedCampaignEntryPublication(WccProject Project, WccEditorSessionContext Session,
        string? ProfileIdentity, string BeforeIdentity, WmlStaticSource ExpectedRoot,
        IReadOnlyDictionary<string, Func<WccCampaignEntry, bool>> Matches)
    {
        internal string? PublishedIdentity { get; set; }
        internal string? PackageEditIdentity { get; init; }
        internal string? TargetCompatibility { get; init; }
    }
    private SavedCampaignEntryPublication? _savedCampaignEntryPublication;
    private bool _refreshingCheckedCampaignProjection;
    private CheckedCampaignEntryDisplayPublication? _checkedCampaignEntryDisplayPublication;

    // The collected host enters this scope only after Commit and candidate readback.
    // It permits adoption of an exact fresh display plan without inventing a campaign
    // contribution or granting another edit/write authority to the committed sources.
    internal IDisposable? BeginCheckedCampaignEntryDisplayPublication(WccProjectSourceEditCandidate candidate)
    {
        lock (_campaignFieldGate)
        {
            var previous = _savedCampaignEntryPublication;
            RememberCheckedCampaignEntry(candidate);
            if (_savedCampaignEntryPublication is not { } receipt || ReferenceEquals(previous, receipt)) return null;
            var publication = new CheckedCampaignEntryDisplayPublication(this, receipt);
            _checkedCampaignEntryDisplayPublication = publication;
            return publication;
        }
    }

    private bool HasActiveCheckedCampaignEntryDisplayPublication =>
        _checkedCampaignEntryDisplayPublication is { } publication &&
        ReferenceEquals(publication.Receipt, _savedCampaignEntryPublication) &&
        ReferenceEquals(publication.Receipt.Project, _project) &&
        _campaignFieldConfiguration is { } configuration && configuration.Session == publication.Receipt.Session &&
        configuration.RuntimeIdentity == publication.Receipt.ProfileIdentity &&
        _project?.SourcePath is { } path && SamePresentationPath(path, publication.Receipt.ExpectedRoot.Path) &&
        GetCurrentMainSourceRawWml() == publication.Receipt.ExpectedRoot.Text &&
        _selectedCampaignEntry is { } selected &&
        (selected.Identity == publication.Receipt.BeforeIdentity || selected.Identity == publication.Receipt.PublishedIdentity);

    private sealed class CheckedCampaignEntryDisplayPublication(CampaignWindowViewModel owner,
        SavedCampaignEntryPublication receipt) : IDisposable
    {
        internal SavedCampaignEntryPublication Receipt { get; } = receipt;
        public void Dispose()
        {
            lock (owner._campaignFieldGate)
                if (ReferenceEquals(owner._checkedCampaignEntryDisplayPublication, this))
                    owner._checkedCampaignEntryDisplayPublication = null;
        }
    }

    // A committed menu Save must publish its fresh view before the busy Save
    // ends. This scope admits only that publication into the ordinary capture
    // pipeline; it never enables user input or grants persistence authority.
    internal Task RefreshCheckedCampaignEntryProjectionAsync()
    {
        lock (_campaignFieldGate)
        {
            if (!IsCampaignFieldSaveBusy || _savedCampaignEntryPublication is not { } receipt ||
                !ReferenceEquals(receipt.Project, _project) || _campaignFieldConfiguration is not { } configuration ||
                configuration.Session != receipt.Session || configuration.RuntimeIdentity != receipt.ProfileIdentity ||
                !configuration.Session.CanWrite || IsReadOnly || HasCampaignSourceDrafts || _campaignHasRawSourceChanges ||
                _collectedSaveRecoveryPending || _campaignFieldSession.Recovery is not null ||
                _project?.SourcePath is not { } rootPath || !SamePresentationPath(rootPath, receipt.ExpectedRoot.Path) ||
                GetCurrentMainSourceRawWml() != receipt.ExpectedRoot.Text ||
                _selectedCampaignEntry?.Identity is not { } selected ||
                selected != receipt.BeforeIdentity && selected != receipt.PublishedIdentity)
                return Task.CompletedTask;
            _refreshingCheckedCampaignProjection = true;
            try
            {
                // Package receipts also cover changed manifests with unchanged main WML.
                if (receipt.PackageEditIdentity is not null) _campaignEntryCatalogConfiguration = null;
                RevokeCampaignFieldAuthority();
                StartCampaignFieldProjection();
                return _campaignProjectionTask;
            }
            finally { _refreshingCheckedCampaignProjection = false; }
        }
    }

    // This receipt restores view selection only after our own successful commit.
    // It cannot authorize a recipe or make an arbitrary refreshed owner writable.
    internal void RememberCheckedCampaignEntry(WccProjectSourceEditCandidate candidate)
    {
        if (_project is not { } project || _campaignFieldConfiguration is not { } configuration) return;
        if (_campaignPackageEditDraft is { } packageEdit && _campaignEntryCatalog is { } catalog &&
            _selectedCampaignEntry is { Plan.CampaignDefine: { } selectedDefine } selected &&
            catalog.Capture.EntrySource is { } beforeRoot && SamePresentationSource(beforeRoot, packageEdit.Source) &&
            beforeRoot.Text == packageEdit.Source.Text)
        {
            var root = candidate.Files.SingleOrDefault(file => SamePresentationPath(file.Source.Path, packageEdit.Source.Path))?.CandidateSource
                ?? packageEdit.Source;
            var logicalMappings = catalog.Entries.ToDictionary(entry => entry.Identity,
                entry => new Func<WccCampaignEntry, bool>(fresh => entry.Plan?.CampaignDefine is { } define && fresh.Plan?.CampaignDefine == define), StringComparer.Ordinal);
            // A checked chapter removal intentionally retires the selected owner.
            // Choose its successor only from the exact committed managed topology.
            var prospectiveSources = catalog.Capture.Sources.ToDictionary(source => source.Path,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var file in candidate.Files) prospectiveSources[file.Source.Path] = file.CandidateSource;
            foreach (var file in candidate.SourceCreates) prospectiveSources[file.Path] = file;
            foreach (var file in packageEdit.SourceDeletes) prospectiveSources.Remove(file.Path);
            var chapters = WccCampaignChapterAuthoringPlanner.Read(root, CampaignPackageAuthoringMetadata, prospectiveSources.Values.ToArray());
            if (chapters.CanEdit)
            {
                // Prefer a newly created chapter only after this exact package
                // candidate committed. The ordinary membership publication then
                // opens its declared first scenario (or its empty workspace).
                var added = chapters.Chapters.Where(chapter => catalog.Entries.All(entry =>
                    entry.Plan?.CampaignDefine != chapter.CampaignDefine)).Take(2).ToArray();
                if (added.Length == 1)
                    logicalMappings[selected.Identity] = fresh => fresh.Plan?.CampaignDefine == added[0].CampaignDefine;
                else if (chapters.Chapters.All(chapter => chapter.CampaignDefine != selectedDefine) &&
                    chapters.Chapters.FirstOrDefault() is { } survivor)
                    logicalMappings[selected.Identity] = fresh => fresh.Plan?.CampaignDefine == survivor.CampaignDefine;
            }
            _savedCampaignEntryPublication = new(project, configuration.Session, configuration.RuntimeIdentity,
                selected.Identity, root, logicalMappings) { PackageEditIdentity = packageEdit.Identity, TargetCompatibility = catalog.Menu.Context.TargetCompatibility };
            return;
        }
        if (!HasSelectedCampaignEntryBinding || _selectedCampaignAdapter is not { } adapter) return;
        var mappedRoot = candidate.Files.FirstOrDefault(file => SamePresentationPath(file.Source.Path, adapter.RootSource.Path))?.CandidateSource
            ?? adapter.RootSource;
        var mappings = adapter.Catalog.Entries.ToDictionary(entry => entry.Identity,
            entry => new Func<WccCampaignEntry, bool>(fresh => MatchesCheckedCampaignEntry(entry, fresh, candidate)), StringComparer.Ordinal);
        _savedCampaignEntryPublication = new(project, configuration.Session, configuration.RuntimeIdentity,
            adapter.Entry.Identity, mappedRoot, mappings) { TargetCompatibility = adapter.Menu.Context.TargetCompatibility };
    }

    // Re-discovery after our own commit must retain the evaluated target. The
    // captured Core version (for example 1.19.28) can differ from the package's
    // authoring target (1.20); switching between them changes context identities.
    // This receipt restores display only and never relaxes source/context review.
    internal string? CheckedCampaignEntryTargetCompatibility
    {
        get
        {
            lock (_campaignFieldGate)
                return (IsCampaignFieldSaveBusy || HasActiveCheckedCampaignEntryDisplayPublication) && _savedCampaignEntryPublication is { } receipt &&
                    ReferenceEquals(receipt.Project, _project) && _campaignFieldConfiguration is { } configuration &&
                    configuration.Session == receipt.Session && configuration.RuntimeIdentity == receipt.ProfileIdentity &&
                    _project?.SourcePath is { } path && SamePresentationPath(path, receipt.ExpectedRoot.Path) &&
                    GetCurrentMainSourceRawWml() == receipt.ExpectedRoot.Text && _selectedCampaignEntry is { } selected &&
                    (selected.Identity == receipt.BeforeIdentity || selected.Identity == receipt.PublishedIdentity)
                        ? receipt.TargetCompatibility : null;
        }
    }
    internal string? CampaignPackageEditDraftIdentity => _campaignPackageEditDraft?.Identity;

    // Save may add the adapter to a single-entry package or remove its selected
    // owner. This receipt permits only that checked view transition, not a new edit.
    internal bool MatchesCheckedPackageAuthoringPublication(string editIdentity) =>
        _savedCampaignEntryPublication is { PackageEditIdentity: { } savedEdit, PublishedIdentity: { } published } receipt &&
        savedEdit == editIdentity && _campaignEntryCatalog is { } catalog &&
        IsCurrentCheckedCampaignEntryPublication(receipt, catalog) && _project?.SourcePath is { } path &&
        SamePresentationPath(path, receipt.ExpectedRoot.Path) && GetCurrentMainSourceRawWml() == receipt.ExpectedRoot.Text &&
        _selectedCampaignEntry is { } selected && selected.Identity == published &&
        receipt.Matches.TryGetValue(receipt.BeforeIdentity, out var matches) && catalog.Entries.Count(matches) == 1 && matches(selected);

    private static bool MatchesCheckedCampaignEntry(WccCampaignEntry before, WccCampaignEntry fresh,
        WccProjectSourceEditCandidate candidate)
    {
        bool SameRange(WmlStaticSourceRange? old, WmlStaticSourceRange? current)
        {
            if (old is null || current is null) return old is null && current is null;
            if (candidate.MapRange(old) is not { } mapped) return false;
            return SamePresentationSource(mapped.Source, current.Source) && mapped.Source.Text == current.Source.Text &&
                mapped.StartOffset == current.StartOffset && mapped.Length == current.Length;
        }
        if (before.OwnerSource is null || before.AuthoredCampaignSpan is null ||
            fresh.OwnerSource is null || fresh.AuthoredCampaignSpan is null ||
            !SameRange(before.Owner.OpeningOrigin, fresh.Owner.OpeningOrigin) ||
            !SameRange(before.Owner.ClosingOrigin, fresh.Owner.ClosingOrigin) ||
            before.Owner.OpeningExpansionStack.Count != fresh.Owner.OpeningExpansionStack.Count) return false;
        // Context metadata may be unavailable in a sibling. Physical navigation
        // does not borrow validity or a gameplay scope from that sibling.
        if (before.Plan is { } oldPlan && fresh.Plan is { } plan &&
            !plan.Contexts.Select(context => context.SemanticKey).SequenceEqual(oldPlan.Contexts.Select(context => context.SemanticKey))) return false;
        for (var index = 0; index < before.Owner.OpeningExpansionStack.Count; index++)
        {
            var oldFrame = before.Owner.OpeningExpansionStack[index];
            var newFrame = fresh.Owner.OpeningExpansionStack[index];
            if (oldFrame.MacroName != newFrame.MacroName || !SameRange(oldFrame.Definition, newFrame.Definition) ||
                oldFrame.InvocationOrigins.Count != newFrame.InvocationOrigins.Count) return false;
            for (var origin = 0; origin < oldFrame.InvocationOrigins.Count; origin++)
                if (!SameRange(oldFrame.InvocationOrigins[origin], newFrame.InvocationOrigins[origin])) return false;
        }
        return true;
    }

    private bool IsCurrentCheckedCampaignEntryPublication(SavedCampaignEntryPublication receipt,
        WccCampaignEntryCatalogProjection catalog) => ReferenceEquals(receipt.Project, _project) &&
        _campaignFieldConfiguration is { } configuration && configuration.Session == receipt.Session &&
        configuration.RuntimeIdentity == receipt.ProfileIdentity &&
        catalog.Capture.EntrySource is { } root && SamePresentationSource(root, receipt.ExpectedRoot) &&
        root.Text == receipt.ExpectedRoot.Text;

    private WccCampaignEntry? FindCheckedCampaignEntryPublication(WccCampaignEntryCatalogProjection catalog)
    {
        if (_savedCampaignEntryPublication is not { } receipt || !IsCurrentCheckedCampaignEntryPublication(receipt, catalog) ||
            _selectedCampaignEntry?.Identity is not { } selected || selected != receipt.BeforeIdentity && selected != receipt.PublishedIdentity ||
            !receipt.Matches.TryGetValue(receipt.BeforeIdentity, out var match)) return null;
        var matches = catalog.Entries.Where(match).Take(2).ToArray();
        if (matches.Length != 1) return null;
        receipt.PublishedIdentity = matches[0].Identity;
        return matches[0];
    }

    internal string? ResolveCheckedCampaignEntryIdentity(string beforeIdentity)
    {
        if (_savedCampaignEntryPublication is { } receipt && _campaignEntryCatalog is { } catalog &&
            IsCurrentCheckedCampaignEntryPublication(receipt, catalog) && receipt.Matches.TryGetValue(beforeIdentity, out var match))
        {
            var matches = catalog.Entries.Where(match).Take(2).ToArray();
            return matches.Length == 1 ? matches[0].Identity : null;
        }
        return _campaignEntryCatalog?.Entries.SingleOrDefault(entry => entry.Identity == beforeIdentity)?.Identity;
    }
}