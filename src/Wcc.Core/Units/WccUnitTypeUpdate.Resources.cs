using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.Core.Units;

public static partial class WccUnitTypeUpdatePlanner
{
    private static IReadOnlyList<WccContentResourceTransferReceipt> CaptureHelperResources(WmlStaticSource candidate,
        IReadOnlyList<(WccContentHelperReceipt Receipt, string Root, WccPackageRevisionId Revision)> helpers,
        IReadOnlyList<(WccContentRegistryReceipt Receipt, string Root, WccPackageRevisionId Revision)> registries,
        WccStaticSourceCaptureResult managed, WccStaticSourceCaptureResult project, WmlEvaluationContext context,
        string root, string domain, CancellationToken cancellationToken)
    {
        if (helpers.Count == 0) return [];
        if (project.IncludeResolver is not IWmlStaticResourceResolver resolver)
            throw new InvalidOperationException("Helper resources need the actual captured project and Core resolver.");
        var rendered = helpers.Select(row => (row.Receipt, row.Root, row.Revision,
            Candidate: WccUnitTypeMaterializationPlanner.RenderUpdateHelperCandidate(row.Receipt, row.Root, root, domain, []))).ToArray();
        var previewDefinitions = rendered.Select(row => row.Candidate.Source)
            .Concat(WccUnitTypeMaterializationPlanner.GetRegistrySources(registries.Select(row => row.Receipt), root, domain)).ToArray();
        var replacements = new[] { candidate }.Concat(previewDefinitions.Where(source => project.Sources.Any(old => SamePath(source.Path, old.Path)))).ToArray();
        var additions = previewDefinitions.Where(source => !project.Sources.Any(old => SamePath(source.Path, old.Path))).ToArray();
        var preview = WccStaticSourceCapture.CreateCandidateCapture(project, CapturedProjectRevision(project, root), replacements, additions);
        var evaluated = EvaluateProject(preview, context, null, cancellationToken);
        var scalar = WmlStaticScalarProjection.Create(evaluated, cancellationToken);
        if (!evaluated.IsComplete || scalar.Diagnostics.Count != 0)
            throw new InvalidOperationException("The selected helper resource discovery needs complete campaign WML (" + context.DisplayName + "): "
                + (evaluated.Diagnostics.FirstOrDefault(issue => issue.IsBlocking)?.Message ?? scalar.Diagnostics.FirstOrDefault()?.Message));
        var projection = WmlCheckResourceProjection.Create(evaluated, scalar, _ => true, cancellationToken);
        var composites = new WmlCheckResourceCompositeAssignments(evaluated, cancellationToken);
        var transfers = new List<WccContentResourceTransferReceipt>();
        foreach (var resource in projection.Owners)
            foreach (var key in resource.OriginalOwner.Fields.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kind = WmlResourceFieldCatalog.Find(resource.OriginalOwner, key);
                if (kind is null) continue;
                var field = resource.ValueOwner.Fields.GetValueOrDefault(key) ?? resource.OriginalOwner.Fields[key];
                if (field.ValueText is null) field = composites.Observe(resource.OriginalOwner, key, cancellationToken) ?? field;
                var contribution = field.Contributions.LastOrDefault();
                var owners = rendered.Where(row => HelperResourceOwner(resource.OriginalOwner, contribution, row.Candidate, row.Receipt.Name)).ToArray();
                if (owners.Length == 0) continue;
                if (kind == WmlResourceFieldKind.Lua)
                    throw ResourceRefusal(owners[0].Receipt.Name, key, "Lua closure needs a separate authored script contract.");
                if (contribution is not { ValueMappingIsExact: true } || contribution.ValueOrigins.Count == 0
                    || !WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var value, out _))
                    throw ResourceRefusal(owners[0].Receipt.Name, key, "The resource has an opaque or unproved captured value.");
                if (value.Length == 0 || value is "none" or "unit_image" && key is "profile" or "small_profile" or "portrait") continue;
                var folder = kind == WmlResourceFieldKind.Sound ? "sounds" : kind == WmlResourceFieldKind.Music ? "music" : kind == WmlResourceFieldKind.Map ? "maps" : "images";
                var syntax = kind == WmlResourceFieldKind.Sound ? WmlResourceReferenceSyntax.ParseSound(value, cancellationToken: cancellationToken)
                    : kind is WmlResourceFieldKind.Image or WmlResourceFieldKind.AnimationImage
                        ? WmlResourceReferenceSyntax.ParseImage(value, kind == WmlResourceFieldKind.AnimationImage, cancellationToken: cancellationToken)
                        : WmlResourceReferenceSyntax.ParseBinaryFilename(value, cancellationToken);
                if (!syntax.IsComplete) throw ResourceRefusal(owners[0].Receipt.Name, value, "The resource syntax is not a supported literal.");
                var aliases = ResourceAliases(folder, syntax.Filenames).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var owner in owners)
                    if (aliases.Any(alias => WccStaticSourceCapture.CapturedHelperResourcePresence(managed, owner.Revision, folder + "/" + alias) == WmlStaticTruth.Unknown))
                        throw ResourceRefusal(owner.Receipt.Name, value, "The source namespace has ambiguous or unproved spelling.");
                var local = owners.SelectMany(owner => aliases.Select(alias => WccStaticSourceCapture.FindCapturedResourceMember(managed, owner.Revision, folder + "/" + alias)))
                    .OfType<WccCapturedResourceMember>().Where(member => !member.IsCore).ToArray();
                var core = syntax.Filenames.Count != 0 && syntax.Filenames.All(filename =>
                    WmlResourceLookupPaths.Create(filename, folder, ["core"], folder == "images", cancellationToken, resolver.InstallationResourceFolders)
                        .Candidates.Where(path => path.StartsWith("core/", StringComparison.Ordinal)
                            || path.StartsWith(WmlResourceLookupPaths.InstallationResourceMountPrefix, StringComparison.Ordinal))
                        .Any(path => resolver.ResolveResource(path, candidate) is { Exists: WmlStaticTruth.True, MembershipIdentity.Length: > 0 }));
                if (core)
                {
                    if (local.Length != 0) throw ResourceRefusal(owners[0].Receipt.Name, value, "The filename exists in both the Library and Core; its intended bytes are ambiguous.");
                    continue;
                }
                if (kind is not (WmlResourceFieldKind.Image or WmlResourceFieldKind.AnimationImage or WmlResourceFieldKind.Sound))
                    throw ResourceRefusal(owners[0].Receipt.Name, value, "This resource kind requires a separate transfer contract.");
                var image = kind == WmlResourceFieldKind.Sound ? null : WccImageExpressionParser.TryParse(value);
                var filename = image?.BaseReference ?? value;
                if (syntax.Filenames.Count != 1 || syntax.Filenames[0] != filename || image?.MayContainNestedImageReferences == true
                    || !WmlResourceReferenceSyntax.ParseBinaryFilename(filename, cancellationToken).IsComplete
                    || contribution.CompositeAssignmentKeys.Count != 0 || contribution.ValueOrigins.Count != 1)
                    throw ResourceRefusal(owners[0].Receipt.Name, value, "Private resources need one direct literal image or sound; lists, generated values and nested assets remain unsupported.");
                var mappings = owners.Select(owner =>
                {
                    var mapped = WccUnitTypeMaterializationPlanner.TryMapHelperResourceOrigin(owner.Receipt, owner.Candidate,
                        contribution.ValueOrigins[0], out var origin);
                    return (Owner: owner, Origin: mapped ? origin : null);
                }).Where(item => item.Origin is not null).ToArray();
                if (mappings.Length != 1 || mappings[0].Origin!.Text.Trim() != field.ValueText?.Trim()
                    || !WccUnitTypeMaterializationPlanner.IsDirectHelperResourceAssignment(mappings[0].Owner.Receipt, mappings[0].Origin!, key))
                    throw ResourceRefusal(owners[0].Receipt.Name, value, "Private bytes have no single direct authored helper assignment; Core or caller origins cannot substitute for it.");
                var selected = mappings[0];
                var member = WccStaticSourceCapture.FindCapturedResourceMember(managed, selected.Owner.Revision, folder + "/" + filename);
                if (member is null || member.IsCore || !SamePath(member.RootPath, selected.Owner.Root))
                    throw ResourceRefusal(selected.Owner.Receipt.Name, filename, "The resource is not one exact captured Library member. Use its exact owned filename.");
                var extension = Path.GetExtension(member.PhysicalPath).ToLowerInvariant();
                if (extension.Length < 2 || extension.Length > 12 || !extension[1..].All(char.IsAsciiLetterOrDigit))
                    throw ResourceRefusal(selected.Owner.Receipt.Name, filename, "The resource needs a portable literal extension.");
                var captured = WccTextDiskRevisionCapture.CaptureBinary(selected.Owner.Root, member.PhysicalPath);
                if (!captured.IsCaptured || captured.Revision is null)
                    throw ResourceRefusal(selected.Owner.Receipt.Name, filename, "The original bytes cannot be captured: " + captured.ErrorMessage);
                if (!transfers.Any(item => SamePiece(item.Origin, selected.Origin!) && item.Field == key))
                    transfers.Add(new(folder, filename, key, selected.Origin!, image?.ModifierSuffix ?? "", member, captured.Revision, selected.Owner.Receipt.Definition));
            }
        if (transfers.GroupBy(item => item.SourceRevision.SourcePath, StringComparer.OrdinalIgnoreCase).Count() > 63
            || transfers.GroupBy(item => item.SourceRevision.SourcePath, StringComparer.OrdinalIgnoreCase).Sum(group => group.First().SourceRevision.RawByteLength) > 64L * 1024 * 1024)
            throw new InvalidOperationException("The unit helper assets exceed the checked 63-resource or 64-MiB limit.");
        return Array.AsReadOnly(transfers.ToArray());
    }

    private static bool HelperResourceOwner(WmlStaticScalarOwner owner, WmlStaticScalarContribution? contribution,
        WccMaterializationCandidate candidate, string name)
    {
        var definition = candidate.Helpers.Single(item => item.Name == name).Range;
        bool Frame(WmlStaticExpansionFrame frame) => frame.MacroName == name && InRange(frame.Definition, definition);
        for (var current = owner; current is not null; current = current.Parent)
            if (current.OpeningExpansionStack.Any(Frame) || current.OpeningOrigin is { } origin && InRange(origin, definition)) return true;
        return contribution is not null && (contribution.ExpansionStack.Any(Frame) || contribution.ValueExpansionStack.Any(Frame)
            || contribution.AssignmentOrigins.Concat(contribution.ValueOrigins).Any(origin => InRange(origin, definition)));
    }

    private static IEnumerable<string> ResourceAliases(string folder, IReadOnlyList<string> filenames)
        => filenames.SelectMany(filename => folder == "images" && (filename.EndsWith(".png", StringComparison.Ordinal) || filename.EndsWith(".jpg", StringComparison.Ordinal))
            ? new[] { filename, filename[..^4] + ".webp" } : [filename]);

    private static WccPackageRevisionId CapturedProjectRevision(WccStaticSourceCaptureResult capture, string root)
    {
        var matches = WccStaticSourceCapture.GetCapturedHelperSourceRoots(capture).Where(item => !item.IsCore && SamePath(item.RootPath, root)).ToArray();
        return matches.Length == 1 ? matches[0].RevisionId : throw new InvalidOperationException("The unit update needs one exact captured campaign namespace.");
    }
    private static bool SamePiece(WmlStaticSourceRange left, WmlStaticSourceRange right)
        => SameSource(left.Source, right.Source) && left.StartOffset == right.StartOffset && left.Length == right.Length;
    private static InvalidOperationException ResourceRefusal(string helper, string resource, string reason)
        => new("Helper {" + helper + "} resource '" + resource + "' cannot be copied: " + reason);

    public static string GetResourceRelativePath(WccUnitTypeUpdateEdit edit, WccContentResourceTransferReceipt transfer)
    {
        var helper = edit.Helpers.Single(item => SamePiece(item.Definition, transfer.Piece));
        return WccUnitTypeMaterializationPlanner.GetHelperResourceRelativePath(helper, edit.ResourceTransfers, transfer);
    }
    public static WccContentBinaryDestinationPlan PlanBinaryDestinations(WccUnitTypeUpdateEdit edit)
        => WccUnitTypeMaterializationPlanner.PlanBinaryDestinationsCore(edit.ProjectRootPath, edit.ResourceTransfers.Select(transfer =>
        {
            var helper = edit.Helpers.Single(item => SamePiece(item.Definition, transfer.Piece));
            return (Helper: (string?)helper.Name, Owner: "helper {" + helper.Name + "}", RelativePath: GetResourceRelativePath(edit, transfer), Source: transfer.SourceRevision);
        }));
}
