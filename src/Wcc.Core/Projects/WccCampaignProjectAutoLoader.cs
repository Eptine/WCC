using Wcc.Core.Campaigns;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

/// <summary>
/// Discovers campaign-owned documents, retaining conventional macro/raw inventory
/// fallbacks. Discovery is not active-preprocessor coverage or write authority.
/// </summary>
public static class WccCampaignProjectAutoLoader
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void PopulateFromCampaignMainIncludes(WccProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Kind != WccProjectKind.Campaign || string.IsNullOrWhiteSpace(project.SourcePath)) return;
        var mainText = project.CampaignRawTextSnapshot ?? project.Campaign?.OriginalText;
        if (mainText is null) return;
        var root = Path.GetDirectoryName(Path.GetFullPath(project.SourcePath));
        if (root is null) return;
        var discovery = new IncludeDiscovery(project, root);
        var references = CampaignMainIncludeScanner.ScanFromMainCfgText(mainText, project.SourcePath);
        var scenarioDirectories = new HashSet<string>(PathComparer);
        var unitDirectories = new HashSet<string>(PathComparer);
        foreach (var reference in references)
        {
            if (reference.ResolvedDirectoryPath is not { } path) continue;
            discovery.Visit(reference, 0);
            if (reference.Kind == CampaignIncludeKind.CfgInclude || reference.IsUnitContainerInclude || File.Exists(path))
            {
                continue;
            }
            if (!Directory.Exists(path)) continue;
            if (reference.Kind == CampaignIncludeKind.ScenariosDirectory && scenarioDirectories.Add(Path.GetFullPath(path)))
                AttachConventionalDirectory(project, path, true);
            else if (reference.Kind == CampaignIncludeKind.UnitsDirectory && unitDirectories.Add(Path.GetFullPath(path)))
                AttachConventionalDirectory(project, path, false);
        }
        // This broader inventory is retained for imported preprocessor wrappers such
        // as UTBS_INCLUDE. It never proves native include execution or active content.
        var scenarios = Path.Combine(root, "scenarios");
        if (Directory.Exists(scenarios) && scenarioDirectories.Add(scenarios)) AttachConventionalDirectory(project, scenarios, true);
        var units = Path.Combine(root, "units");
        if (Directory.Exists(units) && unitDirectories.Add(units)) AttachConventionalDirectory(project, units, false);
    }

    private static void AttachConventionalDirectory(WccProject project, string directory, bool scenarios)
    {
        foreach (var file in EnumerateConventionalCfgFiles(directory, 0).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (scenarios && HasScenarioFile(project, file) || !scenarios && HasUnitFile(project, file)) continue;
            if (!CanReadOrdinaryPath(file, false)) continue;
            var text = File.ReadAllText(file);
            var macros = WmlMacroDefinitionScanner.Scan(text);
            if (scenarios)
            {
                var documents = ScenarioImporter.ImportAllFromText(text);
                if (documents.Count == 0 && !WccScenarioSourceCandidate.MayDeclareScenario(text)) continue;
                var asset = new ScenarioFileAsset { FileName = Path.GetFileName(file), SourcePath = file, RawTextSnapshot = text };
                asset.Documents.AddRange(documents); asset.MacroDefinitions.AddRange(macros);
                project.ProjectScenarioFiles.Add(asset);
            }
            else
            {
                var documents = UnitTypeImporter.ImportAllFromText(text);
                if (documents.Count == 0 && macros.Count == 0) continue;
                var asset = new UnitFileAsset { FileName = Path.GetFileName(file), SourcePath = file, RawTextSnapshot = text };
                asset.Documents.AddRange(documents); asset.MacroDefinitions.AddRange(macros);
                project.ProjectUnitFiles.Add(asset);
            }
        }
    }

    private static IEnumerable<string> EnumerateConventionalCfgFiles(string directory, int depth)
    {
        if (depth > 64) throw new ArgumentException("Campaign content discovery exceeds its supported directory nesting bound.");
        if (!CanReadOrdinaryPath(directory, true)) yield break;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var file in EnumerateConventionalCfgFiles(entry, depth + 1)) yield return file;
            }
            else if (entry.EndsWith(".cfg", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                yield return entry;
        }
    }

    private static bool CanReadOrdinaryPath(string path, bool directory)
    {
        try
        {
            if (directory) WccCampaignCreationPlanner.ValidateDirectoryRoute(path);
            else WccCreationSourceWriter.RejectLinkedFile(path);
            return true;
        }
        catch (IOException) { return false; }
    }

    private static bool HasScenarioFile(WccProject project, string file) => project.ProjectScenarioFiles.Any(asset =>
        asset.SourcePath is not null && PathComparer.Equals(Path.GetFullPath(asset.SourcePath), Path.GetFullPath(file)));
    private static bool HasUnitFile(WccProject project, string file) => project.ProjectUnitFiles.Any(asset =>
        asset.SourcePath is not null && PathComparer.Equals(Path.GetFullPath(asset.SourcePath), Path.GetFullPath(file)));

    private sealed class IncludeDiscovery(WccProject project, string root)
    {
        private const int MaxSources = 16384;
        private const int MaxDirectoryEntries = 65536;
        private const int MaxDepth = 64;
        private const int MaxCharacters = 64 * 1024 * 1024;
        private readonly Dictionary<string, WmlStaticSource> _sources = new(PathComparer);
        private readonly Dictionary<string, WmlStaticSourceSet> _directories = new(PathComparer);
        private readonly HashSet<(string Path, bool Units)> _visited = new(new VisitComparer());
        private int _entries;
        private int _characters;

        public void Visit(CampaignIncludeReference reference, int depth)
        {
            if (depth > MaxDepth) throw new ArgumentException("Campaign include discovery exceeds its supported nesting bound.");
            if (reference.ResolvedDirectoryPath is not { } path || !_visited.Add((path, reference.IsUnitContainerInclude))) return;
            if (!Directory.Exists(path) && !File.Exists(path)) return;
            if (!CanReadOrdinaryPath(path, Directory.Exists(path))) return;
            if (File.Exists(path))
            {
                if (path.EndsWith(".cfg", StringComparison.Ordinal)) VisitSource(ReadSource(path), reference.IsUnitContainerInclude, depth);
                return;
            }
            if (!_directories.TryGetValue(path, out var inventory))
            {
                var sources = new List<WmlStaticSource>();
                var directories = new List<string>();
                CaptureDirectory(path, sources, directories, 0);
                inventory = new WmlStaticSourceSet(sources, dataRoot: path, isInventoryComplete: true, directoryPaths: directories);
                _directories.Add(path, inventory);
            }
            // The shared resolver chooses only _main.cfg, or ordered immediate cfg
            // files and child package mains. Ordinary subfolders are not implicit includes.
            var caller = new WmlStaticSource(Path.Combine(Path.GetDirectoryName(path)!, "__wcc_discovery__.cfg"), "");
            var resolved = inventory.Resolve("./" + Path.GetFileName(path), caller);
            if (resolved.Exists != WmlStaticTruth.True) return;
            foreach (var source in resolved.Sources) VisitSource(source, reference.IsUnitContainerInclude, depth);
        }

        private void CaptureDirectory(string directory, List<WmlStaticSource> sources, List<string> directories, int depth)
        {
            if (depth > MaxDepth) throw new ArgumentException("Campaign source inventory exceeds its supported directory nesting bound.");
            if (!CanReadOrdinaryPath(directory, true)) return;
            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++_entries > MaxDirectoryEntries) throw new ArgumentException("Campaign source inventory exceeds its supported entry bound.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) CaptureDirectory(entry, sources, directories, depth + 1);
                else if (entry.EndsWith(".cfg", StringComparison.Ordinal) && CanReadOrdinaryPath(entry, false)) sources.Add(ReadSource(entry));
            }
        }

        private WmlStaticSource ReadSource(string path)
        {
            WccCreationSourceWriter.RejectLinkedFile(path);
            if (_sources.TryGetValue(path, out var source)) return source;
            if (_sources.Count >= MaxSources || new FileInfo(path).Length > (long)(MaxCharacters - _characters) * 4)
                throw new ArgumentException("Campaign source inventory exceeds its supported source-size bound.");
            var text = File.ReadAllText(path);
            if (text.Length > MaxCharacters - _characters) throw new ArgumentException("Campaign source inventory exceeds its supported source-size bound.");
            _characters += text.Length;
            source = new(path, text);
            _sources.Add(path, source);
            return source;
        }

        private void VisitSource(WmlStaticSource source, bool units, int depth)
        {
            // A package main or an explicitly included source may itself include
            // another local file/directory. Follow those real edges, not all captured descendants.
            AttachLiteralDocuments(source, units);
            foreach (var reference in CampaignMainIncludeScanner.ScanFromCfgText(source.Text, source.Path, root, units))
                Visit(reference, depth + 1);
        }

        private void AttachLiteralDocuments(WmlStaticSource source, bool units)
        {
            var structure = WmlAuthoredStructureScanner.Scan(source.Text);
            var structuralText = structure.MaskedText.Replace("[+units]", "[ units]", StringComparison.Ordinal);
            var spans = WmlBlockScanner.FindTopLevelBlockSpans(structuralText);
            var scenarioStarts = spans.Where(span => span.TagName == "scenario").Select(span => span.StartIndex).ToHashSet();
            var unitStarts = spans.Where(span => span.TagName == "unit_type").Select(span => span.StartIndex).ToHashSet();
            var wrappedUnits = new List<UnitTypeDocument>();
            foreach (var container in spans.Where(span => span.TagName == "units"))
            {
                var bodyStart = structuralText.IndexOf(']', container.StartIndex) + 1;
                var bodyEnd = structuralText.LastIndexOf('[', container.EndIndexExclusive - 1, container.EndIndexExclusive - bodyStart);
                foreach (var span in WmlBlockScanner.FindTopLevelBlockSpans(structuralText[bodyStart..bodyEnd]).Where(span => span.TagName == "unit_type"))
                {
                    var start = bodyStart + span.StartIndex;
                    var end = bodyStart + span.EndIndexExclusive;
                    var parsed = UnitTypeImporter.ImportAllFromText(source.Text[start..end]).FirstOrDefault();
                    if (parsed is null) continue;
                    var document = new UnitTypeDocument
                    {
                        InternalId = parsed.InternalId, OriginalText = source.Text,
                        BlockSpan = new("unit_type", source.Text[start..end], start, end),
                        EditableBlock = parsed.EditableBlock, Unit = parsed.Unit
                    };
                    document.AttackDocuments.AddRange(parsed.AttackDocuments);
                    document.AnimationDocuments.AddRange(parsed.AnimationDocuments);
                    document.GenderVariantDocuments.AddRange(parsed.GenderVariantDocuments);
                    document.AssetReferences.AddRange(parsed.AssetReferences);
                    wrappedUnits.Add(document);
                }
            }
            var definitionLines = structure.Directives.Where(directive => directive.Name == "define" && directive.DefinitionDepth == 0)
                .Select(directive => directive.Line).ToHashSet();
            var macros = WmlMacroDefinitionScanner.Scan(source.Text).Where(macro => definitionLines.Contains(macro.StartLineIndex)).ToArray();
            if (!units && scenarioStarts.Count != 0 && !HasScenarioFile(project, source.Path))
            {
                var asset = new ScenarioFileAsset { FileName = Path.GetFileName(source.Path), SourcePath = source.Path, RawTextSnapshot = source.Text };
                asset.Documents.AddRange(ScenarioImporter.ImportAllFromText(source.Text).Where(doc => scenarioStarts.Contains(doc.BlockSpan.StartIndex)));
                asset.MacroDefinitions.AddRange(macros);
                if (asset.Documents.Count != 0) project.ProjectScenarioFiles.Add(asset);
            }
            if ((unitStarts.Count != 0 || wrappedUnits.Count != 0 || units && macros.Length != 0) && !HasUnitFile(project, source.Path))
            {
                var asset = new UnitFileAsset { FileName = Path.GetFileName(source.Path), SourcePath = source.Path, RawTextSnapshot = source.Text };
                asset.Documents.AddRange(UnitTypeImporter.ImportAllFromText(source.Text).Where(doc => unitStarts.Contains(doc.BlockSpan.StartIndex)));
                asset.Documents.AddRange(wrappedUnits);
                asset.MacroDefinitions.AddRange(macros);
                if (asset.Documents.Count != 0 || units && macros.Length != 0) project.ProjectUnitFiles.Add(asset);
            }
        }

        private sealed class VisitComparer : IEqualityComparer<(string Path, bool Units)>
        {
            public bool Equals((string Path, bool Units) left, (string Path, bool Units) right) => left.Units == right.Units && PathComparer.Equals(left.Path, right.Path);
            public int GetHashCode((string Path, bool Units) value) => HashCode.Combine(PathComparer.GetHashCode(value.Path), value.Units);
        }
    }
}
