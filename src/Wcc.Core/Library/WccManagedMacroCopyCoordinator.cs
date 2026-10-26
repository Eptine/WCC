using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.Core.Library;

public enum WccManagedMacroCopyStatus { Prepared, Succeeded, Refused, RecoveryRequired }

public sealed record WccManagedMacroCopyFileOutcome(string SourcePath, WccTextAtomicReplaceResult? Save,
    bool WasCreated, bool WasReplaced, string Diagnostic)
{ public bool AuthorizesPersistence => false; }

public sealed class WccManagedMacroCopyResult
{
    internal WccManagedMacroCopyResult(WccManagedMacroCopyStatus status, string diagnostic,
        string? primarySourcePath = null, WccManagedMacroCopyTicket? ticket = null,
        IReadOnlyList<string>? writtenPaths = null, IReadOnlyList<string>? createdDirectories = null,
        IReadOnlyList<WccManagedMacroCopyFileOutcome>? files = null)
    {
        Status = status; Diagnostic = diagnostic; PrimarySourcePath = primarySourcePath; Ticket = ticket;
        WrittenPaths = Array.AsReadOnly((writtenPaths ?? []).ToArray());
        CreatedDirectories = Array.AsReadOnly((createdDirectories ?? []).ToArray());
        Files = Array.AsReadOnly((files ?? []).ToArray());
    }
    public WccManagedMacroCopyStatus Status { get; }
    public string Diagnostic { get; }
    public string? PrimarySourcePath { get; }
    public WccManagedMacroCopyTicket? Ticket { get; }
    public IReadOnlyList<string> WrittenPaths { get; }
    public IReadOnlyList<string> CreatedDirectories { get; }
    public IReadOnlyList<WccManagedMacroCopyFileOutcome> Files { get; }
    public bool IsPrepared => Status == WccManagedMacroCopyStatus.Prepared;
    public bool IsSuccess => Status == WccManagedMacroCopyStatus.Succeeded;
}

/// <summary>A single-use exact reviewed managed Library mixed transaction.</summary>
public sealed class WccManagedMacroCopyTicket
{
    internal WccManagedMacroCopyTicket(WccLibrarySnapshot snapshot, WccManagedMacroCopyPlan plan,
        WccStaticSourceCaptureResult sourceCapture, WccStaticSourceCaptureResult destinationCapture,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> binaries,
        IReadOnlyList<WccTextDiskRevision> textRevisions, IReadOnlyList<WccBinaryDiskRevision> binaryRevisions,
        IReadOnlyList<WmlStaticSource> actualDefinitions, string sourceInventoryIdentity, string destinationInventoryIdentity)
    {
        Snapshot = snapshot; Plan = plan; SourceCapture = sourceCapture; DestinationCapture = destinationCapture;
        Creations = creations; BinaryCreations = binaries; TextRevisions = textRevisions; BinaryRevisions = binaryRevisions;
        ActualDefinitions = actualDefinitions; SourceInventoryIdentity = sourceInventoryIdentity;
        DestinationInventoryIdentity = destinationInventoryIdentity;
    }
    internal WccLibrarySnapshot Snapshot { get; }
    internal WccManagedMacroCopyPlan Plan { get; }
    internal WccStaticSourceCaptureResult SourceCapture { get; }
    internal WccStaticSourceCaptureResult DestinationCapture { get; }
    internal IReadOnlyList<WccTextAtomicBatchCreationCandidate> Creations { get; }
    internal IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> BinaryCreations { get; }
    internal IReadOnlyList<WccTextDiskRevision> TextRevisions { get; }
    internal IReadOnlyList<WccBinaryDiskRevision> BinaryRevisions { get; }
    internal IReadOnlyList<WmlStaticSource> ActualDefinitions { get; }
    internal string SourceInventoryIdentity { get; }
    internal string DestinationInventoryIdentity { get; }
    internal int Consumed;
    public string PrimarySourcePath => Plan.PrimarySource.Path;
    public bool AuthorizesPersistence => false;
}

/// <summary>Managed macro adapter for the existing guarded mixed native writer.</summary>
public sealed class WccManagedMacroCopyCoordinator
{
    public async Task<WccManagedMacroCopyResult> PrepareAsync(WccLibrarySnapshot snapshot,
        WccManagedMacroCopyPlan plan, Func<bool> bindingIsCurrent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(bindingIsCurrent);
        try
        {
            if (!bindingIsCurrent()) return Refused("The selected macro or Library destination changed. Reopen Clone.");
            if (!plan.IsComplete) return Refused(string.Join("\n", plan.Issues));
            WccManagedMacroCopyPlanner.ValidateDestination(snapshot, plan);
            if (WccStaticSourceCapture.VerifyCapturedRootInventory(plan.Capture, plan.OriginalSource.RevisionId) is { } oldInventory)
                return Refused(oldInventory);
            var sourceCapture = await WccStaticSourceCapture.CaptureAsync(snapshot, plan.OriginalSource.RevisionId,
                plan.OriginalSource.Source.Path, cancellationToken: cancellationToken, includeCoreUnits: true);
            if (!sourceCapture.IsCaptureComplete) return Refused(string.Join("\n", sourceCapture.Diagnostics));
            var replanned = WccManagedMacroCopyPlanner.Plan(plan.OriginalSource, sourceCapture, plan.DestinationRoot, plan.Kind, plan.CandidateRawText);
            if (!replanned.IsComplete || replanned.Identity != plan.Identity)
                return Refused("The source macro, helper or resource recipe changed: " + string.Join("\n", replanned.Issues));
            var coreEntry = sourceCapture.IncludeResolver.Resolve("core/units.cfg", sourceCapture.EntrySource!).Sources.Single();
            var destinationCapture = await WccStaticSourceCapture.CaptureAsync(snapshot, plan.DestinationRevisionId,
                coreEntry.Path, cancellationToken: cancellationToken, includeCoreUnits: true);
            if (!destinationCapture.IsCaptureComplete) return Refused(string.Join("\n", destinationCapture.Diagnostics));
            // The actual target's pinned Core profile must prove the same candidate.
            var definitions = new List<WmlStaticSource>(); var creations = new List<WccTextAtomicBatchCreationCandidate>();
            var binaryCreations = new List<WccBinaryAtomicBatchCreationCandidate>();
            var binaryRevisions = new List<WccBinaryDiskRevision>();
            var namespaceDefinitions = destinationCapture.Sources.Where(source => WccManagedMacroCopyPlanner.Inside(plan.DestinationRoot, source.Path))
                .SelectMany(source => WmlMacroDefinitionScanner.Scan(source.Text).Select(definition => (Source: source, definition.Name))).ToArray();
            for (var index = 0; index < plan.Sources.Count; index++)
            {
                var expected = plan.Sources[index];
                var helper = WccUnitTypeMaterializationPlanner.CaptureHelper(expected, out var reason);
                if (helper is null || helper.RequiredParameterCount != 0) return Refused("A copied definition is not pure or requires arguments: " + reason);
                var named = namespaceDefinitions.Where(item => item.Name == helper.Name).ToArray();
                if (named.Length != 0)
                {
                    if (index == 0 || named.Length != 1 || named[0].Source.Path != expected.Path)
                        return Refused("Macro {" + helper.Name + "} already exists in the managed destination; choose another name.");
                    var existing = WccUnitTypeMaterializationPlanner.CaptureHelper(named[0].Source, out _);
                    if (existing is null || existing.Definition.Text != helper.Definition.Text || existing.TextDomain != helper.TextDomain)
                        return Refused("Helper {" + helper.Name + "} conflicts with the existing managed definition; no files were overwritten.");
                    definitions.Add(named[0].Source); continue;
                }
                var absent = WccTextDiskRevisionCapture.CaptureCreationAbsence(plan.DestinationRoot, expected.Path);
                if (!absent.IsCaptured || absent.Absence is null) return Refused("Cannot create macro CFG '" + expected.Path + "': " + absent.ErrorMessage);
                creations.Add(new(absent.Absence, expected.Text, expected.ContentHash)); definitions.Add(expected);
            }
            foreach (var binary in plan.Binaries)
            {
                var member = WccStaticSourceCapture.FindCapturedResourceMember(destinationCapture, plan.DestinationRevisionId,
                    Path.GetRelativePath(plan.DestinationRoot, binary.DestinationPath).Replace('\\', '/'));
                if (member is not null)
                {
                    var current = WccTextDiskRevisionCapture.CaptureBinary(plan.DestinationRoot, member.PhysicalPath);
                    if (!current.IsCaptured || current.Revision is null || current.Revision.RawByteSha256 != binary.SourceRevision.RawByteSha256
                        || !current.Revision.CopyBytes().AsSpan().SequenceEqual(binary.SourceRevision.CopyBytes()))
                        return Refused("Helper resource '" + binary.DestinationPath + "' conflicts with existing managed bytes.");
                    binaryRevisions.Add(current.Revision); continue;
                }
                var absent = WccTextDiskRevisionCapture.CaptureCreationAbsence(plan.DestinationRoot, binary.DestinationPath);
                if (!absent.IsCaptured || absent.Absence is null) return Refused("Cannot create helper resource: " + absent.ErrorMessage);
                binaryCreations.Add(new(absent.Absence, binary.SourceRevision));
            }
            binaryRevisions.AddRange(plan.Binaries.Select(item => item.SourceRevision));
            var textRevisions = CaptureTexts(snapshot, sourceCapture, destinationCapture);
            var ticket = new WccManagedMacroCopyTicket(snapshot, plan, sourceCapture, destinationCapture,
                Array.AsReadOnly(creations.ToArray()), Array.AsReadOnly(binaryCreations.ToArray()), textRevisions,
                Array.AsReadOnly(binaryRevisions.ToArray()), Array.AsReadOnly(definitions.ToArray()),
                WccStaticSourceCapture.CapturedMacroClosureIdentity(sourceCapture),
                WccStaticSourceCapture.CapturedMacroClosureIdentity(destinationCapture));
            if (Proof(ticket, destinationCapture) is { } proof) return Refused(proof);
            if (!bindingIsCurrent() || Freshness(ticket) is { }) return Refused("The source, resources, namespace or selected context changed during review. Reopen Clone.");
            return new(WccManagedMacroCopyStatus.Prepared, "The macro and its exact helper/resource closure are ready to copy.", plan.PrimarySource.Path, ticket);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return Refused(error.Message); }
    }

    public async Task<WccManagedMacroCopyResult> CommitAsync(WccManagedMacroCopyTicket ticket,
        Func<bool> bindingIsCurrent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket); ArgumentNullException.ThrowIfNull(bindingIsCurrent);
        if (Interlocked.Exchange(ref ticket.Consumed, 1) != 0) return Refused("This macro copy ticket was already used. Review Clone again.");
        try
        {
            if (!bindingIsCurrent() || Freshness(ticket) is { }) return Refused("The reviewed macro copy is stale. Reopen Clone.");
            WccStaticSourceCaptureResult? stagedDestination = null;
            WccStaticSourceCaptureResult? stagedSource = null;
            var result = await WccTextAtomicBatchReplacer.ReplaceAsync([], ticket.Creations, ticket.BinaryCreations,
                async (stages, token) =>
                {
                    if (!bindingIsCurrent() || Freshness(ticket, ignoreDestinationInventory: true) is { })
                        return "The selected macro, source bytes or context changed before the first write.";
                    if (ValidateStages(ticket, stages) is { } stageError) return stageError;
                    var texts = stages.Where(stage => stage.TextRevision is not null).Select(stage => stage.TextRevision!).ToArray();
                    var binaries = stages.Where(stage => stage.BinaryRevision is not null).Select(stage => stage.BinaryRevision!).ToArray();
                    stagedDestination = await WccStaticSourceCapture.CaptureForStagedManagedMacroCopyAsync(ticket.Snapshot, ticket.Plan,
                        ticket.DestinationCapture.EntrySource!.Path, texts, binaries, token);
                    if (!stagedDestination.IsCaptureComplete) return string.Join("\n", stagedDestination.Diagnostics);
                    if (WccStaticSourceCapture.CapturedMacroClosureIdentity(stagedDestination)
                        != ticket.DestinationInventoryIdentity) return "The managed destination namespace changed while files were staged.";
                    if (ticket.Plan.OriginalSource.RevisionId == ticket.Plan.DestinationRevisionId)
                        stagedSource = stagedDestination;
                    else
                        stagedSource = await WccStaticSourceCapture.CaptureAsync(ticket.Snapshot, ticket.Plan.OriginalSource.RevisionId,
                            ticket.Plan.OriginalSource.Source.Path, cancellationToken: token, includeCoreUnits: true);
                    if (!stagedSource.IsCaptureComplete || WccStaticSourceCapture.CapturedMacroClosureIdentity(stagedSource) != ticket.SourceInventoryIdentity)
                        return "The source package namespace changed while files were staged.";
                    return Proof(ticket, stagedDestination);
                }, commit =>
                {
                    if (!bindingIsCurrent() || stagedDestination is null || stagedSource is null
                        || Freshness(ticket, ignoreDestinationInventory: true) is { } error
                        || WccStaticSourceCapture.VerifyCapturedMacroClosure(stagedDestination) is { }
                        || WccStaticSourceCapture.VerifyCapturedMacroClosure(stagedSource) is { })
                        return new(WccTextAtomicBatchReplaceStatus.Refused, "The source or managed target changed at the final macro copy guard.");
                    return commit();
                }, cancellationToken);
            var retained = result.Files.SelectMany(file => new[] { file.Save?.RecoveryCandidatePath, file.Save?.RecoveryBackupPath })
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var diagnostic = result.Diagnostic + (retained.Length == 0 ? "" : " Retained recovery artifacts: " + string.Join(", ", retained));
            return new(result.IsSuccess ? WccManagedMacroCopyStatus.Succeeded
                : result.Status == WccTextAtomicBatchReplaceStatus.RecoveryRequired ? WccManagedMacroCopyStatus.RecoveryRequired
                : WccManagedMacroCopyStatus.Refused, diagnostic, ticket.PrimarySourcePath,
                writtenPaths: result.Files.Where(file => file.WasWritten).Select(file => file.SourcePath).ToArray(), createdDirectories: result.CreatedDirectories,
                files: result.Files.Select(file => new WccManagedMacroCopyFileOutcome(file.SourcePath, file.Save, file.WasCreated, file.WasReplaced, file.Diagnostic)).ToArray());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return Refused(error.Message); }
    }

    private static string? Proof(WccManagedMacroCopyTicket ticket, WccStaticSourceCaptureResult basis)
    {
        var additions = ticket.Creations.Select(create => new WmlStaticSource(create.ExpectedAbsence.SourcePath, create.CandidateText)).ToArray();
        var binaries = ticket.BinaryCreations.Select(create => new WccCapturedBinaryCandidate(create.ExpectedAbsence.SourcePath, create.ExpectedSource)).ToArray();
        var directories = ticket.Creations.SelectMany(create => create.ExpectedAbsence.RequiredDirectories)
            .Concat(ticket.BinaryCreations.SelectMany(create => create.ExpectedAbsence.RequiredDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length).Select(path => new WccCapturedDirectoryCandidate(path)).ToArray();
        var candidate = WccStaticSourceCapture.CreateManagedMacroCandidateCapture(ticket.Snapshot, ticket.Plan, basis, additions, binaries, directories);
        if (ticket.ActualDefinitions.Any(definition => candidate.Sources.Count(source => WccManagedMacroCopyPlanner.SameSource(source, definition)) != 1))
            return "The generated macro definitions do not have exact captured destination membership.";
        var name = WmlMacroDefinitionScanner.Scan(ticket.Plan.PrimarySource.Text).Single().Name;
        var expected = WccManagedMacroCopyPlanner.Evaluate(ticket.SourceCapture, ticket.Plan.Sources, name, ticket.Plan.Kind);
        var actual = WccManagedMacroCopyPlanner.Evaluate(candidate, ticket.ActualDefinitions, name, ticket.Plan.Kind);
        if (!expected.IsComplete || !actual.IsComplete || expected.Text != actual.Text)
            return "The detached macro expansion differs in the target's captured Core context: "
                + string.Join("\n", actual.Diagnostics.Where(item => item.IsBlocking).Select(item => item.Message));
        // All bindings, including silent nested calls, must be to these exact pure
        // candidate/reused definitions or the unchanged pinned Core definitions.
        var expectedCalls = expected.Invocations.Select(call => CallIdentity(call, ticket.Plan.Sources)).ToArray();
        if (!expectedCalls.SequenceEqual(actual.Invocations.Select(call => CallIdentity(call, ticket.ActualDefinitions)), StringComparer.Ordinal))
            return "A macro invocation's exact definition or nested caller binding changed in the destination.";
        if (candidate.IncludeResolver is not IWmlStaticResourceResolver resolver) return "The target resource namespace is unavailable.";
        var projection = WmlStaticScalarProjection.Create(actual);
        if (projection.Diagnostics.Count != 0) return "The saved macro expansion has malformed WML.";
        if (WccManagedMacroCopyPlanner.ExpansionBoundary(projection) is { } escaped) return escaped;
        foreach (var owner in projection.Owners)
        {
            if (!WccManagedMacroCopyPlanner.IsSelectedExpansionOwner(owner)) continue;
            foreach (var field in owner.Fields)
            {
                var kind = WmlResourceFieldCatalog.Find(owner, field.Key);
                if (kind == WmlResourceFieldKind.Lua) return "Lua resource closure is unsupported for managed macro copies.";
                if (kind is null) continue;
                if (kind is not (WmlResourceFieldKind.Image or WmlResourceFieldKind.AnimationImage or WmlResourceFieldKind.Sound))
                    return "Resource field '" + field.Key + "' (" + kind + ") has no macro copy contract.";
                if (!field.Value.IsKnown || field.Value.ValueText is null
                    || field.Value.Contributions.LastOrDefault() is not { ValueMappingIsExact: true } mapped
                    || mapped.ValueOrigins.Count == 0)
                    return "Resource field '" + field.Key + "' has an opaque or unproved mapped value.";
                var value = WccScenarioFieldValueCodec.DecodeLiteral(field.Value.ValueText);
                if (string.IsNullOrWhiteSpace(value)) continue;
                var folder = kind == WmlResourceFieldKind.Sound ? "sounds" : "images";
                var syntax = kind == WmlResourceFieldKind.Sound ? WmlResourceReferenceSyntax.ParseSound(value)
                    : WmlResourceReferenceSyntax.ParseImage(value, kind == WmlResourceFieldKind.AnimationImage);
                if (!syntax.IsComplete || syntax.Filenames.Count == 0) return "Resource '" + value + "' has unsupported syntax.";
                foreach (var filename in syntax.Filenames)
                {
                    var relative = folder + "/" + filename;
                    var transferred = ticket.Plan.Binaries.SingleOrDefault(binary => Path.GetRelativePath(ticket.Plan.DestinationRoot, binary.DestinationPath).Replace('\\', '/') == relative);
                    if (transferred is not null)
                    {
                        if (WccStaticSourceCapture.FindCapturedResourceMember(candidate, ticket.Plan.DestinationRevisionId, relative) is null)
                            return "The copied helper resource '" + filename + "' has no exact destination membership.";
                    }
                    else if (!WmlResourceLookupPaths.Create(filename, folder, ["core"], folder == "images",
                        installationResourceFolders: resolver.InstallationResourceFolders).Candidates.Any(path => resolver.ResolveResource(path, ticket.Plan.PrimarySource).Exists == WmlStaticTruth.True))
                        return "Resource '" + filename + "' is unavailable without source-package binary paths.";
                }
            }
        }
        return null;

        static string CallIdentity(WmlStaticInvocationTrace call, IReadOnlyList<WmlStaticSource> definitions)
        {
            string Origin(WmlStaticSourceRange range)
            {
                var index = definitions.ToList().FindIndex(source => WccManagedMacroCopyPlanner.SameSource(source, range.Source));
                return index >= 0 ? "copied:" + index + ":" + WccContentHash.ComputeText(range.Text)
                    : range.Source.Path + ":" + range.Source.ContentHash + ":" + range.StartOffset + ":" + range.Length;
            }
            return call.Name + "|" + call.Outcome + "|" + (call.Definition is null ? "" : Origin(call.Definition)) + "|"
                + string.Join("/", call.ExpansionStack.Select(frame => frame.MacroName + ":" + Origin(frame.Definition)));
        }
    }

    private static string? Freshness(WccManagedMacroCopyTicket ticket, bool ignoreDestinationInventory = false)
    {
        WccManagedMacroCopyPlanner.ValidateDestination(ticket.Snapshot, ticket.Plan);
        if (!ignoreDestinationInventory)
        {
            if (WccStaticSourceCapture.VerifyCapturedMacroClosure(ticket.SourceCapture) is { } sourceError) return sourceError;
            if (WccStaticSourceCapture.VerifyCapturedMacroClosure(ticket.DestinationCapture) is { } destinationError) return destinationError;
        }
        foreach (var expected in ticket.TextRevisions)
        {
            var current = WccTextDiskRevisionCapture.Capture(expected.ProjectRootPath, expected.SourcePath, expected.Text);
            if (!current.IsCaptured || current.Revision is null || !expected.IsSameRevisionAs(current.Revision)) return "A captured macro/Core text source changed: " + expected.SourcePath;
        }
        foreach (var expected in ticket.BinaryRevisions)
        {
            var current = WccTextDiskRevisionCapture.CaptureBinary(expected.ProjectRootPath, expected.SourcePath);
            if (!current.IsCaptured || current.Revision is null || !expected.IsSameRevisionAs(current.Revision)) return "A captured helper resource changed: " + expected.SourcePath;
        }
        return null;
    }

    private static IReadOnlyList<WccTextDiskRevision> CaptureTexts(WccLibrarySnapshot snapshot, params WccStaticSourceCaptureResult[] captures)
    {
        var revisions = new List<WccTextDiskRevision>();
        foreach (var source in captures.SelectMany(capture => capture.Sources).DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase))
        {
            var roots = captures.Where(capture => capture.Sources.Any(member => WccManagedMacroCopyPlanner.SameSource(member, source)))
                .Select(capture => WccStaticSourceCapture.CapturedSourceRoot(capture, source)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length != 1) throw new InvalidOperationException("The captured source has no unique physical package root: " + source.Path);
            var captured = WccTextDiskRevisionCapture.Capture(roots[0], source.Path, source.Text);
            if (!captured.IsCaptured || captured.Revision is null) throw new InvalidOperationException("Cannot bind macro source bytes: " + captured.ErrorMessage);
            revisions.Add(captured.Revision);
        }
        return Array.AsReadOnly(revisions.ToArray());
    }

    private static string? ValidateStages(WccManagedMacroCopyTicket ticket, IReadOnlyList<WccAtomicBatchStagedFile> stages)
    {
        if (stages.Count != ticket.Creations.Count + ticket.BinaryCreations.Count
            || stages.Select(stage => stage.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != stages.Count)
            return "The writer did not stage the exact reviewed macro copy members.";
        foreach (var stage in stages)
        {
            if (stage.TextRevision is { } text)
            {
                var expected = ticket.Creations.SingleOrDefault(create => WccManagedMacroCopyPlanner.SamePath(create.ExpectedAbsence.SourcePath, stage.SourcePath));
                if (!stage.IsCreation || expected is null || !WccManagedMacroCopyPlanner.SamePath(text.ProjectRootPath, ticket.Plan.DestinationRoot)
                    || text.Text != expected.CandidateText || text.DecodedTextSha256 != expected.CandidateTextSha256)
                    return "A macro CFG stage differs from its reviewed bytes or target.";
            }
            else if (stage.BinaryRevision is { } binary)
            {
                var expected = ticket.BinaryCreations.SingleOrDefault(create => WccManagedMacroCopyPlanner.SamePath(create.ExpectedAbsence.SourcePath, stage.SourcePath));
                if (!stage.IsCreation || expected is null || !WccManagedMacroCopyPlanner.SamePath(binary.ProjectRootPath, ticket.Plan.DestinationRoot)
                    || binary.RawByteSha256 != expected.CandidateByteSha256 || binary.RawByteLength != expected.CandidateByteLength
                    || !binary.CopyBytes().AsSpan().SequenceEqual(expected.CopyBytes())) return "A macro resource stage differs from its reviewed bytes or target.";
            }
            else return "The macro writer supplied an unrecognized stage.";
        }
        return null;
    }

    private static WccManagedMacroCopyResult Refused(string diagnostic) => new(WccManagedMacroCopyStatus.Refused, diagnostic);
}
