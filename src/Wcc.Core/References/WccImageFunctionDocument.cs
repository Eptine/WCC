using System.Collections.ObjectModel;

namespace Wcc.Core.References;

/// <summary>One authored function, including spelling/trivia that the editor has not changed.</summary>
public sealed record WccImageFunctionStep(string Name, string Arguments, string RawText, bool IsEnabled = true)
{
    public static WccImageFunctionStep Create(string name, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        if (name.Length > 64 || !name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            throw new ArgumentException("An image function name must contain ASCII letters, numbers or underscores.", nameof(name));
        return new(name, arguments, "~" + name + "(" + arguments + ")");
    }
}

/// <summary>An ordered expression recipe. Parsing or previewing never grants source-write authority.</summary>
public sealed class WccImageFunctionDocument
{
    public const int MaximumExpressionLength = 64 * 1024;
    public const int MaximumFunctions = 128;
    private readonly string _prefix;
    private readonly string _suffix;
    private WccImageFunctionDocument(string baseReference, IEnumerable<WccImageFunctionStep> steps, string? diagnostic,
        string prefix = "", string suffix = "")
    {
        _prefix = prefix; _suffix = suffix;
        BaseReference = baseReference;
        Steps = Array.AsReadOnly(steps.ToArray());
        Diagnostic = diagnostic;
    }
    public string BaseReference { get; }
    public IReadOnlyList<WccImageFunctionStep> Steps { get; }
    public string? Diagnostic { get; }
    public bool IsComplete => Diagnostic is null;
    public string BuildExpression() => _prefix + BaseReference + string.Concat(Steps.Where(step => step.IsEnabled).Select(step => step.RawText)) + _suffix;

    public WccImageFunctionDocument WithBaseReference(string baseReference)
    {
        ArgumentNullException.ThrowIfNull(baseReference);
        return Recheck(baseReference, Steps);
    }
    public WccImageFunctionDocument WithSteps(IEnumerable<WccImageFunctionStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return Recheck(BaseReference, steps.ToArray());
    }
    private WccImageFunctionDocument Recheck(string baseReference, IReadOnlyList<WccImageFunctionStep> steps)
    {
        var parsed = Parse(_prefix + baseReference + string.Concat(steps.Where(step => step.IsEnabled).Select(step => step.RawText)) + _suffix);
        return new(baseReference, steps, parsed.Diagnostic, _prefix, _suffix);
    }

    public static WccImageFunctionDocument Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Length > MaximumExpressionLength)
            return new(expression, [], "The image expression exceeds the editor's bounded length.");
        var located = WccImageExpressionParser.TryParse(expression);
        var logicalStart = located is null ? -1 : expression.IndexOf(located.NormalizedExpression, StringComparison.Ordinal);
        if (located is null || logicalStart < 0)
            return new(expression, [], "Choose an image.");
        var prefix = expression[..logicalStart];
        var suffix = expression[(logicalStart + located.NormalizedExpression.Length)..];
        var body = located.NormalizedExpression;
        var first = located.HasModifiers ? located.BaseReference.Length : -1;
        if (first < 0) return new(body, [], null, prefix, suffix);
        var baseReference = body[..first];
        var steps = new List<WccImageFunctionStep>();
        string? problem = string.IsNullOrWhiteSpace(baseReference) ? "Choose an image." : null;
        var cursor = first;
        while (cursor < body.Length)
        {
            if (steps.Count == MaximumFunctions)
            {
                steps.Add(new("", "", body[cursor..]));
                problem ??= "The image body contains too many functions.";
                break;
            }
            var start = cursor++;
            var nameStart = cursor;
            while (cursor < body.Length && (char.IsAsciiLetterOrDigit(body[cursor]) || body[cursor] == '_')) cursor++;
            var name = body[nameStart..cursor];
            while (cursor < body.Length && body[cursor] is ' ' or '\t') cursor++;
            if (name.Length == 0 || cursor == body.Length || body[cursor] != '(')
            {
                steps.Add(new(name, "", body[start..]));
                problem ??= "A function needs its name and balanced parentheses. The original text is retained.";
                break;
            }
            var argumentStart = ++cursor;
            var depth = 1;
            char quote = '\0';
            for (; cursor < body.Length; cursor++)
            {
                var current = body[cursor];
                if (quote != '\0')
                {
                    if (current == '\\' && cursor + 1 < body.Length) { cursor++; continue; }
                    if (current == quote)
                    {
                        if (cursor + 1 < body.Length && body[cursor + 1] == quote) cursor++;
                        else quote = '\0';
                    }
                    continue;
                }
                if (current is '\'' or '"') { quote = current; continue; }
                if (current == '(' && ++depth > 64) { problem ??= "Function nesting exceeds the bounded editor limit."; break; }
                if (current == ')' && --depth == 0) break;
            }
            if (depth != 0 || cursor == body.Length)
            {
                steps.Add(new(name, body[argumentStart..], body[start..]));
                problem ??= "A function has unclosed parentheses or quotes. The original text is retained.";
                break;
            }
            var arguments = body[argumentStart..cursor];
            cursor++;
            while (cursor < body.Length && body[cursor] is ' ' or '\t' or '\r' or '\n') cursor++;
            if (cursor < body.Length && body[cursor] != '~')
            {
                var next = body.IndexOf('~', cursor);
                cursor = next < 0 ? body.Length : next;
                problem ??= "Unexpected text follows a function. The original text is retained.";
            }
            steps.Add(new(name, arguments, body[start..cursor]));
        }
        return new(baseReference, steps, problem, prefix, suffix);
    }

    /// <summary>Commas inside nested image functions, formulas and strings stay in their argument.</summary>
    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new List<string>(); var start = 0; var depth = 0; var brackets = 0; char quote = '\0';
        for (var index = 0; index < arguments.Length; index++)
        {
            var character = arguments[index];
            if (quote != '\0')
            {
                if (character == '\\' && index + 1 < arguments.Length) index++;
                else if (character == quote)
                {
                    if (index + 1 < arguments.Length && arguments[index + 1] == quote) index++;
                    else quote = '\0';
                }
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (character == '(') depth++;
            else if (character == ')') depth--;
            else if (character == '[') brackets++;
            else if (character == ']') brackets--;
            else if (character == ',' && depth == 0 && brackets == 0) { values.Add(arguments[start..index].Trim()); start = index + 1; }
        }
        values.Add(arguments[start..].Trim());
        return new ReadOnlyCollection<string>(values);
    }
}