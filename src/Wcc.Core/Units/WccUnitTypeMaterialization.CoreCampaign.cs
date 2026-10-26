using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>Exact original campaign helper ownership; neither activation nor write authority.</summary>
public sealed class WccUnitTypeCoreCampaignHelperSource
{
    internal WccUnitTypeCoreCampaignHelperSource(WccManagedMacroCopySource source,
        WccStaticSourceCaptureResult capture, IReadOnlyList<WccTextDiskRevision> texts,
        string manifestIdentity, WccPackageRevisionId coreRevision, string coreRoot)
    {
        Source = source; Capture = capture; TextRevisions = Array.AsReadOnly(texts.ToArray());
        ManifestIdentity = manifestIdentity; CoreRevisionId = coreRevision; CoreRootPath = coreRoot;
        Helper = new(source.Name, source.Source, source.Definition, source.TextDomain, []);
    }
    public WccManagedMacroCopySource Source { get; }
    public WccStaticSourceCaptureResult Capture { get; }
    public WccPackageRevisionId CoreRevisionId { get; }
    public string CoreRootPath { get; }
    internal WccContentHelperReceipt Helper { get; }
    internal IReadOnlyList<WccTextDiskRevision> TextRevisions { get; }
    internal string ManifestIdentity { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>Immutable bounded discovery of original mainline-campaign helpers used by one selected unit.</summary>
public sealed class WccUnitTypeCoreCampaignHelperCapture
{
    internal WccUnitTypeCoreCampaignHelperCapture(WccUnitTypeAuthoredCopySource source,
        WccStaticSourceCaptureResult managedCapture, IEnumerable<WccUnitTypeCoreCampaignHelperSource> helpers,
        IEnumerable<WccContentDependencyIssue> issues)
    {
        UnitSource = source; ManagedCapture = managedCapture;
        Sources = Array.AsReadOnly(helpers.ToArray()); Issues = Array.AsReadOnly(issues.Distinct().ToArray());
    }
    internal WccUnitTypeAuthoredCopySource UnitSource { get; }
    internal WccStaticSourceCaptureResult ManagedCapture { get; }
    public IReadOnlyList<WccUnitTypeCoreCampaignHelperSource> Sources { get; }
    public IReadOnlyList<WccContentDependencyIssue> Issues { get; }
    public bool AuthorizesPersistence => false;
}

public static partial class WccUnitTypeMaterializationPlanner
{
    public static Task<WccUnitTypeCoreCampaignHelperCapture> CaptureCoreCampaignHelpersAsync(
        WccLibrarySnapshot snapshot, WccUnitTypeAuthoredCopySource source, WccStaticSourceCaptureResult managedCapture,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(managedCapture); ArgumentNullException.ThrowIfNull(visibleRevisionIds);
        var visible = visibleRevisionIds.ToHashSet();
        return Task.Run(() => CaptureCoreCampaignHelpersCoreAsync(snapshot, source, managedCapture, visible, cancellationToken), cancellationToken);
    }

    private static async Task<WccUnitTypeCoreCampaignHelperCapture> CaptureCoreCampaignHelpersCoreAsync(
        WccLibrarySnapshot snapshot, WccUnitTypeAuthoredCopySource source, WccStaticSourceCaptureResult managedCapture,
        IReadOnlySet<WccPackageRevisionId> visible, CancellationToken cancellationToken)
    {
        var helpers = new List<WccUnitTypeCoreCampaignHelperSource>(); var issues = new List<WccContentDependencyIssue>();
        void Issue(string name, string message, WmlStaticSourceRange? origin = null) =>
            issues.Add(new("materialization.core-campaign-helper", name, "macro", message, origin));
        if (source.IsCore || !managedCapture.IsCaptureComplete || managedCapture.Sources.Count(item => SameSource(item, source.Source)) != 1)
            return new(source, managedCapture, [], []);
        var core = WccStaticSourceCapture.GetCapturedHelperCoreBinding(managedCapture);
        var coreNames = managedCapture.Sources.Where(file => Inside(core.RootPath, file.Path))
            .SelectMany(file => WmlMacroDefinitionScanner.Scan(file.Text)).Select(macro => macro.Name).ToHashSet(StringComparer.Ordinal);
        var managed = managedCapture.Sources.Where(file => Inside(source.RootPath, file.Path)
            && new[] { WccManagedLibraryFileKind.Macro, WccManagedLibraryFileKind.Ability, WccManagedLibraryFileKind.WeaponSpecial }
                .Any(kind => WccManagedLibraryFileStore.IsPathForKind(source.RootPath, kind, file.Path)))
            .Select(file => CaptureHelper(file, out _)).OfType<WccContentHelperReceipt>()
            .GroupBy(helper => helper.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var managedDeclarations = managedCapture.Sources.Where(file => Inside(source.RootPath, file.Path))
            .SelectMany(file => WmlMacroDefinitionScanner.Scan(file.Text)).GroupBy(definition => definition.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var visitedManaged = new HashSet<string>(StringComparer.Ordinal);
        var visitedForeign = new HashSet<(WccPackageRevisionId Revision, string Name)>();
        var scopes = new Dictionary<WccPackageRevisionId, (WccStaticSourceCaptureResult Capture, IReadOnlyList<WccTextDiskRevision> Texts, string Manifest)>();
        bool Eligible(WccSourceFileRecord file)
        {
            if (!visible.Contains(file.PackageRevisionId) || !snapshot.RevisionsById.TryGetValue(file.PackageRevisionId, out var revision)
                || !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package)) return false;
            return package.SourceKind == WccPackageSourceKinds.WesnothMainline && package.ContentKind == WccPackageContentKinds.Campaign
                && package.StorageMode == WccPackageStorageMode.ExternalReadOnly && package.IsAvailable
                && package.CurrentRevisionId == revision.Id && !string.IsNullOrWhiteSpace(package.SourceLocation)
                && WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, revision.Id).Contains(core.RevisionId);
        }
        WccEntityRecord[] Named(string name, WccPackageRevisionId? owner) => snapshot.FindEntities(WccEntityTypes.MacroDefinition)
            .Where(entity => entity.ParentEntityId is null && entity.ExportSymbol == name
                && snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) && Eligible(file)
                && (owner is null || file.PackageRevisionId == owner.Value)).ToArray();
        async Task Visit(string raw, IReadOnlyList<string> parameters, WmlStaticSourceRange origin, WccPackageRevisionId? foreignOwner, int depth)
        {
            foreach (var invocation in WmlMacroInvocationSyntaxScanner.Scan(raw))
            {
                cancellationToken.ThrowIfCancellationRequested(); var name = invocation.Name;
                if (parameters.Contains(name, StringComparer.Ordinal) || name.Contains('/') || name.Contains('\\')) continue;
                if (foreignOwner is null && managedDeclarations.ContainsKey(name))
                {
                    if (!managed.TryGetValue(name, out var local) || local.Length != 1 || managedDeclarations[name] != 1)
                        Issue(name, "Helper {" + name + "} already has an unsupported or ambiguous authored Library declaration; an original campaign helper cannot replace its meaning.", origin);
                    else if (visitedManaged.Add(name) && depth < MaximumHelpers)
                        await Visit(local[0].Definition.Text, local[0].Parameters, local[0].Definition, null, depth + 1);
                    continue;
                }
                if (foreignOwner is null && coreNames.Contains(name)) continue;
                var named = Named(name, foreignOwner);
                if (coreNames.Contains(name))
                {
                    if (foreignOwner is not null && named.Length != 0)
                        Issue(name, "Helper {" + name + "} exists in both its original campaign namespace and Core; the binding is ambiguous.", origin);
                    continue;
                }
                if (named.Length == 0)
                {
                    if (foreignOwner is not null) Issue(name, "Helper {" + name + "} is not one pure definition in its original campaign namespace.", origin);
                    continue;
                }
                if (named.Length != 1)
                { Issue(name, "Helper {" + name + "} is ambiguous across current visible mainline campaign definitions; select or clone a distinct helper.", origin); continue; }
                var entity = named[0]; var file = snapshot.SourceFilesById[entity.SourceFileId];
                if (!visitedForeign.Add((file.PackageRevisionId, name))) continue;
                if (depth >= MaximumHelpers || helpers.Count >= MaximumHelpers)
                { Issue(name, "The original campaign helper closure exceeds 64 definitions.", origin); continue; }
                try
                {
                    if (!scopes.TryGetValue(file.PackageRevisionId, out var scope))
                    {
                        if (string.IsNullOrWhiteSpace(file.StoragePath)) throw new InvalidOperationException("The helper has no indexed physical source.");
                        var capture = await WccStaticSourceCapture.CaptureAsync(snapshot, file.PackageRevisionId, file.StoragePath,
                            cancellationToken: cancellationToken, includeCoreUnits: true);
                        if (!capture.IsCaptureComplete || !capture.HasCompleteCoreUnitPrelude)
                            throw new InvalidOperationException("The original campaign source capture is incomplete: " + string.Join("\n", capture.Diagnostics));
                        var foreignCore = WccStaticSourceCapture.GetCapturedHelperCoreBinding(capture);
                        if (foreignCore.RevisionId != core.RevisionId || !SamePath(foreignCore.RootPath, core.RootPath) || capture.CoreVersion != managedCapture.CoreVersion)
                            throw new InvalidOperationException("The original campaign helper does not pin the same exact Core as this unit copy.");
                        var texts = new List<WccTextDiskRevision>();
                        foreach (var member in capture.Sources)
                        {
                            var text = WccTextDiskRevisionCapture.Capture(WccStaticSourceCapture.CapturedSourceRoot(capture, member), member.Path, member.Text);
                            if (!text.IsCaptured || text.Revision is null) throw new InvalidOperationException("Cannot bind original helper namespace bytes: " + text.ErrorMessage);
                            texts.Add(text.Revision);
                        }
                        scope = (capture, Array.AsReadOnly(texts.ToArray()), CoreCampaignManifestIdentity(snapshot, capture));
                        scopes.Add(file.PackageRevisionId, scope);
                    }
                    var physical = scope.Capture.Sources.SingleOrDefault(member => file.StoragePath is { } path && SamePath(path, member.Path) && file.ContentHash == member.ContentHash)
                        ?? throw new InvalidOperationException("The original helper's exact indexed physical source changed.");
                    var captured = WccManagedMacroCopyPlanner.CaptureSource(snapshot, entity.Id, physical);
                    var standalone = CaptureHelper(new(physical.Path, "#textdomain " + captured.TextDomain + "\n" + captured.Definition.Text), out var reason);
                    if (standalone is null || standalone.RequiredParameterCount != 0 || !MacroName(captured.Name))
                        throw new InvalidOperationException("Original campaign helpers require one unconditional pure parameterless definition: " + reason);
                    var receipt = new WccUnitTypeCoreCampaignHelperSource(captured, scope.Capture, scope.Texts, scope.Manifest, core.RevisionId, core.RootPath);
                    helpers.Add(receipt);
                    await Visit(captured.Definition.Text, [], captured.Definition, captured.RevisionId, depth + 1);
                }
                catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
                { Issue(name, "Helper {" + name + "} cannot be copied from its original campaign: " + failure.Message, origin); }
            }
        }
        await Visit(source.RawText, [], new(source.Source, source.StartOffset, source.Length), null, 0);
        return new(source, managedCapture, helpers, issues);
    }

    private static string CoreCampaignManifestIdentity(WccLibrarySnapshot snapshot, WccStaticSourceCaptureResult capture)
    {
        var value = new StringBuilder();
        foreach (var root in WccStaticSourceCapture.GetCapturedHelperSourceRoots(capture).OrderBy(root => root.RevisionId.ToString(), StringComparer.Ordinal))
        {
            if (!snapshot.RevisionsById.TryGetValue(root.RevisionId, out var revision)
                || !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package))
                throw new InvalidOperationException("The original helper namespace is no longer in the pinned manifest.");
            value.Append(root.RootPath).Append('|').Append(root.RevisionId).Append('|').Append(package.Id).Append('|').Append(package.SourceKind)
                .Append('|').Append(package.ContentKind).Append('|').Append(package.StorageMode).Append('|').Append(package.IsAvailable)
                .Append('|').Append(package.SourceLocation).Append('|').Append(package.SourceIdentifier).Append('|').Append(package.ParentPackageId)
                .Append('|').Append(package.CurrentRevisionId).Append('|').Append(revision.Version).Append('|').Append(revision.TargetWesnothVersion)
                .Append('|').Append(revision.ContentFingerprint).Append('|').Append(string.Join(",", revision.DependencyRevisionIds)).Append('\n');
            foreach (var file in snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == root.RevisionId).OrderBy(file => file.Id.ToString(), StringComparer.Ordinal))
                value.Append(file.Id).Append('|').Append(file.RelativePath).Append('|').Append(file.StoragePath).Append('|').Append(file.FileType).Append('|').Append(file.ContentHash).Append('\n');
        }
        return WccContentHash.ComputeText(value.ToString());
    }

    private static WccUnitTypeCoreCampaignHelperSource? ExternalHelper(WccUnitTypeMaterializationPlan plan, WccContentHelperReceipt helper) =>
        plan.CoreCampaignHelpers.SingleOrDefault(item => item.Source.Name == helper.Name && item.Source.Definition == helper.Definition);

    private static IReadOnlyList<WccUnitTypeCoreCampaignHelperSource> ForeignResourceOwners(WccUnitTypeMaterializationPlan plan,
        WccMaterializationCandidate preview, WmlStaticScalarOwner owner, WmlStaticScalarContribution? contribution)
    {
        var frames = contribution is null ? owner.OpeningExpansionStack
            : contribution.ExpansionStack.Concat(contribution.ValueExpansionStack).ToArray();
        var origins = contribution is null ? [] : contribution.AssignmentOrigins.Concat(contribution.ValueOrigins)
            .Concat(contribution.AssignmentOrigin is { } assignment ? [assignment] : []).ToArray();
        bool Within(WmlStaticSourceRange origin, WmlStaticSourceRange range) => SameSource(origin.Source, range.Source)
            && origin.StartOffset >= range.StartOffset && origin.StartOffset + origin.Length <= range.StartOffset + range.Length;
        return plan.CoreCampaignHelpers.Where(helper => preview.Helpers.Any(generated => generated.Name == helper.Source.Name
            && (frames.Any(frame => frame.MacroName == helper.Source.Name && Within(frame.Definition, generated.Range))
                || origins.Any(origin => Within(origin, generated.Range))))).ToArray();
    }

    private static IEnumerable<string> ResourceAliases(string folder, IEnumerable<string> filenames) => filenames.SelectMany(filename =>
        folder == "images" && (filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            ? new[] { filename, filename[..^4] + ".webp" } : new[] { filename }).Distinct(StringComparer.Ordinal);

    private static void ValidateCoreCampaignHelper(WccLibrarySnapshot snapshot, WccUnitTypeCoreCampaignHelperSource helper)
    {
        var source = helper.Source;
        WccManagedMacroCopyPlanner.ValidateSource(source.Snapshot, source);
        if (CoreCampaignManifestIdentity(snapshot, helper.Capture) != helper.ManifestIdentity
            || !snapshot.SourceFilesById.TryGetValue(source.SourceFileId, out var file) || file.PackageRevisionId != source.RevisionId
            || file.StoragePath is not { } path || !SamePath(path, source.Source.Path) || file.ContentHash != source.Source.ContentHash
            || !snapshot.RevisionsById.TryGetValue(source.RevisionId, out var revision) || revision.PackageId != source.PackageId
            || !snapshot.PackagesById.TryGetValue(source.PackageId, out var package) || package.CurrentRevisionId != source.RevisionId
            || package.SourceKind != WccPackageSourceKinds.WesnothMainline || package.ContentKind != WccPackageContentKinds.Campaign
            || !package.IsAvailable || package.StorageMode != WccPackageStorageMode.ExternalReadOnly || package.SourceLocation is null
            || !SamePath(package.SourceLocation, source.RootPath))
            throw new InvalidOperationException("The original campaign helper {" + source.Name + "} changed its exact current source binding.");
    }

    private static string? VerifyCoreCampaignHelpers(WccUnitTypeMaterializationPlan plan)
    {
        foreach (var helper in plan.CoreCampaignHelpers)
        {
            try { WccManagedMacroCopyPlanner.ValidateSource(helper.Source.Snapshot, helper.Source); }
            catch (Exception failure) when (failure is InvalidOperationException or ArgumentException) { return failure.Message; }
        }
        foreach (var capture in plan.CoreCampaignHelpers.Select(helper => helper.Capture).Distinct())
            if (WccStaticSourceCapture.VerifyCapturedMacroClosure(capture) is { } inventory) return inventory;
        foreach (var expected in plan.CoreCampaignHelpers.SelectMany(helper => helper.TextRevisions).DistinctBy(text => text.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var current = WccTextDiskRevisionCapture.Capture(expected.ProjectRootPath, expected.SourcePath, expected.Text);
            if (!current.IsCaptured || current.Revision is null || !expected.IsSameRevisionAs(current.Revision))
                return "An original campaign helper/Core source changed: " + expected.SourcePath;
        }
        return null;
    }
}
