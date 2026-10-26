using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateDifficultyFieldBinding(WccScenarioDifficultyFieldEdit edit,
        WccScenarioFieldEditSessionState state, WmlStaticScalarOwner scenario, WmlStaticScalarOwner owner,
        WccStaticSourceCaptureResult capture, WmlStaticScalarOwner? campaign)
    {
        var snapshot = edit.Snapshot;
        if (state.ReviewScope != WccScenarioFieldReviewScope.Scenario || state.Plan is null
            || !ReferenceEquals(edit.Source, state.Source) || capture.EntrySource is null || campaign is null
            || !WccScenarioDifficultyFieldPlanner.SameSource(snapshot.MainSource, capture.EntrySource)
            || !snapshot.Plan!.Contexts.Select(context => context.SemanticKey).SequenceEqual(
                state.Plan.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal))
            throw new InvalidOperationException("Difficulty conversion requires its exact authored source, captured campaign main and complete fixed gameplay plan.");
        var root = owner;
        while (root.Parent is not null) root = root.Parent;
        if (!WccScenarioDifficultyFieldPlanner.SameOwner(root, scenario))
            throw new InvalidOperationException("The converted field is outside its exact captured scenario session.");
        // The full captured MENU and each declared gameplay context were already
        // verified by ReviewProjectAsync. Direct field conversion requires that
        // exact context topology, not a particular creator main-file layout.
        // Imported literal owners retain the same fresh source and field proofs.
        var fresh = WccScenarioDifficultyFieldPlanner.Read(snapshot.MainSource, state.Source, owner,
            snapshot.Key, state.Plan, snapshot.Authoring, snapshot.InsertionIntent);
        if (!WccScenarioDifficultyFieldPlanner.Equivalent(snapshot, fresh))
            throw new InvalidOperationException(fresh.CanEdit ? "The direct field's captured source topology changed." : fresh.Diagnostic);
        _ = WccScenarioDifficultyFieldPlanner.BuildEdit(fresh, edit.Mode, edit.SelectedContextSemanticKey, edit.SelectedValue, edit.Values);
    }

    private static void ValidateProjectDifficultyFields(WccProjectComposedFile file,
        PairedEvaluation baseline, PairedEvaluation proposed, ProjectOwnerLookup oldOwners, ProjectOwnerLookup newOwners,
        WmlEvaluationContext context, WccScenarioFieldSourceChange change, WmlStaticSource mainSource,
        WmlCampaignEvaluationPlan? candidatePlan, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (file.Request.DifficultyFields.Count == 0) return;
        var allowedBefore = new List<(WmlStaticScalarOwner Owner, string Key, WmlStaticSourceRange Assignment)>();
        var allowedMacroBefore = new List<(WmlStaticScalarOwner Owner, string Key, WmlStaticScalarContribution Contribution)>();
        var allowedAfter = new List<(WmlStaticScalarOwner Owner, string Key, WmlStaticSourceRange Assignment)>();
        var converted = new List<(WccScenarioDifficultyFieldEdit Edit, WmlStaticScalarOwner Before,
            WmlStaticScalarOwner After, WccScenarioDifficultyFieldSnapshot Current)>();
        var candidateMain = change.Substitute(mainSource);
        foreach (var edit in file.Request.DifficultyFields)
        {
            var before = LookupProjectOwner(oldOwners, edit.Snapshot.Owner, diagnostics);
            var after = LookupProjectOwner(newOwners, edit.Snapshot.Owner, diagnostics);
            if (context.CampaignDefine is null)
            {
                if (before is not null || after is not null
                    || ProjectOwnerHasMatches(oldOwners, edit.Snapshot.Owner, diagnostics)
                    || ProjectOwnerHasMatches(newOwners, edit.Snapshot.Owner, diagnostics))
                    throw new InvalidOperationException("A converted initial field changed activation or acquired ambiguous menu output.");
                continue;
            }
            if (before is null || after is null)
                throw new InvalidOperationException("A converted initial field has no unique exact owner in every declared gameplay context.");
            WccScenarioDifficultyFieldPlanner.RequireLiteralOwner(before, file.Request.Source);
            WccScenarioDifficultyFieldPlanner.RequireLiteralOwner(after, file.Candidate.CandidateSource);
            if ((WccScenarioFieldContentScope.CheckOwner(before) ?? WccScenarioFieldContentScope.CheckOwner(after)) is { } refusal)
                throw new InvalidOperationException(refusal);
            var variants = edit.Values.Select((value, index) => (value, index))
                .Where(item => item.value.ContextSemanticKey == context.SemanticKey).ToArray();
            if (variants.Length != 1) throw new InvalidOperationException("A converted field has no exact intended literal for this reviewed context.");
            var index = variants[0].index;
            var oldAssignment = edit.Snapshot.Layout!.Assignments.Count == 0 ? null
                : edit.Snapshot.Layout.Assignments[edit.Snapshot.Mode == WccScenarioDifficultyValueMode.Shared ? 0 : index];
            if (edit.Snapshot.Layout.MacroContract is { } macroContract)
            {
                if (!WccScenarioDifficultyFieldPlanner.MatchesMacroContribution(macroContract, before,
                    edit.Snapshot.Key, context, out var contribution))
                    throw new InvalidOperationException("The macro field does not reproduce its exact sole captured contribution and active definition chain in this declared context.");
                allowedMacroBefore.Add((before, edit.Snapshot.Key, contribution));
            }
            else
            {
                if (edit.Snapshot.InsertionIntent is not null)
                    _ = WccScenarioDifficultyFieldPlanner.RequireAbsentInsertionRoute(before, edit.Snapshot.Key,
                        file.Request.Source, context);
                ValidateConvertedContribution(before, file.Request.Source, edit.Snapshot.Key, oldAssignment, null);
                if (oldAssignment is not null) allowedBefore.Add((before, edit.Snapshot.Key, oldAssignment.Assignment));
            }
            if (candidatePlan is null || candidatePlan.CampaignDefine != edit.Snapshot.Plan!.CampaignDefine
                || !candidatePlan.Contexts.Select(item => item.SemanticKey)
                    .SequenceEqual(edit.Values.Select(value => value.ContextSemanticKey), StringComparer.Ordinal))
                throw new InvalidOperationException("The candidate changed the fixed declared difficulty contexts.");
            var current = WccScenarioDifficultyFieldPlanner.Read(candidateMain, file.Candidate.CandidateSource,
                after, edit.Snapshot.Key, candidatePlan, edit.Snapshot.Authoring);
            if (!current.CanEdit || current.Mode != edit.Mode || current.Values.Count != edit.Values.Count
                || current.Layout!.MacroContract is not null)
                throw new InvalidOperationException(current.CanEdit ? "The candidate does not reproduce the requested difficulty mode." : current.Diagnostic);
            WccScenarioFieldValueCodec.TryGetDefinition(after.TagName, edit.Snapshot.Key, out var definition);
            for (var slot = 0; slot < edit.Values.Count; slot++)
            {
                var assignment = current.Layout!.Assignments[current.Mode == WccScenarioDifficultyValueMode.Shared ? 0 : slot];
                if (current.Values[slot].Define != edit.Values[slot].Define
                    || current.Values[slot].ContextSemanticKey != edit.Values[slot].ContextSemanticKey
                    || assignment.Literal != edit.EncodedValues[slot]
                    || !WccScenarioFieldValueCodec.Equivalent(definition!, assignment.Literal, edit.Values[slot].ValueText))
                    throw new InvalidOperationException("The generated source does not contain every intended literal and its exact translation encoding.");
                var oldSlot = edit.Snapshot.Layout.Assignments.Count == 0 ? null
                    : edit.Snapshot.Layout.Assignments[edit.Snapshot.Mode == WccScenarioDifficultyValueMode.Shared ? 0 : slot];
                ValidateConvertedTextdomain(file.Request.Source, oldSlot?.Assignment.StartOffset ?? edit.Snapshot.Layout.InsertionOffset,
                    file.Candidate.CandidateSource, assignment);
            }
            var newAssignment = current.Layout!.Assignments[current.Mode == WccScenarioDifficultyValueMode.Shared ? 0 : index];
            ValidateConvertedContribution(after, file.Candidate.CandidateSource, edit.Snapshot.Key, newAssignment, variants[0].value.ValueText);
            allowedAfter.Add((after, edit.Snapshot.Key, newAssignment.Assignment));
            converted.Add((edit, before, after, current));
        }
        ValidateRetainedDifficultyGroupFields(file, converted, context,
            allowedBefore, allowedAfter);
        ValidateConvertedFootprints(baseline, file.Regions.Where(region => region.Kind == "DifficultyField").Select(region => region.Before),
            allowedBefore, allowedMacroBefore);
        ValidateConvertedFootprints(proposed, file.Regions.Where(region => region.Kind == "DifficultyField").Select(region => region.After), allowedAfter);
    }

    private static void ValidateRetainedDifficultyGroupFields(WccProjectComposedFile file,
        IReadOnlyList<(WccScenarioDifficultyFieldEdit Edit, WmlStaticScalarOwner Before,
            WmlStaticScalarOwner After, WccScenarioDifficultyFieldSnapshot Current)> converted,
        WmlEvaluationContext context,
        List<(WmlStaticScalarOwner Owner, string Key, WmlStaticSourceRange Assignment)> allowedBefore,
        List<(WmlStaticScalarOwner Owner, string Key, WmlStaticSourceRange Assignment)> allowedAfter)
    {
        if (context.CampaignDefine is null || converted.Count == 0) return;
        var checkedOwners = new List<WmlStaticScalarOwner>();
        var oldRegions = file.Regions.Where(region => region.Kind == "DifficultyField").Select(region => region.Before).ToArray();
        foreach (var item in converted)
        {
            if (checkedOwners.Any(owner => WccScenarioDifficultyFieldPlanner.SameOwner(owner, item.Edit.Snapshot.Owner))) continue;
            checkedOwners.Add(item.Edit.Snapshot.Owner);
            var convertedKeys = converted.Where(other => WccScenarioDifficultyFieldPlanner.SameOwner(
                    other.Edit.Snapshot.Owner, item.Edit.Snapshot.Owner))
                .Select(other => other.Edit.Snapshot.Key).ToHashSet(StringComparer.Ordinal);
            var oldGroups = item.Edit.Snapshot.Layout!.Groups;
            var retainedKeys = oldGroups.SelectMany(group => group.Members)
                .Where(member => oldRegions.Any(region => ContainsDifficultyAssignment(region, member.Assignment)))
                .Select(member => member.Key).Where(key => !convertedKeys.Contains(key)).Distinct(StringComparer.Ordinal);
            foreach (var key in retainedKeys)
            {
                var oldSlots = oldGroups.SelectMany(group => group.Members.Where(member => member.Key == key)
                    .Select(member => (group.Define, Member: member))).ToArray();
                var defines = item.Edit.Snapshot.Values.Select(value => value.Define).ToArray();
                if (oldSlots.Length != defines.Length
                    || oldSlots.Select(slot => slot.Define).Distinct(StringComparer.Ordinal).Count() != oldSlots.Length
                    || defines.Any(define => !oldSlots.Any(slot => slot.Define == define)))
                    throw new InvalidOperationException("A retained grouped field has no unique literal slot in every fixed difficulty.");
                oldSlots = defines.Select(define => oldSlots.Single(slot => slot.Define == define)).ToArray();
                // The requested field's fresh candidate read has already proved
                // every literal group in this exact owner and fixed plan. Reuse
                // that immutable structure; each retained contribution still
                // receives its separate active all-context proof below.
                var afterSlots = item.Current.Layout!.Groups.SelectMany(group => group.Members.Where(member => member.Key == key)
                    .Select(member => (group.Define, Member: member))).ToArray();
                if (afterSlots.Length != defines.Length
                    || afterSlots.Select(slot => slot.Define).Distinct(StringComparer.Ordinal).Count() != afterSlots.Length
                    || defines.Any(define => !afterSlots.Any(slot => slot.Define == define)))
                    throw new InvalidOperationException("A retained grouped field changed its difficulty topology.");
                afterSlots = defines.Select(define => afterSlots.Single(slot => slot.Define == define)).ToArray();
                for (var index = 0; index < oldSlots.Length; index++)
                {
                    var old = oldSlots[index].Member;
                    var scalarEdits = file.Candidate.Edits.Where(edit => edit.Route.Key == key
                        && WccScenarioDifficultyFieldPlanner.SameOwner(edit.Route.Owner, item.Edit.Snapshot.Owner)
                        && edit.Route.Target is { } target && WccScenarioDifficultyFieldPlanner.SameRange(target, old.Value)).ToArray();
                    if (scalarEdits.Length > 1)
                        throw new InvalidOperationException("A retained grouped slot has conflicting scalar edit intentions.");
                    var scalar = scalarEdits.SingleOrDefault();
                    if (scalar is not null && (scalar.Route.Kind != WccScenarioFieldRouteKind.DirectToken
                        || scalar.Operation != WmlFieldOperation.Set || scalar.ValueInputKind != WccScenarioFieldValueInputKind.Literal
                        || scalar.ReplacementStart != old.Value.StartOffset || scalar.ReplacementLength != old.Value.Length
                        || scalar.RequestedSourceValue is null))
                        throw new InvalidOperationException("Only an exact direct literal value edit can accompany retained grouped-field conversion.");
                    var expectedLiteral = scalar?.RequestedSourceValue ?? old.Literal;
                    var current = afterSlots[index].Member;
                    var after = new WccScenarioDifficultyFieldAssignment(afterSlots[index].Define,
                        current.Assignment, current.Value, current.Assignment, current.Literal);
                    if (after.Literal != expectedLiteral
                        || scalar is not null && (scalar.ProposedValueOrigin is not { } authored
                            || !WccScenarioDifficultyFieldPlanner.SameRange(authored, after.Value)))
                        throw new InvalidOperationException("A retained grouped field changes an unrequested literal, translation encoding or authored scalar route.");
                    ValidateConvertedTextdomain(file.Request.Source, old.Assignment.StartOffset,
                        file.Candidate.CandidateSource, after);
                    if (item.Edit.Snapshot.Values[index].ContextSemanticKey != context.SemanticKey) continue;
                    var before = new WccScenarioDifficultyFieldAssignment(oldSlots[index].Define,
                        old.Assignment, old.Value, old.Assignment, old.Literal);
                    ValidateConvertedContribution(item.Before, file.Request.Source, key, before, null);
                    ValidateConvertedContribution(item.After, file.Candidate.CandidateSource, key, after,
                        scalar?.RequestedValue ?? WccScenarioFieldValueCodec.DecodeLiteral(old.Literal));
                    allowedBefore.Add((item.Before, key, old.Assignment));
                    allowedAfter.Add((item.After, key, after.Assignment));
                }
            }
        }
    }

    private static bool ContainsDifficultyAssignment(WmlStaticSourceRange region, WmlStaticSourceRange assignment) =>
        WccScenarioDifficultyFieldPlanner.SameSource(region.Source, assignment.Source)
        && assignment.StartOffset >= region.StartOffset
        && assignment.StartOffset + assignment.Length <= region.StartOffset + region.Length;

    private static bool MatchesDifficultyMacroFrameChain(IReadOnlyList<WccScenarioDifficultyFieldEdit> edits,
        WmlStaticSourceRange region, WmlStaticOutputSegment segment, int first, WmlEvaluationContext context)
    {
        foreach (var edit in edits)
        {
            if (edit.Snapshot.Layout?.MacroContract is not { } contract
                || !ContainsDifficultyAssignment(region, contract.Invocation)) continue;
            var root = segment.ExpansionStack[first];
            if (root.InvocationOrigins.Count != 1
                || !WccScenarioDifficultyFieldPlanner.SameRange(root.InvocationOrigins[0], contract.Invocation)) continue;
            var slots = contract.Slots.Where(slot => slot.ContextSemanticKey == context.SemanticKey
                && slot.Define == context.DifficultyDefine).ToArray();
            if (slots.Length != 1 || segment.ExpansionStack.Count - first > slots[0].ActiveDefinitions.Count) continue;
            // Wrapper whitespace can have only a prefix of the scalar's full
            // active ancestry. Every present descendant must still match that
            // exact ordered prefix; no other helper or definition is accepted.
            if (segment.ExpansionStack.Skip(first).Where((frame, index) =>
                !WccScenarioDifficultyFieldPlanner.SameRange(frame.Definition, slots[0].ActiveDefinitions[index])).Any()) continue;
            return true;
        }
        return false;
    }

    private static void ValidateConvertedTextdomain(WmlStaticSource before, int oldOffset,
        WmlStaticSource after, WccScenarioDifficultyFieldAssignment assignment)
    {
        var literal = assignment.Literal.TrimStart();
        if (!literal.StartsWith('_') || !literal[1..].TrimStart().StartsWith('"')) return;
        var oldDomain = WmlTextDomainCapture.BeforeOffset(before.Text, oldOffset);
        var newDomain = WmlTextDomainCapture.BeforeOffset(after.Text, assignment.Assignment.StartOffset);
        if (string.IsNullOrWhiteSpace(oldDomain) || oldDomain != newDomain)
            throw new InvalidOperationException("Grouped difficulty conversion moves a translated literal into a different or unproven text domain.");
    }

    private static void ValidateConvertedContribution(WmlStaticScalarOwner owner, WmlStaticSource source, string key,
        WccScenarioDifficultyFieldAssignment? expected, string? intended)
    {
        var resolution = owner.ResolveField(key);
        if (expected is null)
        {
            if (resolution.State != WmlStaticScalarResolutionState.ProvenAbsent || owner.Fields.ContainsKey(key))
                throw new InvalidOperationException("Insertion requires proven field absence in every declared context.");
            return;
        }
        if (resolution.State != WmlStaticScalarResolutionState.Known || !owner.Fields.TryGetValue(key, out var field)
            || field.Contributions.Count != 1
            || !WccScenarioDifficultyFieldPlanner.ExactLocalContribution(field.Contributions[0], source, out var assignment, out var value)
            || !WccScenarioDifficultyFieldPlanner.SameRange(expected.Assignment, assignment!)
            || !WccScenarioDifficultyFieldPlanner.SameRange(expected.Value, value!) || resolution.ValueText != expected.Literal)
            throw new InvalidOperationException("The converted field is not exactly its one expected direct contribution; an earlier, generated or overriding assignment remains.");
        WccScenarioFieldValueCodec.TryGetDefinition(owner.TagName, key, out var definition);
        if (!WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition!, resolution.ValueText)
            || intended is not null && !WccScenarioFieldValueCodec.Equivalent(definition!, resolution.ValueText, intended))
            throw new InvalidOperationException("The converted field does not resolve to its intended supported literal.");
    }

    private static void ValidateConvertedFootprints(PairedEvaluation evaluated, IEnumerable<WmlStaticSourceRange> regions,
        IReadOnlyList<(WmlStaticScalarOwner Owner, string Key, WmlStaticSourceRange Assignment)> allowed,
        IReadOnlyList<(WmlStaticScalarOwner Owner, string Key, WmlStaticScalarContribution Contribution)>? allowedMacros = null)
    {
        foreach (var region in regions)
        {
            var spans = evaluated.GetProjectRegionSpans(region);
            foreach (var owner in evaluated.Projection.Owners)
            {
                if (owner.OpeningOutputOffset is { } opening && spans.Any(span => opening >= span.Start && opening < span.End))
                    throw new InvalidOperationException("A difficulty conversion emits another owner inside its scalar-only footprint.");
                foreach (var (key, field) in owner.Fields)
                    foreach (var contribution in field.Contributions)
                        if (spans.Any(span => contribution.AssignmentOutputOffset >= span.Start && contribution.AssignmentOutputOffset < span.End)
                            && !allowed.Any(target => ReferenceEquals(target.Owner, owner) && target.Key == key
                                && contribution.AssignmentOrigin is { } assignment
                                && WccScenarioDifficultyFieldPlanner.SameRange(target.Assignment, assignment))
                            && !(allowedMacros?.Any(target => ReferenceEquals(target.Owner, owner) && target.Key == key
                                && ReferenceEquals(target.Contribution, contribution)) ?? false))
                            throw new InvalidOperationException("A difficulty conversion changes another scalar inside its approved field-only footprint.");
            }
        }
    }
}
