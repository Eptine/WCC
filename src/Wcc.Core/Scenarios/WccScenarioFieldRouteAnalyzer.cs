using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Generic exact-source field routing. It never joins owners by side number or display value.</summary>
public static class WccScenarioFieldRouteAnalyzer
{
    public static WccScenarioFieldRoute Analyze(WmlStaticScalarOwner owner, string key,
        WmlStaticSource editableSource, WmlEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(editableSource);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!key.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            throw new ArgumentException("A direct WML property key is required.", nameof(key));
        var resolution = owner.ResolveField(key);
        var route = new WccScenarioFieldRoute
        {
            Owner = owner, Key = key, Source = editableSource, ContextSemanticKey = context.SemanticKey,
            Value = resolution.State == WmlStaticScalarResolutionState.Known ? resolution.ValueText : null,
            SourceValue = owner.Fields.TryGetValue(key, out var capturedField)
                && capturedField.Contributions.LastOrDefault() is { ValueOrigins.Count: 1 } capturedContribution
                ? capturedContribution.ValueOrigins[0].Text : resolution.ValueText,
            Origin = resolution.State == WmlStaticScalarResolutionState.ProvenAbsent ? WccScenarioFieldOrigin.Absent : WccScenarioFieldOrigin.Unresolved,
            Kind = WccScenarioFieldRouteKind.RawOnly
        };
        var supportedField = WccScenarioFieldValueCodec.TryGetDefinition(owner.TagName, key, out _);
        if (!editableSource.IsAuthored || owner.OpeningOutputOffset is null || owner.OpeningOrigin is null)
            return route with { Diagnostic = "The current owner has no exact captured opening; use authored RAW source." };
        if (WccScenarioFieldContentScope.CheckOwner(owner) is { } contentRefusal)
            return route with { Diagnostic = contentRefusal };
        if (resolution.State == WmlStaticScalarResolutionState.Unresolved)
            return route with { Diagnostic = "This field is unresolved in the selected context; an empty display does not prove absence." };
        var literal = IsLiteralOwner(owner, editableSource);
        if (resolution.State == WmlStaticScalarResolutionState.ProvenAbsent)
        {
            if (!supportedField) return route with { Diagnostic = "This absent property has no supported initial-field edit; no displayed default creates an authored assignment." };
            return literal && CanAppend(owner, editableSource)
                ? route with { Kind = WccScenarioFieldRouteKind.LocalAssignment, CanInsert = true,
                    Diagnostic = "Insert the missing property inside this exact authored tag." }
                : route with { Diagnostic = "The value is absent, but no exact scenario-local containing tag is available; use authored RAW source." };
        }

        var field = owner.Fields[key];
        var contribution = field.WinningContribution;
        if (contribution is null)
            return route with { Diagnostic = "The winning assignment has no captured source contribution; use authored RAW source." };
        var macroValue = IsMacroContribution(contribution);
        route = route with { Origin = macroValue ? WccScenarioFieldOrigin.Macro : WccScenarioFieldOrigin.Scenario,
            SourceValue = contribution.ValueOrigins.Count == 1 ? contribution.ValueOrigins[0].Text : resolution.ValueText };
        var localFrames = contribution.ExpansionStack.Where(frame =>
            LocalInvocation(frame, editableSource) is { } invocation &&
            OutsideDefinitions(invocation, contribution.ExpansionStack)).ToArray();
        route = route with { Invocation = localFrames.LastOrDefault() is { } nearest ? LocalInvocation(nearest, editableSource) : null };
        if (!supportedField)
            return route with { Diagnostic = "This property has no supported initial-field edit; its authored value remains available in RAW source." };
        if (contribution.CompositeAssignmentKeys.Count != 0 &&
            !(owner.TagName is "side" or "leader" or "image" &&
              contribution.CompositeAssignmentKeys.Count == 2 &&
              contribution.CompositeAssignmentKeys.All(component => component is "x" or "y")))
            return route with { Diagnostic = "This multiple assignment is available for inspection; edit its authored RAW source." };

        var exactDirect = contribution.ValueOrigins.Count == 1 ? contribution.ValueOrigins[0]
            : contribution.ValueOutputLength == 0 && contribution.AssignmentOrigin is { } emptyAssignment
                ? new WmlStaticSourceRange(editableSource, emptyAssignment.StartOffset + emptyAssignment.Length, 0) : null;
        if (contribution.ValueMappingIsExact && contribution.AssignmentOrigin is { } assignment && exactDirect is { } direct &&
            SameSource(assignment.Source, editableSource) && OutsideDefinitions(assignment, contribution.ExpansionStack) &&
            SameSource(direct.Source, editableSource) && direct.Length == contribution.ValueOutputLength && direct.Text == route.Value)
        {
            var overridesMacro = field.Contributions.Take(field.Contributions.Count - 1).Any(IsMacroContribution) ||
                !literal;
            return route with
            {
                Origin = overridesMacro ? WccScenarioFieldOrigin.Override : WccScenarioFieldOrigin.Scenario,
                Kind = WccScenarioFieldRouteKind.DirectToken, Target = direct, Assignment = assignment,
                SourceValue = direct.Text, CanSet = true,
                CanRemove = contribution.CompositeAssignmentKeys.Count == 0 && IsStandaloneAssignment(assignment),
                Diagnostic = overridesMacro ? "Edit or remove this winning scenario-local assignment; earlier contributions remain unchanged."
                    : "Edit this exact scenario source token."
            };
        }

        // A scalar helper may select any required positional slot. Its exact mapped value decides the
        // slot, including equal-valued branches; macro names and difficulty spellings do not participate.
        if (contribution.ValueMappingIsExact && contribution.ValueOrigins.Count > 0)
        {
            var mappedValue = contribution.ValueOrigins[0];
            foreach (var frame in localFrames.Reverse())
            {
                var invocation = LocalInvocation(frame, editableSource)!;
                if (frame.RequiredParameters is null || !TryInvocation(frame, invocation, out var syntax)) continue;
                // A complete assignment can also come through nested wrappers
                // with a substituted property name. Exact winning value flow to
                // the original required argument below is the proof; authored
                // inline key= syntax is not required for initial scalar fields.
                var scalar = IsScalarInvocation(contribution, key, invocation) ||
                    IsSupportedInitialScalar(owner.TagName, key) &&
                    WccScenarioFieldValueCodec.IsSupportedLiteralWml(route.Value!);
                var directRequired = TryDirectPlaceholder(contribution, key, frame, out var requiredName);
                if (!scalar && !directRequired) continue;
                for (var index = 0; index < Math.Min(frame.RequiredParameters.Count, syntax.Arguments.Count); index++)
                {
                    var argument = syntax.Arguments[index];
                    var value = new WmlStaticSourceRange(editableSource,
                        invocation.StartOffset + argument.Span.StartOffset, argument.Span.Length);
                    var exactLiteral = contribution.ValueOrigins.Count == 1 && SameSource(mappedValue.Source, editableSource)
                        && mappedValue.StartOffset == value.StartOffset && mappedValue.Length == value.Length && mappedValue.Text == route.Value;
                    if (!exactLiteral && !(directRequired && requiredName == frame.RequiredParameters[index]
                          && WccScenarioFieldValueCodec.ValidateExpression(value.Text) is null
                          && ValueHasArgumentAncestry(contribution, value))) continue;
                    if (!scalar)
                    {
                        if (requiredName != frame.RequiredParameters[index]) continue;
                        return route with { Origin = WccScenarioFieldOrigin.Scenario, Target = value,
                            Invocation = invocation, Definition = frame.Definition, ParameterName = requiredName,
                            ArgumentIndex = index,
                            Diagnostic = "This required instance argument is resolved, but its producer field has no supported structured edit route yet. Use authored RAW source." };
                    }
                    route = route with
                    {
                        Origin = field.Contributions.Take(field.Contributions.Count - 1).Any(IsMacroContribution)
                            ? WccScenarioFieldOrigin.Override : WccScenarioFieldOrigin.Scenario,
                        Kind = WccScenarioFieldRouteKind.ScalarArgument,
                        Target = value, Invocation = invocation, Definition = frame.Definition,
                        SourceValue = value.Text,
                        ParameterName = frame.RequiredParameters[index], ArgumentIndex = index,
                        CanSet = true, Diagnostic = "Edit the selected scalar argument in this invocation; other arguments and the shared definition remain unchanged."
                    };
                    return route;
                }
            }
        }

        // Only a direct declared optional placeholder establishes the parameter-to-field route.
        // A whole-side producer must be one unambiguous generated child of its exact parent.
        foreach (var frame in localFrames.Reverse())
        {
            var invocation = LocalInvocation(frame, editableSource)!;
            if (frame.OptionalParameters is null || frame.RequiredParameters is null ||
                !TryInvocation(frame, invocation, out var syntax) ||
                !TryDirectPlaceholder(contribution, key, frame, out var parameter) ||
                !frame.OptionalParameters.Contains(parameter, StringComparer.Ordinal)) continue;
            if (!literal && !SingleProducer(owner, frame))
                return route with { Invocation = invocation, Definition = frame.Definition,
                    Diagnostic = "This invocation has nested or multiple generated owners; the local override target is ambiguous. Use authored RAW source." };
            var supplied = syntax.Arguments.Skip(frame.RequiredParameters.Count)
                .Select((argument, index) => (argument, index: index + frame.RequiredParameters.Count))
                .Where(item => item.argument.RawText.StartsWith(parameter + "=", StringComparison.Ordinal)).ToArray();
            if (supplied.Length > 1) continue;
            WmlStaticSourceRange? target = null;
            int? argumentIndex = null;
            if (supplied.Length == 1)
            {
                var item = supplied[0];
                var start = invocation.StartOffset + item.argument.Span.StartOffset + parameter.Length + 1;
                target = new(editableSource, start, item.argument.Span.Length - parameter.Length - 1);
                // Existing expressions retain their complete authored argument; paired review proves
                // the winning scalar and every other consumer rather than flattening the expression.
                if (!WccScenarioFieldValueCodec.IsSupportedLiteralWml(target.Text)
                    && WccScenarioFieldValueCodec.ValidateExpression(target.Text) is not null)
                    return route with { Origin = WccScenarioFieldOrigin.Override, Invocation = invocation,
                        Definition = frame.Definition, ParameterName = parameter, IsOptionalArgumentSupplied = true,
                        SourceValue = target.Text,
                        Diagnostic = "The winning optional argument contains a nested expression; use authored RAW source." };
                argumentIndex = item.index;
            }
            return route with
            {
                Origin = target is null ? WccScenarioFieldOrigin.Macro : WccScenarioFieldOrigin.Override,
                Kind = WccScenarioFieldRouteKind.OptionalArgument, Target = target,
                SourceValue = target?.Text ?? route.SourceValue,
                Invocation = invocation, Definition = frame.Definition, ParameterName = parameter,
                ArgumentIndex = argumentIndex, IsOptionalArgumentSupplied = target is not null,
                CanSet = true, CanInsert = target is null, CanRemove = target is not null,
                Diagnostic = "Edit this declared optional argument on this invocation. Its other consumers may change too; the shared macro remains unchanged."
            };
        }
        if (literal && TryAuthoredScalarAssignment(contribution, key, editableSource, out var authoredAssignment, out var authoredValue))
            return route with { Kind = WccScenarioFieldRouteKind.DirectToken, Target = authoredValue,
                Assignment = authoredAssignment, SourceValue = authoredValue.Text, CanSet = true,
                CanRemove = IsStandaloneAssignment(authoredAssignment),
                Diagnostic = "Edit this exact authored scalar expression; its expanded output is checked in every context." };
        if (literal && macroValue && CanAppend(owner, editableSource))
            return route with { Kind = WccScenarioFieldRouteKind.LocalAssignment, CanSet = true, CanInsert = true,
                Diagnostic = "Add a winning local assignment inside this authored tag; the shared macro remains unchanged." };
        return route with { Diagnostic = macroValue
            ? "The value comes from a shared macro, which exposes no verified local parameter for this field. Editing is blocked; use authored RAW source."
            : "The winning value has no exact local token or argument mapping. Editing is blocked; use authored RAW source." };
    }

    internal static bool SameSource(WmlStaticSource left, WmlStaticSource right) => left.IsAuthored && right.IsAuthored &&
        string.Equals(left.Path, right.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        left.ContentHash == right.ContentHash;
    private static bool Inside(WmlStaticSourceRange range, WmlStaticSourceRange container) =>
        range.Source.ContentHash == container.Source.ContentHash &&
        string.Equals(range.Source.Path, container.Source.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        range.StartOffset >= container.StartOffset &&
        range.StartOffset + range.Length <= container.StartOffset + container.Length;
    private static bool OutsideDefinitions(WmlStaticSourceRange range, IReadOnlyList<WmlStaticExpansionFrame> frames) =>
        frames.All(frame => !Inside(range, frame.Definition));
    private static bool IsMacroContribution(WmlStaticScalarContribution contribution) =>
        contribution.AssignmentOrigins.Any(origin => contribution.ExpansionStack.Any(frame => Inside(origin, frame.Definition)));
    private static bool IsLiteralOwner(WmlStaticScalarOwner owner, WmlStaticSource source) =>
        owner.OpeningOrigin is { } opening && SameSource(opening.Source, source) &&
        OutsideDefinitions(opening, owner.OpeningExpansionStack);
    private static bool CanAppend(WmlStaticScalarOwner owner, WmlStaticSource source) => !owner.IsAmended &&
        owner.ClosingOutputOffset.HasValue && owner.ClosingOrigin is { } closing && SameSource(closing.Source, source) &&
        OutsideDefinitions(closing, owner.ClosingExpansionStack) && IsOwnLine(closing);
    private static bool IsOwnLine(WmlStaticSourceRange range)
    {
        var line = range.Source.Text.LastIndexOf('\n', Math.Max(0, range.StartOffset - 1));
        return range.Source.Text.AsSpan(line + 1, range.StartOffset - line - 1).Trim().IsEmpty;
    }
    private static bool IsStandaloneAssignment(WmlStaticSourceRange assignment) => IsOwnLine(assignment);
    private static WmlStaticSourceRange? LocalInvocation(WmlStaticExpansionFrame frame, WmlStaticSource source) =>
        frame.InvocationOrigins.Count == 1 && frame.InvocationOrigins[0] is { } invocation &&
        SameSource(invocation.Source, source) && !Inside(invocation, frame.Definition) ? invocation : null;
    private static bool TryInvocation(WmlStaticExpansionFrame frame, WmlStaticSourceRange range, out WmlMacroInvocationSyntax syntax)
    {
        syntax = null!;
        if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(range.Text, out var parsed) || parsed!.Name != frame.MacroName)
            return false;
        syntax = parsed;
        return true;
    }
    private static bool IsSupportedInitialScalar(string tag, string key) =>
        WccScenarioFieldValueCodec.TryGetDefinition(tag, key, out _);

    private static bool ValueHasArgumentAncestry(WmlStaticScalarContribution contribution, WmlStaticSourceRange argument) =>
        contribution.ValueOrigins.Count != 0 && contribution.ValueOrigins.All(origin => Inside(origin, argument)
            || contribution.ValueExpansionStack.Any(frame => Inside(origin, frame.Definition)
                && frame.InvocationOrigins.Any(invocation => Inside(invocation, argument))));

    private static bool TryAuthoredScalarAssignment(WmlStaticScalarContribution contribution, string key,
        WmlStaticSource source, out WmlStaticSourceRange assignment, out WmlStaticSourceRange value)
    {
        assignment = null!; value = null!;
        if (contribution.CompositeAssignmentKeys.Count != 0 || contribution.AssignmentOrigins.FirstOrDefault() is not { } first
            || !SameSource(first.Source, source) || !OutsideDefinitions(first, contribution.ExpansionStack)) return false;
        var end = source.Text.IndexOfAny(['\r', '\n'], first.StartOffset);
        if (end < 0) end = source.Text.Length;
        var raw = source.Text[first.StartOffset..end];
        var equals = raw.IndexOf('=');
        if (equals < 1 || raw[..equals].Trim() != key) return false;
        var start = equals + 1;
        while (start < raw.Length && char.IsWhiteSpace(raw[start])) start++;
        var scalar = raw[start..].TrimEnd();
        // Comments outside a quote/macrocapture remain outside the editable scalar.
        var quoted = false; var depth = 0; var stop = scalar.Length;
        for (var index = 0; index < scalar.Length; index++)
        {
            if (scalar[index] == '"') quoted = !quoted;
            if (!quoted && scalar[index] == '{') depth++;
            if (!quoted && scalar[index] == '}') depth--;
            if (!quoted && depth == 0 && scalar[index] == '#') { stop = index; break; }
        }
        scalar = scalar[..stop].TrimEnd();
        if (!scalar.Contains('{') || WccScenarioFieldValueCodec.ValidateExpression(scalar) is not null) return false;
        assignment = new(source, first.StartOffset, start + scalar.Length);
        value = new(source, first.StartOffset + start, scalar.Length);
        return ValueHasArgumentAncestry(contribution, value);
    }
    private static bool IsScalarInvocation(WmlStaticScalarContribution contribution, string key, WmlStaticSourceRange invocation)
    {
        if (contribution.AssignmentOrigins.FirstOrDefault() is not { } first || !SameSource(first.Source, invocation.Source) ||
            first.StartOffset >= invocation.StartOffset) return false;
        var prefix = first.Source.Text[first.StartOffset..invocation.StartOffset].Trim();
        if (prefix.TrimEnd().TrimEnd('=', ' ', '\t') != key || !prefix.EndsWith('=')) return false;
        var end = invocation.StartOffset + invocation.Length;
        while (end < first.Source.Text.Length && first.Source.Text[end] is ' ' or '\t') end++;
        return end == first.Source.Text.Length || first.Source.Text[end] is '\r' or '\n' or '#';
    }
    private static bool TryDirectPlaceholder(WmlStaticScalarContribution contribution, string key,
        WmlStaticExpansionFrame frame, out string parameter)
    {
        parameter = "";
        if (contribution.AssignmentOrigins.FirstOrDefault() is not { } first || !Inside(first, frame.Definition)) return false;
        var text = first.Source.Text;
        var end = text.IndexOfAny(['\r', '\n', '#'], first.StartOffset);
        if (end < 0) end = text.Length;
        var assignment = text[first.StartOffset..end].TrimEnd();
        var equals = assignment.IndexOf('=');
        if (equals < 1 || assignment[..equals].Trim() != key) return false;
        var value = assignment[(equals + 1)..].Trim();
        if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(value, out var placeholder) || placeholder!.Arguments.Count != 0)
            return false;
        parameter = placeholder.Name;
        return true;
    }
    private static bool SingleProducer(WmlStaticScalarOwner owner, WmlStaticExpansionFrame frame)
    {
        if (owner.Parent is null || owner.OpeningOrigin is null || !Inside(owner.OpeningOrigin, frame.Definition)) return false;
        var producerFrames = owner.OpeningExpansionStack.Where(candidate => candidate.InvocationOrigins.Any(origin =>
            origin.Source.Path == frame.Definition.Source.Path && Inside(origin, frame.Definition))).ToArray();
        if (producerFrames.Length > 0) return false;
        return owner.Parent.DirectChildren.Count(child => child.TagName == owner.TagName &&
            child.OpeningExpansionStack.Any(candidate => candidate.Definition == frame.Definition &&
                candidate.InvocationOrigins.SequenceEqual(frame.InvocationOrigins))) == 1;
    }
}
