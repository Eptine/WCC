using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>One immutable existing-unit draft and its captured reusable definitions. Never write authority.</summary>
public sealed class WccUnitTypeUpdateEdit
{
    internal WccUnitTypeUpdateEdit(WmlStaticSource original, WmlStaticSource candidate, WmlStaticSourceRange originalRange,
        WmlStaticSourceRange candidateRange, string id, string root, string domain, WmlEvaluationContext context, WccStaticSourceCaptureResult managed,
        WccStaticSourceCaptureResult project, IEnumerable<(WccContentHelperReceipt Receipt, string Root, WccPackageRevisionId Revision)> helpers,
        IEnumerable<(WccContentRegistryReceipt Receipt, string Root, WccPackageRevisionId Revision)> registries,
        IEnumerable<WmlStaticSource> dependencies, IEnumerable<WmlStaticSource> creates,
        IEnumerable<WccContentResourceTransferReceipt> resources)
    {
        OriginalSource = original; CandidateSource = candidate; OriginalRange = originalRange; CandidateRange = candidateRange;
        Id = id; ProjectRootPath = root; TextDomain = domain; PlanningContext = context; ManagedCapture = managed; ProjectCapture = project;
        HelperBindings = Array.AsReadOnly(helpers.ToArray()); RegistryBindings = Array.AsReadOnly(registries.ToArray());
        Helpers = Array.AsReadOnly(HelperBindings.Select(row => row.Receipt).ToArray());
        Registries = Array.AsReadOnly(RegistryBindings.Select(row => row.Receipt).ToArray());
        DependencySources = Array.AsReadOnly(dependencies.ToArray()); SourceCreates = Array.AsReadOnly(creates.ToArray());
        ResourceTransfers = Array.AsReadOnly(resources.ToArray());
        var identity = new StringBuilder().Append(original.Path).Append('|').Append(original.ContentHash)
            .Append('|').Append(candidate.ContentHash).Append('|').Append(domain);
        foreach (var row in HelperBindings) identity.Append("\nH|").Append(row.Revision).Append('|').Append(row.Root)
            .Append('|').Append(row.Receipt.Source.Path).Append('|').Append(row.Receipt.Source.ContentHash).Append('|').Append(row.Receipt.ContentFingerprint);
        foreach (var row in RegistryBindings) identity.Append("\nG|").Append(row.Revision).Append('|').Append(row.Root)
            .Append('|').Append(row.Receipt.Source.Path).Append('|').Append(row.Receipt.Source.ContentHash).Append('|').Append(row.Receipt.ContentFingerprint);
        foreach (var dependency in DependencySources) identity.Append("\nD|").Append(dependency.Path).Append('|').Append(dependency.ContentHash);
        foreach (var resource in ResourceTransfers) identity.Append("\nR|").Append(resource.SourceRevision.ProjectRootPath)
            .Append('|').Append(resource.SourceRevision.SourcePath).Append('|').Append(resource.SourceRevision.SourceIdentity)
            .Append('|').Append(resource.SourceRevision.RouteIdentitySha256).Append('|').Append(resource.SourceRevision.ProjectRootIdentity)
            .Append('|').Append(resource.SourceRevision.SourceParentIdentity).Append('|').Append(resource.SourceRevision.SourceHardLinkCount)
            .Append('|').Append(resource.SourceRevision.RawByteSha256).Append('|').Append(resource.SourceRevision.RawByteLength)
            .Append('|').Append(resource.CapturedMember.RevisionId).Append('|').Append(resource.CapturedMember.MembershipIdentity)
            .Append('|').Append(resource.Origin.Source.ContentHash).Append('|').Append(resource.Origin.StartOffset).Append('|').Append(resource.Origin.Length)
            .Append('|').Append(resource.Field).Append('|').Append(resource.ModifierSuffix)
            .Append('|').Append(WccUnitTypeUpdatePlanner.GetResourceRelativePath(this, resource));
        Identity = WccContentHash.ComputeText(identity.ToString());
    }
    public WmlStaticSource OriginalSource { get; }
    public WmlStaticSource CandidateSource { get; }
    public WmlStaticSourceRange OriginalRange { get; }
    public WmlStaticSourceRange CandidateRange { get; }
    public string Id { get; }
    public string ProjectRootPath { get; }
    public string TextDomain { get; }
    public WmlEvaluationContext PlanningContext { get; }
    public string Identity { get; }
    public WccStaticSourceCaptureResult ManagedCapture { get; }
    public WccStaticSourceCaptureResult ProjectCapture { get; }
    public IReadOnlyList<WccContentHelperReceipt> Helpers { get; }
    public IReadOnlyList<WccContentRegistryReceipt> Registries { get; }
    public IReadOnlyList<WmlStaticSource> DependencySources { get; }
    public IReadOnlyList<WmlStaticSource> SourceCreates { get; }
    public IReadOnlyList<WccContentResourceTransferReceipt> ResourceTransfers { get; }
    internal IReadOnlyList<(WccContentHelperReceipt Receipt, string Root, WccPackageRevisionId Revision)> HelperBindings { get; }
    internal IReadOnlyList<(WccContentRegistryReceipt Receipt, string Root, WccPackageRevisionId Revision)> RegistryBindings { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>Existing project unit edits keep their literal source; only missing Library definitions are copied.</summary>
public static partial class WccUnitTypeUpdatePlanner
{
    public static WccUnitTypeUpdateEdit Plan(WmlStaticSource original, WmlStaticSource candidate,
        WccStaticSourceCaptureResult managedCapture, WccStaticSourceCaptureResult projectCapture,
        WmlEvaluationContext context, string projectTextDomain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(managedCapture); ArgumentNullException.ThrowIfNull(projectCapture);
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (!managedCapture.IsCaptureComplete || !projectCapture.IsCaptureComplete || projectCapture.EntrySource is null)
            throw new InvalidOperationException("Unit updates need complete pinned project and Library source captures.");
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(projectTextDomain) is { } domainProblem) throw new ArgumentException(domainProblem);
        var root = Path.GetDirectoryName(projectCapture.EntrySource.Path)!;
        if (!SamePath(original.Path, candidate.Path) || !Inside(Path.Combine(root, "units"), original.Path)
            || projectCapture.Sources.Count(source => SameSource(source, original)) != 1)
            throw new InvalidOperationException("The unit draft is not the exact captured project unit file.");
        var before = ReadStandalone(original); var after = ReadStandalone(candidate);
        if (before.Id != after.Id) throw new InvalidOperationException("An existing unit update must keep its literal unit ID. Create a variation to change the ID.");
        if (original.Text[..before.Range.StartOffset] != candidate.Text[..after.Range.StartOffset]
            || original.Text[(before.Range.StartOffset + before.Range.Length)..] != candidate.Text[(after.Range.StartOffset + after.Range.Length)..])
            throw new InvalidOperationException("A checked unit update must preserve all comments and translation directives outside its unit block. Review those edits separately in RAW.");
        var roots = WccStaticSourceCapture.GetCapturedManagedRoots(managedCapture);
        var helperChoices = new Dictionary<string, List<(WccContentHelperReceipt Receipt, string Root, WccPackageRevisionId Revision)>>(StringComparer.Ordinal);
        var registryChoices = new Dictionary<(string Kind, string Name), List<(WccContentRegistryReceipt Receipt, string Root, WccPackageRevisionId Revision)>>();
        var unsupportedHelpers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var library in roots)
        {
            foreach (var file in managedCapture.Sources.Where(source => new[] { WccManagedLibraryFileKind.Macro, WccManagedLibraryFileKind.Ability, WccManagedLibraryFileKind.WeaponSpecial }
                .Any(kind => WccManagedLibraryFileStore.IsPathForKind(library.RootPath, kind, source.Path))))
            {
                var receipt = WccUnitTypeMaterializationPlanner.CaptureHelper(file, out var reason);
                if (receipt is null)
                {
                    foreach (var definition in WmlMacroDefinitionScanner.Scan(file.Text)) unsupportedHelpers[definition.Name] = reason;
                    continue;
                }
                if (!helperChoices.TryGetValue(receipt.Name, out var list)) helperChoices.Add(receipt.Name, list = []);
                list.Add((receipt, library.RootPath, library.RevisionId));
            }
            foreach (var file in WccUnitTypeMaterializationPlanner.GetCapturedRegistrySources(managedCapture, library.RootPath))
            {
                var receipt = WccUnitTypeMaterializationPlanner.CaptureRegistry(file, out _); if (receipt is null) continue;
                if (!registryChoices.TryGetValue((receipt.Kind, receipt.Name), out var list)) registryChoices.Add((receipt.Kind, receipt.Name), list = []);
                list.Add((receipt, library.RootPath, library.RevisionId));
            }
        }
        var projectDefinitions = projectCapture.Sources.Where(source => !roots.Any(library => Inside(library.RootPath, source.Path)))
            .SelectMany(source => WmlMacroDefinitionScanner.Scan(source.Text).Select(definition => (definition.Name, Source: source))).ToLookup(row => row.Name, StringComparer.Ordinal);
        var priorBindings = EvaluateProject(projectCapture, context, null, cancellationToken).Invocations
            .Where(call => call.Outcome == WmlStaticInvocationOutcome.Expanded && call.Definition is { } definition
                && projectCapture.Sources.Any(source => SameSource(source, definition.Source))
                && (call.InvocationOrigins.Any(origin => InRange(origin, before.Range))
                    || call.ExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin => InRange(origin, before.Range)))))
            .Select(call => call.Name).ToHashSet(StringComparer.Ordinal);
        var priorRegistryReferences = RegistryReferences(before.Range.Text).ToHashSet();
        var helpers = new List<(WccContentHelperReceipt Receipt, string Root, WccPackageRevisionId Revision)>();
        var registries = new List<(WccContentRegistryReceipt Receipt, string Root, WccPackageRevisionId Revision)>();
        var visiting = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        var registered = EvaluateAvailableRegistry(projectCapture, original, before.Range, context, cancellationToken);
        void Visit(string raw, IReadOnlyList<string> parameters, int depth)
        {
            if (depth > 64 || helpers.Count + registries.Count > 64) throw new InvalidOperationException("The unit helper and registry closure exceeds 64 definitions.");
            foreach (var invocation in WmlMacroInvocationSyntaxScanner.Scan(raw))
            {
                cancellationToken.ThrowIfCancellationRequested(); var name = invocation.Name;
                if (parameters.Contains(name, StringComparer.Ordinal) || context.DefinedSymbols.Contains(name)) continue;
                if (name.Contains('/') || name.Contains('\\') || name.Contains('{') || name.Contains('}'))
                    throw new InvalidOperationException("Unit helper {" + name + "} requires unsupported include or computed-name activation.");
                if (priorBindings.Contains(name) && projectDefinitions[name].Any()) continue;
                if (helperChoices.TryGetValue(name, out var choices))
                {
                    if (choices.Count != 1) throw new InvalidOperationException("Choose one unique Library definition for helper {" + name + "}.");
                    var selected = choices[0]; var rendered = WccUnitTypeMaterializationPlanner.RenderUpdateHelper(selected.Receipt, selected.Root, root, projectTextDomain);
                    if (projectDefinitions[name].Any(row => !SamePath(row.Source.Path, rendered.Path)))
                        throw new InvalidOperationException("Helper {" + name + "} already has a different Core or project definition. Keep that definition or rename the Library helper.");
                    if (visiting.Contains(name)) throw new InvalidOperationException("The captured helper closure is cyclic at {" + name + "}.");
                    if (!visited.Add(name)) continue;
                    visiting.Add(name); helpers.Add(selected);
                    var body = HelperBody(selected.Receipt);
                    if (WccScenarioEventSourcePlanner.HasUnsafeControls(MaskTextDomains(body), isCapturedMacroBody: true))
                        throw new InvalidOperationException("Helper {" + name + "} changes global preprocessor state and cannot be copied with a unit update.");
                    Visit(body, selected.Receipt.Parameters, depth + 1); visiting.Remove(name);
                }
                else if (!projectDefinitions[name].Any())
                    throw new InvalidOperationException("Helper {" + name + "} cannot be resolved from Core or the project: " + unsupportedHelpers.GetValueOrDefault(name, "Choose one captured standalone Library helper."));
            }
            foreach (var reference in RegistryReferences(raw))
            {
                var key = (reference.Kind, reference.Name); var existing = registered.Where(row => row.Kind == key.Kind && row.Name == key.Name).ToArray();
                if (existing.Length > 1) throw new InvalidOperationException("Registry entry '" + reference.Name + "' is ambiguous in the captured project and Core.");
                if (existing.Length == 1 && priorRegistryReferences.Contains(reference)) continue;
                if (!registryChoices.TryGetValue(key, out var choices))
                {
                    if (existing.Length != 1) throw new InvalidOperationException("Registry entry '" + reference.Name + "' has no unique captured Core, project or supported Library definition.");
                    continue;
                }
                if (choices.Count != 1) throw new InvalidOperationException("Choose one unique Library registry definition for '" + reference.Name + "'.");
                var selected = choices[0]; var rendered = WccUnitTypeMaterializationPlanner.GetRegistrySources([selected.Receipt], root, projectTextDomain).Single();
                if (existing.Length == 1 && !SamePath(existing[0].Source.Path, rendered.Path))
                    throw new InvalidOperationException("Registry entry '" + reference.Name + "' already has a different Core or campaign definition. Keep it or rename the Library entry.");
                var identity = "registry:" + reference.Kind + ":" + reference.Name;
                if (visiting.Contains(identity)) throw new InvalidOperationException("The registry closure is cyclic at '" + reference.Name + "'.");
                if (!visited.Add(identity)) continue;
                visiting.Add(identity); registries.Add(selected); Visit(selected.Receipt.EntryRange.Text, [], depth + 1); visiting.Remove(identity);
            }
        }
        Visit(after.Range.Text, [], 0);
        var resources = CaptureHelperResources(candidate, helpers, registries, managedCapture, projectCapture,
            context, root, projectTextDomain, cancellationToken);
        var dependencies = helpers.Select(row => WccUnitTypeMaterializationPlanner.RenderUpdateHelper(row.Receipt, row.Root, root, projectTextDomain, resources))
            .Concat(WccUnitTypeMaterializationPlanner.GetRegistrySources(registries.Select(row => row.Receipt), root, projectTextDomain)).ToArray();
        var missing = MissingSources(dependencies, projectCapture.Sources);
        return new(original, candidate, before.Range, after.Range, after.Id, root, projectTextDomain, context, managedCapture, projectCapture, helpers, registries, dependencies, missing, resources);
    }

    public static IReadOnlyList<WmlStaticSource> PlanSources(WccUnitTypeUpdateEdit edit) => edit.SourceCreates;

    internal static IReadOnlyList<WmlStaticSource> PlanMissingSources(WccUnitTypeUpdateEdit edit, IEnumerable<WmlStaticSource> existingSources) =>
        MissingSources(edit.DependencySources, existingSources);

    private static IReadOnlyList<WmlStaticSource> MissingSources(IEnumerable<WmlStaticSource> planned, IEnumerable<WmlStaticSource> existingSources)
    {
        var existing = existingSources.ToArray(); var missing = new List<WmlStaticSource>();
        foreach (var source in planned)
        {
            var matches = existing.Where(item => SamePath(item.Path, source.Path)).ToArray();
            if (matches.Length == 0) missing.Add(source);
            else if (matches.Length != 1 || matches[0].Path != source.Path || !SameSource(matches[0], source))
                throw new InvalidOperationException("Shared definition '" + Path.GetFileName(source.Path) + "' already exists with different content or case. Keep the campaign definition or rename the Library entry.");
        }
        if (planned.GroupBy(source => source.Path, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidOperationException("The unit dependency definitions collide at a shared campaign filename.");
        return Array.AsReadOnly(missing.ToArray());
    }

    public static void ValidateSourceEvidence(WccLibrarySnapshot snapshot, WccUnitTypeUpdateEdit edit, WccPackageRevisionId? projectRevision = null)
    {
        if (snapshot.SourceFilesById.Values.Count(file => (projectRevision is null || file.PackageRevisionId == projectRevision)
            && file.StoragePath is { } path && SamePath(path, edit.OriginalSource.Path)
            && file.ContentHash == edit.OriginalSource.ContentHash) != 1)
            throw new InvalidOperationException("The edited unit no longer matches its exact indexed project source.");
        foreach (var row in edit.HelperBindings.Select(row => (row.Root, row.Revision, row.Receipt.Source))
            .Concat(edit.RegistryBindings.Select(row => (row.Root, row.Revision, row.Receipt.Source))))
        {
            if (!snapshot.RevisionsById.TryGetValue(row.Revision, out var revision)
                || !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package)
                || package.SourceKind != WccPackageSourceKinds.WccManagedLibrary || package.SourceLocation is null || !SamePath(package.SourceLocation, row.Root)
                || snapshot.SourceFilesById.Values.Count(file => file.PackageRevisionId == row.Revision && file.StoragePath is { } path
                    && SamePath(path, row.Source.Path) && file.ContentHash == row.Source.ContentHash) != 1)
                throw new InvalidOperationException("The unit dependency no longer matches its captured Library manifest: " + row.Source.Path);
        }
        foreach (var resource in edit.ResourceTransfers)
        {
            var helper = edit.HelperBindings.Single(row => SamePiece(row.Receipt.Definition, resource.Piece));
            var member = WccStaticSourceCapture.FindCapturedResourceMember(edit.ManagedCapture, helper.Revision, resource.Folder + "/" + resource.Filename);
            if (member != resource.CapturedMember || member is null || member.IsCore || !SamePath(member.RootPath, helper.Root)
                || member.PhysicalPath != resource.SourceRevision.SourcePath || !SamePath(resource.SourceRevision.ProjectRootPath, helper.Root)
                || !InRange(resource.Origin, helper.Receipt.Definition)
                || !WccUnitTypeMaterializationPlanner.IsDirectHelperResourceAssignment(helper.Receipt, resource.Origin, resource.Field))
                throw new InvalidOperationException("The helper resource no longer has its exact captured Library owner, member and assignment: " + resource.Filename);
        }
    }

    public static string? VerifySourceFreshness(WccUnitTypeUpdateEdit edit)
    {
        var original = WccTextDiskRevisionCapture.Capture(edit.ProjectRootPath, edit.OriginalSource.Path, edit.OriginalSource.Text);
        if (!original.IsCaptured) return "The edited unit changed or cannot be verified: " + original.ErrorMessage;
        var rows = edit.HelperBindings.Select(row => (row.Root, row.Revision, row.Receipt.Source))
            .Concat(edit.RegistryBindings.Select(row => (row.Root, row.Revision, row.Receipt.Source))).ToArray();
        foreach (var row in rows.DistinctBy(row => row.Revision))
            if (WccStaticSourceCapture.VerifyCapturedRootInventory(edit.ManagedCapture, row.Revision) is { } inventoryProblem) return inventoryProblem;
        foreach (var row in rows.DistinctBy(row => row.Source.Path))
        {
            var captured = WccTextDiskRevisionCapture.Capture(row.Root, row.Source.Path, row.Source.Text);
            if (!captured.IsCaptured) return "The captured unit helper or registry changed: " + row.Source.Path + ": " + captured.ErrorMessage;
        }
        foreach (var resource in edit.ResourceTransfers.DistinctBy(item => item.SourceRevision.SourcePath))
        {
            var captured = WccTextDiskRevisionCapture.CaptureBinary(resource.SourceRevision.ProjectRootPath, resource.SourceRevision.SourcePath);
            if (!captured.IsCaptured || captured.Revision is null || !resource.SourceRevision.IsSameRevisionAs(captured.Revision))
                return "The captured helper resource bytes, identity or route changed: " + resource.SourceRevision.SourcePath;
        }
        return null;
    }

    internal static (string Id, WmlStaticSourceRange Range) ReadStandalone(WmlStaticSource source)
    {
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        if (!source.IsAuthored || structure.Problem is not null || !WmlSingleBlockValidator.ValidateSourceStructure(source.Text).IsValid
            || structure.Directives.Any(directive => directive.Name != "textdomain") || WccScenarioEventSourcePlanner.HasUnsafeControls(MaskTextDomains(source.Text)))
            throw new InvalidOperationException("A checked unit update needs one standalone unit_type without definitions, conditional or stateful directives. Use RAW for other structures.");
        var blocks = WmlBlockScanner.FindTopLevelBlockSpans(structure.MaskedText).ToArray();
        if (blocks.Length != 1 || blocks[0].TagName != "unit_type") throw new InvalidOperationException("A unit update requires exactly one standalone unit_type.");
        var block = blocks[0]; var outside = structure.MaskedText.ToCharArray(); Array.Fill(outside, ' ', block.StartIndex, block.EndIndexExclusive - block.StartIndex);
        if (outside.Any(character => !char.IsWhiteSpace(character)) || structure.Macros.Any(macro => macro.StartOffset < block.StartIndex || macro.EndOffsetExclusive > block.EndIndexExclusive)
            || WmlMacroInvocationSyntaxScanner.Scan(source.Text).Any(call => call.Name.Contains('/') || call.Name.Contains('\\')))
            throw new InvalidOperationException("Includes or executable content outside the edited unit require separate RAW source review.");
        var range = new WmlStaticSourceRange(source, block.StartIndex, block.EndIndexExclusive - block.StartIndex);
        var editable = WmlEditableBlockParser.Parse(new WmlBlock("unit_type", range.Text), new HashSet<string>(["id"], StringComparer.OrdinalIgnoreCase));
        if (!editable.Known.TryGetValue("id", out var idField) || !WmlUnitReferenceProjection.TryLiteral(idField.Value, out var id) || string.IsNullOrWhiteSpace(id)
            || WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelField(editable, "id").Kind != WmlFieldSafetyKind.PlainEditable)
            throw new InvalidOperationException("The edited unit needs one nonempty literal ID.");
        return (id, range);
    }

    private static IEnumerable<(string Kind, string Name)> RegistryReferences(string raw)
    {
        var structure = WmlAuthoredStructureScanner.Scan(raw); var tree = WmlProjectionTreeParser.Parse(structure.MaskedText);
        foreach (var node in tree.Roots.SelectMany(node => node.DescendantsAndSelf()))
        {
            var start = structure.Lines[node.StartLine].StartOffset; var end = structure.Lines[node.EndLineExclusive - 1].ContentEndOffset;
            var editable = WmlEditableBlockParser.Parse(new WmlBlock(node.TagName, raw[start..end]), new HashSet<string>(["abilities_list", "specials_list"], StringComparer.OrdinalIgnoreCase));
            foreach (var key in new[] { "abilities_list", "specials_list" })
                if (editable.Known.TryGetValue(key, out var field))
                {
                    if (!WmlResourceValueTextDecoder.TryDecode(field.Value, out var value, out _)) throw new InvalidOperationException("The unit registry list is not literal: " + key);
                    foreach (var id in WesnothRegistryIdList.Parse(value)) yield return (key == "abilities_list" ? "abilities" : "weapon_specials", id);
                }
        }
    }

    private static IReadOnlyList<(string Kind, string Name, WmlStaticSource Source)> EvaluateAvailableRegistry(WccStaticSourceCaptureResult capture,
        WmlStaticSource original, WmlStaticSourceRange range, WmlEvaluationContext context, CancellationToken token)
    {
        var text = original.Text.ToCharArray(); for (var index = range.StartOffset; index < range.StartOffset + range.Length; index++) if (text[index] is not ('\r' or '\n')) text[index] = ' ';
        var evaluated = EvaluateProject(capture, context, new(original.Path, new string(text)), token);
        var projection = WmlStaticScalarProjection.Create(evaluated, token);
        if (!evaluated.IsComplete || projection.Diagnostics.Count != 0)
        {
            var diagnostic = evaluated.Diagnostics.FirstOrDefault(issue => issue.IsBlocking) ?? projection.Diagnostics.FirstOrDefault();
            var detail = diagnostic?.Message ?? "The captured evaluation is incomplete.";
            if (diagnostic?.Origin is { } origin)
            {
                var line = 1;
                for (var offset = 0; offset < origin.StartOffset; offset++)
                    if (origin.Source.Text[offset] == '\n' || origin.Source.Text[offset] == '\r'
                        && (offset + 1 >= origin.Source.Text.Length || origin.Source.Text[offset + 1] != '\n')) line++;
                var invocation = evaluated.Invocations.FirstOrDefault(call => call.InvocationOrigins.Any(callOrigin =>
                    SameSource(callOrigin.Source, origin.Source) && origin.StartOffset >= callOrigin.StartOffset
                    && origin.StartOffset + origin.Length <= callOrigin.StartOffset + callOrigin.Length));
                if (invocation is not null) detail += " {" + invocation.Name + "}";
                detail += " [" + origin.Source.Path + ":" + line + "]";
            }
            throw new InvalidOperationException("The existing campaign contains unresolved WML outside the edited unit (" +
                context.DisplayName + "): " + detail);
        }
        return projection.Owners.Where(owner => owner.TagName is "abilities" or "weapon_specials" && owner.Parent is { TagName: "units", Parent: null })
            .SelectMany(owner => owner.DirectChildren.Select(entry => (Kind: owner.TagName, Name: WccScenarioFieldValueCodec.DecodeLiteral(entry.GetKnownValue("unique_id") ?? entry.GetKnownValue("id")), Source: entry.OpeningOrigin?.Source)))
            .Where(row => row.Name.Length > 0 && row.Source is not null).Select(row => (row.Kind, row.Name, row.Source!)).ToArray();
    }

    internal static WmlStaticEvaluationResult EvaluateProject(WccStaticSourceCaptureResult capture, WmlEvaluationContext context,
        WmlStaticSource? replacement, CancellationToken token)
    {
        if (!capture.HasCompleteCoreUnitPrelude || !WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            throw new InvalidOperationException("The unit update needs the exact pinned Core registry capture.");
        return new WmlStaticEvaluator().Evaluate(new(capture.EntrySource!, context,
            new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources).ToArray(),
            ["EDITOR", "MULTIPLAYER"], true, replacement is null ? capture.IncludeResolver : new UnitReplacementResolver(capture.IncludeResolver, replacement),
            WmlStaticEvaluationLimits.Campaign), token);
    }

    private static string HelperBody(WccContentHelperReceipt receipt)
    {
        var structure = WmlAuthoredStructureScanner.Scan(receipt.Definition.Text);
        var first = structure.Directives.First(directive => directive.Name == "define"); var last = structure.Directives.Last(directive => directive.Name == "enddef");
        return receipt.Definition.Text[structure.Lines[first.Line].EndOffsetExclusive..structure.Lines[last.Line].StartOffset];
    }

    private static string MaskTextDomains(string raw)
    {
        var structure = WmlAuthoredStructureScanner.Scan(raw); var masked = raw.ToCharArray();
        foreach (var directive in structure.Directives.Where(directive => directive.Name == "textdomain"))
        {
            var line = structure.Lines[directive.Line]; Array.Fill(masked, ' ', line.StartOffset, line.ContentEndOffset - line.StartOffset);
        }
        return new(masked);
    }

    internal sealed class UnitReplacementResolver(IWmlStaticIncludeResolver inner, WmlStaticSource replacement) : IWmlStaticIncludeResolver
    {
        public WmlStaticIncludeResolution Resolve(string path, WmlStaticSource caller)
        {
            var result = inner.Resolve(path, caller);
            return result with { Sources = Array.AsReadOnly(result.Sources.Select(source => SamePath(source.Path, replacement.Path) ? replacement : source).ToArray()) };
        }
    }
    internal static bool SameSource(WmlStaticSource left, WmlStaticSource right) => left.IsAuthored == right.IsAuthored && SamePath(left.Path, right.Path) && left.Text == right.Text && left.ContentHash == right.ContentHash;
    internal static bool InRange(WmlStaticSourceRange origin, WmlStaticSourceRange range) => SameSource(origin.Source, range.Source)
        && origin.StartOffset >= range.StartOffset && origin.StartOffset + origin.Length <= range.StartOffset + range.Length;
    internal static bool SamePath(string left, string right) => string.Equals(
        left.Contains("://", StringComparison.Ordinal) ? left : Path.GetFullPath(left),
        right.Contains("://", StringComparison.Ordinal) ? right : Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Inside(string root, string path) => Path.GetRelativePath(root, path) is var relative && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
