using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public static partial class WccCampaignDependencyAuthoringPlanner
{
    /// <summary>Bounded read-only hints. Candidate entry points remain suggestions, not proof of activation.</summary>
    public static WccCampaignDependencyRecommendations ReadRecommendations(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(root) || Path.GetDirectoryName(root) is null)
            throw new ArgumentException("Select the dependency's add-on folder.", nameof(folder));
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null)
                throw new IOException("Choose an ordinary source folder without symbolic links.");
        var addon = Path.GetFileName(root);
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(addon) is { } issue) throw new ArgumentException(issue, nameof(folder));
        var suggestions = new List<WccCampaignDependencyRecommendation>();
        var notes = new List<string>
        {
            "These are source hints. Review the add-on's instructions and generated WML before applying.",
            "Source availability, runtime loading and the add-on download declaration are separate. The source folder is attached only for this campaign session."
        };
        var rootFiles = Directory.EnumerateFiles(root).Take(257).ToArray();
        if (rootFiles.Length > 256) notes.Add("Only the first 256 top-level files were inspected.");
        foreach (var file in rootFiles.Take(256).OrderBy(RecommendationFileRank).ThenBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase) || name.StartsWith("INSTALL", StringComparison.OrdinalIgnoreCase))
            {
                var readme = ReadSmallOrdinaryFile(file);
                if (readme is not null)
                {
                    var lines = Normalize(readme).Split('\n').Where(line => line.Contains("campaign", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("load_resource", StringComparison.Ordinal) || line.Contains("include", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("~add-ons/", StringComparison.Ordinal)).Take(12).Select(line => line.Trim()).Where(line => line.Length != 0);
                    notes.AddRange(lines.Select(line => name + ": " + (line.Length > 400 ? line[..400] + "…" : line)));
                }
            }
            if (!name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)) continue;
            var text = ReadSmallOrdinaryFile(file);
            if (text is null) continue;
            var scan = WmlAuthoredStructureScanner.Scan(text);
            var tags = WmlEditorAnalysis.Build(scan.MaskedText).TagOccurrences;
            var hasCampaign = tags.Any(tag => !tag.IsClosing && tag.Name == "campaign");
            var hasContent = tags.Any(tag => !tag.IsClosing && tag.Name is "units" or "resource" or "lua" or "binary_path") || scan.Macros.Count != 0;
            var looksLikeEntry = name.StartsWith("campaign", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("include", StringComparison.OrdinalIgnoreCase) || name.Contains("loader", StringComparison.OrdinalIgnoreCase);
            if (!hasCampaign && hasContent && (looksLikeEntry || name == "_main.cfg"))
            {
                var resourceIds = ReadLiteralResourceIds(text);
                var resourceCount = WmlProjectionTreeParser.Parse(scan.MaskedText).Roots.Count(node => node.TagName == "resource");
                if (resourceCount != 0 && resourceIds.Count != resourceCount)
                {
                    notes.Add(name + ": Resource registration has a conditional, repeated or unresolved ID. Configure its registration and activation with custom WML; no complete loader recipe was inferred.");
                    continue;
                }
                var steps = new List<WccCampaignDependencyStep>
                {
                    new(resourceIds.Count == 0 ? WccCampaignDependencyHook.Gameplay : WccCampaignDependencyHook.Menu,
                        WccCampaignDependencyStepKind.Include, name)
                };
                steps.AddRange(resourceIds.Select(id => new WccCampaignDependencyStep(WccCampaignDependencyHook.Campaign,
                    WccCampaignDependencyStepKind.LoadResource, id)));
                suggestions.Add(new("Include " + name,
                    "This file contains loading code without a literal campaign definition. Confirm its intended singleplayer use in the add-on's documentation.",
                    Array.AsReadOnly(steps.ToArray())));
            }
        }
        var assets = new[] { "images", "music", "sounds" }.Where(name => OrdinaryDirectory(Path.Combine(root, name))).ToArray();
        if (assets.Length != 0)
            suggestions.Add(new("Link images / music / sounds",
                "Adds the dependency's asset search root (found: " + string.Join(", ", assets) + "). This does not load unit definitions or helper code.",
                Array.AsReadOnly(new[] { new WccCampaignDependencyStep(WccCampaignDependencyHook.Gameplay, WccCampaignDependencyStepKind.AssetPath, ".") })));
        var inspected = 0;
        foreach (var directoryName in new[] { "utils", "resources" })
        {
            var directory = Path.Combine(root, directoryName);
            if (!OrdinaryDirectory(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.cfg").OrderBy(Path.GetFileName, StringComparer.Ordinal).Take(128))
            {
                if (++inspected > 128) break;
                var text = ReadSmallOrdinaryFile(file);
                if (text is null) continue;
                foreach (var id in ReadLiteralResourceIds(text))
                {
                    suggestions.Add(new("Register and load resource " + id,
                        "Found a literal [resource] in " + directoryName + "/" + Path.GetFileName(file) + ". The directory is registered before campaign menus, then loaded by the selected campaign. Check supporting includes in the documentation.",
                        Array.AsReadOnly(new[]
                        {
                            new WccCampaignDependencyStep(WccCampaignDependencyHook.Menu, WccCampaignDependencyStepKind.Include, directoryName),
                            new WccCampaignDependencyStep(WccCampaignDependencyHook.Campaign, WccCampaignDependencyStepKind.LoadResource, id)
                        })));
                }
            }
        }
        if (suggestions.Count == 0) notes.Add("No supplied entry point or asset root was recognized. Configure loading steps or use custom WML.");
        return new(addon, Array.AsReadOnly(suggestions.Take(64).ToArray()), Array.AsReadOnly(notes.Take(64).ToArray()));
    }

    // Explicit reusable entry-point filenames are stronger hints than a general add-on main.
    // Ranking is only presentation: documentation and the common evaluator still decide usability.
    private static int RecommendationFileRank(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("campaign.cfg", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith("campaign", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("include", StringComparison.OrdinalIgnoreCase) || name.Contains("loader", StringComparison.OrdinalIgnoreCase)) return 1;
        return name.Equals("_main.cfg", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }
    private static IReadOnlyList<string> ReadLiteralResourceIds(string text)
    {
        var scan = WmlAuthoredStructureScanner.Scan(text);
        var result = new List<string>();
        if (scan.Problem is not null || !WmlSingleBlockValidator.ValidateSourceStructure(text).IsValid) return result;
        foreach (var resource in WmlProjectionTreeParser.Parse(scan.MaskedText).Roots.Where(node => node.TagName == "resource").Take(16))
        {
            var start = scan.Lines[resource.StartLine].StartOffset;
            var end = scan.Lines[Math.Min(resource.EndLineExclusive - 1, scan.Lines.Count - 1)].EndOffsetExclusive;
            var parsed = WmlEditableBlockParser.Parse(new WmlBlock("resource", text[start..end]), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" });
            if (!resource.PreprocessorConditional && !scan.ConditionalLines[resource.StartLine] &&
                !scan.Directives.Any(directive => directive.DefinitionDepth == 0 && directive.Line >= resource.StartLine &&
                    directive.Line < resource.EndLineExclusive && WmlAuthoredStructureScanner.IsConditionalStart(directive.Name)) &&
                parsed.Known.TryGetValue("id", out var field) && WccWmlPlainTextCodec.TryReadLiteral(field.Value, out var id, out var translated) &&
                !translated && LiteralResourceId(id) && !result.Contains(id, StringComparer.Ordinal)) result.Add(id);
        }
        return result;
    }
    private static bool OrdinaryDirectory(string path) => Directory.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && new DirectoryInfo(path).LinkTarget is null;
    private static string? ReadSmallOrdinaryFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 512 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null) return null;
        return File.ReadAllText(path);
    }
}