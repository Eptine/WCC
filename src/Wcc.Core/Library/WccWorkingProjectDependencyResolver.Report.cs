namespace Wcc.Core.Library;

public enum WccWorkingProjectDependencyStatus
{
    Resolved,
    Missing,
    Ambiguous,
    Unavailable,
    Incompatible
}

/// <summary>A declared relationship and its exact editor source, not runtime activation.</summary>
public sealed record WccWorkingProjectDependencyEntry(
    string AddonId,
    string RequiredByAddonId,
    bool IsDirect,
    bool IsDeclared,
    WccWorkingProjectDependencyStatus Status,
    string Message,
    WccPackageId? PackageId = null,
    WccPackageRevisionId? RevisionId = null,
    string? RootPath = null)
{
    public string LogicalMount => "~add-ons/" + AddonId;
    public bool IsVerifiedWorkingProjectSource { get; init; }
    /// <summary>Display evidence for the indexed project namespace, never an external-source alias.</summary>
    public bool IsCurrentProjectSource { get; init; }
}

public sealed class WccWorkingProjectDependencyReport
{
    internal WccWorkingProjectDependencyReport(IEnumerable<WccPackageRevisionId> revisions,
        IEnumerable<WccWorkingProjectDependencyEntry> entries, IEnumerable<WccDiagnosticRecord> diagnostics)
    {
        DependencyRevisionIds = Array.AsReadOnly(revisions.Distinct().ToArray());
        Entries = Array.AsReadOnly(entries.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyList<WccPackageRevisionId> DependencyRevisionIds { get; }
    public IReadOnlyList<WccWorkingProjectDependencyEntry> Entries { get; }
    public IReadOnlyList<WccDiagnosticRecord> Diagnostics { get; }
}

public static partial class WccWorkingProjectDependencyResolver
{
    /// <summary>
    /// Explains declared dependencies against the selected exact graph. Available sources
    /// are never treated as already included WML, and inspection never updates any pin.
    /// The caller may supply the roots selected by its checked Core binding policy.
    /// </summary>
    public static WccWorkingProjectDependencyReport Inspect(WccLibraryManifest manifest,
        WccWorkingProjectDescriptor? project, WccPackageRevisionId? projectCoreRevisionId,
        IReadOnlyCollection<WccPackageRevisionId>? resolvedDependencyRevisionIds = null,
        IReadOnlyCollection<string>? referencedAddonIds = null)
        => InspectCore(manifest, project, projectCoreRevisionId, resolvedDependencyRevisionIds, referencedAddonIds, null);

    /// <summary>
    /// Also recognizes a dependency-cycle back-edge supplied by the exact indexed working
    /// project. The installed counterpart and its availability remain unchanged; this is display evidence.
    /// </summary>
    public static WccWorkingProjectDependencyReport Inspect(WccLibrarySnapshot snapshot,
        WccWorkingProjectDescriptor? project, WccPackageRevisionId? projectCoreRevisionId,
        IReadOnlyCollection<WccPackageRevisionId>? resolvedDependencyRevisionIds = null,
        IReadOnlyCollection<string>? referencedAddonIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return InspectCore(snapshot.Manifest, project, projectCoreRevisionId, resolvedDependencyRevisionIds,
            referencedAddonIds, snapshot);
    }

    private static WccWorkingProjectDependencyReport InspectCore(WccLibraryManifest manifest,
        WccWorkingProjectDescriptor? project, WccPackageRevisionId? projectCoreRevisionId,
        IReadOnlyCollection<WccPackageRevisionId>? resolvedDependencyRevisionIds,
        IReadOnlyCollection<string>? referencedAddonIds, WccLibrarySnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (project is null) return new([], [], []);
        var roots = resolvedDependencyRevisionIds ?? Resolve(manifest, project, projectCoreRevisionId) ?? [];
        var entries = new List<WccWorkingProjectDependencyEntry>();
        var diagnostics = new List<WccDiagnosticRecord>();
        var revisions = manifest.PackageRevisions.ToDictionary(revision => revision.Id);
        var packages = manifest.Packages.ToDictionary(package => package.Id);
        var selected = new Dictionary<WccPackageRevisionId, WccPackageRevisionRecord>();
        var pending = new Queue<WccPackageRevisionId>(roots);
        while (pending.TryDequeue(out var id))
        {
            if (selected.ContainsKey(id)) continue;
            if (!revisions.TryGetValue(id, out var revision) || !packages.ContainsKey(revision.PackageId))
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "project.dependency_revision_unavailable",
                    Message = $"Pinned dependency revision '{id}' is missing from the editor source inventory.",
                    Severity = WccDiagnosticSeverity.Warning
                });
                continue;
            }
            selected.Add(id, revision);
            foreach (var dependency in revision.DependencyRevisionIds) pending.Enqueue(dependency);
        }

        var metadata = WccAddonMetadataReader.Read(project.RootDirectory);
        foreach (var issue in metadata.Issues.Where(issue => issue.Severity != WccDiagnosticSeverity.Information))
            diagnostics.Add(new WccDiagnosticRecord { Code = issue.Code, Message = issue.Message, Severity = issue.Severity });
        var corePackageId = projectCoreRevisionId is { } coreId && revisions.TryGetValue(coreId, out var core)
            ? core.PackageId : (WccPackageId?)null;
        var selectedAddons = selected.Values.Where(revision =>
            packages[revision.PackageId].SourceKind == WccPackageSourceKinds.WesnothAddon).ToArray();
        var projectSource = snapshot is null ? null
            : FindCurrentProjectDependencySource(snapshot, project, projectCoreRevisionId, roots);
        var aliases = new HashSet<WccPackageRevisionId>();
        if (snapshot is not null && projectCoreRevisionId is { } selectedCoreId)
        {
            var working = snapshot.PackagesById.Values.Where(package =>
                package.SourceKind == WccPackageSourceKinds.WccProject &&
                package.StorageMode == WccPackageStorageMode.WorkingCopy &&
                WccPackagePlanEditor.LocationsEqual(package.SourceLocation, project.RootDirectory)).ToArray();
            if (working.Length == 1 && working[0].CurrentRevisionId is { } workingId)
                foreach (var candidate in selectedAddons)
                    if (WccWorkingAddonOverlayRevisionAlias.IsVerified(snapshot, workingId, candidate.Id, selectedCoreId))
                        aliases.Add(candidate.Id);
        }

        void AddDeclarations(WccAddonPackageMetadata owner, bool direct, bool declared = true)
        {
            if (!IsStaticMetadataField(owner, "dependencies"))
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "project.dependency_declaration_unresolved",
                    Message = $"Add-on '{owner.AddonId}' has conditional, dynamic or ambiguous dependency metadata; its original source is preserved.",
                    Severity = WccDiagnosticSeverity.Warning
                });
                return;
            }
            foreach (var addonId in owner.DeclaredDependencyIds)
            {
                var pinned = selectedAddons.Where(candidateRevision => IsAddonId(
                    packages[candidateRevision.PackageId], candidateRevision, addonId)).ToArray();
                WccPackageRecord? package = null;
                WccPackageRevisionRecord? revision = null;
                WccWorkingProjectDependencyStatus status;
                string message;
                var providedByProject = false;
                if (pinned.Length == 1)
                {
                    revision = pinned[0];
                    package = packages[revision.PackageId];
                    var matchesCore = corePackageId is not null && package.ParentPackageId == corePackageId;
                    var available = package.IsAvailable || aliases.Contains(revision.Id);
                    status = !available ? WccWorkingProjectDependencyStatus.Unavailable
                        : matchesCore ? WccWorkingProjectDependencyStatus.Resolved : WccWorkingProjectDependencyStatus.Incompatible;
                    message = aliases.Contains(revision.Id)
                        ? package.IsAvailable
                            ? "The configured source is the verified same-physical counterpart of the current campaign and remains available."
                            : "The exact indexed working-project source supplies this dependency-cycle back-edge; the installed counterpart is unavailable."
                        : !matchesCore && package.IsAvailable
                        ? "The captured source belongs to another Core profile."
                        : package.IsAvailable
                        ? "Exact source revision is available to campaign includes; availability does not load its _main.cfg."
                        : "The pinned source is unavailable; its exact revision was retained and was not replaced.";
                    if (projectSource is not null && addonId == projectSource.AddonId &&
                        !SameProjectDependencyRoot(package.SourceLocation, project.RootDirectory))
                    {
                        status = WccWorkingProjectDependencyStatus.Incompatible;
                        message = "The pinned external source and the current campaign occupy the same logical add-on namespace at different folders. " +
                            "Their exact pins are preserved; WCC did not treat those folders as interchangeable. Current campaign: " + project.RootDirectory;
                    }
                }
                else if (pinned.Length > 1)
                {
                    status = WccWorkingProjectDependencyStatus.Ambiguous;
                    message = "The captured graph contains several revisions or packages for this add-on ID.";
                }
                else if (!direct && projectSource is not null && addonId == projectSource.AddonId)
                {
                    package = projectSource.Package;
                    revision = projectSource.Revision;
                    status = WccWorkingProjectDependencyStatus.Resolved;
                    message = "The exact indexed current campaign supplies this add-on namespace. This records source availability; " +
                        "the captured includes and conditions still determine what loads. No external package or dependency pin was replaced.";
                    providedByProject = true;
                }
                else
                {
                    var candidates = manifest.Packages.Where(candidate =>
                        candidate.SourceKind == WccPackageSourceKinds.WesnothAddon &&
                        candidate.StorageMode == WccPackageStorageMode.ExternalReadOnly &&
                        candidate.ParentPackageId == corePackageId &&
                        string.Equals(candidate.SourceIdentifier, addonId, StringComparison.Ordinal)).ToArray();
                    if (candidates.Length > 1)
                    {
                        status = WccWorkingProjectDependencyStatus.Ambiguous;
                        message = "Several sources use this add-on ID in the selected Core profile; none was chosen.";
                    }
                    else if (candidates.Length == 1)
                    {
                        package = candidates[0];
                        status = package.IsAvailable ? WccWorkingProjectDependencyStatus.Incompatible : WccWorkingProjectDependencyStatus.Unavailable;
                        message = package.IsAvailable
                            ? "A source is known, but its exact dependency graph cannot join the retained project context."
                            : "The known source is unavailable; attach or restore its original folder.";
                    }
                    else
                    {
                        status = WccWorkingProjectDependencyStatus.Missing;
                        message = "No source is attached for this add-on ID in the selected Core profile.";
                    }
                }
                if (!declared)
                    message = "Authored source reference candidate; it may be optional or belong to an inactive branch. " + message;
                entries.Add(new(addonId, owner.AddonId, direct, declared, status, message,
                    package?.Id, revision?.Id, package?.SourceLocation)
                    { IsVerifiedWorkingProjectSource = revision is not null && aliases.Contains(revision.Id),
                        IsCurrentProjectSource = providedByProject });
                if (status != WccWorkingProjectDependencyStatus.Resolved)
                    diagnostics.Add(new WccDiagnosticRecord
                    {
                        Code = "project.dependency_" + status.ToString().ToLowerInvariant(),
                        Message = $"Add-on '{owner.AddonId}' {(declared ? "requires" : "references")} '{addonId}': {message}",
                        Severity = !declared ? WccDiagnosticSeverity.Information
                            : status == WccWorkingProjectDependencyStatus.Ambiguous
                            ? WccDiagnosticSeverity.Error : WccDiagnosticSeverity.Warning,
                        PackageId = package?.Id
                    });
            }
        }

        AddDeclarations(metadata.Metadata, direct: true);
        foreach (var revision in selectedAddons)
            if (revision.AddonMetadata is { } addonMetadata)
                AddDeclarations(addonMetadata, direct: false);
        if (referencedAddonIds is { Count: > 0 })
            AddDeclarations(new WccAddonPackageMetadata
            {
                AddonId = metadata.Metadata.AddonId,
                // The working project already supplies this exact logical add-on
                // namespace. Its own includes/binary_path are project files, not
                // missing external sources. Declared cycle edges above stay visible.
                DeclaredDependencyIds = referencedAddonIds.Where(id =>
                    !string.Equals(id, metadata.Metadata.AddonId, StringComparison.Ordinal) &&
                    !entries.Any(entry => string.Equals(entry.AddonId, id, StringComparison.Ordinal)))
                    .Distinct(StringComparer.Ordinal).ToList()
            }, direct: true, declared: false);

        // Distinguish source edges from declarations even for manually retained graphs.
        foreach (var revision in selectedAddons)
        {
            var package = packages[revision.PackageId];
            var addonId = package.SourceIdentifier ?? revision.AddonMetadata?.AddonId;
            if (addonId is null || entries.Any(entry => entry.RevisionId == revision.Id)) continue;
            entries.Add(new(addonId, metadata.Metadata.AddonId, false, false,
                package.IsAvailable || aliases.Contains(revision.Id)
                    ? WccWorkingProjectDependencyStatus.Resolved : WccWorkingProjectDependencyStatus.Unavailable,
                aliases.Contains(revision.Id)
                    ? package.IsAvailable
                        ? "The configured source is the verified same-physical counterpart of the current campaign and remains available."
                        : "The exact indexed working-project source supplies this dependency-cycle back-edge; the installed counterpart is unavailable."
                    : "Retained editor source revision; campaign WML determines whether its files are included.",
                package.Id, revision.Id, package.SourceLocation)
                { IsVerifiedWorkingProjectSource = aliases.Contains(revision.Id) });
        }

        var colors = new Dictionary<WccPackageRevisionId, int>();
        foreach (var root in selected.Keys)
        {
            if (colors.ContainsKey(root)) continue;
            var walk = new Stack<(WccPackageRevisionId Id, bool Exit)>();
            walk.Push((root, false));
            while (walk.TryPop(out var step))
            {
                if (step.Exit) { colors[step.Id] = 2; continue; }
                if (colors.TryGetValue(step.Id, out var color))
                {
                    if (color == 1)
                        diagnostics.Add(new WccDiagnosticRecord
                        {
                            Code = "project.dependency_cycle",
                            Message = "The selected dependency graph contains a cycle; exact revisions and include order are retained.",
                            Severity = WccDiagnosticSeverity.Information
                        });
                    continue;
                }
                colors[step.Id] = 1;
                walk.Push((step.Id, true));
                foreach (var dependency in selected[step.Id].DependencyRevisionIds.Where(selected.ContainsKey).Reverse())
                    walk.Push((dependency, false));
            }
        }
        // The current project and its exact installed counterpart can expose the
        // same declaration twice. They remain one relationship, with directness
        // retained; different declaring owners or exact providers stay distinct.
        var relationships = entries.GroupBy(entry => entry with { IsDirect = false })
            .Select(group => group.First() with { IsDirect = group.Any(entry => entry.IsDirect) });
        return new(roots, relationships, diagnostics.DistinctBy(diagnostic => (diagnostic.Code, diagnostic.Message)));
    }
}
