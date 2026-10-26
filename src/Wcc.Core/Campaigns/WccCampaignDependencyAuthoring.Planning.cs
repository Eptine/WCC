using System.Text;
using System.Text.Json;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public static partial class WccCampaignDependencyAuthoringPlanner
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record CampaignLocation(string Define, WmlStaticSource Source, int Start, int Close, int End);

    public static WccCampaignDependencySnapshot Read(WmlStaticSource main, string projectAddonId,
        IReadOnlyList<WmlStaticSource> sources)
    {
        ArgumentNullException.ThrowIfNull(main); ArgumentNullException.ThrowIfNull(sources);
        var entries = new List<WccCampaignDependencyEntry>();
        var captured = Array.AsReadOnly(sources.OrderBy(source => source?.Path ?? "", StringComparer.Ordinal).ToArray());
        try
        {
            if (!main.IsAuthored || Path.GetFileName(main.Path) != "_main.cfg" ||
                Wcc.Core.Projects.Creation.WccCampaignCreationTokenPolicy.ValidateAddonId(projectAddonId) is not null ||
                sources.Count is < 1 or > 200_000 || sources.Any(source => source is null || !source.IsAuthored) ||
                sources.Select(source => source.Path).Distinct(PathComparer).Count() != sources.Count ||
                !sources.Any(source => ReferenceEquals(source, main)))
                throw new InvalidOperationException("Dependency authoring needs one exact captured package main and a bounded unique source inventory.");
            var root = Path.GetDirectoryName(main.Path)!;
            var local = captured.Where(source => InsideRoot(root, source.Path)).ToArray();
            var mainScan = WmlAuthoredStructureScanner.Scan(main.Text);
            if (main.Text.Length > 8 * 1024 * 1024 || mainScan.Problem is not null || !WmlSingleBlockValidator.ValidateSourceStructure(main.Text).IsValid)
                throw new InvalidOperationException(mainScan.Problem ?? "The package main has incomplete literal structure; use RAW.");
            var campaigns = FindCampaigns(local);
            if (campaigns.Count == 0) throw new InvalidOperationException("No isolated literal campaign definition is captured; use Main file RAW.");
            var manifest = WccPackageAuthoringManifest.Read(main, projectAddonId, local);
            var records = manifest.Document.Dependencies;
            foreach (var record in records)
            {
                RequireSpec(record.Spec);
                if (record.LoaderHash != WccContentHash.ComputeText(RenderLoader(record.Spec)) || record.RecipeHash != RecipeHash(record.Spec))
                    throw new InvalidOperationException("The dependency companion no longer matches its complete recipe and loader; use RAW.");
            }
            ValidateSourceIdentities(records.Select(record => record.Spec).ToArray());
            if (records.Select(record => record.Spec.Id).Distinct().Count() != records.Count)
                throw new InvalidOperationException("Dependency identities must be unique in this package; use RAW.");
            var rootRanges = FindCommentBlocks(main, RootMarkerPrefix, RootMarkerEndPrefix);
            if (rootRanges.Count > 64 || rootRanges.Count != records.Count)
                throw new InvalidOperationException("Dependency registrations and companion recipes differ; preserve the companion and use RAW.");
            foreach (var range in rootRanges)
            {
                var addonId = ValidateCompactRootShape(range);
                var record = records.SingleOrDefault(item => item.Spec.AddonId == addonId)
                    ?? throw new InvalidOperationException("The dependency source marker has no complete companion recipe; use RAW.");
                var spec = record.Spec;
                if (range.Text != RenderRootHook(spec, projectAddonId, Newline(main)) || !IsRootOffset(main, range.StartOffset))
                    throw new InvalidOperationException("A managed dependency hook was moved or altered; use RAW.");
                var relativeLoader = LoaderRelativePath(spec);
                var loaderPath = Path.Combine(root, relativeLoader.Replace('/', Path.DirectorySeparatorChar));
                var loader = local.SingleOrDefault(source => PathComparer.Equals(source.Path, loaderPath));
                if (loader is null || loader.Text != RenderLoader(spec))
                    throw new InvalidOperationException("A managed loader is absent or differs from its captured recipe; preserve it and use RAW.");
                var owned = manifest.Document.OwnedFiles.Where(item => item.OwnerKind == "dependency" && item.OwnerId == spec.Id.ToString("N")).ToArray();
                if (owned.Length != 1 || owned[0] != new WccPackageOwnedFile("dependency", spec.Id.ToString("N"), relativeLoader, "loader", loader.ContentHash))
                    throw new InvalidOperationException("The dependency loader has no exact owned-file receipt; use RAW.");
                var hooks = new List<WmlStaticSourceRange>();
                foreach (var target in spec.TargetCampaignDefines)
                {
                    var candidates = campaigns.Where(campaign => campaign.Define == target).ToArray();
                    if (candidates.Length != 1) throw new InvalidOperationException("A managed dependency target is absent or ambiguous; use RAW.");
                    var campaign = candidates[0]; var expected = RenderCampaignHook(spec, target, Newline(campaign.Source));
                    if (expected.Length == 0) continue;
                    var offset = campaign.Source.Text.IndexOf(expected, campaign.Start, campaign.End - campaign.Start, StringComparison.Ordinal);
                    if (offset < 0 || offset + expected.Length > campaign.Close ||
                        campaign.Source.Text.IndexOf(expected, offset + expected.Length, StringComparison.Ordinal) >= 0)
                        throw new InvalidOperationException("A managed campaign resource hook was removed, changed or duplicated; use RAW.");
                    hooks.Add(new(campaign.Source, offset, expected.Length));
                }
                entries.Add(new(spec, range, loader, hooks));
            }
            if (entries.Select(entry => entry.Spec.Id).Distinct().Count() != entries.Count ||
                manifest.Document.OwnedFiles.Count(item => item.OwnerKind == "dependency") != entries.Count ||
                entries.Count == 0 && manifest.Document.DependencyMetadata is not null)
                throw new InvalidOperationException("Dependency recipes or ownership receipts are repeated or orphaned; use RAW.");
            foreach (var source in local.Where(source => source.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)))
            {
                var actual = FindCommentBlocks(source, CampaignMarkerPrefix, CampaignMarkerEndPrefix);
                var expected = entries.SelectMany(entry => entry.CampaignHookRanges).Where(range => ReferenceEquals(range.Source, source)).ToArray();
                if (actual.Count != expected.Length || actual.Any(range => !expected.Any(value => value.StartOffset == range.StartOffset && value.Length == range.Length)))
                    throw new InvalidOperationException("Unmatched managed campaign hooks require RAW inspection.");
                if (!ReferenceEquals(source, main) && FindCommentBlocks(source, RootMarkerPrefix, RootMarkerEndPrefix).Count != 0)
                    throw new InvalidOperationException("Managed dependency registration belongs in the package main.");
            }
            if (entries.Any(entry => entry.Spec.DeclareDependency) && manifest.Document.DependencyMetadata is null)
                throw new InvalidOperationException("The managed download declarations have no ownership receipt; use RAW.");
            var snapshot = new WccCampaignDependencySnapshot(main, projectAddonId, captured, entries, true,
                "New loading steps are staged in this package. Existing unmarked dependency code remains RAW-only.")
                { MetadataReceipt = manifest.Document.DependencyMetadata, ManifestSource = manifest.Source, Manifest = manifest };
            if (snapshot.MetadataReceipt is { } metadataReceipt) ValidateCurrentMetadata(snapshot, metadataReceipt);
            return snapshot;
        }
        catch (Exception issue) when (issue is ArgumentException or InvalidOperationException or OverflowException)
        { return new(main, projectAddonId, captured, entries, false, issue.Message); }
    }

    public static WccCampaignDependencyMutationPlan Plan(WccCampaignDependencySnapshot snapshot,
        IEnumerable<WccCampaignDependencySpec> desiredSpecs)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(desiredSpecs);
        var fresh = Read(snapshot.MainSource, snapshot.ProjectAddonId, snapshot.Sources);
        if (!snapshot.CanAdd || !fresh.CanAdd) throw new InvalidOperationException(fresh.Diagnostic);
        var desired = desiredSpecs.Select(Freeze).ToArray();
        if (desired.Length > 64 || desired.Any(spec => spec.AddonId == snapshot.ProjectAddonId) ||
            desired.Select(spec => spec.Id).Distinct().Count() != desired.Length ||
            desired.Select(spec => spec.AddonId).Distinct(StringComparer.Ordinal).Count() != desired.Length)
            throw new ArgumentException("Keep at most 64 unique dependency identities and external add-on IDs.", nameof(desiredSpecs));
        foreach (var spec in desired) RequireSpec(spec);
        ValidateSourceIdentities(desired);
        var root = Path.GetDirectoryName(snapshot.MainSource.Path)!;
        var campaigns = FindCampaigns(fresh.Sources.Where(source => InsideRoot(root, source.Path)).ToArray());
        foreach (var target in desired.SelectMany(spec => spec.TargetCampaignDefines).Distinct(StringComparer.Ordinal))
            if (campaigns.Count(campaign => campaign.Define == target) != 1)
                throw new InvalidOperationException("Select one uniquely captured literal campaign for every dependency target.");
        var metadataPlan = PlanMetadata(fresh, desired);
        var patches = new Dictionary<WmlStaticSource, List<(int Start, int Length, string Text)>>();
        void Patch(WmlStaticSource source, int start, int length, string text)
        { if (!patches.TryGetValue(source, out var list)) patches.Add(source, list = []); list.Add((start, length, text)); }
        foreach (var entry in fresh.Entries)
        {
            Patch(entry.RootRange.Source, entry.RootRange.StartOffset, entry.RootRange.Length, "");
            foreach (var hook in entry.CampaignHookRanges) Patch(hook.Source, hook.StartOffset, hook.Length, "");
        }
        var prefix = string.Concat(desired.Select(spec => RenderRootHook(spec, snapshot.ProjectAddonId, Newline(snapshot.MainSource))));
        if (prefix.Length != 0) Patch(snapshot.MainSource, snapshot.MainSource.Text.StartsWith('\uFEFF') ? 1 : 0, 0, prefix);
        foreach (var campaign in campaigns)
        {
            var hooks = string.Concat(desired.Where(spec => spec.TargetCampaignDefines.Contains(campaign.Define, StringComparer.Ordinal))
                .Select(spec => RenderCampaignHook(spec, campaign.Define, Newline(campaign.Source))));
            if (hooks.Length == 0) continue;
            var lineStart = LineStart(campaign.Source.Text, campaign.Close);
            Patch(campaign.Source, lineStart, 0, hooks);
        }
        var replacements = new List<WccCampaignPackageSourceReplacement>();
        foreach (var (source, edits) in patches)
        {
            var text = Apply(source.Text, edits);
            if (text != source.Text) replacements.Add(new(source, text));
        }
        if (metadataPlan.Replacement is { } metadataReplacement) replacements.Add(metadataReplacement);
        var creates = new List<WmlStaticSource>();
        if (metadataPlan.Creation is { } metadataCreation) creates.Add(metadataCreation);
        foreach (var spec in desired)
        {
            var path = Path.Combine(root, LoaderRelativePath(spec).Replace('/', Path.DirectorySeparatorChar));
            var existing = snapshot.Sources.SingleOrDefault(source => PathComparer.Equals(source.Path, path));
            var text = RenderLoader(spec);
            if (existing is null) creates.Add(new(path, text));
            else
            {
                var owned = fresh.Entries.SingleOrDefault(entry => entry.Spec.AddonId == spec.AddonId);
                if (owned is null || !ReferenceEquals(owned.LoaderSource, existing))
                    throw new InvalidOperationException("The loader destination already exists outside this managed dependency; choose a new identity.");
                if (existing.Text != text) replacements.Add(new(existing, text));
            }
        }
        var document = fresh.Manifest!.Document with
        {
            Dependencies = desired.Select(spec => new WccPackageAuthoredDependency(spec,
                WccContentHash.ComputeText(RenderLoader(spec)), RecipeHash(spec))).ToArray(),
            DependencyMetadata = metadataPlan.Receipt,
            OwnedFiles = fresh.Manifest.Document.OwnedFiles.Where(item => item.OwnerKind != "dependency")
                .Concat(desired.Select(spec => new WccPackageOwnedFile("dependency", spec.Id.ToString("N"),
                    LoaderRelativePath(spec), "loader", WccContentHash.ComputeText(RenderLoader(spec))))).ToArray()
        };
        if (desired.Length != 0 || fresh.ManifestSource is not null)
        {
            // Preserve companion bytes when this operation has no semantic metadata change.
            var manifestText = WccPackageAuthoringManifest.Serialize(document);
            var unchanged = manifestText == WccPackageAuthoringManifest.Serialize(fresh.Manifest.Document);
            if (fresh.ManifestSource is { } manifest)
            {
                if (!unchanged) replacements.Add(new(manifest, manifestText));
            }
            else creates.Add(new(Path.Combine(root, PackageManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)), manifestText));
        }
        var deletes = fresh.Entries.Where(entry => !desired.Any(spec => LoaderRelativePath(spec) == LoaderRelativePath(entry.Spec)))
            .Select(entry => entry.LoaderSource).ToArray();
        foreach (var deleted in deletes)
        {
            var entry = fresh.Entries.Single(item => ReferenceEquals(item.LoaderSource, deleted));
            var reference = "~add-ons/" + snapshot.ProjectAddonId + "/" + LoaderRelativePath(entry.Spec);
            var wrapper = CompactPrefix(entry.Spec.AddonId);
            foreach (var source in fresh.Sources.Where(source => !ReferenceEquals(source, deleted) && source.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)))
            {
                var candidate = replacements.SingleOrDefault(file => ReferenceEquals(file.Source, source))?.Candidate ?? source.Text;
                if (candidate.Contains(reference, StringComparison.Ordinal) || candidate.Contains("{" + wrapper + "_", StringComparison.Ordinal))
                    throw new InvalidOperationException("Another captured source still uses this dependency loader. Preserve it and resolve its shared use through RAW before removing it.");
            }
        }
        var identity = WccContentHash.ComputeText(string.Join("\n", replacements.Select(file => file.Source.Path + "\n" + file.Source.ContentHash + "\n" + WccContentHash.ComputeText(file.Candidate))) +
            "\n" + string.Join("\n", creates.Select(file => file.Path + "\n" + file.ContentHash)) +
            "\n" + string.Join("\n", deletes.Select(file => file.Path + "\n" + file.ContentHash)));
        return new(fresh, desired, replacements.OrderBy(file => file.Source.Path, StringComparer.Ordinal),
            creates.OrderBy(source => source.Path, StringComparer.Ordinal), identity, deletes);
    }
    public static IReadOnlyList<WmlStaticSourceRange> ReadManagedSourceRegions(WccCampaignDependencySnapshot snapshot, WmlStaticSource source)
    {
        if (!snapshot.CanAdd || !snapshot.Sources.Any(value => ReferenceEquals(value, source))) return [];
        return Array.AsReadOnly(snapshot.Entries.SelectMany(entry => new[] { entry.RootRange }.Concat(entry.CampaignHookRanges))
            .Where(range => ReferenceEquals(range.Source, source)).OrderBy(range => range.StartOffset).ToArray());
    }

    public static string MaskManagedDependencyBlocks(WccCampaignDependencySnapshot snapshot, WmlStaticSource source)
    {
        var chars = source.Text.ToCharArray();
        foreach (var range in ReadManagedSourceRegions(snapshot, source))
            for (var index = range.StartOffset; index < range.StartOffset + range.Length; index++)
                if (chars[index] is not ('\r' or '\n')) chars[index] = ' ';
        return new(chars);
    }

    /// <summary>Lexical layout selection only. Exact wrapper bytes are recognized, but loader/source freshness
    /// must still be established by Read over the complete captured inventory during checked Save.</summary>
    public static string MaskManagedDependencyBlocksForStructure(WmlStaticSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var chars = source.Text.ToCharArray();
        var roots = FindCommentBlocks(source, RootMarkerPrefix, RootMarkerEndPrefix);
        if (roots.Count > 64) throw new InvalidOperationException("The dependency layout exceeds its bounded scope.");
        foreach (var range in roots)
        {
            ValidateCompactRootShape(range);
            if (!IsRootOffset(source, range.StartOffset)) throw new InvalidOperationException("A dependency registration is no longer at the package root.");
            Mask(range);
        }
        foreach (var range in FindCommentBlocks(source, CampaignMarkerPrefix, CampaignMarkerEndPrefix))
        { ValidateCompactCampaignShape(range); Mask(range); }
        return new(chars);
        void Mask(WmlStaticSourceRange range)
        {
            for (var offset = range.StartOffset; offset < range.StartOffset + range.Length; offset++)
                if (chars[offset] is not ('\r' or '\n')) chars[offset] = ' ';
        }
    }
    private static WccCampaignDependencySpec Freeze(WccCampaignDependencySpec spec)
    { ArgumentNullException.ThrowIfNull(spec); return new(spec.Id, spec.AddonId, spec.TargetCampaignDefines, spec.Steps, spec.DeclareDependency, spec.AppliesToAllCampaigns); }
    private static bool InsideRoot(string root, string path) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static int LineStart(string text, int offset)
    { while (offset > 0 && text[offset - 1] is not ('\r' or '\n')) offset--; return offset == 0 && text.StartsWith('\uFEFF') ? 1 : offset; }

    private static IReadOnlyList<CampaignLocation> FindCampaigns(IReadOnlyList<WmlStaticSource> sources)
    {
        var result = new List<CampaignLocation>();
        foreach (var source in sources)
        {
            if (!source.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) || source.Text.Length > 8 * 1024 * 1024) continue;
            var scan = WmlAuthoredStructureScanner.Scan(source.Text);
            if (scan.Problem is not null || !WmlSingleBlockValidator.ValidateSourceStructure(source.Text).IsValid) continue;
            var analysis = WmlEditorAnalysis.Build(scan.MaskedText);
            var stack = new Stack<WmlEditorTagOccurrence>();
            foreach (var tag in analysis.TagOccurrences)
            {
                if (!tag.IsClosing) { stack.Push(tag); continue; }
                if (stack.Count == 0) break;
                var open = stack.Pop();
                if (tag.Name != open.Name) break;
                if (open.Name != "campaign" || stack.Count != 0 || !IsRootOffset(source, open.StartOffset)) continue;
                var block = source.Text[open.StartOffset..(tag.StartOffset + tag.Length)];
                var parsed = WmlEditableBlockParser.Parse(new WmlBlock("campaign", block), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "define" });
                if (!parsed.Known.TryGetValue("define", out var field) || !Wcc.Core.Scenarios.WccScenarioFieldValueCodec.IsSupportedLiteralWml(field.Value)) continue;
                var define = Wcc.Core.Scenarios.WccScenarioFieldValueCodec.DecodeLiteral(field.Value);
                if (Symbol(define)) result.Add(new(define, source, open.StartOffset, tag.StartOffset, tag.StartOffset + tag.Length));
            }
        }
        return result;
    }

    private static bool IsRootOffset(WmlStaticSource source, int offset)
    {
        var scan = WmlAuthoredStructureScanner.Scan(source.Text);
        var lineIndex = 0;
        while (lineIndex + 1 < scan.Lines.Count && scan.Lines[lineIndex + 1].StartOffset <= offset) lineIndex++;
        if (scan.ConditionalLines[lineIndex]) return false;
        var definitionDepth = 0;
        foreach (var directive in scan.Directives.Where(item => scan.Lines[item.Line].StartOffset < offset))
        { if (directive.Name == "define") definitionDepth++; else if (directive.Name == "enddef") definitionDepth--; }
        if (definitionDepth != 0) return false;
        var depth = 0;
        foreach (var tag in WmlEditorAnalysis.Build(scan.MaskedText).TagOccurrences.Where(tag => tag.StartOffset < offset))
            depth += tag.IsClosing ? -1 : 1;
        return depth == 0;
    }

    private static IReadOnlyList<WmlStaticSourceRange> FindCommentBlocks(WmlStaticSource source, string begin, string end)
    {
        var analysis = WmlEditorAnalysis.Build(source.Text);
        var result = new List<WmlStaticSourceRange>();
        int? start = null;
        foreach (var token in analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Comment))
        {
            var text = source.Text.Substring(token.StartOffset, token.Length).TrimEnd();
            if (text.StartsWith(begin, StringComparison.Ordinal))
            { if (start is not null) throw new InvalidOperationException("Nested managed dependency ownership markers require RAW."); start = LineStart(source.Text, token.StartOffset); }
            else if (text.StartsWith(end, StringComparison.Ordinal))
            {
                if (start is null) throw new InvalidOperationException("A managed dependency end marker has no matching start.");
                var finish = token.StartOffset + token.Length;
                while (finish < source.Text.Length && source.Text[finish] is '\r' or '\n')
                { if (source.Text[finish++] == '\n') break; }
                result.Add(new(source, start.Value, finish - start.Value)); start = null;
            }
        }
        if (start is not null) throw new InvalidOperationException("A managed dependency ownership marker is not closed.");
        return result;
    }

    private static string Apply(string source, IEnumerable<(int Start, int Length, string Text)> edits)
    {
        var result = new StringBuilder(); var position = 0;
        foreach (var group in edits.GroupBy(edit => (edit.Start, edit.Length)).OrderBy(group => group.Key.Start).ThenBy(group => group.Key.Length))
        {
            if (group.Key.Start < position) throw new InvalidOperationException("Managed dependency changes overlap another source region.");
            result.Append(source, position, group.Key.Start - position);
            var items = group.ToArray();
            if (group.Key.Length != 0 && items.Length > 1) throw new InvalidOperationException("Several dependency hooks claim the same source region.");
            result.Append(string.Concat(items.Select(item => item.Text)));
            position = group.Key.Start + group.Key.Length;
        }
        result.Append(source, position, source.Length - position);
        var text = result.ToString();
        if (text.Length > 8 * 1024 * 1024) throw new InvalidOperationException("The managed dependency proposal exceeds its bounded source-file size.");
        if (WmlSingleBlockValidator.ValidateSourceStructure(text) is { IsValid: false } invalid) throw new InvalidOperationException(invalid.Problem);
        return text;
    }
}