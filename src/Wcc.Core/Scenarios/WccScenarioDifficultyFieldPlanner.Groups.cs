using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioDifficultyFieldPlanner
{
    private static IReadOnlyList<WccScenarioDifficultyFieldGroup> ReadGroups(WmlStaticSource source,
        WmlAuthoredStructure scan, IReadOnlyList<WmlEditorToken> directives, IReadOnlyList<Condition> conditions,
        WmlStaticScalarOwner owner, WmlCampaignEvaluationPlan plan)
    {
        var candidates = new List<WccScenarioDifficultyFieldGroup>();
        foreach (var condition in conditions.Where(block => block.Start > owner.OpeningOrigin!.StartOffset
                     && block.End <= owner.ClosingOrigin!.StartOffset && block.Define is not null
                     && block.DirectiveCount == 2 && !block.HasElse))
        {
            if (conditions.Any(other => !ReferenceEquals(other, condition) && other.Start < condition.Start && other.End > condition.End)
                || scan.Macros.Any(macro => macro.StartOffset < condition.BodyEnd && macro.EndOffsetExclusive > condition.BodyStart)
                || directives.Any(token => token.StartOffset >= condition.BodyStart && token.StartOffset < condition.BodyEnd)) continue;
            var members = new List<WccScenarioDifficultyFieldGroupMember>();
            var body = scan.MaskedText[condition.BodyStart..condition.BodyEnd].ToCharArray();
            var valid = true;
            foreach (var (key, field) in owner.Fields)
                foreach (var contribution in field.Contributions)
                {
                    var origin = contribution.AssignmentOrigin;
                    if (origin is null || origin.StartOffset < condition.BodyStart || origin.StartOffset >= condition.BodyEnd) continue;
                    if (!ExactInitialComponentContribution(contribution, key, source, out var assignment, out var value)
                        || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(value.Text)
                        || !IsOwnLine(source.Text, assignment.StartOffset)
                        || assignment.StartOffset + assignment.Length > condition.BodyEnd)
                    { valid = false; break; }
                    members.Add(new(key, assignment, value, value.Text)
                    { CompositeAssignment = ComponentContract(contribution, key, source) });
                    Array.Fill(body, ' ', assignment.StartOffset - condition.BodyStart, assignment.Length);
                }
            if (valid && members.Count != 0 && members.Select(member => member.Key).Distinct(StringComparer.Ordinal).Count() == members.Count
                && body.All(char.IsWhiteSpace))
                candidates.Add(new(condition.Define!, new(source, condition.Start, condition.End - condition.Start),
                    Array.AsReadOnly(members.OrderBy(member => member.Assignment.StartOffset)
                        .ThenBy(member => member.CompositeAssignment?.IndexOf(member.Key) ?? 0).ToArray())));
        }
        // A branch is reusable only when every sibling has one exact literal in
        // every declared context, and the corresponding branches have the same
        // sibling set. A partial income branch cannot lend authority to gold.
        var validKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in candidates.SelectMany(group => group.Members).Select(member => member.Key).Distinct(StringComparer.Ordinal))
        {
            var containing = candidates.Where(group => group.Members.Any(member => member.Key == key)).ToArray();
            var defines = containing.Select(group => group.Define).ToHashSet(StringComparer.Ordinal);
            if (containing.Length != plan.Difficulties.Count || defines.Count != containing.Length
                || !defines.SetEquals(plan.Difficulties.Select(row => row.Define))
                || owner.Fields[key].Contributions.Count != containing.Length) continue;
            var firstKeys = containing[0].Members.Select(member => member.Key).Order(StringComparer.Ordinal).ToArray();
            if (!containing.All(group => group.Members.Select(member => member.Key).Order(StringComparer.Ordinal).SequenceEqual(firstKeys, StringComparer.Ordinal))) continue;
            validKeys.Add(key);
        }
        return Array.AsReadOnly(candidates.Where(group => group.Members.All(member => validKeys.Contains(member.Key))).ToArray());
    }

    private static bool IsTranslated(string literal) => literal.TrimStart().StartsWith('_')
        && literal.TrimStart()[1..].TrimStart().StartsWith('"');

    private static bool SameGroups(IReadOnlyList<WccScenarioDifficultyFieldGroup> left, IReadOnlyList<WccScenarioDifficultyFieldGroup> right) =>
        left.Count == right.Count && left.Where((group, index) => group.Define != right[index].Define
            || !SameRange(group.Region, right[index].Region) || group.Members.Count != right[index].Members.Count
            || group.Members.Where((member, slot) => member.Key != right[index].Members[slot].Key
                || member.Literal != right[index].Members[slot].Literal
                || !SameRange(member.Assignment, right[index].Members[slot].Assignment)
                || !SameRange(member.Value, right[index].Members[slot].Value)
                || !SameCompositeContract(member.CompositeAssignment, right[index].Members[slot].CompositeAssignment)).Any()).Any() == false;

    internal sealed record GroupScalarLocation(int PatchStart, int Offset, int Length);
    internal sealed record GroupPatchPlan(IReadOnlyList<WccScenarioFieldSourcePatch> Patches,
        IReadOnlySet<WccScenarioFieldSourcePatch> AbsorbedScalarPatches,
        IReadOnlyDictionary<WccScenarioFieldCandidate, GroupScalarLocation> ScalarLocations);

    internal static GroupPatchPlan PlanGroupedPatches(IReadOnlyList<WccScenarioDifficultyFieldEdit> edits,
        WccScenarioFieldBatchCandidate? scalar)
    {
        var patches = new List<WccScenarioFieldSourcePatch>();
        var absorbed = new HashSet<WccScenarioFieldSourcePatch>();
        var locations = new Dictionary<WccScenarioFieldCandidate, GroupScalarLocation>();
        var remaining = edits.ToList();
        while (remaining.Count != 0)
        {
            var first = remaining[0];
            var ownerEdits = remaining.Where(edit => first.Snapshot.SameOwner(edit.Snapshot.Owner))
                .OrderBy(edit => edit.Snapshot.Layout!.Assignments.Count == 0 ? edit.Snapshot.Layout.InsertionOffset
                    : edit.Snapshot.Layout.Assignments.Min(assignment => assignment.Assignment.StartOffset)).ToArray();
            remaining.RemoveAll(edit => ownerEdits.Contains(edit));
            ComposeOwner(ownerEdits);
        }
        return new(Array.AsReadOnly(patches.ToArray()), absorbed, locations);

        void ComposeOwner(WccScenarioDifficultyFieldEdit[] ownerEdits)
        {
            foreach (var edit in ownerEdits)
            {
                _ = BuildEdit(edit.Snapshot, edit.Mode, edit.SelectedContextSemanticKey, edit.SelectedValue, edit.Values);
                if (!ReferenceEquals(edit.Source, ownerEdits[0].Source)
                    || !edit.Values.Select(value => value.ContextSemanticKey).SequenceEqual(ownerEdits[0].Values.Select(value => value.ContextSemanticKey), StringComparer.Ordinal)
                    || !SameGroups(ownerEdits[0].Snapshot.Layout!.Groups, edit.Snapshot.Layout!.Groups))
                    throw new InvalidOperationException("Grouped difficulty edits require one exact source, owner and fixed context plan.");
            }
            var source = ownerEdits[0].Source;
            var analysis = WmlEditorAnalysis.Build(source.Text);
            var childConditions = ChildConditionalDirectiveOffsets(source, ownerEdits[0].Snapshot.Owner, analysis);
            foreach (var batch in PartitionOwnerGroups(ownerEdits, analysis, childConditions))
                ComposeGroup(batch.Edits, batch.Groups, analysis, childConditions);
        }

        void ComposeGroup(WccScenarioDifficultyFieldEdit[] ownerEdits,
            IReadOnlyList<WccScenarioDifficultyFieldGroup> groups, WmlEditorAnalysis analysis,
            IReadOnlySet<int> childConditions)
        {
            var source = ownerEdits[0].Source;
            var layout = ownerEdits[0].Snapshot.Layout!;
            var converted = ownerEdits.ToDictionary(edit => edit.Snapshot.Key, StringComparer.Ordinal);
            var regions = GroupCompositionRegions(ownerEdits, groups);
            var anchor = regions.Length == 0 ? layout.InsertionOffset : regions[0].StartOffset;
            if (GroupRelocationProblem(ownerEdits, groups, source, analysis, childConditions) is { } problem)
                throw new InvalidOperationException(problem);
            var retained = groups.SelectMany(group => group.Members).Select(member => member.Key)
                .Where(key => !converted.ContainsKey(key)).Distinct(StringComparer.Ordinal).ToArray();
            var independent = retained.Length != 0 || ownerEdits.Any(edit => edit.Mode == WccScenarioDifficultyValueMode.Independent);
            var groupScalars = new Dictionary<WccScenarioDifficultyFieldGroupMember, WccScenarioFieldCandidate>();
            var sharedComposites = SharedCompositeAssignments(ownerEdits);
            var sharedScalars = new Dictionary<(int Assignment, string Key), WccScenarioFieldCandidate>();
            foreach (var scalarEdit in scalar?.Edits ?? [])
            {
                var shared = sharedComposites.Where(composite => scalarEdit.ReplacementStart < composite.Region.StartOffset + composite.Region.Length
                    && scalarEdit.ReplacementStart + scalarEdit.ReplacementLength >= composite.Region.StartOffset).ToArray();
                if (shared.Length != 0)
                {
                    var component = shared.Length == 1 ? shared[0].IndexOf(scalarEdit.Route.Key) : -1;
                    if (shared.Length != 1 || scalarEdit.Route.Target is not { } target
                        || component < 0
                        || !SameRange(target, shared[0].Value(component))
                        || scalarEdit.Operation != WmlFieldOperation.Set || scalarEdit.Route.Kind != WccScenarioFieldRouteKind.DirectToken
                        || !ownerEdits[0].Snapshot.SameOwner(scalarEdit.Route.Owner)
                        || scalarEdit.ValueInputKind != WccScenarioFieldValueInputKind.Literal || scalarEdit.RequestedSourceValue is null
                        || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(scalarEdit.RequestedSourceValue)
                        || converted.ContainsKey(scalarEdit.Route.Key))
                        throw new InvalidOperationException("A retained composite companion accepts only its exact direct literal value edit alongside conversion.");
                    var identity = (shared[0].Assignment.StartOffset, scalarEdit.Route.Key);
                    if (sharedScalars.TryGetValue(identity, out var previous) && previous.RequestedSourceValue != scalarEdit.RequestedSourceValue)
                        throw new InvalidOperationException("Conflicting literal edits target a composite companion.");
                    sharedScalars[identity] = scalarEdit;
                    absorbed.Add(scalar!.Patches.Single(patch => patch.Start == scalarEdit.ReplacementStart && patch.Length == scalarEdit.ReplacementLength));
                    continue;
                }
                var overlapped = groups.Where(group => scalarEdit.ReplacementStart < group.Region.StartOffset + group.Region.Length
                    && scalarEdit.ReplacementStart + scalarEdit.ReplacementLength > group.Region.StartOffset).ToArray();
                if (overlapped.Length == 0) continue;
                var matched = overlapped.SelectMany(group => group.Members).Where(member => member.Key == scalarEdit.Route.Key
                    && member.Value.StartOffset == scalarEdit.ReplacementStart && member.Value.Length == scalarEdit.ReplacementLength).ToArray();
                if (matched.Length != 1 || scalarEdit.Operation != WmlFieldOperation.Set
                    || scalarEdit.Route.Kind != WccScenarioFieldRouteKind.DirectToken
                    || !ownerEdits[0].Snapshot.SameOwner(scalarEdit.Route.Owner)
                    || scalarEdit.ValueInputKind != WccScenarioFieldValueInputKind.Literal
                    || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(scalarEdit.ReplacementText)
                    || converted.ContainsKey(scalarEdit.Route.Key))
                    throw new InvalidOperationException("A grouped difficulty sibling accepts only its exact direct literal value edit alongside conversion; keep other drafts and save them separately.");
                if (groupScalars.TryGetValue(matched[0], out var prior) && prior.ReplacementText != scalarEdit.ReplacementText)
                    throw new InvalidOperationException("Conflicting value edits target one grouped difficulty sibling.");
                groupScalars[matched[0]] = scalarEdit;
                absorbed.Add(scalar!.Patches.Single(patch => patch.Start == scalarEdit.ReplacementStart && patch.Length == scalarEdit.ReplacementLength));
            }
            var builder = new StringBuilder();
            foreach (var assignment in ownerEdits.SelectMany(edit => edit.Snapshot.Layout!.Assignments).Where(assignment => assignment.Define is null)
                         .DistinctBy(assignment => (assignment.Region.StartOffset, assignment.Region.Length)))
                builder.Append(Comments(assignment, layout.Newline, layout.Indent));
            foreach (var edit in ownerEdits.Where(edit => edit.Snapshot.Layout!.MacroContract is not null))
            {
                var contract = edit.Snapshot.Layout!.MacroContract!;
                // A macro has one physical statement even when it resolves to
                // different assignments in every context. Blank its prefix and
                // invocation solely to retain the trailing source comment once.
                var prefix = new WmlStaticSourceRange(source, contract.StatementRegion.StartOffset,
                    contract.Invocation.StartOffset + contract.Invocation.Length - contract.StatementRegion.StartOffset);
                builder.Append(Comments(new(null, prefix, contract.Invocation, contract.StatementRegion, ""), layout.Newline, layout.Indent));
            }
            foreach (var composite in sharedComposites)
                for (var component = 0; component < composite.Keys.Count; component++)
                {
                    var key = composite.Keys[component];
                    if (converted.ContainsKey(key)) continue;
                    sharedScalars.TryGetValue((composite.Assignment.StartOffset, key), out var valueEdit);
                    builder.Append(layout.Indent).Append(key).Append('=');
                    var value = valueEdit?.RequestedSourceValue ?? composite.Value(component).Text;
                    if (valueEdit is not null)
                        foreach (var same in scalar!.Edits.Where(edit => WccScenarioFieldBatchCandidatePlanner.GetSourceEditIdentity(edit.Route)
                                     == WccScenarioFieldBatchCandidatePlanner.GetSourceEditIdentity(valueEdit.Route)))
                            locations[same] = new(anchor, builder.Length, value.Length);
                    builder.Append(value).Append(layout.Newline);
                }
            foreach (var edit in ownerEdits.Where(edit => edit.Mode == WccScenarioDifficultyValueMode.Shared))
                builder.Append(edit.Snapshot.Layout!.Indent).Append(edit.Snapshot.Key).Append('=').Append(edit.EncodedValues[0]).Append(layout.Newline);
            for (var index = 0; index < ownerEdits[0].Values.Count; index++)
            {
                var define = ownerEdits[0].Values[index].Define;
                var branchGroups = groups.Where(group => group.Define == define).ToArray();
                var body = new StringBuilder();
                var branchLocations = new List<(WccScenarioFieldCandidate Edit, int Offset, int Length)>();
                foreach (var group in branchGroups)
                {
                    var scan = WmlAuthoredStructureScanner.Scan(group.Region.Text);
                    var startLine = scan.Lines[0]; var endLine = scan.Lines[scan.Directives.Last(item => item.Name == "endif").Line];
                    body.Append(WrapperComment(group.Region.Text[startLine.StartOffset..startLine.ContentEndOffset], layout.Newline, layout.Indent));
                    var absoluteStart = group.Region.StartOffset + startLine.EndOffsetExclusive;
                    var absoluteEnd = group.Region.StartOffset + endLine.StartOffset;
                    var position = absoluteStart;
                    foreach (var statementMembers in group.Members.GroupBy(member => (member.Assignment.StartOffset, member.Assignment.Length)))
                    {
                        var firstMember = statementMembers.First();
                        if (firstMember.CompositeAssignment is { } composite)
                        {
                            var ordered = statementMembers.OrderBy(member => composite.IndexOf(member.Key)).ToArray();
                            if (!ordered.Select(member => member.Key).SequenceEqual(composite.Keys, StringComparer.Ordinal))
                                throw new InvalidOperationException("A composite difficulty statement omitted a physical companion.");
                            var line = WholeStatementLine(source, firstMember.Assignment);
                            body.Append(source.Text, position, line.StartOffset - position);
                            body.Append(Comments(new(group.Define, firstMember.Assignment, firstMember.Value, line, firstMember.Literal), layout.Newline, layout.Indent));
                            foreach (var sibling in ordered)
                            {
                                converted.TryGetValue(sibling.Key, out var convertedSibling);
                                if (convertedSibling?.Mode == WccScenarioDifficultyValueMode.Shared) continue;
                                groupScalars.TryGetValue(sibling, out var scalarSibling);
                                var literal = convertedSibling is not null ? convertedSibling.EncodedValues[index]
                                    : scalarSibling?.RequestedSourceValue ?? sibling.Literal;
                                body.Append(layout.Indent).Append(sibling.Key).Append('=');
                                if (scalarSibling is not null) branchLocations.Add((scalarSibling, body.Length, literal.Length));
                                body.Append(literal).Append(layout.Newline);
                            }
                            position = line.StartOffset + line.Length;
                            continue;
                        }
                        var member = statementMembers.Single();
                        if (converted.TryGetValue(member.Key, out var edit) && edit.Mode == WccScenarioDifficultyValueMode.Shared)
                        {
                            var line = WholeStatementLine(source, member.Assignment);
                            body.Append(source.Text, position, line.StartOffset - position);
                            body.Append(Comments(new(group.Define, member.Assignment, member.Value, line, member.Literal), layout.Newline, layout.Indent));
                            position = line.StartOffset + line.Length;
                            continue;
                        }
                        converted.TryGetValue(member.Key, out edit);
                        groupScalars.TryGetValue(member, out var scalarEdit);
                        if (edit is not null || scalarEdit is not null)
                        {
                            body.Append(source.Text, position, member.Value.StartOffset - position);
                            var value = edit is not null ? edit.EncodedValues[index] : scalarEdit!.ReplacementText;
                            if (scalarEdit is not null) branchLocations.Add((scalarEdit, body.Length, value.Length));
                            body.Append(value); position = member.Value.StartOffset + member.Value.Length;
                        }
                    }
                    body.Append(source.Text, position, absoluteEnd - position);
                    body.Append(WrapperComment(group.Region.Text[endLine.StartOffset..endLine.ContentEndOffset], layout.Newline, layout.Indent));
                }
                foreach (var edit in ownerEdits.Where(edit => edit.Mode == WccScenarioDifficultyValueMode.Independent
                             && !branchGroups.Any(group => group.Members.Any(member => member.Key == edit.Snapshot.Key))))
                    body.Append(edit.Snapshot.Layout!.Indent).Append(edit.Snapshot.Key).Append('=').Append(edit.EncodedValues[index]).Append(layout.Newline);
                if (independent)
                {
                    builder.Append("#ifdef ").Append(define).Append(layout.Newline);
                    foreach (var location in branchLocations)
                        foreach (var scalarEdit in scalar!.Edits.Where(edit => WccScenarioFieldBatchCandidatePlanner.GetSourceEditIdentity(edit.Route)
                                     == WccScenarioFieldBatchCandidatePlanner.GetSourceEditIdentity(location.Edit.Route)))
                            locations[scalarEdit] = new(anchor, builder.Length + location.Offset, location.Length);
                    builder.Append(body).Append("#endif").Append(layout.Newline);
                }
                else builder.Append(body);
            }
            if (regions.Length == 0) patches.Add(new(anchor, 0, builder.ToString()));
            else foreach (var region in regions)
                patches.Add(new(region.StartOffset, region.Length, region.StartOffset == anchor ? builder.ToString() : ""));
        }
    }

    private sealed record GroupCompositionBatch(WccScenarioDifficultyFieldEdit[] Edits,
        IReadOnlyList<WccScenarioDifficultyFieldGroup> Groups);

    private static WmlStaticSourceRange[] GroupCompositionRegions(
        IReadOnlyList<WccScenarioDifficultyFieldEdit> edits, IReadOnlyList<WccScenarioDifficultyFieldGroup> groups) =>
        groups.Select(group => group.Region).Concat(edits.SelectMany(edit => edit.Snapshot.Layout!.Assignments)
                .Where(assignment => assignment.Define is null).Select(assignment => assignment.Region))
            .Concat(edits.Where(edit => edit.Snapshot.Layout!.MacroContract is not null)
                .Select(edit => edit.Snapshot.Layout!.MacroContract!.StatementRegion))
            .DistinctBy(region => (region.StartOffset, region.Length)).OrderBy(region => region.StartOffset).ToArray();

    private static IReadOnlyList<GroupCompositionBatch> PartitionOwnerGroups(
        WccScenarioDifficultyFieldEdit[] edits, WmlEditorAnalysis analysis, IReadOnlySet<int> childConditions)
    {
        var source = edits[0].Source;
        var batches = new List<GroupCompositionBatch>();
        // Every complete sibling set is indivisible: its fixed-context proof
        // applies to all its branches. An unrelated set is optional for reuse,
        // so it must not force a conversion to cross another source directive.
        foreach (var group in edits[0].Snapshot.Layout!.Groups)
        {
            var keys = group.Members.Select(member => member.Key).Order(StringComparer.Ordinal).ToArray();
            var index = batches.FindIndex(batch => batch.Groups[0].Members.Select(member => member.Key)
                .Order(StringComparer.Ordinal).SequenceEqual(keys, StringComparer.Ordinal));
            if (index < 0) batches.Add(new([], Array.AsReadOnly(new[] { group })));
            else batches[index] = batches[index] with
            { Groups = Array.AsReadOnly(batches[index].Groups.Append(group).ToArray()) };
        }
        foreach (var edit in edits)
        {
            var index = batches.FindIndex(batch => batch.Groups.Any(group => group.Members.Any(member => member.Key == edit.Snapshot.Key)));
            if (index < 0) batches.Add(new([edit], []));
            else batches[index] = batches[index] with { Edits = batches[index].Edits.Append(edit).ToArray() };
        }
        // Reuse and merge only when the same movement checks used by the actual
        // writer prove the union safe. Otherwise write each changed batch at its
        // own original position and leave the other groups byte-for-byte intact.
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var first = 0; first < batches.Count && !merged; first++)
                for (var second = first + 1; second < batches.Count; second++)
                {
                    if (batches[first].Edits.Length == 0 && batches[second].Edits.Length == 0) continue;
                    var union = new GroupCompositionBatch(batches[first].Edits.Concat(batches[second].Edits)
                            .OrderBy(edit => Array.IndexOf(edits, edit)).ToArray(),
                        Array.AsReadOnly(batches[first].Groups.Concat(batches[second].Groups)
                            .OrderBy(group => group.Region.StartOffset).ToArray()));
                    if (GroupRelocationProblem(union.Edits, union.Groups, source, analysis, childConditions) is not null) continue;
                    batches[first] = union; batches.RemoveAt(second); merged = true; break;
                }
        }
        return Array.AsReadOnly(batches.Where(batch => batch.Edits.Length != 0)
            .OrderBy(batch => GroupCompositionRegions(batch.Edits, batch.Groups).FirstOrDefault()?.StartOffset
                ?? batch.Edits[0].Snapshot.Layout!.InsertionOffset).ToArray());
    }

    private static string? GroupRelocationProblem(IReadOnlyList<WccScenarioDifficultyFieldEdit> edits,
        IReadOnlyList<WccScenarioDifficultyFieldGroup> groups, WmlStaticSource source,
        WmlEditorAnalysis analysis, IReadOnlySet<int> childConditions)
    {
        var regions = GroupCompositionRegions(edits, groups);
        var anchor = regions.Length == 0 ? edits[0].Snapshot.Layout!.InsertionOffset : regions[0].StartOffset;
        var end = regions.Length == 0 ? anchor : regions.Max(region => region.StartOffset + region.Length);
        if (analysis.Tokens.Any(token => token.Kind == WmlEditorTokenKind.Directive
            && token.StartOffset >= anchor && token.StartOffset < end
            && !regions.Any(region => token.StartOffset >= region.StartOffset && token.StartOffset < region.StartOffset + region.Length)
            && !childConditions.Contains(token.StartOffset)))
            return "Difficulty groups are separated by other preprocessor directives; merging their source order requires RAW editing.";
        var translated = groups.SelectMany(group => group.Members).Where(member => IsTranslated(member.Literal))
            .Select(member => member.Assignment.StartOffset)
            .Concat(SharedCompositeAssignments(edits).SelectMany(composite => composite.Keys.Select(key => composite.Value(composite.IndexOf(key))))
                .Where(value => IsTranslated(value.Text)).Select(value => value.StartOffset))
            .Concat(edits.SelectMany(edit => edit.Snapshot.Layout!.Assignments).Where(assignment => IsTranslated(assignment.Literal))
                .Select(assignment => assignment.Assignment.StartOffset)).ToArray();
        if (translated.Length == 0 && !edits.Any(edit => edit.EncodedValues.Any(IsTranslated))) return null;
        var domain = WmlTextDomainCapture.BeforeOffset(source.Text, anchor);
        return !string.IsNullOrWhiteSpace(domain)
            && translated.All(offset => WmlTextDomainCapture.BeforeOffset(source.Text, offset) == domain) ? null
            : "Grouping would move a translated field into another or unproven text domain; keep its existing source layout.";
    }

    private static IReadOnlySet<int> ChildConditionalDirectiveOffsets(WmlStaticSource source,
        WmlStaticScalarOwner owner, WmlEditorAnalysis analysis)
    {
        var result = new HashSet<int>();
        var scan = WmlAuthoredStructureScanner.Scan(source.Text);
        var owners = ProjectLiteral(source, scan).Owners.Where(candidate => SamePhysicalLiteralOwner(owner, candidate)).ToArray();
        if (scan.Problem is not null || owners.Length != 1) return result;
        var children = owners[0].DirectChildren.Where(child => !child.IsAmended
            && child.OpeningOutputOffset is not null && child.ClosingOutputOffset is not null
            && child.OpeningOrigin is { } opening && child.ClosingOrigin is { } closing
            && SameSource(opening.Source, source) && SameSource(closing.Source, source)
            && opening.Text == "[" + child.TagName + "]" && closing.Text == "[/" + child.TagName + "]")
            .Select(child => (Start: child.OpeningOrigin!.StartOffset + child.OpeningOrigin.Length,
                End: child.ClosingOrigin!.StartOffset)).ToArray();
        if (children.Length == 0) return result;
        var conditions = Conditions(source, scan);
        foreach (var directive in scan.Directives.Where(directive => directive.DefinitionDepth == 0
                     && (WmlAuthoredStructureScanner.IsConditionalStart(directive.Name) || directive.Name is "else" or "endif")))
        {
            var line = scan.Lines[directive.Line];
            if (!conditions.Any(condition => condition.Start <= line.StartOffset && condition.End >= line.EndOffsetExclusive
                && children.Any(child => condition.Start >= child.Start && condition.End <= child.End))) continue;
            foreach (var token in analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive
                         && token.StartOffset >= line.StartOffset && token.StartOffset < line.ContentEndOffset))
                result.Add(token.StartOffset);
        }
        // Only balanced conditional control is scoped to a proved untouched
        // child. Definitions, undefinitions, text domains and other directives
        // affect the surrounding preprocessing environment and stay fenced.
        return result;
    }

    private static string WrapperComment(string line, string newline, string indent)
    {
        var hash = line.IndexOf('#'); var trailing = hash < 0 ? -1 : line.IndexOf('#', hash + 1);
        if (trailing < 0) return "";
        var comment = line[trailing..];
        var payload = indent + comment;
        if (WmlEditorAnalysis.Build(payload).Tokens.Any(token => token.Kind == WmlEditorTokenKind.Directive)) payload = indent + "# " + comment;
        return payload + newline;
    }
}
