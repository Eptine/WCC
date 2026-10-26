using System.Text;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Library;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record WccCampaignPresetStorageLocation(WccCampaignPresetScope Scope, string RelativePath,
    WmlStaticSource? Source, WmlStaticSourceRange? IncludeRange);

public static partial class WccCampaignLocalPresetStoragePlanner
{
    public static WccCampaignLocalPresetStorageSnapshot Read(WccStaticSourceCaptureResult capture,
        WccCampaignAuthoringMetadata? authoring, WmlCampaignEvaluationPlan? selectedPlan,
        IReadOnlyList<WmlCampaignEvaluationPlan> packagePlans, WmlStaticSource? packageRoot = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var root = packageRoot ?? capture.EntrySource ?? throw new InvalidOperationException("No exact package source was captured.");
        if (capture.EntrySource is not { } entry || !PathEquals(root.Path, entry.Path)
            || root.ContentHash != entry.ContentHash || root.Text != entry.Text || !root.IsAuthored || !entry.IsAuthored)
            throw new InvalidOperationException("The selected source is not the exact captured package root.");
        var result = Read(root, authoring, selectedPlan, capture.Sources, packagePlans);
        result.CapturedContext = capture;
        return result;
    }

    public static WccCampaignLocalPresetStorageSnapshot Read(WmlStaticSource packageRoot,
        WccCampaignAuthoringMetadata? authoring, WmlCampaignEvaluationPlan? selectedPlan,
        IReadOnlyList<WmlStaticSource> capturedSources, IReadOnlyList<WmlCampaignEvaluationPlan> packagePlans)
    {
        ArgumentNullException.ThrowIfNull(packageRoot); ArgumentNullException.ThrowIfNull(capturedSources);
        ArgumentNullException.ThrowIfNull(packagePlans);
        var selectedAuthoring = selectedPlan is null ? authoring : WccCampaignChapterAuthoringPlanner.ResolveSelectedAuthoring(
            authoring, packageRoot, selectedPlan.CampaignDefine, capturedSources) ?? authoring;
        var canonical = WccCampaignLocalPresetCreationPlanner.Read(packageRoot, selectedAuthoring, selectedPlan);
        WccPackageAuthoringManifestSnapshot? manifest = null;
        var locations = new List<WccCampaignPresetStorageLocation>();
        var entries = new List<WccCampaignLocalPresetEntry>();
        var moved = WccCampaignLocalPresetCatalog.ReadDefinitions(packageRoot, selectedPlan);
        var scopes = new List<WccCampaignPresetScope> { WccCampaignPresetScope.Package };
        WccCampaignChapterAuthoringSnapshot? chapters = null;
        try
        {
            if (!canonical.CanCreate) throw new InvalidOperationException(canonical.Diagnostic);
            if (capturedSources.Count > 200_000 || capturedSources.Any(source => source is null))
                throw new InvalidOperationException("The preset inventory is incomplete or exceeds its bounded scope.");
            if (selectedPlan is null || packagePlans.Count is < 1 or > 32 || packagePlans.Any(plan =>
                    plan.CampaignMainSourcePath != packageRoot.Path || plan.CampaignMainTextHash != packageRoot.ContentHash)
                || packagePlans.Select(plan => plan.CampaignDefine).Distinct(StringComparer.Ordinal).Count() != packagePlans.Count
                || packagePlans.Select(plan => plan.CampaignId).Distinct(StringComparer.Ordinal).Count() != packagePlans.Count
                || packagePlans.Count(plan => plan.CampaignId == selectedPlan.CampaignId && plan.CampaignDefine == selectedPlan.CampaignDefine) != 1)
                throw new InvalidOperationException("Preset storage needs the exact package campaign inventory and selected plan.");
            var root = Path.GetDirectoryName(packageRoot.Path)!;
            var readManifest = WccPackageAuthoringManifest.Read(packageRoot, authoring!.AddonId, capturedSources);
            if (readManifest.Source is not null || authoring.LayoutVersion == 2) manifest = readManifest;
            var content = WccPackageContentAuthoringManifest.Read(readManifest.Document);
            if (!content.CanMutate) throw new InvalidOperationException(content.Issue);
            if (authoring.LayoutVersion == 2)
            {
                chapters = WccCampaignChapterAuthoringPlanner.Read(packageRoot, authoring, capturedSources);
                if (!chapters.CanEdit) throw new InvalidOperationException(chapters.Diagnostic);
                foreach (var chapter in chapters.Chapters)
                {
                    var matches = packagePlans.Where(plan => plan.CampaignId == chapter.CampaignId && plan.CampaignDefine == chapter.CampaignDefine).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("A chapter scope has no exact campaign evaluation plan.");
                    scopes.Add(new(WccCampaignPresetScopeKind.Campaign, chapter.CampaignId, chapter.CampaignDefine, chapter.Id));
                }
            }
            if (packagePlans.Count > 1 && chapters is null)
                throw new InvalidOperationException("Multiple imported campaign gates remain RAW until their activation layout is proved.");
            // The explicit file include establishes presets before any utility
            // consumers. Its exact outer guard suppresses repeated definitions
            // when the ordinary utils directory subsequently visits presets.cfg.
            var structures = packagePlans.ToDictionary(plan => plan.CampaignDefine!, plan =>
            {
                var metadata = WccCampaignChapterAuthoringPlanner.ResolveSelectedAuthoring(authoring, packageRoot, plan.CampaignDefine, capturedSources) ?? authoring;
                var structure = WccCampaignStructurePlanner.Read(packageRoot, metadata);
                if (!structure.CanEdit) throw new InvalidOperationException(structure.Diagnostic);
                var executable = structure.Layout!.Paths.Where(row => row.Row.Kind != WccCampaignPathKind.BinaryRoot)
                    .OrderBy(row => row.Range.StartOffset).ToArray();
                var preset = executable.Where(row => row.Row.Kind == WccCampaignPathKind.CfgInclude && row.Row.Path == SharedStorageRelativePath).ToArray();
                var utility = executable.Where(row => row.Row.Kind == WccCampaignPathKind.CfgInclude && row.Row.Path == "utils").ToArray();
                if (preset.Length > 1 || utility.Length != 1 || executable.Length <= preset.Length
                    || preset.Length == 1 && executable[0] != preset[0] || executable[preset.Length] != utility[0])
                    throw new InvalidOperationException("Preset storage requires the shared utils directory as the first executable include, preceded only by its exact preset file.");
                return structure;
            }, StringComparer.Ordinal);
            var sharedIncludes = structures.Values.Select(structure => structure.Layout!.Paths.SingleOrDefault(row =>
                row.Row.Kind == WccCampaignPathKind.CfgInclude && row.Row.Path == SharedStorageRelativePath)).ToArray();
            if (sharedIncludes.Any(row => row is null) && sharedIncludes.Any(row => row is not null))
                throw new InvalidOperationException("Package-wide presets must be included in every campaign gate.");
            var packageDefinitions = WccCampaignLocalPresetCatalog.ReadPackageDefinitions(capturedSources, selectedPlan,
                content.Presets, root, packagePlans, scopes);
            var unitPresets = packageDefinitions.Where(entry => entry.Role == WccCampaignLocalPresetRole.Unit && entry.IsSupported).ToArray();
            moved = packageDefinitions.Where(entry => PathEquals(entry.SourcePath, packageRoot.Path)).ToArray();
            AddLocation(WccCampaignPresetScope.Package, SharedStorageRelativePath,
                structures[selectedPlan.CampaignDefine!].Layout!.Paths.SingleOrDefault(row => row.Row.Kind == WccCampaignPathKind.CfgInclude
                    && row.Row.Path == SharedStorageRelativePath)?.Range, selectedPlan);
            foreach (var scope in scopes.Where(scope => !scope.IsPackage))
            {
                var structure = structures[scope.CampaignDefine!];
                var path = "chapters/" + scope.ManagedChapterId + "/utils";
                var relative = ChapterStorageRelativePath(scope.ManagedChapterId!);
                var rows = structure.Layout!.Paths.Where(row => row.Row.Kind == WccCampaignPathKind.CfgInclude && row.Row.Path == path).ToArray();
                var presets = structure.Layout.Paths.Where(row => row.Row.Kind == WccCampaignPathKind.CfgInclude && row.Row.Path == relative).ToArray();
                if (rows.Length > 1 || presets.Length > 1) throw new InvalidOperationException("A chapter utility or preset file is included more than once.");
                var sharedRow = structure.Layout.Paths.Single(row => row.Row.Kind == WccCampaignPathKind.CfgInclude && row.Row.Path == "utils");
                var later = structure.Layout.Paths.Where(row => row.Row.Kind != WccCampaignPathKind.BinaryRoot
                        && row.Row.Path != "utils" && row.Row.Path != SharedStorageRelativePath && row.Row.Path != path && row.Row.Path != relative)
                    .Select(row => row.Range.StartOffset).DefaultIfEmpty(structure.Layout.GeneralInsertion).Min();
                if (rows.Length == 1 && (rows[0].Range.StartOffset < sharedRow.Range.StartOffset || rows[0].Range.StartOffset >= later)
                    || presets.Length == 1 && (rows.Length != 1 || presets[0].Range.StartOffset < sharedRow.Range.StartOffset
                        || presets[0].Range.StartOffset >= rows[0].Range.StartOffset))
                    throw new InvalidOperationException("Chapter presets must load after shared utils and before their own utils, units and scenarios.");
                AddLocation(scope, relative, presets.SingleOrDefault()?.Range, packagePlans.Single(scope.Targets));
            }
            if (packagePlans.Count > 1 && moved.Count != 0)
                throw new InvalidOperationException("Legacy inline presets in a multi-campaign root need explicit RAW relocation; their scope cannot be widened implicitly.");
            if (content.Presets.Where(receipt => WccCampaignLocalPresetCatalog.TryRole(receipt.Kind, out _)).Any(receipt => !entries.Any(entry => entry.Id == receipt.Id)))
                throw new InvalidOperationException("A known preset receipt lies outside its proved storage scope or has no exact captured definition.");
            if (moved.Any(entry => !entry.IsSupported) || moved.Count + entries.Count > 256)
                throw new InvalidOperationException("Managed presets need complete supported bodies within the bounded catalog.");
            WccCampaignPresetScopePolicy.Validate(moved.Concat(entries).Select(entry => (entry.Id, entry.Name, entry.Scope)), scopes);
            WccCampaignPresetScopePolicy.ValidateReservedSymbols(moved.Concat(entries).Select(entry => entry.Name), packagePlans);
            if (content.Presets.Any(receipt => !WccCampaignLocalPresetCatalog.TryRole(receipt.Kind, out _)))
                WccCampaignPresetScopePolicy.Validate(moved.Concat(entries).Select(entry => (entry.Id, entry.Name, entry.Scope))
                    .Concat(content.Presets.Where(receipt => !WccCampaignLocalPresetCatalog.TryRole(receipt.Kind, out _)).Select(receipt => (receipt.Id, receipt.Symbol, receipt.Scope))), scopes);
            var result = Result(true, "Preset edits use the captured package root, guarded scoped utilities and one checked Save.");
            ValidateStorageGuardNamespace(result, capturedSources);
            return result;

            void AddLocation(WccCampaignPresetScope scope, string relative, WmlStaticSourceRange? include, WmlCampaignEvaluationPlan plan)
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                var matches = capturedSources.Where(source => PathEquals(source.Path, path)).ToArray();
                if (matches.Length > 1 || matches.Length == 1 && (!matches[0].IsAuthored || matches[0].Path != path))
                    throw new InvalidOperationException("A preset utility has conflicting or noncanonical captured revisions.");
                var folder = Path.GetDirectoryName(path)!;
                if (capturedSources.Any(source => PathEquals(source.Path, Path.Combine(folder, "_main.cfg"))))
                    throw new InvalidOperationException("A utility _main.cfg changes directory loading; use RAW for this layout.");
                var source = matches.SingleOrDefault();
                if ((source is null) != (include is null))
                    throw new InvalidOperationException("A preset file needs its exact explicit include and captured source together.");
                var location = new WccCampaignPresetStorageLocation(scope, relative, source, include);
                locations.Add(location);
                if (source is not null)
                {
                    var definitions = ReadScopedStorage(source, plan, content.Presets, root, packagePlans, unitPresets);
                    if (definitions.Any(entry => entry.Scope != scope)) throw new InvalidOperationException("A preset receipt scope does not match its physical utility.");
                    if (content.Presets.Where(receipt => receipt.SourcePath == relative && WccCampaignLocalPresetCatalog.TryRole(receipt.Kind, out _))
                        .Any(receipt => !definitions.Any(entry => entry.Id == receipt.Id)))
                        throw new InvalidOperationException("A preset content receipt has no exact authored definition.");
                    entries.AddRange(definitions);
                }
                else if (content.Presets.Any(receipt => receipt.SourcePath == relative))
                    throw new InvalidOperationException("A preset content receipt points to a missing captured utility.");
            }
        }
        catch (Exception issue) when (issue is ArgumentException or InvalidOperationException)
        { return Result(false, issue.Message); }

        WccCampaignLocalPresetStorageSnapshot Result(bool canStore, string diagnostic) => new(canonical,
            locations.FirstOrDefault(location => location.Scope.IsPackage)?.Source, moved, entries, canStore, diagnostic)
        {
            Locations = Array.AsReadOnly(locations.ToArray()), AvailableScopes = Array.AsReadOnly(scopes.ToArray()),
            SelectedScope = scopes.SingleOrDefault(scope => !scope.IsPackage && scope.Targets(selectedPlan)) ?? WccCampaignPresetScope.Package,
            PackagePlans = Array.AsReadOnly(packagePlans.ToArray()), PackageAuthoring = authoring, Manifest = manifest, Chapters = chapters
        };
    }

    private static void ValidateScopedCreation(WccCampaignLocalPresetStorageSnapshot snapshot,
        IReadOnlyList<WccCampaignLocalPresetCreationSpec> rows)
    {
        if (rows.Count == 0 && snapshot.MainEntries.Count == 0 || rows.Count + snapshot.Entries.Count > 256
            || rows.Any(row => row is null || row.Payload is null || row.Scope is null))
            throw new InvalidOperationException("Create a bounded set of unused preset definitions.");
        var reserved = snapshot.Manifest is { } manifest ? WccPackageContentAuthoringManifest.Read(manifest.Document).Presets
            .Where(receipt => !WccCampaignLocalPresetCatalog.TryRole(receipt.Kind, out _)).Select(receipt => (receipt.Id, receipt.Symbol, receipt.Scope)) : [];
        WccCampaignPresetScopePolicy.Validate(snapshot.Entries.Select(entry => (entry.Id, entry.Name, entry.Scope))
            .Concat(rows.Select(row => (row.Id, row.Name, row.Scope))).Concat(reserved), snapshot.AvailableScopes);
        WccCampaignPresetScopePolicy.ValidateReservedSymbols(rows.Select(row => row.Name), snapshot.PackagePlans);
        var guards = snapshot.Locations.Select(location => StorageGuardSymbol(snapshot.PackageAuthoring!.AddonId, location.Scope)).ToHashSet(StringComparer.Ordinal);
        if (rows.Any(row => guards.Contains(row.Name)))
            throw new InvalidOperationException("A preset symbol conflicts with private managed storage include state.");
        foreach (var row in rows)
        {
            if (!row.Scope.IsPackage && row.Scope != snapshot.SelectedScope)
                throw new InvalidOperationException("Create chapter presets in the selected campaign context.");
            if (row.Scope.IsPackage && snapshot.PackagePlans.Count > 1 && HasDifficultyValues(row.Payload))
                throw new InvalidOperationException("Package-wide presets currently use shared values. Select the current chapter for difficulty-specific values.");
            ValidateEventUnitReferences(snapshot, row.Payload, row.Scope);
            _ = WccCampaignLocalPresetCatalog.RenderDefinition(row.Id, row.Name, row.Payload, snapshot.Main.Plan);
        }
        if (rows.Sum(row => (long)WccCampaignLocalPresetCatalog.RenderDefinition(row.Id, row.Name, row.Payload, snapshot.Main.Plan).Length)
            + snapshot.MainEntries.Sum(entry => (long)entry.DefinitionRange.Length) > 2 * 1024 * 1024)
            throw new InvalidOperationException("The collected preset payload exceeds its bounded size.");
    }

    internal static void ValidateEventUnitReferences(WccCampaignLocalPresetStorageSnapshot snapshot,
        WccCampaignLocalPresetPayload payload, WccCampaignPresetScope scope)
    {
        foreach (var unit in payload.EventUnitPresets)
        {
            var matches = snapshot.Entries.Where(entry => entry.Id == unit.Id && entry.Name == unit.Name && entry.Role == unit.Role &&
                entry.Scope == unit.Scope && entry.IsSupported && entry.Role == WccCampaignLocalPresetRole.Unit &&
                entry.Definition.TextDomain == unit.Definition.TextDomain && SameRange(entry.DefinitionRange, unit.DefinitionRange) &&
                SameRange(entry.BodyRange, unit.BodyRange)).ToArray();
            if (!unit.IsSupported || matches.Length != 1 || !(unit.Scope.IsPackage || !scope.IsPackage && unit.Scope == scope))
                throw new InvalidOperationException("An Event must reference one exact captured saved Unit preset in its own scope or the package scope.");
        }
        static bool SameRange(WmlStaticSourceRange left, WmlStaticSourceRange right) => PathEquals(left.Source.Path, right.Source.Path) &&
            left.Source.ContentHash == right.Source.ContentHash && left.Source.Text == right.Source.Text &&
            left.StartOffset == right.StartOffset && left.Length == right.Length && left.Text == right.Text;
    }
    private static bool HasDifficultyValues(WccCampaignLocalPresetPayload payload) => payload.DifficultyValues.Count != 0
        || payload.Leaders.Any(leader => leader.DifficultyValues.Count != 0)
        || payload.EventSeed?.Graph.DifficultyRegions.Count > 0;
    private static bool PathEquals(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record ContentPlan(IReadOnlyList<WccScenarioFieldBatchCandidate> Replacements, IReadOnlyList<WmlStaticSource> Creates);
    private static ContentPlan PlanContent(WccCampaignLocalPresetStorageEdit edit)
    {
        var snapshot = edit.Snapshot; var root = Path.GetDirectoryName(edit.Source.Path)!;
        var replacements = new List<WccScenarioFieldBatchCandidate>(); var creates = new List<WmlStaticSource>();
        var outputs = new List<(WccCampaignPresetStorageLocation Location, WmlStaticSource Source)>();
        foreach (var location in snapshot.Locations)
        {
            var additions = edit.Presets.Where(row => row.Scope == location.Scope).ToArray();
            var moving = edit.Mutations.Count == 0 && location.Scope.IsPackage ? snapshot.MainEntries : [];
            var mutations = edit.Mutations.Where(row => PathEquals(row.Target.SourcePath,
                Path.Combine(root, location.RelativePath.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
            if (additions.Length == 0 && moving.Count == 0 && mutations.Length == 0) continue;
            var before = location.Source;
            var plan = location.Scope.IsPackage ? snapshot.Main.Plan : snapshot.PackagePlans.Single(location.Scope.Targets);
            var patches = before is null ? new List<WccScenarioFieldSourcePatch>() : MutationPatches(edit, before).ToList();
            var retained = before is null ? RenderStorageFile(snapshot.PackageAuthoring!.AddonId, location.Scope, "") : ApplyMutations(before, patches).Text;
            var retainedSource = new WmlStaticSource(Path.Combine(root, location.RelativePath.Replace('/', Path.DirectorySeparatorChar)), retained);
            var closing = ReadStorageEnvelope(retainedSource).ClosingStart;
            var newline = retained.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var text = new StringBuilder(retained[..closing]);
            if (text.Length != 0 && text[^1] is not ('\n' or '\r')) text.Append(newline);
            var domain = WmlTextDomainCapture.BeforeOffset(edit.Source.Text, snapshot.Main.InsertionOffset);
            foreach (var entry in moving) { Domain(entry.Definition.TextDomain); text.Append(entry.DefinitionRange.Text).Append(newline); }
            foreach (var row in additions)
            {
                Domain(domain);
                text.Append(RenderNewDefinition(row, plan, snapshot.UsesContentReceipts).Replace("\n", newline, StringComparison.Ordinal)).Append(newline);
            }
            var added = text.ToString()[closing..];
            text.Append(retained[closing..]);
            if (text.Length > 8 * 1024 * 1024) throw new InvalidOperationException("The preset utility exceeds its bounded source size.");
            var output = new WmlStaticSource(Path.Combine(root, location.RelativePath.Replace('/', Path.DirectorySeparatorChar)), text.ToString());
            if (before is null) creates.Add(output);
            else
            {
                if (added.Length != 0) patches.Add(new(ReadStorageEnvelope(before).ClosingStart, 0, added));
                if (before.Text != output.Text) replacements.Add(new(before, output, [], patches.OrderBy(patch => patch.Start).ToArray()));
            }
            outputs.Add((location, output));
            void Domain(string? value)
            {
                if (Wcc.Core.Projects.Creation.WccCampaignCreationTokenPolicy.ValidateTextDomain(value) is { } issue) throw new InvalidOperationException(issue);
                if (WmlTextDomainCapture.BeforeOffset(text.ToString(), text.Length) != value) text.Append("#textdomain ").Append(value).Append(newline);
            }
        }
        if (snapshot.Manifest is { } manifest)
        {
            var receipts = new List<WccPackagePresetReceipt>();
            foreach (var (location, output) in outputs)
            {
                var original = manifest.Document;
                var existing = WccPackageContentAuthoringManifest.Read(original).Presets;
                foreach (var old in snapshot.StoredEntries.Where(entry => entry.HasContentReceipt && PathEquals(entry.SourcePath, output.Path)))
                {
                    var mutation = edit.Mutations.SingleOrDefault(row => row.Target.Id == old.Id);
                    if (mutation?.IsDeletion == true) continue;
                    var definition = FindDefinition(output, old.Name);
                    receipts.Add(new(old.Id, Kind(old.Role), old.Name, old.Scope, location.RelativePath,
                        WccContentHash.ComputeText(definition.Text), old.Definition.TextDomain!));
                }
                foreach (var row in edit.Presets.Where(row => row.Scope == location.Scope))
                {
                    var definition = FindDefinition(output, row.Name);
                    receipts.Add(new(row.Id, Kind(row.Payload.Role), row.Name, row.Scope, location.RelativePath,
                        WccContentHash.ComputeText(definition.Text), WmlTextDomainCapture.BeforeOffset(output.Text, definition.StartOffset)!));
                }
            }
            var document = WccPackageContentAuthoringManifest.Apply(manifest.Document, receipts,
                edit.Mutations.Where(row => row.IsDeletion && row.Target.HasContentReceipt).Select(row => row.Target.Id).ToArray());
            foreach (var (location, output) in outputs)
                _ = ReadScopedStorage(output, location.Scope.IsPackage ? snapshot.Main.Plan : snapshot.PackagePlans.Single(location.Scope.Targets),
                    WccPackageContentAuthoringManifest.Read(document).Presets, root, snapshot.PackagePlans,
                    snapshot.Entries.Where(entry => entry.Role == WccCampaignLocalPresetRole.Unit).ToArray());
            var text = WccPackageAuthoringManifest.Serialize(document);
            var path = Path.Combine(root, WccPackageAuthoringManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var companionOutput = new WmlStaticSource(path, text);
            if (manifest.Source is null) creates.Add(companionOutput);
            else if (manifest.Source.Text != text) replacements.Add(new(manifest.Source, companionOutput, [], [new(0, manifest.Source.Text.Length, text)]));
        }
        if (snapshot.Manifest is null)
            foreach (var (_, output) in outputs) _ = ReadStorage(output, snapshot.Main.Plan);
        return new(Array.AsReadOnly(replacements.ToArray()), Array.AsReadOnly(creates.ToArray()));
    }

    internal static WccCampaignLocalPresetEntry? ReadMappedDefinition(WccCampaignLocalPresetEntry original,
        WmlStaticSourceRange range, WccCampaignLocalPresetStorageSnapshot snapshot)
    {
        var units = snapshot.Entries.Where(entry => entry.Role == WccCampaignLocalPresetRole.Unit).ToArray();
        if (!original.HasContentReceipt) return WccCampaignLocalPresetCatalog.ReadDefinitions(range.Source, snapshot.Main.Plan, units)
            .SingleOrDefault(entry => entry.DefinitionRange.StartOffset == range.StartOffset && entry.DefinitionRange.Length == range.Length);
        var root = Path.GetDirectoryName(snapshot.Source.Path)!;
        var receipt = new WccPackagePresetReceipt(original.Id, Kind(original.Role), original.Name, original.Scope,
            Path.GetRelativePath(root, range.Source.Path).Replace('\\', '/'), WccContentHash.ComputeText(range.Text), original.Definition.TextDomain!);
        return WccCampaignLocalPresetCatalog.ReadDefinitions(range.Source, snapshot.Main.Plan, [receipt], root, snapshot.PackagePlans, units)
            .SingleOrDefault(entry => entry.Id == original.Id && entry.DefinitionRange.StartOffset == range.StartOffset && entry.DefinitionRange.Length == range.Length);
    }
    private static string Kind(WccCampaignLocalPresetRole role) => WccCampaignLocalPresetCatalog.Kind(role);
    private static string RenderNewDefinition(WccCampaignLocalPresetCreationSpec row, WmlCampaignEvaluationPlan? plan, bool receipt) =>
        receipt ? WccCampaignLocalPresetCatalog.RenderDefinition(row.Id, row.Name, row.Payload, plan)
            .Replace("    " + WccCampaignLocalPresetCatalog.Marker(row.Payload.Role, row.Id) + "\n", "", StringComparison.Ordinal)
            : WccCampaignLocalPresetCatalog.RenderDefinition(row.Id, row.Name, row.Payload, plan);

    private static WmlStaticSourceRange FindDefinition(WmlStaticSource source, string symbol)
    {
        var scan = WmlAuthoredStructureScanner.Scan(source.Text);
        var matches = scan.Directives.Where(directive => directive.Name == "define" && directive.DefinitionDepth == 0)
            .Where(directive => source.Text[scan.Lines[directive.Line].StartOffset..scan.Lines[directive.Line].ContentEndOffset].Trim() == "#define " + symbol).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The prospective utility has no unique requested definition.");
        var first = scan.Lines[matches[0].Line];
        var close = scan.Directives.First(directive => directive.Name == "enddef" && directive.DefinitionDepth == 1 && directive.Line > matches[0].Line);
        return new(source, first.StartOffset, scan.Lines[close.Line].EndOffsetExclusive - first.StartOffset);
    }

    private static IReadOnlyList<WccScenarioFieldSourcePatch> PlanScopeIncludes(WccCampaignLocalPresetStorageEdit edit)
    {
        var patches = new List<WccScenarioFieldSourcePatch>();
        var scan = WmlAuthoredStructureScanner.Scan(edit.Source.Text);
        var newline = edit.Source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var location in edit.Snapshot.Locations.Where(location => location.IncludeRange is null
            && (edit.Presets.Any(row => row.Scope == location.Scope) || edit.Mutations.Count == 0 && location.Scope.IsPackage && edit.Snapshot.MainEntries.Count != 0)))
        {
            // Shared declarations must become available in every existing gate;
            // chapter-local declarations are activated only by their own gate.
            foreach (var plan in edit.Snapshot.PackagePlans.Where(location.Scope.Targets))
            {
                var metadata = WccCampaignChapterAuthoringPlanner.ResolveSelectedAuthoring(edit.Snapshot.PackageAuthoring, edit.Source,
                    plan.CampaignDefine, edit.Snapshot.Chapters?.ProjectSources ?? [edit.Source]) ?? edit.Snapshot.PackageAuthoring;
                var structure = WccCampaignStructurePlanner.Read(edit.Source, metadata);
                var shared = structure.Layout!.Paths.Single(row => row.Row.Kind == WccCampaignPathKind.CfgInclude && row.Row.Path == "utils");
                var utilityPath = location.Scope.IsPackage ? "utils" : "chapters/" + location.Scope.ManagedChapterId + "/utils";
                var utility = structure.Layout.Paths.SingleOrDefault(row => row.Row.Kind == WccCampaignPathKind.CfgInclude && row.Row.Path == utilityPath);
                var anchor = utility ?? shared;
                var line = scan.Lines.First(line => line.StartOffset <= anchor.Range.StartOffset && line.EndOffsetExclusive > anchor.Range.StartOffset);
                var indentation = edit.Source.Text[line.StartOffset..anchor.Range.StartOffset];
                var text = indentation + "{~add-ons/" + edit.Snapshot.PackageAuthoring!.AddonId + "/" + location.RelativePath + "}" + newline;
                if (utility is null) text += indentation + "{~add-ons/" + edit.Snapshot.PackageAuthoring.AddonId + "/" + utilityPath + "}" + newline;
                patches.Add(new(utility is null ? line.EndOffsetExclusive : line.StartOffset, 0, text));
            }
        }
        return patches;
    }

    internal static IReadOnlyList<WccCampaignLocalPresetEntry> ReadScopedStorage(WmlStaticSource source,
        WmlCampaignEvaluationPlan? plan,
        IReadOnlyList<WccPackagePresetReceipt> receipts, string root, IReadOnlyList<WmlCampaignEvaluationPlan> plans,
        IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets)
    {
        if (!source.IsAuthored || source.Text.Length > 8 * 1024 * 1024
            || source.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) != StorageHeader)
            throw new InvalidOperationException("The existing utility is not exact managed preset storage; preserve it and use RAW.");
        _ = ReadStorageEnvelope(source, ExpectedStorageGuard(source, root));
        var definitions = WccCampaignLocalPresetCatalog.ReadDefinitions(source, plan, receipts, root, plans, unitPresets);
        ValidateStorageCoverage(source, definitions);
        // Keep the exact first-pass Unit objects used by Event graph capture.
        return Array.AsReadOnly(definitions.Select(entry => entry.Role == WccCampaignLocalPresetRole.Unit
            ? unitPresets?.SingleOrDefault(unit => unit.Id == entry.Id && unit.Name == entry.Name && unit.Scope == entry.Scope &&
                SameSourceRange(unit.DefinitionRange, entry.DefinitionRange) && SameSourceRange(unit.BodyRange, entry.BodyRange)) ?? entry
            : entry).ToArray());
    }

    private static void ValidateStorageCoverage(WmlStaticSource source, IReadOnlyList<WccCampaignLocalPresetEntry> definitions)
    {
        var scan = WmlAuthoredStructureScanner.Scan(source.Text);
        if (scan.Problem is not null || definitions.Any(entry => !entry.IsSupported))
            throw new InvalidOperationException("Preset storage has incomplete, stale or unsupported declarations.");
        var envelope = ReadStorageEnvelope(source, structure: scan);
        if (definitions.Any(entry => entry.DefinitionRange.StartOffset < envelope.BodyStart
                || entry.DefinitionRange.StartOffset + entry.DefinitionRange.Length > envelope.ClosingStart))
            throw new InvalidOperationException("Every preset definition must remain inside its exact include guard.");
        bool Inside(int offset) => definitions.Any(entry => offset >= entry.DefinitionRange.StartOffset && offset < entry.DefinitionRange.StartOffset + entry.DefinitionRange.Length);
        bool EnvelopeDirective(int offset) => offset == envelope.Opening.StartOffset || offset == envelope.Definition.StartOffset
            || offset >= envelope.Definition.StartOffset && offset < envelope.Definition.StartOffset + envelope.Definition.Length
            || offset == envelope.Closing.StartOffset;
        if (scan.Macros.Any(macro => !Inside(macro.StartOffset)) || scan.Directives.Any(directive => !Inside(scan.Lines[directive.Line].StartOffset)
                && !EnvelopeDirective(scan.Lines[directive.Line].StartOffset) && directive.Name != "textdomain"))
            throw new InvalidOperationException("Preset storage contains extra includes or stateful declarations; use RAW.");
        var remaining = scan.MaskedText.ToCharArray();
        Array.Fill(remaining, ' ', 0, envelope.BodyStart);
        Array.Fill(remaining, ' ', envelope.ClosingStart, envelope.Closing.Length);
        foreach (var definition in definitions) Array.Fill(remaining, ' ', definition.DefinitionRange.StartOffset, definition.DefinitionRange.Length);
        foreach (var directive in scan.Directives.Where(directive => directive.DefinitionDepth == 0 && directive.Name == "textdomain"))
        {
            var line = scan.Lines[directive.Line];
            if (line.StartOffset < envelope.BodyStart || line.StartOffset >= envelope.ClosingStart)
                throw new InvalidOperationException("Text domains must remain inside the preset storage guard.");
            if (Wcc.Core.Projects.Creation.WccCampaignCreationTokenPolicy.ValidateTextDomain(source.Text[line.StartOffset..line.ContentEndOffset].Trim()["#textdomain".Length..].Trim()) is { } issue)
                throw new InvalidOperationException(issue);
            Array.Fill(remaining, ' ', line.StartOffset, line.EndOffsetExclusive - line.StartOffset);
        }
        if (remaining.Any(character => !char.IsWhiteSpace(character))) throw new InvalidOperationException("Preset storage has unowned executable source; use RAW.");
    }
}