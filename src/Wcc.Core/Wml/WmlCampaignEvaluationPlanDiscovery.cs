namespace Wcc.Core.Wml;

/// <summary>
/// Discovers editor context metadata without claiming to evaluate the complete
/// campaign. Literal declarations can be complete; macro-derived declarations
/// remain useful preview evidence until their active definitions are bound.
/// </summary>
public static class WmlCampaignEvaluationPlanDiscovery
{
    private static readonly HashSet<string> CampaignKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "define", "difficulties", "default_difficulty", "extra_defines"
    };

    private static readonly HashSet<string> DifficultyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "define", "image", "label", "description", "default"
    };

    public static IReadOnlyList<WmlCampaignEvaluationPlan> Discover(
        string mainCfgText,
        string campaignMainSourcePath,
        string? targetCompatibility = null,
        IEnumerable<string>? additionalDefines = null)
    {
        ArgumentNullException.ThrowIfNull(mainCfgText);
        if (string.IsNullOrWhiteSpace(campaignMainSourcePath))
            throw new ArgumentException("A campaign-main source path is required.", nameof(campaignMainSourcePath));

        // The existing source scanners do not share bare-CR line accounting.
        // Preserve usable preview metadata without assigning normalized offsets
        // to authored text or allowing it to become an exhaustive edit scope.
        if (mainCfgText.Replace("\r\n", "\n", StringComparison.Ordinal).Contains('\r'))
        {
            var normalized = mainCfgText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            return Array.AsReadOnly(Discover(normalized, campaignMainSourcePath, targetCompatibility, additionalDefines)
                .Select(plan => new WmlCampaignEvaluationPlan(
                    plan.CampaignId, plan.CampaignDefine,
                    plan.Difficulties.Select(difficulty => difficulty with { SourceSpan = null, RawDeclaration = null }),
                    plan.Contexts,
                    plan.Diagnostics.Append("Bare carriage-return line endings are supported for discovery preview only; authored source coordinates remain unverified."),
                    campaignMainSourcePath, mainCfgText, isContextDiscoveryComplete: false))
                .ToArray());
        }

        var sourceDefinitions = WmlMacroDefinitionScanner.Scan(mainCfgText);
        var sourceInvocations = WmlMacroInvocationSyntaxScanner.Scan(mainCfgText);
        var definitions = sourceDefinitions
            .Select(definition => new WmlMacroExpansionDefinition(
                definition.Name,
                definition.RawText,
                SourcePath: campaignMainSourcePath,
                SourceStartLine: definition.StartLineIndex))
            .ToArray();
        var suppliedDefines = additionalDefines?.ToArray() ?? [];
        var hasUnselectedControlFlow = HasPreprocessorControl(mainCfgText);
        var campaignSpans = WmlBlockScanner.FindTopLevelBlockSpans(mainCfgText)
            .Where(x => string.Equals(x.TagName, "campaign", StringComparison.OrdinalIgnoreCase))
            .Where(span => !sourceDefinitions.Any(definition =>
            {
                var line = CountLines(mainCfgText.AsSpan(0, span.StartIndex));
                return definition.StartLineIndex <= line && line < definition.EndLineIndexExclusive;
            }))
            .Where(span => !sourceInvocations.Any(invocation =>
                invocation.Span.StartOffset <= span.StartIndex &&
                span.EndIndexExclusive <= invocation.Span.EndOffsetExclusive))
            .ToArray();
        var hasUnboundOuterContributions = sourceInvocations
            .Any(invocation => !invocation.IsInsideMacroDefinition && !campaignSpans.Any(span =>
                span.StartIndex <= invocation.Span.StartOffset &&
                invocation.Span.EndOffsetExclusive <= span.EndIndexExclusive)) ||
            HasCampaignAmendment(mainCfgText);
        var plans = new List<WmlCampaignEvaluationPlan>();

        foreach (var span in campaignSpans)
        {
            var editable = WmlEditableBlockParser.Parse(
                new WmlBlock(span.TagName, span.RawText), CampaignKeys);
            var diagnostics = new List<string>();
            var complete = !hasUnselectedControlFlow && !hasUnboundOuterContributions;
            if (!string.Equals(span.TagName, "campaign", StringComparison.Ordinal))
            {
                complete = false;
                diagnostics.Add("The campaign tag uses noncanonical casing; this tolerant preview cannot define an edit scope.");
            }
            if (hasUnselectedControlFlow)
                diagnostics.Add("Campaign source contains preprocessor control flow that context discovery has not selected.");
            if (hasUnboundOuterContributions)
                diagnostics.Add("Includes, macros, or campaign amendments outside the campaign block may contribute context metadata that discovery has not bound.");
            if (HasUnsupportedContextAssignment(span.RawText))
            {
                complete = false;
                diagnostics.Add("Multiple-key assignments or noncanonical context key casing are not yet interpreted by discovery.");
            }

            var campaignDefine = Unquote(ReadPlain(editable, "define"));
            var defineAnalysis = WmlTopLevelFieldAnalyzer.Analyze(editable, "define");
            if (defineAnalysis.Kind == WmlTopLevelFieldKind.ConditionalOrMultiple ||
                (campaignDefine is not null && !IsSymbol(campaignDefine)))
            {
                complete = false;
                diagnostics.Add("The campaign define is conditional, repeated, or not a literal preprocessor symbol.");
            }

            var discovered = ReadMenu(
                span.RawText, definitions, diagnostics, allowExpansion: true,
                sourceOffset: span.StartIndex,
                sourceLine: CountLines(mainCfgText.AsSpan(0, span.StartIndex)));
            complete &= discovered.Complete;
            var difficulties = discovered.Difficulties;

            var legacyAnalysis = WmlTopLevelFieldAnalyzer.Analyze(editable, "difficulties");
            var legacy = ReadLegacyDifficulties(editable, diagnostics, ref complete);
            if (difficulties.Count == 0)
                difficulties.AddRange(legacy);
            else if (legacyAnalysis.Kind != WmlTopLevelFieldKind.None)
            {
                complete = false;
                diagnostics.Add("Difficulty entries coexist with a legacy difficulties= list; the entry preview is retained, but the complete menu is not established.");
            }

            var extras = ReadExtraDefines(editable, diagnostics, ref complete)
                .Concat(suppliedDefines)
                .ToArray();
            if (extras.Any(symbol => !IsSymbol(symbol.Trim())))
            {
                complete = false;
                diagnostics.Add("Additional defines contain a nonliteral preprocessor symbol.");
            }

            var duplicateDefines = difficulties
                .GroupBy(x => x.Define, StringComparer.Ordinal)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            if (duplicateDefines.Length > 0)
            {
                complete = false;
                diagnostics.Add($"Duplicate difficulty define(s): {string.Join(", ", duplicateDefines)}.");
            }
            if (difficulties.Count(x => x.IsDefault) > 1)
                diagnostics.Add("More than one difficulty is marked as default; WCC retains every declaration without choosing one silently.");

            var contexts = difficulties.Count == 0
                ? new[] { new WmlEvaluationContext(campaignDefine, additionalDefines: extras, targetCompatibility: targetCompatibility) }
                : difficulties.Select(difficulty => new WmlEvaluationContext(
                    campaignDefine, difficulty.Define, extras, targetCompatibility)).ToArray();
            if (!complete)
                diagnostics.Add("The displayed contexts are a discovery preview; all-context edit validation is unavailable until discovery is complete.");

            plans.Add(new WmlCampaignEvaluationPlan(
                Unquote(ReadPlain(editable, "id")),
                campaignDefine,
                difficulties,
                contexts,
                diagnostics.Distinct(StringComparer.Ordinal),
                campaignMainSourcePath,
                mainCfgText,
                isContextDiscoveryComplete: complete) { AuthoredCampaignSpan = span });
        }

        return Array.AsReadOnly(plans.ToArray());
    }

    private static MenuDiscovery ReadMenu(
        string campaignText,
        IReadOnlyList<WmlMacroExpansionDefinition> definitions,
        List<string> diagnostics,
        bool allowExpansion,
        int? sourceOffset = null,
        int sourceLine = 0)
    {
        var result = new MenuDiscovery();
        var campaign = WmlProjectionTreeParser.Parse(campaignText).Roots
            .FirstOrDefault(node => string.Equals(node.TagName, "campaign", StringComparison.OrdinalIgnoreCase));
        if (campaign is null)
        {
            result.Complete = false;
            diagnostics.Add("The expanded campaign menu has no readable campaign owner.");
            return result;
        }

        var lineStarts = LineStarts(campaignText);
        var internalDefinitions = WmlMacroDefinitionScanner.Scan(campaignText);
        if (internalDefinitions.Count > 0)
        {
            result.Complete = false;
            diagnostics.Add("Definitions inside campaign metadata require source-order evaluation before the menu is complete.");
        }
        var invocations = WmlMacroInvocationSyntaxScanner.Scan(campaignText)
            .Where(invocation => !invocation.IsInsideMacroDefinition && invocation.NestingDepth == 0)
            .ToArray();
        var masked = campaignText.ToCharArray();
        foreach (var invocation in invocations)
        {
            for (var index = invocation.Span.StartOffset; index < invocation.Span.EndOffsetExclusive; index++)
            {
                if (masked[index] is not '\r' and not '\n')
                    masked[index] = ' ';
            }
        }
        var maskedLines = new string(masked).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var events = campaign.Children
            .Where(node => string.Equals(node.TagName, "difficulty", StringComparison.OrdinalIgnoreCase))
            .Where(node => !internalDefinitions.Any(definition =>
                definition.StartLineIndex <= node.StartLine && node.StartLine < definition.EndLineIndexExclusive))
            .Select(node => new MenuPart(lineStarts[node.StartLine], node, null))
            .ToList();
        foreach (var invocation in invocations)
        {
            if (campaign.Children.Any(child =>
                    child.StartLine <= invocation.Span.StartLine &&
                    invocation.Span.StartLine < child.EndLineExclusive))
            {
                result.Complete = false;
                diagnostics.Add("A campaign child contains macro content whose effect on context metadata has not been established.");
                continue;
            }
            if (invocation.Span.StartLine <= campaign.StartLine ||
                invocation.Span.EndLine >= campaign.EndLineExclusive - 1)
                continue;
            if (!IsStandalone(invocation, maskedLines))
            {
                result.Complete = false;
                diagnostics.Add("Inline macro content in campaign metadata is not fully resolved.");
                continue;
            }
            events.Add(new MenuPart(invocation.Span.StartOffset, null, invocation));
        }

        foreach (var part in events.OrderBy(part => part.Offset))
        {
            if (part.Node is { } node)
            {
                var openingCode = WmlInlineCommentSplitter.Split(node.SourceLines[node.StartLine]).Value.Trim();
                if (!string.Equals(openingCode, "[difficulty]", StringComparison.Ordinal))
                {
                    result.Complete = false;
                    diagnostics.Add("A difficulty amendment or noncanonical tag requires interpretation before the menu is complete.");
                    continue;
                }
                var difficulty = ReadDirectDifficulty(node.RawText);
                if (difficulty is null)
                {
                    result.Complete = false;
                    diagnostics.Add("A [difficulty] entry does not supply one literal define.");
                }
                else
                {
                    result.Difficulties.Add(difficulty);
                }
                if (node.PreprocessorConditional || HasPreprocessorControl(node.RawText))
                    result.Complete = false;
                continue;
            }

            var invocation = part.Invocation!;
            result.Complete = false;
            if (string.Equals(invocation.Name, "CAMPAIGN_DIFFICULTY", StringComparison.Ordinal))
            {
                var difficulty = ReadModernDifficulty(invocation, invocations);
                if (difficulty is not null)
                {
                    result.Difficulties.Add(difficulty with
                    {
                        SourceSpan = sourceOffset is { } offset
                            ? TranslateSpan(invocation.Span, offset, sourceLine)
                            : null
                    });
                }
                diagnostics.Add("CAMPAIGN_DIFFICULTY calls provide menu preview evidence; their active macro definitions are not verified by context discovery.");
                continue;
            }
            if (string.Equals(invocation.Name, "DEFAULT_DIFFICULTY", StringComparison.Ordinal))
            {
                diagnostics.Add("DEFAULT_DIFFICULTY is retained as macro preview evidence.");
                continue;
            }

            if (!allowExpansion)
            {
                diagnostics.Add($"Campaign macro '{{{invocation.Name}}}' remains unresolved; it may contribute further difficulty declarations.");
                continue;
            }

            var expansion = new WmlMacroExpansionService().Expand(
                invocation.RawText,
                definitions,
                options: new WmlMacroExpansionOptions(MaxExpansionDepth: 16, MaxExpandedLines: 4096));
            if (expansion.Expansions.Count == 0)
            {
                diagnostics.Add($"Campaign macro '{{{invocation.Name}}}' remains unresolved; it may contribute further difficulty declarations.");
                continue;
            }

            var nested = ReadMenu(
                "[campaign]\n" + expansion.ExpandedText + "\n[/campaign]",
                definitions, diagnostics, allowExpansion: false);
            result.Difficulties.AddRange(nested.Difficulties);
            diagnostics.Add("Difficulty contexts were discovered from conservatively expanded local campaign macros; invocation-time definition binding remains unverified.");
            if (expansion.Issues.Count > 0)
                diagnostics.Add($"Local campaign macro '{{{invocation.Name}}}' was only partially expanded.");
        }

        return result;
    }

    private static bool IsStandalone(WmlMacroInvocationSyntax invocation, IReadOnlyList<string> maskedLines)
        => Enumerable.Range(invocation.Span.StartLine, invocation.Span.EndLine - invocation.Span.StartLine + 1)
            .All(line => string.IsNullOrWhiteSpace(WmlInlineCommentSplitter.Split(maskedLines[line]).Value));

    private static WmlDifficultyVariant? ReadModernDifficulty(
        WmlMacroInvocationSyntax invocation,
        IReadOnlyList<WmlMacroInvocationSyntax> invocations)
    {
        var define = Unquote(invocation.Arguments.FirstOrDefault()?.RawText);
        if (define is null || !IsSymbol(define))
            return null;
        var isDefault = invocations.Any(other =>
            other.NestingDepth == 0 &&
            other.Span.StartLine == invocation.Span.EndLine &&
            other.Span.StartOffset >= invocation.Span.EndOffsetExclusive &&
            string.Equals(other.Name, "DEFAULT_DIFFICULTY", StringComparison.Ordinal) &&
            !invocations.Any(between =>
                between.Span.StartOffset > invocation.Span.StartOffset &&
                between.Span.StartOffset < other.Span.StartOffset &&
                between.NestingDepth == 0));
        return new WmlDifficultyVariant(
            define,
            invocation.Arguments.ElementAtOrDefault(1)?.RawText,
            invocation.Arguments.ElementAtOrDefault(2)?.RawText,
            invocation.Arguments.ElementAtOrDefault(3)?.RawText,
            isDefault,
            invocation.Span,
            invocation.RawText);
    }

    private static WmlDifficultyVariant? ReadDirectDifficulty(string raw)
    {
        var editable = WmlEditableBlockParser.Parse(new WmlBlock("difficulty", raw), DifficultyKeys);
        var define = Unquote(ReadPlain(editable, "define"));
        if (define is null || !IsSymbol(define))
            return null;
        var defaultValue = Unquote(ReadPlain(editable, "default"));
        return new WmlDifficultyVariant(
            define,
            ReadPlain(editable, "image"),
            ReadPlain(editable, "label"),
            ReadPlain(editable, "description"),
            string.Equals(defaultValue, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(defaultValue, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(defaultValue, "1", StringComparison.Ordinal),
            null,
            raw);
    }

    private static IReadOnlyList<WmlDifficultyVariant> ReadLegacyDifficulties(
        WmlEditableBlock campaign, List<string> diagnostics, ref bool complete)
    {
        var analysis = WmlTopLevelFieldAnalyzer.Analyze(campaign, "difficulties");
        if (analysis.Kind == WmlTopLevelFieldKind.None)
            return [];
        if (analysis.Kind != WmlTopLevelFieldKind.Plain)
        {
            complete = false;
            diagnostics.Add("The legacy difficulty list is conditional or repeated.");
            return [];
        }
        var raw = Unquote(analysis.PlainValue);
        var symbols = (raw ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (symbols.Any(symbol => !IsSymbol(symbol)))
        {
            complete = false;
            diagnostics.Add("The legacy difficulty list contains a nonliteral define.");
        }
        var defaultDefine = Unquote(ReadPlain(campaign, "default_difficulty"));
        return symbols.Where(IsSymbol).Select(symbol => new WmlDifficultyVariant(
            symbol, null, null, null,
            string.Equals(symbol, defaultDefine, StringComparison.Ordinal),
            null, $"difficulties={analysis.PlainValue}")).ToArray();
    }

    private static IReadOnlyList<string> ReadExtraDefines(
        WmlEditableBlock campaign, List<string> diagnostics, ref bool complete)
    {
        var analysis = WmlTopLevelFieldAnalyzer.Analyze(campaign, "extra_defines");
        if (analysis.Kind == WmlTopLevelFieldKind.None)
            return [];
        if (analysis.Kind != WmlTopLevelFieldKind.Plain)
        {
            complete = false;
            diagnostics.Add("Campaign extra_defines is conditional or repeated.");
            return [];
        }
        return (Unquote(analysis.PlainValue) ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool HasUnsupportedContextAssignment(string text)
    {
        var state = new WmlStringScanState();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var startsInsideString = state.InsideMultilineString;
            WmlRawStringScanner.UpdateState(line, ref state);
            if (startsInsideString)
                continue;
            var code = WmlInlineCommentSplitter.Split(line).Value;
            var equals = code.IndexOf('=');
            if (equals <= 0)
                continue;
            var keys = code[..equals].Split(',', StringSplitOptions.TrimEntries);
            if (keys.Any(key => CampaignKeys.Contains(key) &&
                    (keys.Length > 1 || !CampaignKeys.Any(known => string.Equals(known, key, StringComparison.Ordinal)))))
                return true;
        }
        return false;
    }

    private static bool HasPreprocessorControl(string text)
    {
        var definitions = WmlMacroDefinitionScanner.Scan(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var state = new WmlStringScanState();
        for (var index = 0; index < lines.Length; index++)
        {
            var startsInsideString = state.InsideMultilineString;
            WmlRawStringScanner.UpdateState(lines[index], ref state);
            if (startsInsideString || definitions.Any(definition =>
                    definition.StartLineIndex <= index && index < definition.EndLineIndexExclusive))
                continue;
            var trimmed = lines[index].Trim();
            if (WmlPreprocessorScanner.GetDirectiveKind(trimmed) != WmlPreprocessorDirectiveKind.None ||
                trimmed.StartsWith("#undef", StringComparison.Ordinal) ||
                trimmed.StartsWith("#define", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool HasCampaignAmendment(string text)
    {
        var state = new WmlStringScanState();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var startsInsideString = state.InsideMultilineString;
            WmlRawStringScanner.UpdateState(line, ref state);
            if (startsInsideString)
                continue;
            var code = WmlInlineCommentSplitter.Split(line).Value.Trim();
            if (string.Equals(code, "[+campaign]", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "[-campaign]", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int[] LineStarts(string text)
        => new[] { 0 }.Concat(text.Select((character, index) => (character, index))
            .Where(item => item.character == '\n').Select(item => item.index + 1)).ToArray();

    private static int CountLines(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (character == '\n')
                count++;
        }
        return count;
    }

    private static WmlTextSpan TranslateSpan(WmlTextSpan span, int offset, int line)
        => span with
        {
            StartOffset = span.StartOffset + offset,
            EndOffsetExclusive = span.EndOffsetExclusive + offset,
            StartLine = span.StartLine + line,
            EndLine = span.EndLine + line
        };

    private static bool IsSymbol(string value)
        => value.Length > 0 && (value[0] == '_' || char.IsAsciiLetter(value[0])) &&
           value.Skip(1).All(character => character == '_' || char.IsAsciiLetterOrDigit(character));

    private static string? ReadPlain(WmlEditableBlock block, string key)
    {
        var analysis = WmlTopLevelFieldAnalyzer.Analyze(block, key);
        return analysis.Kind == WmlTopLevelFieldKind.Plain ? analysis.PlainValue : null;
    }

    private static string? Unquote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length >= 2 &&
               ((trimmed[0] == '"' && trimmed[^1] == '"') ||
                (trimmed[0] == '\'' && trimmed[^1] == '\''))
            ? trimmed[1..^1]
            : trimmed;
    }

    private sealed record MenuPart(int Offset, WmlProjectionNode? Node, WmlMacroInvocationSyntax? Invocation);

    private sealed class MenuDiscovery
    {
        public List<WmlDifficultyVariant> Difficulties { get; } = [];
        public bool Complete { get; set; } = true;
    }
}
