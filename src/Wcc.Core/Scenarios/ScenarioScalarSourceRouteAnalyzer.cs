using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Builds conservative, read-only source breadcrumbs for the first scalar
/// resolver proof: scenario turns and literal-side gold/income.
///
/// The exact effective document is the semantic oracle. The rendered authored
/// WML remains the only source of coordinates. No normalized WML is ever
/// treated as editable source.
/// </summary>
public static class ScenarioScalarSourceRouteAnalyzer
{
    private static readonly string[] StandardDifficultyDefines =
        ["EASY", "NORMAL", "HARD", "NIGHTMARE"];

    public static ScenarioScalarSourceRouteAnalysis Analyze(
        ScenarioDocument authoredScenario,
        ScenarioDocument exactEffectiveScenario,
        WmlEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(authoredScenario);
        ArgumentNullException.ThrowIfNull(exactEffectiveScenario);
        ArgumentNullException.ThrowIfNull(context);

        var enclosingProblem = FindEnclosingSourceProblem(authoredScenario);
        if (enclosingProblem is not null)
            return RawAnalysis(exactEffectiveScenario, enclosingProblem, context);

        return AnalyzeStandaloneScenarioBlock(
            authoredScenario.EditableBlock.ToRawText(),
            exactEffectiveScenario,
            context);
    }

    /// <summary>
    /// Analyzes a standalone rendered <c>[scenario]</c> block after the caller
    /// has independently established that no enclosing preprocessor branch or
    /// macro definition exists. Editor integrations should use the
    /// <see cref="Analyze(ScenarioDocument, ScenarioDocument, WmlEvaluationContext)"/>
    /// overload so those full-source ownership checks cannot be skipped.
    /// </summary>
    public static ScenarioScalarSourceRouteAnalysis AnalyzeStandaloneScenarioBlock(
        string renderedAuthoredWml,
        ScenarioDocument exactEffectiveScenario,
        WmlEvaluationContext context)
    {
        if (renderedAuthoredWml is null)
            throw new ArgumentNullException(nameof(renderedAuthoredWml));
        if (exactEffectiveScenario is null)
            throw new ArgumentNullException(nameof(exactEffectiveScenario));
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        if (ContainsBareCarriageReturn(renderedAuthoredWml))
        {
            return RawAnalysis(
                exactEffectiveScenario,
                "Authored WML uses bare carriage-return line endings, so reliable source coordinates were not produced.",
                context);
        }

        var source = new SourceDocument(renderedAuthoredWml);
        var tree = WmlProjectionTreeParser.Parse(renderedAuthoredWml);
        var scenarioNodes = tree.Roots
            .Where(node => string.Equals(node.TagName, "scenario", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (scenarioNodes.Length != 1)
        {
            return RawAnalysis(
                exactEffectiveScenario,
                $"Rendered authored WML must contain exactly one literal root [scenario]; found {scenarioNodes.Length}.",
                context);
        }

        var macroDefinitions = WmlMacroDefinitionScanner.Scan(renderedAuthoredWml);
        var invocations = WmlMacroInvocationSyntaxScanner.Scan(renderedAuthoredWml);
        var lexical = BuildLexicalFacts(source, context);
        if (!lexical.HasReliableConditionalStructure)
        {
            return RawAnalysis(
                exactEffectiveScenario,
                "Authored preprocessor conditionals are malformed or not structurally balanced, so precise source breadcrumbs were refused.",
                context);
        }
        var scenarioOwner = new SourceOwner(
            scenarioNodes[0],
            source,
            lexical,
            macroDefinitions,
            invocations);

        if (scenarioOwner.IsInsideMacroDefinition)
        {
            return RawAnalysis(
                exactEffectiveScenario,
                "The literal [scenario] is inside a macro definition; generated definition content has no direct scalar route.",
                context);
        }

        if (scenarioOwner.IsConditional)
        {
            return RawAnalysis(
                exactEffectiveScenario,
                "The literal [scenario] is preprocessor-conditional; this breadcrumb slice does not invert conditional ownership.",
                context);
        }

        var authoredId = WmlDisplayStringNormalizer.Normalize(
            ReadUniqueLiteralText(scenarioOwner, "id"));
        var effectiveId = WmlDisplayStringNormalizer.Normalize(
            exactEffectiveScenario.Scenario.Id);
        if (authoredId is not null &&
            effectiveId is not null &&
            !string.Equals(authoredId, effectiveId, StringComparison.Ordinal))
        {
            return RawAnalysis(
                exactEffectiveScenario,
                $"Authored scenario id '{authoredId}' does not match exact effective id '{effectiveId}'.",
                context);
        }

        var turns = AnalyzeOwnerField(
            scenarioOwner,
            ScenarioScalarSourceField.Turns,
            sideNumber: null,
            key: "turns",
            wholeLineHelpers: ["TURNS", "TURNS4"],
            exactEffectiveScenario.Scenario.Turns,
            context);

        var authoredSides = scenarioNodes[0].Children
            .Where(node => string.Equals(node.TagName, "side", StringComparison.OrdinalIgnoreCase))
            .Select(node => BuildAuthoredSide(
                new SourceOwner(node, source, lexical, macroDefinitions, invocations)))
            .ToArray();
        var effectiveSides = exactEffectiveScenario.Scenario.Sides;
        var effectiveNumberCounts = effectiveSides
            .Where(side => side.SideNumber is not null)
            .GroupBy(side => side.SideNumber!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var sideRoutes = new List<ScenarioScalarSourceRoute>(effectiveSides.Count * 2);
        foreach (var effectiveSide in effectiveSides)
        {
            var sideNumber = effectiveSide.SideNumber;
            if (sideNumber is null)
            {
                sideRoutes.Add(Raw(
                    ScenarioScalarSourceField.SideGold,
                    null,
                    effectiveSide.Gold,
                    "The exact effective side has no side number, so it cannot be joined to one literal authored [side]."));
                sideRoutes.Add(Raw(
                    ScenarioScalarSourceField.SideIncome,
                    null,
                    effectiveSide.Income,
                    "The exact effective side has no side number, so it cannot be joined to one literal authored [side]."));
                continue;
            }

            if (effectiveNumberCounts.GetValueOrDefault(sideNumber.Value) != 1)
            {
                var reason = $"Exact effective side number {sideNumber.Value} is duplicated; side ownership is ambiguous.";
                sideRoutes.Add(Raw(
                    ScenarioScalarSourceField.SideGold,
                    sideNumber,
                    effectiveSide.Gold,
                    reason));
                sideRoutes.Add(Raw(
                    ScenarioScalarSourceField.SideIncome,
                    sideNumber,
                    effectiveSide.Income,
                    reason));
                continue;
            }

            var matchingAuthoredSides = authoredSides
                .Where(side => side.SideNumber == sideNumber)
                .ToArray();
            if (matchingAuthoredSides.Length != 1)
            {
                var reason = matchingAuthoredSides.Length == 0
                    ? $"Exact side {sideNumber.Value} has no uniquely identified literal authored [side]; it may be generated or dynamically numbered."
                    : $"Authored side number {sideNumber.Value} is duplicated; side ownership is ambiguous.";
                sideRoutes.Add(Raw(
                    ScenarioScalarSourceField.SideGold,
                    sideNumber,
                    effectiveSide.Gold,
                    reason));
                sideRoutes.Add(Raw(
                    ScenarioScalarSourceField.SideIncome,
                    sideNumber,
                    effectiveSide.Income,
                    reason));
                continue;
            }

            var authoredSide = matchingAuthoredSides[0];
            if (authoredSide.IdentityProblem is not null)
            {
                var reason = $"Authored side {sideNumber.Value} is not a stable literal owner: {authoredSide.IdentityProblem}";
                sideRoutes.Add(Raw(
                    ScenarioScalarSourceField.SideGold,
                    sideNumber,
                    effectiveSide.Gold,
                    reason,
                    authoredSide.IdentitySpan,
                    authoredSide.Owner.Source));
                sideRoutes.Add(Raw(
                    ScenarioScalarSourceField.SideIncome,
                    sideNumber,
                    effectiveSide.Income,
                    reason,
                    authoredSide.IdentitySpan,
                    authoredSide.Owner.Source));
                continue;
            }

            sideRoutes.Add(AnalyzeOwnerField(
                authoredSide.Owner,
                ScenarioScalarSourceField.SideGold,
                sideNumber,
                "gold",
                ["GOLD", "GOLD4"],
                effectiveSide.Gold,
                context));
            sideRoutes.Add(AnalyzeOwnerField(
                authoredSide.Owner,
                ScenarioScalarSourceField.SideIncome,
                sideNumber,
                "income",
                ["INCOME", "INCOME4"],
                effectiveSide.Income,
                context));
        }

        return StampEffectiveScenarioId(
            new ScenarioScalarSourceRouteAnalysis
            {
                Turns = turns,
                SideValues = sideRoutes
            },
            exactEffectiveScenario,
            context);
    }

    private static string? FindEnclosingSourceProblem(ScenarioDocument authoredScenario)
    {
        var source = authoredScenario.OriginalText;
        var startOffset = authoredScenario.BlockSpan.StartIndex;
        if (startOffset < 0 || startOffset > source.Length)
            return "The authored scenario span is stale or outside its source document.";

        var startLine = 0;
        for (var offset = 0; offset < startOffset; offset++)
        {
            if (source[offset] == '\n')
                startLine++;
        }

        if (WmlMacroDefinitionScanner.Scan(source).Any(definition =>
                definition.StartLineIndex <= startLine &&
                startLine < definition.EndLineIndexExclusive))
        {
            return "The authored [scenario] is enclosed by a macro definition; this breadcrumb slice does not claim direct field provenance inside generated definition content.";
        }

        if (IsLineInsidePreprocessorConditional(source, startLine))
        {
            return "The authored [scenario] is enclosed by a preprocessor conditional; this breadcrumb slice does not invert outer branch ownership.";
        }

        return null;
    }

    private static bool IsLineInsidePreprocessorConditional(string source, int targetLine)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var conditionalDepth = 0;
        var stringState = new WmlStringScanState();
        for (var lineIndex = 0; lineIndex < lines.Length && lineIndex <= targetLine; lineIndex++)
        {
            var line = lines[lineIndex];
            var startedInsideString = stringState.InsideMultilineString;
            if (!startedInsideString)
            {
                var directive = GetStrictStructuralDirectiveKind(line.Trim());
                if (directive == WmlPreprocessorDirectiveKind.EndIf)
                    conditionalDepth = Math.Max(0, conditionalDepth - 1);

                if (lineIndex == targetLine)
                    return conditionalDepth > 0;

                if (directive == WmlPreprocessorDirectiveKind.IfStart)
                    conditionalDepth++;
            }
            else if (lineIndex == targetLine)
            {
                return conditionalDepth > 0;
            }

            WmlRawStringScanner.UpdateState(line, ref stringState);
        }

        return false;
    }

    private static AuthoredSide BuildAuthoredSide(SourceOwner owner)
    {
        var sideFields = CollectFieldOccurrences(owner, "side");
        var openingLine = owner.Source.Lines[owner.Node.StartLine];
        var tagCode = WmlInlineCommentSplitter.Split(openingLine.Text).Value.Trim();
        if (!IsOrdinaryOpeningTag(tagCode, "side"))
        {
            return new AuthoredSide(
                owner,
                TryReadCommonInteger(sideFields),
                "the source uses a merge/modified/non-literal side tag",
                owner.Source.SpanForLineCode(owner.Node.StartLine));
        }

        if (sideFields.Count != 1)
        {
            return new AuthoredSide(
                owner,
                TryReadCommonInteger(sideFields),
                sideFields.Count == 0
                    ? "there is no direct literal side= integer"
                    : "side= occurs more than once",
                sideFields.FirstOrDefault()?.ValueSpan);
        }

        var identity = sideFields[0];
        if (!TryParsePlainInteger(identity.ValueText, out var sideNumber))
        {
            return new AuthoredSide(
                owner,
                null,
                "side= is dynamic or not a plain integer",
                identity.ValueSpan);
        }

        if (owner.IsConditional || identity.ConditionalState != ConditionalLineState.Unconditional)
        {
            return new AuthoredSide(
                owner,
                sideNumber,
                "the [side] or its side= identity is preprocessor-conditional",
                identity.ValueSpan);
        }

        if (owner.IsInsideMacroDefinition || identity.IsInsideMacroDefinition)
        {
            return new AuthoredSide(
                owner,
                sideNumber,
                "the [side] or its side= identity is inside a macro definition",
                identity.ValueSpan);
        }

        return new AuthoredSide(owner, sideNumber, null, identity.ValueSpan);
    }

    private static int? TryReadCommonInteger(IReadOnlyList<FieldOccurrence> occurrences)
    {
        int? common = null;
        foreach (var occurrence in occurrences)
        {
            if (!TryParsePlainInteger(occurrence.ValueText, out var value))
                return null;
            if (common is not null && common != value)
                return null;
            common = value;
        }
        return common;
    }

    private static ScenarioScalarSourceRoute AnalyzeOwnerField(
        SourceOwner owner,
        ScenarioScalarSourceField field,
        int? sideNumber,
        string key,
        IReadOnlyCollection<string> wholeLineHelpers,
        int? effectiveValue,
        WmlEvaluationContext context)
    {
        var fieldOccurrences = CollectFieldOccurrences(owner, key);
        var allCandidates = fieldOccurrences
            .Select(occurrence => BuildFieldCandidate(occurrence, context))
            .ToList();

        foreach (var invocation in owner.Invocations.Where(invocation =>
                     wholeLineHelpers.Contains(invocation.Name) &&
                     owner.TopLevelLines.Contains(invocation.Span.StartLine) &&
                     !fieldOccurrences.Any(occurrence => Contains(occurrence.ValueSpan, invocation.Span))))
        {
            allCandidates.Add(BuildWholeLineHelperCandidate(owner, invocation, context));
        }

        var candidates = allCandidates
            .Where(candidate => candidate.ConditionalState != ConditionalLineState.Excluded)
            .ToList();

        if (candidates.Count == 0)
        {
            return Raw(
                field,
                sideNumber,
                effectiveValue,
                $"No direct authored {key}= token or recognized whole-line {string.Join('/', wholeLineHelpers)} helper owns this exact value; it may be defaulted or generated.");
        }

        if (candidates.Count != 1)
        {
            return Raw(
                field,
                sideNumber,
                effectiveValue,
                $"Authored {key} has {candidates.Count} competing scalar source candidates, so no precise source token was selected.");
        }

        var candidate = candidates[0];
        if (candidate.Problem is not null)
        {
            return Raw(
                field,
                sideNumber,
                effectiveValue,
                candidate.Problem,
                candidate.SourceSpan,
                owner.Source,
                candidate.InvocationSpan,
                candidate.HelperName,
                candidate.HelperArgumentIndex);
        }

        if (effectiveValue is null)
        {
            return Raw(
                field,
                sideNumber,
                null,
                $"The exact effective document has no {key} integer, so the authored candidate cannot be value-verified.",
                candidate.SourceSpan,
                owner.Source,
                candidate.InvocationSpan,
                candidate.HelperName,
                candidate.HelperArgumentIndex);
        }

        if (candidate.Value != effectiveValue)
        {
            return Raw(
                field,
                sideNumber,
                effectiveValue,
                $"The authored candidate resolves syntactically to {candidate.Value?.ToString(CultureInfo.InvariantCulture) ?? "no integer"}, but the exact effective {key} is {effectiveValue.Value}; source ownership was refused.",
                candidate.SourceSpan,
                owner.Source,
                candidate.InvocationSpan,
                candidate.HelperName,
                candidate.HelperArgumentIndex);
        }

        if (candidate.IsContextSelectedConditional &&
            allCandidates.Any(other =>
                !HasSameSourceSpan(other, candidate) &&
                (other.ConditionalState == ConditionalLineState.Unknown ||
                 (other.Problem is null && other.Value == effectiveValue))))
        {
            return Raw(
                field,
                sideNumber,
                effectiveValue,
                $"The exact {key} value also matches or may be supplied by another conditional branch, so no precise source token was selected.");
        }

        var line = owner.Source.Lines[candidate.SourceSpan.StartLine];
        var explanation = candidate.Kind switch
        {
            ScenarioScalarSourceRouteKind.DirectScalarToken =>
                $"The unique direct authored {key} integer matches the exact effective value. This is only a syntactic navigation candidate: other macro or runtime contributions are not excluded, and write-back is not authorized.",
            ScenarioScalarSourceRouteKind.ContextSelectedConditionalToken =>
                $"The selected preprocessor context activates this authored {key} integer and it matches the exact effective value. Editing this token may affect multiple contexts, so this is a read-only breadcrumb rather than write-back authorization.",
            _ =>
                $"Argument {(candidate.HelperArgumentIndex ?? 0) + 1} of {candidate.HelperName} matches the exact {context.DifficultyDefine} value.{(candidate.IsContextSelectedConditional ? " The invocation is also inside the context-selected conditional branch." : string.Empty)} The Core helper shape is only a syntactic candidate; macro-definition provenance is not yet verified and write-back is not authorized."
        };
        return new ScenarioScalarSourceRoute(
            field,
            sideNumber,
            effectiveValue,
            candidate.Kind,
            candidate.SourceSpan,
            candidate.SourceSpan.StartLine,
            line.Text,
            explanation,
            candidate.HelperName,
            candidate.HelperArgumentIndex,
            candidate.InvocationSpan)
        {
            IsContextSelectedConditional = candidate.IsContextSelectedConditional
        };
    }

    private static SourceCandidate BuildFieldCandidate(
        FieldOccurrence occurrence,
        WmlEvaluationContext context)
    {
        if (occurrence.IsInsideMacroDefinition)
        {
            return SourceCandidate.Invalid(
                occurrence.ValueSpan,
                "The authored scalar is inside a macro definition; definition provenance is not established.")
                with { ConditionalState = occurrence.ConditionalState };
        }

        if (occurrence.ConditionalState == ConditionalLineState.Unknown)
        {
            return SourceCandidate.Invalid(
                occurrence.ValueSpan,
                "The authored scalar is inside a preprocessor condition that WCC cannot select safely; conditional ownership remains raw-only.")
                with { ConditionalState = occurrence.ConditionalState };
        }

        if (TryParsePlainInteger(occurrence.ValueText, out var directValue))
        {
            return new SourceCandidate(
                occurrence.ConditionalState == ConditionalLineState.Selected
                    ? ScenarioScalarSourceRouteKind.ContextSelectedConditionalToken
                    : ScenarioScalarSourceRouteKind.DirectScalarToken,
                occurrence.ValueSpan,
                directValue)
            {
                ConditionalState = occurrence.ConditionalState,
                IsContextSelectedConditional = occurrence.ConditionalState == ConditionalLineState.Selected
            };
        }

        var exactInvocations = occurrence.Owner.Invocations
            .Where(invocation =>
                invocation.Span.StartOffset == occurrence.ValueSpan.StartOffset &&
                invocation.Span.EndOffsetExclusive == occurrence.ValueSpan.EndOffsetExclusive)
            .ToArray();
        if (exactInvocations.Length != 1 ||
            exactInvocations[0].Name is not ("ON_DIFFICULTY" or "ON_DIFFICULTY4"))
        {
            return SourceCandidate.Invalid(
                occurrence.ValueSpan,
                "The authored right-hand side is dynamic, nested, concatenated, or not an exact ON_DIFFICULTY/ON_DIFFICULTY4 invocation.")
                with { ConditionalState = occurrence.ConditionalState };
        }

        var helper = BuildDifficultyHelperCandidate(exactInvocations[0], context);
        return helper with
        {
            ConditionalState = occurrence.ConditionalState,
            IsContextSelectedConditional = occurrence.ConditionalState == ConditionalLineState.Selected
        };
    }

    private static SourceCandidate BuildWholeLineHelperCandidate(
        SourceOwner owner,
        WmlMacroInvocationSyntax invocation,
        WmlEvaluationContext context)
    {
        var conditionalState = owner.Lexical.ConditionalStates[invocation.Span.StartLine];
        if (invocation.IsInsideMacroDefinition || owner.IsMacroDefinitionLine(invocation.Span.StartLine))
        {
            return SourceCandidate.Invalid(
                invocation.Span,
                "The helper invocation is inside a macro definition; definition provenance is not established.",
                invocation.Span,
                invocation.Name)
                with { ConditionalState = conditionalState };
        }

        if (invocation.NestingDepth != 0)
        {
            return SourceCandidate.Invalid(
                invocation.Span,
                $"{invocation.Name} is nested inside another invocation; nested ownership remains raw-only.",
                invocation.Span,
                invocation.Name)
                with { ConditionalState = conditionalState };
        }

        if (invocation.Span.StartLine != invocation.Span.EndLine ||
            !owner.Source.IsOnlyCodeOnLine(invocation.Span))
        {
            return SourceCandidate.Invalid(
                invocation.Span,
                $"{invocation.Name} is recognized only as a correct-owner whole-line helper.",
                invocation.Span,
                invocation.Name)
                with { ConditionalState = conditionalState };
        }

        if (conditionalState == ConditionalLineState.Unknown)
        {
            return SourceCandidate.Invalid(
                invocation.Span,
                $"The whole-line {invocation.Name} helper is inside a preprocessor condition that WCC cannot select safely; ownership remains raw-only.",
                invocation.Span,
                invocation.Name)
                with { ConditionalState = conditionalState };
        }

        var helper = BuildDifficultyHelperCandidate(invocation, context);
        return helper with
        {
            ConditionalState = conditionalState,
            IsContextSelectedConditional = conditionalState == ConditionalLineState.Selected
        };
    }

    private static SourceCandidate BuildDifficultyHelperCandidate(
        WmlMacroInvocationSyntax invocation,
        WmlEvaluationContext context)
    {
        var expectedArity = invocation.Name.EndsWith('4') ? 4 : 3;
        if (invocation.Arguments.Count != expectedArity)
        {
            return SourceCandidate.Invalid(
                invocation.Span,
                $"{invocation.Name} has {invocation.Arguments.Count} arguments; exactly {expectedArity} are required.",
                invocation.Span,
                invocation.Name);
        }

        if (!TrySelectDifficultyArgument(context, expectedArity, out var argumentIndex, out var contextProblem))
        {
            return SourceCandidate.Invalid(
                invocation.Span,
                contextProblem,
                invocation.Span,
                invocation.Name);
        }

        var values = new int[invocation.Arguments.Count];
        for (var index = 0; index < invocation.Arguments.Count; index++)
        {
            if (TryParsePlainInteger(invocation.Arguments[index].RawText, out values[index]))
                continue;
            return SourceCandidate.Invalid(
                invocation.Arguments[index].Span,
                $"Argument {index + 1} of {invocation.Name} is not one plain integer; nested or dynamic helper arguments remain raw-only.",
                invocation.Span,
                invocation.Name,
                index);
        }

        var selected = invocation.Arguments[argumentIndex];
        return new SourceCandidate(
            ScenarioScalarSourceRouteKind.KnownDifficultyArgument,
            selected.Span,
            values[argumentIndex],
            invocation.Span,
            invocation.Name,
            argumentIndex);
    }

    private static bool TrySelectDifficultyArgument(
        WmlEvaluationContext context,
        int arity,
        out int argumentIndex,
        out string problem)
    {
        argumentIndex = -1;
        var difficulty = context.DifficultyDefine;
        var supported = StandardDifficultyDefines.Take(arity).ToArray();
        if (difficulty is null || !supported.Contains(difficulty, StringComparer.Ordinal))
        {
            problem = $"Context difficulty '{difficulty ?? "(none)"}' does not identify one of the {string.Join('/', supported)} helper slots.";
            return false;
        }

        var activeStandardDefines = StandardDifficultyDefines
            .Where(context.DefinedSymbols.Contains)
            .ToArray();
        if (activeStandardDefines.Length != 1 || activeStandardDefines[0] != difficulty)
        {
            problem = "The evaluation context contains zero or multiple standard difficulty symbols, so one helper argument cannot be selected safely.";
            return false;
        }

        argumentIndex = Array.IndexOf(supported, difficulty);
        problem = string.Empty;
        return true;
    }

    private static List<FieldOccurrence> CollectFieldOccurrences(SourceOwner owner, string key)
    {
        var result = new List<FieldOccurrence>();
        foreach (var lineIndex in owner.TopLevelLines.Order())
        {
            if (owner.Lexical.StartsInsideMultilineString[lineIndex])
                continue;
            var line = owner.Source.Lines[lineIndex];
            var codeEndColumn = FindCodeEndColumn(line.Text);
            var code = line.Text[..codeEndColumn];
            var equals = code.IndexOf('=');
            if (equals <= 0 ||
                !string.Equals(code[..equals].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var valueStartColumn = equals + 1;
            while (valueStartColumn < code.Length && char.IsWhiteSpace(code[valueStartColumn]))
                valueStartColumn++;
            var valueEndColumn = code.Length;
            while (valueEndColumn > valueStartColumn && char.IsWhiteSpace(code[valueEndColumn - 1]))
                valueEndColumn--;

            var valueSpan = owner.Source.CreateSpan(
                line.StartOffset + valueStartColumn,
                line.StartOffset + valueEndColumn,
                lineIndex);
            result.Add(new FieldOccurrence(
                owner,
                code[valueStartColumn..valueEndColumn],
                valueSpan,
                owner.Lexical.ConditionalStates[lineIndex],
                owner.IsMacroDefinitionLine(lineIndex)));
        }
        return result;
    }

    private static string? ReadUniqueLiteralText(SourceOwner owner, string key)
    {
        var values = CollectFieldOccurrences(owner, key);
        if (values.Count != 1 ||
            values[0].ConditionalState != ConditionalLineState.Unconditional ||
            values[0].IsInsideMacroDefinition ||
            values[0].ValueText.ContainsAny(['{', '}', '$']))
        {
            return null;
        }
        return values[0].ValueText.Trim();
    }

    private static LexicalFacts BuildLexicalFacts(
        SourceDocument source,
        WmlEvaluationContext context)
    {
        var startsInsideString = new bool[source.Lines.Count];
        var conditionalStates = new ConditionalLineState[source.Lines.Count];
        var conditionalStack = new List<ConditionalFrame>();
        var malformedConditionalStructure = false;
        var stringState = new WmlStringScanState();
        for (var index = 0; index < source.Lines.Count; index++)
        {
            var line = source.Lines[index].Text;
            var startedInsideString = stringState.InsideMultilineString;
            startsInsideString[index] = startedInsideString;
            if (!startedInsideString)
            {
                var trimmed = line.Trim();
                var directive = GetStrictStructuralDirectiveKind(trimmed);
                switch (directive)
                {
                    case WmlPreprocessorDirectiveKind.IfStart:
                    {
                        var known = TryEvaluateSimpleConditional(trimmed, context, out var selected);
                        conditionalStack.Add(new ConditionalFrame(known, selected));
                        break;
                    }
                    case WmlPreprocessorDirectiveKind.Else:
                    {
                        if (conditionalStack.Count == 0)
                        {
                            malformedConditionalStructure = true;
                            break;
                        }

                        var frame = conditionalStack[^1];
                        if (frame.HasAlternative ||
                            !string.Equals(trimmed, "#else", StringComparison.OrdinalIgnoreCase))
                        {
                            frame.IsKnown = false;
                            malformedConditionalStructure = true;
                        }
                        else if (frame.IsKnown)
                        {
                            frame.IsSelected = !frame.IsSelected;
                        }
                        frame.HasAlternative = true;
                        break;
                    }
                    case WmlPreprocessorDirectiveKind.EndIf:
                        if (conditionalStack.Count == 0)
                            malformedConditionalStructure = true;
                        else
                            conditionalStack.RemoveAt(conditionalStack.Count - 1);
                        break;
                }
            }
            conditionalStates[index] = GetConditionalLineState(
                conditionalStack,
                malformedConditionalStructure);
            WmlRawStringScanner.UpdateState(line, ref stringState);
        }
        if (conditionalStack.Count != 0)
            malformedConditionalStructure = true;
        return new LexicalFacts(
            startsInsideString,
            conditionalStates,
            !malformedConditionalStructure);
    }

    private static WmlPreprocessorDirectiveKind GetStrictStructuralDirectiveKind(
        string trimmedLine)
    {
        if (string.Equals(trimmedLine, "#endif", StringComparison.OrdinalIgnoreCase))
            return WmlPreprocessorDirectiveKind.EndIf;
        if (string.Equals(trimmedLine, "#else", StringComparison.OrdinalIgnoreCase) ||
            HasDirectiveKeyword(trimmedLine, "#else") ||
            HasDirectiveKeyword(trimmedLine, "#elseif"))
        {
            return WmlPreprocessorDirectiveKind.Else;
        }
        if (HasDirectiveKeyword(trimmedLine, "#ifdef") ||
            HasDirectiveKeyword(trimmedLine, "#ifndef") ||
            HasDirectiveKeyword(trimmedLine, "#ifhave") ||
            HasDirectiveKeyword(trimmedLine, "#ifnhave") ||
            HasDirectiveKeyword(trimmedLine, "#ifver") ||
            HasDirectiveKeyword(trimmedLine, "#ifnver") ||
            string.Equals(trimmedLine, "#if", StringComparison.OrdinalIgnoreCase) ||
            HasDirectiveKeyword(trimmedLine, "#if"))
        {
            return WmlPreprocessorDirectiveKind.IfStart;
        }
        return WmlPreprocessorDirectiveKind.None;
    }

    private static bool HasDirectiveKeyword(string line, string keyword)
        => line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
           line.Length > keyword.Length &&
           char.IsWhiteSpace(line[keyword.Length]);

    private static bool TryEvaluateSimpleConditional(
        string trimmedDirective,
        WmlEvaluationContext context,
        out bool selected)
    {
        selected = false;
        var activeStandardDefines = StandardDifficultyDefines
            .Where(context.DefinedSymbols.Contains)
            .ToArray();
        if (activeStandardDefines.Length != 1 ||
            !string.Equals(activeStandardDefines[0], context.DifficultyDefine, StringComparison.Ordinal))
        {
            return false;
        }

        if (TryReadDirectiveSymbol(trimmedDirective, "#ifdef", out var symbol) &&
            StandardDifficultyDefines.Contains(symbol, StringComparer.Ordinal))
        {
            selected = context.IsDefined(symbol);
            return true;
        }
        if (TryReadDirectiveSymbol(trimmedDirective, "#ifndef", out symbol) &&
            StandardDifficultyDefines.Contains(symbol, StringComparer.Ordinal))
        {
            selected = !context.IsDefined(symbol);
            return true;
        }
        return false;
    }

    private static bool TryReadDirectiveSymbol(
        string trimmedDirective,
        string keyword,
        out string symbol)
    {
        symbol = string.Empty;
        if (!trimmedDirective.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) ||
            trimmedDirective.Length <= keyword.Length ||
            !char.IsWhiteSpace(trimmedDirective[keyword.Length]))
        {
            return false;
        }

        var remainder = trimmedDirective[keyword.Length..].Trim();
        if (remainder.Length == 0 ||
            remainder.Any(char.IsWhiteSpace) ||
            !IsPreprocessorIdentifier(remainder))
            return false;
        symbol = remainder;
        return true;
    }

    private static bool IsPreprocessorIdentifier(string value)
    {
        if (value.Length == 0 || !(value[0] == '_' || char.IsAsciiLetter(value[0])))
            return false;
        return value.Skip(1).All(character =>
            character == '_' || char.IsAsciiLetterOrDigit(character));
    }

    private static ConditionalLineState GetConditionalLineState(
        IReadOnlyList<ConditionalFrame> stack,
        bool malformed)
    {
        if (malformed)
            return ConditionalLineState.Unknown;
        if (stack.Count == 0)
            return ConditionalLineState.Unconditional;
        if (stack.Any(frame => frame.IsKnown && !frame.IsSelected))
            return ConditionalLineState.Excluded;
        if (stack.Any(frame => !frame.IsKnown))
            return ConditionalLineState.Unknown;
        return ConditionalLineState.Selected;
    }

    private static ScenarioScalarSourceRouteAnalysis RawAnalysis(
        ScenarioDocument effective,
        string explanation,
        WmlEvaluationContext context)
    {
        var sideValues = effective.Scenario.Sides
            .SelectMany(side => new[]
            {
                Raw(ScenarioScalarSourceField.SideGold, side.SideNumber, side.Gold, explanation),
                Raw(ScenarioScalarSourceField.SideIncome, side.SideNumber, side.Income, explanation)
            })
            .ToArray();
        return StampEffectiveScenarioId(
            new ScenarioScalarSourceRouteAnalysis
            {
                Turns = Raw(
                    ScenarioScalarSourceField.Turns,
                    null,
                    effective.Scenario.Turns,
                    explanation),
                SideValues = sideValues
            },
            effective,
            context);
    }

    private static ScenarioScalarSourceRouteAnalysis StampEffectiveScenarioId(
        ScenarioScalarSourceRouteAnalysis analysis,
        ScenarioDocument effective,
        WmlEvaluationContext context)
    {
        var effectiveId = WmlDisplayStringNormalizer.Normalize(effective.Scenario.Id);
        return new ScenarioScalarSourceRouteAnalysis
        {
            Turns = analysis.Turns with
            {
                EffectiveScenarioId = effectiveId,
                AnalysisContextSemanticKey = context.SemanticKey
            },
            SideValues = analysis.SideValues
                .Select(route => route with
                {
                    EffectiveScenarioId = effectiveId,
                    AnalysisContextSemanticKey = context.SemanticKey
                })
                .ToArray()
        };
    }

    private static ScenarioScalarSourceRoute Raw(
        ScenarioScalarSourceField field,
        int? sideNumber,
        int? effectiveValue,
        string explanation,
        WmlTextSpan? sourceSpan = null,
        SourceDocument? source = null,
        WmlTextSpan? invocationSpan = null,
        string? helperName = null,
        int? helperArgumentIndex = null)
    {
        var lineIndex = sourceSpan?.StartLine;
        var lineText = lineIndex is { } index && source is not null &&
                       index >= 0 && index < source.Lines.Count
            ? source.Lines[index].Text
            : null;
        return new ScenarioScalarSourceRoute(
            field,
            sideNumber,
            effectiveValue,
            ScenarioScalarSourceRouteKind.RawOnly,
            sourceSpan,
            lineIndex,
            lineText,
            explanation,
            helperName,
            helperArgumentIndex,
            invocationSpan);
    }

    private static bool Contains(WmlTextSpan outer, WmlTextSpan inner)
        => outer.StartOffset <= inner.StartOffset &&
           inner.EndOffsetExclusive <= outer.EndOffsetExclusive;

    private static bool HasSameSourceSpan(SourceCandidate left, SourceCandidate right)
        => left.SourceSpan.StartOffset == right.SourceSpan.StartOffset &&
           left.SourceSpan.EndOffsetExclusive == right.SourceSpan.EndOffsetExclusive;

    private static bool IsOrdinaryOpeningTag(string code, string tagName)
    {
        if (!code.StartsWith('[') || !code.EndsWith(']'))
            return false;
        var inner = code[1..^1].Trim();
        if (inner.StartsWith('/') || inner.StartsWith('+') || inner.StartsWith('-'))
            return false;
        var stop = inner.IndexOfAny([' ', '\t']);
        var name = stop < 0 ? inner : inner[..stop];
        return string.Equals(name, tagName, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindCodeEndColumn(string line)
    {
        var split = WmlInlineCommentSplitter.Split(line);
        return split.InlineCommentSuffix.Length == 0
            ? line.Length
            : line.Length - split.InlineCommentSuffix.Length;
    }

    private static bool TryParsePlainInteger(string value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool ContainsBareCarriageReturn(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n'))
                return true;
        }
        return false;
    }

    private sealed record AuthoredSide(
        SourceOwner Owner,
        int? SideNumber,
        string? IdentityProblem,
        WmlTextSpan? IdentitySpan);

    private sealed record FieldOccurrence(
        SourceOwner Owner,
        string ValueText,
        WmlTextSpan ValueSpan,
        ConditionalLineState ConditionalState,
        bool IsInsideMacroDefinition);

    private sealed record LexicalFacts(
        bool[] StartsInsideMultilineString,
        ConditionalLineState[] ConditionalStates,
        bool HasReliableConditionalStructure);

    private enum ConditionalLineState
    {
        Unconditional,
        Selected,
        Excluded,
        Unknown
    }

    private sealed class ConditionalFrame(bool isKnown, bool isSelected)
    {
        public bool IsKnown { get; set; } = isKnown;
        public bool IsSelected { get; set; } = isSelected;
        public bool HasAlternative { get; set; }
    }

    private sealed record SourceCandidate(
        ScenarioScalarSourceRouteKind Kind,
        WmlTextSpan SourceSpan,
        int? Value,
        WmlTextSpan? InvocationSpan = null,
        string? HelperName = null,
        int? HelperArgumentIndex = null,
        string? Problem = null)
    {
        public bool IsContextSelectedConditional { get; init; }
        public ConditionalLineState ConditionalState { get; init; }

        public static SourceCandidate Invalid(
            WmlTextSpan sourceSpan,
            string problem,
            WmlTextSpan? invocationSpan = null,
            string? helperName = null,
            int? helperArgumentIndex = null)
            => new(
                ScenarioScalarSourceRouteKind.RawOnly,
                sourceSpan,
                null,
                invocationSpan,
                helperName,
                helperArgumentIndex,
                problem);
    }

    private sealed class SourceOwner
    {
        private readonly IReadOnlyList<WmlMacroDefinition> _macroDefinitions;

        public SourceOwner(
            WmlProjectionNode node,
            SourceDocument source,
            LexicalFacts lexical,
            IReadOnlyList<WmlMacroDefinition> macroDefinitions,
            IReadOnlyList<WmlMacroInvocationSyntax> invocations)
        {
            Node = node;
            Source = source;
            Lexical = lexical;
            _macroDefinitions = macroDefinitions;
            Invocations = invocations;
            TopLevelLines = Enumerable.Range(
                    node.StartLine + 1,
                    Math.Max(0, node.EndLineExclusive - node.StartLine - 2))
                .Where(line => !node.Children.Any(child =>
                    child.StartLine <= line && line < child.EndLineExclusive))
                .ToHashSet();
        }

        public WmlProjectionNode Node { get; }
        public SourceDocument Source { get; }
        public LexicalFacts Lexical { get; }
        public IReadOnlyList<WmlMacroInvocationSyntax> Invocations { get; }
        public HashSet<int> TopLevelLines { get; }
        public bool IsConditional =>
            Node.PreprocessorConditional ||
            Lexical.ConditionalStates[Node.StartLine] != ConditionalLineState.Unconditional;
        public bool IsInsideMacroDefinition => IsMacroDefinitionLine(Node.StartLine);

        public bool IsMacroDefinitionLine(int lineIndex)
            => _macroDefinitions.Any(definition =>
                definition.StartLineIndex <= lineIndex &&
                lineIndex < definition.EndLineIndexExclusive);
    }

    private sealed class SourceDocument
    {
        public SourceDocument(string text)
        {
            Text = text;
            Lines = ReadLines(text);
        }

        public string Text { get; }
        public IReadOnlyList<SourceLine> Lines { get; }

        public WmlTextSpan CreateSpan(int startOffset, int endOffsetExclusive, int lineIndex)
            => new(
                startOffset,
                endOffsetExclusive,
                lineIndex,
                startOffset - Lines[lineIndex].StartOffset,
                lineIndex,
                endOffsetExclusive - Lines[lineIndex].StartOffset);

        public WmlTextSpan SpanForLineCode(int lineIndex)
        {
            var line = Lines[lineIndex];
            var startColumn = 0;
            while (startColumn < line.Text.Length && char.IsWhiteSpace(line.Text[startColumn]))
                startColumn++;
            var endColumn = FindCodeEndColumn(line.Text);
            while (endColumn > startColumn && char.IsWhiteSpace(line.Text[endColumn - 1]))
                endColumn--;
            return CreateSpan(
                line.StartOffset + startColumn,
                line.StartOffset + endColumn,
                lineIndex);
        }

        public bool IsOnlyCodeOnLine(WmlTextSpan span)
        {
            if (span.StartLine != span.EndLine)
                return false;
            var expected = SpanForLineCode(span.StartLine);
            return expected.StartOffset == span.StartOffset &&
                   expected.EndOffsetExclusive == span.EndOffsetExclusive;
        }

        private static IReadOnlyList<SourceLine> ReadLines(string text)
        {
            var result = new List<SourceLine>();
            var start = 0;
            var lineIndex = 0;
            for (var offset = 0; offset < text.Length; offset++)
            {
                if (text[offset] != '\n')
                    continue;
                var contentEnd = offset > start && text[offset - 1] == '\r'
                    ? offset - 1
                    : offset;
                result.Add(new SourceLine(
                    lineIndex++,
                    start,
                    contentEnd,
                    text[start..contentEnd]));
                start = offset + 1;
            }
            result.Add(new SourceLine(
                lineIndex,
                start,
                text.Length,
                text[start..]));
            return result;
        }
    }

    private sealed record SourceLine(
        int Index,
        int StartOffset,
        int ContentEndOffset,
        string Text);
}

internal static class ScenarioScalarSourceRouteStringExtensions
{
    public static bool ContainsAny(this string value, IReadOnlyList<char> characters)
        => value.IndexOfAny(characters.ToArray()) >= 0;
}
