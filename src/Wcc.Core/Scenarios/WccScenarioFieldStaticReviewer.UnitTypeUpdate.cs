using Wcc.Core.Library;
using Wcc.Core.Units;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateUnitTypeUpdateBinding(WccUnitTypeUpdateEdit edit, WccScenarioFieldEditSessionState state,
        WccStaticSourceCaptureResult capture)
    {
        if (state.ReviewScope != WccScenarioFieldReviewScope.CampaignMenu || state.Plan is null
            || edit.ProjectCapture.EntrySource is null || capture.EntrySource is null
            || !WccUnitTypeUpdatePlanner.SameSource(edit.ProjectCapture.EntrySource, capture.EntrySource)
            || !PathComparer.Equals(edit.ProjectRootPath, state.ProjectRootPath)
            || capture.Sources.Count(source => WccUnitTypeUpdatePlanner.SameSource(source, edit.OriginalSource)) != 1)
            throw new InvalidOperationException("The unit update is not bound to its exact current campaign and captured unit source.");
        WccUnitTypeUpdatePlanner.ValidateSourceEvidence(state.Snapshot, edit, state.RootRevisionId);
        var fresh = WccUnitTypeUpdatePlanner.Plan(edit.OriginalSource, edit.CandidateSource, edit.ManagedCapture,
            capture, edit.PlanningContext, edit.TextDomain);
        if (fresh.Identity != edit.Identity || !fresh.SourceCreates.Select(source => (source.Path, source.ContentHash))
            .SequenceEqual(edit.SourceCreates.Select(source => (source.Path, source.ContentHash))))
            throw new InvalidOperationException("The unit draft's captured dependency or destination plan changed. Review the draft again.");
        var binaryPlan = WccUnitTypeUpdatePlanner.PlanBinaryDestinations(edit);
        WccUnitTypeMaterializationPlanner.ValidateBinaryReuseMembership(binaryPlan.Reuses, capture, state.RootRevisionId);
        if (WccUnitTypeMaterializationPlanner.VerifyBinaryReuses(binaryPlan.Reuses) is { } binaryReuseError)
            throw new InvalidOperationException(binaryReuseError);
        var binaries = binaryPlan.Creates.Select(binary => new WccCapturedBinaryCandidate(binary.DestinationPath, binary.SourceRevision)).ToArray();
        var directories = CaptureCreationDirectories(state.ProjectRootPath,
            edit.SourceCreates.Select(source => source.Path).Concat(binaries.Select(binary => binary.DestinationPath)));
        _ = WccStaticSourceCapture.CreateCandidateCapture(capture, state.RootRevisionId, [edit.CandidateSource], edit.SourceCreates,
            binaries, directories.Select(path => new WccCapturedDirectoryCandidate(path)).ToArray());
    }

    private static void ValidateUnitUpdateBaseline(WccUnitTypeUpdateEdit edit, PairedEvaluation baseline,
        WmlEvaluationContext context, WccStaticSourceCaptureResult capture, WmlCampaignEvaluationPlan? plan,
        WmlStaticSingleplayerEnvironment environment, CancellationToken cancellationToken)
    {
        if (baseline.Evaluation.IsComplete && baseline.Projection.Diagnostics.Count == 0) return;
        RequireUnitUpdateMissingHelperDiagnostics(edit, baseline, context: context);
        var text = edit.OriginalSource.Text.ToCharArray();
        for (var offset = edit.OriginalRange.StartOffset; offset < edit.OriginalRange.StartOffset + edit.OriginalRange.Length; offset++)
            if (text[offset] is not ('\r' or '\n')) text[offset] = ' ';
        var replacement = new WmlStaticSource(edit.OriginalSource.Path, new string(text));
        var undefined = (plan?.Difficulties.Select(row => row.Define) ?? []).Concat(["EDITOR", "MULTIPLAYER"])
            .Where(symbol => !context.DefinedSymbols.Contains(symbol)).Distinct(StringComparer.Ordinal).ToArray();
        var evaluated = new WmlStaticEvaluator().Evaluate(new(capture.EntrySource!, context,
            new[] { environment.DefinitionsSource }.Concat(capture.PreludeSources).ToArray(), undefined, true,
            new WccUnitTypeUpdatePlanner.UnitReplacementResolver(capture.IncludeResolver, replacement),
            WmlStaticEvaluationLimits.Campaign), cancellationToken);
        var projection = WmlStaticScalarProjection.Create(evaluated, cancellationToken);
        RequireComplete(new(evaluated, projection), context, "Existing campaign without the selected unit");
    }

    private static PairedEvaluation NormalizeUnitUpdateMaskedBaseline(WccUnitTypeUpdateEdit edit, PairedEvaluation masked)
    {
        if (masked.Evaluation.IsComplete) return masked;
        RequireUnitUpdateMissingHelperDiagnostics(edit, masked, checkProjection: false);
        // Only removed or materialized missing calls wholly inside the already masked literal unit
        // are removed from this comparison view. The actual candidate still needs
        // complete independent evaluation and the unmasked baseline was proved with
        // this unit omitted. This result never leaves the checked reviewer.
        var evaluation = new WmlStaticEvaluationResult(masked.Evaluation.Text, masked.Evaluation.Segments,
            masked.Evaluation.Diagnostics.Where(diagnostic => !diagnostic.IsBlocking), masked.Evaluation.Sources,
            masked.Evaluation.Includes, true, masked.Evaluation.Invocations);
        var projection = WmlStaticScalarProjection.Create(evaluation, masked.CancellationToken);
        return new(evaluation, projection) { CancellationToken = masked.CancellationToken, Diagnostics = masked.Diagnostics };
    }

    private static void RequireUnitUpdateMissingHelperDiagnostics(WccUnitTypeUpdateEdit edit, PairedEvaluation baseline,
        bool checkProjection = true, WmlEvaluationContext? context = null)
    {
        var names = edit.Helpers.Select(helper => helper.Name).ToHashSet(StringComparer.Ordinal);
        var candidateNames = WmlMacroInvocationSyntaxScanner.Scan(edit.CandidateRange.Text)
            .Select(invocation => invocation.Name).ToHashSet(StringComparer.Ordinal);
        var originalInvocations = WmlMacroInvocationSyntaxScanner.Scan(edit.OriginalRange.Text);
        bool RemovedLiteralCall(WmlStaticInvocationTrace call) => !candidateNames.Contains(call.Name)
            && call.InvocationOrigins.Count == 1 && originalInvocations.Any(invocation => invocation.Name == call.Name
                && call.InvocationOrigins[0].StartOffset == edit.OriginalRange.StartOffset + invocation.Span.StartOffset
                && call.InvocationOrigins[0].Length == invocation.Span.Length);
        var evaluation = baseline.Evaluation.MaskedOriginalEvaluation ?? baseline.Evaluation;
        var calls = evaluation.Invocations.Where(call => call.Definition is null
            && call.Outcome is WmlStaticInvocationOutcome.Unresolved or WmlStaticInvocationOutcome.IncludeUnresolved
            && call.InvocationOrigins.Count != 0
            && call.InvocationOrigins.All(origin => WccUnitTypeUpdatePlanner.InRange(origin, edit.OriginalRange))
            && (names.Contains(call.Name) || RemovedLiteralCall(call))).ToArray();
        bool MissingOrigin(WmlStaticSourceRange? origin) => origin is not null && calls.Any(call => call.InvocationOrigins.Any(invocation =>
            WccUnitTypeUpdatePlanner.SameSource(invocation.Source, origin.Source)
            && origin.StartOffset >= invocation.StartOffset && origin.StartOffset + origin.Length <= invocation.StartOffset + invocation.Length));
        var other = evaluation.Diagnostics.Where(diagnostic => diagnostic.IsBlocking && !MissingOrigin(diagnostic.Origin))
            .Concat(checkProjection ? baseline.Projection.Diagnostics.Where(diagnostic => !MissingOrigin(diagnostic.Origin)) : []).FirstOrDefault();
        if (other is not null && context is not null)
        {
            var report = new WmlStaticEvaluationResult("", [], [other], evaluation.Sources, [], false, evaluation.Invocations);
            RequireComplete(new(report, WmlStaticScalarProjection.Create(report)), context, "Existing campaign");
        }
        if (calls.Length == 0 || other is not null)
            throw new InvalidOperationException("The existing campaign has unresolved WML beyond a missing captured helper in the selected unit. Repair that source before saving this unit.");
    }

    private static IReadOnlyList<WmlStaticSourceRange> ValidateProjectUnitTypeUpdate(WccUnitTypeUpdateEdit edit,
        PairedEvaluation baseline, PairedEvaluation proposed, WccStaticSourceCaptureResult capture,
        WccStaticSourceCaptureResult candidateCapture, WmlEvaluationContext context, WccScenarioFieldSourceChange? change = null)
    {
        var members = candidateCapture.Sources.Where(source => PathComparer.Equals(source.Path, edit.CandidateSource.Path)).ToArray();
        if (members.Length != 1 || !WccUnitTypeUpdatePlanner.SameSource(members[0], edit.CandidateSource))
            throw new InvalidOperationException("The edited unit differs from its exact immutable draft.");
        var helpers = edit.HelperBindings.Select(row => WccUnitTypeMaterializationPlanner.RenderUpdateHelper(row.Receipt,
            row.Root, edit.ProjectRootPath, edit.TextDomain, edit.ResourceTransfers)).ToArray();
        foreach (var source in edit.DependencySources)
        {
            var actual = candidateCapture.Sources.Where(member => PathComparer.Equals(member.Path, source.Path)).ToArray();
            if (actual.Length != 1 || actual[0].Path != source.Path || !WccUnitTypeUpdatePlanner.SameSource(actual[0], source))
                throw new InvalidOperationException("Shared unit definition differs from its captured recipe: " + Path.GetFileName(source.Path));
            var visits = proposed.Evaluation.Includes.SelectMany(include => include.Resolution.Sources).Count(member => PathComparer.Equals(member.Path, source.Path));
            if (context.CampaignDefine is null ? visits != 0 || proposed.Evaluation.Sources.Any(member => PathComparer.Equals(member.Path, source.Path)) : visits != 1)
                throw new InvalidOperationException("A shared unit definition must remain inactive in MENU and load once in play: " + Path.GetFileName(source.Path));
        }
        var registryRanges = ValidateUnitRegistrySources(edit.Registries, edit.ProjectRootPath, edit.TextDomain,
            baseline, proposed, candidateCapture, context, edit.CandidateSource.Path, helpers);
        var units = proposed.Projection.Owners.Where(owner => owner.TagName == "unit_type" && owner.OpeningOrigin is { } origin
            && PathComparer.Equals(origin.Source.Path, edit.CandidateSource.Path)).ToArray();
        var unitVisits = proposed.Evaluation.Includes.SelectMany(include => include.Resolution.Sources)
            .Count(source => PathComparer.Equals(source.Path, edit.CandidateSource.Path));
        if (context.CampaignDefine is null)
        {
            if (units.Length != 0 || unitVisits != 0 || proposed.Evaluation.Sources.Any(source => PathComparer.Equals(source.Path, edit.CandidateSource.Path)))
                throw new InvalidOperationException("The edited campaign unit is unexpectedly active in MENU.");
            ValidateOutsideUnitInvocationBindings(edit, baseline, proposed, registryRanges, change);
            return registryRanges;
        }
        // The host's selected preview cannot lend private-resource authority to
        // another declared difficulty. Re-prove the same closure using this
        // actual campaign context, including resources emitted by Core callees.
        var contextual = WccUnitTypeUpdatePlanner.Plan(edit.OriginalSource, edit.CandidateSource, edit.ManagedCapture,
            capture, context, edit.TextDomain, proposed.CancellationToken);
        if (contextual.Identity != edit.Identity || !contextual.SourceCreates.Select(source => (source.Path, source.ContentHash))
            .SequenceEqual(edit.SourceCreates.Select(source => (source.Path, source.ContentHash))))
            throw new InvalidOperationException("The selected unit helper or resource recipe differs in " + context.DisplayName + ". Review one supported shared closure for every difficulty.");
        if (units.Length != 1 || unitVisits != 1 || units[0].IsAmended || units[0].Parent is not { TagName: "units", Parent: null } container
            || container.IsAmended || container.OpeningOrigin is not { } opening || candidateCapture.EntrySource is null
            || !WccUnitTypeUpdatePlanner.SameSource(opening.Source, candidateCapture.EntrySource)
            || container.ClosingOrigin is not { } closing || !WccUnitTypeUpdatePlanner.SameSource(closing.Source, candidateCapture.EntrySource))
            throw new InvalidOperationException("The edited unit must load exactly once inside the campaign's literal units container.");
        if (WccScenarioFieldValueCodec.DecodeLiteral(units[0].GetKnownValue("id")) != edit.Id)
            throw new InvalidOperationException("The edited unit no longer has its requested literal ID.");
        var registered = WccUnitTypeUpdatePlanner.EvaluateProject(candidateCapture, context, null, proposed.CancellationToken);
        var registeredProjection = WmlStaticScalarProjection.Create(registered, proposed.CancellationToken);
        if (!registered.IsComplete || registeredProjection.Diagnostics.Count != 0
            || registeredProjection.Owners.Count(owner => owner.TagName == "unit_type"
                && WccScenarioFieldValueCodec.DecodeLiteral(owner.GetKnownValue("id")) == edit.Id) != 1)
            throw new InvalidOperationException("Unit ID '" + edit.Id + "' is duplicated or unresolved in the actual Core and campaign registry.");
        ValidateUnitUpdateRuntimeReferences(edit, registered, registeredProjection, proposed.CancellationToken);
        var bounds = UnitRegistryBounds(units[0]);
        foreach (var segment in proposed.GetProjectRegionSegments(edit.CandidateRange))
        {
            var masked = WmlAuthoredStructureScanner.Scan(segment.Text).MaskedText;
            for (var offset = 0; offset < segment.Text.Length; offset++)
                if ((segment.OutputOffset + offset < bounds.Start || segment.OutputOffset + offset >= bounds.Start + bounds.Length)
                    && !char.IsWhiteSpace(masked[offset])) throw new InvalidOperationException("The unit draft emits output outside its exact unit_type.");
        }
        var spans = proposed.GetProjectRegionSpans(edit.CandidateRange);
        var outputMask = WmlAuthoredStructureScanner.Scan(proposed.Evaluation.Text).MaskedText;
        for (var offset = bounds.Start; offset < bounds.Start + bounds.Length; offset++)
            if (!spans.Any(span => offset >= span.Start && offset < span.End) && !char.IsWhiteSpace(outputMask[offset]))
                throw new InvalidOperationException("The edited unit consumes existing output outside its authored draft.");
        foreach (var call in proposed.Evaluation.Invocations.Where(call => UnitUpdateCaller(call, edit.CandidateRange)))
        {
            if (call.Outcome is WmlStaticInvocationOutcome.Unsupported or WmlStaticInvocationOutcome.Unresolved or WmlStaticInvocationOutcome.Computed
                or WmlStaticInvocationOutcome.Include or WmlStaticInvocationOutcome.IncludeUnresolved)
                throw new InvalidOperationException("The edited unit invocation has no exact captured macro binding: {" + call.Name + "}.");
            if (call.Definition is { } definition)
            {
                var expected = helpers.Where(source => WmlMacroDefinitionScanner.Scan(source.Text).Any(item => item.Name == call.Name)).ToArray();
                if (expected.Length != 0)
                {
                    if (expected.Length != 1 || !WccUnitTypeUpdatePlanner.SameSource(definition.Source, expected[0])
                        || !UnitUpdatePhysicalDefinition(call.Name, definition, expected[0]))
                        throw new InvalidOperationException("Helper {" + call.Name + "} uses a different active definition than the captured Library recipe.");
                }
                else if (!capture.Sources.Any(source => WccUnitTypeUpdatePlanner.SameSource(source, definition.Source)
                    && UnitUpdatePhysicalDefinition(call.Name, definition, source)))
                    throw new InvalidOperationException("Helper {" + call.Name + "} is not an exact Core or existing campaign binding.");
            }
        }
        ValidateUnitUpdateResources(edit, proposed, candidateCapture);
        ValidateOutsideUnitInvocationBindings(edit, baseline, proposed, registryRanges, change);
        return Array.AsReadOnly(new[] { edit.CandidateRange }.Concat(registryRanges).ToArray());
    }

    private static bool UnitUpdatePhysicalDefinition(string name, WmlStaticSourceRange definition, WmlStaticSource source)
    {
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        return structure.Directives.Where(directive => directive.Name == "define").Any(start =>
        {
            var line = structure.Lines[start.Line]; var header = source.Text[line.StartOffset..line.ContentEndOffset].Trim();
            if (!header.StartsWith("#define " + name, StringComparison.Ordinal)
                || header.Length > 8 + name.Length && !char.IsWhiteSpace(header[8 + name.Length])) return false;
            var end = structure.Directives.FirstOrDefault(directive => directive.Name == "enddef" && directive.Line > start.Line
                && directive.DefinitionDepth == start.DefinitionDepth + 1);
            return end.Line > start.Line && definition.StartOffset >= line.StartOffset
                && definition.StartOffset + definition.Length <= structure.Lines[end.Line].EndOffsetExclusive;
        });
    }

    private static bool UnitUpdateCaller(WmlStaticInvocationTrace call, WmlStaticSourceRange range) =>
        call.InvocationOrigins.Any(origin => WccUnitTypeUpdatePlanner.InRange(origin, range))
        || call.ExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin => WccUnitTypeUpdatePlanner.InRange(origin, range)));

    private static void ValidateOutsideUnitInvocationBindings(WccUnitTypeUpdateEdit edit, PairedEvaluation baseline,
        PairedEvaluation proposed, IReadOnlyList<WmlStaticSourceRange> newRegistryRanges, WccScenarioFieldSourceChange? change)
    {
        var before = baseline.Evaluation.Invocations.Where(call => !UnitUpdateCaller(call, edit.OriginalRange)).ToArray();
        var after = proposed.Evaluation.Invocations.Where(call => !UnitUpdateCaller(call, edit.CandidateRange)
            && !newRegistryRanges.Any(range => UnitUpdateCaller(call, range))).ToArray();
        if (before.Length != after.Length) throw new InvalidOperationException("The unit update changes macro invocations outside its unit and new registry definitions.");
        bool SameMapped(WmlStaticSourceRange left, WmlStaticSourceRange right)
        {
            var mapped = change is null ? left : change.MapRange(left);
            return mapped is not null && WccUnitTypeUpdatePlanner.SameSource(mapped.Source, right.Source)
                && mapped.StartOffset == right.StartOffset && mapped.Length == right.Length;
        }
        for (var index = 0; index < before.Length; index++)
        {
            var left = before[index]; var right = after[index];
            if (left.Name != right.Name || left.Outcome != right.Outcome || (left.Definition is null) != (right.Definition is null)
                || left.Definition is { } definition && !SameMapped(definition, right.Definition!)
                || left.InvocationOrigins.Count != right.InvocationOrigins.Count || left.ExpansionStack.Count != right.ExpansionStack.Count
                || left.InvocationOrigins.Where((origin, ordinal) => !SameMapped(origin, right.InvocationOrigins[ordinal])).Any()
                || left.ExpansionStack.Where((frame, ordinal) => frame.MacroName != right.ExpansionStack[ordinal].MacroName
                    || !SameMapped(frame.Definition, right.ExpansionStack[ordinal].Definition)
                    || frame.InvocationOrigins.Count != right.ExpansionStack[ordinal].InvocationOrigins.Count
                    || frame.InvocationOrigins.Where((origin, originOrdinal) => !SameMapped(origin, right.ExpansionStack[ordinal].InvocationOrigins[originOrdinal])).Any()).Any())
                throw new InvalidOperationException("The unit update changes an existing captured macro binding outside its unit: {" + left.Name + "}.");
        }
    }

    private static void ValidateUnitUpdateRuntimeReferences(WccUnitTypeUpdateEdit edit, WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, CancellationToken cancellationToken)
    {
        var types = WmlUnitReferenceProjection.Create(evaluation, projection, cancellationToken);
        if (!types.HasCompleteDeclaredInventory) throw new InvalidOperationException("The actual unit registry is incomplete for the edited unit.");
        foreach (var reference in types.References.Where(reference => UnitUpdateOwner(reference.Owner, edit.CandidateRange)))
        {
            if (!WmlUnitReferenceProjection.Mapped(reference.Field) || !WmlUnitReferenceProjection.TryLiteral(reference.Field.ValueText, out var value))
                throw new InvalidOperationException("The edited unit reference is not literal: " + reference.Field.Key);
            if (reference.Kind == WmlUnitReferenceKind.Advancement && value == "null") continue;
            var ids = reference.IsList ? WesnothRegistryIdList.Parse(value) : new[] { value };
            foreach (var id in ids)
                if (id.Length == 0 || types.Declarations.Count(declaration => declaration.Id == id) != 1)
                    throw new InvalidOperationException("The edited unit reference '" + id + "' has no unique active Core or campaign definition: " + reference.Field.Key);
        }
        var definitions = WmlDefinitionReferenceProjection.Create(evaluation, projection, cancellationToken);
        foreach (var reference in definitions.References.Where(reference => reference.Kind is WmlDefinitionReferenceKind.Race or WmlDefinitionReferenceKind.MovementType
            && UnitUpdateOwner(reference.Owner, edit.CandidateRange)))
            if (!definitions.TryObserveField(reference.Owner, reference.Field, out var id)
                || !WmlUnitReferenceProjection.IsStaticName(id) || definitions.Declarations.Count(declaration => declaration.Kind == reference.Kind && declaration.Id == id) != 1)
                throw new InvalidOperationException("The edited unit needs one active Core or campaign " + reference.Kind + " definition: " + reference.Field.ValueText);
        foreach (var owner in projection.Owners.Where(owner => UnitUpdateOwner(owner, edit.CandidateRange)))
            foreach (var key in new[] { "abilities_list", "specials_list" })
                if (owner.Fields.TryGetValue(key, out var field))
                {
                    if (!WmlUnitReferenceProjection.Mapped(field) || !WmlUnitReferenceProjection.TryLiteral(field.ValueText, out var value))
                        throw new InvalidOperationException("The edited unit registry reference is not literal: " + key);
                    var kind = key == "abilities_list" ? "abilities" : "weapon_specials";
                    foreach (var id in WesnothRegistryIdList.Parse(value))
                        if (projection.Owners.Where(container => container.TagName == kind && container.Parent is { TagName: "units", Parent: null })
                            .SelectMany(container => container.DirectChildren).Count(entry => UnitRegistryIdentity(entry) == id) != 1)
                            throw new InvalidOperationException("Registry entry '" + id + "' has no unique active Core or campaign definition.");
                }
    }

    private static void ValidateUnitUpdateResources(WccUnitTypeUpdateEdit edit, PairedEvaluation proposed, WccStaticSourceCaptureResult capture)
    {
        if (capture.IncludeResolver is not IWmlStaticResourceResolver resolver) throw new InvalidOperationException("Unit resources need the actual captured project and Core resolver.");
        var resources = WmlCheckResourceProjection.Create(proposed.Evaluation, proposed.Projection, _ => true, proposed.CancellationToken);
        if (resources.HasUncertainBinaryPathInventory) throw new InvalidOperationException("The campaign's resource search paths are unresolved.");
        var mounts = new List<string> { "core" };
        foreach (var resource in resources.Owners.Where(resource => resource.OriginalOwner is { TagName: "binary_path", Parent: null }))
        {
            if (!resource.HasProvenTopLevelExtent || !WmlResourceValueTextDecoder.TryDecode(resource.ValueOwner.GetKnownValue("path"), out var path, out _)
                || !WmlResourceLookupPaths.TryMapDataPath(path, out var mount)) throw new InvalidOperationException("A campaign binary_path is not an exact captured resource mount.");
            mounts.Add(mount);
        }
        var composites = new WmlCheckResourceCompositeAssignments(proposed.Evaluation, proposed.CancellationToken);
        var dependencyPaths = edit.DependencySources.Select(source => source.Path).ToHashSet(PathComparer);
        var helperCandidates = edit.HelperBindings.Select(row => (row.Receipt, row.Root, row.Revision,
            Candidate: WccUnitTypeMaterializationPlanner.RenderUpdateHelperCandidate(row.Receipt,
                row.Root, edit.ProjectRootPath, edit.TextDomain, edit.ResourceTransfers))).ToArray();
        var projectRoots = WccStaticSourceCapture.GetCapturedHelperSourceRoots(capture)
            .Where(row => !row.IsCore && PathComparer.Equals(row.RootPath, edit.ProjectRootPath)).ToArray();
        if (projectRoots.Length != 1) throw new InvalidOperationException("Helper resources need one exact campaign namespace.");
        var libraryOwners = helperCandidates.Select(row => (Source: row.Candidate.Source, row.Root, row.Revision))
            .Concat(edit.RegistryBindings.Select(row => (Source: WccUnitTypeMaterializationPlanner.GetRegistrySources([row.Receipt],
                edit.ProjectRootPath, edit.TextDomain).Single(), row.Root, row.Revision))).ToArray();
        foreach (var resource in resources.Owners.Where(resource => resource.OriginalOwner.OpeningOrigin is { } origin
            && (UnitUpdateOwner(resource.OriginalOwner, edit.CandidateRange) || dependencyPaths.Contains(origin.Source.Path))))
            foreach (var key in resource.OriginalOwner.Fields.Keys)
            {
                var kind = WmlResourceFieldCatalog.Find(resource.OriginalOwner, key);
                if (kind is null) continue;
                if (kind == WmlResourceFieldKind.Lua) throw new InvalidOperationException("Lua resource closure requires a separate authored script copy contract.");
                var field = resource.ValueOwner.Fields.GetValueOrDefault(key) ?? resource.OriginalOwner.Fields[key];
                if (field.ValueText is null) field = composites.Observe(resource.OriginalOwner, key, proposed.CancellationToken) ?? field;
                if (field.Contributions.LastOrDefault() is not { ValueMappingIsExact: true } contribution || contribution.ValueOrigins.Count == 0
                    || !WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var value, out _)) throw new InvalidOperationException("Resource field '" + key + "' needs an exact literal captured value.");
                if (value.Length == 0 || value is "none" or "unit_image" && key is "profile" or "small_profile" or "portrait") continue;
                var syntax = kind == WmlResourceFieldKind.Sound ? WmlResourceReferenceSyntax.ParseSound(value, cancellationToken: proposed.CancellationToken)
                    : kind is WmlResourceFieldKind.Image or WmlResourceFieldKind.AnimationImage ? WmlResourceReferenceSyntax.ParseImage(value, kind == WmlResourceFieldKind.AnimationImage, cancellationToken: proposed.CancellationToken)
                    : WmlResourceReferenceSyntax.ParseBinaryFilename(value, proposed.CancellationToken);
                if (!syntax.IsComplete) throw new InvalidOperationException("Resource '" + value + "' needs supported literal resource syntax: " + string.Join("; ", syntax.Issues.Select(issue => issue.Reason)));
                var folder = kind == WmlResourceFieldKind.Sound ? "sounds" : kind == WmlResourceFieldKind.Music ? "music" : kind == WmlResourceFieldKind.Map ? "maps" : "images";
                var transferred = false;
                foreach (var helper in helperCandidates)
                    foreach (var binding in helper.Candidate.Resources.Where(binding => binding.Field == key
                        && contribution.ValueOrigins.Any(origin => SameUnitUpdateResourceRange(origin, binding.Origin))))
                    {
                        if (contribution.ValueOrigins.Count != 1 || contribution.CompositeAssignmentKeys.Count != 0
                            || !WmlResourceValueTextDecoder.TryDecode(binding.Origin.Text, out var expectedValue, out _) || value != expectedValue
                            || syntax.Filenames.Count != 1 || syntax.Filenames[0] != binding.Filename)
                            throw new InvalidOperationException("Helper {" + helper.Receipt.Name + "} resource differs from its exact rewritten authored assignment.");
                        var relative = folder + "/" + binding.Filename;
                        var member = WccStaticSourceCapture.FindCapturedResourceMember(capture, projectRoots[0].RevisionId, relative);
                        var destination = Path.Combine(edit.ProjectRootPath, relative.Replace('/', Path.DirectorySeparatorChar));
                        if (member is null || member.IsCore || member.PhysicalPath != destination)
                            throw new InvalidOperationException("Helper {" + helper.Receipt.Name + "} resource is not its exact reviewed campaign member: " + relative);
                        transferred = true;
                    }
                foreach (var filename in syntax.Filenames)
                {
                    foreach (var library in libraryOwners.Where(row => !transferred && contribution.ValueOrigins.Any(origin => PathComparer.Equals(origin.Source.Path, row.Source.Path))))
                    {
                        var aliases = folder == "images" && (filename.EndsWith(".png", StringComparison.Ordinal) || filename.EndsWith(".jpg", StringComparison.Ordinal))
                            ? new[] { filename, filename[..^4] + ".webp" } : new[] { filename };
                        if (aliases.Any(alias => WccStaticSourceCapture.FindCapturedResourceMember(edit.ManagedCapture, library.Revision, folder + "/" + alias) is { IsCore: false }))
                            throw new InvalidOperationException("Library-owned helper resource '" + filename + "' needs a checked asset copy before this unit can be saved; a Core or campaign filename match does not prove the same resource.");
                    }
                    if (!WmlResourceLookupPaths.Create(filename, folder, mounts, folder == "images", proposed.CancellationToken, resolver.InstallationResourceFolders).Candidates
                        .Any(path => resolver.ResolveResource(path, edit.CandidateSource) is { Exists: WmlStaticTruth.True, MembershipIdentity.Length: > 0 }))
                        throw new InvalidOperationException("Resource '" + filename + "' is not available in the actual campaign or Core. Library-owned helper assets need a separate checked asset copy.");
                }
            }
    }

    private static bool SameUnitUpdateResourceRange(WmlStaticSourceRange left, WmlStaticSourceRange right)
        => WccUnitTypeUpdatePlanner.SameSource(left.Source, right.Source)
            && left.StartOffset == right.StartOffset && left.Length == right.Length;

    private static bool UnitUpdateOwner(WmlStaticScalarOwner owner, WmlStaticSourceRange range)
    {
        for (var current = owner; current is not null; current = current.Parent)
            if (current.TagName == "unit_type" && current.OpeningOrigin is { } origin && WccUnitTypeUpdatePlanner.InRange(origin, range)) return true;
        return false;
    }
}
