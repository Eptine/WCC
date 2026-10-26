using Wcc.Core.Library;
using Wcc.Core.Wml;
using System.Text.RegularExpressions;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    /// <summary>Report-only editable binding for one captured zero-argument, pure Story definition.
    /// The checked save independently proves ownership, its physical caller inventory and every context.</summary>
    public static WccScenarioStorySnapshot WithUniqueWritableDefinition(WccScenarioStorySnapshot preview,
        IReadOnlyDictionary<string, WmlStaticScalarProjection> projections, string projectRootPath,
        IEnumerable<WmlStaticSource>? sourceInventory = null)
    {
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(projections);
        if (preview.StoryRange is not null || preview.DefinitionBinding is not null) return preview;
        try
        {
            var frames = new List<WmlStaticExpansionFrame>();
            var capturedStories = new Dictionary<string, (WmlStaticScalarOwner Story, WmlStaticEvaluationResult Evaluation)>(StringComparer.Ordinal);
            var sources = new Dictionary<string, WmlStaticSource>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var context in preview.Contexts)
            {
                if (!projections.TryGetValue(context.SemanticKey, out var projection) || projection.SourceEvaluation is not { IsComplete: true } evaluation
                    || projection.Diagnostics.Count != 0) return preview;
                var scenario = projection.Owners.SingleOrDefault(owner => owner.TagName == "scenario" && owner.Parent is null
                    && owner.OpeningOrigin is { } opening && SameStorySource(opening.Source, preview.Source)
                    && opening.StartOffset == preview.ScenarioRange.StartOffset);
                var stories = scenario?.DirectChildren.Where(owner => owner.TagName == "story").ToArray() ?? [];
                if (stories.Length != 1 || stories[0].OpeningExpansionStack.Count != 1 || stories[0].ClosingExpansionStack.Count != 1
                    || stories[0].IsAmended || stories[0].HasUnresolvedContributions) return preview;
                var frame = stories[0].OpeningExpansionStack[0];
                if (frame.RequiredParameters is null || frame.RequiredParameters.Count != 0 || frame.OptionalParameters is null || frame.OptionalParameters.Count != 0
                    || frame.InvocationOrigins.Count != 1 || !InScenario(frame.InvocationOrigins[0], preview)
                    || frame.InvocationOrigins[0].Text.Trim() != "{" + frame.MacroName + "}") return preview;
                if (!SameFrame(frame, stories[0].ClosingExpansionStack[0])) return preview;
                frames.Add(frame);
                capturedStories[context.Define] = (stories[0], evaluation);
                foreach (var source in evaluation.Sources)
                {
                    if (!source.IsAuthored) continue;
                    if (sources.TryGetValue(source.Path, out var previous) && !SameStorySource(source, previous)) return preview;
                    sources[source.Path] = source;
                }
            }
            if (frames.Count == 0 || frames.Skip(1).Any(frame => !SameFrame(frames[0], frame))) return preview;
            foreach (var source in sourceInventory ?? [])
            {
                if (!source.IsAuthored) continue;
                if (sources.TryGetValue(source.Path, out var previous) && !SameStorySource(source, previous)) return preview;
                sources[source.Path] = source;
            }
            var captured = frames[0];
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRootPath));
            if (!HasCompleteStoryDefinitionInvocationInventory(sources.Values, root)) return preview;
            var relative = Path.GetRelativePath(root, captured.Definition.Source.Path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || SameStorySource(captured.Definition.Source, preview.Source)) return preview;
            var inventory = sources.Values.SelectMany(source => WmlMacroInvocationSyntaxScanner.Scan(StoryDefinitionInventoryText(source.Text))
                .Where(call => call.Name == captured.MacroName).Select(call => new WmlStaticSourceRange(source, call.Span.StartOffset, call.Span.Length))).ToArray();
            if (inventory.Length != 1 || !SameRange(inventory[0], captured.InvocationOrigins[0])) return preview;
            var declarations = sources.Values.Select(source => (Source: source, Structure: WmlAuthoredStructureScanner.Scan(source.Text)))
                .SelectMany(item => item.Structure.Directives
                .Where(directive => directive.Name == "define" && directive.DefinitionDepth == 0)
                .Select(directive => (item.Source, Directive: directive, item.Structure)))
                .Where(item => item.Source.Text[item.Structure.Lines[item.Directive.Line].StartOffset..item.Structure.Lines[item.Directive.Line].ContentEndOffset]
                    .Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } words && words[1] == captured.MacroName).ToArray();
            if (declarations.Length != 1 || declarations[0].Structure.ConditionalLines[declarations[0].Directive.Line]) return preview;
            var structure = WmlAuthoredStructureScanner.Scan(captured.Definition.Source.Text);
            var opening = structure.Directives.SingleOrDefault(directive => directive.Name == "define" && directive.DefinitionDepth == 0
                && structure.Lines[directive.Line].StartOffset <= captured.Definition.StartOffset
                && captured.Definition.StartOffset < structure.Lines[directive.Line].ContentEndOffset);
            if (opening.Name is null) return preview;
            var header = structure.Lines[opening.Line];
            if (captured.Definition.Source.Text[header.StartOffset..header.ContentEndOffset].Trim() != "#define " + captured.MacroName) return preview;
            var closing = structure.Directives.FirstOrDefault(directive => directive.Name == "enddef" && directive.DefinitionDepth == 1 && directive.Line > opening.Line);
            if (closing.Name is null) return preview;
            var footer = structure.Lines[closing.Line];
            if (captured.Definition.StartOffset < header.StartOffset || captured.Definition.StartOffset > header.ContentEndOffset
                || captured.Definition.StartOffset + captured.Definition.Length < footer.ContentEndOffset
                || captured.Definition.StartOffset + captured.Definition.Length > footer.EndOffsetExclusive) return preview;
            var bodyStart = header.EndOffsetExclusive;
            var body = captured.Definition.Source.Text[bodyStart..footer.StartOffset];
            var bodyStructure = WmlAuthoredStructureScanner.Scan(body);
            if (bodyStructure.Problem is not null || bodyStructure.Directives.Any(directive => directive.Name is not ("ifdef" or "ifndef" or "else" or "endif" or "textdomain"))
                || WmlEditorAnalysis.Build(body).Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation)
                    .Any(token => !Regex.IsMatch(body.Substring(token.StartOffset, token.Length),
                        @"^\{(?:NEW_JOURNEY|OLD_JOURNEY|NEW_BATTLE|OLD_BATTLE|NEW_REST|OLD_REST)\s+[+-]?\d+\s+[+-]?\d+\s*\}$", RegexOptions.CultureInvariant))) return preview;
            var spans = WmlBlockScanner.FindTopLevelBlockSpans(bodyStructure.MaskedText).ToArray();
            if (spans.Length != 1 || spans[0].TagName != "story") return preview;
            var span = spans[0];
            var outside = body.Remove(span.StartIndex, span.EndIndexExclusive - span.StartIndex);
            if (WmlEditorAnalysis.Build(outside).Tokens.Any(token => token.Kind != WmlEditorTokenKind.Comment
                && !(token.Kind == WmlEditorTokenKind.Directive && Regex.IsMatch(outside.Substring(token.StartOffset, token.Length),
                    @"^#textdomain\s+[A-Za-z0-9_.-]+\s*$", RegexOptions.CultureInvariant)))) return preview;
            var storyRange = new WmlStaticSourceRange(captured.Definition.Source, bodyStart + span.StartIndex, span.EndIndexExclusive - span.StartIndex);
            if (storyRange.Length > 2 * 1024 * 1024) return preview;
            var literal = ReadDefinitionStory(preview, captured.Definition, storyRange);
            if (!literal.CanEdit) return preview;
            if (capturedStories.Any(pair => !literal.Stories.TryGetValue(pair.Key, out var expected)
                || !DefinitionDomainsMatch(expected, pair.Value.Story, pair.Value.Evaluation)))
                return new(preview.Source, preview.ScenarioRange, preview.StoryRange, preview.Plan, preview.DestinationTextDomain,
                    preview.Contexts, preview.Parts, preview.Stories, false,
                    "This Story macro is read-only because its captured textdomain differs from the helper's literal scope. Put the intended #textdomain inside the macro body before editing it here.")
                { CapturedPreviewParts = Array.AsReadOnly(preview.DisplayParts.ToArray()), SupportsTopologyEditing = false,
                    ScenarioTitles = preview.ScenarioTitles, PreviewDiagnostic = preview.PreviewDiagnostic };
            var representedTravel = literal.Stories.Values.SelectMany(story => story.Children).SelectMany(part => part.Children)
                .Where(child => child.TravelInvocation is not null).Select(child => child.TravelInvocation!).ToArray();
            if (WmlEditorAnalysis.Build(body).Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation)
                .Any(token => !representedTravel.Any(invocation => invocation.StartOffset == bodyStart + token.StartOffset && invocation.Length == token.Length))) return preview;
            var binding = new WccStoryDefinitionBinding(captured.MacroName, captured.Definition, captured.InvocationOrigins[0], literal, root);
            return new(preview.Source, preview.ScenarioRange, null, preview.Plan, preview.DestinationTextDomain,
                preview.Contexts, literal.Parts, literal.Stories, true, "The captured pure Story macro has one authored caller; Apply stages its definition for scenario Save.")
            { DefinitionBinding = binding, SupportsTopologyEditing = true, ScenarioTitles = preview.ScenarioTitles };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return preview; }
    }

    internal static WccScenarioStorySnapshot ReadDefinitionStory(WccScenarioStorySnapshot outer, WmlStaticSourceRange definition, WmlStaticSourceRange storyRange)
    {
        var source = storyRange.Source; var contexts = outer.Contexts;
        var active = ProjectFixedBranches(source, storyRange, contexts).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var lexical = WmlAuthoredStructureScanner.Scan(source.Text);
        foreach (var define in active.Keys.ToArray())
        {
            var buffer = active[define].ToCharArray();
            foreach (var directive in lexical.Directives.Where(item => item.Name == "textdomain" && item.DefinitionDepth == 1))
            {
                var line = lexical.Lines[directive.Line];
                if (line.StartOffset > definition.StartOffset && line.ContentEndOffset <= storyRange.StartOffset)
                    source.Text.AsSpan(line.StartOffset, line.ContentEndOffset - line.StartOffset).CopyTo(buffer.AsSpan(line.StartOffset));
            }
            active[define] = new(buffer);
        }
        var stories = new Dictionary<string, WccStoryLiteralNode>(StringComparer.Ordinal);
        var parts = new Dictionary<string, Dictionary<string, WccStoryLiteralNode>>(StringComparer.Ordinal);
        var orders = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var context in contexts)
        {
            var projected = ParseLiteralProjection(source.Path, active[context.Define]);
            var owner = projected.Owners.Single(item => item.TagName == "story" && item.Parent is null);
            var node = ReadNode(source, owner, active[context.Define], "story", context.Define); stories.Add(context.Define, node);
            var ordinal = 0;
            foreach (var part in node.Children.Where(child => child.TagName == "part"))
            {
                if (!parts.TryGetValue(part.Id, out var byContext)) parts[part.Id] = byContext = new(StringComparer.Ordinal);
                if (!byContext.TryAdd(context.Define, part)) throw new InvalidOperationException("Ambiguous Story macro part identity.");
                if (!orders.TryGetValue(part.Id, out var byOrder)) orders[part.Id] = byOrder = new(StringComparer.Ordinal);
                byOrder[context.Define] = ordinal++;
            }
        }
        var snapshots = parts.Select(pair =>
        {
            WccScenarioStoryChildSnapshot[] Children(string tag) => pair.Value.SelectMany(item => item.Value.Children.Where(child => child.TagName == tag)
                .Select((child, index) => (Define: item.Key, Node: child, Order: index))).GroupBy(item => item.Node.Id, StringComparer.Ordinal)
                .Select(group => new WccScenarioStoryChildSnapshot(group.Key, tag, group.First().Node.Range.Text, group.Select(item => item.Define),
                    group.ToDictionary(item => item.Define, item => item.Order, StringComparer.Ordinal),
                    Fields(group.ToDictionary(item => item.Define, item => item.Node, StringComparer.Ordinal), tag == "image" ? ImageKeys : LayerKeys, contexts),
                    group.ToDictionary(item => item.Define, item => item.Node, StringComparer.Ordinal), true, null)).Select(AddTravelDisplay).ToArray();
            return new WccScenarioStoryPartSnapshot(pair.Key, pair.Value.First().Value.Range.Text, pair.Value.Keys, orders[pair.Key],
                Fields(pair.Value, PartKeys, contexts), Children("image"), Children("background_layer"), pair.Value, true, null)
                { OriginLabel = source.Path };
        }).ToArray();
        if (snapshots.Length > 256 || snapshots.Sum(part => part.Images.Count + part.BackgroundLayers.Count) > 1024)
            throw new InvalidOperationException("The captured Story macro exceeds the bounded editor size.");
        return new(source, definition, storyRange, outer.Plan, outer.DestinationTextDomain, contexts, snapshots, stories, true,
            "Exact literal Story body in a captured pure definition.") { ScenarioTitles = outer.ScenarioTitles };
    }

    internal static WccScenarioFieldBatchCandidate? PlanDefinitionCandidate(WccScenarioStorySnapshot snapshot, IReadOnlyList<WccScenarioStoryPartSpec> parts)
    {
        if (snapshot.DefinitionBinding is not { } binding) return null;
        var patches = RenderPatches(binding.LiteralStory, parts);
        if (patches.Count == 0) return null;
        var text = ApplyLocal(binding.Definition.Source.Text, patches);
        return new(binding.Definition.Source, new(binding.Definition.Source.Path, text), [], patches);
    }
    /// <summary>Report-only planned helper files. Nonoverlapping Story bodies in one exact source
    /// are composed once; only the checked coordinator can persist them.</summary>
    public static IReadOnlyList<WmlStaticSource> PlanDefinitionSources(IEnumerable<WccScenarioStoryEdit> edits)
        => Array.AsReadOnly(PlanCombinedDefinitionCandidates(edits).Select(candidate => candidate.CandidateSource).ToArray());

    internal static IReadOnlyList<WccScenarioFieldBatchCandidate> PlanCombinedDefinitionCandidates(IEnumerable<WccScenarioStoryEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return Array.AsReadOnly(edits.Where(edit => edit.DefinitionCandidate is not null).Select(edit => edit.DefinitionCandidate!)
            .GroupBy(candidate => candidate.Source.Path, comparer).Select(group =>
            {
                var source = group.First().Source;
                if (group.Any(candidate => !SameStorySource(candidate.Source, source)))
                    throw new InvalidOperationException("Story definitions in one helper file require the same exact captured source revision.");
                var patches = group.SelectMany(candidate => candidate.Patches).GroupBy(patch => (patch.Start, patch.Length))
                    .Select(patches => patches.Select(patch => patch.ReplacementText).Distinct(StringComparer.Ordinal).Count() == 1 ? patches.First()
                        : throw new InvalidOperationException("Two Story drafts replace the same helper body differently.")).OrderBy(patch => patch.Start).ToArray();
                var text = ApplyLocal(source.Text, patches);
                return new WccScenarioFieldBatchCandidate(source, new(source.Path, text), [], patches);
            }).ToArray());
    }
    private static bool InScenario(WmlStaticSourceRange range, WccScenarioStorySnapshot snapshot) => SameStorySource(range.Source, snapshot.Source)
        && range.StartOffset >= snapshot.ScenarioRange.StartOffset && range.StartOffset + range.Length <= snapshot.ScenarioRange.StartOffset + snapshot.ScenarioRange.Length;
    internal static bool SameRange(WmlStaticSourceRange left, WmlStaticSourceRange right) => SameStorySource(left.Source, right.Source)
        && left.StartOffset == right.StartOffset && left.Length == right.Length;
    private static bool SameFrame(WmlStaticExpansionFrame left, WmlStaticExpansionFrame right) => left.MacroName == right.MacroName
        && SameRange(left.Definition, right.Definition) && left.InvocationOrigins.Count == right.InvocationOrigins.Count
        && left.InvocationOrigins.Zip(right.InvocationOrigins).All(pair => SameRange(pair.First, pair.Second));

    private static bool DefinitionDomainsMatch(WccStoryLiteralNode expected, WmlStaticScalarOwner actual, WmlStaticEvaluationResult evaluation)
    {
        if (actual.OpeningOutputOffset is not { } opening || actual.ClosingOutputOffset is not { } closing
            || actual.ClosingOrigin is not { } closingOrigin
            || expected.OpeningDomain != WmlTextDomainCapture.BeforeOffset(evaluation.Text, opening)
            || expected.ClosingDomain != WmlTextDomainCapture.BeforeOffset(evaluation.Text, closing + closingOrigin.Length)) return false;
        foreach (var (key, field) in expected.Fields.Where(pair => Translated(pair.Value.Value.Text)))
            if (!actual.Fields.TryGetValue(key, out var value) || value.WinningContribution is not { } contribution
                || field.Domain != WmlTextDomainCapture.BeforeOffset(evaluation.Text, contribution.AssignmentOutputOffset)) return false;
        foreach (var child in expected.Children.Where(child => child.TravelMacroName is null))
        {
            var captured = actual.DirectChildren.SingleOrDefault(node => node.OpeningOrigin is { } origin
                && SameStorySource(origin.Source, child.Range.Source) && origin.StartOffset == child.Range.StartOffset);
            if (captured is null || !DefinitionDomainsMatch(child, captured, evaluation)) return false;
        }
        return true;
    }
}
