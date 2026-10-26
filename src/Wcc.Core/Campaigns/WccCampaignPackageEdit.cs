using Wcc.Core.Library;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>A session-only provider pin. Local folder paths never enter exported loader recipes.</summary>
public sealed record WccCampaignDependencySourcePin(string AddonId, WccPackageRevisionId RevisionId);

/// <summary>
/// Typed package intent for the common collected Save. A preview is not write authority:
/// review rebuilds this recipe from freshly captured physical sources before issuing a ticket.
/// </summary>
public sealed class WccCampaignPackageEdit
{
    private WccCampaignPackageEdit(WccCampaignDependencyMutationPlan? dependency,
        WccCampaignChapterMutationPlan? chapter, IEnumerable<WccCampaignDependencySourcePin>? pins,
        IReadOnlyList<WccCampaignPackageSourceReplacement>? replacements = null, IReadOnlyList<WmlStaticSource>? creates = null,
        IReadOnlyList<WmlStaticSource>? deletes = null, WccContentBinaryDestinationPlan? binaries = null,
        IReadOnlyList<WccPackageBinaryFileDeletion>? binaryDeletes = null, IReadOnlyList<string>? retainedFiles = null)
    {
        Dependency = dependency; Chapter = chapter;
        Source = dependency?.Snapshot.MainSource ?? chapter!.Snapshot.Source;
        SourceReplacements = replacements ?? dependency?.SourceReplacements ?? chapter!.SourceReplacements;
        SourceCreates = creates ?? dependency?.SourceCreates ?? chapter!.SourceCreates;
        SourceDeletes = deletes ?? dependency?.SourceDeletes ?? chapter!.SourceDeletes;
        BinaryDestinations = binaries ?? chapter?.BinaryDestinations ?? new([], []);
        BinaryDeletes = binaryDeletes ?? chapter?.BinaryDeletes ?? [];
        RetainedFiles = retainedFiles ?? chapter?.RetainedFiles ?? [];
        DirectoryRemovals = chapter?.DirectoryRemovals ?? [];
        SourcePins = Array.AsReadOnly((pins ?? []).ToArray());
        if (SourcePins.Count > 32 || SourcePins.Any(pin => pin is null ||
                WccCampaignCreationTokenPolicy.ValidateAddonId(pin.AddonId) is not null) ||
            SourcePins.Select(pin => pin.AddonId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != SourcePins.Count)
            throw new ArgumentException("Use one exact session provider pin per dependency.", nameof(pins));
        var destinations = SourceReplacements.Select(file => file.Source.Path).Concat(SourceCreates.Select(file => file.Path))
            .Concat(SourceDeletes.Select(file => file.Path)).Concat(BinaryDestinations.Creates.Select(file => file.DestinationPath))
            .Concat(BinaryDeletes.Select(file => file.Path)).ToArray();
        if (destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != destinations.Length)
            throw new InvalidOperationException("Package operations overlap a source or resource destination.");
        Identity = WccContentHash.ComputeText((dependency is null ? "chapter:" + chapter!.Operation + ":" + chapter.ChapterId : "dependencies") + "\n" +
            string.Join("\n", SourceReplacements.Select(file => file.Source.Path + ":" + file.Source.ContentHash + ":" + WccContentHash.ComputeText(file.Candidate))) + "\n" +
            string.Join("\n", SourceCreates.Select(file => file.Path + ":" + file.ContentHash)) + "\n" +
            string.Join("\n", SourceDeletes.Select(file => "delete:" + file.Path + ":" + file.ContentHash)) + "\n" +
            string.Join("\n", BinaryDestinations.Creates.Select(file => "binary:" + file.DestinationPath + ":" + file.SourceRevision.RawByteSha256)) + "\n" +
            string.Join("\n", BinaryDestinations.Reuses.Select(file => "reuse:" + file.DestinationPath + ":" + file.SourceRevision.RawByteSha256 + ":" + file.DestinationRevision.RawByteSha256)) + "\n" +
            string.Join("\n", BinaryDeletes.Select(file => "delete-binary:" + file.Path + ":" + file.RawByteSha256)) + "\n" +
            string.Join("\n", RetainedFiles.Select(path => "retain:" + path)) + "\n" +
            string.Join("\n", DirectoryRemovals.Select(path => "empty-directory:" + path)) + "\n" +
            string.Join("\n", SourcePins.Select(pin => pin.AddonId + ":" + pin.RevisionId)));
    }
    public static WccCampaignPackageEdit ForDependencies(WccCampaignDependencyMutationPlan plan,
        IEnumerable<WccCampaignDependencySourcePin>? sources = null) => new(plan ?? throw new ArgumentNullException(nameof(plan)), null, sources);

    public static WccCampaignPackageEdit ForChapter(WccCampaignChapterMutationPlan plan,
        IReadOnlyList<WmlStaticSource>? projectSources = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var initial = projectSources ?? plan.Snapshot.ProjectSources;
        var main = initial.SingleOrDefault(source => Same(source, plan.Snapshot.Source)) ??
            throw new InvalidOperationException("The chapter recipe requires its current captured main source.");
        var addonId = plan.Snapshot.Authoring?.AddonId ?? throw new InvalidOperationException("Chapter authoring needs its managed scaffold metadata.");
        var dependencySnapshot = WccCampaignDependencyAuthoringPlanner.Read(main, addonId, initial);
        if (!dependencySnapshot.CanAdd) throw new InvalidOperationException(dependencySnapshot.Diagnostic);
        var originals = initial.ToDictionary(source => source.Path, PathComparer);
        var current = new Dictionary<string, WmlStaticSource>(originals, PathComparer);
        var effectiveChapter = plan;
        void Apply(IEnumerable<WccCampaignPackageSourceReplacement> replacements, IEnumerable<WmlStaticSource> creates,
            IEnumerable<WmlStaticSource> deletes)
        {
            foreach (var file in replacements)
            {
                if (!current.TryGetValue(file.Source.Path, out var captured) || !Same(captured, file.Source))
                    throw new InvalidOperationException("The package operations overlap different source revisions.");
                current[file.Source.Path] = file.CandidateSource;
            }
            foreach (var file in creates)
                if (!current.TryAdd(file.Path, file)) throw new InvalidOperationException("The new package files overlap.");
            foreach (var file in deletes)
            {
                if (!current.TryGetValue(file.Path, out var captured) || !Same(captured, file))
                    throw new InvalidOperationException("The package cleanup no longer matches one exact captured source.");
                current.Remove(file.Path);
            }
        }
        if (plan.Operation == "Add")
        {
            Apply(plan.SourceReplacements, plan.SourceCreates, plan.SourceDeletes);
            var nextMain = current[main.Path];
            var chapters = WccCampaignChapterAuthoringPlanner.Read(nextMain, plan.Snapshot.Authoring, current.Values.ToArray());
            if (!chapters.CanEdit) throw new InvalidOperationException(chapters.Diagnostic);
            var chapter = chapters.Chapters.Single(entry => entry.Id == plan.ChapterId);
            var desired = dependencySnapshot.Entries.Select(entry => entry.Spec.AppliesToAllCampaigns
                ? Retarget(entry.Spec, entry.Spec.TargetCampaignDefines.Append(chapter.CampaignDefine)) : entry.Spec).ToArray();
            if (desired.Length != 0)
            {
                var dependencies = WccCampaignDependencyAuthoringPlanner.Plan(
                    WccCampaignDependencyAuthoringPlanner.Read(nextMain, addonId, current.Values.ToArray()), desired);
                Apply(dependencies.SourceReplacements, dependencies.SourceCreates, dependencies.SourceDeletes);
            }
        }
        else
        {
            var removed = plan.Snapshot.Chapters.Single(entry => entry.Id == plan.ChapterId);
            var desired = dependencySnapshot.Entries.Select(entry => Retarget(entry.Spec,
                    entry.Spec.TargetCampaignDefines.Where(define => define != removed.CampaignDefine)))
                .Where(spec => spec.TargetCampaignDefines.Count != 0).ToArray();
            if (dependencySnapshot.Entries.Count != 0)
            {
                var dependencies = WccCampaignDependencyAuthoringPlanner.Plan(dependencySnapshot, desired);
                Apply(dependencies.SourceReplacements, dependencies.SourceCreates, dependencies.SourceDeletes);
            }
            effectiveChapter = WccCampaignChapterAuthoringPlanner.Remove(
                WccCampaignChapterAuthoringPlanner.Read(current[main.Path], plan.Snapshot.Authoring, current.Values.ToArray()), plan.ChapterId);
            Apply(effectiveChapter.SourceReplacements, effectiveChapter.SourceCreates, effectiveChapter.SourceDeletes);
        }
        var changed = current.Values.Where(source => originals.TryGetValue(source.Path, out var old) && !Same(old, source))
            .OrderBy(source => source.Path, StringComparer.Ordinal).Select(source => new WccCampaignPackageSourceReplacement(originals[source.Path], source.Text)).ToArray();
        var added = current.Values.Where(source => !originals.ContainsKey(source.Path)).OrderBy(source => source.Path, StringComparer.Ordinal).ToArray();
        var removedSources = originals.Values.Where(source => !current.ContainsKey(source.Path)).OrderBy(source => source.Path, StringComparer.Ordinal).ToArray();
        return new(null, plan, null, Array.AsReadOnly(changed), Array.AsReadOnly(added), Array.AsReadOnly(removedSources),
            effectiveChapter.BinaryDestinations, effectiveChapter.BinaryDeletes, effectiveChapter.RetainedFiles);
    }
    private static WccCampaignDependencySpec Retarget(WccCampaignDependencySpec spec, IEnumerable<string> targets) =>
        new(spec.Id, spec.AddonId, Array.AsReadOnly(targets.Distinct(StringComparer.Ordinal).ToArray()), spec.Steps,
            spec.DeclareDependency, spec.AppliesToAllCampaigns);
    public WmlStaticSource Source { get; }
    public IReadOnlyList<WccCampaignPackageSourceReplacement> SourceReplacements { get; }
    public IReadOnlyList<WmlStaticSource> SourceCreates { get; }
    public IReadOnlyList<WmlStaticSource> SourceDeletes { get; }
    public WccContentBinaryDestinationPlan BinaryDestinations { get; }
    public IReadOnlyList<WccPackageBinaryFileDeletion> BinaryDeletes { get; }
    public IReadOnlyList<string> RetainedFiles { get; }
    public IReadOnlyList<string> DirectoryRemovals { get; }
    public IReadOnlyList<WccCampaignDependencySourcePin> SourcePins { get; }
    public string Identity { get; }
    public bool AuthorizesPersistence => false;
    internal WccCampaignDependencyMutationPlan? Dependency { get; }
    internal WccCampaignChapterMutationPlan? Chapter { get; }

    internal WccCampaignPackageEdit Rebuild(IReadOnlyList<WmlStaticSource> projectSources)
    {
        var main = projectSources.SingleOrDefault(source => Same(source, Source)) ??
            throw new InvalidOperationException("The managed package main no longer matches its exact captured source.");
        if (Dependency is { } dependency)
            return ForDependencies(WccCampaignDependencyAuthoringPlanner.Plan(
                WccCampaignDependencyAuthoringPlanner.Read(main, dependency.Snapshot.ProjectAddonId, projectSources),
                dependency.DesiredSpecs), SourcePins);
        var chapter = Chapter!;
        return ForChapter(WccCampaignChapterAuthoringPlanner.Rebuild(
            WccCampaignChapterAuthoringPlanner.Read(main, chapter.Snapshot.Authoring, projectSources), chapter), projectSources);
    }
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static bool Same(WmlStaticSource left, WmlStaticSource right) => PathComparer.Equals(left.Path, right.Path)
        && left.ContentHash == right.ContentHash && left.Text == right.Text;
}
