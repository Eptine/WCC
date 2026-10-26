using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Macros;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.References;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.Core.Units;

/// <summary>Captured authored helper text, not an active definition or write authority.</summary>
public sealed class WccContentHelperReceipt
{
    internal WccContentHelperReceipt(string name, WmlStaticSource source, WmlStaticSourceRange definition,
        string? textDomain, IReadOnlyList<string> parameters, int? requiredParameterCount = null)
    { Name = name; Source = source; Definition = definition; TextDomain = textDomain;
        ContentFingerprint = WccContentHash.ComputeText(definition.Text); Parameters = Array.AsReadOnly(parameters.ToArray());
        RequiredParameterCount = requiredParameterCount ?? parameters.Count; }
    public string Name { get; }
    public WmlStaticSource Source { get; }
    public WmlStaticSourceRange Definition { get; }
    public string ContentFingerprint { get; }
    public string? TextDomain { get; }
    internal IReadOnlyList<string> Parameters { get; }
    internal int RequiredParameterCount { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>One exact owned resource assignment and an immutable original byte revision.</summary>
public sealed class WccContentResourceTransferReceipt
{
    internal WccContentResourceTransferReceipt(string folder, string filename, string field,
        WmlStaticSourceRange origin, string suffix, WccCapturedResourceMember member,
        WccBinaryDiskRevision revision, WmlStaticSourceRange piece)
    { Folder = folder; Filename = filename; Field = field; Origin = origin; ModifierSuffix = suffix;
        CapturedMember = member; SourceRevision = revision; Piece = piece; }
    public string Folder { get; }
    public string Filename { get; }
    public string Field { get; }
    public WmlStaticSourceRange Origin { get; }
    public string ModifierSuffix { get; }
    public WccCapturedResourceMember CapturedMember { get; }
    public WccBinaryDiskRevision SourceRevision { get; }
    internal WmlStaticSourceRange Piece { get; }
    public bool AuthorizesPersistence => false;
}

public sealed record WccContentBinaryCreation(string DestinationPath, WccBinaryDiskRevision SourceRevision)
{ public bool AuthorizesPersistence => false; }

/// <summary>Reusable source/helper/byte closure receipt. No mutation is performed by planning.</summary>
public sealed class WccContentMaterializationPlan
{
    internal WccContentMaterializationPlan(IEnumerable<WccContentHelperReceipt> helpers,
        IEnumerable<WccContentResourceTransferReceipt> resources, IEnumerable<WccContentDependencyIssue> issues,
        WccContentDependencyPlan? dependencies, string identity, IEnumerable<WccContentRegistryReceipt>? registries = null)
    { Helpers = Array.AsReadOnly(helpers.ToArray()); ResourceTransfers = Array.AsReadOnly(resources.ToArray());
        Issues = Array.AsReadOnly(issues.Distinct().ToArray()); Dependencies = dependencies; Identity = identity;
        Registries = Array.AsReadOnly((registries ?? []).ToArray()); }
    public IReadOnlyList<WccContentRegistryReceipt> Registries { get; }
    public IReadOnlyList<WccContentHelperReceipt> Helpers { get; }
    public IReadOnlyList<WccContentResourceTransferReceipt> ResourceTransfers { get; }
    public IReadOnlyList<WccContentDependencyIssue> Issues { get; }
    public WccContentDependencyPlan? Dependencies { get; }
    public string Identity { get; }
    public bool IsComplete => Issues.Count == 0 && Dependencies is { IsComplete: true };
    public bool AuthorizesPersistence => false;
}

public sealed class WccUnitTypeMaterializationPlan
{
    internal WccUnitTypeMaterializationPlan(WccUnitTypeAuthoredCopySource source, WccStaticSourceCaptureResult capture,
        IEnumerable<WccContentHelperReceipt> helpers, IEnumerable<WccContentResourceTransferReceipt> resources,
        IEnumerable<WccContentDependencyIssue> issues, WccContentDependencyPlan? dependencies = null,
        IEnumerable<WccContentRegistryReceipt>? registries = null,
        IEnumerable<WccUnitTypeCoreCampaignHelperSource>? coreCampaignHelpers = null)
    {
        OriginalSource = source; Capture = capture;
        var helperArray = helpers.ToArray(); var resourceArray = resources.ToArray(); var registryArray = (registries ?? []).ToArray();
        CoreCampaignHelpers = Array.AsReadOnly((coreCampaignHelpers ?? []).Where(item => helperArray.Any(helper =>
            helper.Name == item.Source.Name && helper.Definition == item.Source.Definition)).ToArray());
        var identity = new StringBuilder().Append(source.RevisionId).Append('|').Append(source.Source.ContentHash)
            .Append('|').Append(source.ContentFingerprint).Append('|').Append(source.StartOffset).Append('|').Append(source.Length);
        foreach (var helper in helperArray)
            identity.Append("\nH|").Append(helper.Name).Append('|').Append(Path.GetRelativePath(source.RootPath, helper.Source.Path).Replace('\\', '/'))
                .Append('|').Append(helper.Source.ContentHash).Append('|').Append(helper.Definition.StartOffset)
                .Append('|').Append(helper.Definition.Length).Append('|').Append(helper.ContentFingerprint).Append('|').Append(helper.TextDomain);
        foreach (var resource in resourceArray)
            identity.Append("\nR|").Append(resource.Folder).Append('|').Append(resource.Filename).Append('|').Append(resource.Field)
                .Append('|').Append(Path.GetRelativePath(source.RootPath, resource.Origin.Source.Path).Replace('\\', '/'))
                .Append('|').Append(resource.Origin.StartOffset).Append('|').Append(resource.Origin.Length).Append('|').Append(resource.ModifierSuffix)
                .Append('|').Append(resource.CapturedMember.MembershipIdentity).Append('|').Append(resource.SourceRevision.RawByteSha256);
        foreach (var registry in registryArray)
            identity.Append("\nG|").Append(registry.Kind).Append('|').Append(registry.Name).Append('|').Append(registry.Source.Path)
                .Append('|').Append(registry.Source.ContentHash).Append('|').Append(registry.ContentFingerprint);
        foreach (var helper in CoreCampaignHelpers)
            identity.Append("\nC|").Append(helper.Source.PackageId).Append('|').Append(helper.Source.RevisionId)
                .Append('|').Append(helper.Source.EntityId).Append('|').Append(helper.Source.SourceFileId).Append('|').Append(helper.Source.RootPath)
                .Append('|').Append(helper.ManifestIdentity).Append('|').Append(helper.CoreRevisionId).Append('|').Append(helper.CoreRootPath);
        Content = new(helperArray, resourceArray, issues, dependencies, WccContentHash.ComputeText(identity.ToString()), registryArray);
    }
    public WccUnitTypeAuthoredCopySource OriginalSource { get; }
    public WccStaticSourceCaptureResult Capture { get; }
    public IReadOnlyList<WccUnitTypeCoreCampaignHelperSource> CoreCampaignHelpers { get; }
    public WccContentMaterializationPlan Content { get; }
    public IReadOnlyList<WccContentHelperReceipt> Helpers => Content.Helpers;
    public IReadOnlyList<WccContentRegistryReceipt> Registries => Content.Registries;
    public IReadOnlyList<WccContentResourceTransferReceipt> ResourceTransfers => Content.ResourceTransfers;
    public IReadOnlyList<WccContentDependencyIssue> Issues => Content.Issues;
    public string Identity => Content.Identity;
    public bool IsComplete => Content.IsComplete;
    public bool AuthorizesPersistence => false;
}

internal sealed record WccMaterializationResourceBinding(string Field, string Filename, WmlStaticSourceRange Origin);
internal sealed record WccMaterializationCandidate(WmlStaticSource Source,
    IReadOnlyList<(string Name, WmlStaticSourceRange Range)> Helpers,
    IReadOnlyList<WccMaterializationResourceBinding> Resources,
    IReadOnlyList<(string Name, WmlStaticSourceRange Range)> ConflictGuards)
{
    internal IReadOnlyList<(string Kind, string Name, WmlStaticSourceRange Range)> Registries { get; init; } = [];
    internal bool ContainsRegistry(string kind, string name, WmlStaticScalarOwner owner) =>
        owner.OpeningOrigin is { } opening && owner.ClosingOrigin is { } closing && !owner.IsAmended
        && Registries.Any(entry => entry.Kind == kind && entry.Name == name
            && WccUnitTypeMaterializationPlanner.SameSource(opening.Source, entry.Range.Source)
            && WccUnitTypeMaterializationPlanner.SameSource(closing.Source, entry.Range.Source)
            && opening.StartOffset >= entry.Range.StartOffset
            && closing.StartOffset + closing.Length <= entry.Range.StartOffset + entry.Range.Length);
    internal bool ContainsHelper(string name, WmlStaticSourceRange definition) => Helpers.Any(helper => helper.Name == name
        && WccUnitTypeMaterializationPlanner.SameSource(definition.Source, helper.Range.Source)
        && definition.StartOffset >= helper.Range.StartOffset
        && definition.StartOffset + definition.Length <= helper.Range.StartOffset + helper.Range.Length);
    internal bool ContainsResource(string field, string filename, WmlStaticScalarField value) => Resources.Any(resource =>
        resource.Field == field && resource.Filename == filename && value.Contributions.LastOrDefault() is { ValueMappingIsExact: true } contribution
        && contribution.ValueOrigins.Count == 1 && contribution.ValueOrigins[0].StartOffset == resource.Origin.StartOffset
        && contribution.ValueOrigins[0].Length == resource.Origin.Length
        && WccUnitTypeMaterializationPlanner.SameSource(contribution.ValueOrigins[0].Source, resource.Origin.Source));
    internal IReadOnlyList<WmlStaticSource> HelperSources => Helpers.Select(helper => helper.Range.Source).Concat(Registries.Select(entry => entry.Range.Source))
        .Where(source => !WccUnitTypeMaterializationPlanner.SameSource(source, Source)).DistinctBy(source => source.Path).ToArray();
    internal string? HelperConflict(WmlStaticSourceRange? origin) => origin is not null
        ? ConflictGuards.Where(helper => WccUnitTypeMaterializationPlanner.SameSource(origin.Source, helper.Range.Source)
            && origin.StartOffset >= helper.Range.StartOffset
            && origin.StartOffset + origin.Length <= helper.Range.StartOffset + helper.Range.Length)
            .Select(helper => helper.Name).FirstOrDefault() : null;
}

/// <summary>Bounded owned/original helper and literal image/sound materialization; Core remains referenced.</summary>
public static partial class WccUnitTypeMaterializationPlanner
{
    private const string LibraryDomain = "wesnoth-wcc-library";
    private const int MaximumHelpers = 64;
    private const int MaximumResources = 63;

    public static WccUnitTypeMaterializationPlan Plan(WccUnitTypeAuthoredCopySource source,
        WccStaticSourceCaptureResult managedCapture, WmlEvaluationContext context, CancellationToken cancellationToken = default)
        => PlanCore(source, managedCapture, context, null, cancellationToken);

    public static WccUnitTypeMaterializationPlan Plan(WccUnitTypeAuthoredCopySource source,
        WccStaticSourceCaptureResult managedCapture, WmlEvaluationContext context,
        WccUnitTypeCoreCampaignHelperCapture externalHelpers, CancellationToken cancellationToken = default)
        => PlanCore(source, managedCapture, context, externalHelpers, cancellationToken);

    private static WccUnitTypeMaterializationPlan PlanCore(WccUnitTypeAuthoredCopySource source,
        WccStaticSourceCaptureResult managedCapture, WmlEvaluationContext context,
        WccUnitTypeCoreCampaignHelperCapture? externalHelpers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(managedCapture); ArgumentNullException.ThrowIfNull(context);
        var issues = new List<WccContentDependencyIssue>(); var helpers = new List<WccContentHelperReceipt>();
        var transfers = new List<WccContentResourceTransferReceipt>(); var registries = new List<WccContentRegistryReceipt>();
        void Issue(string code, string reference, string field, string message, WmlStaticSourceRange? origin = null)
            => issues.Add(new(code, reference, field, message, origin));
        var external = externalHelpers?.Sources ?? [];
        if (externalHelpers is not null)
        {
            if (!SameReceipt(source, externalHelpers.UnitSource) || !ReferenceEquals(managedCapture, externalHelpers.ManagedCapture))
                throw new ArgumentException("The original campaign helper capture belongs to another exact unit source or namespace.", nameof(externalHelpers));
            issues.AddRange(externalHelpers.Issues);
        }
        if (!managedCapture.IsCaptureComplete || managedCapture.IncludeResolver is not IWmlStaticResourceResolver resources
            || managedCapture.Sources.Count(item => SameSource(item, source.Source)) != 1)
        { Issue("materialization.capture", source.Id, "source", "Materialization needs the exact selected file and complete pinned Library/Core capture."); return new(source, managedCapture, [], [], issues); }
        var coreEntries = managedCapture.IncludeResolver.Resolve("core/units.cfg", managedCapture.EntrySource!).Sources;
        if (coreEntries.Count != 1)
        { Issue("materialization.core", "Core", "source", "The exact captured Core entry is unavailable or ambiguous."); return new(source, managedCapture, [], [], issues); }
        var coreRoot = Path.GetDirectoryName(coreEntries[0].Path)!;
        if (issues.Count != 0) return new(source, managedCapture, [], [], issues);
        var coreNames = managedCapture.Sources.Where(item => Inside(coreRoot, item.Path))
            .SelectMany(item => WmlMacroDefinitionScanner.Scan(item.Text)).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var candidates = new Dictionary<string, List<WccContentHelperReceipt>>(StringComparer.Ordinal);
        if (!source.IsCore)
            foreach (var file in managedCapture.Sources.Where(item => Inside(source.RootPath, item.Path)
                && new[] { WccManagedLibraryFileKind.Macro, WccManagedLibraryFileKind.Ability, WccManagedLibraryFileKind.WeaponSpecial }
                    .Any(kind => WccManagedLibraryFileStore.IsPathForKind(source.RootPath, kind, item.Path))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var helper = CaptureHelper(file, out _);
                if (helper is null) continue;
                if (!candidates.TryGetValue(helper.Name, out var list)) candidates.Add(helper.Name, list = []);
                list.Add(helper);
            }
        foreach (var original in external)
        {
            if (!candidates.TryGetValue(original.Helper.Name, out var list)) candidates.Add(original.Helper.Name, list = []);
            list.Add(original.Helper);
        }
        var active = new HashSet<string>(StringComparer.Ordinal); var done = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string raw, IReadOnlyList<string> parameters, WmlStaticSourceRange owner, int depth)
        {
            foreach (var invocation in WmlMacroInvocationSyntaxScanner.Scan(raw))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = invocation.Name;
                if (parameters.Contains(name, StringComparer.Ordinal) || coreNames.Contains(name) && !candidates.ContainsKey(name)
                    || context.DefinedSymbols.Contains(name) && !candidates.ContainsKey(name) || name.Contains('/') || name.Contains('\\')) continue;
                if (!candidates.TryGetValue(name, out var matches) || matches.Count != 1)
                {
                    var namedFiles = managedCapture.Sources.Where(item => Inside(source.RootPath, item.Path)
                        && WmlMacroDefinitionScanner.Scan(item.Text).Any(definition => definition.Name == name)).ToArray();
                    var detail = namedFiles.Length == 1 ? CaptureHelper(namedFiles[0], out var reason) is null ? reason : "The helper is outside its managed Macro/Ability/WeaponSpecial storage." : "Choose one unique standalone managed helper.";
                    Issue("materialization.helper", name, "macro", "Helper {" + name + "} cannot be copied: " + detail, owner); continue;
                }
                if (active.Contains(name) || depth >= MaximumHelpers)
                { Issue("materialization.helper-cycle", name, "macro", "The helper closure is cyclic or exceeds 64 definitions.", matches[0].Definition); continue; }
                if (!done.Add(name)) continue;
                if (helpers.Count >= MaximumHelpers)
                { Issue("materialization.helper-limit", name, "macro", "The helper closure exceeds 64 definitions.", matches[0].Definition); continue; }
                active.Add(name); helpers.Add(matches[0]);
                Visit(matches[0].Definition.Text, matches[0].Parameters, matches[0].Definition, depth + 1); active.Remove(name);
            }
        }
        Visit(source.RawText, [], new(source.Source, source.StartOffset, source.Length), 0);
        // Missing or unsafe authored helpers make detached registration and resource
        // discovery impossible. Keep the exact primary faults without reporting their cascades.
        if (issues.Count != 0) return new(source, managedCapture, helpers, [], issues, coreCampaignHelpers: external);
        var draft = new WccUnitTypeMaterializationPlan(source, managedCapture, helpers, [], issues, coreCampaignHelpers: external);
        var preview = BuildCandidate(draft, null, null, Path.Combine(source.RootPath, "__wcc_materialization_preview.cfg"));
        var probe = WccUnitTypeDependencyPlanner.ProbeMaterialization(source, managedCapture, context, preview, cancellationToken);
        if (!source.IsCore)
        {
            var registryInventory = GetCapturedRegistrySources(managedCapture, source.RootPath)
                .Select(file => CaptureRegistry(file, out _)).Where(receipt => receipt is not null).Cast<WccContentRegistryReceipt>().ToArray();
            for (var round = 0; round < MaximumHelpers; round++)
            {
                var added = false;
                foreach (var missing in probe.Issues.Where(item => item.Code == "dependency.registry-needs-materialization"))
                {
                    var kind = missing.Field == "abilities_list" ? "abilities" : "weapon_specials";
                    if (registries.Any(item => item.Kind == kind && item.Name == missing.Reference)) continue;
                    var matches = registryInventory.Where(item => item.Kind == kind && item.Name == missing.Reference).ToArray();
                    if (matches.Length != 1) continue; // The final dependency report retains the named missing/ambiguous ID.
                    registries.Add(matches[0]); added = true;
                    Visit(matches[0].EntryRange.Text, [], matches[0].EntryRange, 0);
                }
                if (!added) break;
                draft = new(source, managedCapture, helpers, [], issues, registries: registries, coreCampaignHelpers: external);
                preview = BuildCandidate(draft, null, null, preview.Source.Path);
                probe = WccUnitTypeDependencyPlanner.ProbeMaterialization(source, managedCapture, context, preview, cancellationToken);
            }
        }
        if (probe.Evaluation is { } evaluated)
        {
            var scalar = WmlStaticScalarProjection.Create(evaluated, cancellationToken);
            var projection = WmlCheckResourceProjection.Create(evaluated, scalar, _ => true, cancellationToken);
            foreach (var resourceOwner in projection.Owners)
                foreach (var entry in resourceOwner.ValueOwner.Fields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var kind = WmlResourceFieldCatalog.Find(resourceOwner.OriginalOwner, entry.Key);
                    if (kind is not (WmlResourceFieldKind.Image or WmlResourceFieldKind.AnimationImage or WmlResourceFieldKind.Sound)) continue;
                    var field = entry.Value; var contribution = field.Contributions.LastOrDefault();
                    var foreignOwners = ForeignResourceOwners(draft, preview, resourceOwner.OriginalOwner, contribution);
                    var folder = kind == WmlResourceFieldKind.Sound ? "sounds" : "images";
                    if (foreignOwners.Count != 0)
                    {
                        if (contribution is not { ValueMappingIsExact: true } || !field.IsKnown
                            || !WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var foreignValue, out _))
                        { Issue("materialization.core-campaign-resource-shape", entry.Key, entry.Key,
                            "An original campaign helper resource has an opaque or unproved mapped value.", foreignOwners[0].Source.Definition); continue; }
                        var foreignSyntax = kind == WmlResourceFieldKind.Sound ? WmlResourceReferenceSyntax.ParseSound(foreignValue, cancellationToken: cancellationToken)
                            : WmlResourceReferenceSyntax.ParseImage(foreignValue, kind == WmlResourceFieldKind.AnimationImage, cancellationToken: cancellationToken);
                        if (foreignOwners.Any(helper => ResourceAliases(folder, foreignSyntax.Filenames).Any(alias =>
                            WccStaticSourceCapture.CapturedHelperResourcePresence(helper.Capture, helper.Source.RevisionId, folder + "/" + alias) == WmlStaticTruth.Unknown)))
                        { Issue("materialization.core-campaign-resource-member", foreignValue, entry.Key,
                            "The original campaign resource namespace has ambiguous or unproved spelling; its bytes cannot be replaced by a Core reference.", foreignOwners[0].Source.Definition); continue; }
                        var privateMembers = foreignOwners.SelectMany(helper => ResourceAliases(folder, foreignSyntax.Filenames).Select(alias =>
                            WccStaticSourceCapture.FindCapturedResourceMember(helper.Capture, helper.Source.RevisionId, folder + "/" + alias)))
                            .OfType<WccCapturedResourceMember>().Where(member => !member.IsCore).Distinct().ToArray();
                        if (privateMembers.Length != 0 && (contribution.ValueOrigins.Count != 1
                            || !TryOriginalOrigin(draft, preview, contribution.ValueOrigins[0], out _, out _)))
                        { Issue("materialization.core-campaign-resource-shape", foreignValue, entry.Key,
                            "Original campaign-owned resource '" + foreignValue + "' needs one direct authored assignment; composite origins cannot borrow Core bytes.", foreignOwners[0].Source.Definition); continue; }
                    }
                    if (contribution is null) continue;
                    var copiedRegistry = preview.Registries.SingleOrDefault(registry => WithinCopiedRegistry(resourceOwner.OriginalOwner, registry.Range));
                    WmlStaticSourceRange origin; WmlStaticSourceRange piece;
                    if (copiedRegistry.Range is not null)
                    {
                        // Registry assets have no authored rewrite/copy contract yet.
                        // Bind refusals to the immutable receipt, without treating a
                        // coincident Core filename as proof of Library byte ownership.
                        origin = draft.Registries.Single(registry => registry.Kind == copiedRegistry.Kind && registry.Name == copiedRegistry.Name).EntryRange;
                        piece = origin;
                    }
                    else
                    {
                        if (!contribution.ValueMappingIsExact || contribution.ValueOrigins.Count != 1
                            || !TryOriginalOrigin(draft, preview, contribution.ValueOrigins[0], out origin, out piece)) continue;
                    }
                    var helperOwner = helpers.FirstOrDefault(helper => helper.Definition == piece);
                    var originalHelper = helperOwner is null ? null : ExternalHelper(draft, helperOwner);
                    var resourceCapture = originalHelper?.Capture ?? managedCapture;
                    var resourceRevision = originalHelper?.Source.RevisionId ?? source.RevisionId;
                    var resourceRoot = originalHelper?.Source.RootPath ?? source.RootPath;
                    if (!WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var value, out _) || value.Length == 0
                        || value is "none" or "unit_image" && entry.Key is "profile" or "small_profile" or "portrait") continue;
                    var syntax = kind == WmlResourceFieldKind.Sound ? WmlResourceReferenceSyntax.ParseSound(value, cancellationToken: cancellationToken)
                        : WmlResourceReferenceSyntax.ParseImage(value, kind == WmlResourceFieldKind.AnimationImage, cancellationToken: cancellationToken);
                    if (copiedRegistry.Range is not null)
                    {
                        foreach (var registryFilename in syntax.Filenames)
                        {
                            var aliases = folder == "images" && (registryFilename.EndsWith(".png", StringComparison.Ordinal) || registryFilename.EndsWith(".jpg", StringComparison.Ordinal))
                                ? new[] { registryFilename, registryFilename[..^4] + ".webp" } : new[] { registryFilename };
                            if (aliases.Any(alias => WccStaticSourceCapture.FindCapturedResourceMember(managedCapture, source.RevisionId, folder + "/" + alias)
                                is { IsCore: false } member && SamePath(member.RootPath, source.RootPath)))
                                Issue("materialization.registry-resource", registryFilename, entry.Key, "Library-owned registry resource '" + registryFilename
                                    + "' needs a separate checked asset copy; a Core filename match does not prove the same bytes.", origin);
                        }
                        continue; // Only captured Core registry assets remain references.
                    }
                    var coreAvailable = syntax.IsComplete && syntax.Filenames.Count != 0 && syntax.Filenames.All(filename =>
                        WmlResourceLookupPaths.Create(filename, folder, ["core"], folder == "images", cancellationToken, resources.InstallationResourceFolders)
                            .Candidates.Any(path => resources.ResolveResource(path, preview.Source) is { Exists: WmlStaticTruth.True, MembershipIdentity.Length: > 0 }));
                    if (source.IsCore) continue;
                    if (coreAvailable)
                    {
                        var localMatches = ResourceAliases(folder, syntax.Filenames).Where(filename => WccStaticSourceCapture.FindCapturedResourceMember(resourceCapture, resourceRevision, folder + "/" + filename) is not null).ToArray();
                        if (localMatches.Length != 0)
                            Issue("materialization.resource-ambiguous", value, entry.Key, "Resource '" + value + "' in " + entry.Key + " exists in both captured Core and the source package; its intended bytes cannot be selected safely.", origin);
                        continue;
                    }
                    var image = kind == WmlResourceFieldKind.Sound ? null : WccImageExpressionParser.TryParse(value);
                    var filename = image?.BaseReference ?? value; var suffix = image?.ModifierSuffix ?? "";
                    if (!syntax.IsComplete || syntax.Filenames.Count != 1 || syntax.Filenames[0] != filename
                        || image?.MayContainNestedImageReferences == true || !WmlResourceReferenceSyntax.ParseBinaryFilename(filename, cancellationToken).IsComplete
                        || kind == WmlResourceFieldKind.Sound && value != filename || contribution.CompositeAssignmentKeys.Count != 0
                        || origin.Text.Trim() != field.ValueText?.Trim() || !HasDirectAssignment(piece, origin, entry.Key))
                    { Issue("materialization.resource-shape", value, entry.Key, "Local resource '" + value + "' in " + entry.Key + " needs one direct mapped literal image or sound; lists, generated values and nested assets require later materialization.", origin); continue; }
                    var member = WccStaticSourceCapture.FindCapturedResourceMember(resourceCapture, resourceRevision, folder + "/" + filename);
                    if (member is null || member.IsCore || member.RootPath != resourceRoot)
                    { Issue("materialization.resource-member", filename, entry.Key, "Local resource '" + filename + "' in " + entry.Key + " is not one exact captured source package member.", origin); continue; }
                    var extension = Path.GetExtension(member.PhysicalPath).ToLowerInvariant();
                    if (extension.Length < 2 || extension.Length > 12 || !extension[1..].All(char.IsAsciiLetterOrDigit))
                    { Issue("materialization.resource-extension", filename, entry.Key, "The local resource needs a portable literal file extension.", origin); continue; }
                    var captured = WccTextDiskRevisionCapture.CaptureBinary(resourceRoot, member.PhysicalPath);
                    if (!captured.IsCaptured)
                    { Issue("materialization.resource-bytes", filename, entry.Key, "Cannot capture local resource '" + filename + "': " + captured.ErrorMessage, origin); continue; }
                    if (!transfers.Any(item => item.Origin == origin && item.Field == entry.Key))
                        transfers.Add(new(folder, filename, entry.Key, origin, suffix, member, captured.Revision!, piece));
                }
        }
        if (transfers.Select(item => (item.Folder, item.SourceRevision.RawByteSha256, Path.GetExtension(item.SourceRevision.SourcePath).ToLowerInvariant())).Distinct().Count() > MaximumResources
            || transfers.GroupBy(item => item.SourceRevision.SourcePath, StringComparer.OrdinalIgnoreCase).Sum(group => group.First().SourceRevision.RawByteLength) > 64L * 1024 * 1024)
            Issue("materialization.resource-limit", source.Id, "resources", "This copy exceeds the checked batch's 63 resources or 64 MiB binary limit.");
        draft = new(source, managedCapture, helpers, transfers, issues, registries: registries, coreCampaignHelpers: external);
        preview = BuildCandidate(draft, null, null, preview.Source.Path);
        var dependencies = WccUnitTypeDependencyPlanner.ProbeMaterialization(source, managedCapture, context, preview, cancellationToken);
        return new(source, managedCapture, helpers, transfers, issues.Concat(dependencies.Issues), dependencies, registries, external);
    }

    public static void ValidateSpec(WccUnitTypeCreationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        WccUnitTypeCreationPlanner.ValidateSpec(spec with { Materialization = null });
        if (spec.Materialization is not { IsComplete: true } plan || spec.Copy is null || !SameReceipt(plan.OriginalSource, spec.Copy))
            throw new ArgumentException("Choose one complete fresh materialization plan for this exact authored copy.", nameof(spec));
        if (plan.OriginalSource.IsCore && (plan.Helpers.Count != 0 || plan.Registries.Count != 0 || plan.ResourceTransfers.Count != 0))
            throw new ArgumentException("Core dependencies remain references.", nameof(spec));
        ValidateBinaryDestinationGroups(spec);
    }

    public static string Render(WccUnitTypeCreationSpec spec, string textDomain)
    { ValidateSpec(spec); return BuildCandidate(spec.Materialization!, spec, textDomain, Path.Combine(spec.Copy!.RootPath, "__wcc_materialization_render.cfg"), shared: true).Source.Text; }

    /// <summary>One editable campaign definition per helper, shared by all unit consumers.</summary>
    public static IReadOnlyList<WmlStaticSource> PlanHelperSources(WccUnitTypeCreationSpec spec, string projectRoot, string textDomain)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Materialization is null) return [];
        ValidateSpec(spec);
        var root = Path.GetFullPath(projectRoot);
        return Array.AsReadOnly(spec.Materialization.Helpers.Select(helper => BuildCandidate(spec.Materialization, spec, textDomain,
            Path.Combine(root, "utils", HelperFileName(spec.Materialization, helper)), shared: true, selectedHelper: helper.Name).Source)
            .Concat(GetRegistrySources(spec.Materialization.Registries, root, textDomain)).ToArray());
    }

    internal static IReadOnlyList<WmlStaticSource> PlanMissingHelperSources(WccUnitTypeCreationEdit edit,
        IEnumerable<WmlStaticSource> existingSources)
    {
        var existing = existingSources.ToArray();
        var planned = PlanHelperSources(edit.Spec, Path.GetDirectoryName(edit.Source.Path)!, edit.Snapshot.TextDomain);
        var missing = new List<WmlStaticSource>();
        foreach (var helper in planned)
        {
            var matches = existing.Where(source => SamePath(source.Path, helper.Path)).ToArray();
            if (matches.Length == 0) missing.Add(helper);
            else if (matches.Length != 1 || matches[0].Path != helper.Path || matches[0].Text != helper.Text
                || matches[0].ContentHash != helper.ContentHash)
                throw new InvalidOperationException("Shared helper '" + Path.GetFileName(helper.Path)
                    + "' already exists with different content or case. Keep the campaign definition or choose a distinct Library helper name.");
        }
        return Array.AsReadOnly(missing.ToArray());
    }

    internal static IReadOnlyList<WmlStaticSource> PlanMissingHelperSources(WccUnitTypeCreationEdit edit,
        WccLibrarySnapshot snapshot, WccPackageRevisionId revision)
    {
        var planned = PlanHelperSources(edit.Spec, Path.GetDirectoryName(edit.Source.Path)!, edit.Snapshot.TextDomain);
        var existing = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == revision && file.StoragePath is not null).ToArray();
        return Array.AsReadOnly(planned.Where(helper =>
        {
            var matches = existing.Where(file => SamePath(file.StoragePath!, helper.Path)).ToArray();
            if (matches.Length == 0) return true;
            if (matches.Length != 1 || matches[0].StoragePath != helper.Path || matches[0].ContentHash != helper.ContentHash)
                throw new InvalidOperationException("Shared helper '" + Path.GetFileName(helper.Path)
                    + "' already exists with different content or case. Keep the campaign definition or choose a distinct Library helper name.");
            return false;
        }).ToArray());
    }

    private static string HelperFileName(WccUnitTypeMaterializationPlan plan, WccContentHelperReceipt helper)
    {
        var kind = WccManagedLibraryFileStore.IsPathForKind(plan.OriginalSource.RootPath, WccManagedLibraryFileKind.Ability, helper.Source.Path)
            ? "abilities" : WccManagedLibraryFileStore.IsPathForKind(plan.OriginalSource.RootPath, WccManagedLibraryFileKind.WeaponSpecial, helper.Source.Path)
                ? "specials" : "macros";
        if (kind == "macros" && ExternalHelper(plan, helper) is not null)
        {
            var fingerprint = WmlMacroDefinitionBodyAnalyzer.Analyze(helper.Definition.Text);
            var registry = new WccMacroRegistry();
            var entry = registry.GetOrCreate(helper.Name, new(WccMacroScopeKind.CoreCampaign, null));
            entry.DefinitionOccurrences.Add(new(helper.Name, helper.Source.Path, Path.GetFileName(helper.Source.Path),
                helper.Definition.Text, 0, 0, fingerprint.DirectTagNames, fingerprint.DirectInvokedMacroNames));
            var classification = WccMacroCatalogBuilder.Build(registry).Single();
            if (classification.PurposeConfidence != WccMacroPurposeConfidenceKind.Low)
                kind = classification.PurposeKind switch
                { WccMacroPurposeKind.Ability => "abilities", WccMacroPurposeKind.WeaponSpecial => "specials", _ => "macros" };
        }
        return kind + "_" + helper.Name.Replace(':', '_') + ".cfg";
    }

    internal static WmlStaticSource RenderUpdateHelper(WccContentHelperReceipt helper, string libraryRoot, string projectRoot, string domain)
        => RenderUpdateHelper(helper, libraryRoot, projectRoot, domain, []);

    internal static WmlStaticSource RenderUpdateHelper(WccContentHelperReceipt helper, string libraryRoot, string projectRoot, string domain,
        IReadOnlyList<WccContentResourceTransferReceipt> resources)
        => RenderUpdateHelperCandidate(helper, libraryRoot, projectRoot, domain, resources).Source;

    internal static WccMaterializationCandidate RenderUpdateHelperCandidate(WccContentHelperReceipt helper, string libraryRoot,
        string projectRoot, string domain, IReadOnlyList<WccContentResourceTransferReceipt> resources)
    {
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is { } problem) throw new ArgumentException(problem);
        var captured = CaptureHelper(helper.Source, out var reason);
        if (captured is null || captured.Definition != helper.Definition || captured.ContentFingerprint != helper.ContentFingerprint)
            throw new InvalidOperationException("The captured helper definition changed: " + reason);
        var kind = WccManagedLibraryFileStore.IsPathForKind(libraryRoot, WccManagedLibraryFileKind.Ability, helper.Source.Path)
            ? "abilities" : WccManagedLibraryFileStore.IsPathForKind(libraryRoot, WccManagedLibraryFileKind.WeaponSpecial, helper.Source.Path)
                ? "specials" : "macros";
        return RenderSharedHelper(helper, libraryRoot,
            Path.Combine(projectRoot, "utils", kind + "_" + helper.Name.Replace(':', '_') + ".cfg"), domain,
            resources, resource => GetHelperResourceRelativePath(helper, resources, resource));
    }

    public static string GetResourceRelativePath(WccUnitTypeCreationSpec spec, WccContentResourceTransferReceipt transfer)
    {
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(spec.Id) is not null) throw new ArgumentException("Use a portable unit ID.", nameof(spec));
        var helper = spec.Materialization?.Helpers.FirstOrDefault(item => SamePiece(item.Definition, transfer.Piece));
        if (helper is not null) return GetHelperResourceRelativePath(helper, spec.Materialization!.ResourceTransfers, transfer);
        var category = transfer.Folder == "sounds" ? "units"
            : transfer.Field is "profile" or "small_profile" or "portrait" ? "portraits" : "units";
        string Category(WccContentResourceTransferReceipt item) => item.Folder == "sounds" ? "units"
            : spec.Materialization?.Helpers.Any(candidate => SamePiece(candidate.Definition, item.Piece)) == true ? "abilities"
            : item.Field is "profile" or "small_profile" or "portrait" ? "portraits" : "units";
        var groups = spec.Materialization!.ResourceTransfers.Where(item => item.Folder == transfer.Folder && Category(item) == category
                && !spec.Materialization.Helpers.Any(candidate => SamePiece(candidate.Definition, item.Piece)))
            .Select(item => (Hash: item.SourceRevision.RawByteSha256, Extension: Path.GetExtension(item.SourceRevision.SourcePath).ToLowerInvariant()))
            .Distinct().OrderBy(item => item.Hash, StringComparer.Ordinal).ThenBy(item => item.Extension, StringComparer.Ordinal).ToArray();
        var extension = Path.GetExtension(transfer.SourceRevision.SourcePath).ToLowerInvariant();
        var ordinal = Array.IndexOf(groups, (transfer.SourceRevision.RawByteSha256, extension));
        return WccUnitResourcePathPolicy.GetRelativePath(transfer.Folder, category, spec.Id, extension, ordinal + 1);
    }

    public static IReadOnlyList<WccContentBinaryCreation> PlanBinaries(WccUnitTypeCreationSpec spec, string projectRoot)
    {
        return PlanBinaryDestinations(spec, projectRoot).Creates;
    }

    public static string? VerifySourceFreshness(WccUnitTypeMaterializationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (WccUnitTypeAuthoredCopyPlanner.VerifySourceFreshness(plan.OriginalSource) is { } error) return error;
        if (!plan.OriginalSource.IsCore && WccStaticSourceCapture.VerifyCapturedRootInventory(plan.Capture, plan.OriginalSource.RevisionId) is { } inventoryError)
            return "The captured Library helper/resource inventory changed: " + inventoryError;
        if (VerifyCoreCampaignHelpers(plan) is { } externalError) return externalError;
        foreach (var helper in plan.Helpers.DistinctBy(item => item.Source.Path))
        {
            var captured = WccTextDiskRevisionCapture.Capture(ExternalHelper(plan, helper)?.Source.RootPath ?? plan.OriginalSource.RootPath, helper.Source.Path, helper.Source.Text);
            if (!captured.IsCaptured) return "Helper {" + helper.Name + "} changed or cannot be verified: " + captured.ErrorMessage;
        }
        foreach (var registry in plan.Registries)
        {
            var captured = WccTextDiskRevisionCapture.Capture(plan.OriginalSource.RootPath, registry.Source.Path, registry.Source.Text);
            if (!captured.IsCaptured) return "Registry entry '" + registry.Name + "' changed or cannot be verified: " + captured.ErrorMessage;
        }
        foreach (var resource in plan.ResourceTransfers.DistinctBy(item => item.SourceRevision.SourcePath))
        {
            var captured = WccTextDiskRevisionCapture.CaptureBinary(resource.SourceRevision.ProjectRootPath, resource.SourceRevision.SourcePath);
            if (!captured.IsCaptured || !resource.SourceRevision.IsSameRevisionAs(captured.Revision!))
                return "Resource '" + resource.Filename + "' changed or cannot be verified: " + captured.ErrorMessage;
        }
        return null;
    }

    public static void ValidateSourceEvidence(WccLibrarySnapshot snapshot, WccUnitTypeMaterializationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(plan);
        WccUnitTypeAuthoredCopyPlanner.ValidateSourceEvidence(snapshot, plan.OriginalSource);
        foreach (var registry in plan.Registries)
        {
            var files = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == plan.OriginalSource.RevisionId
                && file.StoragePath is { } path && SamePath(path, registry.Source.Path) && file.ContentHash == registry.Source.ContentHash).ToArray();
            var captured = CaptureRegistry(registry.Source, out _);
            if (files.Length != 1 || !Inside(plan.OriginalSource.RootPath, registry.Source.Path) || captured is null
                || captured.WrapperRange != registry.WrapperRange || captured.ContentFingerprint != registry.ContentFingerprint
                || captured.Name != registry.Name || captured.Kind != registry.Kind)
                throw new InvalidOperationException("The exact manifest/source evidence for registry '" + registry.Name + "' changed.");
        }
        foreach (var helper in plan.Helpers)
        {
            if (ExternalHelper(plan, helper) is { } original)
            { ValidateCoreCampaignHelper(snapshot, original); continue; }
            var files = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == plan.OriginalSource.RevisionId
                && file.StoragePath is { } path && SamePath(path, helper.Source.Path) && file.ContentHash == helper.Source.ContentHash).ToArray();
            var captured = CaptureHelper(helper.Source, out _);
            if (files.Length != 1 || !Inside(plan.OriginalSource.RootPath, helper.Source.Path) || captured is null
                || captured.Definition != helper.Definition || captured.ContentFingerprint != helper.ContentFingerprint || captured.TextDomain != helper.TextDomain)
                throw new InvalidOperationException("The exact manifest/source evidence for helper {" + helper.Name + "} changed.");
        }
        foreach (var resource in plan.ResourceTransfers)
        {
            var owner = plan.Helpers.FirstOrDefault(helper => helper.Definition == resource.Piece);
            var original = owner is null ? null : ExternalHelper(plan, owner);
            var root = original?.Source.RootPath ?? plan.OriginalSource.RootPath;
            var member = WccStaticSourceCapture.FindCapturedResourceMember(original?.Capture ?? plan.Capture,
                original?.Source.RevisionId ?? plan.OriginalSource.RevisionId, resource.CapturedMember.RelativePath);
            if (member != resource.CapturedMember || member is null || member.IsCore || member.RootPath != root
                || member.PhysicalPath != resource.SourceRevision.SourcePath || resource.SourceRevision.ProjectRootPath != root)
                throw new InvalidOperationException("The exact Library membership for resource '" + resource.Filename + "' changed.");
        }
    }

    /// <summary>Names only blocking diagnostics mapped inside an exact generated conflict guard.</summary>
    public static WccContentDependencyIssue? FindHelperConflict(WccUnitTypeMaterializationPlan plan,
        WmlStaticSource candidateSource, WmlStaticEvaluationResult evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var candidate = ValidateCandidate(plan, candidateSource);
        foreach (var diagnostic in evaluation.Diagnostics.Where(item => item.IsBlocking))
            if (candidate.HelperConflict(diagnostic.Origin) is { } name)
                return new("materialization.helper-conflict", name, "macro", "Helper {" + name
                    + "} conflicts with an active definition in captured Core or the destination.", diagnostic.Origin);
        return null;
    }

    internal static WccMaterializationCandidate ValidateCandidate(WccUnitTypeMaterializationPlan plan, WmlStaticSource candidate)
    {
        if (!plan.IsComplete) throw new ArgumentException("The materialization closure is incomplete.");
        var blocks = WmlBlockScanner.FindTopLevelBlockSpans(candidate.Text).Where(item => item.TagName == "unit_type").ToArray();
        if (!candidate.IsAuthored || blocks.Length != 1) throw new ArgumentException("The materialization candidate needs one exact authored unit.");
        var block = WmlEditableBlockParser.Parse(new WmlBlock("unit_type", blocks[0].RawText), UnitTypeSchema.KnownKeys);
        var idText = block.Known.GetValueOrDefault("id")?.Value; var nameText = block.Known.GetValueOrDefault("name")?.Value;
        if (!WmlResourceValueTextDecoder.TryDecode(idText, out var id, out _)) throw new ArgumentException("The copied unit ID is not literal.");
        var name = Wcc.Core.Scenarios.WccScenarioFieldValueCodec.DecodeLiteral(nameText);
        var mode = id == plan.OriginalSource.Id ? WccUnitTypeCreationMode.MaterializeLibrary : WccUnitTypeCreationMode.CopyAuthored;
        var spec = new WccUnitTypeCreationSpec(mode, id, name) { Copy = plan.OriginalSource, Materialization = plan };
        var domains = WmlAuthoredStructureScanner.Scan(candidate.Text).Directives.Where(item => item.Name == "textdomain")
            .Select(item => WmlTextDomainCapture.BeforeOffset(candidate.Text, WmlAuthoredStructureScanner.Scan(candidate.Text).Lines[item.Line].EndOffsetExclusive))
            .Where(item => item is not null).Cast<string>().Append(plan.OriginalSource.TextDomain).Distinct(StringComparer.Ordinal);
        foreach (var domain in domains)
        {
            if (WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is not null) continue;
            var rendered = BuildCandidate(plan, spec, domain, candidate.Path, shared: true);
            if (rendered.Source.Text == candidate.Text)
            {
                var root = Path.GetDirectoryName(Path.GetDirectoryName(candidate.Path))!;
                var helpers = plan.Helpers.Select(helper => BuildCandidate(plan, spec, domain,
                    Path.Combine(root, "utils", HelperFileName(plan, helper)), shared: true, selectedHelper: helper.Name)).ToArray();
                var registrySources = GetRegistrySources(plan.Registries, root, domain);
                return rendered with { Helpers = helpers.SelectMany(helper => helper.Helpers).ToArray(),
                    Resources = rendered.Resources.Concat(helpers.SelectMany(helper => helper.Resources)).ToArray(),
                    ConflictGuards = helpers.SelectMany(helper => helper.ConflictGuards).ToArray(),
                    Registries = registrySources.Select(file => CaptureRegistry(file, out _) ?? throw new InvalidOperationException("The rendered registry recipe is invalid."))
                        .Select(receipt => (receipt.Kind, receipt.Name, receipt.EntryRange)).ToArray() };
            }
            // The detached discovery preview still keeps all helpers in one temporary source.
            rendered = BuildCandidate(plan, spec, domain, candidate.Path);
            if (rendered.Source.Text == candidate.Text) return rendered;
        }
        throw new ArgumentException("The candidate differs from its exact helper/resource materialization recipe.");
    }

    internal static WccContentHelperReceipt? CaptureHelper(WmlStaticSource source, out string reason)
    {
        reason = "A helper must contain one standalone pure #define and only comments/textdomain outside it.";
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        var starts = structure.Directives.Where(item => item.Name == "define").ToArray();
        var ends = structure.Directives.Where(item => item.Name == "enddef").ToArray();
        if (structure.Problem is not null || starts.Length != 1 || ends.Length != 1 || starts[0].DefinitionDepth != 0
            || ends[0].DefinitionDepth != 1 || ends[0].Line <= starts[0].Line) return null;
        var first = structure.Lines[starts[0].Line]; var last = structure.Lines[ends[0].Line];
        const string endMarker = "#enddef";
        var endLine = source.Text[last.StartOffset..last.ContentEndOffset].TrimStart();
        // Split the suffix only: splitting a complete preprocessor directive
        // interprets its leading '#' as a comment and erases the marker.
        if (!endLine.StartsWith(endMarker, StringComparison.Ordinal)
            || WmlInlineCommentSplitter.Split(endLine[endMarker.Length..]).Value.Trim().Length != 0) return null;
        var header = WmlInlineCommentSplitter.Split(source.Text[first.StartOffset..first.ContentEndOffset].Trim()[7..]).Value.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (header.Length == 0 || !MacroName(header[0]) || header.Skip(1).Any(parameter => !MacroName(parameter))) return null;
        var parameters = header.Skip(1).ToList();
        var requiredParameterCount = parameters.Count;
        if (!TryCaptureOptionalArguments(source, first.ContentEndOffset, last.StartOffset, parameters,
                out var optionalParameters, out var optionalDirectives, out reason)) return null;
        parameters.AddRange(optionalParameters);
        var stringState = new WmlStringScanState();
        for (var index = 0; index < structure.Lines.Count; index++)
        {
            var line = structure.Lines[index]; var raw = source.Text[line.StartOffset..line.ContentEndOffset];
            var directiveText = !stringState.InsideMultilineString && raw.TrimStart().StartsWith('#') ? raw.TrimStart() : null;
            var masked = new char[raw.Length]; WmlRawStringScanner.CopyWithoutStringContent(raw.AsSpan(), masked.AsSpan(), ref stringState);
            var visible = new string(masked).Trim();
            var inside = index >= starts[0].Line && index <= ends[0].Line;
            if (!inside && visible.Length != 0 && !visible.StartsWith('#')) return null;
            if (directiveText is null) continue;
            var directive = directiveText[1..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (directive is "define" or "enddef" or "textdomain") continue;
            if (inside && directive is "arg" or "endarg")
            { continue; }
            if (directive is "undef" or "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver" or "else" or "endif"
                or "error" or "warning" or "deprecated" or "lua" or "endarg" or "arg") return null;
        }
        var domain = WmlTextDomainCapture.BeforeOffset(source.Text, first.StartOffset);
        var controls = source.Text.ToCharArray();
        foreach (var token in optionalDirectives)
            for (var offset = token.StartOffset; offset < token.StartOffset + token.Length; offset++) controls[offset] = ' ';
        foreach (var directive in structure.Directives.Where(item => item.Name is "define" or "enddef" or "textdomain" or "arg" or "endarg"))
        {
            var line = structure.Lines[directive.Line];
            if (!source.Text[line.StartOffset..line.ContentEndOffset].TrimStart().StartsWith('#')) continue;
            for (var offset = line.StartOffset; offset < line.ContentEndOffset; offset++) controls[offset] = ' ';
        }
        if (Wcc.Core.Scenarios.WccScenarioEventSourcePlanner.HasUnsafeControls(new string(controls)))
        { reason = "Executable stateful helper directives need a separate authored closure contract."; return null; }
        if (domain is not null && WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is not null) return null;
        var range = new WmlStaticSourceRange(source, first.StartOffset, last.EndOffsetExclusive - first.StartOffset);
        if (WmlMacroInvocationSyntaxScanner.Scan(range.Text).Any(item => item.Name.Contains('/') || item.Name.Contains('\\')))
        { reason = "Include paths or computed helper activation require a later closure contract."; return null; }
        return new(header[0], source, range, domain, parameters, requiredParameterCount);
    }

    private static WccMaterializationCandidate BuildCandidate(WccUnitTypeMaterializationPlan plan,
        WccUnitTypeCreationSpec? spec, string? domain, string path, bool shared = false, string? selectedHelper = null)
    {
        if (shared && selectedHelper is not null)
        {
            var helper = plan.Helpers.Single(item => item.Name == selectedHelper);
            var original = ExternalHelper(plan, helper);
            var helperProvenance = original is null ? Array.Empty<string>() : new[]
            { "# WCC original helper: package=" + original.Source.PackageId + "; revision=" + original.Source.RevisionId + "; entity=" + original.Source.EntityId };
            return RenderSharedHelper(helper, original?.Source.RootPath ?? plan.OriginalSource.RootPath, path, domain ?? LibraryDomain,
                plan.ResourceTransfers, resource => spec is null ? resource.Folder + "/" + resource.Filename : GetResourceRelativePath(spec, resource), helperProvenance);
        }
        if (!shared && plan.Registries.Count != 0)
        {
            // Registry wrappers must run outside the selected unit's [units]
            // include, after their exact helper definitions have been loaded.
            var unit = BuildCandidate(plan, spec, domain, path, shared: true);
            var root = spec is null ? plan.OriginalSource.RootPath : Path.GetDirectoryName(Path.GetDirectoryName(path))!;
            var helpers = plan.Helpers.Select(helper => BuildCandidate(plan, spec, domain,
                Path.Combine(root, "utils", HelperFileName(plan, helper)), shared: true, selectedHelper: helper.Name)).ToArray();
            var registrySources = GetRegistrySources(plan.Registries, root, domain ?? LibraryDomain);
            return unit with { Helpers = helpers.SelectMany(helper => helper.Helpers).ToArray(),
                Resources = unit.Resources.Concat(helpers.SelectMany(helper => helper.Resources)).ToArray(),
                ConflictGuards = helpers.SelectMany(helper => helper.ConflictGuards).ToArray(),
                Registries = registrySources.Select(file => CaptureRegistry(file, out _) ?? throw new InvalidOperationException("The registry preview has no exact entry."))
                    .Select(receipt => (receipt.Kind, receipt.Name, receipt.EntryRange)).ToArray() };
        }
        if (domain is not null && WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is { } issue) throw new ArgumentException(issue);
        var text = new StringBuilder(); var helperRanges = new List<(string Name, int Start, int Length)>();
        var provenance = new List<string>();
        var conflictGuards = new List<(string Name, int Start, int Length)>();
        var resourceRanges = new List<(string Field, string Filename, int Start, int Length)>();
        void AppendPiece(WmlStaticSourceRange piece, string rendered, string? helperName)
        {
            var patches = new List<(int Start, int Length, string Value, WccContentResourceTransferReceipt Receipt)>();
            foreach (var resource in plan.ResourceTransfers.Where(item => item.Piece == piece))
            {
                var offset = LocateValue(piece, resource.Origin, rendered);
                var filename = spec is null ? resource.Filename : GetResourceRelativePath(spec, resource)[(resource.Folder.Length + 1)..];
                var value = spec is null ? resource.Origin.Text : resource.Origin.Text.TrimStart().StartsWith('"')
                    ? WccCreationSourceWriter.EncodeText(filename + resource.ModifierSuffix) : filename + resource.ModifierSuffix;
                patches.Add((offset, resource.Origin.Length, value, resource));
            }
            foreach (var patch in patches.OrderByDescending(item => item.Start)) rendered = rendered.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.Value);
            var start = text.Length; text.Append(rendered); if (!rendered.EndsWith('\n')) text.Append('\n');
            if (helperName is not null) helperRanges.Add((helperName, start, text.Length - start));
            foreach (var patch in patches)
            {
                var shifted = patch.Start + patches.Where(other => other.Start < patch.Start).Sum(other => other.Value.Length - other.Length);
                var filename = spec is null ? patch.Receipt.Filename : GetResourceRelativePath(spec, patch.Receipt)[(patch.Receipt.Folder.Length + 1)..];
                resourceRanges.Add((patch.Receipt.Field, filename, start + shifted, patch.Value.Length));
            }
        }
        foreach (var helper in plan.Helpers.Where(helper => !shared || selectedHelper == helper.Name))
        {
            text.Append("#ifdef ").Append(helper.Name).Append('\n');
            var guardStart = text.Length;
            text.Append("#error WCC copied helper ").Append(helper.Name).Append(" conflicts with an active definition\n");
            conflictGuards.Add((helper.Name, guardStart, text.Length - guardStart));
            text.Append("#endif\n#textdomain ")
                .Append(helper.TextDomain is null or LibraryDomain ? domain ?? LibraryDomain : helper.TextDomain).Append('\n');
            provenance.Add("# WCC helper source: " + SafeComment(Path.GetRelativePath(ExternalHelper(plan, helper)?.Source.RootPath ?? plan.OriginalSource.RootPath, helper.Source.Path).Replace('\\', '/')));
            provenance.Add("# WCC helper SHA256: " + helper.ContentFingerprint);
            if (ExternalHelper(plan, helper) is { } original)
                provenance.Add("# WCC original helper: package=" + original.Source.PackageId + "; revision=" + original.Source.RevisionId
                    + "; entity=" + original.Source.EntityId);
            AppendResourceProvenance(helper.Definition);
            AppendPiece(helper.Definition, domain is null ? helper.Definition.Text : WccUnitTypeAuthoredCopyPlanner.MapLibraryTextDomains(helper.Definition.Text, domain), helper.Name);
        }
        if (selectedHelper is null)
        {
            var unit = new WmlStaticSourceRange(plan.OriginalSource.Source, plan.OriginalSource.StartOffset, plan.OriginalSource.Length);
            var renderedUnit = spec is null ? WccUnitTypeAuthoredCopyPlanner.RenderOriginalContent(plan.OriginalSource)
                : WccUnitTypeAuthoredCopyPlanner.RenderCopyContent(plan.OriginalSource, spec.Id, spec.Name, domain);
            provenance.AddRange(WccUnitTypeAuthoredCopyPlanner.Provenance(plan.OriginalSource));
            AppendResourceProvenance(unit);
            AppendPiece(unit, renderedUnit, null);
        }
        if (!shared) foreach (var helper in plan.Helpers) text.Append("#undef ").Append(helper.Name).Append('\n');
        var candidate = new WmlStaticSource(path, WccCopiedContentProvenance.Append(text.ToString(), provenance));
        return new(candidate, Array.AsReadOnly(helperRanges.Select(item => (item.Name, new WmlStaticSourceRange(candidate, item.Start, item.Length))).ToArray()),
            Array.AsReadOnly(resourceRanges.Select(item => new WccMaterializationResourceBinding(item.Field, item.Filename,
                new(candidate, item.Start, item.Length))).ToArray()),
            Array.AsReadOnly(conflictGuards.Select(item => (item.Name, new WmlStaticSourceRange(candidate, item.Start, item.Length))).ToArray()));

        void AppendResourceProvenance(WmlStaticSourceRange piece)
        {
            foreach (var resource in plan.ResourceTransfers.Where(item => SameSource(item.Piece.Source, piece.Source)
                && item.Piece.StartOffset == piece.StartOffset && item.Piece.Length == piece.Length)
                .DistinctBy(item => (item.CapturedMember.RelativePath, item.SourceRevision.RawByteSha256,
                    Destination: spec is null ? item.Folder + "/" + item.Filename : GetResourceRelativePath(spec, item))))
                provenance.Add("# WCC resource source: " + SafeComment(resource.CapturedMember.RelativePath) + " -> "
                    + SafeComment(spec is null ? resource.Folder + "/" + resource.Filename : GetResourceRelativePath(spec, resource))
                    + "; byte SHA256: " + resource.SourceRevision.RawByteSha256);
        }
    }

    private static bool WithinCopiedRegistry(WmlStaticScalarOwner owner, WmlStaticSourceRange entry)
    {
        for (WmlStaticScalarOwner? current = owner; current is not null; current = current.Parent)
            if (current.OpeningOrigin is { } opening && current.ClosingOrigin is { } closing
                && SameSource(opening.Source, entry.Source) && SameSource(closing.Source, entry.Source)
                && opening.StartOffset >= entry.StartOffset && closing.StartOffset + closing.Length <= entry.StartOffset + entry.Length)
                return true;
        return false;
    }

    private static bool TryOriginalOrigin(WccUnitTypeMaterializationPlan plan, WccMaterializationCandidate preview,
        WmlStaticSourceRange generated, out WmlStaticSourceRange origin, out WmlStaticSourceRange piece)
    {
        foreach (var helper in plan.Helpers)
        {
            var range = preview.Helpers.First(item => item.Name == helper.Name).Range;
            if (SameSource(generated.Source, range.Source) && generated.StartOffset >= range.StartOffset && generated.StartOffset + generated.Length <= range.StartOffset + range.Length)
            { piece = helper.Definition; origin = new(piece.Source, piece.StartOffset + generated.StartOffset - range.StartOffset, generated.Length); return true; }
        }
        var blocks = WmlBlockScanner.FindTopLevelBlockSpans(preview.Source.Text).Where(item => item.TagName == "unit_type").ToArray();
        if (SameSource(generated.Source, preview.Source) && blocks.Length == 1 && generated.StartOffset >= blocks[0].StartIndex && generated.StartOffset + generated.Length <= blocks[0].EndIndexExclusive)
        { piece = new(plan.OriginalSource.Source, plan.OriginalSource.StartOffset, plan.OriginalSource.Length);
            origin = new(piece.Source, piece.StartOffset + generated.StartOffset - blocks[0].StartIndex, generated.Length); return true; }
        origin = null!; piece = null!; return false;
    }

    private static int LocateValue(WmlStaticSourceRange piece, WmlStaticSourceRange origin, string rendered)
    {
        var structure = WmlAuthoredStructureScanner.Scan(piece.Text); var relative = origin.StartOffset - piece.StartOffset;
        var lineIndex = Enumerable.Range(0, structure.Lines.Count).Single(index => relative >= structure.Lines[index].StartOffset
            && relative + origin.Length <= structure.Lines[index].ContentEndOffset);
        var line = structure.Lines[lineIndex]; var rawLine = piece.Text[line.StartOffset..line.ContentEndOffset];
        var ordinal = structure.Lines.Take(lineIndex).Count(item => piece.Text[item.StartOffset..item.ContentEndOffset] == rawLine);
        var renderedLines = WmlAuthoredStructureScanner.Scan(rendered).Lines.Where(item => rendered[item.StartOffset..item.ContentEndOffset] == rawLine).ToArray();
        if (renderedLines.Length != structure.Lines.Count(item => piece.Text[item.StartOffset..item.ContentEndOffset] == rawLine))
            throw new InvalidOperationException("A resource assignment's exact authored line changed during materialization.");
        return renderedLines[ordinal].StartOffset + relative - line.StartOffset;
    }

    private static bool HasDirectAssignment(WmlStaticSourceRange piece, WmlStaticSourceRange origin, string field)
    {
        var relative = origin.StartOffset - piece.StartOffset;
        foreach (var line in WmlAuthoredStructureScanner.Scan(piece.Text).Lines)
            if (relative >= line.StartOffset && relative + origin.Length <= line.ContentEndOffset)
            {
                var prefix = piece.Text[line.StartOffset..relative]; var equals = prefix.IndexOf('=');
                return equals >= 0 && prefix[..equals].Trim() == field && prefix[(equals + 1)..].Trim().Length == 0;
            }
        return false;
    }
    private static bool MacroName(string name) => name.Length is > 0 and <= 64 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static string SafeComment(string value) => value.Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);
    internal static bool SameReceipt(WccUnitTypeAuthoredCopySource left, WccUnitTypeAuthoredCopySource right) =>
        left.EntityId == right.EntityId && left.RevisionId == right.RevisionId && left.StartOffset == right.StartOffset && left.Length == right.Length
        && left.ContentFingerprint == right.ContentFingerprint && SameSource(left.Source, right.Source);
    internal static bool SameSource(WmlStaticSource left, WmlStaticSource right) => SamePath(left.Path, right.Path) && left.ContentHash == right.ContentHash && left.Text == right.Text;
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Inside(string root, string path) => Path.GetRelativePath(root, path) is var relative && !Path.IsPathRooted(relative)
        && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
