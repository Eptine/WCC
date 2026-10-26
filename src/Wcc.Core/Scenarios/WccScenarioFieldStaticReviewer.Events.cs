using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateEventDirectiveClosure(WccScenarioInitialSettingsEdit settings,
        WccStaticSourceCaptureResult capture)
    {
        if (settings.Events.Count == 0) return;
        // A helper that only changes preprocessor state has no output frame.
        // Check every captured declaration of a referenced name, including
        // inactive branches/shadows, rather than assuming that silence is pure.
        var declarations = capture.Sources.SelectMany(EventPhysicalDeclarations)
            .GroupBy(declaration => declaration.Definition.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var steps = 0;
        foreach (var edit in settings.Events)
        {
            if (edit.Target is { } target) Inspect(target.Text, 0);
            if (edit.EventText is { } text) Inspect(text, 0);
        }

        void Inspect(string text, int depth, bool isCapturedMacroBody = false,
            IReadOnlyDictionary<string, string>? parameters = null)
        {
            if (++steps > 4096 || depth > 32 || text.Length > 4 * 1024 * 1024)
                throw new InvalidOperationException("The event helper graph is not bounded; use full RAW source.");
            if (WccScenarioEventSourcePlanner.HasUnsafeControls(text, isCapturedMacroBody) ||
                WccScenarioEventSourcePlanner.HasUnsafeControls(SubstituteEventParameters(text, parameters), isCapturedMacroBody))
                throw new InvalidOperationException("An event helper changes global preprocessor state; use full RAW source.");
            // The evaluator parses the callee's physical argument boundaries
            // before expanding caller-bound parameters. A multiline parameter
            // forwarded as {PLACEMENT} is still one argument, not several words.
            foreach (var call in EventExecutableInvocations(text).Where(call => call.NestingDepth == 0))
            {
                if (parameters is not null && parameters.TryGetValue(call.Name, out var parameter))
                {
                    if (call.Arguments.Count != 0)
                        throw new InvalidOperationException($"Event helper parameter '{call.Name}' is invoked with arguments; use full RAW source.");
                    Inspect(parameter, depth + 1);
                    continue;
                }
                if (call.Name.Contains('{') || call.Name.Contains('}'))
                    throw new InvalidOperationException("A computed event helper name needs full RAW source review.");
                if (!declarations.TryGetValue(call.Name, out var choices))
                {
                    // A bare name can also load included WML or an untracked
                    // inline declaration. Absence from the physical inventory
                    // never proves that a silent invocation has no state effect.
                    throw new InvalidOperationException("An event helper has no exact physical captured declaration; included or unknown WML needs full RAW source review.");
                }
                foreach (var declaration in choices)
                {
                    var definition = declaration.Definition;
                    if (!WmlMacroDefinitionTemplate.TryParse(definition, out var template, out var templateProblem))
                        throw new InvalidOperationException($"Event helper '{call.Name}' has an unsupported captured definition template: {templateProblem}; use full RAW source.");
                    if (!template!.TryResolveArguments(call.Arguments.Select(argument => argument.RawText).ToArray(), out var arguments, out var argumentProblem))
                        throw new InvalidOperationException($"Event helper '{call.Name}' has unsupported captured arguments: {argumentProblem}; use full RAW source.");
                    var bound = BindEventParameters(template, arguments, call.Arguments, parameters);
                    // Include the bound arguments in the visited key: the same
                    // physical forwarding call may receive different payloads.
                    var identity = definition.SourcePath + "\n" + definition.SourceStartLine + "\n" + call.RawText +
                        string.Concat(bound.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => pair.Key.Length + ":" + pair.Key + pair.Value.Length + ":" + pair.Value));
                    if (!visited.Add(identity)) continue;
                    // Supplied/default arguments execute even if the body does
                    // not use them. Inspect their complete bound helper closure.
                    foreach (var argument in bound.Values) Inspect(argument, depth + 1);
                    // The legacy template parser may stop at '#enddef' quoted
                    // inside Lua/text. Inspect the complete lexical physical body
                    // with its caller environment, including output-free calls.
                    Inspect(declaration.Body, depth + 1, true, bound);
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, string> BindEventParameters(WmlMacroDefinitionTemplate template,
        IReadOnlyList<string> arguments, IReadOnlyList<WmlMacroArgumentSyntax> supplied,
        IReadOnlyDictionary<string, string>? caller)
    {
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < template.Parameters.Count; index++)
            bound.Add(template.Parameters[index], NormalizeEventArgument(SubstituteEventParameters(arguments[index], caller)));
        var suppliedOptional = supplied.Skip(template.Parameters.Count)
            .Select(argument => argument.RawText.Trim()).Select(argument =>
                argument.StartsWith('(') && argument.EndsWith(')') ? argument[1..^1].Trim() : argument)
            .Select(argument => argument[..argument.IndexOf('=')].Trim()).ToHashSet(StringComparer.Ordinal);
        foreach (var (optional, index) in template.OptionalParameters.Select((optional, index) => (optional, index)))
            if (suppliedOptional.Contains(optional.Name))
                bound.Add(optional.Name, NormalizeEventArgument(SubstituteEventParameters(arguments[template.Parameters.Count + index], caller)));
        // Match the evaluator's ordered optional-default environment: required
        // and supplied values are installed before omitted defaults are bound.
        foreach (var optional in template.OptionalParameters.OrderBy(optional => optional.Name, StringComparer.Ordinal))
            if (!bound.ContainsKey(optional.Name))
                bound.Add(optional.Name, NormalizeEventArgument(SubstituteEventParameters(optional.DefaultValue, bound)));
        return bound;
    }

    private static string NormalizeEventArgument(string argument)
    {
        var value = argument.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') ||
            (value[0] == '\'' && value[^1] == '\'')) && !value[1..^1].Any(char.IsWhiteSpace))
            return value[1..^1];
        return value;
    }

    private static string SubstituteEventParameters(string text, IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return text;
        // Substitute only original executable parameter tokens, simultaneously;
        // inserted payloads must not be rebound to another callee parameter.
        var result = text;
        foreach (var call in EventExecutableInvocations(text).Where(call => call.Arguments.Count == 0 && parameters.ContainsKey(call.Name))
            .OrderByDescending(call => call.Span.StartOffset))
        {
            if ((long)result.Length - call.Span.Length + parameters[call.Name].Length > 4 * 1024 * 1024)
                throw new InvalidOperationException("The event helper graph is not bounded; use full RAW source.");
            result = result.Remove(call.Span.StartOffset, call.Span.Length).Insert(call.Span.StartOffset, parameters[call.Name]);
        }
        return result;
    }

    private static IEnumerable<WmlMacroInvocationSyntax> EventExecutableInvocations(string text)
    {
        // The preprocessor expands braces in quoted WML values, but raw Lua
        // and comments are opaque. Mask only those opaque spans before asking
        // the shared syntax scanner, then recover exact original arguments.
        var masked = text.ToCharArray();
        var raw = false;
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (raw)
            {
                if (text[index] == '>' && index + 1 < text.Length && text[index + 1] == '>')
                { masked[index] = masked[index + 1] = ' '; raw = false; index++; }
                else if (text[index] is not ('\r' or '\n')) masked[index] = ' ';
                continue;
            }
            if (text[index] == '"') { quoted = !quoted; continue; }
            if (!quoted && text[index] == '<' && index + 1 < text.Length && text[index + 1] == '<')
            { masked[index] = masked[index + 1] = ' '; raw = true; index++; continue; }
            if (!quoted && text[index] == '#')
                while (index < text.Length && text[index] is not ('\r' or '\n')) masked[index++] = ' ';
        }
        foreach (var invocation in WmlMacroInvocationSyntaxScanner.Scan(new string(masked)))
            if (WmlMacroInvocationSyntaxScanner.TryParseSingle(text.Substring(invocation.Span.StartOffset, invocation.Span.Length), out var exact))
                yield return exact! with { Span = invocation.Span, NestingDepth = invocation.NestingDepth };
    }

    private static IEnumerable<(WmlMacroExpansionDefinition Definition, string Body)> EventPhysicalDeclarations(WmlStaticSource source)
    {
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        foreach (var opening in structure.Directives.Where(directive => directive.Name == "define"))
        {
            var closing = structure.Directives.Where(directive => directive.Name == "enddef" &&
                directive.Line > opening.Line && directive.DefinitionDepth == opening.DefinitionDepth + 1).Take(1).ToArray();
            if (closing.Length != 1) continue;
            var first = structure.Lines[opening.Line];
            var last = structure.Lines[closing[0].Line];
            var header = source.Text.Substring(first.StartOffset, first.ContentEndOffset - first.StartOffset)
                .Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (header.Length < 2) continue;
            yield return (new(header[1], source.Text.Substring(first.StartOffset, last.EndOffsetExclusive - first.StartOffset),
                    source.Path, SourceStartLine: opening.Line),
                source.Text.Substring(first.EndOffsetExclusive, last.EndOffsetExclusive - first.EndOffsetExclusive));
        }
    }

    private static void ValidateProjectEventRegions(PairedEvaluation evaluated, WccProjectComposedFile file,
        bool proposed, WmlStaticScalarOwner scenario, IDictionary<WccProjectAuthorizedRegion, int> baselinePresence,
        IDictionary<WccProjectAuthorizedRegion, int> contextPresence, WccScenarioFieldSourceChange change,
        WmlEvaluationContext context, WmlCampaignEvaluationPlan? plan)
    {
        var regions = file.Regions.Where(region => region.Kind == "Event").ToArray();
        if (regions.Length == 0) return;
        var masked = WmlAuthoredStructureScanner.Scan(evaluated.Evaluation.Text).MaskedText;
        foreach (var region in regions)
        {
            var recipe = region.EventEdit ?? throw new InvalidOperationException("An event region has no exact source recipe.");
            var range = proposed ? region.After : region.Before;
            var spans = MergeEventSpans(evaluated.GetProjectRegionSpans(range));
            bool Inside(int offset) => spans.Any(span => offset >= span.Start && offset < span.End);
            var events = scenario.DirectChildren.Where(owner => owner.TagName == "event" &&
                owner.OpeningOutputOffset is { } opening && Inside(opening)).ToArray();
            var intervals = new List<(int Start, int End)>();
            foreach (var owner in events)
            {
                if (owner.IsAmended || owner.OpeningOutputOffset is not { } opening ||
                    owner.ClosingOutputOffset is not { } closing || owner.ClosingOrigin is not { } closingOrigin ||
                    closing < opening || !Inside(closing) || !Inside(closing + closingOrigin.Length - 1))
                    throw new InvalidOperationException("An event draft has amended, incomplete or nonlocal event boundaries.");
                if (recipe.Kind is not (WccScenarioEventSourceEditKind.RemoveMacroInvocation or WccScenarioEventSourceEditKind.ReplaceMacroInvocation or WccScenarioEventSourceEditKind.AddMacroInvocation) &&
                    (!IsProjectLiteralOpening(owner, range) || !InsideProposedScalar(closingOrigin, range) ||
                        !IsProjectLiteralOrigin(closingOrigin, owner.ClosingExpansionStack, range)))
                    throw new InvalidOperationException("A literal event draft does not have exact local authored opening and closing tags.");
                var end = closing + closingOrigin.Length;
                for (var offset = opening; offset < end; offset++)
                    if (!Inside(offset) && !char.IsWhiteSpace(evaluated.Evaluation.Text[offset]))
                        throw new InvalidOperationException("An event consumes content outside its exact authorized source footprint.");
                intervals.Add((opening, end));
            }
            bool InEvent(WmlStaticScalarOwner owner)
            {
                for (var current = owner; current is not null && !ReferenceEquals(current, scenario); current = current.Parent)
                    if (events.Contains(current)) return true;
                return false;
            }
            foreach (var owner in evaluated.Projection.Owners)
            {
                if (owner.OpeningOutputOffset is { } opening && Inside(opening) && !InEvent(owner))
                    throw new InvalidOperationException("The event invocation also produces another root owner; remove it through full RAW source.");
                if (owner.Fields.Values.SelectMany(field => field.Contributions)
                    .Any(contribution => Inside(contribution.AssignmentOutputOffset)) && !InEvent(owner))
                    throw new InvalidOperationException("The event invocation also changes a scalar outside its event subtrees.");
            }
            // Unparsed text must not be hidden merely because it has no scalar owner.
            foreach (var span in spans)
                for (var offset = span.Start; offset < span.End; offset++)
                    if (!intervals.Any(interval => offset >= interval.Start && offset < interval.End) && !char.IsWhiteSpace(masked[offset]))
                        throw new InvalidOperationException("The event footprint contains non-event output.");
            var removal = recipe.Kind is WccScenarioEventSourceEditKind.RemoveLiteral or WccScenarioEventSourceEditKind.RemoveMacroInvocation;
            if (proposed && recipe.Kind == WccScenarioEventSourceEditKind.ReplaceMacroInvocation && events.Length != contextPresence[region])
                throw new InvalidOperationException("The replacement event invocation changes its event count in a reviewed context. Use full RAW source for that group conversion.");
            if (proposed && removal && events.Length != 0 || !proposed && recipe.Kind is (WccScenarioEventSourceEditKind.AddLiteral or WccScenarioEventSourceEditKind.AddMacroInvocation) && events.Length != 0 ||
                recipe.Kind is not (WccScenarioEventSourceEditKind.RemoveMacroInvocation or WccScenarioEventSourceEditKind.ReplaceMacroInvocation or WccScenarioEventSourceEditKind.AddMacroInvocation) && events.Length > 1 ||
                proposed && recipe.Kind is (WccScenarioEventSourceEditKind.AddLiteral or WccScenarioEventSourceEditKind.AddMacroInvocation) && events.Length != 1 ||
                proposed && recipe.Kind == WccScenarioEventSourceEditKind.ReplaceLiteral && events.Length != contextPresence[region])
                throw new InvalidOperationException("The event recipe does not produce its exact intended event cardinality.");
            if (proposed && recipe.GraphSeed is { } graphSeed)
            {
                if (plan is null || events.Length != 1)
                    throw new InvalidOperationException("A structured event needs its exact campaign plan and one active event owner.");
                _ = ValidateEventGraphUnits(events[0], graphSeed.Graph, range, evaluated.Evaluation, change, context, plan,
                    (origin, frames) => origin is not null && InsideProposedScalar(origin, range) &&
                                        IsProjectLiteralOrigin(origin, frames, range), evaluated.Projection);
            }
            if (proposed && recipe.Kind == WccScenarioEventSourceEditKind.AddMacroInvocation)
                ValidateAppliedEventPreset(events[0], recipe.Preset!, range, evaluated.Evaluation, change, context, plan, evaluated.Projection);
            if (!proposed)
            {
                baselinePresence.TryGetValue(region, out var previous);
                baselinePresence[region] = previous + events.Length;
                contextPresence[region] = events.Length;
            }
        }
    }

    private static IReadOnlyList<(int Start, int End)> MergeEventSpans(IReadOnlyList<(int Start, int End)> source)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var span in source.OrderBy(span => span.Start))
            if (merged.Count != 0 && span.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
            else merged.Add(span);
        return merged;
    }
}
