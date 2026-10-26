using System.Text;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.Core.Library;

/// <summary>An exact selected authored macro, without activation or write authority.</summary>
public sealed class WccManagedMacroCopySource
{
    internal WccManagedMacroCopySource(WccLibrarySnapshot snapshot, WccEntityRecord entity,
        WccSourceFileRecord file, WccPackageRecord package, WccPackageRevisionRecord revision,
        WmlStaticSource source, WmlStaticSourceRange definition, string name, string domain)
    {
        Snapshot = snapshot; EntityId = entity.Id; SourceFileId = file.Id; PackageId = package.Id;
        RevisionId = revision.Id; Source = source; Definition = definition; Name = name;
        TextDomain = domain; RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SourceLocation!));
        ContentFingerprint = WccContentHash.ComputeText(definition.Text);
        ManifestIdentity = WccManagedMacroCopyPlanner.ManifestIdentity(snapshot);
    }
    internal WccLibrarySnapshot Snapshot { get; }
    internal string ManifestIdentity { get; }
    public WccEntityId EntityId { get; }
    public WccSourceFileId SourceFileId { get; }
    public WccPackageId PackageId { get; }
    public WccPackageRevisionId RevisionId { get; }
    public WmlStaticSource Source { get; }
    public WmlStaticSourceRange Definition { get; }
    public string Name { get; }
    public string TextDomain { get; }
    public string RootPath { get; }
    public string ContentFingerprint { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>Immutable selected definition, pure helper and exact byte copy recipe.</summary>
public sealed class WccManagedMacroCopyPlan
{
    internal WccManagedMacroCopyPlan(WccManagedMacroCopySource source, WccStaticSourceCaptureResult capture,
        string root, WccPackageId package, WccPackageRevisionId revision, WccManagedLibraryFileKind kind,
        string raw, IEnumerable<WmlStaticSource> sources, IEnumerable<WccContentHelperReceipt> helpers,
        IEnumerable<WccContentBinaryCreation> binaries, IEnumerable<string> issues)
    {
        OriginalSource = source; Capture = capture; DestinationRoot = root; DestinationPackageId = package;
        DestinationRevisionId = revision; Kind = kind; CandidateRawText = raw;
        Sources = Array.AsReadOnly(sources.ToArray()); Helpers = Array.AsReadOnly(helpers.ToArray());
        Binaries = Array.AsReadOnly(binaries.ToArray()); Issues = Array.AsReadOnly(issues.Distinct(StringComparer.Ordinal).ToArray());
        Identity = WccContentHash.ComputeText(source.ManifestIdentity + "\n" + source.ContentFingerprint + "\n"
            + root + "\n" + package + "\n" + revision + "\n" + kind + "\n" + raw + "\n"
            + string.Join("\n", Sources.Select(item => item.Path + "|" + item.ContentHash)) + "\n"
            + string.Join("\n", Binaries.Select(item => item.DestinationPath + "|" + item.SourceRevision.RawByteSha256)));
    }
    public WccManagedMacroCopySource OriginalSource { get; }
    public WccStaticSourceCaptureResult Capture { get; }
    public string DestinationRoot { get; }
    public WccPackageId DestinationPackageId { get; }
    public WccPackageRevisionId DestinationRevisionId { get; }
    public WccManagedLibraryFileKind Kind { get; }
    public string CandidateRawText { get; }
    public IReadOnlyList<WmlStaticSource> Sources { get; }
    public WmlStaticSource PrimarySource => Sources[0];
    public IReadOnlyList<WccContentHelperReceipt> Helpers { get; }
    public IReadOnlyList<WccContentBinaryCreation> Binaries { get; }
    public IReadOnlyList<string> Issues { get; }
    public string Identity { get; }
    public bool IsComplete => Issues.Count == 0;
    public bool AuthorizesPersistence => false;
}

/// <summary>Lossless selected macro copying. No whole source-package activation or early writes.</summary>
public static class WccManagedMacroCopyPlanner
{
    /// <summary>Display eligibility only; selected source and closure capture remain required.</summary>
    public static bool IsSupportedSourcePackage(WccPackageRecord package)
    { ArgumentNullException.ThrowIfNull(package); return AllowedSource(package); }

    public static WccManagedMacroCopySource CaptureSource(WccLibrarySnapshot snapshot, WccEntityId entityId,
        WmlStaticSource source)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(source);
        if (!snapshot.EntitiesById.TryGetValue(entityId, out var entity)
            || entity.EntityType != WccEntityTypes.MacroDefinition || entity.ParentEntityId is not null
            || !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file)
            || !snapshot.RevisionsById.TryGetValue(file.PackageRevisionId, out var revision)
            || !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !AllowedSource(package)
            || !source.IsAuthored || !SamePath(file.StoragePath, source.Path) || file.ContentHash != source.ContentHash
            || entity.SourceSpan?.StartOffset is not int start || entity.SourceSpan.EndOffsetExclusive is not int end
            || start < 0 || end <= start || end > source.Text.Length || !Inside(package.SourceLocation!, source.Path))
            throw new ArgumentException("Choose one exact indexed authored Core, CoreCampaign or WCC Library macro.", nameof(entityId));
        var range = new WmlStaticSourceRange(source, start, end - start);
        var helper = CaptureDefinition(source, range, out var reason);
        if (helper is null || helper.Name != entity.ExportSymbol || entity.ContentFingerprint != WccContentHash.ComputeText(range.Text))
            throw new ArgumentException("The selected macro span or fingerprint cannot be detached: " + reason, nameof(entityId));
        if (snapshot.FindDefinitions(revision.Id, WccEntityTypes.MacroDefinition, helper.Name).Count != 1
            || helper.TextDomain is null)
            throw new ArgumentException("Choose a unique selected macro with an explicit lexical textdomain.", nameof(entityId));
        return new(snapshot, entity, file, package, revision, source, range, helper.Name, helper.TextDomain);
    }

    public static WccManagedMacroCopyPlan Plan(WccManagedMacroCopySource source,
        WccStaticSourceCaptureResult capture, string destinationRoot, WccManagedLibraryFileKind managedKind,
        string candidateRawText)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(candidateRawText);
        ValidateSource(source.Snapshot, source);
        if (managedKind is not (WccManagedLibraryFileKind.Macro or WccManagedLibraryFileKind.Ability
                or WccManagedLibraryFileKind.WeaponSpecial or WccManagedLibraryFileKind.Schedule))
            throw new ArgumentException("This copy adapter accepts macro-backed managed Library kinds only.", nameof(managedKind));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var targets = source.Snapshot.PackagesById.Values.Where(package => package.IsAvailable
            && package.SourceKind == WccPackageSourceKinds.WccManagedLibrary && SamePath(package.SourceLocation, root)).ToArray();
        if (targets.Length != 1) throw new ArgumentException("Choose one exact available managed Library root.", nameof(destinationRoot));
        if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(targets[0].SourceLocation!)) != root)
            throw new ArgumentException("Use the exact captured managed Library root spelling.", nameof(destinationRoot));
        if (targets[0].CurrentRevisionId is not { } currentRevisionId
            || !source.Snapshot.RevisionsById.TryGetValue(currentRevisionId, out var currentRevision)
            || currentRevision.PackageId != targets[0].Id)
            throw new ArgumentException("The managed destination needs a valid explicitly selected current revision.", nameof(destinationRoot));
        var issues = new List<string>(); var helpers = new List<WccContentHelperReceipt>();
        var binaries = new List<WccContentBinaryCreation>();
        var rawStructure = WmlAuthoredStructureScanner.Scan(candidateRawText);
        var defineDirective = rawStructure.Directives.FirstOrDefault(directive => directive.Name == "define");
        var defineOffset = rawStructure.Lines.Count == 0 ? 0 : rawStructure.Lines[defineDirective.Line].StartOffset;
        var domainPrefix = WmlTextDomainCapture.BeforeOffset(candidateRawText, defineOffset) is null
            ? "#textdomain " + source.TextDomain + "\n" : "";
        var primary = new WmlStaticSource(Path.Combine(WccManagedLibraryFileStore.GetKindDirectory(root, managedKind), "__copy.cfg"),
            domainPrefix + candidateRawText);
        var primaryHelper = WccUnitTypeMaterializationPlanner.CaptureHelper(primary, out var primaryReason);
        if (primaryHelper is null || primaryHelper.RequiredParameterCount != 0)
            throw new ArgumentException("Clone needs one standalone pure macro with no required arguments: " + primaryReason, nameof(candidateRawText));
        var name = primaryHelper.Name;
        if (!Portable(name)) throw new ArgumentException("The copied macro name must be a portable identifier.", nameof(candidateRawText));
        primary = new(Path.Combine(WccManagedLibraryFileStore.GetKindDirectory(root, managedKind), name + ".cfg"), primary.Text);
        primaryHelper = WccUnitTypeMaterializationPlanner.CaptureHelper(primary, out _)!;
        if (!capture.IsCaptureComplete || !capture.HasCompleteCoreUnitPrelude || capture.IncludeResolver is not IWmlStaticResourceResolver resources
            || capture.Sources.Count(file => SameSource(file, source.Source)) != 1)
            issues.Add("Macro copying needs the exact selected physical file and complete pinned Core capture.");
        var coreEntries = capture.EntrySource is null ? [] : capture.IncludeResolver.Resolve("core/units.cfg", capture.EntrySource).Sources.ToArray();
        if (coreEntries.Length != 1) issues.Add("Macro copying needs one exact captured Core units entry.");
        var coreRoot = coreEntries.Length == 1 ? Path.GetDirectoryName(coreEntries[0].Path)! : "";
        var coreNames = capture.Sources.Where(file => coreRoot.Length != 0 && Inside(coreRoot, file.Path))
            .SelectMany(file => WmlMacroDefinitionScanner.Scan(file.Text)).Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        if (coreNames.Contains(name)) issues.Add("Copied macro {" + name + "} conflicts with a captured Core definition; choose another name.");
        var inventory = capture.Sources.Where(file => Inside(source.RootPath, file.Path) && !SamePath(source.RootPath, coreRoot))
            .SelectMany(Definitions).GroupBy(helper => helper.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal) { name }; var done = new HashSet<string>(StringComparer.Ordinal);
        void Visit(WccContentHelperReceipt current, int depth)
        {
            foreach (var invocation in WmlMacroInvocationSyntaxScanner.Scan(current.Definition.Text))
            {
                if (current.Parameters.Contains(invocation.Name, StringComparer.Ordinal)) continue;
                var local = inventory.GetValueOrDefault(invocation.Name);
                // A campaign-owned definition cannot silently borrow a coincident Core name.
                if (local is not null && coreNames.Contains(invocation.Name))
                { issues.Add("Helper {" + invocation.Name + "} exists in both source package and Core; its binding is ambiguous."); continue; }
                if (local is null && coreNames.Contains(invocation.Name)) continue;
                if (local is null || local.Length != 1)
                { issues.Add("Helper {" + invocation.Name + "} needs one exact pure source-package definition; includes, dynamic names and external helpers cannot be copied."); continue; }
                if (active.Contains(invocation.Name) || depth >= 7)
                { issues.Add("Helper {" + invocation.Name + "} has a cyclic or over-eight-file closure."); continue; }
                if (!done.Add(invocation.Name)) continue;
                if (local[0].RequiredParameterCount != 0 || !Portable(invocation.Name))
                { issues.Add("Helper {" + invocation.Name + "} is parameterized or has a nonportable name; this copy requires a pure parameterless helper."); continue; }
                helpers.Add(local[0]); active.Add(invocation.Name); Visit(local[0], depth + 1); active.Remove(invocation.Name);
            }
        }
        Visit(primaryHelper, 0);
        var originalInputs = new[] { primary }.Concat(helpers.Select(RenderHelper)).ToArray();
        var evaluated = Evaluate(capture, originalInputs, name, managedKind);
        if (!evaluated.IsComplete) issues.AddRange(evaluated.Diagnostics.Where(item => item.IsBlocking).Select(item => item.Message));
        var rewrites = new Dictionary<string, List<(int Start, int Length, string Value)>>(StringComparer.OrdinalIgnoreCase);
        var assetSlots = new Dictionary<(string Owner, string Folder), List<(string Hash, string Extension, string Relative)>>() ;
        if (evaluated.IsComplete && capture.IncludeResolver is IWmlStaticResourceResolver resourceResolver)
        {
            var projection = WmlStaticScalarProjection.Create(evaluated);
            if (projection.Diagnostics.Count != 0) issues.Add("The selected macro expansion is malformed or has unproved scalar owners.");
            if (ExpansionBoundary(projection) is { } escaped) issues.Add(escaped);
            foreach (var owner in projection.Owners)
            {
                if (!IsSelectedExpansionOwner(owner)) continue;
                foreach (var entry in owner.Fields)
                {
                    var kind = WmlResourceFieldCatalog.Find(owner, entry.Key);
                    if (kind == WmlResourceFieldKind.Lua)
                    { issues.Add("Macro {" + name + "} contains Lua; its runtime resource closure needs a separate copy contract."); continue; }
                    if (kind is null) continue;
                    if (kind is not (WmlResourceFieldKind.Image or WmlResourceFieldKind.AnimationImage or WmlResourceFieldKind.Sound))
                    { issues.Add("Resource field " + entry.Key + " (" + kind + ") needs a separate macro copy contract."); continue; }
                    if (!entry.Value.IsKnown || entry.Value.ValueText is null
                        || entry.Value.Contributions.LastOrDefault() is not { ValueMappingIsExact: true } mapped
                        || mapped.ValueOrigins.Count == 0)
                    { issues.Add("Resource field " + entry.Key + " has an opaque or unproved mapped value; its macro closure cannot be copied."); continue; }
                    var value = WccScenarioFieldValueCodec.DecodeLiteral(entry.Value.ValueText);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    var folder = kind == WmlResourceFieldKind.Sound ? "sounds" : "images";
                    var syntax = kind == WmlResourceFieldKind.Sound ? WmlResourceReferenceSyntax.ParseSound(value)
                        : WmlResourceReferenceSyntax.ParseImage(value, kind == WmlResourceFieldKind.AnimationImage);
                    var image = folder == "images" ? WccImageExpressionParser.TryParse(value) : null;
                    var filename = image?.BaseReference ?? value;
                    var aliases = syntax.Filenames.SelectMany(reference => reference.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || reference.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                            ? new[] { reference, reference[..^4] + ".webp" } : new[] { reference }).Distinct(StringComparer.Ordinal).ToArray();
                    var localMembers = aliases.Select(alias => WccStaticSourceCapture.FindCapturedResourceMember(capture, source.RevisionId, folder + "/" + alias))
                        .OfType<WccCapturedResourceMember>().Where(member => !member.IsCore).ToArray();
                    bool Core(string reference) => WmlResourceLookupPaths.Create(reference, folder, ["core"], folder == "images",
                        installationResourceFolders: resourceResolver.InstallationResourceFolders).Candidates.Any(path =>
                        resourceResolver.ResolveResource(path, primary).Exists == WmlStaticTruth.True);
                    var corePresent = syntax.IsComplete && syntax.Filenames.Count != 0 && syntax.Filenames.All(Core);
                    if (localMembers.Length == 0)
                    {
                        if (!corePresent) issues.Add("Resource '" + value + "' in " + entry.Key + " is unavailable from captured Core or the selected source package.");
                        continue;
                    }
                    if (corePresent || localMembers.Length != 1)
                    { issues.Add("Resource '" + value + "' has ambiguous Core/source-package bytes or aliases."); continue; }
                    var contribution = entry.Value.Contributions.LastOrDefault();
                    if (!syntax.IsComplete || syntax.Filenames.Count != 1 || syntax.Filenames[0] != filename
                        || image?.MayContainNestedImageReferences == true || contribution is not { ValueMappingIsExact: true }
                        || contribution.ValueOrigins.Count != 1 || contribution.CompositeAssignmentKeys.Count != 0)
                    { issues.Add("Local resource '" + value + "' needs one direct literal image or sound; composite/list/generated values cannot be copied."); continue; }
                    var origin = contribution.ValueOrigins[0];
                    var input = originalInputs.SingleOrDefault(file => SameSource(file, origin.Source));
                    if (input is null || origin.Text.Trim() != entry.Value.ValueText?.Trim())
                    { issues.Add("Local resource '" + value + "' has no exact selected helper assignment."); continue; }
                    var owningHelper = WccUnitTypeMaterializationPlanner.CaptureHelper(input, out _)!;
                    var member = localMembers[0];
                    if (!SamePath(member.RootPath, source.RootPath))
                    { issues.Add("Local resource '" + value + "' belongs to another source namespace."); continue; }
                    var bytes = WccTextDiskRevisionCapture.CaptureBinary(source.RootPath, member.PhysicalPath);
                    if (!bytes.IsCaptured || bytes.Revision is null)
                    { issues.Add("Cannot capture resource '" + value + "': " + bytes.ErrorMessage); continue; }
                    var extension = Path.GetExtension(member.PhysicalPath).ToLowerInvariant();
                    if (extension.Length is < 2 or > 12 || !extension[1..].All(char.IsAsciiLetterOrDigit))
                    { issues.Add("Resource '" + value + "' needs a portable extension."); continue; }
                    var slot = (owningHelper.Name, folder);
                    if (!assetSlots.TryGetValue(slot, out var slots)) assetSlots.Add(slot, slots = []);
                    var match = slots.FirstOrDefault(item => item.Hash == bytes.Revision.RawByteSha256 && item.Extension == extension);
                    var relative = match.Relative;
                    if (relative is null)
                    {
                        relative = (folder == "images" ? "abilities/" : "units/") + owningHelper.Name + "/" + owningHelper.Name
                            + (slots.Count == 0 ? "" : "_" + (slots.Count + 1)) + extension;
                        slots.Add((bytes.Revision.RawByteSha256, extension, relative));
                        binaries.Add(new(Path.Combine(root, folder, relative.Replace('/', Path.DirectorySeparatorChar)), bytes.Revision));
                    }
                    if (!rewrites.TryGetValue(input.Path, out var edits)) rewrites.Add(input.Path, edits = []);
                    var replacement = WccCreationSourceWriter.EncodeText(relative + (image?.ModifierSuffix ?? ""));
                    if (!edits.Any(edit => edit.Start == origin.StartOffset && edit.Length == origin.Length)) edits.Add((origin.StartOffset, origin.Length, replacement));
                }
            }
        }
        var sources = originalInputs.Select((input, index) =>
        {
            var text = input.Text;
            if (rewrites.TryGetValue(input.Path, out var edits))
                foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
            var path = index == 0 ? primary.Path : Path.Combine(WccManagedLibraryFileStore.GetKindDirectory(root, WccManagedLibraryFileKind.Macro),
                helpers[index - 1].Name + ".cfg");
            var provenance = index == 0 ? "# WCC authored macro copy: package=" + source.PackageId + "; revision=" + source.RevisionId + "; entity=" + source.EntityId
                : "# WCC authored macro helper: package=" + source.PackageId + "; revision=" + source.RevisionId + "; source="
                    + WccCopiedContentProvenance.SafeComment(Path.GetRelativePath(source.RootPath, helpers[index - 1].Source.Path).Replace('\\', '/'))
                    + "; macro=" + helpers[index - 1].Name;
            var records = new List<string>
            { provenance, "# WCC source block SHA256: " + (index == 0 ? source.ContentFingerprint : helpers[index - 1].ContentFingerprint) };
            var macroName = index == 0 ? primaryHelper.Name : helpers[index - 1].Name;
            foreach (var binary in binaries.Where(binary => new[] { "images/abilities/" + macroName + "/", "sounds/units/" + macroName + "/" }
                .Any(prefix => Path.GetRelativePath(root, binary.DestinationPath).Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal))))
                records.Add("# WCC resource source: " + WccCopiedContentProvenance.SafeComment(Path.GetRelativePath(binary.SourceRevision.ProjectRootPath,
                    binary.SourceRevision.SourcePath).Replace('\\', '/')) + " -> "
                    + WccCopiedContentProvenance.SafeComment(Path.GetRelativePath(root, binary.DestinationPath).Replace('\\', '/'))
                    + "; byte SHA256: " + binary.SourceRevision.RawByteSha256);
            return new WmlStaticSource(path, WccCopiedContentProvenance.Append(text, records));
        }).ToArray();
        if (sources.Length > 8 || sources.Length + binaries.Count > 64 || binaries.Sum(item => item.SourceRevision.RawByteLength) > 64L * 1024 * 1024)
            issues.Add("The macro copy exceeds the checked eight text files or 64-file/64-MiB mixed batch limit.");
        return new(source, capture, root, targets[0].Id, currentRevision.Id, managedKind, candidateRawText, sources, helpers, binaries, issues);
    }

    internal static WmlStaticEvaluationResult Evaluate(WccStaticSourceCaptureResult capture,
        IReadOnlyList<WmlStaticSource> definitions, string name, WccManagedLibraryFileKind kind)
    {
        if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            throw new InvalidOperationException("The macro copy needs an exact supported Core profile.");
        // Generic macro copies also need ability-field resource observation. This
        // analysis container is evidence only; it is never saved or an edit owner.
        var tag = kind == WccManagedLibraryFileKind.WeaponSpecial ? "specials" : "abilities";
        var entry = new WmlStaticSource("wcc-static://managed-macro-copy/entry.cfg", "{" + "~wcc-macro-copy/definitions.cfg" + "}\n[" + tag + "]\n{" + name + "}\n[/" + tag + "]\n", false);
        return new WmlStaticEvaluator().Evaluate(new(entry, new WmlEvaluationContext(targetCompatibility: "1.20"),
            new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources).ToArray(),
            ["EDITOR", "MULTIPLAYER"], true, new MacroResolver(capture.IncludeResolver, definitions),
            WmlStaticEvaluationLimits.Campaign));
    }

    internal static void ValidateSource(WccLibrarySnapshot snapshot, WccManagedMacroCopySource source)
    {
        if (ManifestIdentity(snapshot) != source.ManifestIdentity
            || !snapshot.EntitiesById.TryGetValue(source.EntityId, out var entity) || entity.SourceFileId != source.SourceFileId
            || entity.ContentFingerprint != source.ContentFingerprint || entity.SourceSpan?.StartOffset != source.Definition.StartOffset
            || entity.SourceSpan.EndOffsetExclusive != source.Definition.StartOffset + source.Definition.Length
            || !snapshot.SourceFilesById.TryGetValue(source.SourceFileId, out var file) || file.PackageRevisionId != source.RevisionId
            || file.ContentHash != source.Source.ContentHash || !SamePath(file.StoragePath, source.Source.Path))
            throw new InvalidOperationException("The selected macro's captured entity, source manifest or package binding changed. Reopen Clone.");
        var current = CaptureSource(snapshot, source.EntityId, source.Source);
        if (current.Name != source.Name || current.TextDomain != source.TextDomain || current.RevisionId != source.RevisionId
            || current.PackageId != source.PackageId || !SamePath(current.RootPath, source.RootPath))
            throw new InvalidOperationException("The selected macro's source identity, purity or textdomain changed.");
    }

    internal static void ValidateDestination(WccLibrarySnapshot snapshot, WccManagedMacroCopyPlan plan)
    {
        if (!snapshot.PackagesById.TryGetValue(plan.DestinationPackageId, out var package) || !package.IsAvailable
            || package.SourceKind != WccPackageSourceKinds.WccManagedLibrary || !SamePath(package.SourceLocation, plan.DestinationRoot)
            || package.CurrentRevisionId != plan.DestinationRevisionId
            || !snapshot.RevisionsById.TryGetValue(plan.DestinationRevisionId, out var revision) || revision.PackageId != package.Id)
            throw new InvalidOperationException("The selected managed Library destination changed. Reopen Clone.");
        ValidateSource(snapshot, plan.OriginalSource);
    }

    internal static string ManifestIdentity(WccLibrarySnapshot snapshot)
    {
        var value = new StringBuilder();
        foreach (var package in snapshot.PackagesById.Values.OrderBy(item => item.Id.ToString(), StringComparer.Ordinal))
            value.Append(package.Id).Append('|').Append(package.SourceKind).Append('|').Append(package.ContentKind).Append('|')
                .Append(package.SourceLocation).Append('|').Append(package.IsAvailable).Append('|').Append(package.SourceIdentifier).Append('|').Append(package.StorageMode)
                .Append('|').Append(package.ParentPackageId).Append('|').Append(package.CurrentRevisionId).Append('\n');
        foreach (var revision in snapshot.RevisionsById.Values.OrderBy(item => item.Id.ToString(), StringComparer.Ordinal))
            value.Append(revision.Id).Append('|').Append(revision.PackageId).Append('|').Append(string.Join(",", revision.DependencyRevisionIds)).Append('|').Append(revision.Version)
                .Append('|').Append(revision.TargetWesnothVersion).Append('|').Append(revision.ContentFingerprint).Append('\n');
        foreach (var file in snapshot.SourceFilesById.Values.OrderBy(item => item.Id.ToString(), StringComparer.Ordinal))
            value.Append(file.Id).Append('|').Append(file.PackageRevisionId).Append('|').Append(file.RelativePath).Append('|').Append(file.StoragePath).Append('|').Append(file.ContentHash).Append('|').Append(file.FileType).Append('\n');
        foreach (var entity in snapshot.EntitiesById.Values.OrderBy(item => item.Id.ToString(), StringComparer.Ordinal))
            value.Append(entity.Id).Append('|').Append(entity.SourceFileId).Append('|').Append(entity.EntityType).Append('|').Append(entity.ParentEntityId)
                .Append('|').Append(entity.ExportSymbol).Append('|').Append(entity.ContentFingerprint).Append('|').Append(entity.SourceSpan?.StartOffset)
                .Append('|').Append(entity.SourceSpan?.EndOffsetExclusive).Append('\n');
        return WccContentHash.ComputeText(value.ToString());
    }

    private static IEnumerable<WccContentHelperReceipt> Definitions(WmlStaticSource source)
    {
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        if (structure.Problem is not null) yield break;
        foreach (var macro in WmlMacroDefinitionScanner.Scan(source.Text))
        {
            if (macro.StartLineIndex >= structure.Lines.Count || macro.EndLineIndexExclusive > structure.Lines.Count) continue;
            var start = structure.Lines[macro.StartLineIndex].StartOffset;
            var end = structure.Lines[macro.EndLineIndexExclusive - 1].EndOffsetExclusive;
            var helper = CaptureDefinition(source, new(source, start, end - start), out _);
            if (helper is not null) yield return helper;
        }
    }

    private static WccContentHelperReceipt? CaptureDefinition(WmlStaticSource source, WmlStaticSourceRange range, out string reason)
    {
        reason = "Choose one unconditional pure top-level #define with an explicit textdomain.";
        var structure = WmlAuthoredStructureScanner.Scan(source.Text); var depth = 0;
        if (structure.Directives.Count(item => item.Name == "define" && item.DefinitionDepth == 0
                && structure.Lines[item.Line].StartOffset == range.StartOffset) != 1) return null;
        foreach (var directive in structure.Directives.Where(item => structure.Lines[item.Line].StartOffset < range.StartOffset
                     && item.DefinitionDepth == 0))
        {
            if (directive.Name is "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver") depth++;
            else if (directive.Name == "endif") depth--;
        }
        if (structure.Problem is not null || depth != 0) return null;
        var domain = WmlTextDomainCapture.BeforeOffset(source.Text, range.StartOffset);
        if (domain is null || WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is not null) return null;
        var excerpt = new WmlStaticSource(source.Path, "#textdomain " + domain + "\n" + range.Text);
        var helper = WccUnitTypeMaterializationPlanner.CaptureHelper(excerpt, out reason);
        if (helper is null || helper.Definition.Text.TrimEnd('\r', '\n') != range.Text.TrimEnd('\r', '\n')) return null;
        return new(helper.Name, source, range, domain, helper.Parameters, helper.RequiredParameterCount);
    }

    private static WmlStaticSource RenderHelper(WccContentHelperReceipt helper) => new(helper.Source.Path + ".wcc-selected-" + helper.Name,
        "#textdomain " + helper.TextDomain + "\n" + helper.Definition.Text + (helper.Definition.Text.EndsWith('\n') ? "" : "\n"));
    internal static bool SameSource(WmlStaticSource first, WmlStaticSource second) => SamePath(first.Path, second.Path)
        && first.ContentHash == second.ContentHash && first.Text == second.Text;
    internal static bool SamePath(string? first, string? second) => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    internal static bool Inside(string root, string path) => Path.GetRelativePath(root, path) is var relative && !Path.IsPathRooted(relative)
        && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static bool Portable(string name) => name.Length is > 0 and <= 120 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static bool AllowedSource(WccPackageRecord package) => package.IsAvailable && !string.IsNullOrWhiteSpace(package.SourceLocation)
        && (package.SourceKind == WccPackageSourceKinds.WesnothCore || package.SourceKind == WccPackageSourceKinds.WccManagedLibrary
            || package.SourceKind == WccPackageSourceKinds.WesnothMainline
                && (package.ContentKind == WccPackageContentKinds.Campaign || package.ParentPackageId is null
                    && (package.ContentKind == WccPackageContentKinds.Core || package.ContentKind == WccPackageContentKinds.ResourcePack)));

    internal static bool IsSelectedExpansionOwner(WmlStaticScalarOwner owner)
    {
        var root = owner;
        while (root.Parent is not null) root = root.Parent;
        return root.OpeningOrigin?.Source.Path == "wcc-static://managed-macro-copy/entry.cfg";
    }

    internal static string? ExpansionBoundary(WmlStaticScalarProjection projection)
    {
        const string entryPath = "wcc-static://managed-macro-copy/entry.cfg";
        var wrappers = projection.Owners.Where(owner => owner.Parent is null && owner.OpeningOrigin?.Source.Path == entryPath).ToArray();
        if (wrappers.Length != 1 || wrappers[0].IsAmended || wrappers[0].ClosingOrigin?.Source.Path != entryPath
            || wrappers[0].OpeningExpansionStack.Count != 0 || wrappers[0].ClosingExpansionStack.Count != 0)
            return "The selected macro emits WML outside its analysis container; escaped output needs a separate macro copy contract.";
        return null;
    }

    private sealed class MacroResolver(IWmlStaticIncludeResolver basis, IReadOnlyList<WmlStaticSource> definitions) : IWmlStaticExistenceResolver
    {
        public WmlStaticIncludeResolution Resolve(string path, WmlStaticSource caller) => path == "~wcc-macro-copy/definitions.cfg"
            ? new(WmlStaticTruth.True, definitions, WccContentHash.ComputeText(string.Join("\n", definitions.Select(source => source.Path + "|" + source.ContentHash))))
            : Core(path) ? basis.Resolve(path, caller) : Refused(path);
        public WmlStaticIncludeResolution ResolveExistence(string path, WmlStaticSource caller) => Core(path)
            && basis is IWmlStaticExistenceResolver existence ? existence.ResolveExistence(path, caller) : Refused(path);
        private static bool Core(string path) => new[] { "core", "themes", "lua", "ai", "internal" }
            .Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal));
        private static WmlStaticIncludeResolution Refused(string path) => new(WmlStaticTruth.Unknown, [], "wcc-managed-macro-refused:" + path,
            "The macro reference '" + path + "' needs unsupported source-package activation.");
    }
}
