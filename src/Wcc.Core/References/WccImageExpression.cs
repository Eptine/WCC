using Wcc.Core.Wml;

namespace Wcc.Core.References;

public sealed record WccImageExpression(
    string NormalizedExpression,
    string BaseReference,
    string ModifierSuffix)
{
    public bool HasModifiers => !string.IsNullOrWhiteSpace(ModifierSuffix);

    public bool MayContainNestedImageReferences =>
        ModifierSuffix.Contains("~BLIT(", StringComparison.OrdinalIgnoreCase) ||
        ModifierSuffix.Contains("~MASK(", StringComparison.OrdinalIgnoreCase) ||
        ModifierSuffix.Contains("~LAYER(", StringComparison.OrdinalIgnoreCase);
}

public static class WccImageExpressionParser
{
    public static WccImageExpression? TryParse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var value = WmlInlineCommentSplitter.Split(rawValue.Trim()).Value.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1].Trim();
        }

        if (value.Length == 0)
            return null;

        // A leading '~' may be Wesnoth's add-on path marker. In that case the
        // next '~' begins the first Image Path Function.
        var searchStart = value[0] == '~' ? 1 : 0;
        var modifierIndex = FindModifierStart(value, searchStart);
        var baseReference = modifierIndex >= 0 ? value[..modifierIndex] : value;
        var modifierSuffix = modifierIndex >= 0 ? value[modifierIndex..] : string.Empty;
        if (string.IsNullOrWhiteSpace(baseReference))
            return null;

        return new WccImageExpression(value, baseReference, modifierSuffix);
    }

    private static int FindModifierStart(string value, int searchStart)
    {
        var squareDepth = 0;
        var braceDepth = 0;
        for (var i = searchStart; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    squareDepth = Math.Max(0, squareDepth - 1);
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
                case '~' when squareDepth == 0 && braceDepth == 0:
                    return i;
            }
        }

        return -1;
    }
}

public static class WccImageExpressionNestedReferenceScanner
{
    private static readonly HashSet<string> ImageArgumentFunctions = new(
        ["BLIT", "MASK", "LAYER"],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> FindBaseReferences(WccImageExpression expression)
    {
        if (expression is null) throw new ArgumentNullException(nameof(expression));

        var references = new HashSet<string>(StringComparer.Ordinal);
        Scan(expression.ModifierSuffix, references);
        return references.ToArray();
    }

    private static void Scan(string value, HashSet<string> references)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '~')
                continue;

            var nameStart = index + 1;
            var cursor = nameStart;
            while (cursor < value.Length &&
                   (char.IsLetterOrDigit(value[cursor]) || value[cursor] == '_'))
            {
                cursor++;
            }

            if (cursor == nameStart || cursor >= value.Length || value[cursor] != '(')
                continue;

            var close = FindMatchingParenthesis(value, cursor);
            if (close < 0)
                return;

            var functionName = value[nameStart..cursor];
            var arguments = value[(cursor + 1)..close];
            if (ImageArgumentFunctions.Contains(functionName))
            {
                var imageArgument = ReadFirstArgument(arguments);
                var nested = WccImageExpressionParser.TryParse(imageArgument);
                if (nested is not null)
                {
                    references.Add(nested.BaseReference);
                    Scan(nested.ModifierSuffix, references);
                }
            }

            index = close;
        }
    }

    private static int FindMatchingParenthesis(string value, int openingIndex)
    {
        var depth = 0;
        char? quote = null;
        for (var index = openingIndex; index < value.Length; index++)
        {
            var current = value[index];
            if (quote is not null)
            {
                if (current == quote && (index == 0 || value[index - 1] != '\\'))
                    quote = null;
                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current == '(')
                depth++;
            else if (current == ')' && --depth == 0)
                return index;
        }

        return -1;
    }

    private static string ReadFirstArgument(string arguments)
    {
        var parenthesisDepth = 0;
        var squareDepth = 0;
        char? quote = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            var current = arguments[index];
            if (quote is not null)
            {
                if (current == quote && (index == 0 || arguments[index - 1] != '\\'))
                    quote = null;
                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            switch (current)
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    squareDepth = Math.Max(0, squareDepth - 1);
                    break;
                case ',' when parenthesisDepth == 0 && squareDepth == 0:
                    return arguments[..index].Trim();
            }
        }

        return arguments.Trim();
    }
}
