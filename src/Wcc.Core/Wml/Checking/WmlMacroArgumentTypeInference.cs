using System.Text.RegularExpressions;

namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Bounded wmlscope-style naming hints. These are not a WML type system or
/// preprocessor binding rules; callers must establish the active definition first.
/// Grouped WML, code, substitutions and expressions are deliberately not typed.
/// </summary>
public static class WmlMacroArgumentTypeInference
{
    private static readonly Regex Integer = new(@"^[+-]?[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Numeric = new(@"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SideName = new(@"^SIDE[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SpanItem = new(@"^[+-]?[0-9]+(?:-[+-]?[0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex RandomIntegerChoice = new(@"^[+-]?[0-9]+(?:\s*\.\.\s*[+-]?[0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string? InferFormalType(string parameterName)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        var name = parameterName.StartsWith('_') ? parameterName[1..] : parameterName;
        // Explicit variable-name tokens outrank suffix guesses such as _X or
        // _AMOUNT. These may be names to read/reset, rather than numeric values.
        // Variable path grammar and definition-body semantics are not inferred.
        if (name.Split('_').Any(token => token is "VAR" or "VARIABLE" or "VARNAME")) return null;
        if (name == "SIDE" || name.EndsWith("_SIDE", StringComparison.Ordinal) || SideName.IsMatch(name)) return "side";
        if (name is "X" or "Y" or "RED" or "GREEN" or "BLUE" or "TURN" or "PROB" or "LAYER" or "TIME" or "DURATION" ||
            EndsWithAny(name, "NUMBER", "AMOUNT", "COST", "RADIUS", "_X", "_Y", "_INCREMENT", "_FACTOR", "_TIME", "_SIZE")) return "numeric";
        if (name.EndsWith("PERCENTAGE", StringComparison.Ordinal)) return "percentage";
        if (name is "POSITION" or "BASE" || name.EndsWith("_POSITION", StringComparison.Ordinal)) return "position";
        if (name.EndsWith("_SPAN", StringComparison.Ordinal)) return "span";
        if (name == "SIDES" || name.EndsWith("_SIDES", StringComparison.Ordinal)) return "alliance";
        return name switch { "RANGE" => "range", "ALIGN" => "alignment", _ => null };
    }

    /// <summary>
    /// Required slots follow the captured definition's order. Trailing NAME=value
    /// arguments may be inspected only after the caller has proved successful binding.
    /// No defaults, inferred macro expansions or resource existence are checked here.
    /// </summary>
    public static IReadOnlyList<WmlMacroArgumentTypeFinding> FindMismatches(
        IReadOnlyList<string> requiredParameters, WmlMacroInvocationSyntax invocation)
    {
        ArgumentNullException.ThrowIfNull(requiredParameters);
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Arguments.Count < requiredParameters.Count) return [];
        var findings = new List<WmlMacroArgumentTypeFinding>();
        for (var index = 0; index < invocation.Arguments.Count; index++)
        {
            var argument = invocation.Arguments[index];
            // The shared parser exposes only the inside of a parenthesized
            // argument. Retain that author's grouping choice instead of
            // treating its stripped contents as an ordinary typed literal.
            if (IsGroupedArgument(invocation.RawText, argument.Span, invocation.Span.StartOffset)) continue;
            var raw = argument.RawText;
            var span = argument.Span;
            string name;
            if (index < requiredParameters.Count) name = requiredParameters[index];
            else
            {
                var equals = raw.IndexOf('=');
                if (equals <= 0 || !WmlPreprocessorToken.IsLiteralName(raw[..equals])) continue;
                name = raw[..equals];
                raw = raw[(equals + 1)..];
                span = span with { StartOffset = span.StartOffset + equals + 1, StartColumn = span.StartColumn + equals + 1 };
            }
            var expected = InferFormalType(name);
            if (expected is null || !TryReadLiteral(raw, out var literal) || Matches(expected, literal, name)) continue;
            findings.Add(new(name, expected, literal, span, index));
        }
        return Array.AsReadOnly(findings.ToArray());
    }

    private static bool Matches(string expected, string value, string parameterName) => expected switch
    {
        // Coordinate and side formals often feed a location/side filter. A
        // scalar-only naming hint would wrongly flag legitimate list/range
        // arguments, integer random-choice ranges (1..7), or the UnitWML
        // recall-list sentinel. The formal name alone
        // cannot establish how the macro uses that coordinate or validate a pair.
        "numeric" => IsNumeric(value) || IsCoordinateName(parameterName) &&
            (IsSpan(value) || IsRandomIntegerChoices(value) || value == "recall"),
        "percentage" => IsNumeric(value),
        "side" => IsNumeric(value) || value == "global" || IsSpan(value),
        "position" => value.Split(',') is [var x, var y] && Integer.IsMatch(x.Trim()) && Integer.IsMatch(y.Trim()),
        "span" or "alliance" => value.Length == 0 && expected == "alliance" ||
            IsSpan(value),
        "range" => value is "melee" or "ranged",
        "alignment" => value is "lawful" or "neutral" or "chaotic" or "liminal",
        _ => true
    };

    private static bool IsNumeric(string value) => Numeric.IsMatch(value);
    private static bool IsSpan(string value) => value.Split(',').All(item => SpanItem.IsMatch(item.Trim()));
    private static bool IsRandomIntegerChoices(string value)
    {
        // mathx.random_choice uses stringx.quoted_split with remove_empty:
        // a trailing comma (or another empty list member) contributes no choice.
        // Retain at least one numeric choice and leave non-coordinate hints strict.
        var choices = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return choices.Length > 0 && choices.All(RandomIntegerChoice.IsMatch);
    }
    private static bool IsCoordinateName(string parameterName)
    {
        var name = parameterName.StartsWith('_') ? parameterName[1..] : parameterName;
        return name is "X" or "Y" || name.EndsWith("_X", StringComparison.Ordinal) || name.EndsWith("_Y", StringComparison.Ordinal);
    }

    private static bool IsGroupedArgument(string raw, WmlTextSpan span, int invocationOffset)
    {
        var before = span.StartOffset - invocationOffset - 1;
        var after = span.EndOffsetExclusive - invocationOffset;
        if (before >= raw.Length || after < 0) return false;
        while (before >= 0 && char.IsWhiteSpace(raw[before])) before--;
        while (after < raw.Length && char.IsWhiteSpace(raw[after])) after++;
        return before >= 0 && after < raw.Length && raw[before] == '(' && raw[after] == ')';
    }

    private static bool TryReadLiteral(string raw, out string value)
    {
        value = raw.Trim();
        if (value.IndexOfAny(['\r', '\n', '{', '}', '$', '(', ')', '[', ']', '<', '>', '=', '#', '\\']) >= 0) return false;
        if (value.StartsWith('"'))
        {
            if (value.Length < 2 || !value.EndsWith('"') || value.AsSpan(1, value.Length - 2).Contains('"')) return false;
            value = value[1..^1];
        }
        else if (value.Contains('"')) return false;
        // Arithmetic and concatenation have runtime/preprocessor meaning beyond
        // this literal subset. A signed number remains a numeric literal.
        return IsNumeric(value) || value.IndexOfAny(['+', '*', '/', '%', '?', ':', ';', '|', '&', '!']) < 0;
    }

    private static bool EndsWithAny(string value, params string[] suffixes) =>
        suffixes.Any(suffix => value.EndsWith(suffix, StringComparison.Ordinal));
}

public sealed record WmlMacroArgumentTypeFinding(string ParameterName, string ExpectedType,
    string LiteralText, WmlTextSpan Span, int ArgumentIndex);
