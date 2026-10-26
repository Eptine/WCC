namespace Wcc.Core.Wml;

public sealed record WmlSingleBlockValidationResult(bool IsValid, string? Problem = null);

/// <summary>
/// Conservative structural validation for explicit raw-block editors. It does
/// not claim to validate all WML semantics; it only prevents text outside the
/// requested block and unbalanced literal tag structure.
/// </summary>
public static class WmlSingleBlockValidator
{
    /// <summary>A literal block with no invocations or directives other than lexical textdomains.</summary>
    public static WmlSingleBlockValidationResult ValidateLiteral(string rawWml, string tagName)
    {
        var block = Validate(rawWml, tagName);
        if (!block.IsValid) return block;
        var structure = WmlAuthoredStructureScanner.Scan(rawWml);
        return WmlMacroInvocationSyntaxScanner.Scan(rawWml).Count != 0 || structure.Directives.Any(directive => directive.Name != "textdomain")
            ? new(false, $"Use one literal [{tagName}] block. Macro and conditional code belongs to a captured preset.")
            : block;
    }

    public static WmlSingleBlockValidationResult Validate(string rawWml, string tagName)
    {
        if (string.IsNullOrWhiteSpace(rawWml))
            return new WmlSingleBlockValidationResult(false, $"[{tagName}] WML cannot be empty.");
        var normalized = rawWml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var structure = WmlAuthoredStructureScanner.Scan(normalized);
        if (structure.Problem is { } lexicalProblem)
            return new(false, lexicalProblem);
        var structuralLines = structure.MaskedText.Split('\n');
        var first = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var last = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (first < 0 ||
            !string.Equals(structuralLines[first].Trim(), $"[{tagName}]", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(structuralLines[last].Trim(), $"[/{tagName}]", StringComparison.OrdinalIgnoreCase))
        {
            return new WmlSingleBlockValidationResult(
                false,
                $"The editor must contain exactly one complete [{tagName}]...[/{tagName}] block.");
        }

        if ((ValidateDirectives(structure.Directives) ?? ValidateTagBalance(structuralLines)) is { } structureProblem)
            return new(false, structureProblem);
        var tree = WmlProjectionTreeParser.Parse(structure.MaskedText);
        if (tree.Roots.Count != 1 ||
            !string.Equals(tree.Roots[0].TagName, tagName, StringComparison.OrdinalIgnoreCase) ||
            tree.Roots[0].StartLine != first ||
            tree.Roots[0].EndLineExclusive - 1 != last)
        {
            return new WmlSingleBlockValidationResult(
                false,
                $"Text outside the single [{tagName}] block or mismatched literal tags cannot be saved here.");
        }

        foreach (var node in tree.Roots[0].DescendantsAndSelf())
        {
            var closingLine = structuralLines[Math.Clamp(node.EndLineExclusive - 1, 0, structuralLines.Length - 1)].Trim();
            if (!string.Equals(
                    closingLine,
                    $"[/{node.TagName}]",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new WmlSingleBlockValidationResult(
                    false,
                    $"The [{node.TagName}] tag is not closed with [/{node.TagName}].");
            }
        }

        return new WmlSingleBlockValidationResult(true);
    }

    /// <summary>Literal source balance only; does not expand macros or validate game semantics.</summary>
    public static WmlSingleBlockValidationResult ValidateSourceStructure(string rawWml)
    {
        var structure = WmlAuthoredStructureScanner.Scan(rawWml);
        var problem = structure.Problem ?? ValidateDirectives(structure.Directives) ??
            ValidateTagBalance(structure.MaskedText.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').Split('\n'));
        return new(problem is null, problem);
    }

    private static string? ValidateDirectives(IReadOnlyList<WmlAuthoredDirective> directives)
    {
        var conditionals = new Stack<(int DefinitionDepth, bool HasElse)>();
        foreach (var directive in directives)
        {
            if (WmlAuthoredStructureScanner.IsConditionalStart(directive.Name))
                conditionals.Push((directive.DefinitionDepth, false));
            else if (directive.Name is "else" or "endif")
            {
                if (conditionals.Count == 0 || conditionals.Peek().DefinitionDepth != directive.DefinitionDepth)
                    return $"A #{directive.Name} has no matching conditional in this source scope.";
                var active = conditionals.Pop();
                if (directive.Name == "else")
                {
                    if (active.HasElse) return "A preprocessor conditional contains more than one #else.";
                    conditionals.Push((active.DefinitionDepth, true));
                }
            }
            else if (directive.Name == "enddef" &&
                     conditionals.Any(item => item.DefinitionDepth >= directive.DefinitionDepth))
                return "A conditional inside a #define is missing its #endif.";
        }
        return conditionals.Count == 0 ? null : "A preprocessor conditional is missing its #endif.";
    }

    private static string? ValidateTagBalance(IReadOnlyList<string> lines)
    {
        var tags = new Stack<string>();
        foreach (var sourceLine in lines)
        {
            var line = sourceLine.Trim();
            if (!line.StartsWith('[') || !line.EndsWith(']')) continue;
            if (line.Contains("][", StringComparison.Ordinal))
            {
                // The projection reader deliberately omits compact empty children.
                // Validate complete empty pairs without changing physical source spans.
                var remaining = line.AsSpan();
                while (!remaining.IsEmpty)
                {
                    remaining = remaining.TrimStart();
                    if (remaining.IsEmpty) break;
                    if (remaining[0] != '[') return "A compact WML tag pair contains surrounding code.";
                    var openingEnd = remaining.IndexOf(']');
                    if (openingEnd < 2) return "A compact WML opening tag is malformed.";
                    var opening = remaining[1..openingEnd].Trim().ToString();
                    if (opening.StartsWith('/')) return "A compact WML tag pair is missing its opening tag.";
                    remaining = remaining[(openingEnd + 1)..].TrimStart();
                    if (!remaining.StartsWith("[/", StringComparison.Ordinal)) return "Only complete empty compact tag pairs can be validated here.";
                    var closingEnd = remaining.IndexOf(']');
                    if (closingEnd < 3 || !remaining[2..closingEnd].Trim().Equals(opening.TrimStart('+', '-').AsSpan(), StringComparison.OrdinalIgnoreCase))
                        return "The compact closing tag does not match its opening tag.";
                    remaining = remaining[(closingEnd + 1)..];
                }
                continue;
            }
            var inner = line[1..^1].Trim();
            var closing = inner.StartsWith('/');
            if (closing) inner = inner[1..].Trim();
            var separator = inner.IndexOfAny([' ', '\t']);
            var name = (separator < 0 ? inner : inner[..separator]).TrimStart('+', '-');
            if (name.Length == 0) return "A literal WML tag has no name.";
            if (!closing) tags.Push(name);
            else if (tags.Count == 0 || !tags.Pop().Equals(name, StringComparison.OrdinalIgnoreCase))
                return $"The closing [/{name}] tag does not match an open literal tag.";
        }
        return tags.Count == 0 ? null : $"The [{tags.Peek()}] tag is not closed.";
    }
}
