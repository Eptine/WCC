using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioDifficultyFieldPlanner
{
    private const int MacroCapturedCharacterLimit = 2 * 1024 * 1024;
    private const int MacroClosureCharacterLimit = 128 * 1024;
    private const int MacroInvocationCharacterLimit = 16 * 1024;

    /// <summary>Pure scalar display proof over immutable captured definitions; no file IO or campaign evaluation.</summary>
    internal static bool TryReadMacroValues(WmlStaticSource source, WmlStaticScalarOwner owner, string key,
        WmlCampaignEvaluationPlan plan, [NotNullWhen(true)] out WccScenarioDifficultyMacroContract? contract,
        out string? problem)
    {
        contract = null; problem = null;
        try
        {
            if (plan.Contexts.Count is < 1 or > 16 || !owner.Fields.TryGetValue(key, out var field)
                || !field.IsKnown || field.Contributions.Count != 1 || field.WinningContribution is not { } current
                || current.CompositeAssignmentKeys.Count != 0
                || !WccScenarioFieldValueCodec.TryGetDefinition(owner.TagName, key, out var definition))
                throw new InvalidOperationException("A difficulty helper needs one known scalar contribution and fixed contexts.");
            var routes = plan.Contexts.Select(context => WccScenarioFieldRouteAnalyzer.Analyze(owner, key, source, context)).ToArray();
            var route = routes.FirstOrDefault(candidate => candidate.Kind == WccScenarioFieldRouteKind.ScalarArgument
                && candidate.Invocation is not null && candidate.Target is not null && candidate.ArgumentIndex.HasValue);
            if (route?.Invocation is not { } capturedInvocation || capturedInvocation.Length > MacroInvocationCharacterLimit
                || !SameSource(capturedInvocation.Source, source)
                || !WmlMacroInvocationSyntaxScanner.TryParseSingle(capturedInvocation.Text, out var syntax)
                || syntax!.Arguments.Count != plan.Contexts.Count || syntax.Name.Contains('{')
                || owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing
                || capturedInvocation.StartOffset <= opening.StartOffset || capturedInvocation.StartOffset + capturedInvocation.Length > closing.StartOffset)
                throw new InvalidOperationException("The field has no exact local helper invocation with one required argument per declared difficulty.");
            // Collected Save creates its own exact Source object. Rebind ranges
            // to that immutable request instance, never to an equivalent capture clone.
            var invocation = new WmlStaticSourceRange(source, capturedInvocation.StartOffset, capturedInvocation.Length);
            var occurrence = WmlMacroInvocationSyntaxScanner.Scan(source.Text).Where(candidate =>
                candidate.Span.StartOffset == invocation.StartOffset && candidate.Span.Length == invocation.Length).ToArray();
            if (occurrence.Length != 1 || occurrence[0].NestingDepth != 0 || occurrence[0].IsInsideMacroDefinition)
                throw new InvalidOperationException("Difficulty conversion needs a direct invocation outside shared definitions.");
            var statement = WholeStatementLine(source, invocation);
            var prefix = source.Text[statement.StartOffset..invocation.StartOffset];
            var inline = !prefix.AsSpan().Trim().IsEmpty;
            if (inline && !Regex.IsMatch(prefix, @"^\s*" + Regex.Escape(key) + @"\s*=\s*$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("The helper must be its whole scalar value or a standalone field-producing invocation.");
            var suffix = source.Text[(invocation.StartOffset + invocation.Length)..(statement.StartOffset + statement.Length)].Trim();
            if (suffix.Length != 0 && !suffix.StartsWith('#'))
                throw new InvalidOperationException("Concatenated helper values need RAW editing for conversion.");
            var relevant = MacroContributionFrames(current, invocation);
            var rootFrame = relevant.FirstOrDefault(frame => HasInvocation(frame, invocation));
            if (rootFrame is null || rootFrame.RequiredParameters?.Count != plan.Contexts.Count
                || rootFrame.OptionalParameters is not { Count: 0 }
                || syntax.Arguments.Any(argument => argument.RawText.Contains('=')
                    || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(argument.RawText)
                    || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition, argument.RawText)))
                throw new InvalidOperationException("The helper needs only explicit supported literal required arguments; optional, named, nested or runtime arguments stay RAW.");
            var originals = relevant.Select(frame => frame.Definition.Source).DistinctBy(item => (item.Path, item.ContentHash))
                .OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            if (originals.Length == 0 || originals.Any(item => !item.IsAuthored)
                || originals.Sum(item => (long)item.Text.Length) > MacroCapturedCharacterLimit)
                throw new InvalidOperationException("The captured helper definition closure exceeds the supported bounded scope.");
            var names = relevant.Select(frame => frame.MacroName).ToHashSet(StringComparer.Ordinal);
            var symbols = plan.Contexts.SelectMany(context => context.DefinedSymbols)
                .Concat(plan.Difficulties.Select(row => row.Define)).ToHashSet(StringComparer.Ordinal);
            var closure = originals.Select(item => MakeMacroClosure(item, names, symbols)).ToArray();
            if (closure.Sum(item => (long)item.Source.Text.Length) > MacroClosureCharacterLimit)
                throw new InvalidOperationException("The isolated helper definition closure is too large.");
            var header = "[" + owner.TagName + "]\n";
            var snippet = new WmlStaticSource(source.Path + ".wcc-difficulty-contract", header + statement.Text + "\n[/" + owner.TagName + "]\n");
            var snippetInvocation = new WmlStaticSourceRange(snippet,
                header.Length + invocation.StartOffset - statement.StartOffset, invocation.Length);
            var slots = new List<WccScenarioDifficultyMacroSlot>();
            foreach (var context in plan.Contexts)
            {
                var evaluated = new WmlStaticEvaluator().Evaluate(new(snippet, context, closure.Select(item => item.Source).ToArray(),
                    plan.Difficulties.Select(row => row.Define).Where(define => !context.DefinedSymbols.Contains(define)).ToArray(), true,
                    Limits: new(MaxExpansionDepth: 16, MaxOutputCharacters: 64 * 1024,
                        MaxSourceCharacters: 256 * 1024, MaxSteps: 100_000)));
                if (!evaluated.IsComplete || evaluated.Diagnostics.Count != 0 || evaluated.Includes.Count != 0
                    || evaluated.Invocations.Any(call => call.Outcome is not (WmlStaticInvocationOutcome.Expanded or WmlStaticInvocationOutcome.Parameter)))
                    throw new InvalidOperationException("The isolated helper does not have a complete pure scalar expansion in every declared context.");
                foreach (var call in evaluated.Invocations.Where(call => call.Outcome == WmlStaticInvocationOutcome.Expanded))
                {
                    if (call.Definition is null) throw new InvalidOperationException("An active helper has no exact captured definition.");
                    _ = RemapMacroDefinition(call.Definition, closure);
                }
                var projected = WmlStaticScalarProjection.Create(evaluated);
                if (projected.Diagnostics.Count != 0 || projected.Owners.Count != 1 || projected.Owners[0] is not { } scalar
                    || scalar.TagName != owner.TagName || scalar.Parent is not null || scalar.DirectChildren.Count != 0
                    || scalar.Fields.Count != 1 || !scalar.Fields.TryGetValue(key, out var scalarField)
                    || scalarField.Contributions.Count != 1 || !scalarField.IsKnown || scalarField.WinningContribution is not { } contribution
                    || contribution.CompositeAssignmentKeys.Count != 0 || !contribution.ValueMappingIsExact
                    || scalarField.ValueText is not { } literal || IsTranslated(literal)
                    || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(literal)
                    || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition, literal))
                    throw new InvalidOperationException("The helper must emit exactly this one literal scalar, without children, other fields or translated payload changes.");
                var selected = WccScenarioFieldRouteAnalyzer.Analyze(scalar, key, snippet, context);
                if (selected.Kind != WccScenarioFieldRouteKind.ScalarArgument || selected.ArgumentIndex is not { } index
                    || selected.Invocation is null || !SameRange(selected.Invocation, snippetInvocation)
                    || selected.Target is null || index < 0 || index >= syntax.Arguments.Count)
                    throw new InvalidOperationException("The helper output has no exact required-argument mapping in every declared context.");
                var argument = syntax.Arguments[index];
                var expectedTarget = new WmlStaticSourceRange(snippet, snippetInvocation.StartOffset + argument.Span.StartOffset, argument.Span.Length);
                if (!SameRange(selected.Target, expectedTarget) || selected.Target.Text != literal)
                    throw new InvalidOperationException("The helper argument does not flow unchanged into the complete literal value.");
                var active = MacroContributionFrames(contribution, snippetInvocation).Select(frame =>
                    RemapMacroDefinition(frame.Definition, closure)).DistinctBy(item => (item.Source.Path, item.Source.ContentHash, item.StartOffset, item.Length)).ToArray();
                if (active.Length == 0) throw new InvalidOperationException("The pure scalar helper has no active immutable definition chain.");
                slots.Add(new(context.DifficultyDefine!, context.SemanticKey, index,
                    new(source, invocation.StartOffset + argument.Span.StartOffset, argument.Span.Length), literal,
                    Array.AsReadOnly(active)));
            }
            if (slots.Select(slot => slot.ArgumentIndex).Distinct().Count() != plan.Contexts.Count)
                throw new InvalidOperationException("The helper does not select a different required argument for each declared difficulty.");
            var proposedContract = new WccScenarioDifficultyMacroContract(invocation, statement, slots, originals);
            if (!slots.Any(slot => MatchesMacroContribution(proposedContract, owner, key,
                    plan.Contexts.Single(context => context.SemanticKey == slot.ContextSemanticKey), out _)))
                throw new InvalidOperationException("The isolated helper does not match this captured winning contribution and active definitions.");
            contract = proposedContract;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        { contract = null; problem = exception.Message; return false; }
    }

    internal static bool MatchesMacroContribution(WccScenarioDifficultyMacroContract contract, WmlStaticScalarOwner owner,
        string key, WmlEvaluationContext context, [NotNullWhen(true)] out WmlStaticScalarContribution? contribution)
    {
        contribution = null;
        var slots = contract.Slots.Where(slot => slot.ContextSemanticKey == context.SemanticKey && slot.Define == context.DifficultyDefine).ToArray();
        if (slots.Length != 1 || !owner.Fields.TryGetValue(key, out var field) || !field.IsKnown
            || field.Contributions.Count != 1 || field.WinningContribution is not { } current
            || current.CompositeAssignmentKeys.Count != 0 || !current.ValueMappingIsExact || field.ValueText != slots[0].Literal)
            return false;
        var route = WccScenarioFieldRouteAnalyzer.Analyze(owner, key, contract.Invocation.Source, context);
        if (route.Kind != WccScenarioFieldRouteKind.ScalarArgument || route.ArgumentIndex != slots[0].ArgumentIndex
            || route.Target is null || route.Invocation is null || !SameRange(route.Target, slots[0].Argument)
            || !SameRange(route.Invocation, contract.Invocation)) return false;
        var frames = MacroContributionFrames(current, contract.Invocation);
        var definitions = frames.Select(frame => frame.Definition)
            .DistinctBy(item => (item.Source.Path, item.Source.ContentHash, item.StartOffset, item.Length)).ToArray();
        if (definitions.Length != slots[0].ActiveDefinitions.Count
            || definitions.Where((item, index) => !SameRange(item, slots[0].ActiveDefinitions[index])).Any()) return false;
        contribution = current; return true;
    }

    internal static bool SameMacroContract(WccScenarioDifficultyMacroContract? left, WccScenarioDifficultyMacroContract? right) =>
        left is null || right is null ? left is null && right is null :
        SameRange(left.Invocation, right.Invocation) && SameRange(left.StatementRegion, right.StatementRegion)
        && left.DefinitionSources.Count == right.DefinitionSources.Count
        && !left.DefinitionSources.Where((source, index) => !SameSource(source, right.DefinitionSources[index])).Any()
        && left.Slots.Count == right.Slots.Count && !left.Slots.Where((slot, index) =>
            slot.Define != right.Slots[index].Define || slot.ContextSemanticKey != right.Slots[index].ContextSemanticKey
            || slot.ArgumentIndex != right.Slots[index].ArgumentIndex || slot.Literal != right.Slots[index].Literal
            || !SameRange(slot.Argument, right.Slots[index].Argument)
            || slot.ActiveDefinitions.Count != right.Slots[index].ActiveDefinitions.Count
            || slot.ActiveDefinitions.Where((definition, position) => !SameRange(definition, right.Slots[index].ActiveDefinitions[position])).Any()).Any();

    private static bool HasInvocation(WmlStaticExpansionFrame frame, WmlStaticSourceRange invocation) =>
        frame.InvocationOrigins.Count == 1 && SameRange(frame.InvocationOrigins[0], invocation);

    private static IReadOnlyList<WmlStaticExpansionFrame> MacroContributionFrames(WmlStaticScalarContribution contribution,
        WmlStaticSourceRange invocation)
    {
        var result = new List<WmlStaticExpansionFrame>();
        foreach (var stack in new[] { contribution.ExpansionStack, contribution.ValueExpansionStack })
        {
            var root = stack.Select((frame, index) => (frame, index)).FirstOrDefault(item => HasInvocation(item.frame, invocation));
            if (root.frame is not null) result.AddRange(stack.Skip(root.index));
        }
        return result.DistinctBy(frame => (frame.MacroName, frame.Definition.Source.Path,
            frame.Definition.Source.ContentHash, frame.Definition.StartOffset, frame.Definition.Length)).ToArray();
    }

    private sealed record MacroMappedChunk(int Start, WmlStaticSourceRange Original);
    private sealed record MacroClosureSource(WmlStaticSource Source, IReadOnlyList<MacroMappedChunk> Chunks);

    private static MacroClosureSource MakeMacroClosure(WmlStaticSource original, IReadOnlySet<string> names,
        IReadOnlySet<string> symbols)
    {
        var scan = WmlAuthoredStructureScanner.Scan(original.Text);
        if (scan.Problem is not null) throw new InvalidOperationException("The captured macro source has incomplete lexical structure.");
        var retained = WmlMacroDefinitionScanner.Scan(original.Text).Where(definition => names.Contains(definition.Name))
            .Select(definition => new WmlStaticSourceRange(original, scan.Lines[definition.StartLineIndex].StartOffset,
                scan.Lines[definition.EndLineIndexExclusive - 1].EndOffsetExclusive - scan.Lines[definition.StartLineIndex].StartOffset)).ToArray();
        if (retained.Length == 0) throw new InvalidOperationException("An active helper definition is not physically captured.");
        foreach (var definition in retained) RequirePureMacroDefinition(definition, symbols);
        var conditions = Conditions(original, scan);
        var relevantConditions = conditions.Where(condition => retained.Any(definition => definition.StartOffset > condition.Start
            && definition.StartOffset + definition.Length <= condition.End)).ToArray();
        var ranges = retained.ToList();
        var stack = new Stack<Condition>();
        foreach (var directive in scan.Directives.Where(item => item.DefinitionDepth == 0))
        {
            var line = scan.Lines[directive.Line];
            if (WmlAuthoredStructureScanner.IsConditionalStart(directive.Name))
            {
                var condition = conditions.Single(item => item.Start == line.StartOffset); stack.Push(condition);
                if (relevantConditions.Contains(condition))
                {
                    RequireMacroCondition(original.Text[line.StartOffset..line.ContentEndOffset], symbols);
                    ranges.Add(new(original, line.StartOffset, line.EndOffsetExclusive - line.StartOffset));
                }
            }
            else if (directive.Name is "else" or "endif")
            {
                if (stack.Count == 0) throw new InvalidOperationException("A macro definition guard is unbalanced.");
                if (relevantConditions.Contains(stack.Peek())) ranges.Add(new(original, line.StartOffset, line.EndOffsetExclusive - line.StartOffset));
                if (directive.Name == "endif") stack.Pop();
            }
        }
        var builder = new StringBuilder(); var chunks = new List<MacroMappedChunk>();
        foreach (var range in ranges.DistinctBy(item => (item.StartOffset, item.Length)).OrderBy(item => item.StartOffset))
        {
            chunks.Add(new(builder.Length, range)); builder.Append(range.Text);
            if (builder.Length != 0 && builder[^1] is not ('\r' or '\n')) builder.Append('\n');
        }
        return new(new(original.Path + ".wcc-definition-contract", builder.ToString()), Array.AsReadOnly(chunks.ToArray()));
    }

    private static void RequirePureMacroDefinition(WmlStaticSourceRange definition, IReadOnlySet<string> symbols)
    {
        var lines = WmlEditorAnalysis.Build(definition.Text).Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive)
            .Select(token => definition.Text.Substring(token.StartOffset, token.Length).TrimStart()).ToArray();
        var directives = lines.Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]).ToArray();
        if (directives.Length < 2 || directives[0] != "#define" || directives[^1] != "#enddef"
            || directives.Count(name => name == "#define") != 1 || directives.Count(name => name == "#enddef") != 1
            || directives.Any(name => name is not ("#define" or "#enddef" or "#ifdef" or "#ifndef" or "#else" or "#endif")))
            throw new InvalidOperationException("Difficulty helper definitions must contain only scalar code and conditional control, without defaults, definitions, undefinitions, includes, warnings or textdomain changes.");
        foreach (var line in lines.Where(line => line.StartsWith("#ifdef", StringComparison.Ordinal)
                     || line.StartsWith("#ifndef", StringComparison.Ordinal))) RequireMacroCondition(line, symbols);
    }

    private static void RequireMacroCondition(string line, IReadOnlySet<string> symbols)
    {
        var comment = line.IndexOf('#', line.IndexOf('#') + 1);
        if (comment >= 0) line = line[..comment];
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 2 || words[0] is not ("#ifdef" or "#ifndef") || !symbols.Contains(words[1]))
            throw new InvalidOperationException("A helper condition depends on symbols outside the exact declared campaign difficulty contexts.");
    }

    private static WmlStaticSourceRange RemapMacroDefinition(WmlStaticSourceRange definition, IReadOnlyList<MacroClosureSource> sources)
    {
        var mapped = sources.SingleOrDefault(item => ReferenceEquals(item.Source, definition.Source));
        if (mapped is null) throw new InvalidOperationException("An active helper escaped its captured immutable definition closure.");
        var chunks = mapped.Chunks.Where(chunk => definition.StartOffset >= chunk.Start
            && definition.StartOffset + definition.Length <= chunk.Start + chunk.Original.Length).ToArray();
        if (chunks.Length != 1) throw new InvalidOperationException("The active helper has a fragmented definition mapping.");
        var original = new WmlStaticSourceRange(chunks[0].Original.Source,
            chunks[0].Original.StartOffset + definition.StartOffset - chunks[0].Start, definition.Length);
        if (original.Text != definition.Text) throw new InvalidOperationException("The active helper definition no longer matches its original captured source.");
        return original;
    }
}
