using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Wcc.Core.Wml;
using Wcc.Core.Campaigns;

namespace Wcc.Core.Scenarios;

public enum WccScenarioFieldValueInputKind { Literal, WmlExpression }

/// <summary>Scalar authoring syntax, separate from defaults, resolved display and persistence authority.</summary>
public static class WccScenarioFieldValueCodec
{
    private static readonly ScenarioInitialFieldDefinition Turns =
        ScenarioInitialFieldCatalog.ScenarioFields.Single(field => field.Key == "turns");

    /// <summary>Input syntax only. Marker text still requires scalar validation and complete source review.</summary>
    public static WccScenarioFieldValueInputKind InferInputKind(string? text) =>
        text is not null && text.IndexOfAny(['{', '}', '$']) >= 0
            ? WccScenarioFieldValueInputKind.WmlExpression : WccScenarioFieldValueInputKind.Literal;

    public static bool TryGetDefinition(string tag, string key, [NotNullWhen(true)] out ScenarioInitialFieldDefinition? field)
    {
        if (tag == "campaign") return CampaignInitialFieldCatalog.TryGet(key, out field) && field.IsEditable;
        if (tag == "scenario" && key == "turns") { field = Turns; return true; }
        if (ScenarioInitialFieldCatalog.TryGet(tag, key, out field)) return field.IsEditable;
        return tag == "side" && ScenarioInitialFieldCatalog.LegacyInlineKeys.Contains(key)
            && ScenarioInitialFieldCatalog.TryGet("leader", key, out field) && field.IsEditable;
    }

    public static string DecodeLiteral(string? wml)
    {
        if (wml is null) return "";
        return TryDecode(wml, out var value, out var translated) ?
            translated && value.IndexOf('^') is var separator && separator >= 0 ? value[(separator + 1)..] : value
            : wml;
    }

    public static string? ValidateInput(ScenarioInitialFieldDefinition field, string? text,
        WccScenarioFieldValueInputKind kind = WccScenarioFieldValueInputKind.Literal)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (text is null) return "Enter a value, or use the explicit default/removal action.";
        if (kind == WccScenarioFieldValueInputKind.WmlExpression) return ValidateExpression(text);
        if (kind != WccScenarioFieldValueInputKind.Literal) return "The value input mode is unsupported.";
        if (field.Kind is not (ScenarioInitialFieldKind.Multiline or ScenarioInitialFieldKind.TranslatableText)
            && text.IndexOfAny(['\r', '\n']) >= 0)
            return "Enter one value; multiline text is supported only for a multiline field.";
        if (text.IndexOfAny(['{', '}', '$']) >= 0)
            return "Enter a complete scalar value or macro expression.";
        if (field.Kind == ScenarioInitialFieldKind.TranslatableText)
            return WccWmlPlainTextCodec.TryEncodeInput(text, true, null, out _, out var textProblem) ? null : textProblem;
        var value = text.Trim();
        return field.Kind switch
        {
            ScenarioInitialFieldKind.Integer when !IsInteger(value) && !(field.Key == "turns" && value == "unlimited") => "Enter a whole number (negative values supported) or a macro expression.",
            ScenarioInitialFieldKind.Boolean when !TryBoolean(value, out _) => "Enter yes, no, true or false or a macro expression.",
            ScenarioInitialFieldKind.Coordinate when !IsInteger(value) && value != "recall" => "Enter a signed coordinate, recall or a macro expression.",
            _ => null
        };
    }

    public static string EncodeLiteral(ScenarioInitialFieldDefinition field, string text, string? existingWml = null)
    {
        var refusal = ValidateInput(field, text);
        if (refusal is not null) throw new ArgumentException(refusal, nameof(text));
        if (field.Kind == ScenarioInitialFieldKind.TranslatableText)
        {
            var translate = existingWml is null || WccWmlPlainTextCodec.TryReadLiteral(existingWml, out _, out var flag) && flag;
            if (WccWmlPlainTextCodec.TryEncodeInput(text, translate, existingWml, out var encoded, out var problem)) return encoded;
            throw new ArgumentException(problem, nameof(text));
        }
        var value = field.Kind is ScenarioInitialFieldKind.Text or ScenarioInitialFieldKind.TranslatableText
            or ScenarioInitialFieldKind.Multiline ? text : text.Trim();
        if (field.Kind is ScenarioInitialFieldKind.Integer or ScenarioInitialFieldKind.Boolean or ScenarioInitialFieldKind.Coordinate)
            return value;
        var translated = existingWml is not null ? IsTranslated(existingWml) : field.Kind == ScenarioInitialFieldKind.TranslatableText;
        if (translated && existingWml is not null && TryDecode(existingWml, out var oldValue, out _)
            && oldValue.IndexOf('^') is var separator && separator >= 0)
            value = oldValue[..(separator + 1)] + value;
        return (translated ? "_ " : "") + "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static bool Equivalent(ScenarioInitialFieldDefinition field, string? actualWml, string? requestedLiteral)
    {
        if (field.Kind == ScenarioInitialFieldKind.TranslatableText)
        {
            if (requestedLiteral is null || !WccWmlPlainTextCodec.TryReadLiteral(actualWml, out var actualText, out var flag) ||
                !WccWmlPlainTextCodec.TryEncodeInput(requestedLiteral, flag, actualWml, out var encoded, out _) ||
                !WccWmlPlainTextCodec.TryReadLiteral(encoded, out var requestedText, out _)) return false;
            return actualText == requestedText;
        }
        if (actualWml is null || requestedLiteral is null || !TryDecode(actualWml, out var actual, out var translated)
            || translated && field.Kind is ScenarioInitialFieldKind.Integer or ScenarioInitialFieldKind.Boolean or ScenarioInitialFieldKind.Coordinate) return false;
        var requested = field.Kind is ScenarioInitialFieldKind.Text or ScenarioInitialFieldKind.TranslatableText
            or ScenarioInitialFieldKind.Multiline ? requestedLiteral : requestedLiteral.Trim();
        if (field.Kind == ScenarioInitialFieldKind.Integer)
            return SameInteger(actual, requested) || field.Key == "turns" && actual == "unlimited" && requested == "unlimited";
        if (field.Kind == ScenarioInitialFieldKind.Boolean)
            return TryBoolean(actual, out var left) && TryBoolean(requested, out var right) && left == right;
        if (field.Kind == ScenarioInitialFieldKind.Coordinate) return SameInteger(actual, requested) || actual == "recall" && requested == "recall";
        return DecodeLiteral(actualWml) == requested;
    }

    public static bool IsSupportedResolvedValue(ScenarioInitialFieldDefinition field, string? wml)
    {
        if (field.Kind == ScenarioInitialFieldKind.TranslatableText)
            return WccWmlPlainTextCodec.TryReadLiteral(wml, out _, out _);
        if (wml is null || !TryDecode(wml, out var value, out var translated)
            || translated && field.Kind is ScenarioInitialFieldKind.Integer or ScenarioInitialFieldKind.Boolean or ScenarioInitialFieldKind.Coordinate) return false;
        if (value.IndexOfAny(['{', '}', '$']) >= 0) return false;
        return field.Kind switch
        {
            ScenarioInitialFieldKind.Integer => IsInteger(value) || field.Key == "turns" && value == "unlimited",
            ScenarioInitialFieldKind.Boolean => TryBoolean(value, out _),
            ScenarioInitialFieldKind.Coordinate => IsInteger(value) || value == "recall",
            _ => true
        };
    }

    // Primitive single-literal syntax stays strict for IDs, paths and non-text authoring proofs.
    internal static bool TryDecodeSingleLiteral(string value, out string text, out bool translated) => TryDecode(value, out text, out translated);

    internal static bool IsSupportedLiteralWml(string value) => TryDecode(value, out _, out _)
        && value.IndexOfAny(['$', '{', '}']) < 0;

    internal static string? ValidateExpression(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.IndexOfAny(['\r', '\n']) >= 0)
            return "Enter one complete single-line WML scalar expression.";
        var macros = WmlMacroInvocationSyntaxScanner.Scan(value).Where(invocation => invocation.NestingDepth == 0)
            .ToDictionary(invocation => invocation.Span.StartOffset);
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '{')
            {
                if (!macros.TryGetValue(index, out var invocation) || !WmlMacroInvocationSyntaxScanner.TryParseSingle(invocation.RawText, out _))
                    return "The macro expression is incomplete.";
                if (invocation.RawText.Contains('#')) return "Preprocessor directives and comments are not scalar input.";
                index = invocation.Span.EndOffsetExclusive - 1;
                continue;
            }
            if (current == '"')
            {
                if (quoted && index + 1 < value.Length && value[index + 1] == '"') { index++; continue; }
                quoted = !quoted;
                continue;
            }
            if (!quoted && current is '}' or '[' or ']' or '#' or '=' or '<' or '>')
                return "Enter a scalar expression without WML tags, assignments, comments or raw-code delimiters.";
        }
        return quoted ? "The quoted scalar is incomplete." : null;
    }

    private static bool TryDecode(string wml, out string value, out bool translated)
    {
        var source = wml.Trim();
        translated = IsTranslated(source);
        if (translated) source = source[1..].TrimStart();
        value = "";
        if (source.Length == 0) return true;
        if (source[0] != '"')
        {
            if (source.IndexOfAny(['"', '{', '}', '$', '\r', '\n', '<', '>']) >= 0
                || source.Contains('+') && !IsInteger(source)) return false;
            value = source;
            return true;
        }
        var result = new StringBuilder();
        for (var index = 1; index < source.Length; index++)
        {
            if (source[index] != '"') { result.Append(source[index]); continue; }
            if (index + 1 < source.Length && source[index + 1] == '"') { result.Append('"'); index++; continue; }
            if (index != source.Length - 1) return false;
            value = result.ToString();
            return true;
        }
        return false;
    }

    private static bool IsTranslated(string value) => value.TrimStart().StartsWith('_')
        && value.TrimStart()[1..].TrimStart().StartsWith('"');
    private static bool IsInteger(string value) => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    private static bool SameInteger(string left, string right) => int.TryParse(left, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var a)
        && int.TryParse(right, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var b) && a == b;
    private static bool TryBoolean(string value, out bool result)
    {
        result = value is "yes" or "true";
        return value is "yes" or "true" or "no" or "false";
    }
}
