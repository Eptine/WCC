using System.Text.RegularExpressions;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>Pure, bounded source inventory and typed patches for the layout emitted by the creator.</summary>
public static class WccCampaignStructurePlanner
{
    private static readonly string[] DifficultyKeys = ["define", "label", "description", "image", "default"];

    public static WccCampaignStructureSnapshot Read(WmlStaticSource source, WccCampaignAuthoringMetadata? authoring)
    {
        ArgumentNullException.ThrowIfNull(source);
        var difficulties = new List<WccCampaignDifficultyPresentation>();
        var paths = new List<WccCampaignPathReference>();
        try
        {
            // Layout selection keeps exact physical coordinates. The real source is retained below
            // for values, hashes and write receipts; this mask never activates dependency content.
            var shapeText = WccCampaignDependencyAuthoringPlanner.MaskManagedDependencyBlocksForStructure(source);
            if (authoring is not null)
                shapeText = WccCampaignChapterAuthoringPlanner.MaskOtherChaptersForStructure(
                    new WmlStaticSource(source.Path, shapeText), authoring.CampaignDefine);
            var scan = WmlAuthoredStructureScanner.Scan(shapeText);
            if (scan.Problem is not null || !WmlSingleBlockValidator.ValidateSourceStructure(shapeText).IsValid)
                throw new InvalidOperationException(scan.Problem ?? "The source has unresolved literal structure.");
            var roots = Nodes(scan.MaskedText);
            var campaigns = roots.Where(node => node.Tag == "campaign").ToArray();
            if (campaigns.Length != 1) throw new InvalidOperationException("A unique literal campaign block is required.");
            var campaign = campaigns[0];
            var projection = Project(source, campaign);
            foreach (var child in projection.DirectChildren.Where(child => child.TagName == "difficulty"))
                difficulties.Add(new(Decode(child, "define"), Decode(child, "label"), Decode(child, "description"),
                    Decode(child, "image"), Decode(child, "default") is "yes" or "true"));
            var pathNodes = new List<WccCampaignStructurePath>();
            foreach (var node in roots.Where(node => node.Tag == "binary_path"))
            {
                var owner = Project(source, node);
                var raw = Decode(owner, "path");
                var relative = Relative(raw, "data/add-ons/", authoring?.AddonId);
                var row = new WccCampaignPathReference(WccCampaignPathKind.BinaryRoot, relative,
                    Key(WccCampaignPathKind.BinaryRoot, node.Start, node.End - node.Start));
                paths.Add(row); pathNodes.Add(new(row, new(source, node.Start, node.End - node.Start)));
            }
            var units = roots.Where(node => node.Tag == "units").ToArray();
            var unsupportedMacro = false;
            foreach (var macro in scan.Macros)
            {
                var raw = source.Text[macro.StartOffset..macro.EndOffsetExclusive];
                var inner = raw[1..^1].Trim();
                if (!inner.Contains('/') || inner.Any(char.IsWhiteSpace) || inner.IndexOfAny(['{', '}', '$', '(', ')', '=', '"', '[', ']']) >= 0)
                { unsupportedMacro = true; continue; }
                var kind = units.Any(node => macro.StartOffset > node.Start && macro.EndOffsetExclusive < node.Close)
                    ? WccCampaignPathKind.UnitsInclude : WccCampaignPathKind.CfgInclude;
                var relative = inner;
                try { relative = Relative(inner, "~add-ons/", authoring?.AddonId); }
                catch (InvalidOperationException) { unsupportedMacro = true; }
                var row = new WccCampaignPathReference(kind, relative,
                    Key(kind, macro.StartOffset, macro.EndOffsetExclusive - macro.StartOffset));
                paths.Add(row); pathNodes.Add(new(row, new(source, macro.StartOffset, macro.EndOffsetExclusive - macro.StartOffset)));
            }
            paths = paths.OrderBy(row => pathNodes.Single(item => item.Row.Key == row.Key).Range.StartOffset).ToList();
            if (authoring is null) throw new InvalidOperationException("Imported campaign structure is shown as an inventory. Use RAW to edit its topology.");
            if (unsupportedMacro) throw new InvalidOperationException("An invocation is not a literal directory include into this add-on; use RAW.");
            if (!source.IsAuthored || authoring.LayoutVersion is not (1 or 2) || !authoring.UsesExplicitDifficultyValues || authoring.MainRelativePath != "_main.cfg"
                || WccCampaignCreationTokenPolicy.ValidateAddonId(authoring.AddonId) is not null || !Symbol(authoring.CampaignDefine)
                || Path.GetFileName(source.Path) != "_main.cfg"
                || !string.Equals(Path.GetFileName(Path.GetDirectoryName(source.Path)), authoring.AddonId, StringComparison.Ordinal)
                || authoring.FixedDifficultyDefines is null || authoring.FixedDifficultyDefines.Count is < 1 or > 16
                || authoring.FixedDifficultyDefines.Any(value => !Symbol(value))
                || authoring.FixedDifficultyDefines.Distinct(StringComparer.Ordinal).Count() != authoring.FixedDifficultyDefines.Count)
                throw new InvalidOperationException("The authoring marker does not match this exact main file and stable add-on layout.");
            if (Decode(projection, "define") != authoring.CampaignDefine
                || projection.Fields.ContainsKey("difficulties") || projection.Fields.ContainsKey("default_difficulty")
                || projection.Fields.ContainsKey("extra_defines") || Decode(projection, "type") is not ("" or "sp")
                || campaign.Children.Any(node => node.Tag is not ("difficulty" or "about"))
                || difficulties.Count != campaign.Children.Count(node => node.Tag == "difficulty")
                || !difficulties.Select(row => row.Define).SequenceEqual(authoring.FixedDifficultyDefines, StringComparer.Ordinal)
                || difficulties.Count(row => row.IsDefault) != 1)
                throw new InvalidOperationException("The campaign's actual symbols, order or difficulty topology differ from the authoring layout.");
            foreach (var child in projection.DirectChildren.Where(child => child.TagName == "difficulty"))
                if (child.DirectChildren.Count != 0 || child.Fields.Keys.Any(key => !DifficultyKeys.Contains(key, StringComparer.Ordinal))
                    || child.Fields.Any(pair => !Literal(pair.Value, WccWmlPlainTextCodec.IsTextField(child.TagName, pair.Key, child.Parent?.TagName))) || child.IsAmended
                    || Decode(child, "default") is not ("" or "yes" or "true" or "no" or "false"))
                    throw new InvalidOperationException("A difficulty contains shared, repeated or unsupported authored values; use RAW.");
            if (units.Length != 1 || units[0].Children.Count != 0
                || roots.Any(node => node.Tag is not ("campaign" or "binary_path" or "textdomain" or "units")))
                throw new InvalidOperationException("Only the creator's single units wrapper and literal resource roots are editable.");
            // Only exact supported managed bodies may be ignored by the layout
            // inventory. The marker is not activation or persistence authority.
            var presetPlans = WmlCampaignEvaluationPlanDiscovery.Discover(source.Text, source.Path, "1.20")
                .Where(plan => plan.CampaignDefine == authoring.CampaignDefine).ToArray();
            var managed = WccCampaignLocalPresetCatalog.ReadDefinitions(source, presetPlans.Length == 1 ? presetPlans[0] : null)
                .Where(entry => entry.IsSupported && !shapeText.AsSpan(entry.DefinitionRange.StartOffset, entry.DefinitionRange.Length).IsWhiteSpace()).ToArray();
            var gates = scan.Directives.Where(directive => directive.Name is not "textdomain"
                && !managed.Any(entry => scan.Lines[directive.Line].StartOffset >= entry.DefinitionRange.StartOffset
                    && scan.Lines[directive.Line].StartOffset < entry.DefinitionRange.StartOffset + entry.DefinitionRange.Length)).ToArray();
            if (gates.Length != 2 || gates[0].Name != "ifdef" || gates[1].Name != "endif"
                || gates.Any(directive => directive.DefinitionDepth != 0))
                throw new InvalidOperationException("The main file requires one complete canonical campaign gate.");
            var gateStart = scan.Lines[gates[0].Line];
            var gateEnd = scan.Lines[gates[1].Line];
            var firstExecutable = Math.Min(units[0].Start,
                pathNodes.Where(item => item.Row.Kind != WccCampaignPathKind.BinaryRoot).Select(item => item.Range.StartOffset).DefaultIfEmpty(gateEnd.StartOffset).Min());
            if (managed.Any(entry => entry.DefinitionRange.StartOffset < gateStart.EndOffsetExclusive
                    || entry.DefinitionRange.StartOffset + entry.DefinitionRange.Length > firstExecutable))
                throw new InvalidOperationException("Managed preset definitions must stay inside the campaign gate before executable includes.");
            if (source.Text[gateStart.StartOffset..gateStart.ContentEndOffset].Trim() != "#ifdef " + authoring.CampaignDefine
                || campaign.End > gateStart.StartOffset || units[0].Start < gateStart.EndOffsetExclusive || units[0].End > gateEnd.StartOffset
                || roots.Where(node => node.Tag is "binary_path" or "textdomain").Any(node => node.End > gateStart.StartOffset))
                throw new InvalidOperationException("The literal campaign gate and menu resource roots have changed shape.");
            foreach (var item in pathNodes)
            {
                ValidatePath(item.Row);
                if (item.Row.Kind == WccCampaignPathKind.BinaryRoot)
                {
                    var node = roots.Single(node => node.Start == item.Range.StartOffset);
                    var owner = Project(source, node);
                    if (owner.DirectChildren.Count != 0 || owner.Fields.Count != 1 || !Literal(owner.Fields["path"]))
                        throw new InvalidOperationException("A binary root contains additional code; use RAW.");
                }
                else if (item.Range.StartOffset < gateStart.EndOffsetExclusive || item.Range.StartOffset + item.Range.Length > gateEnd.StartOffset
                    || item.Row.Kind == WccCampaignPathKind.CfgInclude && roots.Any(node => item.Range.StartOffset > node.Start && item.Range.StartOffset < node.End))
                    throw new InvalidOperationException("An include is outside its canonical root or units scope.");
            }
            EnsureNoOtherCode(shapeText, scan, roots);
            var layout = new WccCampaignStructureLayout(campaign, campaign.Children.Where(node => node.Tag == "difficulty").ToArray(), pathNodes.ToArray(),
                gateEnd.StartOffset, LineStart(source.Text, units[0].Close), LineStart(source.Text, campaign.Start));
            return new(source, authoring, difficulties, paths, true, "Difficulty symbols and count remain fixed. Paths edit references only.", layout);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return new(source, authoring, difficulties, paths, false, exception.Message, null);
        }
    }

    public static WccCampaignStructureEdit BuildEdit(WccCampaignStructureSnapshot snapshot,
        IEnumerable<WccCampaignDifficultyPresentation> difficulties, IEnumerable<WccCampaignPathReference> paths)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(difficulties); ArgumentNullException.ThrowIfNull(paths);
        var fresh = Read(snapshot.Source, snapshot.Authoring);
        if (!snapshot.CanEdit || !fresh.CanEdit || !fresh.DifficultyRows.SequenceEqual(snapshot.DifficultyRows)
            || !fresh.PathRows.SequenceEqual(snapshot.PathRows)) throw new InvalidOperationException(fresh.Diagnostic);
        var rows = difficulties.ToArray(); var references = paths.ToArray();
        if (rows.Any(row => row is null) || rows.Length != fresh.DifficultyRows.Count
            || !rows.Select(row => row.Define).SequenceEqual(fresh.DifficultyRows.Select(row => row.Define), StringComparer.Ordinal)
            || rows.Count(row => row.IsDefault) != 1)
            throw new InvalidOperationException("Keep the fixed difficulty count, order and symbols, and select one default.");
        rows = rows.Select((row, index) =>
        {
            if (string.IsNullOrWhiteSpace(row.Label)) throw new InvalidOperationException("Every difficulty needs a label.");
            foreach (var issue in new[] { WccCreationSourceWriter.ValidateText(row.Label),
                         WccCreationSourceWriter.ValidateText(row.Description, true), WccCreationSourceWriter.ValidateText(row.Image) })
                if (issue is not null) throw new InvalidOperationException(issue);
            return row.Image == fresh.DifficultyRows[index].Image ? row
                : row with { Image = CampaignPresentationImagePolicy.Fit(row.Image, CampaignPresentationImagePurpose.DifficultyIcon) };
        }).ToArray();
        if (references.Length > 128 || references.Any(row => row is null)) throw new InvalidOperationException("The Paths table exceeds its bounded source scope.");
        foreach (var row in references) ValidatePath(row);
        if (references.GroupBy(row => (row.Kind, row.Path), new PathTupleComparer()).Any(group => group.Count() > 1)
            || references.Where(row => row.Key is not null).GroupBy(row => row.Key).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Each path and existing source reference must appear only once.");
        if (references.Any(row => row.Key is not null && !fresh.PathRows.Any(old => old.Key == row.Key)))
            throw new InvalidOperationException("An existing path reference belongs to another source.");
        var retained = references.Where(row => row.Key is not null && fresh.PathRows.Single(old => old.Key == row.Key).Kind == row.Kind)
            .Select(row => row.Key).ToArray();
        if (!fresh.PathRows.Where(row => retained.Contains(row.Key)).Select(row => row.Key).SequenceEqual(retained))
            throw new InvalidOperationException("Existing path references cannot be borrowed or reordered. Add a new row or change its value.");
        var layout = fresh.Layout!;
        references = references.Select((row, index) => (Row: row, Index: index,
            Offset: row.Key is not null && layout.Paths.Single(item => item.Row.Key == row.Key).Row.Kind == row.Kind
                ? layout.Paths.Single(item => item.Row.Key == row.Key).Range.StartOffset
                : row.Kind switch { WccCampaignPathKind.CfgInclude => layout.GeneralInsertion,
                    WccCampaignPathKind.UnitsInclude => layout.UnitsInsertion, _ => layout.BinaryInsertion }))
            .OrderBy(item => item.Offset).ThenBy(item => item.Index).Select(item => item.Row).ToArray();
        return new(fresh, rows, references);
    }

    internal static IReadOnlyList<WccCampaignStructurePatch> PlanPatches(WccCampaignStructureEdit edit)
    {
        var validated = BuildEdit(edit.Snapshot, edit.DifficultyRows, edit.PathRows);
        var snapshot = validated.Snapshot; var layout = snapshot.Layout!; var source = snapshot.Source;
        var result = new List<WccCampaignStructurePatch>(); var newline = source.Text.Contains("\r\n") ? "\r\n" : "\n";
        for (var index = 0; index < validated.DifficultyRows.Count; index++)
        {
            var before = snapshot.DifficultyRows[index]; var after = validated.DifficultyRows[index];
            if (before == after) continue;
            var node = layout.Difficulties[index]; var owner = Project(source, node);
            var values = new Dictionary<string, string>
            {
                ["label"] = Encode("label", after.Label, owner.GetKnownValue("label")),
                ["description"] = Encode("description", after.Description, owner.GetKnownValue("description")),
                ["image"] = WccCreationSourceWriter.EncodeText(after.Image), ["default"] = after.IsDefault ? "yes" : "no"
            };
            var patches = new List<(int Start, int Length, string Text)>(); var insert = "";
            foreach (var (key, value) in values)
            {
                if (key switch { "label" => before.Label == after.Label, "description" => before.Description == after.Description,
                    "image" => before.Image == after.Image, _ => before.IsDefault == after.IsDefault }) continue;
                if (owner.Fields.TryGetValue(key, out var field))
                {
                    var contribution = field.Contributions.Single();
                    patches.Add((contribution.ValueOutputOffset, contribution.ValueOutputLength, value));
                }
                else if (key != "description" || after.Description.Length != 0) insert += "        " + key + "=" + value + newline;
            }
            if (insert.Length != 0) patches.Add((LineStart(source.Text, node.Close) - node.Start, 0, insert));
            var raw = source.Text[node.Start..node.End];
            foreach (var patch in patches.OrderByDescending(patch => patch.Start)) raw = raw.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.Text);
            result.Add(new(new(source, node.Start, node.End - node.Start), raw, "CampaignDifficulty"));
        }
        foreach (var old in layout.Paths)
        {
            var current = validated.PathRows.SingleOrDefault(row => row.Key == old.Row.Key);
            if (current == old.Row) continue;
            if (current is null || current.Kind != old.Row.Kind) result.Add(new(old.Range, "", "CampaignPath"));
            else
            {
                var replacement = ReferenceText(current, snapshot.Authoring!.AddonId, newline);
                if (current.Kind == WccCampaignPathKind.BinaryRoot)
                {
                    var node = new WccCampaignStructureNode("binary_path", old.Range.StartOffset, null)
                    { End = old.Range.StartOffset + old.Range.Length };
                    var contribution = Project(source, node).Fields["path"].Contributions.Single();
                    replacement = old.Range.Text.Remove(contribution.ValueOutputOffset, contribution.ValueOutputLength)
                        .Insert(contribution.ValueOutputOffset, WccCreationSourceWriter.EncodeText("data/add-ons/" + snapshot.Authoring!.AddonId
                            + (current.Path == "." ? "" : "/" + current.Path)));
                }
                result.Add(new(old.Range, replacement, "CampaignPath"));
            }
        }
        foreach (var row in validated.PathRows.Where(row => row.Key is null || layout.Paths.Single(item => item.Row.Key == row.Key).Row.Kind != row.Kind))
        {
            var start = row.Kind switch { WccCampaignPathKind.CfgInclude => layout.GeneralInsertion,
                WccCampaignPathKind.UnitsInclude => layout.UnitsInsertion, _ => layout.BinaryInsertion };
            var prefix = row.Kind == WccCampaignPathKind.UnitsInclude ? "    " : "";
            result.Add(new(new(source, start, 0), prefix + ReferenceText(row, snapshot.Authoring!.AddonId, newline) + newline, "CampaignPath"));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    internal static WmlStaticScalarOwner Project(WmlStaticSource source, WccCampaignStructureNode node)
    {
        var range = new WmlStaticSourceRange(source, node.Start, node.End - node.Start);
        var shape = WccCampaignDependencyAuthoringPlanner.MaskManagedDependencyBlocksForStructure(source)
            .Substring(node.Start, node.End - node.Start);
        var evaluation = new WmlStaticEvaluationResult(shape, [new(0, shape, range, [])], [], [source], [], true);
        var projection = WmlStaticScalarProjection.Create(evaluation);
        var owners = projection.Owners.Where(owner => owner.Parent is null && owner.TagName == node.Tag).ToArray();
        if (projection.Diagnostics.Count != 0 || owners.Length != 1 || owners[0].IsAmended || owners[0].ClosingOrigin is null)
            throw new InvalidOperationException("A literal block has no complete unique source structure.");
        return owners[0];
    }

    private static string Encode(string key, string value, string? old) => WccScenarioFieldValueCodec.EncodeLiteral(new(
        key, key, "Difficulty", ScenarioInitialFieldKind.TranslatableText,
        new(ScenarioInitialFieldDefaultKind.None, null, ""), []), value, old);
    internal static string Decode(WmlStaticScalarOwner owner, string key)
    {
        if (!owner.Fields.TryGetValue(key, out var field)) return "";
        var humanText = WccWmlPlainTextCodec.IsTextField(owner.TagName, key, owner.Parent?.TagName);
        if (!Literal(field, humanText)) throw new InvalidOperationException("A campaign structure value is not one exact literal assignment.");
        return humanText && WccWmlPlainTextCodec.TryReadLiteral(field.ValueText, out var text, out _)
            ? text : WccScenarioFieldValueCodec.DecodeLiteral(field.ValueText);
    }
    internal static bool Literal(WmlStaticScalarField field, bool humanText = false) => field.IsKnown && field.ValueText is not null && field.Contributions.Count == 1
        && field.Contributions[0].ExpansionStack.Count == 0 && field.Contributions[0].ValueMappingIsExact
        && (field.Contributions[0].CompositeAssignmentKeys.Count == 0
            || field.Contributions[0].CompositeAssignmentKeys.SequenceEqual(new[] { field.Key }, StringComparer.Ordinal))
        && (humanText ? WccWmlPlainTextCodec.TryReadLiteral(field.ValueText, out _, out _)
            : WccScenarioFieldValueCodec.IsSupportedLiteralWml(field.ValueText));
    private static string Key(WccCampaignPathKind kind, int start, int length) => $"{kind}:{start}:{length}";
    private static string Relative(string path, string prefix, string? addon)
    {
        if (addon is null) return path;
        var root = prefix + addon;
        if (path == root) return ".";
        if (!path.StartsWith(root + "/", StringComparison.Ordinal)) throw new InvalidOperationException("A path is not a literal reference into the current add-on.");
        return path[(root.Length + 1)..];
    }
    private static string ReferenceText(WccCampaignPathReference row, string addon, string newline) => row.Kind == WccCampaignPathKind.BinaryRoot
        ? "[binary_path]" + newline + "    path=" + WccCreationSourceWriter.EncodeText("data/add-ons/" + addon + (row.Path == "." ? "" : "/" + row.Path)) + newline + "[/binary_path]"
        : "{~add-ons/" + addon + "/" + row.Path + "}";
    private static void ValidatePath(WccCampaignPathReference row)
    {
        if (!Enum.IsDefined(row.Kind) || string.IsNullOrWhiteSpace(row.Path) || row.Path.Length > 256
            || row.Path == "." && row.Kind != WccCampaignPathKind.BinaryRoot
            || row.Path != "." && row.Path.Split('/').Any(part => part.Length == 0 || part is "." or ".."
                || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')))
            || row.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
                && (row.Kind != WccCampaignPathKind.CfgInclude || !WccCampaignLocalPresetStoragePlanner.IsPresetStorageRelativePath(row.Path)))
            throw new InvalidOperationException("Use an add-on relative directory or an exact managed preset-file include, with '/' separators and no parent traversal or expressions. Binary root may use '.'.");
    }
    private static bool Symbol(string? value) => !string.IsNullOrEmpty(value) && (value[0] == '_' || char.IsAsciiLetter(value[0]))
        && value.Skip(1).All(character => character == '_' || char.IsAsciiLetterOrDigit(character));
    private static int LineStart(string text, int offset) { while (offset > 0 && text[offset - 1] is not ('\r' or '\n')) offset--; return offset; }
    private static IReadOnlyList<WccCampaignStructureNode> Nodes(string masked)
    {
        var roots = new List<WccCampaignStructureNode>(); var stack = new Stack<WccCampaignStructureNode>();
        foreach (Match match in Regex.Matches(masked, @"\[(/?)([a-z_]+)\]", RegexOptions.CultureInvariant))
        {
            var tag = match.Groups[2].Value;
            if (match.Groups[1].Length == 0)
            {
                var node = new WccCampaignStructureNode(tag, match.Index, stack.Count == 0 ? null : stack.Peek());
                if (node.Parent is null) roots.Add(node); else node.Parent.Children.Add(node); stack.Push(node);
            }
            else
            {
                if (stack.Count == 0 || stack.Peek().Tag != tag) throw new InvalidOperationException("The literal tag hierarchy is not canonical.");
                var node = stack.Pop(); node.Close = match.Index; node.End = match.Index + match.Length;
            }
        }
        if (stack.Count != 0) throw new InvalidOperationException("A literal tag has no closing pair.");
        return roots;
    }
    private static void EnsureNoOtherCode(string text, WmlAuthoredStructure scan, IReadOnlyList<WccCampaignStructureNode> roots)
    {
        var remainder = scan.MaskedText.ToCharArray();
        foreach (var node in roots.Where(node => node.Tag != "units")) Array.Fill(remainder, ' ', node.Start, node.End - node.Start);
        foreach (var node in roots.Where(node => node.Tag == "units"))
        {
            Array.Fill(remainder, ' ', node.Start, "[units]".Length); Array.Fill(remainder, ' ', node.Close, "[/units]".Length);
        }
        foreach (var macro in scan.Macros) Array.Fill(remainder, ' ', macro.StartOffset, macro.EndOffsetExclusive - macro.StartOffset);
        foreach (var directive in scan.Directives)
        {
            var line = scan.Lines[directive.Line]; Array.Fill(remainder, ' ', line.StartOffset, line.ContentEndOffset - line.StartOffset);
        }
        if (remainder.Any(character => !char.IsWhiteSpace(character) && character != '\uFEFF'))
            throw new InvalidOperationException("Additional authored main-file code requires RAW topology editing.");
    }
    private sealed class PathTupleComparer : IEqualityComparer<(WccCampaignPathKind Kind, string Path)>
    {
        public bool Equals((WccCampaignPathKind Kind, string Path) x, (WccCampaignPathKind Kind, string Path) y) => x.Kind == y.Kind && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);
        public int GetHashCode((WccCampaignPathKind Kind, string Path) value) => HashCode.Combine(value.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }
}
