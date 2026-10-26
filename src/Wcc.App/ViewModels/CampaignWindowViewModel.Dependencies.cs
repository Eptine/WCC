using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public sealed record CampaignDeclaredDependencyRow(string Id, string Status, string Detail)
{
    public bool IsDeclared { get; init; } = true;
    /// <summary>A local provider observed in this captured phase; external requirements keep their own status.</summary>
    public bool HasCapturedContainedSource { get; init; }
    public string? RequiredBy { get; init; }
    public string? LogicalMount { get; init; }
    public string? SourcePath { get; init; }
}
public sealed record CampaignEditorDependencyRow(string Label, string Kind, string? SourcePath,
    string Status, string Revision, bool IsDirect)
{
    public string? LogicalMount { get; init; }
    public string? Detail { get; init; }
}

public sealed record CampaignAdditionalDependencySourceRow(string SourcePath, bool CanForget);

/// <summary>Revocable context for the asynchronous folder picker, never write authority.</summary>
public sealed class CampaignDependencySourceSelection
{
    internal CampaignDependencySourceSelection(CampaignWindowViewModel owner, object host,
        WccProject project, CampaignDocument? document, long generation, string source)
    { Owner = owner; Host = host; Project = project; Document = document; Generation = generation; Source = source; }
    internal CampaignWindowViewModel Owner { get; }
    internal object Host { get; }
    internal WccProject Project { get; }
    internal CampaignDocument? Document { get; }
    internal long Generation { get; }
    internal string Source { get; }
}

public partial class CampaignWindowViewModel
{
    private CampaignDraftBinding? _dependencyPresentationBinding;
    private IReadOnlyList<CampaignDeclaredDependencyRow> _declaredDependencyRows = [];
    private IReadOnlyList<CampaignEditorDependencyRow> _editorDependencyRows = [];
    private IReadOnlyList<CampaignDeclaredDependencyRow> _projectProvidedDependencyRows = [];
    private CampaignFieldSourceLocation? _dependencyMetadataLocation;
    private string _dependencyMetadataSummary = "No indexed dependency metadata is available.";
    private CampaignDependencySourceHost? _dependencySourceHost;
    private CampaignDependencySourceHost? _dependencyPresentationHost;
    private bool _dependencyPresentationCanChange;
    private IReadOnlyList<CampaignAdditionalDependencySourceRow> _additionalDependencySourceRows = [];
    private bool _dependencySourceOperationBusy;

    public IReadOnlyList<CampaignDeclaredDependencyRow> CampaignDeclaredDependencyRows
    { get { lock (_campaignFieldGate) { RefreshCampaignDependencyPresentation(); return _declaredDependencyRows; } } }
    public IReadOnlyList<CampaignEditorDependencyRow> CampaignEditorDependencyRows
    { get { lock (_campaignFieldGate) { RefreshCampaignDependencyPresentation(); return _editorDependencyRows; } } }
    public IReadOnlyList<CampaignDeclaredDependencyRow> CampaignProjectProvidedDependencyRows
    { get { lock (_campaignFieldGate) { RefreshCampaignDependencyPresentation(); return _projectProvidedDependencyRows; } } }
    public IReadOnlyList<WccCampaignContainedSourceObservation> CampaignContainedSourceRows => CurrentContainedSourceObservations();
    public bool HasCampaignProjectProvidedDependencies => CampaignProjectProvidedDependencyRows.Count != 0;
    public bool HasCampaignContainedSources => CampaignContainedSourceRows.Count != 0;
    public CampaignFieldSourceLocation? CampaignDependencyMetadataLocation
    { get { lock (_campaignFieldGate) { RefreshCampaignDependencyPresentation(); return _dependencyMetadataLocation; } } }
    public bool HasCampaignDependencyMetadataSource => CampaignDependencyMetadataLocation is not null;
    public string CampaignDependencyMetadataSummary
    { get { lock (_campaignFieldGate) { RefreshCampaignDependencyPresentation(); return _dependencyMetadataSummary; } } }
    public string CampaignDependenciesGuidance => "Declared add-on requirements and captured source references are checked against this installation profile. Campaign WML determines which dependency files and macros are loaded. Managed loading recipes can be added below. Existing unmarked loading code and download declarations remain available in RAW WML.";
    public string CampaignEditorSourcesGuidance => "These exact package revisions are visible to the editor. Availability does not prove that their WML is activated in the game. Core, project files and dependency sources retain separate origins.";
    public string? CampaignProjectSourcePath => _project?.SourcePath;
    public IReadOnlyList<CampaignAdditionalDependencySourceRow> CampaignAdditionalDependencySourceRows
    { get { lock (_campaignFieldGate) { RefreshCampaignDependencyPresentation(); return _additionalDependencySourceRows; } } }
    public string CampaignAdditionalDependencySourcesGuidance => "Additional source folders apply only to the current campaign session. Add a folder containing an add-on's _main.cfg to make it available for dependency resolution. These locations are cleared when you close or switch the campaign, or restart WCC. Forgetting a folder removes this editor location; its files and campaign dependency declarations stay intact.";
    public string? CampaignDependencySourcesStatus => CurrentCampaignDependencySourceHost?.Status;
    public string CampaignDependencyResolutionSummary => BuildDependencyResolutionSummary();

    public bool CanAttachCampaignDependencySource => CanChangeCampaignDependencySources() && CurrentCampaignDependencySourceHost?.Attach is not null;

    internal void NotifyCampaignDependencySourceAvailabilityChanged()
    {
        // Called on the UI queue after pending-change source locks have unwound.
        // Eligibility getters always evaluate the current host's live guard.
        foreach (var card in CampaignDependencyCards) card.RefreshAvailability();
        OnPropertyChanged(nameof(CampaignDependencyCards));
        OnPropertyChanged(nameof(CanAttachCampaignDependencySource));
        OnPropertyChanged(nameof(CampaignAdditionalDependencySourceRows));
        OnPropertyChanged(nameof(CampaignDependencySourcesStatus));
    }

    /// <summary>The shell owns indexing, campaign-session source locations and capture refresh; this view only presents their result.</summary>
    public void ConfigureCampaignDependencySources(WccWorkingProjectDependencyReport? report,
        IReadOnlyCollection<string> explicitSourceRoots, Func<string, Task>? attachFolderAsync,
        Func<string, Task>? detachFolderAsync, Func<bool>? canChange, string? status = null)
    {
        ArgumentNullException.ThrowIfNull(explicitSourceRoots);
        lock (_campaignFieldGate)
        {
            _dependencySourceHost = new(_project, _project?.Campaign, report, explicitSourceRoots.ToArray(),
                attachFolderAsync, detachFolderAsync, canChange, status);
            RaiseCampaignDependencyPresentationChanged();
        }
    }

    public CampaignDependencySourceSelection? BeginCampaignDependencySourceSelection()
    {
        lock (_campaignFieldGate)
            return CanAttachCampaignDependencySource && _dependencySourceHost is { } host && _project is { } project &&
                GetCurrentMainSourceRawWml() is { } source
                ? new(this, host, project, project.Campaign, _campaignFieldGeneration, source) : null;
    }

    public async Task<bool> AttachCampaignDependencySourceAsync(CampaignDependencySourceSelection selection, string folderPath)
    {
        if (!IsCurrentCampaignDependencySourceSelection(selection) || string.IsNullOrWhiteSpace(folderPath) ||
            _dependencySourceHost?.Attach is not { } attach) return false;
        return await RunCampaignDependencySourceOperationAsync(selection, attach, folderPath);
    }

    public async Task<bool> ForgetCampaignDependencySourceAsync(CampaignAdditionalDependencySourceRow row)
    {
        CampaignDependencySourceSelection selection;
        Func<string, Task> detach;
        lock (_campaignFieldGate)
        {
            RefreshCampaignDependencyPresentation();
            if (!CanChangeCampaignDependencySources() || _dependencySourceHost is not { Detach: { } callback } host ||
                _project is not { } project || GetCurrentMainSourceRawWml() is not { } source ||
                !_additionalDependencySourceRows.Any(current => ReferenceEquals(current, row) && current.CanForget)) return false;
            selection = new(this, host, project, project.Campaign, _campaignFieldGeneration, source);
            detach = callback;
        }
        return await RunCampaignDependencySourceOperationAsync(selection, detach, row.SourcePath);
    }

    private bool IsCurrentCampaignDependencySourceSelection(CampaignDependencySourceSelection selection)
    {
        lock (_campaignFieldGate)
            return MatchesCampaignDependencySourceContext(selection) && CanAttachCampaignDependencySource;
    }

    private bool MatchesCampaignDependencySourceContext(CampaignDependencySourceSelection selection) =>
        ReferenceEquals(selection.Owner, this) && ReferenceEquals(selection.Host, _dependencySourceHost) &&
        ReferenceEquals(selection.Project, _project) && ReferenceEquals(selection.Document, _project?.Campaign) &&
        selection.Generation == _campaignFieldGeneration && selection.Source == GetCurrentMainSourceRawWml();

    private CampaignDependencySourceHost? CurrentCampaignDependencySourceHost => _dependencySourceHost is { } host &&
        ReferenceEquals(host.Project, _project) && ReferenceEquals(host.Document, _project?.Campaign) ? host : null;

    private bool CanChangeCampaignDependencySources() => !_dependencySourceOperationBusy && !_campaignSaveBusy &&
        !IsCampaignSourceLoading && _project is not null && CurrentCampaignDependencySourceHost?.CanChange?.Invoke() == true;

    private async Task<bool> RunCampaignDependencySourceOperationAsync(CampaignDependencySourceSelection selection,
        Func<string, Task> operation, string folderPath)
    {
        _dependencySourceOperationBusy = true;
        _dependencyPresentationHost = null;
        RaiseCampaignDependencyPresentationChanged();
        try
        {
            // Status notifications can synchronously navigate or reconfigure the host.
            if (!MatchesCampaignDependencySourceContext(selection) || _campaignSaveBusy || IsCampaignSourceLoading ||
                _dependencySourceHost?.CanChange?.Invoke() != true) return false;
            await operation(folderPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Status = "The dependency source could not be changed: " + exception.Message; return false; }
        finally
        {
            _dependencySourceOperationBusy = false;
            _dependencyPresentationHost = null;
            RaiseCampaignDependencyPresentationChanged();
        }
    }

    private void RefreshCampaignDependencyPresentation()
    {
        var binding = _campaignProjectionBinding is { } current && IsCurrentCampaignBinding(current) ? current : null;
        var host = CurrentCampaignDependencySourceHost;
        var canChange = CanChangeCampaignDependencySources();
        if (ReferenceEquals(binding, _dependencyPresentationBinding) && ReferenceEquals(host, _dependencyPresentationHost) &&
            canChange == _dependencyPresentationCanChange) return;
        _dependencyPresentationBinding = binding;
        _dependencyPresentationHost = host;
        _dependencyPresentationCanChange = canChange;
        _declaredDependencyRows = [];
        _editorDependencyRows = [];
        _projectProvidedDependencyRows = [];
        _additionalDependencySourceRows = Array.AsReadOnly((host?.ExplicitSourceRoots ?? [])
            .Select(path => new CampaignAdditionalDependencySourceRow(path,
                canChange && host?.Detach is not null)).ToArray());
        _dependencyMetadataLocation = null;
        _dependencyMetadataSummary = "No indexed dependency metadata is available.";
        if (binding?.Configuration.Snapshot is not { } snapshot || binding.Configuration.RootRevision is not { } root ||
            !snapshot.RevisionsById.TryGetValue(root, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var rootPackage))
        { ApplyConfiguredDependencyReport(); return; }

        // Retain missing exact pins in the display. The standard visibility closure
        // intentionally omits them when deciding which definitions can resolve.
        var pins = new List<(WccPackageRevisionId Id, bool Direct)>();
        var visited = new HashSet<WccPackageRevisionId> { root };
        var pending = new Queue<(WccPackageRevisionId Id, bool Direct)>();
        foreach (var dependency in revision.DependencyRevisionIds) pending.Enqueue((dependency, true));
        while (pending.TryDequeue(out var pin))
        {
            if (!visited.Add(pin.Id)) continue;
            pins.Add(pin);
            if (snapshot.RevisionsById.TryGetValue(pin.Id, out var value))
                foreach (var child in value.DependencyRevisionIds) pending.Enqueue((child, false));
        }
        _editorDependencyRows = Array.AsReadOnly(pins.Select(pin =>
        {
            if (!snapshot.RevisionsById.TryGetValue(pin.Id, out var value) ||
                !snapshot.PackagesById.TryGetValue(value.PackageId, out var package))
                return new CampaignEditorDependencyRow("Missing source revision", "Dependency", null,
                    "The captured package revision is missing.", pin.Id.ToString(), pin.Direct);
            var core = package.ParentPackageId is null &&
                (package.SourceKind == WccPackageSourceKinds.WesnothCore || package.SourceKind == WccPackageSourceKinds.WesnothMainline) &&
                (package.ContentKind == WccPackageContentKinds.Core || package.ContentKind == WccPackageContentKinds.ResourcePack);
            return new CampaignEditorDependencyRow(package.Name, core ? "Core" : pin.Direct ? "Direct dependency" : "Transitive dependency",
                package.SourceLocation, package.IsAvailable ? "Indexed source available" : "Pinned source unavailable",
                value.Id.ToString(), pin.Direct);
        }).ToArray());

        var metadata = revision.AddonMetadata;
        var metadataRoot = rootPackage.SourceLocation;
        if (metadata is null && metadataRoot is not null)
        {
            // A WorkingCopy may shadow its indexed installed package while retaining
            // dependency pins. Only one exact colocated current revision supplies
            // metadata; unrelated packages with matching names are never fallback.
            var candidates = snapshot.PackagesById.Values.Where(package => package.Id != rootPackage.Id &&
                    SameDependencyMetadataRoot(package.SourceLocation, metadataRoot))
                .Select(package => package.CurrentRevisionId is { } id && snapshot.RevisionsById.TryGetValue(id, out var value)
                    && value.PackageId == package.Id ? value.AddonMetadata : null)
                .Where(value => value is not null).Take(2).ToArray();
            if (candidates.Length == 1) metadata = candidates[0];
            else if (candidates.Length > 1)
                _dependencyMetadataSummary = "More than one indexed metadata source matches this campaign folder. Use the metadata source to review it.";
        }
        if (metadata is null) { ApplyConfiguredDependencyReport(); return; }
        _dependencyMetadataSummary = metadata.MetadataSourceFileName is { } file
            ? "Indexed declarations from " + file + "." : "Indexed declarations for " + metadata.AddonId + ".";
        if (metadata.RawFields.TryGetValue("dependencies", out var raw))
            _dependencyMetadataSummary += " dependencies=" + raw;
        if (metadata.RawMetadataText is { } text && metadata.MetadataSourceFileName is { } name &&
            metadataRoot is not null && name == Path.GetFileName(name))
            _dependencyMetadataLocation = new(Path.Combine(metadataRoot, name), text, 0, text.Length,
                "Indexed dependency metadata; read-only snapshot. Dependency editing is planned.");

        _declaredDependencyRows = Array.AsReadOnly(metadata.DeclaredDependencyIds.Select(id =>
        {
            var matches = pins.Select(pin => snapshot.RevisionsById.GetValueOrDefault(pin.Id))
                .Where(value => value is not null && snapshot.PackagesById.TryGetValue(value.PackageId, out var package) &&
                    string.Equals(package.SourceIdentifier ?? value.AddonMetadata?.AddonId, id, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 0) return new CampaignDeclaredDependencyRow(id, "No editor source attached",
                "The indexed metadata declares this add-on, but no exact dependency revision is visible to this editor.");
            if (matches.Length != 1) return new CampaignDeclaredDependencyRow(id, "Ambiguous editor sources",
                "Several pinned revisions match this declared add-on ID; no source was chosen.");
            var package = snapshot.PackagesById[matches[0]!.PackageId];
            return new CampaignDeclaredDependencyRow(id, package.IsAvailable ? "Editor source available" : "Editor source unavailable",
                package.Name + ". Runtime activation must be provided by the campaign's WML.");
        }).ToArray());
        ApplyConfiguredDependencyReport();
    }

    private void ApplyConfiguredDependencyReport()
    {
        if (CurrentCampaignDependencySourceHost?.Report is not { } report) return;
        var providers = report.Entries.GroupBy(entry => (entry.AddonId, entry.Status, entry.PackageId,
            entry.RevisionId, entry.RootPath, Project: IsProjectDependencyProvider(entry))).ToArray();
        _declaredDependencyRows = Array.AsReadOnly(providers.Where(group => !group.Key.Project)
            .Select(group => BuildDependencyRow(group.ToArray())).ToArray());
        _projectProvidedDependencyRows = Array.AsReadOnly(providers.Where(group => group.Key.Project)
            .Select(group => BuildDependencyRow(group.ToArray())).ToArray());
        _editorDependencyRows = Array.AsReadOnly(_editorDependencyRows.Where(row => !report.Entries.Any(entry =>
            IsProjectDependencyProvider(entry) && entry.RevisionId?.ToString() == row.Revision)).Select(row =>
        {
            var entry = report.Entries.FirstOrDefault(value => value.RevisionId?.ToString() == row.Revision);
            return entry is null ? row : row with
            {
                LogicalMount = entry.LogicalMount,
                Detail = entry.Message,
                Status = entry.IsCurrentProjectSource ? "Current campaign source" :
                    entry.IsVerifiedWorkingProjectSource && entry.Status == WccWorkingProjectDependencyStatus.Resolved
                    ? "Verified working project source" : row.Status
            };
        }).ToArray());
    }

    private static bool SameDependencyMetadataRoot(string? left, string right)
    {
        if (left is null) return false;
        try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private void RaiseCampaignDependencyPresentationChanged()
    {
        RefreshCampaignDependencyPresentation();
        RaiseDependencyAuthoringChanged();
        foreach (var card in CampaignDependencyCards) card.RefreshAvailability();
        OnPropertyChanged(nameof(CampaignDependencyCards));
        OnPropertyChanged(nameof(CampaignDependencyOverviewInformation));
        foreach (var property in new[] { nameof(CampaignDeclaredDependencyRows), nameof(CampaignEditorDependencyRows),
                     nameof(CampaignDependencyMetadataLocation), nameof(HasCampaignDependencyMetadataSource),
                     nameof(CampaignDependencyMetadataSummary), nameof(CampaignDependenciesGuidance),
                     nameof(CampaignEditorSourcesGuidance), nameof(CanModifyCampaignDependencies),
                     nameof(CanAttachCampaignDependencySource), nameof(CampaignProjectSourcePath),
                     nameof(CampaignAdditionalDependencySourceRows), nameof(CampaignAdditionalDependencySourcesGuidance),
                     nameof(CampaignDependencySourcesStatus), nameof(CampaignDependencyResolutionSummary),
                     nameof(CampaignProjectProvidedDependencyRows), nameof(CampaignContainedSourceRows),
                     nameof(HasCampaignProjectProvidedDependencies), nameof(HasCampaignContainedSources) }) OnPropertyChanged(property);
    }

    private sealed record CampaignDependencySourceHost(WccProject? Project, CampaignDocument? Document, WccWorkingProjectDependencyReport? Report,
        IReadOnlyList<string> ExplicitSourceRoots, Func<string, Task>? Attach, Func<string, Task>? Detach,
        Func<bool>? CanChange, string? Status);
}
