using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using System.Text;
using Wcc.Core.Compatibility;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.Core.Projects.Creation;

public static class WccCampaignCreationPlanner
{
    private static readonly string[] StandardDirectories =
        ["maps", "units", "utils", "images", "music", "sounds", "lua", "translations", ".wcc"];
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".webp", ".jpg", ".jpeg", ".bmp", ".gif" };

    public static IReadOnlyList<string> Validate(WccCampaignCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<string>();
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(request.Id) is { } idIssue) issues.Add(idIssue);
        if (!Enum.IsDefined(request.Mode)) issues.Add("Choose Empty or Guided creation.");
        CheckText(request.Name, "Campaign name", false, issues);
        if (string.IsNullOrWhiteSpace(request.Name)) issues.Add("Enter a campaign name.");
        CheckText(request.Description, "Campaign description", true, issues);
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(request.TextDomain) is { } domainIssue) issues.Add(domainIssue);
        if (request.CoreRevision is { } pin && (!WccCompatibilityPolicy.CanCreateFor(pin.TargetWesnothVersion) ||
            string.IsNullOrWhiteSpace(pin.ExactWesnothVersion) || string.IsNullOrWhiteSpace(pin.ContentFingerprint)))
            issues.Add("The supplied Core revision must be an exact fingerprinted Wesnoth 1.20 authoring revision.");

        ValidateImage(request.Icon, "Campaign icon", issues);
        ValidateImage(request.Image, "Campaign image", issues);
        ValidateImage(request.Background, "Campaign background", issues);
        if (request.Difficulties is null) issues.Add("Every campaign needs a difficulty collection.");
        if (request.Mode == WccCampaignCreationMode.Guided || request.Difficulties?.Count > 0)
        {
            if (request.Difficulties is null || request.Difficulties.Count is < 1 or > WmlCampaignDifficultyObserver.MaxDifficulties)
                issues.Add("Choose between 1 and 16 difficulties.");
            else
            {
                if (request.Difficulties.Any(difficulty => difficulty is null)) issues.Add("Every difficulty needs a definition.");
                else
                {
                    if (request.Difficulties.Count(difficulty => difficulty.IsDefault) > 1)
                        issues.Add("Choose only one default difficulty.");
                    foreach (var difficulty in request.Difficulties)
                    {
                        CheckText(difficulty.Label, "Difficulty name", false, issues);
                        if (string.IsNullOrWhiteSpace(difficulty.Label)) issues.Add("Enter a name for every difficulty.");
                        CheckText(difficulty.Description, "Difficulty description", false, issues);
                        ValidateImage(new(difficulty.Image, difficulty.ImageFilePath), "Difficulty image", issues);
                    }
                }
            }
        }

        if (request.Chapters is null || request.Chapters.Count > 31 || request.Chapters.Any(chapter => chapter is null))
            issues.Add("Choose up to 31 additional chapters.");
        else
        {
            if (request.Chapters.Select(chapter => chapter.Id).Append(request.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Chapters.Count + 1)
                issues.Add("Every chapter needs its own unique portable ID.");
            foreach (var chapter in request.Chapters)
            {
                issues.AddRange(WccCampaignChapterAuthoringPlanner.Validate(chapter with
                { IconFilePath = null, ImageFilePath = null, BackgroundFilePath = null, Difficulties = chapter.Difficulties?.Select(difficulty => difficulty is null ? null! : difficulty with { ImageFilePath = null }).ToArray() ?? [] }));
                ValidateImage(new(chapter.Icon, chapter.IconFilePath), "Chapter icon", issues);
                ValidateImage(new(chapter.Image, chapter.ImageFilePath), "Chapter image", issues);
                ValidateImage(new(chapter.Background, chapter.BackgroundFilePath), "Chapter background", issues);
                foreach (var difficulty in chapter.Difficulties ?? [])
                    if (difficulty is not null) ValidateImage(new(difficulty.Image, difficulty.ImageFilePath), "Chapter difficulty image", issues);
            }
        }
        if (request.Dependencies is null || request.Dependencies.Count > 64 || request.Dependencies.Any(dependency => dependency is null))
            issues.Add("Choose up to 64 complete dependency recipes.");
        else
        {
            foreach (var dependency in request.Dependencies) issues.AddRange(WccCampaignDependencyAuthoringPlanner.ValidateSpec(dependency));
            if (request.Dependencies.Select(dependency => dependency.Id).Distinct().Count() != request.Dependencies.Count ||
                request.Dependencies.Select(dependency => dependency.AddonId).Distinct(StringComparer.Ordinal).Count() != request.Dependencies.Count ||
                request.Dependencies.Any(dependency => dependency.AddonId == request.Id))
                issues.Add("Dependency identities and add-on IDs must be unique and external to this project.");
        }
        try
        {
            if (string.IsNullOrWhiteSpace(request.ParentDirectory)) throw new ArgumentException("Choose an existing parent directory.");
            var parent = Path.GetFullPath(request.ParentDirectory);
            ValidateDirectoryRoute(parent);
            if (IsId(request.Id) && Directory.EnumerateFileSystemEntries(parent).Any(entry =>
                string.Equals(Path.GetFileName(entry), request.Id, StringComparison.OrdinalIgnoreCase)))
                issues.Add("A file or directory with this campaign ID already exists in the selected parent directory.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { issues.Add("Campaign location: " + error.Message); }
        return Array.AsReadOnly(issues.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static WccCampaignCreationPlan Plan(WccCampaignCreationRequest request)
    {
        var issues = Validate(request);
        if (issues.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, issues), nameof(request));
        var root = Path.Combine(Path.GetFullPath(request.ParentDirectory), request.Id);
        var campaignDefine = "CAMPAIGN_" + request.Id.ToUpperInvariant();
        var difficulties = request.Mode == WccCampaignCreationMode.Empty && request.Difficulties.Count == 0
            ? new[] { new WccCampaignDifficultySpec("Normal", "", "units/human-loyalists/lieutenant.png", true) }
            : request.Difficulties.ToArray();
        var defines = WccCampaignChapterAuthoringPlanner.DifficultyDefinesFor(difficulties.Length);
        var assets = new List<WccCreationAssetCopy>();
        string Image(WccCampaignImageSpec? spec, string stem, string fallback, CampaignPresentationImagePurpose purpose)
        {
            if (spec?.FilePath is null) return CampaignPresentationImagePolicy.Fit(
                string.IsNullOrWhiteSpace(spec?.Reference) ? fallback : spec.Reference, purpose);
            var relative = "images/menu/" + request.Id + "-" + stem + Path.GetExtension(spec.FilePath).ToLowerInvariant();
            assets.Add(new(Path.GetFullPath(spec.FilePath), relative));
            return CampaignPresentationImagePolicy.Fit(relative["images/".Length..], purpose);
        }
        var icon = Image(request.Icon, "campaign-icon", "units/human-loyalists/lieutenant.png", CampaignPresentationImagePurpose.CampaignIcon);
        var image = Image(request.Image, "campaign-image", "maps/wesnoth.webp", CampaignPresentationImagePurpose.CampaignImage);
        var background = Image(request.Background, "campaign-background", "maps/background.webp", CampaignPresentationImagePurpose.Background);
        var difficultyImages = difficulties.Select((difficulty, index) => Image(
            new(difficulty.Image, difficulty.ImageFilePath), "difficulty-" + (index + 1), "units/human-loyalists/lieutenant.png",
            CampaignPresentationImagePurpose.DifficultyIcon)).ToArray();
        var starter = request.CreateStarterScenario;
        var main = RenderMain(request, campaignDefine, difficulties, defines, difficultyImages, icon, image, background, starter);
        var files = new List<WccCreationSourceFile> { new("_main.cfg", main), new("README-WCC.txt", Readme(request, starter)) };
        if (starter)
        {
            files.Add(new(WccCampaignChapterAuthoringPlanner.PrimaryScenarioDirectoryFor(request.Id) + "/01_Start.cfg", RenderStarter(request, defines)));
            files.Add(new("maps/01_Start.map", WccScenarioCreationWriter.DefaultMap));
        }
        var metadata = new WccProjectMetadata
        {
            Compatibility = new WccProjectCompatibility
            {
                TargetWesnothVersion = WccCompatibilityPolicy.CurrentAuthoringTarget,
                CoreRevision = request.CoreRevision
            },
            Authoring = new WccCampaignAuthoringMetadata
            {
                LayoutVersion = 2,
                AddonId = request.Id,
                CampaignDefine = campaignDefine,
                FixedDifficultyDefines = defines.ToList(),
                UsesExplicitDifficultyValues = true
            }
        };
        // Identity, topology and exact owned-file receipts travel with the package, even without dependencies.
        var primary = new WccPackageAuthoredChapter(request.Id, request.Id, campaignDefine,
            WccCampaignChapterAuthoringPlanner.PrimaryScenarioDirectoryFor(request.Id), request.TextDomain, defines, true);
        var document = WccPackageAuthoringManifest.Empty(request.Id) with
        {
            Chapters = new[] { primary },
            OwnedFiles = files.Where(file => file.RelativePath.EndsWith(".cfg", StringComparison.Ordinal) && file.RelativePath != "_main.cfg" || file.RelativePath.EndsWith(".map", StringComparison.Ordinal))
                .Select(file => new WccPackageOwnedFile("chapter", request.Id, file.RelativePath, file.RelativePath.EndsWith(".map", StringComparison.Ordinal) ? "map" : "scenario", WccContentHash.ComputeText(file.Text))).ToArray()
        };
        files.Add(new(WccPackageAuthoringManifest.RelativePath, WccPackageAuthoringManifest.Serialize(document)));
        var directories = new[] { "chapters", "chapters/" + request.Id, WccCampaignChapterAuthoringPlanner.PrimaryScenarioDirectoryFor(request.Id) }.Concat(StandardDirectories).ToList();
        foreach (var chapter in request.Chapters)
        {
            var captured = chapter with {
                Icon = Image(new(chapter.Icon, chapter.IconFilePath), "chapter-" + chapter.Id + "-icon", "units/human-loyalists/lieutenant.png", CampaignPresentationImagePurpose.CampaignIcon),
                Image = Image(new(chapter.Image, chapter.ImageFilePath), "chapter-" + chapter.Id + "-image", "maps/wesnoth.webp", CampaignPresentationImagePurpose.CampaignImage),
                Background = Image(new(chapter.Background, chapter.BackgroundFilePath), "chapter-" + chapter.Id + "-background", "maps/background.webp", CampaignPresentationImagePurpose.Background),
                IconFilePath = null, ImageFilePath = null, BackgroundFilePath = null,
                Difficulties = chapter.Difficulties.Select((difficulty, index) => difficulty with
            {
                Image = Image(new(difficulty.Image, difficulty.ImageFilePath), "chapter-" + chapter.Id + "-difficulty-" + (index + 1),
                    "units/human-loyalists/lieutenant.png", CampaignPresentationImagePurpose.DifficultyIcon), ImageFilePath = null
            }).ToArray() };
            var currentSources = files.Select(file => new WmlStaticSource(Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), file.Text)).ToArray();
            var mutation = WccCampaignChapterAuthoringPlanner.Add(WccCampaignChapterAuthoringPlanner.Read(
                new(Path.Combine(root, "_main.cfg"), main), metadata.Authoring, currentSources), captured);
            foreach (var replacement in mutation.SourceReplacements)
            {
                var relative = Path.GetRelativePath(root, replacement.Source.Path).Replace('\\', '/');
                var index = files.FindIndex(file => file.RelativePath == relative);
                if (index < 0) throw new InvalidOperationException("A chapter recipe targeted an unplanned creation source.");
                files[index] = new(relative, replacement.Candidate);
            }
            main = mutation.SourceReplacements.Single(file => Path.GetFileName(file.Source.Path) == "_main.cfg").Candidate;
            files.AddRange(mutation.SourceCreates.Select(source => new WccCreationSourceFile(Path.GetRelativePath(root, source.Path).Replace('\\', '/'), source.Text)));
            directories.AddRange(mutation.DirectoryPaths.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')));
        }
        files[0] = new("_main.cfg", main);
        if (request.Dependencies.Count != 0)
        {
            var capturedSources = files.Where(file => (file.RelativePath.EndsWith(".cfg", StringComparison.Ordinal) || file.RelativePath.EndsWith(".pbl", StringComparison.Ordinal) || file.RelativePath == WccCampaignDependencyAuthoringPlanner.PackageManifestRelativePath))
                .Select(file => new WmlStaticSource(Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), file.Text)).ToArray();
            var mainSource = capturedSources.Single(source => source.Path == Path.Combine(root, "_main.cfg"));
            IReadOnlyList<WccCampaignDependencySpec> dependencies = request.Dependencies;
            if (dependencies.Any(dependency => dependency.AppliesToAllCampaigns))
            {
                var chapters = WccCampaignChapterAuthoringPlanner.Read(mainSource, metadata.Authoring, capturedSources);
                if (!chapters.CanEdit) throw new InvalidOperationException(chapters.Diagnostic);
                var targets = Array.AsReadOnly(chapters.Chapters.Select(chapter => chapter.CampaignDefine).ToArray());
                dependencies = dependencies.Select(dependency => dependency.AppliesToAllCampaigns
                    ? new WccCampaignDependencySpec(dependency.Id, dependency.AddonId, targets, dependency.Steps,
                        dependency.DeclareDependency, true) : dependency).ToArray();
            }
            var mutation = WccCampaignDependencyAuthoringPlanner.Plan(
                WccCampaignDependencyAuthoringPlanner.Read(mainSource, request.Id, capturedSources), dependencies);
            foreach (var replacement in mutation.SourceReplacements)
            {
                var relative = Path.GetRelativePath(root, replacement.Source.Path).Replace(Path.DirectorySeparatorChar, '/');
                var index = files.FindIndex(file => file.RelativePath == relative);
                if (index < 0) throw new InvalidOperationException("A dependency recipe targeted an unplanned creation source.");
                files[index] = new(relative, replacement.Candidate);
            }
            files.AddRange(mutation.SourceCreates.Select(source => new WccCreationSourceFile(
                Path.GetRelativePath(root, source.Path).Replace(Path.DirectorySeparatorChar, '/'), source.Text)));
            directories.Add("loaders");
        }
        if (assets.Count != 0)
        {
            directories.Add("images/menu");
            var manifestIndex = files.FindIndex(file => file.RelativePath == WccPackageAuthoringManifest.RelativePath);
            var capturedSources = files.Select(file => new WmlStaticSource(Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), file.Text)).ToArray();
            var current = WccPackageAuthoringManifest.Read(capturedSources.Single(source => Path.GetFileName(source.Path) == "_main.cfg"), request.Id, capturedSources).Document;
            var ownedImages = assets.Select(asset =>
            {
                var chapter = request.Chapters.FirstOrDefault(item => asset.RelativePath.StartsWith("images/menu/" + request.Id + "-chapter-" + item.Id + "-", StringComparison.Ordinal));
                using var stream = File.OpenRead(asset.SourcePath);
                return new WccPackageOwnedFile("chapter", chapter?.Id ?? request.Id, asset.RelativePath, "menu-image", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), true);
            }).ToArray();
            files[manifestIndex] = new(WccPackageAuthoringManifest.RelativePath, WccPackageAuthoringManifest.Serialize(current with { OwnedFiles = current.OwnedFiles.Concat(ownedImages).ToArray() }));
        }
        return new(root, directories.Distinct(StringComparer.OrdinalIgnoreCase), files, assets, metadata);
    }

    internal static void ValidateDirectoryRoute(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        if (!current.Exists) throw new DirectoryNotFoundException("The selected parent directory does not exist.");
        for (; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null)
                throw new IOException("Linked directories cannot be used as a campaign creation route.");
    }

    internal static bool IsId(string? value) => WccCampaignCreationTokenPolicy.IsAddonIdSyntax(value);

    private static void CheckText(string? value, string label, bool multiline, List<string> issues)
    {
        if (WccCreationSourceWriter.ValidateText(value, multiline) is { } issue) issues.Add(label + ": " + issue);
    }

    private static void ValidateImage(WccCampaignImageSpec? image, string label, List<string> issues)
    {
        if (image is null) return;
        if (image.FilePath is null) { CheckText(image.Reference, label, false, issues); return; }
        try
        {
            if (string.IsNullOrWhiteSpace(image.FilePath)) throw new ArgumentException("Choose an image file.");
            var path = Path.GetFullPath(image.FilePath);
            if (!ImageExtensions.Contains(Path.GetExtension(path))) throw new ArgumentException("Choose a PNG, WebP, JPEG, BMP or GIF image.");
            WccCreationSourceWriter.RejectLinkedFile(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { issues.Add(label + ": " + error.Message); }
    }

    private static string Q(string value, bool translated = false, bool multiline = false) =>
        WccCreationSourceWriter.EncodeText(value, translated, multiline);

    private static string RenderMain(WccCampaignCreationRequest request, string campaignDefine,
        IReadOnlyList<WccCampaignDifficultySpec> difficulties, IReadOnlyList<string> defines,
        IReadOnlyList<string> difficultyImages, string icon, string image, string background, bool starter)
    {
        var builder = new StringBuilder();
        builder.Append("#textdomain ").Append(request.TextDomain).Append("\n\n[textdomain]\n    name=").Append(Q(request.TextDomain))
            .Append("\n    path=").Append(Q("data/add-ons/" + request.Id + "/translations")).Append("\n[/textdomain]\n\n")
            .Append("[binary_path]\n    path=").Append(Q("data/add-ons/" + request.Id)).Append("\n[/binary_path]\n\n# WCC chapter begin: ").Append(request.Id).Append("\n[campaign]\n    id=").Append(request.Id)
            .Append("\n    name=").Append(Q(request.Name, true)).Append("\n    define=").Append(campaignDefine)
            .Append("\n    icon=").Append(Q(icon)).Append("\n    image=").Append(Q(image)).Append("\n    background=").Append(Q(background)).Append('\n');
        if (starter) builder.Append("    first_scenario=").Append(request.Id).Append("_01_Start\n");
        if (request.Description.Length != 0)
            builder.Append("    description=").Append(Q(request.Description, true, true)).Append('\n');
        AppendDifficultyMenu(difficulties, defines, difficultyImages, builder);
        return builder.Append("[/campaign]\n\n#ifdef ").Append(campaignDefine)
            .Append("\n{~add-ons/").Append(request.Id).Append("/utils}\n\n[units]\n    {~add-ons/").Append(request.Id)
            .Append("/units}\n[/units]\n\n{~add-ons/").Append(request.Id).Append("/").Append(WccCampaignChapterAuthoringPlanner.PrimaryScenarioDirectoryFor(request.Id)).Append("}\n#endif\n# WCC chapter end: ").Append(request.Id).Append('\n').ToString();
    }

    internal static void AppendDifficultyMenu(IReadOnlyList<WccCampaignDifficultySpec> difficulties, IReadOnlyList<string> defines,
        IReadOnlyList<string> difficultyImages, StringBuilder builder)
    {
        var defaultIndex = difficulties.Any(difficulty => difficulty.IsDefault)
            ? Enumerable.Range(0, difficulties.Count).First(index => difficulties[index].IsDefault) : 0;
        for (var index = 0; index < difficulties.Count; index++)
        {
            builder.Append("    [difficulty]\n        define=").Append(defines[index]).Append("\n        image=").Append(Q(difficultyImages[index]))
                .Append("\n        label=").Append(Q(difficulties[index].Label, true)).Append('\n');
            if (difficulties[index].Description.Length != 0) builder.Append("        description=").Append(Q(difficulties[index].Description, true)).Append('\n');
            builder.Append("        default=").Append(index == defaultIndex ? "yes" : "no").Append("\n    [/difficulty]\n");
        }
    }

    private static string RenderStarter(WccCampaignCreationRequest request, IReadOnlyList<string> defines)
        => WccScenarioCreationWriter.RenderGuided(request.TextDomain, request.Id + "_01_Start", "A New Beginning",
            "~add-ons/" + request.Id + "/maps/01_Start.map", defines, 20, request.Id + "_Leader", request.Id + "_Enemy_Leader");

    private static string Readme(WccCampaignCreationRequest request, bool starter) =>
        "WCC campaign source project: " + request.Id + "\n\n" +
        (starter ? "A small starter scenario is included. Replace its map and story as your campaign develops.\n" :
            "This is an incomplete authoring scaffold, not a playable campaign yet. Add a first scenario and set first_scenario in _main.cfg before playing or strict schema validation.\n") +
        "Open _main.cfg in WCC to continue editing. Empty content directories are intentional; utils, units and scenarios are already included.\n" +
        "The add-on folder ID and difficulty defines are stable authoring identities; difficulty labels, descriptions and icons may be changed. WCC v0.1 does not add or remove difficulties after creation.\n" +
        "Keep the complete .wcc folder when sharing or backing up this project. It holds portable authoring identities, ownership and dependency/chapter recipes; Wesnoth uses the ordinary WML files.\n" +
        "A folder outside Wesnoth's add-ons directory is editable in WCC but must be copied or deployed there before playing.\n";
}
