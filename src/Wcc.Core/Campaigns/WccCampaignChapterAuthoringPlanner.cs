using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public sealed record WccCampaignChapterSpec(string Id, string Name)
{
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "units/human-loyalists/lieutenant.png";
    public string Image { get; init; } = "maps/wesnoth.webp";
    public string Background { get; init; } = "maps/background.webp";
    public string? IconFilePath { get; init; }
    public string? ImageFilePath { get; init; }
    public string? BackgroundFilePath { get; init; }
    public IReadOnlyList<WccCampaignDifficultySpec> Difficulties { get; init; } =
        [new("Normal", "", "units/human-loyalists/lieutenant.png", true)];
    public bool CreateStarterScenario { get; init; }
}

public sealed record WccCampaignChapterAuthoringEntry(string Id, string CampaignId, string Name,
    string CampaignDefine, string ScenarioDirectory, IReadOnlyList<string> FixedDifficultyDefines,
    WmlStaticSourceRange CampaignRange, WmlStaticSourceRange GameplayRange, bool IsOriginal);

/// <summary>Raw bytes stay outside text capture. The coordinator captures the current exact native revision before deleting.</summary>
public sealed record WccPackageBinaryFileDeletion(string Path, string RawByteSha256);

public sealed class WccCampaignChapterAuthoringSnapshot
{
    internal WccCampaignChapterAuthoringSnapshot(WmlStaticSource source, WccCampaignAuthoringMetadata? authoring,
        IEnumerable<WccCampaignChapterAuthoringEntry> chapters, bool canEdit, string diagnostic,
        IReadOnlyList<WmlStaticSource>? projectSources = null, WccPackageAuthoringManifestSnapshot? manifest = null)
    {
        Source = source; Authoring = authoring is null ? null : new WccCampaignAuthoringMetadata
        { LayoutVersion = authoring.LayoutVersion, AddonId = authoring.AddonId, CampaignDefine = authoring.CampaignDefine,
            MainRelativePath = authoring.MainRelativePath, FixedDifficultyDefines = authoring.FixedDifficultyDefines?.ToList() ?? [],
            UsesExplicitDifficultyValues = authoring.UsesExplicitDifficultyValues };
        Chapters = Array.AsReadOnly(chapters.ToArray()); CanEdit = canEdit; Diagnostic = diagnostic;
        ProjectSources = Array.AsReadOnly((projectSources ?? new[] { source }).ToArray()); Manifest = manifest;
    }
    public WmlStaticSource Source { get; }
    public IReadOnlyList<WccCampaignChapterAuthoringEntry> Chapters { get; }
    public bool CanEdit { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal WccCampaignAuthoringMetadata? Authoring { get; }
    internal IReadOnlyList<WmlStaticSource> ProjectSources { get; }
    internal WccPackageAuthoringManifestSnapshot? Manifest { get; }
}

/// <summary>Immutable intent; checked Save rebuilds text, resources and removals against one captured package.</summary>
public sealed class WccCampaignChapterMutationPlan
{
    internal WccCampaignChapterMutationPlan(WccCampaignChapterAuthoringSnapshot snapshot, string candidate,
        IEnumerable<WmlStaticSource> creates, IEnumerable<string> directories, string operation, string chapterId,
        WccCampaignChapterSpec? addedChapter = null, IEnumerable<WccCampaignPackageSourceReplacement>? replacements = null,
        WccContentBinaryDestinationPlan? binaries = null, IEnumerable<WmlStaticSource>? deletes = null,
        IEnumerable<WccPackageBinaryFileDeletion>? binaryDeletes = null, IEnumerable<string>? retainedFiles = null)
    {
        Snapshot = snapshot;
        SourceReplacements = Array.AsReadOnly((replacements ?? new[] { new WccCampaignPackageSourceReplacement(snapshot.Source, candidate) }).ToArray());
        SourceCreates = Array.AsReadOnly(creates.ToArray()); DirectoryPaths = Array.AsReadOnly(directories.ToArray());
        SourceDeletes = Array.AsReadOnly((deletes ?? []).ToArray()); BinaryDeletes = Array.AsReadOnly((binaryDeletes ?? []).ToArray());
        RetainedFiles = Array.AsReadOnly((retainedFiles ?? []).ToArray()); BinaryDestinations = binaries ?? new([], []);
        var removedChapter = operation == "Remove" ? snapshot.Chapters.Single(chapter => chapter.Id == chapterId) : null;
        DirectoryRemovals = removedChapter is not null && removedChapter.ScenarioDirectory == "chapters/" + chapterId + "/scenarios" &&
            snapshot.Manifest?.Document.Chapters.Any(chapter => chapter.Id == chapterId) == true
            ? Array.AsReadOnly(new[] { Path.Combine(Path.GetDirectoryName(snapshot.Source.Path)!, removedChapter.ScenarioDirectory.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(Path.GetDirectoryName(snapshot.Source.Path)!, "chapters", chapterId) }) : [];
        if (DirectoryRemovals.Count != 0)
        {
            var chapterUtils = Path.Combine(Path.GetDirectoryName(snapshot.Source.Path)!, "chapters", chapterId, "utils");
            if (SourceDeletes.Any(source => string.Equals(source.Path, Path.Combine(chapterUtils, "presets.cfg"),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                DirectoryRemovals = Array.AsReadOnly(DirectoryRemovals.Take(1).Append(chapterUtils).Concat(DirectoryRemovals.Skip(1)).ToArray());
            var prospective = snapshot.ProjectSources.ToDictionary(source => source.Path,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var file in SourceReplacements) prospective[file.Source.Path] = file.CandidateSource;
            foreach (var file in SourceCreates) prospective[file.Path] = file;
            foreach (var file in SourceDeletes) prospective.Remove(file.Path);
            var root = Path.GetDirectoryName(snapshot.Source.Path)!;
            // Only inspect this already captured prospective text, including the NEW
            // companion. An old chapter receipt would otherwise retain every folder.
            // Custom #ifhave/include paths can observe an empty directory, so preserve
            // it when any remaining source still mentions it. Conservative matches
            // deliberately leave folders behind rather than alter unknown behavior.
            DirectoryRemovals = Array.AsReadOnly(DirectoryRemovals.Where(directory =>
            {
                var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                return !prospective.Values.Any(source => source.Text.Replace('\\', '/').Contains(relative, StringComparison.OrdinalIgnoreCase));
            }).ToArray());
        }
        Operation = operation; ChapterId = chapterId;
        AddedChapter = addedChapter is null ? null : addedChapter with { Difficulties = Array.AsReadOnly(addedChapter.Difficulties.ToArray()) };
        Identity = WccContentHash.ComputeText(snapshot.Source.ContentHash + "\n" + operation + "\n" + chapterId + "\n" +
            string.Join("\n", SourceReplacements.Select(file => file.Source.Path + ":" + file.Source.ContentHash + ":" + file.Candidate)) + "\n" +
            string.Join("\n", SourceCreates.Select(file => file.Path + ":" + file.ContentHash)) + "\n" +
            string.Join("\n", BinaryDestinations.Creates.Select(file => file.DestinationPath + ":" + file.SourceRevision.RawByteSha256)) + "\n" +
            string.Join("\n", SourceDeletes.Select(file => file.Path + ":" + file.ContentHash)) + "\n" +
            string.Join("\n", BinaryDeletes.Select(file => file.Path + ":" + file.RawByteSha256)) + "\n" +
            string.Join("\n", DirectoryRemovals));
    }
    public WccCampaignChapterAuthoringSnapshot Snapshot { get; }
    public IReadOnlyList<WccCampaignPackageSourceReplacement> SourceReplacements { get; }
    public IReadOnlyList<WmlStaticSource> SourceCreates { get; }
    public IReadOnlyList<WmlStaticSource> SourceDeletes { get; }
    public IReadOnlyList<WccPackageBinaryFileDeletion> BinaryDeletes { get; }
    public IReadOnlyList<string> RetainedFiles { get; }
    public IReadOnlyList<string> DirectoryRemovals { get; }
    public WccContentBinaryDestinationPlan BinaryDestinations { get; }
    public IReadOnlyList<string> DirectoryPaths { get; }
    public string Operation { get; }
    public string ChapterId { get; }
    public WccCampaignChapterSpec? AddedChapter { get; }
    public string Identity { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>Chapter topology uses ordinary WML and a captured portable companion; markers carry only stable chapter names.</summary>
public static class WccCampaignChapterAuthoringPlanner
{
    private const string Begin = "# WCC chapter begin: ";
    private const string End = "# WCC chapter end: ";

    private sealed record Managed(WccPackageAuthoredChapter Receipt, WmlStaticSourceRange Range);
    private sealed record Scope(WmlStaticSourceRange Campaign, WmlStaticSourceRange Gate,
        WmlStaticScalarOwner Owner, string Define);
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static WccCampaignChapterAuthoringSnapshot Read(WmlStaticSource source, WccCampaignAuthoringMetadata? authoring,
        IReadOnlyList<WmlStaticSource>? projectSources = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var entries = new List<WccCampaignChapterAuthoringEntry>();
        WccPackageAuthoringManifestSnapshot? manifest = null;
        var sources = projectSources ?? new[] { source };
        try
        {
            if (authoring is null) throw new InvalidOperationException("Imported chapter topology requires RAW editing. WCC only adds and removes chapters in its own package scaffold.");
            if (!source.IsAuthored || authoring.LayoutVersion is not (1 or 2) || authoring.MainRelativePath != "_main.cfg" ||
                WccCampaignCreationTokenPolicy.ValidateAddonId(authoring.AddonId) is not null || Path.GetFileName(source.Path) != "_main.cfg" ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(source.Path)), authoring.AddonId, StringComparison.Ordinal))
                throw new InvalidOperationException("The chapter authoring metadata does not identify this managed package main file.");
            manifest = ReadManifest(source, authoring.AddonId, sources);
            var managed = ReadManaged(source, manifest.Document);
            var scopes = ReadScopes(source);
            if (scopes.Count is < 1 or > 32) throw new InvalidOperationException("A managed package needs between one and 32 campaign entries.");
            foreach (var scope in scopes)
            {
                var record = managed.SingleOrDefault(item => Contains(item.Range, scope.Campaign) && Contains(item.Range, scope.Gate));
                if (record is null && scope.Define != authoring.CampaignDefine)
                    throw new InvalidOperationException("An untracked campaign or gameplay gate requires RAW topology editing.");
                var id = record?.Receipt.Id ?? authoring.AddonId;
                var directory = record?.Receipt.ScenarioDirectory ?? (authoring.LayoutVersion == 2 ? PrimaryScenarioDirectoryFor(authoring.AddonId) : "scenarios");
                var symbols = record?.Receipt.DifficultyDefines.ToArray() ?? authoring.FixedDifficultyDefines.ToArray();
                var actual = scope.Owner.DirectChildren.Where(child => child.TagName == "difficulty")
                    .Select(child => WccCampaignStructurePlanner.Decode(child, "define")).ToArray();
                if (!actual.SequenceEqual(symbols, StringComparer.Ordinal) || record is not null &&
                    (WccCampaignStructurePlanner.Decode(scope.Owner, "id") != record.Receipt.CampaignId || scope.Define != record.Receipt.CampaignDefine))
                    throw new InvalidOperationException("A campaign identity or its fixed difficulty symbols differ from the managed chapter receipt.");
                var structure = WccCampaignStructurePlanner.Read(source, SelectedMetadata(authoring, scope.Define, symbols));
                if (!structure.CanEdit) throw new InvalidOperationException(structure.Diagnostic);
                if (structure.PathRows.Count(row => row.Kind == WccCampaignPathKind.CfgInclude && row.Path == directory) != 1)
                    throw new InvalidOperationException("The chapter needs its exact managed scenario directory include.");
                entries.Add(new(id, WccCampaignStructurePlanner.Decode(scope.Owner, "id"), WccCampaignStructurePlanner.Decode(scope.Owner, "name"),
                    scope.Define, directory, Array.AsReadOnly(symbols), scope.Campaign, scope.Gate, record?.Receipt.IsPrimary ?? (record is null)));
            }
            if (managed.Count > entries.Count || managed.Any(record => !entries.Any(entry => entry.Id == record.Receipt.Id)) ||
                manifest.Document.Chapters.Any(record => !managed.Any(block => block.Receipt.Id == record.Id)) ||
                entries.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count ||
                entries.Select(entry => entry.CampaignId).Distinct(StringComparer.Ordinal).Count() != entries.Count ||
                entries.Select(entry => entry.CampaignDefine).Distinct(StringComparer.Ordinal).Count() != entries.Count)
                throw new InvalidOperationException("Managed chapter markers or campaign identities are repeated, orphaned or ambiguous.");
            return new(source, authoring, entries, true, "Chapter changes use checked Save. Removing a chapter removes only its proven owned, unshared files.", sources, manifest);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or JsonException or FormatException)
        { return new(source, authoring, entries, false, exception.Message, sources, manifest); }
    }

    public static WccCampaignChapterMutationPlan Add(WccCampaignChapterAuthoringSnapshot snapshot, WccCampaignChapterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(spec);
        var fresh = RequireFresh(snapshot);
        if (fresh.Authoring!.LayoutVersion != 2) throw new InvalidOperationException("This older WCC package uses root-level scenarios. Create a new package with chapter folders, or use RAW WML to reorganize its loading before adding chapters.");
        var issues = Validate(spec); if (issues.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, issues), nameof(spec));
        if (fresh.Chapters.Count >= 32 || fresh.Chapters.Any(chapter => string.Equals(chapter.Id, spec.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose a unique chapter ID; a package supports up to 32 chapters.");
        var addedIdentity = IdentityFor(fresh.Authoring.AddonId, spec);
        var reservedSymbols = addedIdentity.DifficultyDefines.Append(addedIdentity.CampaignDefine).ToHashSet(StringComparer.Ordinal);
        if (fresh.ProjectSources.Where(source => source.IsAuthored).SelectMany(source => WmlMacroDefinitionScanner.Scan(source.Text))
            .Any(definition => reservedSymbols.Contains(definition.Name)))
            throw new InvalidOperationException("The new chapter activation or difficulty symbols conflict with an existing authored macro. Choose another chapter ID or resolve that definition with RAW.");
        var presetCompatibility = WccCampaignLocalPresetStoragePlanner.ValidateChapterAdditionCompatibility(
            fresh.Source, fresh.ProjectSources, fresh.Manifest!.Document,
            WmlCampaignEvaluationPlanDiscovery.Discover(fresh.Source.Text, fresh.Source.Path),
            IdentityFor(fresh.Authoring.AddonId, spec).DifficultyDefines);
        if (presetCompatibility is not null) throw new InvalidOperationException(presetCompatibility);
        var domain = WmlTextDomainCapture.BeforeOffset(snapshot.Source.Text, fresh.Chapters[0].CampaignRange.StartOffset);
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is { } problem) throw new InvalidOperationException(problem);
        var root = Path.GetDirectoryName(snapshot.Source.Path)!;
        var images = CaptureChapterImages(root, fresh.Authoring.AddonId, spec);
        var renderedSpec = images.Spec;
        // Shared preset files are explicit early includes, rather than members of
        // the utils directory scan. A new gate inherits their existing package scope.
        var sharedPresetIncludes = fresh.Chapters.Select(chapter => WccCampaignStructurePlanner.Read(fresh.Source,
                SelectedMetadata(fresh.Authoring, chapter.CampaignDefine, chapter.FixedDifficultyDefines)).PathRows.Count(row =>
                row.Kind == WccCampaignPathKind.CfgInclude && row.Path == WccCampaignLocalPresetStoragePlanner.SharedStorageRelativePath)).ToArray();
        if (sharedPresetIncludes.Any(count => count > 1) || sharedPresetIncludes.Any(count => count == 1) && sharedPresetIncludes.Any(count => count == 0))
            throw new InvalidOperationException("Package-wide presets must have one explicit include in every existing campaign gate before adding a chapter.");
        var block = RenderChapterBlock(fresh.Authoring.AddonId, domain!, renderedSpec, false, sharedPresetIncludes.All(count => count == 1));
        var newline = snapshot.Source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = snapshot.Source.Text.Length == 0 || snapshot.Source.Text.EndsWith('\n') ? newline : newline + newline;
        var candidate = snapshot.Source.Text + prefix + block.Replace("\n", newline, StringComparison.Ordinal);
        var scenarioDirectory = "chapters/" + spec.Id + "/scenarios";
        var sources = new List<WmlStaticSource>();
        var identity = IdentityFor(fresh.Authoring.AddonId, renderedSpec);
        if (spec.CreateStarterScenario)
        {
            sources.Add(new(Path.Combine(root, scenarioDirectory.Replace('/', Path.DirectorySeparatorChar), "01_Start.cfg"),
                WccScenarioCreationWriter.RenderGuided(domain!, identity.CampaignId + "_01_Start", "A New Beginning",
                    "~add-ons/" + fresh.Authoring.AddonId + "/maps/" + identity.CampaignId + "_01_Start.map", identity.DifficultyDefines,
                    20, identity.CampaignId + "_Leader", identity.CampaignId + "_Enemy_Leader")));
            sources.Add(new(Path.Combine(root, "maps", identity.CampaignId + "_01_Start.map"), WccScenarioCreationWriter.DefaultMap));
        }
        else sources.Add(new(Path.Combine(root, scenarioDirectory.Replace('/', Path.DirectorySeparatorChar), "00_initial.cfg"), "# This chapter has no scenarios yet. Add a scenario in WCC.\n"));
        var receipt = new WccPackageAuthoredChapter(spec.Id, identity.CampaignId, identity.CampaignDefine, scenarioDirectory, domain!, identity.DifficultyDefines);
        var manifest = fresh.Manifest!;
        var ledger = sources.Select(source => OwnedText(root, spec.Id, source)).Concat(images.Files).ToArray();
        var document = manifest.Document with { Chapters = manifest.Document.Chapters.Append(receipt).ToArray(), OwnedFiles = manifest.Document.OwnedFiles.Concat(ledger).ToArray() };
        var replacements = new List<WccCampaignPackageSourceReplacement> { new(snapshot.Source, candidate) };
        AddManifestMutation(root, manifest, document, replacements, sources);
        return new(snapshot, candidate, sources, [Path.Combine(root, "chapters"), Path.Combine(root, "chapters", spec.Id),
            Path.Combine(root, scenarioDirectory.Replace('/', Path.DirectorySeparatorChar))], "Add", spec.Id, spec, replacements, images.Plan);
    }

    public static WccCampaignChapterMutationPlan Remove(WccCampaignChapterAuthoringSnapshot snapshot, string chapterId)
    {
        ArgumentNullException.ThrowIfNull(snapshot); var fresh = RequireFresh(snapshot);
        if (fresh.Chapters.Count <= 1) throw new InvalidOperationException("Keep at least one campaign entry in the package.");
        var entry = fresh.Chapters.SingleOrDefault(item => item.Id == chapterId) ?? throw new InvalidOperationException("The selected chapter is not in the current managed package capture.");
        var managed = ReadManaged(snapshot.Source, fresh.Manifest!.Document).SingleOrDefault(item => item.Receipt.Id == entry.Id);
        var ranges = managed is not null ? new[] { managed.Range } : new[] { entry.CampaignRange, entry.GameplayRange };
        if (managed is null && WmlAuthoredStructureScanner.Scan(entry.GameplayRange.Text).Directives.Any(item => item.Name == "define"))
            throw new InvalidOperationException("This original chapter contains inline helper definitions. Move or preserve those helpers with RAW before removing its chapter registration.");
        var text = snapshot.Source.Text;
        foreach (var range in ranges.OrderByDescending(item => item.StartOffset)) text = text.Remove(range.StartOffset, range.Length);
        var root = Path.GetDirectoryName(snapshot.Source.Path)!;
        var owned = fresh.Manifest.Document.OwnedFiles.Where(file => file.OwnerKind == "chapter" && file.OwnerId == entry.Id).ToArray();
        var candidatePaths = owned.Select(file => Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar))).ToHashSet(Paths);
        var outside = fresh.ProjectSources.Where(source => !candidatePaths.Contains(source.Path) && !Paths.Equals(source.Path, fresh.Manifest.Source?.Path) && !Paths.Equals(source.Path, snapshot.Source.Path))
            .Select(source => source.Text).Append(text).ToArray();
        var deletes = new List<WmlStaticSource>(); var binaryDeletes = new List<WccPackageBinaryFileDeletion>(); var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in owned)
        {
            var path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!IsChapterFile(file, entry) || !file.IsBinary && fresh.ProjectSources.Count(source => Paths.Equals(source.Path, path) && source.IsAuthored) != 1)
                retained.Add(file.RelativePath);
        }
        // A retained scenario can itself keep another owned map or image alive. Follow that
        // small owned set to a fixed point; never classify unrelated files as disposable.
        bool changed;
        do
        {
            changed = false;
            var references = outside.Concat(owned.Where(file => retained.Contains(file.RelativePath)).SelectMany(file =>
                fresh.ProjectSources.Where(source => Paths.Equals(source.Path, Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)))).Select(source => source.Text))).ToArray();
            foreach (var file in owned.Where(file => !retained.Contains(file.RelativePath)))
                if (IsReferenced(references, file.RelativePath, fresh.Authoring!.AddonId)) { retained.Add(file.RelativePath); changed = true; }
        } while (changed);
        foreach (var file in owned.Where(file => !retained.Contains(file.RelativePath)))
        {
            var path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (file.IsBinary) binaryDeletes.Add(new(path, file.ContentHash));
            else deletes.Add(fresh.ProjectSources.Single(source => Paths.Equals(source.Path, path) && source.IsAuthored));
        }
        var manifest = fresh.Manifest;
        var document = manifest.Document with { Chapters = manifest.Document.Chapters.Where(chapter => chapter.Id != chapterId).ToArray(),
            OwnedFiles = manifest.Document.OwnedFiles.Where(file => !(file.OwnerKind == "chapter" && file.OwnerId == chapterId)).ToArray() };
        var survivingPresetSources = fresh.ProjectSources.Where(source =>
                !Paths.Equals(source.Path, snapshot.Source.Path) && !Paths.Equals(source.Path, manifest.Source?.Path) &&
                !deletes.Any(deleted => Paths.Equals(deleted.Path, source.Path)))
            .Append(new WmlStaticSource(snapshot.Source.Path, text)).ToArray();
        var presetCleanup = WccCampaignLocalPresetStoragePlanner.InspectChapterRemovalStorage(
            fresh.Source, fresh.ProjectSources, manifest.Document, entry,
            WmlCampaignEvaluationPlanDiscovery.Discover(fresh.Source.Text, fresh.Source.Path), survivingPresetSources);
        if (presetCleanup.ReceiptIdsToRemove.Count != 0)
            document = WccPackageContentAuthoringManifest.Apply(document, [], presetCleanup.ReceiptIdsToRemove);
        foreach (var relative in presetCleanup.RetainedFiles) retained.Add(relative);
        foreach (var utility in presetCleanup.SourceDeletionCandidates)
        {
            var relative = Path.GetRelativePath(root, utility.Path).Replace('\\', '/');
            var remaining = fresh.ProjectSources.Where(source => !Paths.Equals(source.Path, utility.Path) &&
                    !Paths.Equals(source.Path, snapshot.Source.Path) && !Paths.Equals(source.Path, manifest.Source?.Path) &&
                    !deletes.Any(deleted => Paths.Equals(deleted.Path, source.Path)))
                .Select(source => source.Text).Append(text);
            if (IsReferenced(remaining, relative, fresh.Authoring!.AddonId)) retained.Add(relative);
            else deletes.Add(utility);
        }
        var creates = new List<WmlStaticSource>(); var replacements = new List<WccCampaignPackageSourceReplacement> { new(snapshot.Source, text) };
        if (manifest.Source is not null) AddManifestMutation(root, manifest, document, replacements, creates);
        return new(snapshot, text, creates, [], "Remove", chapterId, replacements: replacements,
            deletes: deletes, binaryDeletes: binaryDeletes, retainedFiles: retained);
    }

    public static WccCampaignChapterMutationPlan Rebuild(WccCampaignChapterAuthoringSnapshot snapshot, WccCampaignChapterMutationPlan mutation)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(mutation);
        if (!WccCampaignPackageEdit.Same(snapshot.Source, mutation.Snapshot.Source)) throw new InvalidOperationException("The chapter source changed after its authoring recipe was captured.");
        if (VerifyUploadFreshness(mutation) is { } imageProblem) throw new InvalidOperationException(imageProblem);
        return mutation.AddedChapter is { } chapter ? Add(snapshot, chapter) : Remove(snapshot, mutation.ChapterId);
    }
    public static string? VerifyUploadFreshness(WccCampaignChapterMutationPlan plan)
    {
        foreach (var creation in plan.BinaryDestinations.Creates)
        {
            var expected = creation.SourceRevision;
            var current = WccTextDiskRevisionCapture.CaptureBinary(expected.ProjectRootPath, expected.SourcePath);
            if (!current.IsCaptured || current.Revision is null || !expected.IsSameRevisionAs(current.Revision))
                return "A selected chapter image changed its bytes, native identity or route. Reopen the chapter wizard: " + expected.SourcePath;
        }
        return null;
    }
    public static WccCampaignAuthoringMetadata? ResolveSelectedAuthoring(WccCampaignAuthoringMetadata? authoring,
        WmlStaticSource source, string? selectedDefine, IReadOnlyList<WmlStaticSource>? projectSources = null)
    {
        if (authoring is null || selectedDefine is null) return authoring;
        var snapshot = Read(source, authoring, projectSources);
        if (!snapshot.CanEdit) return selectedDefine == authoring.CampaignDefine ? authoring : null;
        var match = snapshot.Chapters.SingleOrDefault(chapter => chapter.CampaignDefine == selectedDefine);
        return match is null ? null : SelectedMetadata(authoring, selectedDefine, match.FixedDifficultyDefines);
    }
    public static string MaskOtherChaptersForStructure(WmlStaticSource source, string selectedDefine)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentException.ThrowIfNullOrWhiteSpace(selectedDefine);
        var chars = source.Text.ToCharArray();
        foreach (var scope in ReadScopes(source).Where(item => item.Define != selectedDefine))
            foreach (var range in new[] { scope.Campaign, scope.Gate })
                for (var offset = range.StartOffset; offset < range.StartOffset + range.Length; offset++)
                    if (chars[offset] is not ('\r' or '\n')) chars[offset] = ' ';
        return new(chars);
    }
    // Legacy source-only lookup remains read-only compatibility. Compact ownership requires its captured companion.
    public static string PrimaryScenarioDirectoryFor(string addonId) => "chapters/" + addonId + "/scenarios";
    public static string? ScenarioDirectoryFor(WmlStaticSource source, string campaignDefine, WccCampaignAuthoringMetadata? authoring,
        IReadOnlyList<WmlStaticSource>? projectSources = null)
    {
        var chapters = Read(source, authoring, projectSources);
        return chapters.CanEdit ? chapters.Chapters.SingleOrDefault(chapter => chapter.CampaignDefine == campaignDefine)?.ScenarioDirectory
            : null;
    }
    public static IReadOnlyList<string> Validate(WccCampaignChapterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec); var issues = new List<string>();
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(spec.Id) is { } id) issues.Add("Chapter ID: " + id);
        if (string.IsNullOrWhiteSpace(spec.Name)) issues.Add("Enter a chapter name.");
        foreach (var value in new[] { spec.Name, spec.Icon, spec.Image, spec.Background }) if (WccCreationSourceWriter.ValidateText(value) is { } issue) issues.Add(issue);
        if (WccCreationSourceWriter.ValidateText(spec.Description, true) is { } description) issues.Add(description);
        foreach (var file in new[] { spec.IconFilePath, spec.ImageFilePath, spec.BackgroundFilePath }) ValidateImageFile(file, issues);
        if (spec.Difficulties is null || spec.Difficulties.Count is < 1 or > 16 || spec.Difficulties.Any(row => row is null)) issues.Add("Choose between one and 16 chapter difficulties.");
        else
        {
            if (spec.Difficulties.Count(row => row.IsDefault) > 1) issues.Add("Choose one default chapter difficulty.");
            foreach (var difficulty in spec.Difficulties)
            {
                if (string.IsNullOrWhiteSpace(difficulty.Label)) issues.Add("Every chapter difficulty needs a name.");
                foreach (var value in new[] { difficulty.Label, difficulty.Description, difficulty.Image }) if (WccCreationSourceWriter.ValidateText(value) is { } issue) issues.Add(issue);
                ValidateImageFile(difficulty.ImageFilePath, issues);
            }
        }
        return Array.AsReadOnly(issues.Distinct(StringComparer.Ordinal).ToArray());
    }
    public static string RenderBlock(string addonId, string textDomain, WccCampaignChapterSpec spec) => RenderChapterBlock(addonId, textDomain, spec, false, false);
    internal static string RenderPrimaryBlock(string addonId, string textDomain, WccCampaignChapterSpec spec) => RenderChapterBlock(addonId, textDomain, spec, true, false);
    private static string RenderChapterBlock(string addonId, string textDomain, WccCampaignChapterSpec spec,
        bool primary, bool includeSharedPresets)
    {
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(addonId) is { } addonIssue) throw new ArgumentException(addonIssue, nameof(addonId));
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(textDomain) is { } domainIssue) throw new ArgumentException(domainIssue, nameof(textDomain));
        var issues = Validate(spec); if (issues.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, issues), nameof(spec));
        if (spec.IconFilePath is not null || spec.ImageFilePath is not null || spec.BackgroundFilePath is not null || spec.Difficulties.Any(row => row.ImageFilePath is not null))
            throw new InvalidOperationException("Capture uploaded chapter images before rendering ordinary WML.");
        var identity = primary ? (CampaignId: addonId, CampaignDefine: "CAMPAIGN_" + addonId.ToUpperInvariant(), DifficultyDefines: DifficultyDefinesFor(spec.Difficulties.Count)) : IdentityFor(addonId, spec);
        var directory = primary ? PrimaryScenarioDirectoryFor(addonId) : "chapters/" + spec.Id + "/scenarios";
        var result = new StringBuilder().Append(Begin).Append(spec.Id).Append('\n').Append("#textdomain ").Append(textDomain)
            .Append("\n[campaign]\n    id=").Append(identity.CampaignId).Append("\n    name=").Append(Q(spec.Name, true)).Append("\n    define=").Append(identity.CampaignDefine)
            .Append("\n    icon=").Append(Q(CampaignPresentationImagePolicy.Fit(spec.Icon, CampaignPresentationImagePurpose.CampaignIcon)))
            .Append("\n    image=").Append(Q(CampaignPresentationImagePolicy.Fit(spec.Image, CampaignPresentationImagePurpose.CampaignImage)))
            .Append("\n    background=").Append(Q(CampaignPresentationImagePolicy.Fit(spec.Background, CampaignPresentationImagePurpose.Background))).Append('\n');
        if (spec.Description.Length != 0) result.Append("    description=").Append(Q(spec.Description, true, true)).Append('\n');
        if (spec.CreateStarterScenario) result.Append("    first_scenario=").Append(identity.CampaignId).Append("_01_Start\n");
        WccCampaignCreationPlanner.AppendDifficultyMenu(spec.Difficulties, identity.DifficultyDefines,
            spec.Difficulties.Select(row => CampaignPresentationImagePolicy.Fit(row.Image, CampaignPresentationImagePurpose.DifficultyIcon)).ToArray(), result);
        result.Append("[/campaign]\n\n#ifdef ").Append(identity.CampaignDefine).Append('\n');
        if (includeSharedPresets) result.Append("{~add-ons/").Append(addonId).Append('/')
            .Append(WccCampaignLocalPresetStoragePlanner.SharedStorageRelativePath).Append("}\n");
        result.Append("{~add-ons/").Append(addonId).Append("/utils}\n\n[units]\n    {~add-ons/")
            .Append(addonId).Append("/units}\n[/units]\n\n{~add-ons/").Append(addonId).Append('/').Append(directory).Append("}\n#endif\n").Append(End).Append(spec.Id).Append('\n');
        return result.ToString();
    }
    public static string CampaignDefineFor(string addonId, WccCampaignChapterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(addonId) is { } addonIssue) throw new ArgumentException(addonIssue, nameof(addonId));
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(spec.Id) is { } chapterIssue) throw new ArgumentException(chapterIssue, nameof(spec));
        return "CAMPAIGN_" + (addonId + "_" + spec.Id).ToUpperInvariant();
    }
    internal static string[] DifficultyDefinesFor(int count) => Enumerable.Range(1, count).Select(index => "DIFFICULTY_" + index).ToArray();
    private static (string CampaignId, string CampaignDefine, string[] DifficultyDefines) IdentityFor(string addonId, WccCampaignChapterSpec spec) =>
        (addonId + "_" + spec.Id, CampaignDefineFor(addonId, spec), DifficultyDefinesFor(spec.Difficulties.Count));
    private static string Q(string value, bool translated = false, bool multiline = false) => WccCreationSourceWriter.EncodeText(value, translated, multiline);
    private static WccCampaignChapterAuthoringSnapshot RequireFresh(WccCampaignChapterAuthoringSnapshot snapshot)
    { var fresh = Read(snapshot.Source, snapshot.Authoring, snapshot.ProjectSources); if (!snapshot.CanEdit || !fresh.CanEdit) throw new InvalidOperationException(fresh.Diagnostic); return fresh; }
    private static bool Contains(WmlStaticSourceRange outer, WmlStaticSourceRange inner) => inner.StartOffset >= outer.StartOffset && inner.StartOffset + inner.Length <= outer.StartOffset + outer.Length;
    private static WccCampaignAuthoringMetadata SelectedMetadata(WccCampaignAuthoringMetadata original, string define, IEnumerable<string> symbols) => new()
    { LayoutVersion = original.LayoutVersion, AddonId = original.AddonId, CampaignDefine = define, MainRelativePath = original.MainRelativePath,
        FixedDifficultyDefines = symbols.ToList(), UsesExplicitDifficultyValues = original.UsesExplicitDifficultyValues };
    private static IReadOnlyList<Scope> ReadScopes(WmlStaticSource source)
    {
        var shape = WccCampaignDependencyAuthoringPlanner.MaskManagedDependencyBlocksForStructure(source);
        var scan = WmlAuthoredStructureScanner.Scan(shape);
        if (scan.Problem is { } problem) throw new InvalidOperationException(problem);
        var result = new List<Scope>();
        foreach (var block in WmlBlockScanner.FindTopLevelBlockSpans(scan.MaskedText).Where(block => block.TagName == "campaign"))
        {
            var node = new WccCampaignStructureNode("campaign", block.StartIndex, null) { End = block.EndIndexExclusive };
            var owner = WccCampaignStructurePlanner.Project(source, node);
            var define = WccCampaignStructurePlanner.Decode(owner, "define");
            var candidates = new List<WmlStaticSourceRange>();
            var stack = new Stack<(WmlAuthoredDirective Directive, bool Selected)>();
            foreach (var directive in scan.Directives.Where(item => item.DefinitionDepth == 0))
            {
                var line = scan.Lines[directive.Line];
                if (WmlAuthoredStructureScanner.IsConditionalStart(directive.Name))
                    stack.Push((directive, source.Text[line.StartOffset..line.ContentEndOffset].Trim() == "#ifdef " + define));
                else if (directive.Name == "endif")
                {
                    if (stack.Count == 0) throw new InvalidOperationException("A chapter gate has no matching opening directive.");
                    var opening = stack.Pop();
                    if (opening.Selected)
                    {
                        if (stack.Count != 0) throw new InvalidOperationException("A chapter gameplay gate is nested in another condition.");
                        var begin = scan.Lines[opening.Directive.Line].StartOffset;
                        candidates.Add(new(source, begin, line.EndOffsetExclusive - begin));
                    }
                }
            }
            if (stack.Count != 0 || candidates.Count != 1) throw new InvalidOperationException("Each managed chapter needs one complete independent gameplay gate.");
            result.Add(new(new(source, block.StartIndex, block.EndIndexExclusive - block.StartIndex), candidates[0], owner, define));
        }
        return result;
    }

    private static WccPackageAuthoringManifestSnapshot ReadManifest(WmlStaticSource source, string addonId, IReadOnlyList<WmlStaticSource> sources) =>
        WccPackageAuthoringManifest.Read(source, addonId, sources);
    private static IReadOnlyList<Managed> ReadManaged(WmlStaticSource source, WccPackageAuthoringDocument manifest)
    {
        var comments = WmlEditorAnalysis.Build(source.Text).Tokens.Where(token => token.Kind == WmlEditorTokenKind.Comment).ToArray();
        var result = new List<Managed>(); WccPackageAuthoredChapter? current = null; var start = -1;
        foreach (var token in comments)
        {
            var raw = source.Text.Substring(token.StartOffset, token.Length).TrimEnd('\r', '\n');
            if (raw.StartsWith(Begin, StringComparison.Ordinal))
            {
                if (current is not null || result.Count >= 32) throw new InvalidOperationException("Chapter ownership markers are nested or exceed the bounded package scope.");
                if (token.StartOffset != 0 && source.Text[token.StartOffset - 1] is not ('\r' or '\n')) throw new InvalidOperationException("Chapter ownership markers must occupy their own root-level lines.");
                var id = raw[Begin.Length..];
                if (WccCampaignCreationTokenPolicy.ValidateAddonId(id) is not null)
                    throw new InvalidOperationException("This earlier chapter ownership format is no longer authored by WCC. Preserve its files and use RAW WML.");
                current = manifest.Chapters.SingleOrDefault(chapter => chapter.Id == id) ??
                    throw new InvalidOperationException("Keep the package's .wcc authoring companion to edit this chapter; its exact captured receipt is missing.");
                if (WccCampaignCreationTokenPolicy.ValidateAddonId(current.Id) is not null ||
                    current.ScenarioDirectory != "chapters/" + current.Id + "/scenarios" ||
                    WccCampaignCreationTokenPolicy.ValidateTextDomain(current.TextDomain) is not null ||
                    current.DifficultyDefines is null || current.DifficultyDefines.Count is < 1 or > 16)
                    throw new InvalidOperationException("The chapter ownership receipt is unsupported or malformed.");
                start = token.StartOffset;
            }
            else if (raw.StartsWith(End, StringComparison.Ordinal))
            {
                if (current is null || raw[End.Length..] != current.Id) throw new InvalidOperationException("A chapter ownership marker is unmatched.");
                var end = token.StartOffset + token.Length;
                if (end < source.Text.Length && source.Text[end] == '\r') end++;
                if (end < source.Text.Length && source.Text[end] == '\n') end++;
                result.Add(new(current, new(source, start, end - start))); current = null;
            }
        }
        if (current is not null) throw new InvalidOperationException("A chapter ownership marker is not closed.");
        return result;
    }
    private static void AddManifestMutation(string root, WccPackageAuthoringManifestSnapshot snapshot, WccPackageAuthoringDocument document,
        List<WccCampaignPackageSourceReplacement> replacements, List<WmlStaticSource> creates)
    {
        var text = WccPackageAuthoringManifest.Serialize(document);
        if (snapshot.Source is { } source)
        { if (source.Text != text) replacements.Add(new(source, text)); }
        else creates.Add(new(Path.Combine(root, WccPackageAuthoringManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar)), text));
    }
    internal static WccPackageOwnedFile OwnedText(string root, string chapterId, WmlStaticSource source) =>
        new("chapter", chapterId, Path.GetRelativePath(root, source.Path).Replace('\\', '/'), Path.GetExtension(source.Path) == ".map" ? "map" : "scenario", source.ContentHash);
    private static bool IsChapterFile(WccPackageOwnedFile file, WccCampaignChapterAuthoringEntry entry) =>
        file.Role == "scenario" && file.RelativePath.StartsWith(entry.ScenarioDirectory + "/", StringComparison.Ordinal) && !file.IsBinary ||
        file.Role == "map" && file.RelativePath.StartsWith("maps/", StringComparison.Ordinal) && file.RelativePath.EndsWith(".map", StringComparison.Ordinal) && !file.IsBinary ||
        file.Role == "menu-image" && file.RelativePath.StartsWith("images/menu/", StringComparison.Ordinal) && file.IsBinary;
    private static bool IsReferenced(IEnumerable<string> texts, string relative, string addonId)
    {
        var reference = relative.StartsWith("images/", StringComparison.Ordinal) ? relative["images/".Length..] : relative;
        var segments = relative.Split('/');
        var directoryIncludes = Enumerable.Range(1, segments.Length - 1).Select(count => "{~add-ons/" + addonId + "/" + string.Join('/', segments.Take(count)) + "}").ToArray();
        return texts.Any(text => text.Contains(relative, StringComparison.Ordinal) || text.Contains(reference, StringComparison.Ordinal) || directoryIncludes.Any(include => text.Contains(include, StringComparison.Ordinal)));
    }
    private static void ValidateImageFile(string? file, List<string> issues)
    {
        if (file is null) return;
        try
        {
            var path = Path.GetFullPath(file);
            if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".webp" or ".jpg" or ".jpeg" or ".bmp" or ".gif")) issues.Add("Choose a PNG, WebP, JPEG, BMP or GIF image.");
            else if (!File.Exists(path)) issues.Add("The selected chapter image is no longer available: " + path);
        }
        catch (Exception issue) when (issue is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { issues.Add(issue.Message); }
    }
    private static (WccCampaignChapterSpec Spec, WccContentBinaryDestinationPlan Plan, WccPackageOwnedFile[] Files) CaptureChapterImages(string root, string addonId, WccCampaignChapterSpec spec)
    {
        var creates = new List<WccContentBinaryCreation>(); var files = new List<WccPackageOwnedFile>();
        string Image(string reference, string? file, string slot, CampaignPresentationImagePurpose purpose)
        {
            if (file is null) return CampaignPresentationImagePolicy.Fit(reference, purpose);
            var path = Path.GetFullPath(file);
            var source = WccTextDiskRevisionCapture.CaptureBinary(Path.GetDirectoryName(path)!, path);
            if (!source.IsCaptured || source.Revision is null) throw new InvalidOperationException("The chapter image could not be captured: " + source.ErrorMessage);
            var relative = "images/menu/" + addonId + "-chapter-" + spec.Id + "-" + slot + Path.GetExtension(path).ToLowerInvariant();
            creates.Add(new(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), source.Revision));
            files.Add(new("chapter", spec.Id, relative, "menu-image", source.Revision.RawByteSha256, true));
            return CampaignPresentationImagePolicy.Fit(relative["images/".Length..], purpose);
        }
        var normalized = spec with
        {
            Icon = Image(spec.Icon, spec.IconFilePath, "icon", CampaignPresentationImagePurpose.CampaignIcon),
            Image = Image(spec.Image, spec.ImageFilePath, "image", CampaignPresentationImagePurpose.CampaignImage),
            Background = Image(spec.Background, spec.BackgroundFilePath, "background", CampaignPresentationImagePurpose.Background),
            IconFilePath = null, ImageFilePath = null, BackgroundFilePath = null,
            Difficulties = spec.Difficulties.Select((row, index) => row with { Image = Image(row.Image, row.ImageFilePath, "difficulty-" + (index + 1), CampaignPresentationImagePurpose.DifficultyIcon), ImageFilePath = null }).ToArray()
        };
        return (normalized, new(creates, []), files.ToArray());
    }
}
