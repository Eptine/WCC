using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Planning evidence from the real ordered evaluation. It is deliberately
/// private to the editors: the source reviewer reconstructs it for every context.</summary>
internal sealed class ScenarioInitialSettingsCapturedContext
{
    private ScenarioInitialSettingsCapturedContext(WmlStaticSourceRange range,
        WmlStaticScalarProjection projection, WmlStaticEvaluationResult evaluation,
        WmlStaticScalarOwner scenario)
    { Range = range; Projection = projection; Evaluation = evaluation; Scenario = scenario; }

    internal WmlStaticSourceRange Range { get; }
    internal WmlStaticScalarProjection Projection { get; }
    internal WmlStaticEvaluationResult Evaluation { get; }
    internal WmlStaticScalarOwner Scenario { get; }

    internal static ScenarioInitialSettingsCapturedContext? Create(WmlStaticSourceRange range,
        WmlStaticScalarProjection projection)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(projection);
        if (!range.Source.IsAuthored || range.Length > 4 * 1024 * 1024 ||
            projection.SourceEvaluation is not { IsComplete: true } evaluation || evaluation.OutputLimitExceeded ||
            evaluation.Diagnostics.Any(item => item.IsBlocking) || projection.Diagnostics.Count != 0 ||
            !evaluation.Sources.Any(source => SameSource(source, range.Source)) ||
            WccCapturedScenarioPreview.FindLiteralOwner(range, projection) is not { StructureKnown: true } scenario ||
            !WithinPlanningBounds(scenario))
            return null;
        return new(range, projection, evaluation, scenario);
    }

    private static bool WithinPlanningBounds(WmlStaticScalarOwner scenario)
    {
        var pending = new Stack<(WmlStaticScalarOwner Owner, int Depth)>();
        pending.Push((scenario, 0));
        var count = 0;
        while (pending.TryPop(out var current))
        {
            if (++count > 16_384 || current.Depth > 128) return false;
            foreach (var child in current.Owner.DirectChildren) pending.Push((child, current.Depth + 1));
        }
        return true;
    }

    internal bool Matches(WmlStaticSourceRange range) => range.StartOffset == Range.StartOffset &&
        range.Length == Range.Length && SameSource(range.Source, Range.Source);

    internal static WmlMacroExpansionDefinition? FindActiveDefinition(IEnumerable<WmlMacroExpansionDefinition> definitions,
        string name, WmlStaticSourceRange active)
    {
        if (!active.Source.IsAuthored) return null;
        var line = 0;
        for (var index = 0; index < active.StartOffset; index++)
            if (active.Source.Text[index] == '\n' || active.Source.Text[index] == '\r' &&
                (index + 1 >= active.StartOffset || active.Source.Text[index + 1] != '\n')) line++;
        var matches = definitions.Where(definition => definition.Name == name && definition.SourcePath is { } path &&
            string.Equals(path, active.Source.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            definition.SourceStartLine == line && definition.RawText.Trim() == active.Text.Trim()).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal CapturedFragment? Capture(ScenarioContentSourceEntry entry)
    {
        if (entry.StartOffset < 0 || entry.EndOffsetExclusive < entry.StartOffset || entry.EndOffsetExclusive > Range.Length) return null;
        var range = new WmlStaticSourceRange(Range.Source, Range.StartOffset + entry.StartOffset,
            entry.EndOffsetExclusive - entry.StartOffset);
        var spans = WccScenarioFieldStaticReviewer.ProjectRegionSpans(Evaluation, range);
        WmlStaticInvocationTrace? invocation = null;
        if (entry.Kind == ScenarioContentSourceKind.MacroInvocation)
        {
            var calls = Evaluation.Invocations.Where(call => call.Name == entry.Name &&
                call.InvocationOrigins.Count == 1 && SameRange(call.InvocationOrigins[0], range)).Take(2).ToArray();
            if (calls.Length != 1 || calls[0].Outcome is not
                (WmlStaticInvocationOutcome.Expanded or WmlStaticInvocationOutcome.Include)) return null;
            invocation = calls[0];
        }
        return new(this, range, spans, invocation);
    }

    internal sealed class CapturedFragment(ScenarioInitialSettingsCapturedContext context,
        WmlStaticSourceRange range, IReadOnlyList<(int Start, int End)> spans,
        WmlStaticInvocationTrace? invocation)
    {
        internal WmlStaticInvocationTrace? Invocation => invocation;
        internal IReadOnlyList<(int Start, int End)> Spans => spans;

        internal bool Contains(int start, int length = 1) => length >= 0 &&
            Covered(start, start + length, spans);

        internal bool Touches(WmlStaticScalarContribution contribution) =>
            Intersects(contribution.AssignmentOutputOffset, contribution.AssignmentOutputLength);

        internal bool Touches(WmlStaticScalarOwner owner) =>
            owner.OpeningOutputOffset is { } opening && Intersects(opening, owner.OpeningOrigin!.Length) ||
            owner.ClosingOutputOffset is { } closing && Intersects(closing, owner.ClosingOrigin!.Length) ||
            owner.Fields.Values.SelectMany(field => field.Contributions).Any(Touches) ||
            owner.DirectChildren.Any(Touches);

        internal bool Owns(WmlStaticScalarOwner owner) => !owner.IsAmended && owner.StructureKnown &&
            owner.OpeningOutputOffset is { } opening && owner.OpeningOrigin is { } openingOrigin &&
            Contains(opening, openingOrigin.Length) &&
            owner.ClosingOutputOffset is { } closing && owner.ClosingOrigin is { } closingOrigin &&
            Contains(closing, closingOrigin.Length) && owner.Fields.Values.All(field => field.IsKnown &&
                field.Contributions.Count != 0 && field.Contributions.All(contribution =>
                    Contains(contribution.AssignmentOutputOffset, contribution.AssignmentOutputLength))) &&
            owner.DirectChildren.All(Owns);

        // Definition/include mutations have effects beyond emitted WML. A replaced
        // call cannot borrow the fact that its current winning time looks pure.
        internal bool HasStatefulEffects => DirectiveNames(range.Text).Any(name => name is "define" or "undef" ||
                name == "textdomain" && !HasBalancedLiteralTimeDomains) ||
            invocation?.Outcome == WmlStaticInvocationOutcome.Include ||
            context.Evaluation.Invocations.Where(Belongs).Any(call => call.Outcome is
                WmlStaticInvocationOutcome.Include or WmlStaticInvocationOutcome.IncludeUnresolved ||
                call.Definition is { } definition && DefinitionMutates(definition.Text));

        // Materialized times retain their translated names under a local domain
        // switch. Only a literal, balanced block may restore the proven lexical
        // domain; a macro/include still cannot conceal preprocessor mutations.
        private bool HasBalancedLiteralTimeDomains
        {
            get
            {
                if (invocation is not null || !WmlSingleBlockValidator.Validate(range.Text, "time").IsValid) return false;
                var structure = WmlAuthoredStructureScanner.Scan(range.Text);
                if (structure.Directives.Any(directive => directive.Name == "textdomain" &&
                        (directive.DefinitionDepth != 0 || structure.ConditionalLines[directive.Line]))) return false;
                return WmlTextDomainCapture.BeforeOffset(range.Source.Text, range.StartOffset) is { } before &&
                    WmlTextDomainCapture.BeforeOffset(range.Source.Text, range.StartOffset + range.Length) == before;
            }
        }

        internal bool OnlyEmits(IReadOnlyList<(int Start, int End)> allowed)
        {
            foreach (var span in spans)
            {
                var cursor = span.Start;
                foreach (var part in allowed.Where(part => part.End > cursor && part.Start < span.End).OrderBy(part => part.Start))
                {
                    if (part.Start > cursor && !Trivia(context.Evaluation.Text[cursor..Math.Min(part.Start, span.End)])) return false;
                    cursor = Math.Max(cursor, Math.Min(part.End, span.End));
                }
                if (cursor < span.End && !Trivia(context.Evaluation.Text[cursor..span.End])) return false;
            }
            return true;
        }

        private bool Belongs(WmlStaticInvocationTrace call) => call.InvocationOrigins.Any(origin => Inside(origin, range)) ||
            call.ExpansionStack.Any(frame => frame.InvocationOrigins.Any(origin => Inside(origin, range)));
        private bool Intersects(int start, int length) => spans.Any(span => start < span.End && span.Start < start + length);
    }

    internal static (int Start, int End)? OwnerSpan(WmlStaticScalarOwner owner) =>
        owner.OpeningOutputOffset is { } opening && owner.ClosingOutputOffset is { } closing &&
        owner.ClosingOrigin is { } origin ? (opening, closing + origin.Length) : null;

    private static bool DefinitionMutates(string raw)
    {
        var headerSeen = false;
        foreach (var directive in DirectiveNames(raw))
        {
            if (directive == "define" && !headerSeen) { headerSeen = true; continue; }
            if (directive is "define" or "undef" or "textdomain") return true;
        }
        return false;
    }

    private static IEnumerable<string> DirectiveNames(string raw) => WmlEditorAnalysis.Build(raw).Tokens
        .Where(token => token.Kind == WmlEditorTokenKind.Directive)
        .Select(token => raw.Substring(token.StartOffset, token.Length).TrimStart('#')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");

    private static bool Trivia(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0 && !trimmed.StartsWith('#')) return false;
        }
        return true;
    }

    private static bool Covered(int start, int end, IReadOnlyList<(int Start, int End)> spans)
    {
        var cursor = start;
        foreach (var span in spans.Where(span => span.End > start && span.Start <= end).OrderBy(span => span.Start))
        { if (span.Start > cursor) return false; cursor = Math.Max(cursor, span.End); if (cursor >= end) return true; }
        return cursor >= end;
    }

    internal static bool SameSource(WmlStaticSource left, WmlStaticSource right) => left.IsAuthored && right.IsAuthored &&
        WccScenarioFieldRouteAnalyzer.SameSource(left, right) && left.Text == right.Text;
    internal static bool SameRange(WmlStaticSourceRange left, WmlStaticSourceRange right) =>
        left.StartOffset == right.StartOffset && left.Length == right.Length && SameSource(left.Source, right.Source);
    private static bool Inside(WmlStaticSourceRange origin, WmlStaticSourceRange range) => SameSource(origin.Source, range.Source) &&
        origin.StartOffset >= range.StartOffset && origin.StartOffset + origin.Length <= range.StartOffset + range.Length;
}
