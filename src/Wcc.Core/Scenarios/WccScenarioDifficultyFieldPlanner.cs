using System.Text;
using System.Diagnostics.CodeAnalysis;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Bounded literal-field authoring for one exact fixed campaign difficulty plan.</summary>
public static partial class WccScenarioDifficultyFieldPlanner
{
    public static WccScenarioDifficultyFieldSnapshot Read(WmlStaticSource mainSource, WmlStaticSource source,
        WmlStaticScalarOwner owner, string key, WmlCampaignEvaluationPlan? plan, WccCampaignAuthoringMetadata? authoring)
        => Read(mainSource, source, owner, key, plan, authoring, null);

    public static WccScenarioDifficultyFieldSnapshot Read(WmlStaticSource mainSource, WmlStaticSource source,
        WmlStaticScalarOwner owner, string key, WmlCampaignEvaluationPlan? plan, WccCampaignAuthoringMetadata? authoring,
        WccScenarioDifficultyFieldInsertionIntent? insertionIntent)
    {
        ArgumentNullException.ThrowIfNull(mainSource); ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(owner); ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var values = new List<WccScenarioDifficultyFieldValue>();
        var mode = WccScenarioDifficultyValueMode.Shared;
        try
        {
            if (key is "id" or "side" or "save_id" || owner.TagName is not ("scenario" or "side" or "leader")
                || !WccScenarioFieldValueCodec.TryGetDefinition(owner.TagName, key, out var field))
                throw new InvalidOperationException("This field has no supported difficulty conversion; identity fields remain fixed.");
            var main = authoring is null ? null : WccCampaignStructurePlanner.Read(mainSource, authoring);
            if (authoring is not null && !main!.CanEdit || plan is null
                || plan.CampaignMainTextHash != mainSource.ContentHash
                || !SamePath(plan.CampaignMainSourcePath, mainSource.Path)
                || string.IsNullOrWhiteSpace(plan.CampaignDefine)
                || plan.Difficulties.Count is < 1 or > 16
                || plan.Difficulties.Select(row => row.Define).Distinct(StringComparer.Ordinal).Count() != plan.Difficulties.Count
                || plan.Difficulties.Any(row => string.IsNullOrWhiteSpace(row.Define) || row.Define.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                || authoring is not null && (plan.CampaignDefine != authoring.CampaignDefine
                    || !main!.DifficultyRows.Select(row => row.Define).SequenceEqual(plan.Difficulties.Select(row => row.Define), StringComparer.Ordinal))
                || plan.Contexts.Count != plan.Difficulties.Count
                || plan.Contexts.Where((context, index) => context.CampaignDefine != plan.CampaignDefine
                    || context.DifficultyDefine != plan.Difficulties[index].Define).Any())
                throw new InvalidOperationException(main is { CanEdit: false } ? main.Diagnostic : "The exact complete fixed campaign difficulty plan is required.");
            var root = Path.GetDirectoryName(mainSource.Path)!;
            var relative = Path.GetRelativePath(root, source.Path);
            if (!source.IsAuthored || Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || SamePath(source.Path, mainSource.Path))
                throw new InvalidOperationException("The scenario source must be authored inside this exact current campaign.");
            RequireLiteralOwner(owner, source);
            if (WccScenarioFieldContentScope.CheckOwner(owner) is { } refusal) throw new InvalidOperationException(refusal);
            var opening = owner.OpeningOrigin!; var closing = owner.ClosingOrigin!;
            var scan = WmlAuthoredStructureScanner.Scan(source.Text);
            if (scan.Problem is not null || !WmlSingleBlockValidator.ValidateSourceStructure(source.Text).IsValid)
                throw new InvalidOperationException(scan.Problem ?? "The literal source structure is incomplete.");
            var directives = WmlEditorAnalysis.Build(source.Text).Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive).ToArray();
            var blocks = Conditions(source, scan);
            if (blocks.Any(block => block.Start < opening.StartOffset && block.End > opening.StartOffset))
                throw new InvalidOperationException("The containing owner is itself conditionally authored; use RAW for its topology.");
            var literal = ProjectLiteral(source, scan).Owners.Where(candidate => SamePhysicalLiteralOwner(owner, candidate)).ToArray();
            if (literal.Length != 1) throw new InvalidOperationException("The owner has no unique direct paired literal source block.");
            var rawOwner = literal[0];
            var groups = ReadGroups(source, scan, directives, blocks, rawOwner, plan);
            if (insertionIntent is not null) ValidateInsertionIntent(insertionIntent, source, owner, key, plan);
            // A macro contract is a display and composition recipe. It does not
            // grant authority over a generated assignment: the fresh reviewer
            // separately matches its real sole contribution in every context.
            if (owner.ResolveField(key).State == WmlStaticScalarResolutionState.Known
                && (!rawOwner.Fields.TryGetValue(key, out var macroRawField)
                    || macroRawField.Contributions.Any(contribution =>
                    {
                        if (!ExactInitialComponentContribution(contribution, key, source, out var assignment, out var value)
                            || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(value.Text)) return true;
                        var statement = WholeStatementLine(source, assignment);
                        return scan.Macros.Any(macro => macro.StartOffset < statement.StartOffset + statement.Length
                            && macro.EndOffsetExclusive > statement.StartOffset);
                    })))
            {
                if (!TryReadMacroValues(source, owner, key, plan, out var contract, out var problem))
                    throw new InvalidOperationException(problem ?? "The field has no proven pure fixed-difficulty macro conversion.");
                if (blocks.Any(block => block.Start < contract.Invocation.StartOffset
                    && block.End > contract.Invocation.StartOffset))
                    throw new InvalidOperationException("The macro field is itself conditionally authored; keep its existing source topology.");
                var macroClosingLine = LineStart(source.Text, closing.StartOffset);
                if (!IsOwnLine(source.Text, closing.StartOffset))
                    throw new InvalidOperationException("The containing closing tag must occupy its own source line.");
                var macroAssignments = contract.Slots.Select(slot => new WccScenarioDifficultyFieldAssignment(
                    slot.Define, contract.Invocation, slot.Argument, contract.StatementRegion, slot.Literal)).ToArray();
                var macroIndentLength = 0;
                while (macroIndentLength < contract.StatementRegion.Length
                    && contract.StatementRegion.Text[macroIndentLength] is ' ' or '\t') macroIndentLength++;
                var macroIndent = contract.StatementRegion.Text[..macroIndentLength];
                var macroValues = contract.Slots.Select(slot => new WccScenarioDifficultyFieldValue(
                    slot.Define, slot.ContextSemanticKey, WccScenarioFieldValueCodec.DecodeLiteral(slot.Literal)));
                return new(mainSource, source, owner, key, plan, authoring,
                    WccScenarioDifficultyValueMode.Independent, macroValues, true,
                    "Replace this proven literal difficulty macro with one shared literal or one literal per fixed difficulty.",
                    new(Array.AsReadOnly(macroAssignments), macroClosingLine, macroIndent,
                        source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", groups, contract));
            }
            var assignments = new List<WccScenarioDifficultyFieldAssignment>();
            if (rawOwner.Fields.TryGetValue(key, out var rawField))
                foreach (var contribution in rawField.Contributions)
                {
                    if (!ExactInitialComponentContribution(contribution, key, source, out var assignment, out var value)
                        || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(value!.Text)
                        || !WccScenarioFieldValueCodec.IsSupportedResolvedValue(field!, value.Text))
                        throw new InvalidOperationException("The field is composite, generated or contains a nonliteral value; use its existing value edit or RAW.");
                    var lineRange = WholeStatementLine(source, assignment!);
                    if (!IsOwnLine(source.Text, assignment!.StartOffset)
                        || scan.Macros.Any(macro => macro.StartOffset < lineRange.StartOffset + lineRange.Length
                            && macro.EndOffsetExclusive > lineRange.StartOffset))
                        throw new InvalidOperationException("The field needs a standalone literal assignment without a macro value.");
                    var containing = blocks.Where(block => block.Start < assignment.StartOffset && block.End > assignment.StartOffset).ToArray();
                    if (containing.Length == 0)
                        assignments.Add(new(null, assignment, value, lineRange, value.Text)
                        { CompositeAssignment = ComponentContract(contribution, key, source) });
                    else if (containing.Length == 1 && containing[0] is { } block && block.Define is not null
                        && block.Start > opening.StartOffset && block.End <= closing.StartOffset
                        && block.DirectiveCount == 2 && !block.HasElse && groups.Any(group => group.Region.StartOffset == block.Start
                            && group.Region.Length == block.End - block.Start && group.Members.Any(member => member.Key == key
                                && SameRange(member.Assignment, assignment))))
                        assignments.Add(new(block.Define, assignment, value, new(source, block.Start, block.End - block.Start), value.Text)
                        { CompositeAssignment = ComponentContract(contribution, key, source) });
                    else throw new InvalidOperationException("The field uses mixed, nested or shared conditional code; its existing source remains editable without changing topology.");
                }
            foreach (var composite in assignments.Select(item => item.CompositeAssignment).OfType<WccScenarioCompositeAssignment>()
                         .DistinctBy(item => (item.Assignment.StartOffset, item.Assignment.Length)))
                RequireLiteralComposite(rawOwner, composite);
            if (assignments.Count > 0 && assignments.All(item => item.Define is not null))
            {
                mode = WccScenarioDifficultyValueMode.Independent;
                var byDefine = assignments.Select(item => item.Define!).ToHashSet(StringComparer.Ordinal);
                if (assignments.Count != plan.Difficulties.Count || byDefine.Count != assignments.Count
                    || !byDefine.SetEquals(plan.Difficulties.Select(row => row.Define))
                    || assignments.Select(item => (item.Region.StartOffset, item.Region.Length)).Distinct().Count() != assignments.Count)
                    throw new InvalidOperationException("Keep one unique literal #ifdef assignment for every fixed difficulty.");
                // Menu order determines the value and encoding slots. The source
                // branches may use another order, but each exact define must map
                // to one and only one original assignment.
                var assignmentByDefine = assignments.ToDictionary(item => item.Define!, StringComparer.Ordinal);
                assignments = plan.Difficulties.Select(row => assignmentByDefine[row.Define]).ToList();
            }
            else if (assignments.Count > 1 || assignments.Any(item => item.Define is not null))
                throw new InvalidOperationException("The field has repeated or mixed source assignments; use its existing value edit or RAW.");
            if (insertionIntent is not null)
            {
                if (assignments.Count != 0)
                    throw new InvalidOperationException("The pending missing-field insertion now has an authored assignment; reload or reset it.");
            }
            else if (assignments.Count == 0 && authoring is null)
                throw new InvalidOperationException("Set an explicit literal value before changing a missing imported field's difficulty dependence.");
            if (assignments.Any(item => item.Literal.TrimStart().StartsWith('_') && item.Literal.TrimStart()[1..].TrimStart().StartsWith('"')))
            {
                var domains = assignments.Select(item => WmlTextDomainCapture.BeforeOffset(source.Text, item.Assignment.StartOffset)).ToArray();
                if (domains.Any(string.IsNullOrWhiteSpace) || domains.Distinct(StringComparer.Ordinal).Count() != 1)
                    throw new InvalidOperationException("The translated difficulty branches have different or unproven text domains; keep their existing source layout.");
            }
            var selected = owner.ResolveField(key);
            if (selected.State == WmlStaticScalarResolutionState.Unresolved
                || selected.State == WmlStaticScalarResolutionState.Known && (!owner.Fields.TryGetValue(key, out var captured)
                    || captured.Contributions.Count != 1 || !ExactInitialComponentContribution(captured.Contributions[0], key, source, out var current, out _)
                    || !assignments.Any(item => SameRange(item.Assignment, current!))))
                throw new InvalidOperationException("The captured field is not one exact direct local literal assignment or proven absence.");
            for (var index = 0; index < plan.Difficulties.Count; index++)
            {
                var assignment = mode == WccScenarioDifficultyValueMode.Independent ? assignments[index] : assignments.FirstOrDefault();
                values.Add(new(plan.Difficulties[index].Define, plan.Contexts[index].SemanticKey,
                    assignment is null ? null : WccScenarioFieldValueCodec.DecodeLiteral(assignment.Literal)));
            }
            var closingLine = LineStart(source.Text, closing.StartOffset);
            if (!IsOwnLine(source.Text, closing.StartOffset)) throw new InvalidOperationException("The containing closing tag must occupy its own source line.");
            if (assignments.Count == 0 && field!.Kind == ScenarioInitialFieldKind.TranslatableText
                && string.IsNullOrWhiteSpace(WmlTextDomainCapture.BeforeOffset(source.Text, closingLine)))
                throw new InvalidOperationException("The new translated field has no proven local text domain.");
            var indent = assignments.FirstOrDefault() is { } first
                ? source.Text[LineStart(source.Text, first.Assignment.StartOffset)..first.Assignment.StartOffset]
                : source.Text[closingLine..closing.StartOffset] + "    ";
            return new(mainSource, source, owner, key, plan, authoring, mode, values, true,
                "Change this direct field between one shared literal and one literal per fixed difficulty.",
                new(Array.AsReadOnly(assignments.ToArray()), closingLine, indent,
                    source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", groups), insertionIntent);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return new(mainSource, source, owner, key, plan, authoring, mode, values, false, exception.Message, null);
        }
    }

    public static WccScenarioDifficultyFieldEdit BuildEdit(WccScenarioDifficultyFieldSnapshot snapshot,
        WccScenarioDifficultyValueMode targetMode, string selectedContextSemanticKey, string selectedValue,
        IEnumerable<WccScenarioDifficultyFieldValue>? values = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(selectedValue);
        if (!Enum.IsDefined(targetMode)) throw new InvalidOperationException("The requested difficulty mode is unsupported.");
        var fresh = Read(snapshot.MainSource, snapshot.Source, snapshot.Owner, snapshot.Key, snapshot.Plan, snapshot.Authoring,
            snapshot.InsertionIntent);
        if (!snapshot.CanEdit || !Equivalent(snapshot, fresh)) throw new InvalidOperationException(fresh.Diagnostic);
        var selected = snapshot.Values.Select((value, index) => (value, index))
            .Where(item => item.value.ContextSemanticKey == selectedContextSemanticKey).ToArray();
        if (selected.Length != 1) throw new InvalidOperationException("Select one exact declared gameplay context.");
        WccScenarioFieldValueCodec.TryGetDefinition(snapshot.Owner.TagName, snapshot.Key, out var definition);
        if (WccScenarioFieldValueCodec.ValidateInput(definition!, selectedValue) is { } refusal)
            throw new InvalidOperationException(refusal);
        var final = targetMode == WccScenarioDifficultyValueMode.Shared || values is null
            ? snapshot.Values.Select(value => value with { ValueText = selectedValue }).ToArray() : values.ToArray();
        if (final.Length != snapshot.Values.Count || final.Where((value, index) => value is null
                || value.Define != snapshot.Values[index].Define
                || value.ContextSemanticKey != snapshot.Values[index].ContextSemanticKey).Any())
            throw new InvalidOperationException("Final literal values must follow every fixed difficulty and captured context in order.");
        var layout = snapshot.Layout!;
        var selectedSource = layout.Assignments.Count == 0 ? null
            : layout.Assignments[snapshot.Mode == WccScenarioDifficultyValueMode.Shared ? 0 : selected[0].index].Literal;
        var encoded = new List<string>();
        for (var index = 0; index < final.Length; index++)
        {
            if (final[index].ValueText is not { } text || WccScenarioFieldValueCodec.ValidateInput(definition!, text) is not null)
                throw new InvalidOperationException("Every difficulty needs one supported explicit literal value.");
            var existing = targetMode == WccScenarioDifficultyValueMode.Independent
                && snapshot.Mode == WccScenarioDifficultyValueMode.Independent ? layout.Assignments[index].Literal : selectedSource;
            encoded.Add(WccScenarioFieldValueCodec.EncodeLiteral(definition!, text, existing));
        }
        return new(snapshot, targetMode, selectedContextSemanticKey, selectedValue, final, encoded);
    }

    internal static IReadOnlyList<WccScenarioFieldSourcePatch> PlanPatches(WccScenarioDifficultyFieldEdit edit)
    {
        return PlanGroupedPatches([edit], null).Patches;
    }

    internal static bool Equivalent(WccScenarioDifficultyFieldSnapshot left, WccScenarioDifficultyFieldSnapshot right) =>
        left.CanEdit && right.CanEdit && SameOwner(left.Owner, right.Owner) && left.Key == right.Key && left.Mode == right.Mode
        && SameSource(left.MainSource, right.MainSource) && SameSource(left.Source, right.Source)
        && left.Values.SequenceEqual(right.Values) && SameDifficultyAssignments(left.Layout!.Assignments, right.Layout!.Assignments)
        && SameGroups(left.Layout.Groups, right.Layout.Groups)
        && SameMacroContract(left.Layout.MacroContract, right.Layout.MacroContract)
        && SameInsertionIntent(left.InsertionIntent, right.InsertionIntent)
        && left.Layout.InsertionOffset == right.Layout.InsertionOffset && left.Layout.Indent == right.Layout.Indent;

    internal static bool SameOwner(WmlStaticScalarOwner left, WmlStaticScalarOwner right)
    {
        for (WmlStaticScalarOwner? a = left, b = right; a is not null || b is not null; a = a?.Parent, b = b?.Parent)
            if (a is null || b is null || a.TagName != b.TagName || a.IsAmended != b.IsAmended
                || a.OpeningOrigin is not { } opening || b.OpeningOrigin is not { } otherOpening
                || !SameRange(opening, otherOpening) || a.ClosingOrigin is not { } closing
                || b.ClosingOrigin is not { } otherClosing || !SameRange(closing, otherClosing)
                || !SameLiteralExpansionFrames(a.OpeningExpansionStack, b.OpeningExpansionStack)
                || !SameLiteralExpansionFrames(a.ClosingExpansionStack, b.ClosingExpansionStack)) return false;
        return true;
    }

    private static bool SamePhysicalLiteralOwner(WmlStaticScalarOwner left, WmlStaticScalarOwner right)
    {
        for (WmlStaticScalarOwner? a = left, b = right; a is not null || b is not null; a = a?.Parent, b = b?.Parent)
        {
            if (a is null || b is null || a.TagName != b.TagName
                || a.OpeningOrigin is not { } ao || b.OpeningOrigin is not { } bo || !SameRange(ao, bo)) return false;
            // A paired child belongs to the same physical scenario opening even
            // when a helper closes and reopens that scenario. Its lexical parent
            // supplies containment, not a replacement captured closing anchor.
            if (!ReferenceEquals(a, left) && a.TagName == "scenario" && a.Parent is null && b.Parent is null
                && (a.IsAmended || b.IsAmended)) continue;
            if (a.IsAmended || b.IsAmended || a.ClosingOrigin is not { } ac
                || b.ClosingOrigin is not { } bc || !SameRange(ac, bc)) return false;
        }
        return true;
    }

    internal static void RequireLiteralOwner(WmlStaticScalarOwner owner, WmlStaticSource source)
    {
        var ancestors = new List<WmlStaticScalarOwner>();
        for (WmlStaticScalarOwner? current = owner; current is not null; current = current.Parent) ancestors.Add(current);
        if (ancestors[^1].TagName != "scenario" || ancestors[^1].Parent is not null
            || owner.TagName == "side" && owner.Parent?.TagName != "scenario"
            || owner.TagName == "leader" && (owner.Parent?.TagName != "side" || owner.Parent?.Parent?.TagName != "scenario")
            || ancestors.Any(current => current.OpeningOutputOffset is null || current.ClosingOutputOffset is null
                || current.OpeningOrigin is not { } opening || current.ClosingOrigin is not { } closing
                || !SameSource(opening.Source, source)
                || !OutsideLiteralExpansionDefinitions(opening, current.OpeningExpansionStack)
                || opening.Text != "[" + current.TagName + "]"
                || !(current.TagName == "scenario" && current.Parent is null && !ReferenceEquals(current, owner) && current.IsAmended)
                    && (current.IsAmended || !SameSource(closing.Source, source)
                        || !OutsideLiteralExpansionDefinitions(closing, current.ClosingExpansionStack)
                        || !SameLiteralExpansionFrames(current.OpeningExpansionStack, current.ClosingExpansionStack)
                        || closing.Text != "[/" + current.TagName + "]")))
            throw new InvalidOperationException("The field needs one direct unamended literal initial owner and exact scenario ancestry.");
        if (ancestors[^1].IsAmended)
        {
            // Do not merely omit the scenario ancestor check: prove that the
            // paired target and each physical parent sit beneath this exact
            // literal scenario opening in the original authored source too.
            var scan = WmlAuthoredStructureScanner.Scan(source.Text);
            if (scan.Problem is not null || !WmlSingleBlockValidator.ValidateSourceStructure(source.Text).IsValid
                || ProjectLiteral(source, scan).Owners.Count(candidate => SamePhysicalLiteralOwner(owner, candidate)) != 1)
                throw new InvalidOperationException("The paired field owner has no unique literal containment under its captured scenario opening.");
        }
    }

    internal static bool ExactLocalContribution(WmlStaticScalarContribution contribution, WmlStaticSource source,
        [NotNullWhen(true)] out WmlStaticSourceRange? assignment, [NotNullWhen(true)] out WmlStaticSourceRange? value)
    {
        assignment = contribution.AssignmentOrigin; value = contribution.ValueOrigins.Count == 1 ? contribution.ValueOrigins[0]
            : contribution.ValueOutputLength == 0 && assignment is not null ? new(source, assignment.StartOffset + assignment.Length, 0) : null;
        return assignment is not null && value is not null && contribution.AssignmentOrigins.Count == 1
            && SameRange(assignment, contribution.AssignmentOrigins[0])
            && OutsideLiteralExpansionDefinitions(assignment, contribution.ExpansionStack)
            && OutsideLiteralExpansionDefinitions(value, contribution.ValueExpansionStack)
            && SameLiteralExpansionFrames(contribution.ExpansionStack, contribution.ValueExpansionStack)
            && contribution.CompositeAssignmentKeys.Count == 0
            && contribution.ValueMappingIsExact && assignment.Length == contribution.AssignmentOutputLength
            && value.Length == contribution.ValueOutputLength && SameSource(assignment.Source, source) && SameSource(value.Source, source)
            && value.StartOffset >= assignment.StartOffset && value.StartOffset + value.Length <= assignment.StartOffset + assignment.Length;
    }

    private static bool OutsideLiteralExpansionDefinitions(WmlStaticSourceRange origin,
        IReadOnlyList<WmlStaticExpansionFrame> frames) => frames.All(frame => !SameSource(origin.Source, frame.Definition.Source)
            || origin.StartOffset < frame.Definition.StartOffset
            || origin.StartOffset + origin.Length > frame.Definition.StartOffset + frame.Definition.Length);

    private static bool SameLiteralExpansionFrames(IReadOnlyList<WmlStaticExpansionFrame> left,
        IReadOnlyList<WmlStaticExpansionFrame> right) => left.Count == right.Count
        && left.Where((frame, index) => frame.MacroName != right[index].MacroName
            || !SameRange(frame.Definition, right[index].Definition)
            || frame.InvocationOrigins.Count != right[index].InvocationOrigins.Count
            || frame.InvocationOrigins.Where((origin, slot) => !SameRange(origin, right[index].InvocationOrigins[slot])).Any()
            || !SameParameters(frame.RequiredParameters, right[index].RequiredParameters)
            || !SameParameters(frame.OptionalParameters, right[index].OptionalParameters)).Any() == false;

    private static bool SameParameters(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    internal static bool SameRange(WmlStaticSourceRange left, WmlStaticSourceRange right) =>
        SameSource(left.Source, right.Source) && left.StartOffset == right.StartOffset && left.Length == right.Length;
    internal static bool SameSource(WmlStaticSource left, WmlStaticSource right) => left.IsAuthored && right.IsAuthored
        && SamePath(left.Path, right.Path) && left.ContentHash == right.ContentHash && left.Text == right.Text;
    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static WmlStaticScalarProjection ProjectLiteral(WmlStaticSource source, WmlAuthoredStructure scan)
    {
        var text = source.Text.ToCharArray();
        foreach (var macro in scan.Macros) Blank(macro.StartOffset, macro.EndOffsetExclusive);
        // The authored structure scan keeps quoted values opaque. Include their
        // preprocessor references too, or a quoted map include invalidates this
        // lexical owner graph. The shared editor analysis leaves raw Lua opaque;
        // actual expansion and source-edit authority still use the captured evaluation.
        foreach (var reference in WmlEditorAnalysis.Build(source.Text).References)
            Blank(reference.StartOffset, reference.StartOffset + reference.Length);
        var definitionStart = -1;
        foreach (var directive in scan.Directives)
        {
            var line = scan.Lines[directive.Line];
            if (directive.Name == "define" && directive.DefinitionDepth == 0) definitionStart = line.StartOffset;
            if (directive.Name == "enddef" && directive.DefinitionDepth == 1 && definitionStart >= 0)
            { Blank(definitionStart, line.EndOffsetExclusive); definitionStart = -1; }
            Blank(line.StartOffset, line.ContentEndOffset);
        }
        var raw = new string(text);
        return WmlStaticScalarProjection.Create(new(raw, [new(0, raw, new(source, 0, source.Text.Length), [])], [], [source], [], true));
        void Blank(int start, int end) { for (var index = start; index < end; index++) if (text[index] is not ('\r' or '\n')) text[index] = ' '; }
    }

    private sealed class Condition(int start, int bodyStart, string? define)
    {
        internal int Start { get; } = start;
        internal int BodyStart { get; } = bodyStart;
        internal string? Define { get; } = define;
        internal int End { get; set; }
        internal int BodyEnd { get; set; }
        internal int DirectiveCount { get; set; } = 1;
        internal bool HasElse { get; set; }
    }
    private static IReadOnlyList<Condition> Conditions(WmlStaticSource source, WmlAuthoredStructure scan)
    {
        var result = new List<Condition>(); var stack = new Stack<Condition>();
        foreach (var directive in scan.Directives.Where(item => item.DefinitionDepth == 0 && item.Name is not ("define" or "enddef")))
        {
            var line = scan.Lines[directive.Line];
            foreach (var condition in stack) condition.DirectiveCount++;
            if (WmlAuthoredStructureScanner.IsConditionalStart(directive.Name))
            {
                var code = source.Text[line.StartOffset..line.ContentEndOffset].Trim();
                var define = directive.Name == "ifdef" && code.StartsWith("#ifdef ", StringComparison.Ordinal) ? code[7..].Trim() : null;
                if (define is not null && (define.Length == 0 || define.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))) define = null;
                var condition = new Condition(line.StartOffset, line.EndOffsetExclusive, define);
                result.Add(condition); stack.Push(condition);
            }
            else if (directive.Name == "else") { if (stack.Count == 0) throw new InvalidOperationException("An #else has no exact condition."); stack.Peek().HasElse = true; }
            else if (directive.Name == "endif")
            {
                if (stack.Count == 0) throw new InvalidOperationException("An #endif has no exact condition.");
                var condition = stack.Pop(); condition.BodyEnd = line.StartOffset; condition.End = line.EndOffsetExclusive;
            }
        }
        if (stack.Count != 0) throw new InvalidOperationException("An authored conditional has no closing #endif.");
        return result;
    }
    private static bool FieldOnly(WmlAuthoredStructure scan, IReadOnlyList<WmlEditorToken> directives, Condition block, WmlStaticSourceRange assignment)
    {
        if (assignment.StartOffset < block.BodyStart || assignment.StartOffset + assignment.Length > block.BodyEnd
            || scan.Macros.Any(macro => macro.StartOffset < block.BodyEnd && macro.EndOffsetExclusive > block.BodyStart)
            || directives.Any(token => token.StartOffset >= block.BodyStart && token.StartOffset < block.BodyEnd)) return false;
        var body = scan.MaskedText[block.BodyStart..block.BodyEnd].ToCharArray();
        Array.Fill(body, ' ', assignment.StartOffset - block.BodyStart, assignment.Length);
        return body.All(char.IsWhiteSpace);
    }
    private static WmlStaticSourceRange WholeStatementLine(WmlStaticSource source, WmlStaticSourceRange assignment)
    {
        var end = assignment.StartOffset + assignment.Length;
        while (end < source.Text.Length && source.Text[end] is not ('\r' or '\n')) end++;
        if (end < source.Text.Length && source.Text[end] == '\r') end++;
        if (end < source.Text.Length && source.Text[end] == '\n') end++;
        var start = LineStart(source.Text, assignment.StartOffset);
        return new(source, start, end - start);
    }
    private static string Comments(WccScenarioDifficultyFieldAssignment assignment, string newline, string indent)
    {
        var region = assignment.Region; var raw = region.Text.ToCharArray();
        var start = assignment.Assignment.StartOffset - region.StartOffset;
        for (var index = start; index < start + assignment.Assignment.Length; index++) if (raw[index] is not ('\r' or '\n')) raw[index] = ' ';
        var text = new string(raw); var scan = WmlAuthoredStructureScanner.Scan(text); var builder = new StringBuilder();
        var directives = scan.Directives.Where(item => item.Name is "ifdef" or "endif").Select(item => item.Line).ToHashSet();
        for (var index = 0; index < scan.Lines.Count; index++)
        {
            var line = scan.Lines[index]; var comment = text[line.StartOffset..line.ContentEndOffset];
            var wrapperComment = false;
            if (directives.Contains(index))
            {
                var hash = comment.IndexOf('#');
                var trailing = hash < 0 ? -1 : comment.IndexOf('#', hash + 1);
                if (trailing < 0) continue;
                comment = comment[trailing..]; wrapperComment = true;
            }
            if (comment.TrimStart().StartsWith('#'))
            {
                var inline = wrapperComment || line.StartOffset < start + assignment.Assignment.Length && line.ContentEndOffset > start;
                var payload = inline ? indent + comment.TrimStart() : comment;
                // Moving a comment from behind an assignment must not turn a
                // directive-shaped comment into executable preprocessor code.
                if (inline && WmlEditorAnalysis.Build(payload).Tokens.Any(token => token.Kind == WmlEditorTokenKind.Directive))
                    payload = indent + "# " + comment.TrimStart();
                builder.Append(payload).Append(newline);
            }
        }
        return builder.ToString();
    }
    private static bool IsOwnLine(string text, int offset) => text.AsSpan(LineStart(text, offset), offset - LineStart(text, offset)).Trim().IsEmpty;
    private static int LineStart(string text, int offset) { while (offset > 0 && text[offset - 1] is not ('\r' or '\n')) offset--; return offset; }
}
