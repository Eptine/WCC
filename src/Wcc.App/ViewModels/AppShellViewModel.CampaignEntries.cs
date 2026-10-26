using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private WccCampaignEntryCatalogProjection? _campaignEntryMembershipSource;
    // Navigation is detached from evaluation/owner graphs for every entry.
    // Only the selected entry keeps its ordinary detailed gameplay receipts.
    private readonly Dictionary<string, WccCampaignScenarioNavigationProjection> _campaignScenarioNavigation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WccCampaignScenarioCatalogProjection> _campaignEntryMemberships = new(StringComparer.Ordinal);
    private long _campaignEntryMembershipWorkVersion;
    // Retained scenarios are display evidence only. Keep them separate from the
    // full package observations used by RAW review and unit-absence checks.
    private readonly Dictionary<string, WccCampaignScenarioInspectionCatalogProjection> _campaignScenarioInspections = new(StringComparer.Ordinal);
    private CancellationTokenSource? _campaignEntryMembershipCancellation;
    private Task _campaignEntryMembershipTask = Task.CompletedTask;
    private long _campaignEntryNavigationVersion;
    private long _campaignEntryNavigationRequestVersion;
    private int _campaignEntrySelectionPublicationDepth;
    private bool _publishingCampaignEntrySelection => Volatile.Read(ref _campaignEntrySelectionPublicationDepth) > 0;

    // Overlapping publications own independent leases. A completed older frame
    // cannot restore another frame's captured flag or unblock a newer publisher.
    private IDisposable BeginCampaignEntrySelectionPublication()
    {
        Interlocked.Increment(ref _campaignEntrySelectionPublicationDepth);
        return new CampaignEntrySelectionPublicationScope(this);
    }

    private sealed class CampaignEntrySelectionPublicationScope(AppShellViewModel owner) : IDisposable
    {
        private AppShellViewModel? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) Interlocked.Decrement(ref current._campaignEntrySelectionPublicationDepth);
        }
    }
    private ProjectScenarioFileVm? _activeCampaignScenarioRow;
    private bool _isCampaignEntryNavigationBusy;

    public bool IsCampaignEntryNavigationBusy => _isCampaignEntryNavigationBusy;
    public async Task WaitForCampaignEntriesAsync(CancellationToken cancellationToken = default)
    {
        // A field refresh can publish a new capture while the previous membership
        // task finishes. Wait for the resulting publication as well.
        while (true)
        {
            await CampaignEditor.WaitForCampaignFieldProjectionAsync(cancellationToken);
            var membershipTask = _campaignEntryMembershipTask;
            await membershipTask.WaitAsync(cancellationToken);
            await CampaignEditor.WaitForCampaignFieldProjectionAsync(cancellationToken);
            if (ReferenceEquals(membershipTask, _campaignEntryMembershipTask) && !_isCampaignEntryNavigationBusy)
                return;
        }
    }

    private void StartCampaignEntryMembershipRefresh()
    {
        var catalog = CampaignEditor.CampaignEntryCatalog;
        if (ReferenceEquals(catalog, _campaignEntryMembershipSource)) return;
        var sourceProject = CampaignEditor.CurrentProject;
        var snapshot = LibrarySnapshot;
        var previousCancellation = _campaignEntryMembershipCancellation;
        _campaignEntryMembershipCancellation = null;
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var resetVersion = ++_campaignEntryMembershipWorkVersion;
        var requestVersion = _campaignEntryNavigationRequestVersion;
        bool CurrentSource() => resetVersion == _campaignEntryMembershipWorkVersion &&
            requestVersion == _campaignEntryNavigationRequestVersion && ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) &&
            ReferenceEquals(sourceProject, CampaignEditor.CurrentProject) && ReferenceEquals(snapshot, LibrarySnapshot);
        _campaignEntryMembershipSource = catalog;
        _campaignEntryMemberships.Clear();
        _campaignScenarioInspections.Clear();
        _campaignScenarioNavigation.Clear();
        using var previewRefresh = CampaignEditor.RequiresSelectedCampaignAdapter ? ScenarioEditor.DeferPreviewRefresh() : null;
        ScenarioEditor.RevokeCampaignScenarioEvaluation();
        if (!CurrentSource()) return;
        ScenarioEditor.SetCampaignScenarioInspection(null, null, null, null, () => true);
        if (!CurrentSource()) return;
        ScenarioEditor.SetSelectedScenarioSourceBinding(null);
        if (!CurrentSource()) return;
        // A replaced source can keep navigation labels, but not its old owners,
        // projections or membership evidence while the new capture is evaluated.
        var rows = CampaignEditor.CaptureCampaignScenarioRows();
        if (rows.Any(row => row.Membership is not null || row.Navigation is not null) ||
            _activeCampaignScenarioRow?.Membership is not null || CampaignEditor.SelectedScenarioFile?.Membership is not null ||
            SelectedCampaignScenarioFile?.Membership is not null)
        {
            var hints = rows.Select(CampaignScenarioNavigationHint).ToArray();
            var activeIndex = Array.FindIndex(rows, row => ReferenceEquals(row, _activeCampaignScenarioRow));
            var campaignSelectionIndex = Array.FindIndex(rows, row => ReferenceEquals(row, CampaignEditor.SelectedScenarioFile));
            var shellSelectionIndex = Array.FindIndex(rows, row => ReferenceEquals(row, SelectedCampaignScenarioFile));
            _activeCampaignScenarioRow = activeIndex >= 0 ? hints[activeIndex] : null;
            using (BeginCampaignEntrySelectionPublication())
            {
                if (!CampaignEditor.ReplaceCampaignScenarioRows(hints, CurrentSource)) return;

                CampaignEditor.SelectedScenarioFile = campaignSelectionIndex >= 0 ? hints[campaignSelectionIndex] : null;
                if (!CurrentSource()) return;
                SelectedCampaignScenarioFile = shellSelectionIndex >= 0 ? hints[shellSelectionIndex] : null;
                if (!CurrentSource()) return;
            }
        }
        if (catalog is null) _activeCampaignScenarioRow = null;
        _campaignEntryNavigationVersion++;
        // Managed chapter includes remain authoritative after their siblings are
        // removed. The checked Save also publishes that transition before ending.
        if (catalog is null || CampaignEditor.CurrentProject is not { } project)
        { _campaignEntryMembershipTask = Task.CompletedTask; SetCampaignEntryNavigationBusy(false); ConfigureCampaignEntryInspection(); return; }
        if (!CampaignEditor.RequiresSelectedCampaignAdapter && !CampaignEditor.RequiresManagedChapterScenarioNavigation &&
            !NeedsCheckedPackageWorkspacePublication)
        {
            _campaignEntryMembershipTask = Task.CompletedTask;
            SetCampaignEntryNavigationBusy(false);
            if (!CurrentSource()) return;
            if (catalog.IsComplete && rows.Any(row => row.IsUnresolvedCampaignMembership || row.CampaignEntry is not null ||
                    row.Membership is not null || row.Navigation is not null || row.NeedsCampaignMembershipRefresh))
                RestoreOrdinaryCampaignScenarioRows(project, catalog, CurrentSource);
            else ConfigureCampaignEntryInspection();
            return;
        }

        var cancellation = _campaignEntryMembershipCancellation = new();
        SetCampaignEntryNavigationBusy(true);
        if (!CurrentSource()) return;
        // Save keeps old sibling labels until opened; an ordinary open discovers
        // all navigation with the same preprocessor, retaining only compact rows.
        var selected = CampaignEditor.SelectedCampaignEntry;
        _campaignEntryMembershipTask = BuildCampaignEntryMembershipsAsync(project, catalog, cancellation.Token,
            IsGlobalSaveBusy && selected is not null ? new[] { selected } : null);
    }

    private sealed record CampaignEntryDetail(WccCampaignScenarioCatalogProjection Membership,
        WccCampaignScenarioInspectionCatalogProjection? Inspection)
    {
        public WccCampaignScenarioNavigationProjection Navigation => Inspection is { } inspection
            ? WccCampaignScenarioCatalog.DetachNavigation(inspection)
            : WccCampaignScenarioCatalog.DetachNavigation(Membership);
    }

    private static CampaignEntryDetail ReadCampaignEntryDetail(WccCampaignEntryCatalogProjection catalog,
        WccCampaignEntry entry, CancellationToken cancellationToken)
    {
        var membership = WccCampaignScenarioCatalog.Project(catalog.Capture, entry, cancellationToken);
        var inspection = membership.HasOutputLimitFailure
            ? WccCampaignScenarioCatalog.ProjectInspection(catalog.Capture, entry, cancellationToken) : null;
        return new(membership, inspection);
    }

    private static ProjectScenarioFileVm CampaignScenarioNavigationHint(ProjectScenarioFileVm row) => new(row.Asset)
    {
        CampaignEntry = row.CampaignEntry,
        NeedsCampaignMembershipRefresh = row.CampaignEntry is not null,
        PendingDisplayName = row.DisplayName,
        PendingMembershipIdentity = row.MembershipIdentity,
        PendingScenarioDocumentIndex = CampaignScenarioDocumentIndex(row),
        ScenarioDocumentInternalId = row.ScenarioDocumentInternalId,
        CampaignGroupName = row.CampaignGroupName,
        ShowCampaignGroupHeading = row.ShowCampaignGroupHeading,
        IsUnresolvedCampaignMembership = row.IsUnresolvedCampaignMembership
    };

    private async Task BuildCampaignEntryMembershipsAsync(WccProject project,
        WccCampaignEntryCatalogProjection catalog, CancellationToken cancellationToken,
        IReadOnlyList<WccCampaignEntry>? entries)
    {
        var previousRows = CampaignEditor.CaptureCampaignScenarioRows().Select(CampaignScenarioNavigationHint).ToArray();
        var previousMembership = _activeCampaignScenarioRow?.MembershipIdentity;
        var snapshot = LibrarySnapshot;
        var navigationVersion = _campaignEntryNavigationVersion;
        var requestVersion = _campaignEntryNavigationRequestVersion;
        var workVersion = ++_campaignEntryMembershipWorkVersion;
        var selected = CampaignEditor.SelectedCampaignEntry;
        bool Current() => !cancellationToken.IsCancellationRequested &&
            workVersion == _campaignEntryMembershipWorkVersion && navigationVersion == _campaignEntryNavigationVersion &&
            requestVersion == _campaignEntryNavigationRequestVersion && ReferenceEquals(project, CampaignEditor.CurrentProject) &&
            ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) && ReferenceEquals(snapshot, LibrarySnapshot) &&
            ReferenceEquals(selected, CampaignEditor.SelectedCampaignEntry);
        await Task.Yield();
        try
        {
            if (selected is null || !Current()) return;
            var prepared = await MeasureSavePhaseAsync("Read campaign contexts and navigation", () => Task.Run(() =>
            {
                var detail = ReadCampaignEntryDetail(catalog, selected, cancellationToken);
                var navigation = new Dictionary<string, WccCampaignScenarioNavigationProjection>(StringComparer.Ordinal)
                { [selected.Identity] = detail.Navigation };
                foreach (var entry in entries ?? catalog.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(entry, selected))
                        navigation[entry.Identity] = WccCampaignScenarioCatalog.ProjectNavigation(catalog.Capture, entry, cancellationToken);
                }
                return (Detail: detail, Navigation: navigation);
            }, cancellationToken));
            if (!Current()) return;
            MeasureSavePhase("Publish campaign contexts and navigation", () =>
                PublishCampaignEntryDetails(project, catalog, prepared.Detail, prepared.Navigation, previousRows,
                    previousMembership, null, Current));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { if (Current()) Status = "Could not read campaign entry scenarios: " + exception.Message; }
        finally
        {
            if (workVersion == _campaignEntryMembershipWorkVersion)
            {
                _campaignEntryMembershipTask = Task.CompletedTask;
                SetCampaignEntryNavigationBusy(false);
            }
        }
    }

    // Publish one detailed entry plus detached sidebar metadata. Neither the
    // metadata nor its source spans are ever passed into an editing session.
    private bool PublishCampaignEntryDetails(WccProject project, WccCampaignEntryCatalogProjection catalog,
        CampaignEntryDetail detail, IReadOnlyDictionary<string, WccCampaignScenarioNavigationProjection> navigation,
        IReadOnlyList<ProjectScenarioFileVm> previousRows, string? previousMembership,
        ProjectScenarioFileVm? requestedRow, Func<bool> isCurrent)
    {
        if (!isCurrent() || !ReferenceEquals(detail.Membership.Entry, CampaignEditor.SelectedCampaignEntry)) return false;
        var rows = BuildCampaignScenarioRows(project, catalog, detail, navigation, previousRows);
        var requested = requestedRow is not null ? FindCampaignScenarioRow(rows, detail.Membership.Entry, requestedRow) : null;
        if (!isCurrent() || requestedRow is not null && requested is null) return false;
        using var previewRefresh = ScenarioEditor.DeferPreviewRefresh();
        ScenarioEditor.RevokeCampaignScenarioEvaluation();
        if (!isCurrent()) return false;
        ScenarioEditor.SetCampaignScenarioInspection(null, null, null, null, () => true);
        if (!isCurrent()) return false;
        ScenarioEditor.SetSelectedScenarioSourceBinding(null);
        if (!isCurrent()) return false;
        _campaignEntryMemberships.Clear();
        _campaignEntryMemberships[detail.Membership.Entry.Identity] = detail.Membership;
        _campaignScenarioInspections.Clear();
        if (detail.Inspection is { } inspection) _campaignScenarioInspections[inspection.Entry.Identity] = inspection;
        _campaignScenarioNavigation.Clear();
        foreach (var pair in navigation) _campaignScenarioNavigation[pair.Key] = pair.Value;
        using (BeginCampaignEntrySelectionPublication())
        {
            if (!CampaignEditor.ReplaceCampaignScenarioRows(rows, isCurrent)) return false;

            var selected = requestedRow is not null ? requested :
                FindCheckedPackageScenarioPublication(rows) ?? rows.FirstOrDefault(row =>
                    row.CampaignEntry?.Identity == detail.Membership.Entry.Identity && row.MembershipIdentity == previousMembership)
                ?? FirstCampaignEntryScenario(detail.Membership.Entry.Identity);
            if (requestedRow is not null && selected is null) return false;
            // An ordinary Save preserves an empty workspace. The scenario creation
            // command opens its new file after Save; discovering it during refresh
            // must not change the captured editing session before Save completes.
            // Package topology changes retain their exact checked transition receipt.
            if (requestedRow is null && IsGlobalSaveBusy && ScenarioEditor.CurrentScenarioAsset is null &&
                !NeedsCheckedPackageWorkspacePublication) selected = null;
            PublishCampaignEntrySelection(detail.Membership.Entry, selected);
            if (!isCurrent()) return false;
            Status = detail.Inspection is { IsComplete: true }
                ? "Read-only scenario preview: full expansion exceeded the output limit. Unit interiors were omitted for display; scenario and unit saving require complete validation."
                : detail.Membership.IsComplete
                    ? "Selected campaign scenarios are ready. Other campaigns keep compact navigation and load details when opened."
                    : "Campaign entries loaded. Some scenario memberships remain unresolved; inspect their source diagnostics.";
            return true;
        }
    }

    private List<ProjectScenarioFileVm> BuildCampaignScenarioRows(WccProject project,
        WccCampaignEntryCatalogProjection catalog, CampaignEntryDetail detail,
        IReadOnlyDictionary<string, WccCampaignScenarioNavigationProjection> navigation,
        IReadOnlyList<ProjectScenarioFileVm> previousRows)
    {
        return CampaignEditor.WithCampaignScenarioRows(_ =>
        {
            var rows = new List<ProjectScenarioFileVm>();
            foreach (var entry in catalog.Entries)
            {
                var first = true;
                if (!navigation.TryGetValue(entry.Identity, out var entryNavigation))
                {
                    foreach (var previous in previousRows.Where(row => row.CampaignEntry is { } old &&
                        CampaignEditor.ResolveCheckedCampaignEntryIdentity(old.Identity) == entry.Identity))
                    {
                        if (previous.Asset.SourcePath is not { } previousPath) continue;
                        var asset = project.ProjectScenarioFiles.FirstOrDefault(item => PathsEqual(item.SourcePath, previousPath));
                        if (asset is null) continue;
                        var documentIndex = CampaignScenarioDocumentIndex(previous);
                        rows.Add(new(asset) { CampaignEntry = entry, NeedsCampaignMembershipRefresh = true,
                            PendingDisplayName = previous.DisplayName, PendingMembershipIdentity = previous.MembershipIdentity,
                            PendingScenarioDocumentIndex = documentIndex,
                            ScenarioDocumentInternalId = documentIndex is { } index ? asset.Documents.ElementAtOrDefault(index)?.InternalId : null,
                            CampaignGroupName = entry.Name, ShowCampaignGroupHeading = catalog.Entries.Count > 1 && first });
                        first = false;
                    }
                    continue;
                }
                var detailedScenarios = ReferenceEquals(entry, detail.Membership.Entry)
                    ? detail.Inspection?.Scenarios ?? detail.Membership.Scenarios : null;
                foreach (var reached in entryNavigation.Scenarios)
                {
                    if (reached.OwnerSourcePath is not { } path || ProjectSession.RootPath is not { } projectRoot ||
                        !IsPathInsideRoot(projectRoot, path)) continue;
                    var sources = catalog.Capture.Sources.Where(source => PathsEqual(source.Path, path) &&
                        source.ContentHash == reached.OwnerSourceContentHash).Take(2).ToArray();
                    if (sources.Length != 1) continue;
                    var source = sources[0];
                    var asset = project.ProjectScenarioFiles.FirstOrDefault(item => PathsEqual(item.SourcePath, source.Path));
                    if (asset is null)
                    {
                        asset = new() { FileName = Path.GetFileName(source.Path), SourcePath = source.Path, RawTextSnapshot = source.Text };
                        asset.Documents.AddRange(ScenarioImporter.ImportAllFromText(source.Text));
                        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(source.Text));
                        project.ProjectScenarioFiles.Add(asset);
                    }
                    // The physical baseline is always authored text, never expanded output.
                    if (asset.RawTextSnapshot != source.Text) continue;
                    var document = reached.DisplayStartIndex is { } start && reached.DisplayEndIndexExclusive is { } end
                        ? asset.Documents.SingleOrDefault(item => item.BlockSpan.StartIndex == start && item.BlockSpan.EndIndexExclusive == end) : null;
                    var matches = detailedScenarios?.Where(item => item.Identity == reached.Identity).Take(2).ToArray();
                    var membership = matches is { Length: 1 } ? matches[0] : null;
                    rows.Add(new(asset) { CampaignEntry = entry, Membership = membership, Navigation = reached,
                        PendingScenarioDocumentIndex = document is not null ? asset.Documents.IndexOf(document) : null,
                        ScenarioDocumentInternalId = document?.InternalId, CampaignGroupName = entry.Name,
                        ShowCampaignGroupHeading = catalog.Entries.Count > 1 && first });
                    first = false;
                }
            }
            if (navigation.Values.Any(item => !item.IsComplete))
            {
                var firstUnresolved = true;
                foreach (var asset in project.ProjectScenarioFiles.Where(asset => !rows.Any(row => ReferenceEquals(row.Asset, asset)) &&
                    (asset.Documents.Count != 0 || asset.RawTextSnapshot is null || WccScenarioSourceCandidate.MayDeclareScenario(asset.RawTextSnapshot))))
                {
                    rows.Add(new(asset) { IsUnresolvedCampaignMembership = true,
                        CampaignGroupName = "Unresolved membership - source only", ShowCampaignGroupHeading = firstUnresolved });
                    firstUnresolved = false;
                }
            }
            return rows;
        });
    }

    private static int? CampaignScenarioDocumentIndex(ProjectScenarioFileVm row)
    {
        if (row.PendingScenarioDocumentIndex is { } pending) return pending;
        var current = row.Asset.Documents.FindIndex(document => document.InternalId == row.ScenarioDocumentInternalId);
        if (current >= 0) return current;
        var start = row.Navigation?.DisplayStartIndex ?? row.Membership?.DisplayScenarioSpan?.StartIndex;
        var end = row.Navigation?.DisplayEndIndexExclusive ?? row.Membership?.DisplayScenarioSpan?.EndIndexExclusive;
        for (var index = 0; index < row.Asset.Documents.Count; index++)
            if (row.Asset.Documents[index].BlockSpan.StartIndex == start && row.Asset.Documents[index].BlockSpan.EndIndexExclusive == end)
                return index;
        return null;
    }

    private ProjectScenarioFileVm? FindRefreshedCampaignScenarioRow(WccCampaignEntry entry, ProjectScenarioFileVm previous) =>
        FindCampaignScenarioRow(CampaignEditor.CaptureCampaignScenarioRows(), entry, previous);

    private static ProjectScenarioFileVm? FindCampaignScenarioRow(IEnumerable<ProjectScenarioFileVm> rows,
        WccCampaignEntry entry, ProjectScenarioFileVm previous)
    {
        var candidates = rows.Where(row => row.CampaignEntry?.Identity == entry.Identity &&
            !row.NeedsCampaignMembershipRefresh && ReferenceEquals(row.Asset, previous.Asset)).ToArray();
        if (previous.MembershipIdentity is { } identity)
        {
            var exact = candidates.Where(row => row.MembershipIdentity == identity).Take(2).ToArray();
            if (exact.Length == 1) return exact[0];
        }
        if (previous.Navigation is { OwnerSourcePath: { } previousPath } navigation)
        {
            var physical = candidates.Where(row => row.Navigation is { } reached &&
                reached.OwnerSourcePath is { } sourcePath && PathsEqual(sourcePath, previousPath) &&
                reached.OwnerSourceContentHash == navigation.OwnerSourceContentHash &&
                reached.DisplayStartIndex == navigation.DisplayStartIndex &&
                reached.DisplayEndIndexExclusive == navigation.DisplayEndIndexExclusive).Take(2).ToArray();
            return physical.Length == 1 ? physical[0] : null;
        }
        // A successful Save can change physical hashes and document IDs. Its
        // existing pending-label receipt carries the known authored document index.
        if (!previous.NeedsCampaignMembershipRefresh) return null;
        var index = CampaignScenarioDocumentIndex(previous);
        var document = index is { } documentIndex ? previous.Asset.Documents.ElementAtOrDefault(documentIndex) : null;
        if (document is null) return null;
        var matches = candidates.Where(row => row.ScenarioDocumentInternalId == document.InternalId).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private ProjectScenarioFileVm? FirstCampaignEntryScenario(string? identity)
    {
        var rows = CampaignEditor.CaptureCampaignScenarioRows().Where(row => row.CampaignEntry?.Identity == identity).ToArray();
        var first = CampaignEditor.CampaignEntries.FirstOrDefault(entry => entry.Identity == identity)?.FirstScenarioId;
        var matches = rows.Where(row => row.ScenarioId == first).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : rows.FirstOrDefault();
    }

    public async Task<bool> SelectCampaignEntryAsync(string identity, CancellationToken cancellationToken = default)
    {
        var row = FirstCampaignEntryScenario(identity);
        // A pending label is only a physical navigation hint. Resolve the
        // declared first_scenario after obtaining this entry's fresh membership.
        if (row?.NeedsCampaignMembershipRefresh == true) row = null;
        return await NavigateCampaignEntryAsync(identity, row, cancellationToken);
    }

    public Task<bool> OpenCampaignScenarioRowAsync(ProjectScenarioFileVm row, CancellationToken cancellationToken = default)
    {
        if (_publishingCampaignEntrySelection) return Task.FromResult(false);
        if (row.IsUnresolvedCampaignMembership) return OpenUnresolvedCampaignScenarioSourceAsync(row);
        return row.CampaignEntry is { } entry
            ? NavigateCampaignEntryAsync(entry.Identity, row, cancellationToken) : Task.FromResult(false);
    }

    private async Task<bool> OpenUnresolvedCampaignScenarioSourceAsync(ProjectScenarioFileVm row)
    {
        _campaignEntryNavigationRequestVersion++;
        var project = CampaignEditor.CurrentProject;
        var catalog = CampaignEditor.CampaignEntryCatalog;
        var version = ++_campaignEntryNavigationVersion;
        if (!await ConfirmSourceNavigationAsync() || version != _campaignEntryNavigationVersion ||
            !ReferenceEquals(project, CampaignEditor.CurrentProject) || !ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) ||
            !CampaignEditor.ScenarioFiles.Contains(row))
        { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
        using (BeginCampaignEntrySelectionPublication())
        {
            LoadScenarioDocument(row.Asset, CreateProjectEditorSession("Unresolved campaign membership: " + row.Asset.FileName));
            ScenarioEditor.SelectedScenario = null;
            _activeCampaignScenarioRow = row;
            CampaignEditor.SelectedScenarioFile = row;
            SelectedCampaignScenarioFile = row;
            Status = "This source has no proved campaign-entry membership. Use View source to inspect its authored WML.";
            RaiseScenarioWorkspaceStateChanged();
            return true;
        }
    }
    private async Task<bool> NavigateCampaignEntryAsync(string identity, ProjectScenarioFileVm? row, CancellationToken cancellationToken)
    {
        // A rejected click does not supersede an in-flight source preparation.
        if (_isCampaignEntryNavigationBusy) { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
        var requestVersion = ++_campaignEntryNavigationRequestVersion;
        var version = ++_campaignEntryNavigationVersion;
        var project = CampaignEditor.CurrentProject;
        var session = ProjectSession;
        var catalog = CampaignEditor.CampaignEntryCatalog;
        var destination = CapturePackageRawNavigationDestination(row);
        var hadRow = row is not null;
        var previousPublicationVersion = _packageRawNavigationPublicationVersion;
        var entry = catalog?.Entries.SingleOrDefault(item => item.Identity == identity);
        if (entry is null || _isCampaignEntryNavigationBusy || row is not null && !CampaignEditor.ScenarioFiles.Contains(row))
        { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
        if (CampaignEditor.SelectedCampaignEntry?.Identity == identity && row is not null &&
            ReferenceEquals(row.Asset, ScenarioEditor.CurrentScenarioAsset) &&
            row.ScenarioDocumentInternalId == ScenarioEditor.SelectedScenario?.Document.InternalId &&
            _activeCampaignScenarioRow?.MembershipIdentity == row.MembershipIdentity)
        { CampaignEditor.SelectedScenarioFile = row; SelectedCampaignScenarioFile = row; return true; }
        if (!await ConfirmSourceNavigationAsync(cancellationToken) ||
            !ReferenceEquals(project, CampaignEditor.CurrentProject) || session != ProjectSession ||
            requestVersion != _campaignEntryNavigationRequestVersion)
        { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
        if (version != _campaignEntryNavigationVersion || !ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) ||
            row is not null && !CampaignEditor.ScenarioFiles.Contains(row))
        {
            if (!TryRebindNavigationAfterPackageRawSave(project, session, catalog, identity, destination, hadRow,
                    requestVersion, version, previousPublicationVersion, out var freshEntry, out var freshRow))
            { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
            entry = freshEntry!;
            row = freshRow;
            identity = entry.Identity;
            catalog = CampaignEditor.CampaignEntryCatalog;
        }
        if (project is null || catalog is null) return false;
        if (!_campaignEntryMemberships.ContainsKey(entry.Identity))
        {
            var navigationVersion = _campaignEntryNavigationVersion;
            var workVersion = _campaignEntryMembershipWorkVersion;
            var snapshot = LibrarySnapshot;
            SetCampaignEntryNavigationBusy(true);
            if (requestVersion != _campaignEntryNavigationRequestVersion || navigationVersion != _campaignEntryNavigationVersion ||
                !ReferenceEquals(project, CampaignEditor.CurrentProject) || session != ProjectSession ||
                !ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) || !ReferenceEquals(snapshot, LibrarySnapshot))
            {
                if (workVersion == _campaignEntryMembershipWorkVersion) SetCampaignEntryNavigationBusy(false);
                RestoreCampaignScenarioSelectionToCurrentEditorAsset();
                return false;
            }
            var work = LoadCampaignEntryForNavigationAsync(project, catalog, entry, row, session,
                requestVersion, navigationVersion, cancellationToken);
            _campaignEntryMembershipTask = work;
            return await work.WaitAsync(cancellationToken);
        }
        if (row?.NeedsCampaignMembershipRefresh == true || row is not null && !CampaignEditor.ScenarioFiles.Contains(row))
        {
            row = FindRefreshedCampaignScenarioRow(entry, row);
            if (row is null) { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
        }
        else if (!hadRow) row = FirstCampaignEntryScenario(entry.Identity);
        using (BeginCampaignEntrySelectionPublication())
        {
            using var previewRefresh = ScenarioEditor.DeferPreviewRefresh();
            if (!CampaignEditor.ApplySelectedCampaignEntry(identity))
            { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
            PublishCampaignEntrySelection(entry, row);
            return CampaignEditor.SelectedCampaignEntry?.Identity == identity;
        }
    }

    private async Task<bool> LoadCampaignEntryForNavigationAsync(WccProject project,
        WccCampaignEntryCatalogProjection catalog, WccCampaignEntry entry, ProjectScenarioFileVm? requestedRow,
        WccEditorSessionContext session, long requestVersion, long navigationVersion, CancellationToken cancellationToken)
    {
        var workVersion = ++_campaignEntryMembershipWorkVersion;
        var snapshot = LibrarySnapshot;
        var previousRows = CampaignEditor.CaptureCampaignScenarioRows().Select(CampaignScenarioNavigationHint).ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _campaignEntryMembershipCancellation?.Token ?? CancellationToken.None);
        var committing = false;
        bool Current() => (committing || !cancellation.IsCancellationRequested) && workVersion == _campaignEntryMembershipWorkVersion &&
            requestVersion == _campaignEntryNavigationRequestVersion && navigationVersion == _campaignEntryNavigationVersion &&
            ReferenceEquals(project, CampaignEditor.CurrentProject) && session == ProjectSession &&
            ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) && ReferenceEquals(snapshot, LibrarySnapshot);
        await Task.Yield();
        try
        {
            var detail = await Task.Run(() => ReadCampaignEntryDetail(catalog, entry, cancellation.Token), cancellation.Token);
            if (!Current()) { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
            var navigation = new Dictionary<string, WccCampaignScenarioNavigationProjection>(_campaignScenarioNavigation, StringComparer.Ordinal)
            { [entry.Identity] = detail.Navigation };
            // Resolve the requested occurrence before retiring any current detailed
            // graph. A failed or canceled preparation leaves the old editor intact.
            var rows = BuildCampaignScenarioRows(project, catalog, detail, navigation, previousRows);
            if (!Current() || requestedRow is not null && FindCampaignScenarioRow(rows, entry, requestedRow) is null)
            { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
            using (BeginCampaignEntrySelectionPublication())
            {
                using var previewRefresh = ScenarioEditor.DeferPreviewRefresh();
                if (!Current()) { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
                // Cancellation may stop preparation. Once synchronous publication
                // starts, finish its coherent menu/scenario binding; newer sources
                // and requests still supersede every publication step.
                committing = true;
                if (!CampaignEditor.ApplySelectedCampaignEntry(entry.Identity) || !Current())
                { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
                return PublishCampaignEntryDetails(project, catalog, detail, navigation, previousRows,
                    requestedRow?.MembershipIdentity, requestedRow, Current);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { RestoreCampaignScenarioSelectionToCurrentEditorAsset(); return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (Current()) Status = "Could not read campaign entry scenarios: " + exception.Message;
            RestoreCampaignScenarioSelectionToCurrentEditorAsset();
            return false;
        }
        finally
        {
            if (workVersion == _campaignEntryMembershipWorkVersion)
            {
                _campaignEntryMembershipTask = Task.CompletedTask;
                SetCampaignEntryNavigationBusy(false);
            }
        }
    }

    private void PublishCampaignEntrySelection(WccCampaignEntry? entry, ProjectScenarioFileVm? row)
    {
        var project = CampaignEditor.CurrentProject;
        var catalog = CampaignEditor.CampaignEntryCatalog;
        var snapshot = LibrarySnapshot;
        var navigation = _campaignEntryNavigationVersion;
        var request = _campaignEntryNavigationRequestVersion;
        bool IsCurrentPublication() => ReferenceEquals(project, CampaignEditor.CurrentProject) &&
            ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) && ReferenceEquals(snapshot, LibrarySnapshot) &&
            navigation == _campaignEntryNavigationVersion && request == _campaignEntryNavigationRequestVersion &&
            ReferenceEquals(entry, CampaignEditor.SelectedCampaignEntry) && ReferenceEquals(row, _activeCampaignScenarioRow) &&
            (row is null || ReferenceEquals(row.CampaignEntry, entry) && CampaignEditor.ScenarioFiles.Contains(row));
        if (!ReferenceEquals(entry, CampaignEditor.SelectedCampaignEntry) || row is not null &&
            (!ReferenceEquals(row.CampaignEntry, entry) || !CampaignEditor.ScenarioFiles.Contains(row))) return;
        using var publicationScope = BeginCampaignEntrySelectionPublication();
        // A nested publication may replace this row while loading invokes host
        // callbacks. Install it once; an older outer frame must never rewind it.
        _activeCampaignScenarioRow = row;
        using var checkedPackageWorkspace = BeginCheckedPackageWorkspacePublication(entry, row);
        using var previewRefresh = ScenarioEditor.DeferPreviewRefresh();
        if (!IsCurrentPublication()) return;
        if (row is null) ScenarioEditor.RevokeCampaignScenarioEvaluation();
        if (!IsCurrentPublication()) return;
        _campaignFieldEvaluationPlan = entry?.Plan;
        ScenarioEditor.SetEvaluationPlan(entry?.Plan);
        if (catalog?.Capture.EntrySource is { } root)
        {
            _scenarioDifficultyCampaignMain = root;
            ScenarioEditor.ConfigureWccDifficultyFields(root,
                WccCampaignChapterAuthoringPlanner.ResolveSelectedAuthoring(_currentProjectMetadata?.Authoring, root, entry?.Plan?.CampaignDefine, catalog.Capture.Sources));
        }
        if (!IsCurrentPublication()) return;
        ConfigureCampaignEntryInspection();
        if (!IsCurrentPublication()) return;
        // The package field capture already owns this menu. Do not recapture it
        // merely because the selected entry uses another set of play defines.
        ScenarioEditor.ConfigureWccCampaignPresetHost(CampaignEditor);
        if (!IsCurrentPublication()) return;
        if (row is not null)
        {
            void Load() => MeasureSavePhase("Load campaign scenario", () =>
                LoadScenarioDocument(row.Asset, CreateProjectEditorSession("Project scenario: " + row.DisplayName)));
            if (ReferenceEquals(row, FindCheckedPackageScenarioPublication(CampaignEditor.CaptureCampaignScenarioRows())) &&
                row.ScenarioDocumentInternalId is { } documentId && _checkedPackageScenarioPublication is { } publication)
            {
                if (!ScenarioEditor.TrySynchronizeReviewedScenarioSelection(row.Asset, documentId, publication.SavedSource.Text, Load))
                    Status = "The scenario was saved, but its selected source changed before publication. Reload its source.";
            }
            else Load();
            if (!IsCurrentPublication()) return;
            if (row.ScenarioDocumentInternalId is null) ScenarioEditor.SelectedScenario = null;
            if (!IsCurrentPublication()) return;
            if (row.Membership?.Owners.All(owner => owner.Context.SemanticKey != ScenarioEditor.EvaluationContext.SemanticKey) == true)
                ScenarioEditor.SetEvaluationContext(row.Membership.Owners[0].Context);
            if (!IsCurrentPublication()) return;
        }
        else
        {
            ScenarioEditor.LoadFromText("(no resolved scenario in this entry)", "");
            if (!IsCurrentPublication()) return;
            ScenarioSession = WccEditorSessionContext.Empty;
            if (!IsCurrentPublication()) return;
            ClearCurrentScenarioDirty();
        }
        checkedPackageWorkspace?.Complete();
        ConfigureCampaignEntryInspection();
        if (!IsCurrentPublication()) return;
        CampaignEditor.SelectedScenarioFile = row;
        if (!IsCurrentPublication()) return;
        SelectedCampaignScenarioFile = row;
        if (!IsCurrentPublication()) return;
        RefreshProjectUnitWorkspaceEntries();
        if (!IsCurrentPublication()) return;
        if (UnitSession.Scope == WccEditorSessionScope.Project && UnitEditor.CurrentUnitAsset is { } unitAsset &&
            CampaignEditor.CurrentProject?.ProjectUnitFiles.Any(item => ReferenceEquals(item, unitAsset)) == true && !_allProjectOwnedUnitEntries.Any(item => ReferenceEquals(item.OwnedAsset, unitAsset) &&
                item.OwnedDocumentInternalId == UnitEditor.SelectedUnit?.Document.InternalId))
        {
            if ((_allProjectOwnedUnitEntries.FirstOrDefault(item => item.UnitTypeId == UnitEditor.CurrentSelectedUnitTypeId)
                ?? _allProjectOwnedUnitEntries.FirstOrDefault()) is { } unitRow)
            { SelectedProjectUnitEntry = unitRow; LoadOwnedProjectUnitDocument(unitRow); }
            else
            { UnitEditor.LoadFromText("(no active project unit)", ""); ClearCurrentUnitDirty(); }
        }
        RefreshCachedCampaignTitle();
        RaiseScenarioWorkspaceStateChanged();
    }

    // Retain the already hydrated owner only inside its exact checked publication.
    // Otherwise restoration follows the ordinary common loader and binding path.
    private bool HasCurrentCheckedCampaignScenarioSelection(ProjectScenarioFileVm row)
    {
        if (_isCampaignEntryNavigationBusy || HasPendingEditorChanges ||
            !ReferenceEquals(_activeCampaignScenarioRow, row) ||
            !ReferenceEquals(CampaignEditor.SelectedScenarioFile, row) || !ReferenceEquals(SelectedCampaignScenarioFile, row) ||
            row.NeedsCampaignMembershipRefresh || row.IsUnresolvedCampaignMembership ||
            row.CampaignEntry is not { Plan: { } plan } entry || !ReferenceEquals(entry, CampaignEditor.SelectedCampaignEntry) ||
            !ReferenceEquals(_campaignFieldEvaluationPlan, plan) || row.Membership is not { } membership ||
            CampaignEditor.CampaignEntryCatalog is not { Capture.EntrySource: { } main } catalog ||
            !ReferenceEquals(_campaignEntryMembershipSource, catalog) || LibrarySnapshot is not { } snapshot ||
            !_campaignEntryMemberships.TryGetValue(entry.Identity, out var scenarios) || !scenarios.IsComplete ||
            !ReferenceEquals(scenarios.Entry, entry) || !scenarios.Scenarios.Any(item => ReferenceEquals(item, membership)) ||
            !ReferenceEquals(row.Asset, ScenarioEditor.CurrentScenarioAsset) ||
            ScenarioEditor.SelectedScenario?.Document is not { } document || document.InternalId != row.ScenarioDocumentInternalId ||
            ScenarioSession != CreateProjectEditorSession("Project scenario: " + row.DisplayName) ||
            _scenarioDifficultyCampaignMain is not { } difficultyMain || !PathsEqual(difficultyMain.Path, main.Path) ||
            difficultyMain.Text != main.Text ||
            !ReferenceEquals(FindCheckedPackageScenarioPublication(CampaignEditor.CaptureCampaignScenarioRows()), row) ||
            CaptureSelectedScenarioSourceBinding(false) is not { } source || !source.IsCurrent() ||
            !ReferenceEquals(source.Asset, row.Asset) || !ReferenceEquals(source.Document, document)) return false;
        return ScenarioEditor.HasCurrentCampaignScenarioEvaluation(catalog, scenarios, membership, snapshot, plan);
    }

    private void ConfigureCampaignEntryInspection()
    {
        ScenarioEditor.SetCampaignEntryInspection(ScenarioSession.Scope == WccEditorSessionScope.Project && CampaignEditor.RequiresSelectedCampaignAdapter);
        var row = _activeCampaignScenarioRow;
        var catalog = CampaignEditor.CampaignEntryCatalog;
        var snapshot = LibrarySnapshot;
        var project = CampaignEditor.CurrentProject;
        var inspection = ScenarioSession.Scope == WccEditorSessionScope.Project && row?.CampaignEntry is { } entry &&
            entry.Identity == CampaignEditor.SelectedCampaignEntry?.Identity
            ? _campaignScenarioInspections.GetValueOrDefault(entry.Identity) : null;
        var evaluated = ScenarioSession.Scope == WccEditorSessionScope.Project && row?.CampaignEntry is { } selected &&
            selected.Identity == CampaignEditor.SelectedCampaignEntry?.Identity
            ? _campaignEntryMemberships.GetValueOrDefault(selected.Identity) : null;
        ScenarioEditor.SetCampaignScenarioEvaluation(catalog, evaluated, row?.Membership, snapshot,
            () => ReferenceEquals(project, CampaignEditor.CurrentProject) && ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) &&
                ReferenceEquals(snapshot, LibrarySnapshot) && ReferenceEquals(row, _activeCampaignScenarioRow) &&
                row?.CampaignEntry?.Identity == CampaignEditor.SelectedCampaignEntry?.Identity);
        ScenarioEditor.SetCampaignScenarioInspection(catalog?.Capture, inspection, row?.Membership, snapshot,
            () => ReferenceEquals(project, CampaignEditor.CurrentProject) && ReferenceEquals(catalog, CampaignEditor.CampaignEntryCatalog) &&
                ReferenceEquals(snapshot, LibrarySnapshot) && ReferenceEquals(row, _activeCampaignScenarioRow) &&
                row?.CampaignEntry?.Identity == CampaignEditor.SelectedCampaignEntry?.Identity);
        var scalar = CaptureSelectedScenarioSourceBinding(false);
        ScenarioEditor.SetSelectedScenarioSourceBinding(scalar is null ? null :
            new(scalar.State, scalar.Asset, scalar.Document, scalar.IsCurrent));
        if (UnitSession.Scope == WccEditorSessionScope.Project)
            UnitEditor.SetReadOnly(UnitSession.IsReadOnly || RequiresSelectedCampaignUnitInspection);
    }

    // Unit editing needs full ordinary gameplay receipts in the selected entry.
    // A complete menu or a bounded scenario inspection cannot supply that scope.
    private bool RequiresSelectedCampaignUnitInspection => CampaignEditor.RequiresSelectedCampaignAdapter &&
        (!CampaignEditor.HasSelectedCampaignEntryBinding || CampaignEditor.CampaignEntryCatalog is not { } catalog ||
            !ReferenceEquals(catalog, _campaignEntryMembershipSource) || CampaignEditor.SelectedCampaignEntry is not { } entry ||
            !_campaignEntryMemberships.TryGetValue(entry.Identity, out var membership) ||
            !ReferenceEquals(membership.Entry, entry) || !membership.IsComplete);

    private Func<UnitFileAsset, global::Wcc.Core.Units.UnitTypeDocument, bool> CreateCampaignUnitActivityPredicate()
    {
        if (!CampaignEditor.RequiresSelectedCampaignAdapter || CampaignEditor.SelectedCampaignEntry is not { } entry ||
            !_campaignEntryMemberships.TryGetValue(entry.Identity, out var membership)) return (_, _) => true;
        var context = membership.Contexts.FirstOrDefault(item => item.Context.SemanticKey == ScenarioEditor.EvaluationContext.SemanticKey);
        // Partial observations cannot establish that a unit is absent.
        if (context is not { IsComplete: true }) return (_, _) => true;

        // Match each captured source's exact text once per asset during this pass.
        // Repeating that comparison for every preceding unit owner makes multiunit
        // source files unnecessarily expensive. No owner graph escapes this refresh.
        var openingsBySource = new Dictionary<WmlStaticSource, List<int>>(ReferenceEqualityComparer.Instance);
        foreach (var owner in context.Projection.Owners)
        {
            if (owner.TagName != "unit_type" || owner.Parent is not { TagName: "units", Parent: null } ||
                owner.OpeningOrigin is not { } opening) continue;
            if (!openingsBySource.TryGetValue(opening.Source, out var offsets))
                openingsBySource.Add(opening.Source, offsets = new());
            offsets.Add(opening.StartOffset);
        }
        var sourceOpenings = openingsBySource.Select(item => (Source: item.Key, Offsets: item.Value.ToArray())).ToArray();
        var offsetsByAsset = new Dictionary<UnitFileAsset, int[]>(ReferenceEqualityComparer.Instance);
        return (asset, document) =>
        {
            if (asset.SourcePath is not { } sourcePath) return true;
            if (!offsetsByAsset.TryGetValue(asset, out var offsets))
            {
                var matchingOffsets = new List<int>();
                foreach (var source in sourceOpenings)
                    if (PathsEqual(source.Source.Path, sourcePath) && source.Source.Text == asset.RawTextSnapshot)
                        matchingOffsets.AddRange(source.Offsets);
                matchingOffsets.Sort();
                offsets = matchingOffsets.ToArray();
                offsetsByAsset.Add(asset, offsets);
            }
            var index = Array.BinarySearch(offsets, document.BlockSpan.StartIndex);
            if (index < 0) index = ~index;
            return index < offsets.Length && offsets[index] < document.BlockSpan.EndIndexExclusive;
        };
    }

    private void SetCampaignEntryNavigationBusy(bool busy)
    {
        _isCampaignEntryNavigationBusy = busy;
        OnPropertyChanged(nameof(IsCampaignEntryNavigationBusy));
        RaiseShellPresentation();
    }
}
