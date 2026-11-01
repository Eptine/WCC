using Wcc.Core.Wml;

namespace Wcc.Core.Library;

/// <summary>Per-physical-unit preview diagnostics. These observations never activate a unit or authorize an edit.</summary>
public sealed record WccUnitPreviewContextObservation(bool UsesConditionalPreprocessing,
    bool IsInactiveDefinition, string? Notice, string? UnavailableReason)
{
    /// <summary>
    /// Follows this unit's actual base owners and reached macro definitions. Unrelated units,
    /// unused prelude definitions and the campaign's registration guard are not preview notices.
    /// </summary>
    public static WccUnitPreviewContextObservation Observe(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, WmlStaticSourceRange selectedUnit,
        string? libraryContextNotice, string? exactCoreVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(selectedUnit);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(projection.SourceEvaluation, evaluation) || !evaluation.Sources.Contains(selectedUnit.Source) ||
            selectedUnit.StartOffset < 0 || selectedUnit.Length <= 0 ||
            selectedUnit.StartOffset > selectedUnit.Source.Text.Length - selectedUnit.Length)
            return new(false, false, null, "The unit preview has no matching evaluated source range.");
        var structures = new Dictionary<WmlStaticSource, WmlAuthoredStructure>();
        var ranges = new List<(WmlStaticSourceRange Range, bool Definition)>();
        var reachedDefinitions = new HashSet<WmlStaticSourceRange>();
        var conditions = new HashSet<string>(StringComparer.Ordinal);
        var selectedGuards = new List<string>();
        var versionConditions = false;
        var otherConditions = false;
        var selected = projection.Owners.Where(owner => ExactOwner(owner, selectedUnit)).Take(2).ToArray();
        AddRange(selectedUnit, false, selectedGuards);
        if (selected.Length == 1)
        {
            var registry = WmlUnitReferenceProjection.Create(evaluation, projection, cancellationToken);
            var visited = new HashSet<WmlStaticScalarOwner>();
            AddBases(selected[0], 0);

            void AddBases(WmlStaticScalarOwner owner, int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth >= 64 || !visited.Add(owner)) return;
                if (owner.OpeningOrigin is { } opening && owner.ClosingOrigin is { } closing &&
                    ReferenceEquals(opening.Source, closing.Source) && closing.StartOffset + closing.Length > opening.StartOffset)
                    AddRange(new(opening.Source, opening.StartOffset, closing.StartOffset + closing.Length - opening.StartOffset), false);
                else if (PhysicalUnitForOpening(evaluation, owner, cancellationToken) is { } physical)
                    // Some active helpers close/reopen the unit. Its evaluated
                    // closing tag is then in the helper, while earlier physical
                    // invocations still belong to this actual registry base.
                    // This range supplies notices only, never an owner/edit route.
                    AddRange(physical, false);
                var basis = owner.DirectChildren.FirstOrDefault(child => child.TagName == "base_unit");
                if (!registry.HasCompleteDeclaredInventory || basis is null ||
                    !WmlUnitReferenceProjection.TryLiteral(basis.GetKnownValue("id"), out var baseId)) return;
                var definitions = registry.Declarations.Where(declaration => declaration.Id == baseId).Take(2).ToArray();
                if (definitions.Length == 1) AddBases(definitions[0].Owner, depth + 1);
            }
        }
        for (var index = 0; index < ranges.Count && index < 512; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = ranges[index].Range;
            foreach (var invocation in evaluation.Invocations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (invocation.Outcome != WmlStaticInvocationOutcome.Expanded || invocation.Definition is not { } definition ||
                    !invocation.InvocationOrigins.Any(origin => Within(origin, range)) || !reachedDefinitions.Add(definition)) continue;
                AddRange(definition, true);
            }
        }
        var complete = evaluation.IsComplete && !evaluation.OutputLimitExceeded && projection.Diagnostics.Count == 0;
        var visits = evaluation.Includes.Count(include => include.Resolution.Exists == WmlStaticTruth.True &&
            include.Resolution.Sources.Any(source => ReferenceEquals(source, selectedUnit.Source))) +
            (ReferenceEquals(evaluation.SourceEntry, selectedUnit.Source) ? 1 : 0);
        var inactive = complete && selected.Length == 0 && visits == 1 && selectedGuards.Count != 0 &&
            IsExactPhysicalUnit(selectedUnit);
        var context = evaluation.SourceContext?.DisplayName;
        var version = string.IsNullOrWhiteSpace(exactCoreVersion) ? "the selected Wesnoth installation" : "Wesnoth " + exactCoreVersion;
        if (inactive)
        {
            var reason = "This physical unit definition is inactive " +
                (versionConditions && !otherConditions ? "for " + version +
                    (string.IsNullOrWhiteSpace(context) ? "" : " in preview context " + context)
                    : "in " + (string.IsNullOrWhiteSpace(context) ? "the current preview context" : "preview context " + context)) +
                ". Its enclosing preprocessor guard selects another branch: " + string.Join("; ", selectedGuards.Take(3)) + ". RAW source remains available.";
            return new(true, true, reason, reason);
        }
        string? unavailable = null;
        if (!complete)
        {
            var diagnostic = evaluation.Diagnostics.FirstOrDefault(item => item.IsBlocking);
            unavailable = diagnostic is null
                ? evaluation.OutputLimitExceeded ? "The unit preview reached its evaluation output limit."
                    : projection.Diagnostics.Count != 0 ? "The unit preview contains unresolved expanded WML: " + projection.Diagnostics[0].Message
                    : "The unit preview source context could not be fully evaluated."
                : "The unit preview source context could not be fully evaluated: " + diagnostic.Message + Location(diagnostic.Origin);
        }
        else if (selected.Length != 1)
            unavailable = "This physical unit definition could not be matched to one active evaluated unit in the preview context.";
        string? notice = null;
        if (conditions.Count != 0)
            notice = versionConditions && !otherConditions
                ? "This unit uses version preprocessor conditions. The preview evaluates them for " + version + ". The source is unchanged."
                : "This unit uses conditional preprocessor code. " + (libraryContextNotice ??
                    (string.IsNullOrWhiteSpace(context) ? "The preview uses the captured source context." : "The preview uses context " + context + ".") +
                    " Other contexts are not evaluated. The source is unchanged.");
        return new(conditions.Count != 0, false, notice, unavailable);

        void AddRange(WmlStaticSourceRange range, bool definition, List<string>? guards = null)
        {
            if (range.StartOffset < 0 || range.Length <= 0 || range.StartOffset > range.Source.Text.Length - range.Length ||
                ranges.Count >= 512 || !evaluation.Sources.Contains(range.Source)) return;
            if (ranges.Any(existing => existing.Range == range && existing.Definition == definition)) return;
            ranges.Add((range, definition));
            if (!structures.TryGetValue(range.Source, out var structure))
                structures.Add(range.Source, structure = WmlAuthoredStructureScanner.Scan(range.Source.Text));
            if (structure.Problem is not null) return;
            var enclosing = new Stack<WmlAuthoredDirective>();
            foreach (var directive in structure.Directives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = structure.Lines[directive.Line];
                if (line.StartOffset >= range.StartOffset) break;
                if (directive.DefinitionDepth != 0) continue;
                if (WmlAuthoredStructureScanner.IsConditionalStart(directive.Name)) enclosing.Push(directive);
                else if (directive.Name == "endif" && enclosing.Count != 0) enclosing.Pop();
            }
            foreach (var directive in enclosing.Reverse()) AddCondition(structure, directive, guards);
            foreach (var directive in structure.Directives)
            {
                var line = structure.Lines[directive.Line];
                if (line.StartOffset < range.StartOffset || line.StartOffset >= range.StartOffset + range.Length ||
                    directive.DefinitionDepth != (definition ? 1 : 0) || !WmlAuthoredStructureScanner.IsConditionalStart(directive.Name)) continue;
                AddCondition(structure, directive, null);
            }
        }

        void AddCondition(WmlAuthoredStructure structure, WmlAuthoredDirective directive, List<string>? guards)
        {
            var line = structure.Lines[directive.Line];
            var raw = structures.First(pair => ReferenceEquals(pair.Value, structure)).Key.Text[line.StartOffset..line.ContentEndOffset].Trim();
            var argument = raw[(raw.IndexOf('#') + 1 + directive.Name.Length)..].Trim();
            var symbol = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            // Campaign activation is common registration scaffolding. Episode,
            // difficulty, version and other unit-specific guards remain visible.
            if (directive.Name is "ifdef" or "ifndef" && symbol == evaluation.SourceContext?.CampaignDefine) return;
            if (raw.Length > 240) raw = raw[..237] + "...";
            conditions.Add(raw);
            guards?.Add(raw);
            if (directive.Name is "ifver" or "ifnver") versionConditions = true;
            else otherConditions = true;
        }
    }

    private static bool Within(WmlStaticSourceRange inner, WmlStaticSourceRange outer) =>
        ReferenceEquals(inner.Source, outer.Source) && inner.StartOffset >= outer.StartOffset &&
        inner.Length >= 0 && inner.StartOffset <= outer.StartOffset + outer.Length - inner.Length;

    private static bool ExactOwner(WmlStaticScalarOwner owner, WmlStaticSourceRange range) =>
        owner.TagName == "unit_type" && owner.OpeningOrigin is { } opening && owner.ClosingOrigin is { } closing &&
        ReferenceEquals(opening.Source, range.Source) && ReferenceEquals(closing.Source, range.Source) &&
        opening.StartOffset == range.StartOffset && closing.StartOffset + closing.Length == range.StartOffset + range.Length;

    private static bool IsExactPhysicalUnit(WmlStaticSourceRange range) => range.Source.IsAuthored &&
        WmlSingleBlockValidator.ValidateSourceStructure(range.Text).IsValid &&
        WmlBlockScanner.FindTopLevelBlockSpans(range.Source.Text).Any(block => block.TagName == "unit_type" &&
            block.StartIndex == range.StartOffset && block.EndIndexExclusive == range.StartOffset + range.Length);

    private static WmlStaticSourceRange? PhysicalUnitForOpening(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarOwner owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (owner.OpeningOrigin is not { Source.IsAuthored: true } opening ||
            owner.ClosingOrigin is not { } closing || ReferenceEquals(opening.Source, closing.Source) ||
            !evaluation.Sources.Contains(opening.Source)) return null;
        var blocks = WmlBlockScanner.FindTopLevelBlockSpans(opening.Source.Text)
            .Where(block => block.TagName == "unit_type" && block.StartIndex == opening.StartOffset).Take(2).ToArray();
        if (blocks.Length != 1 || !WmlSingleBlockValidator.ValidateSourceStructure(blocks[0].RawText).IsValid) return null;
        return new(opening.Source, blocks[0].StartIndex, blocks[0].EndIndexExclusive - blocks[0].StartIndex);
    }

    private static string Location(WmlStaticSourceRange? origin)
    {
        if (origin is null) return "";
        var line = 1;
        for (var index = 0; index < origin.StartOffset && index < origin.Source.Text.Length; index++)
            if (origin.Source.Text[index] == '\n') line++;
        return " (" + origin.Source.Path + ":" + line + ").";
    }
}
