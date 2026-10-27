using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>One minimal source replacement. Semantic review and atomic persistence are separate mandatory steps.</summary>
public static class WccScenarioFieldCandidatePlanner
{
    public static WccScenarioFieldCandidate Plan(WccScenarioFieldRoute route, WmlStaticSource currentSource,
        WmlFieldOperation operation, string? requestedValue) =>
        Plan(route, currentSource, operation, requestedValue, WccScenarioFieldValueInputKind.Literal);

    public static WccScenarioFieldCandidate Plan(WccScenarioFieldRoute route, WmlStaticSource currentSource,
        WmlFieldOperation operation, string? requestedValue, WccScenarioFieldValueInputKind valueInputKind)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(currentSource);
        if (route.Key.Length == 0 || !route.Key.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            throw new InvalidOperationException("The captured property key is invalid.");
        if (!WccScenarioFieldRouteAnalyzer.SameSource(route.Source, currentSource))
            throw new InvalidOperationException("The captured source revision has changed; resolve the field again.");
        var permitted = operation switch
        {
            WmlFieldOperation.Set => route.CanSet,
            WmlFieldOperation.Insert => route.CanInsert,
            WmlFieldOperation.Remove => route.CanRemove,
            _ => false
        };
        if (!permitted || route.Kind == WccScenarioFieldRouteKind.RawOnly)
            throw new InvalidOperationException("This operation has no verified scenario-local route.");
        if (!WccScenarioFieldValueCodec.TryGetDefinition(route.Owner.TagName, route.Key, out var field))
            throw new InvalidOperationException("This property has no supported initial-field editing semantics.");
        var value = operation == WmlFieldOperation.Remove ? null : requestedValue;
        if (operation != WmlFieldOperation.Remove && WccScenarioFieldValueCodec.ValidateInput(field, value, valueInputKind) is { } refusal)
            throw new ArgumentException(refusal, nameof(requestedValue));
        var sourceValue = value is null ? null : valueInputKind == WccScenarioFieldValueInputKind.WmlExpression ? value
            : WccScenarioFieldValueCodec.EncodeLiteral(field, value, route.SourceValue is { } authoredLiteral
                && WccScenarioFieldValueCodec.IsSupportedLiteralWml(authoredLiteral) ? authoredLiteral : route.Value);
        int start;
        int length;
        string replacement;
        var scalarOffset = 0;
        if (route.Kind == WccScenarioFieldRouteKind.OptionalArgument)
        {
            var invocation = route.Invocation ?? throw new InvalidOperationException("The exact invocation is unavailable.");
            if (operation == WmlFieldOperation.Remove)
            {
                if (route.Target is null || route.ParameterName is null) throw new InvalidOperationException("No explicit optional argument exists.");
                start = route.Target.StartOffset - route.ParameterName.Length - 1;
                length = route.Target.Length + route.ParameterName.Length + 1;
                // Parenthesized argument delimiters belong to the removable argument too.
                var before = start - 1;
                var after = start + length;
                while (before > invocation.StartOffset && char.IsWhiteSpace(currentSource.Text[before])) before--;
                while (after < invocation.StartOffset + invocation.Length && char.IsWhiteSpace(currentSource.Text[after])) after++;
                if (before > invocation.StartOffset && currentSource.Text[before] == '(' &&
                    after < invocation.StartOffset + invocation.Length && currentSource.Text[after] == ')')
                { start = before; length = after + 1 - before; }
                if (start > invocation.StartOffset && currentSource.Text[start - 1] is ' ' or '\t') { start--; length++; }
                replacement = "";
            }
            else if (route.Target is { } target)
            {
                start = target.StartOffset; length = target.Length; replacement = sourceValue!;

                if (NeedsArgumentGrouping(sourceValue!) && !HasOuterParentheses(currentSource.Text,
                        target.StartOffset - route.ParameterName!.Length - 1, target.StartOffset + target.Length, invocation))
                {
                    start -= route.ParameterName!.Length + 1;
                    length += route.ParameterName.Length + 1;
                    replacement = "(" + route.ParameterName + "=" + sourceValue + ")";
                    scalarOffset = route.ParameterName.Length + 2;
                }
            }
            else
            {
                start = invocation.StartOffset + invocation.Length - 1; length = 0;
                var grouped = NeedsArgumentGrouping(sourceValue!);
                replacement = " " + (grouped ? "(" : "") + route.ParameterName + "=" + sourceValue + (grouped ? ")" : "");
                scalarOffset = 1 + (grouped ? 1 : 0) + route.ParameterName!.Length + 1;
            }
        }
        else if (route.Kind == WccScenarioFieldRouteKind.LocalAssignment)
        {
            var closing = route.Owner.ClosingOrigin ?? throw new InvalidOperationException("The exact containing-tag closing is unavailable.");
            var lineStart = currentSource.Text.LastIndexOf('\n', Math.Max(0, closing.StartOffset - 1)) + 1;
            var indent = currentSource.Text[lineStart..closing.StartOffset];
            var newline = currentSource.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            start = lineStart; length = 0; replacement = indent + "    " + route.Key + "=" + sourceValue + newline;
            scalarOffset = indent.Length + 4 + route.Key.Length + 1;
        }
        else if (operation == WmlFieldOperation.Remove)
        {
            var assignment = route.Assignment ?? throw new InvalidOperationException("The exact assignment is unavailable.");
            start = assignment.StartOffset; length = assignment.Length; replacement = "";
        }
        else
        {
            var target = route.Target ?? throw new InvalidOperationException("The exact value token is unavailable.");
            start = target.StartOffset; length = target.Length; replacement = sourceValue!;
            if (route.Kind == WccScenarioFieldRouteKind.DirectToken && route.CompositeAssignment is { } composite)
            {
                var prefix = composite.MissingPrefix(route.CompositeComponentIndex);
                replacement = prefix + sourceValue;
                scalarOffset = prefix.Length;
            }
            if (route.Kind == WccScenarioFieldRouteKind.ScalarArgument && NeedsArgumentGrouping(sourceValue!)
                && !HasOuterParentheses(currentSource.Text, start, start + length, route.Invocation!))
            { replacement = "(" + sourceValue + ")"; scalarOffset = 1; }
        }
        if (start < 0 || length < 0 || start > currentSource.Text.Length - length)
            throw new InvalidOperationException("The replacement is outside the captured source.");
        var text = currentSource.Text[..start] + replacement + currentSource.Text[(start + length)..];
        var candidateSource = new WmlStaticSource(currentSource.Path, text);
        return new(route, candidateSource, start, length, replacement, value, operation)
        {
            ValueInputKind = valueInputKind, RequestedSourceValue = sourceValue,
            ProposedValueOrigin = sourceValue is null ? null : new(candidateSource, start + scalarOffset, sourceValue.Length)
        };
    }

    private static bool NeedsArgumentGrouping(string value) =>
        !WmlMacroInvocationSyntaxScanner.TryParseSingle("{__WCC_VALUE " + value + "}", out var syntax)
        || syntax!.Arguments.Count != 1 || syntax.Arguments[0].RawText != value;

    private static bool HasOuterParentheses(string text, int start, int end, WmlStaticSourceRange invocation)
    {
        var before = start - 1;
        while (before > invocation.StartOffset && char.IsWhiteSpace(text[before])) before--;
        while (end < invocation.StartOffset + invocation.Length && char.IsWhiteSpace(text[end])) end++;
        return before > invocation.StartOffset && text[before] == '(' && end < invocation.StartOffset + invocation.Length && text[end] == ')';
    }

    internal static bool IsSafeScalar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            value.IndexOfAny(['\r', '\n', '{', '}', '[', ']', '#', '$', '=', '<', '>']) >= 0) return false;
        if (value[0] == '"') return value.Length >= 2 && value[^1] == '"' && !value[1..^1].Contains('"');
        return !value.Any(char.IsWhiteSpace) && !value.Contains('"') && !value.Contains('+') &&
            !value.Contains('(') && !value.Contains(')');
    }
}
