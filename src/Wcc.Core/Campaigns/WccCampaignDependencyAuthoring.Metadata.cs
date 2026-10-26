using System.Text.RegularExpressions;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public static partial class WccCampaignDependencyAuthoringPlanner
{
    private sealed record MetadataField(WmlStaticSource? Source, string RelativePath, int Start, int Length,
        string? Assignment, string Prefix, string Suffix, IReadOnlyList<string> Ids);
    private sealed record MetadataPlan(WccCampaignDependencyMetadataReceipt? Receipt,
        WccCampaignPackageSourceReplacement? Replacement, WmlStaticSource? Creation);

    private static MetadataPlan PlanMetadata(WccCampaignDependencySnapshot snapshot, IReadOnlyList<WccCampaignDependencySpec> desired)
    {
        var root = Path.GetDirectoryName(snapshot.MainSource.Path)!;
        var existingReceipt = snapshot.MetadataReceipt;
        if (existingReceipt is null && !desired.Any(spec => spec.DeclareDependency)) return new(null, null, null);
        var field = ReadMetadata(root, snapshot.Sources, existingReceipt?.RelativePath);
        var receipt = existingReceipt ?? new(field.RelativePath, field.Assignment, field.Prefix, field.Suffix,
            Array.AsReadOnly(field.Ids.ToArray()));
        ValidateReceipt(receipt);
        if (existingReceipt is not null) ValidateCurrentMetadata(snapshot, receipt);
        var assignment = MetadataAssignment(receipt, desired);
        WccCampaignPackageSourceReplacement? replacement = null;
        WmlStaticSource? creation = null;
        if (field.Source is null)
        {
            if (assignment is not null)
                creation = new(Path.Combine(root, receipt.RelativePath), assignment + "type=\"campaign\"\n");
        }
        else
        {
            var candidate = field.Source.Text.Remove(field.Start, field.Length).Insert(field.Start, assignment ?? "");
            if (candidate != field.Source.Text) replacement = new(field.Source, candidate);
        }
        return new(desired.Any(spec => spec.DeclareDependency) ? receipt : null, replacement, creation);
    }

    private static void ValidateCurrentMetadata(WccCampaignDependencySnapshot snapshot, WccCampaignDependencyMetadataReceipt receipt)
    {
        ValidateReceipt(receipt);
        var field = ReadMetadata(Path.GetDirectoryName(snapshot.MainSource.Path)!, snapshot.Sources, receipt.RelativePath);
        if (field.Source is null || field.Assignment != MetadataAssignment(receipt, snapshot.Entries.Select(entry => entry.Spec).ToArray()))
            throw new InvalidOperationException("The managed dependency declaration was removed or edited outside WCC. Review the metadata in RAW before changing dependencies.");
    }

    private static string? MetadataAssignment(WccCampaignDependencyMetadataReceipt receipt, IReadOnlyList<WccCampaignDependencySpec> specs)
    {
        var ids = receipt.OriginalIds.Concat(specs.Where(spec => spec.DeclareDependency).Select(spec => spec.AddonId))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.SequenceEqual(receipt.OriginalIds, StringComparer.Ordinal)) return receipt.OriginalAssignment;
        return receipt.Prefix + WccCreationSourceWriter.EncodeText(string.Join(",", ids)) + receipt.Suffix;
    }

    private static void ValidateReceipt(WccCampaignDependencyMetadataReceipt receipt)
    {
        if (receipt.RelativePath is not ("_info.cfg" or "_server.pbl") || receipt.OriginalIds is null || receipt.OriginalIds.Count > 512 ||
            receipt.OriginalIds.Any(id => WccCampaignCreationTokenPolicy.ValidateAddonId(id) is not null) ||
            receipt.OriginalIds.Distinct(StringComparer.Ordinal).Count() != receipt.OriginalIds.Count ||
            receipt.Prefix is null || receipt.Suffix is null || receipt.Prefix.Length > 1024 || receipt.Suffix.Length > 8192 || (receipt.OriginalAssignment?.Length ?? 0) > 64 * 1024 ||
            !Regex.IsMatch(receipt.Prefix, @"^\s*dependencies\s*=\s*$", RegexOptions.CultureInvariant) ||
            receipt.Prefix.IndexOfAny(['\r', '\n']) >= 0 || !Regex.IsMatch(receipt.Suffix, @"^[ \t]*(?:#[^\r\n]*)?(?:\r\n|\n|\r)?$", RegexOptions.CultureInvariant) ||
            (receipt.OriginalAssignment is null && receipt.OriginalIds.Count != 0))
            throw new InvalidOperationException("A managed dependency metadata receipt is malformed or unsupported.");
        if (receipt.OriginalAssignment is { } original)
        {
            var parsed = ParseDependencyLine(original);
            if (parsed is null || !parsed.Value.Ids.SequenceEqual(receipt.OriginalIds, StringComparer.Ordinal) ||
                parsed.Value.Prefix != receipt.Prefix || parsed.Value.Suffix != receipt.Suffix)
                throw new InvalidOperationException("The preserved dependency declaration no longer matches its exact receipt.");
        }
    }

    private static MetadataField ReadMetadata(string root, IReadOnlyList<WmlStaticSource> sources, string? requested)
    {
        var infoPath = Path.Combine(root, "_info.cfg");
        var pblPath = Path.Combine(root, "_server.pbl");
        var info = sources.SingleOrDefault(source => PathComparer.Equals(source.Path, infoPath));
        var pbl = sources.SingleOrDefault(source => PathComparer.Equals(source.Path, pblPath));
        var relative = info is not null ? "_info.cfg" : "_server.pbl";
        if (requested is not null && requested != relative)
            throw new InvalidOperationException("The dependency metadata owner changed. Review _info.cfg / _server.pbl in RAW.");
        var source = info ?? pbl;
        if (source is null) return new(null, relative, 0, 0, null, "dependencies=", "\n", []);
        if (source.Text.Length > 512 * 1024) throw new InvalidOperationException("Dependency metadata exceeds its bounded editing scope.");
        var scan = WmlAuthoredStructureScanner.Scan(source.Text);
        if (scan.Problem is not null || scan.Directives.Count != 0 || scan.Macros.Count != 0 ||
            !WmlSingleBlockValidator.ValidateSourceStructure(source.Text).IsValid)
            throw new InvalidOperationException("Conditional or unresolved dependency metadata requires RAW editing.");
        var blocks = WmlBlockScanner.FindTopLevelBlockSpans(scan.MaskedText);
        if (blocks.Count > 1 || blocks.Count == 1 && blocks[0].TagName != "info" || relative == "_info.cfg" && blocks.Count != 1 ||
            WmlEditorAnalysis.Build(scan.MaskedText).TagOccurrences.Count != blocks.Count * 2)
            throw new InvalidOperationException("Dependency metadata needs one isolated [info] block or a plain _server.pbl document.");
        var start = 0; var end = source.Text.Length; var insertion = source.Text.StartsWith('\uFEFF') ? 1 : 0;
        var prefix = "dependencies=";
        if (blocks.Count == 1)
        {
            var block = blocks[0];
            var openingLine = scan.Lines.Single(line => line.StartOffset <= block.StartIndex && line.ContentEndOffset > block.StartIndex);
            var closingLine = scan.Lines.Single(line => line.StartOffset < block.EndIndexExclusive && line.EndOffsetExclusive >= block.EndIndexExclusive);
            if (source.Text[openingLine.StartOffset..openingLine.ContentEndOffset].Trim() != "[info]" ||
                source.Text[closingLine.StartOffset..closingLine.ContentEndOffset].Trim() != "[/info]")
                throw new InvalidOperationException("Compact or commented metadata tag headers require RAW editing.");
            start = openingLine.EndOffsetExclusive; end = closingLine.StartOffset; insertion = start; prefix = "    dependencies=";
        }
        var matches = new List<MetadataField>();
        foreach (var line in scan.Lines.Where(line => line.StartOffset >= start && line.StartOffset < end))
        {
            var offset = line.StartOffset == 0 && source.Text.StartsWith('\uFEFF') ? 1 : line.StartOffset;
            var raw = source.Text[offset..line.EndOffsetExclusive];
            if (ParseDependencyLine(raw) is not { } parsed) continue;
            matches.Add(new(source, relative, offset, line.EndOffsetExclusive - offset, raw,
                parsed.Prefix, parsed.Suffix, parsed.Ids));
        }
        if (matches.Count > 1) throw new InvalidOperationException("Repeated dependency declarations require RAW editing.");
        return matches.SingleOrDefault() ?? new(source, relative, insertion, 0, null, prefix, Newline(source), []);
    }

    private static (string Prefix, string Suffix, IReadOnlyList<string> Ids)? ParseDependencyLine(string raw)
    {
        var content = raw.TrimEnd('\r', '\n');
        var newline = raw[content.Length..];
        var match = Regex.Match(content, @"^(\s*dependencies\s*=\s*)(.*)$", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var value = WmlInlineCommentSplitter.Split(match.Groups[2].Value);
        if (!WccScenarioFieldValueCodec.IsSupportedLiteralWml(value.Value))
            throw new InvalidOperationException("Use one literal comma-separated dependency declaration, or edit this metadata in RAW.");
        var decoded = WccScenarioFieldValueCodec.DecodeLiteral(value.Value);
        var ids = decoded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        if (ids.Length > 512 || ids.Any(id => WccCampaignCreationTokenPolicy.ValidateAddonId(id) is not null) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidOperationException("Dependency declarations need a bounded unique list of literal add-on folder IDs.");
        var suffix = value.InlineCommentSuffix;
        if (suffix.Length == 0)
        {
            var rawValue = match.Groups[2].Value;
            suffix = rawValue[rawValue.TrimEnd().Length..];
        }
        return (match.Groups[1].Value, suffix + newline, Array.AsReadOnly(ids));
    }
}