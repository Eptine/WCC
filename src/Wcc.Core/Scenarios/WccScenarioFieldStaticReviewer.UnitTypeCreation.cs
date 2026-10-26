using Wcc.Core.Library;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateUnitTypeCreationBinding(WccUnitTypeCreationEdit edit,
        WccScenarioFieldEditSessionState state, WmlStaticScalarOwner anchor,
        WmlCampaignEvaluationPlan? plan, WccStaticSourceCaptureResult capture)
    {
        if (state.ReviewScope != WccScenarioFieldReviewScope.CampaignMenu || plan is null ||
            !ReferenceEquals(edit.Source, state.Source) || !IsCampaignMenuSource(state, plan, anchor) ||
            edit.Snapshot.Plan is not { } oldPlan || oldPlan.CampaignMainTextHash != plan.CampaignMainTextHash ||
            oldPlan.CampaignDefine != plan.CampaignDefine ||
            !oldPlan.Contexts.Select(context => context.SemanticKey).SequenceEqual(plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal))
            throw new InvalidOperationException("Unit type creation needs the exact campaign menu source and fixed difficulties.");
        var fresh = WccUnitTypeCreationPlanner.Read(state.Source, edit.Snapshot.Campaign.Structure.Authoring, plan, capture.Sources);
        if (!fresh.CanCreate) throw new InvalidOperationException(fresh.Diagnostic);
        ValidateCampaignStructureOwner(fresh.Campaign.Structure, anchor);
        _ = WccUnitTypeCreationPlanner.CreateEdit(fresh, edit.Spec);
        if (edit.Spec.Base is { } basis)
        {
            WccUnitTypeCreationPlanner.ValidateCoreBaseEvidence(state.Snapshot, basis);
        }
        if (edit.Spec.Copy is { } copy)
            WccUnitTypeAuthoredCopyPlanner.ValidateSourceEvidence(state.Snapshot, copy);
        if (edit.Spec.Materialization is { } materialization)
            WccUnitTypeMaterializationPlanner.ValidateSourceEvidence(state.Snapshot, materialization);
        var sources = WccUnitTypeCreationPlanner.PlanSources(edit)
            .Concat(WccUnitTypeMaterializationPlanner.PlanMissingHelperSources(edit, capture.Sources)).ToArray();
        var binaryPlan = WccUnitTypeMaterializationPlanner.PlanBinaryDestinations(edit.Spec, state.ProjectRootPath);
        WccUnitTypeMaterializationPlanner.ValidateBinaryReuseMembership(binaryPlan.Reuses, capture, state.RootRevisionId);
        var binaries = binaryPlan.Creates
            .Select(binary => new WccCapturedBinaryCandidate(binary.DestinationPath, binary.SourceRevision)).ToArray();
        var directories = CaptureCreationDirectories(state.ProjectRootPath,
            sources.Select(source => source.Path).Concat(binaries.Select(binary => binary.DestinationPath)));
        _ = WccStaticSourceCapture.CreateCandidateCapture(capture, state.RootRevisionId, [], sources, binaries,
            directories.Select(path => new WccCapturedDirectoryCandidate(path)).ToArray());
    }

    private static IReadOnlyList<WmlStaticSourceRange> ValidateProjectUnitTypeCreation(WccUnitTypeCreationEdit edit,
        PairedEvaluation baseline, PairedEvaluation proposed, WccStaticSourceCaptureResult capture,
        WccStaticSourceCaptureResult candidateCapture, WmlEvaluationContext context, WmlCampaignEvaluationPlan plan,
        WmlStaticSingleplayerEnvironment environment, CancellationToken cancellationToken)
    {
        var source = WccUnitTypeCreationPlanner.PlanSources(edit).Single();
        var registryRanges = ValidateUnitRegistrySources(edit.Spec.Materialization?.Registries ?? [],
            Path.GetDirectoryName(edit.Source.Path)!, edit.Snapshot.TextDomain, baseline, proposed, candidateCapture,
            context, source.Path, WccUnitTypeMaterializationPlanner.PlanHelperSources(edit.Spec,
                Path.GetDirectoryName(edit.Source.Path)!, edit.Snapshot.TextDomain));
        ValidateSharedUnitHelpers(edit, baseline, proposed, candidateCapture, context);
        var candidate = candidateCapture.Sources.Where(file => PathComparer.Equals(file.Path, source.Path)).ToArray();
        if (candidate.Length != 1 || candidate[0].Text != source.Text || candidate[0].ContentHash != source.ContentHash ||
            baseline.Evaluation.Sources.Any(file => PathComparer.Equals(file.Path, source.Path)))
            throw new InvalidOperationException("The new unit source is not the exact missing file from its typed recipe.");
        var roots = proposed.Projection.Owners.Where(owner => owner.TagName == "unit_type" && owner.OpeningOrigin is { } opening
            && PathComparer.Equals(opening.Source.Path, source.Path)).ToArray();
        var visits = proposed.Evaluation.Includes.SelectMany(include => include.Resolution.Sources).Count(file => PathComparer.Equals(file.Path, source.Path));
        WccUnitTypeCreationRuntime.Validate(edit.Spec, capture, context, plan, cancellationToken: cancellationToken, creationSource: source);
        WccUnitTypeCreationRuntime.Validate(edit.Spec, candidateCapture, context, plan,
            expectCreatedType: context.CampaignDefine is not null, cancellationToken: cancellationToken, creationSource: source);
        if (context.CampaignDefine is null)
        {
            if (roots.Length != 0 || visits != 0 || proposed.Evaluation.Sources.Any(file => PathComparer.Equals(file.Path, source.Path)))
                throw new InvalidOperationException("The new campaign unit type is unexpectedly active in the menu.");
            return [];
        }
        if (roots.Length != 1 || visits != 1 || roots[0].Parent is not { TagName: "units", Parent: null } container ||
            container.OpeningOrigin is not { } containerOpening || !PathComparer.Equals(containerOpening.Source.Path, edit.Source.Path) ||
            containerOpening.Source.ContentHash != candidateCapture.EntrySource!.ContentHash ||
            container.ClosingOrigin is not { } containerClosing || !PathComparer.Equals(containerClosing.Source.Path, edit.Source.Path) ||
            containerClosing.Source.ContentHash != candidateCapture.EntrySource.ContentHash ||
            container.IsAmended || container.HasUnresolvedContributions)
            throw new InvalidOperationException("The new type must load exactly once inside the campaign's literal units container.");
        var range = new WmlStaticSourceRange(candidate[0], 0, source.Text.Length);
        if (edit.Spec.Copy is { } copied)
            ValidateAuthoredUnitCopyFootprint(copied, edit.Spec.Materialization, source, roots[0], proposed.Evaluation, capture, context, cancellationToken);
        else
        {
            var literal = new WmlStaticEvaluator().Evaluate(new(source, context, IsInitialEnvironmentComplete: true), cancellationToken);
            var expectedProjection = WmlStaticScalarProjection.Create(literal, cancellationToken);
            if (!literal.IsComplete || expectedProjection.Diagnostics.Count != 0 || expectedProjection.Owners.Count(owner => owner.Parent is null) != 1)
                throw new InvalidOperationException("The requested literal unit payload cannot be independently verified.");
            ValidateTree(roots[0], expectedProjection.Owners.Single(owner => owner.Parent is null));
        }
        var spans = proposed.GetProjectRegionSpans(range);
        var masked = WmlAuthoredStructureScanner.Scan(proposed.Evaluation.Text).MaskedText;
        if (roots[0].OpeningOutputOffset is not { } start || roots[0].ClosingOutputOffset is not { } end || roots[0].ClosingOrigin is not { } closing)
            throw new InvalidOperationException("The created unit has no exact expanded boundaries.");
        bool Inside(int offset) => spans.Any(span => offset >= span.Start && offset < span.End);
        for (var offset = start; offset < end + closing.Length; offset++)
            if (!Inside(offset) && !char.IsWhiteSpace(masked[offset])) throw new InvalidOperationException("A created type consumes existing source output.");
        foreach (var span in spans)
            for (var offset = span.Start; offset < span.End; offset++)
                if ((offset < start || offset >= end + closing.Length) && !char.IsWhiteSpace(masked[offset]))
                    throw new InvalidOperationException("The new source emits output outside its exact unit type.");
        return new[] { range }.Concat(registryRanges).ToArray();

        void ValidateTree(WmlStaticScalarOwner actual, WmlStaticScalarOwner desired)
        {
            if (actual.TagName != desired.TagName || actual.IsAmended || actual.HasUnresolvedContributions ||
                actual.Fields.Count != desired.Fields.Count || actual.DirectChildren.Count != desired.DirectChildren.Count ||
                actual.OpeningOrigin is not { } opening || actual.ClosingOrigin is not { } close ||
                !InsideProposedScalar(opening, range) || !InsideProposedScalar(close, range))
                throw new InvalidOperationException("The created unit differs from its exact literal payload.");
            foreach (var pair in desired.Fields)
                if (!actual.Fields.TryGetValue(pair.Key, out var field) || !field.IsKnown || !pair.Value.IsKnown ||
                    field.ValueText != pair.Value.ValueText || field.Contributions.Count != 1 ||
                    field.Contributions[0].AssignmentOrigins.Count != 1 || field.Contributions[0].AssignmentOrigin is not { } assignment ||
                    !InsideProposedScalar(assignment, range) || !field.Contributions[0].ValueMappingIsExact ||
                    field.Contributions[0].CompositeAssignmentKeys.Count != 0 || field.Contributions[0].ValueOrigins.Count == 0 ||
                    field.Contributions[0].ValueOrigins.Any(origin => !InsideProposedScalar(origin, range)))
                    throw new InvalidOperationException("A created unit field lacks its exact requested literal contribution: " + pair.Key);
            for (var index = 0; index < desired.DirectChildren.Count; index++) ValidateTree(actual.DirectChildren[index], desired.DirectChildren[index]);
        }
    }

    private static IReadOnlyList<string> CaptureCreationDirectories(string root, IEnumerable<string> destinations)
    {
        var paths = new List<string>();
        foreach (var destination in destinations)
        {
            var absence = Wcc.Core.Projects.WccTextDiskRevisionCapture.CaptureCreationAbsence(root, destination);
            if (!absence.IsCaptured || absence.Absence is null)
                throw new InvalidOperationException(absence.ErrorMessage ?? "A reviewed creation path has no exact absent route.");
            paths.AddRange(absence.Absence.RequiredDirectories);
        }
        return Array.AsReadOnly(paths.Distinct(StringComparer.Ordinal).OrderBy(path => path.Length).ThenBy(path => path, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateSharedUnitHelpers(WccUnitTypeCreationEdit edit, PairedEvaluation baseline,
        PairedEvaluation proposed, WccStaticSourceCaptureResult capture, WmlEvaluationContext context)
    {
        var helpers = WccUnitTypeMaterializationPlanner.PlanHelperSources(edit.Spec,
            Path.GetDirectoryName(edit.Source.Path)!, edit.Snapshot.TextDomain);
        var registries = helpers.Where(file => WccUnitTypeMaterializationPlanner.CaptureRegistry(file, out _) is not null).ToArray();
        foreach (var helper in helpers)
        {
            if (WccUnitTypeMaterializationPlanner.CaptureRegistry(helper, out _) is not null) continue;
            var members = capture.Sources.Where(source => PathComparer.Equals(source.Path, helper.Path)).ToArray();
            if (members.Length != 1 || members[0].Path != helper.Path || members[0].Text != helper.Text || members[0].ContentHash != helper.ContentHash)
                throw new InvalidOperationException("The shared helper differs from its exact captured copy: " + Path.GetFileName(helper.Path));
            var visits = proposed.Evaluation.Includes.SelectMany(include => include.Resolution.Sources)
                .Count(source => PathComparer.Equals(source.Path, helper.Path));
            if (context.CampaignDefine is null)
            {
                if (visits != 0 || proposed.Evaluation.Sources.Any(source => PathComparer.Equals(source.Path, helper.Path)))
                    throw new InvalidOperationException("A shared unit helper is active in the campaign menu.");
            }
            else if (visits != 1)
                throw new InvalidOperationException("The campaign must load each shared helper exactly once before its unit consumers: " + Path.GetFileName(helper.Path));
            // A newly introduced helper may expand only within the new type. Existing
            // consumers remain protected by the ordinary full outside-output comparison.
            if (!baseline.Evaluation.Sources.Any(source => PathComparer.Equals(source.Path, helper.Path)))
                foreach (var segment in proposed.Evaluation.Segments.Where(segment => PathComparer.Equals(segment.Origin.Source.Path, helper.Path)))
                    if (!segment.ExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin =>
                        PathComparer.Equals(origin.Source.Path, WccUnitTypeCreationPlanner.PlanSources(edit).Single().Path)
                        || registries.Any(file => PathComparer.Equals(origin.Source.Path, file.Path)))))
                        if (!string.IsNullOrWhiteSpace(WmlAuthoredStructureScanner.Scan(segment.Text).MaskedText))
                            throw new InvalidOperationException("A new shared helper emits output outside the copied unit.");
        }
    }

    private static void ValidateAuthoredUnitCopyFootprint(WccUnitTypeAuthoredCopySource receipt, WccUnitTypeMaterializationPlan? materialization,
        WmlStaticSource source,
        WmlStaticScalarOwner actual, WmlStaticEvaluationResult actualEvaluation, WccStaticSourceCaptureResult capture,
        WmlEvaluationContext context, CancellationToken cancellationToken)
    {
        var dependencyPlan = materialization is null
            ? WccUnitTypeDependencyPlanner.Plan(receipt, capture, context, source, cancellationToken)
            : WccUnitTypeDependencyPlanner.Plan(receipt, capture, context, materialization, source, cancellationToken);
        if (!dependencyPlan.IsComplete || dependencyPlan.Evaluation is not { IsComplete: true } expectedEvaluation)
            throw new InvalidOperationException("The authored copy needs additional resources: " +
                string.Join("; ", dependencyPlan.Issues.Select(issue => issue.Diagnostic)));
        var projection = WmlStaticScalarProjection.Create(expectedEvaluation, cancellationToken);
        var roots = projection.Owners.Where(owner => owner.TagName == "unit_type" && owner.OpeningOrigin is { } opening
            && PathComparer.Equals(opening.Source.Path, source.Path)).ToArray();
        if (roots.Length != 1 || actual.IsAmended || roots[0].IsAmended)
            throw new InvalidOperationException("The authored copy has no sole independently proved literal unit root.");
        var desired = roots[0];
        // Invocation receipts also cover macros which emit no output.
        var range = new WmlStaticSourceRange(source, 0, source.Text.Length);
        var observedCalls = LocalCalls(actualEvaluation); var requestedCalls = LocalCalls(expectedEvaluation);
        if (observedCalls.Length != requestedCalls.Length)
            throw new InvalidOperationException("The copied unit's authored macro invocations changed.");
        for (var index = 0; index < observedCalls.Length; index++)
        {
            var observed = observedCalls[index]; var requested = requestedCalls[index];
            if (observed.Name != requested.Name || observed.Outcome != requested.Outcome
                || (observed.Definition is null) != (requested.Definition is null)
                || observed.Definition is { } definition && !SameRange(definition, requested.Definition!)
                || observed.InvocationOrigins.Count != requested.InvocationOrigins.Count
                || observed.ExpansionStack.Count != requested.ExpansionStack.Count
                || observed.ExpansionStack.Where((frame, frameIndex) => frame.MacroName != requested.ExpansionStack[frameIndex].MacroName
                    || !SameRange(frame.Definition, requested.ExpansionStack[frameIndex].Definition)
                    || frame.InvocationOrigins.Count != requested.ExpansionStack[frameIndex].InvocationOrigins.Count
                    || frame.InvocationOrigins.Where((origin, originIndex) => !SameRange(origin,
                        requested.ExpansionStack[frameIndex].InvocationOrigins[originIndex])).Any()).Any()
                || observed.InvocationOrigins.Where((origin, originIndex) => !SameRange(origin, requested.InvocationOrigins[originIndex])).Any())
                throw new InvalidOperationException("A copied invocation does not use its exact independently captured Core binding: " + requested.Name);
        }

        var actualBounds = Bounds(actual); var desiredBounds = Bounds(desired);
        if (!actualEvaluation.Text.AsSpan(actualBounds.Start, actualBounds.Length)
                .SequenceEqual(expectedEvaluation.Text.AsSpan(desiredBounds.Start, desiredBounds.Length)))
            throw new InvalidOperationException("The copied unit output differs from its captured authored payload and Core dependencies.");
        var actualSegments = Footprint(actualEvaluation, actualBounds); var desiredSegments = Footprint(expectedEvaluation, desiredBounds);
        if (actualSegments.Count != desiredSegments.Count)
            throw new InvalidOperationException("The copied unit's output contribution boundaries changed.");
        for (var index = 0; index < actualSegments.Count; index++)
        {
            var observed = actualSegments[index]; var requested = desiredSegments[index];
            if (observed.Offset != requested.Offset || observed.Text != requested.Text || !SameRange(observed.Origin, requested.Origin)
                || observed.Frames.Count != requested.Frames.Count)
                throw new InvalidOperationException("The copied unit output no longer comes from its exact captured source and Core definitions.");
            for (var frameIndex = 0; frameIndex < observed.Frames.Count; frameIndex++)
            {
                var frame = observed.Frames[frameIndex]; var expected = requested.Frames[frameIndex];
                if (frame.MacroName != expected.MacroName || !SameRange(frame.Definition, expected.Definition)
                    || frame.InvocationOrigins.Count != expected.InvocationOrigins.Count
                    || frame.InvocationOrigins.Where((origin, originIndex) => !SameRange(origin, expected.InvocationOrigins[originIndex])).Any())
                    throw new InvalidOperationException("A copied unit macro uses a different active physical definition or invocation.");
            }
        }
        WmlStaticInvocationTrace[] LocalCalls(WmlStaticEvaluationResult evaluation) => evaluation.Invocations
            .Where(call => call.InvocationOrigins.Count != 0 && (call.InvocationOrigins.Any(origin => InsideProposedScalar(origin, range))
                || call.ExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin => InsideProposedScalar(origin, range))))).ToArray();

        static (int Start, int Length) Bounds(WmlStaticScalarOwner owner)
        {
            if (owner.OpeningOutputOffset is not { } start || owner.ClosingOutputOffset is not { } end || owner.ClosingOrigin is not { } close)
                throw new InvalidOperationException("A copied unit lacks complete expanded source boundaries.");
            return (start, end + close.Length - start);
        }
        static IReadOnlyList<(int Offset, string Text, WmlStaticSourceRange Origin, IReadOnlyList<WmlStaticExpansionFrame> Frames)>
            Footprint(WmlStaticEvaluationResult evaluation, (int Start, int Length) bounds)
        {
            var result = new List<(int, string, WmlStaticSourceRange, IReadOnlyList<WmlStaticExpansionFrame>)>();
            foreach (var segment in evaluation.Segments)
            {
                var start = Math.Max(bounds.Start, segment.OutputOffset);
                var end = Math.Min(bounds.Start + bounds.Length, segment.OutputOffset + segment.Text.Length);
                if (start >= end) continue;
                if (segment.Origin.Length != segment.Text.Length)
                    throw new InvalidOperationException("A copied output segment has no exact source mapping.");
                var skipped = start - segment.OutputOffset;
                result.Add((start - bounds.Start, segment.Text.Substring(skipped, end - start),
                    new(segment.Origin.Source, segment.Origin.StartOffset + skipped, end - start), segment.ExpansionStack));
            }
            return result;
        }
    }
}
