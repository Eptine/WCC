using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public static partial class WccUnitTypeMaterializationPlanner
{
    internal static bool TryMapHelperResourceOrigin(WccContentHelperReceipt helper, WccMaterializationCandidate rendered,
        WmlStaticSourceRange generated, out WmlStaticSourceRange original)
    {
        var range = rendered.Helpers.Single(item => item.Name == helper.Name).Range;
        if (SameSource(generated.Source, range.Source) && generated.StartOffset >= range.StartOffset
            && generated.StartOffset + generated.Length <= range.StartOffset + range.Length)
        {
            var offset = LocateValue(range, generated, helper.Definition.Text);
            original = new(helper.Source, helper.Definition.StartOffset + offset, generated.Length);
            return true;
        }
        original = null!; return false;
    }

    internal static bool IsDirectHelperResourceAssignment(WccContentHelperReceipt helper, WmlStaticSourceRange origin, string field)
        => HasDirectAssignment(helper.Definition, origin, field)
            && WccScenarioFieldValueCodec.IsSupportedLiteralWml(origin.Text);

    internal static bool SamePiece(WmlStaticSourceRange left, WmlStaticSourceRange right)
        => SameSource(left.Source, right.Source) && left.StartOffset == right.StartOffset && left.Length == right.Length;

    internal static string GetHelperResourceRelativePath(WccContentHelperReceipt helper,
        IReadOnlyList<WccContentResourceTransferReceipt> resources, WccContentResourceTransferReceipt transfer)
    {
        if (!SamePiece(helper.Definition, transfer.Piece))
            throw new InvalidOperationException("A helper resource has no exact authored definition owner.");
        var groups = resources.Where(item => item.Folder == transfer.Folder && SamePiece(item.Piece, helper.Definition))
            .Select(item => (Hash: item.SourceRevision.RawByteSha256, Extension: Path.GetExtension(item.SourceRevision.SourcePath).ToLowerInvariant()))
            .Distinct().OrderBy(item => item.Hash, StringComparer.Ordinal).ThenBy(item => item.Extension, StringComparer.Ordinal).ToArray();
        var extension = Path.GetExtension(transfer.SourceRevision.SourcePath).ToLowerInvariant();
        var ordinal = Array.IndexOf(groups, (transfer.SourceRevision.RawByteSha256, extension));
        if (ordinal < 0) throw new InvalidOperationException("The helper resource is outside its immutable resource recipe.");
        var owner = helper.Name.Replace(':', '_');
        return transfer.Folder + "/" + (transfer.Folder == "sounds" ? "units" : "abilities") + "/" + owner + "/" + owner
            + (ordinal > 0 ? "_" + (ordinal + 1) : "") + extension;
    }

    internal static WccMaterializationCandidate RenderSharedHelper(WccContentHelperReceipt helper, string sourceRoot,
        string path, string domain, IReadOnlyList<WccContentResourceTransferReceipt> resources,
        Func<WccContentResourceTransferReceipt, string> resourcePath, IReadOnlyList<string>? originalProvenance = null)
    {
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is { } problem) throw new ArgumentException(problem);
        var raw = WccUnitTypeAuthoredCopyPlanner.MapLibraryTextDomains(helper.Definition.Text, domain);
        var patches = new List<(int Start, int Length, string Value, WccContentResourceTransferReceipt Receipt)>();
        foreach (var resource in resources.Where(item => SamePiece(item.Piece, helper.Definition)))
        {
            if (!SameSource(resource.Origin.Source, helper.Source) || resource.Origin.StartOffset < helper.Definition.StartOffset
                || resource.Origin.StartOffset + resource.Origin.Length > helper.Definition.StartOffset + helper.Definition.Length
                || !HasDirectAssignment(helper.Definition, resource.Origin, resource.Field))
                throw new InvalidOperationException("A helper resource rewrite is outside its exact authored assignment.");
            var filename = resourcePath(resource)[(resource.Folder.Length + 1)..];
            var value = resource.Origin.Text.TrimStart().StartsWith('"')
                ? WccCreationSourceWriter.EncodeText(filename + resource.ModifierSuffix) : filename + resource.ModifierSuffix;
            patches.Add((LocateValue(helper.Definition, resource.Origin, raw), resource.Origin.Length, value, resource));
        }
        foreach (var patch in patches.OrderByDescending(item => item.Start)) raw = raw.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.Value);
        var text = new StringBuilder().Append("#ifdef ").Append(helper.Name).Append('\n');
        var guardStart = text.Length;
        text.Append("#error WCC copied helper ").Append(helper.Name).Append(" conflicts with an active definition\n");
        var guardLength = text.Length - guardStart;
        text.Append("#endif\n#textdomain ").Append(helper.TextDomain is null or LibraryDomain ? domain : helper.TextDomain).Append('\n');
        var definitionStart = text.Length;
        text.Append(raw); if (!raw.EndsWith('\n')) text.Append('\n');
        var definitionLength = text.Length - definitionStart;
        var provenance = new List<string> { "# WCC helper source: " + SafeComment(Path.GetRelativePath(sourceRoot, helper.Source.Path).Replace('\\', '/')),
            "# WCC helper SHA256: " + helper.ContentFingerprint };
        provenance.AddRange(originalProvenance ?? []);
        foreach (var resource in resources.Where(item => SamePiece(item.Piece, helper.Definition))
            .DistinctBy(item => (item.CapturedMember.RelativePath, item.SourceRevision.RawByteSha256, Destination: resourcePath(item))))
            provenance.Add("# WCC resource source: " + SafeComment(resource.CapturedMember.RelativePath) + " -> " + SafeComment(resourcePath(resource))
                + "; byte SHA256: " + resource.SourceRevision.RawByteSha256);
        var source = new WmlStaticSource(path, WccCopiedContentProvenance.Append(text.ToString(), provenance));
        var bindings = patches.Select(patch => new WccMaterializationResourceBinding(patch.Receipt.Field,
            resourcePath(patch.Receipt)[(patch.Receipt.Folder.Length + 1)..], new(source,
                definitionStart + patch.Start + patches.Where(other => other.Start < patch.Start).Sum(other => other.Value.Length - other.Length), patch.Value.Length))).ToArray();
        return new(source, [(helper.Name, new(source, definitionStart, definitionLength))], Array.AsReadOnly(bindings),
            [(helper.Name, new(source, guardStart, guardLength))]);
    }
}
