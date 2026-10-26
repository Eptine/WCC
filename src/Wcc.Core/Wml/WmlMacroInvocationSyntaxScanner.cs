namespace Wcc.Core.Wml;

/// <summary>
/// Exact source coordinates for a piece of WML. Offsets are measured against
/// the original text and the line/column values are zero based.
/// </summary>
public sealed record WmlTextSpan(
    int StartOffset,
    int EndOffsetExclusive,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn)
{
    public int Length => EndOffsetExclusive - StartOffset;
}

public sealed record WmlMacroArgumentSyntax(
    string RawText,
    WmlTextSpan Span);

/// <summary>
/// One macro-like brace expression together with exact source spans. Unlike
/// <see cref="WmlMacroInvocationScanner"/>, this scanner intentionally finds
/// embedded, adjacent, nested, and multiline invocations. This is the shared
/// source-mapping representation used by graph/navigation and future safe
/// argument write-back; it does not imply that an invocation can be expanded.
/// </summary>
public sealed record WmlMacroInvocationSyntax(
    string Name,
    string RawText,
    WmlTextSpan Span,
    IReadOnlyList<WmlMacroArgumentSyntax> Arguments,
    int NestingDepth,
    bool IsInsideMacroDefinition);

public static class WmlMacroInvocationSyntaxScanner
{
    /// <summary>
    /// Parses one already bounded invocation without enumerating nested brace
    /// candidates. The evaluator supplies lexical depth/cancellation bounds;
    /// raw-string arguments can then be handled in linear scanning passes.
    /// </summary>
    public static bool TryParseSingle(string text, out WmlMacroInvocationSyntax? invocation)
    {
        invocation = null;
        if (text.Length < 2 || text[0] != '{' || text[^1] != '}')
            return false;
        var lineStarts = BuildLineStarts(text);
        var commentMask = BuildCommentMask(text);
        if (FindMatchingBrace(text, 0, commentMask) != text.Length - 1)
            return false;
        var parseText = RemoveCommentsForParsing(text, 0, text.Length, commentMask);
        if (!TryParseInvocationSyntax(text, parseText, 0, lineStarts, out var name, out var arguments))
            return false;
        invocation = new WmlMacroInvocationSyntax(name, text,
            CreateSpan(0, text.Length, lineStarts), arguments, 0, false);
        return true;
    }

    public static IReadOnlyList<WmlMacroInvocationSyntax> Scan(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.Length == 0) return [];

        var lineStarts = BuildLineStarts(text);
        var commentMask = BuildCommentMask(text);
        var definitionLineRanges = WmlMacroDefinitionScanner.Scan(text)
            .Select(x => (x.StartLineIndex, x.EndLineIndexExclusive))
            .ToArray();
        var candidates = new List<InvocationCandidate>();

        for (var offset = 0; offset < text.Length; offset++)
        {
            if (text[offset] != '{' || commentMask[offset])
                continue;

            var close = FindMatchingBrace(text, offset, commentMask);
            if (close < 0)
                continue;

            var raw = text[offset..(close + 1)];
            var parseText = RemoveCommentsForParsing(text, offset, close + 1, commentMask);
            if (!TryParseInvocationSyntax(
                    raw,
                    parseText,
                    offset,
                    lineStarts,
                    out var name,
                    out var arguments))
                continue;

            var span = CreateSpan(offset, close + 1, lineStarts);
            candidates.Add(new InvocationCandidate(
                name,
                raw,
                span,
                arguments,
                definitionLineRanges.Any(x =>
                    span.StartLine >= x.StartLineIndex &&
                    span.StartLine < x.EndLineIndexExclusive)));
        }

        // Determine containment after scanning so adjacent expressions remain
        // siblings while invocations used inside an argument retain a parent.
        var ordered = candidates
            .OrderBy(x => x.Span.StartOffset)
            .ThenByDescending(x => x.Span.EndOffsetExclusive)
            .ToArray();
        var result = new List<WmlMacroInvocationSyntax>(ordered.Length);
        var active = new Stack<InvocationCandidate>();
        foreach (var candidate in ordered)
        {
            while (active.Count > 0 &&
                   candidate.Span.StartOffset >= active.Peek().Span.EndOffsetExclusive)
            {
                active.Pop();
            }

            while (active.Count > 0 &&
                   candidate.Span.EndOffsetExclusive > active.Peek().Span.EndOffsetExclusive)
            {
                active.Pop();
            }

            result.Add(new WmlMacroInvocationSyntax(
                candidate.Name,
                candidate.RawText,
                candidate.Span,
                candidate.Arguments,
                active.Count,
                candidate.IsInsideMacroDefinition));
            active.Push(candidate);
        }

        return result;
    }

    private static string RemoveCommentsForParsing(
        string text,
        int startOffset,
        int endOffsetExclusive,
        IReadOnlyList<bool> commentMask)
    {
        if (!Enumerable.Range(startOffset, endOffsetExclusive - startOffset)
                .Any(offset => commentMask[offset]))
        {
            return text[startOffset..endOffsetExclusive];
        }

        var characters = text[startOffset..endOffsetExclusive].ToCharArray();
        for (var offset = startOffset; offset < endOffsetExclusive; offset++)
        {
            if (commentMask[offset] && characters[offset - startOffset] is not ('\r' or '\n'))
                characters[offset - startOffset] = ' ';
        }
        return new string(characters);
    }

    private static int FindMatchingBrace(string text, int start, IReadOnlyList<bool> commentMask)
    {
        var depth = 0;
        var insideQuotedString = false;
        var insideRawString = false;
        for (var offset = start; offset < text.Length; offset++)
        {
            if (commentMask[offset])
                continue;

            var current = text[offset];
            if (insideRawString)
            {
                if (current == '>' &&
                    offset + 1 < text.Length &&
                    text[offset + 1] == '>')
                {
                    insideRawString = false;
                    offset++;
                }
                continue;
            }

            if (insideQuotedString)
            {
                if (current == '"')
                {
                    // WML escapes a quote by doubling it; backslashes are literal.
                    if (offset + 1 < text.Length && text[offset + 1] == '"')
                        offset++;
                    else
                        insideQuotedString = false;
                }
                continue;
            }

            // WML only uses double quotes and <<...>> as string delimiters.
            // An apostrophe is ordinary identifier/text content (for example
            // Gra'ritos), so it must never hide a following brace.
            if (current == '"')
            {
                insideQuotedString = true;
                continue;
            }

            if (current == '<' &&
                offset + 1 < text.Length &&
                text[offset + 1] == '<')
            {
                insideRawString = true;
                offset++;
                continue;
            }

            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current == '}' && --depth == 0)
                return offset;
        }

        return -1;
    }

    private static bool TryParseInvocationSyntax(
        string rawInvocation,
        string parseInvocation,
        int sourceStartOffset,
        IReadOnlyList<int> lineStarts,
        out string name,
        out IReadOnlyList<WmlMacroArgumentSyntax> arguments)
    {
        name = string.Empty;
        arguments = [];
        if (rawInvocation.Length < 3 ||
            rawInvocation.Length != parseInvocation.Length ||
            rawInvocation[0] != '{' ||
            rawInvocation[^1] != '}')
        {
            return false;
        }

        var contentStart = 1;
        var contentEnd = parseInvocation.Length - 1;
        while (contentStart < contentEnd && char.IsWhiteSpace(parseInvocation[contentStart]))
            contentStart++;
        while (contentEnd > contentStart && char.IsWhiteSpace(parseInvocation[contentEnd - 1]))
            contentEnd--;
        if (contentStart == contentEnd)
            return false;

        // Nested name expressions may themselves contain argument whitespace.
        // Only a delimiter outside those expressions starts the outer arguments.
        var nameEnd = FindOrdinaryArgumentEnd(parseInvocation, contentStart, contentEnd);
        if (nameEnd == contentStart)
            return false;

        name = rawInvocation[contentStart..nameEnd];

        // Wesnoth parentheses delimit any one multi-word argument and are not
        // themselves part of its value. They can directly follow the name,
        // occur between ordinary arguments, and represent an empty argument:
        // {MODIFICATIONS( {TRAIT_INTELLIGENT} {TRAIT_RESILIENT} )}
        // {ENEMY_UNIT (Wolf Rider) 18 24}
        // {FLASH_WHITE ()}
        if (LooksLikeMacroName(name) || name.Contains('{'))
        {
            // Once a regular macro name has been recognized, an unmatched
            // parenthesized argument makes the expression incomplete. Do not
            // fall back to the permissive include-path tokenizer: doing so
            // would expose misleading write-back spans for malformed WML.
            return TryReadWesnothArguments(
                rawInvocation,
                parseInvocation,
                nameEnd,
                contentEnd,
                sourceStartOffset,
                lineStarts,
                out arguments);
        }

        var tokens = ReadTopLevelTokenSpans(parseInvocation, contentStart, contentEnd);
        if (tokens.Count == 0)
            return false;

        name = rawInvocation[tokens[0].Start..tokens[0].End];
        arguments = tokens.Skip(1)
            .Select(token =>
            {
                var absoluteStart = sourceStartOffset + token.Start;
                var absoluteEnd = sourceStartOffset + token.End;
                return new WmlMacroArgumentSyntax(
                    rawInvocation[token.Start..token.End],
                    CreateSpan(absoluteStart, absoluteEnd, lineStarts));
            })
            .ToArray();
        return true;
    }

    private static bool TryReadWesnothArguments(
        string rawInvocation,
        string parseInvocation,
        int argumentsStart,
        int contentEnd,
        int sourceStartOffset,
        IReadOnlyList<int> lineStarts,
        out IReadOnlyList<WmlMacroArgumentSyntax> arguments)
    {
        var result = new List<WmlMacroArgumentSyntax>();
        var cursor = argumentsStart;
        while (cursor < contentEnd)
        {
            while (cursor < contentEnd && char.IsWhiteSpace(parseInvocation[cursor]))
                cursor++;
            if (cursor == contentEnd)
                break;

            int argumentStart;
            int argumentEnd;
            if (parseInvocation[cursor] == '(')
            {
                var closeParenthesis = FindMatchingParenthesis(parseInvocation, cursor, contentEnd);
                if (closeParenthesis < 0)
                {
                    arguments = [];
                    return false;
                }

                argumentStart = cursor + 1;
                argumentEnd = closeParenthesis;
                while (argumentStart < argumentEnd && char.IsWhiteSpace(parseInvocation[argumentStart]))
                    argumentStart++;
                while (argumentEnd > argumentStart && char.IsWhiteSpace(parseInvocation[argumentEnd - 1]))
                    argumentEnd--;
                cursor = closeParenthesis + 1;
            }
            else
            {
                argumentStart = cursor;
                argumentEnd = FindOrdinaryArgumentEnd(parseInvocation, cursor, contentEnd);
                if (argumentEnd == argumentStart)
                {
                    arguments = [];
                    return false;
                }
                cursor = argumentEnd;
            }

            var absoluteStart = sourceStartOffset + argumentStart;
            var absoluteEnd = sourceStartOffset + argumentEnd;
            result.Add(new WmlMacroArgumentSyntax(
                rawInvocation[argumentStart..argumentEnd],
                CreateSpan(absoluteStart, absoluteEnd, lineStarts)));
        }

        arguments = result;
        return true;
    }

    private static int FindOrdinaryArgumentEnd(string value, int start, int contentEnd)
    {
        var braceDepth = 0;
        var squareDepth = 0;
        var insideQuotedString = false;
        var insideRawString = false;
        for (var offset = start; offset < contentEnd; offset++)
        {
            var current = value[offset];
            if (insideRawString)
            {
                if (current == '>' &&
                    offset + 1 < contentEnd &&
                    value[offset + 1] == '>')
                {
                    insideRawString = false;
                    offset++;
                }
                continue;
            }

            if (insideQuotedString)
            {
                if (current == '"')
                {
                    if (offset + 1 < contentEnd && value[offset + 1] == '"')
                        offset++;
                    else
                        insideQuotedString = false;
                }
                continue;
            }

            if (current == '"')
            {
                insideQuotedString = true;
                continue;
            }

            if (current == '<' &&
                offset + 1 < contentEnd &&
                value[offset + 1] == '<')
            {
                insideRawString = true;
                offset++;
                continue;
            }

            switch (current)
            {
                case '{': braceDepth++; break;
                case '}': braceDepth = Math.Max(0, braceDepth - 1); break;
                case '[': squareDepth++; break;
                case ']': squareDepth = Math.Max(0, squareDepth - 1); break;
            }

            if (braceDepth == 0 &&
                squareDepth == 0 &&
                (char.IsWhiteSpace(current) || current == '('))
            {
                return offset;
            }
        }

        return contentEnd;
    }

    private static IReadOnlyList<(int Start, int End)> ReadTopLevelTokenSpans(
        string parseInvocation,
        int contentStart,
        int contentEnd)
    {
        var tokens = new List<(int Start, int End)>();
        var tokenStart = -1;
        var braceDepth = 0;
        var parenthesisDepth = 0;
        var squareDepth = 0;
        var insideQuotedString = false;
        var insideRawString = false;

        for (var offset = contentStart; offset < contentEnd; offset++)
        {
            var current = parseInvocation[offset];
            if (insideRawString)
            {
                if (current == '>' &&
                    offset + 1 < contentEnd &&
                    parseInvocation[offset + 1] == '>')
                {
                    insideRawString = false;
                    offset++;
                }
                continue;
            }

            if (insideQuotedString)
            {
                if (current == '"')
                {
                    if (offset + 1 < contentEnd && parseInvocation[offset + 1] == '"')
                        offset++;
                    else
                        insideQuotedString = false;
                }
                continue;
            }

            if (current == '"')
            {
                insideQuotedString = true;
                tokenStart = tokenStart < 0 ? offset : tokenStart;
                continue;
            }

            if (current == '<' &&
                offset + 1 < contentEnd &&
                parseInvocation[offset + 1] == '<')
            {
                insideRawString = true;
                tokenStart = tokenStart < 0 ? offset : tokenStart;
                offset++;
                continue;
            }

            switch (current)
            {
                case '{': braceDepth++; break;
                case '}': braceDepth = Math.Max(0, braceDepth - 1); break;
                case '(': parenthesisDepth++; break;
                case ')': parenthesisDepth = Math.Max(0, parenthesisDepth - 1); break;
                case '[': squareDepth++; break;
                case ']': squareDepth = Math.Max(0, squareDepth - 1); break;
            }

            if (char.IsWhiteSpace(current) &&
                braceDepth == 0 &&
                parenthesisDepth == 0 &&
                squareDepth == 0)
            {
                if (tokenStart >= 0)
                {
                    tokens.Add((tokenStart, offset));
                    tokenStart = -1;
                }
                continue;
            }

            tokenStart = tokenStart < 0 ? offset : tokenStart;
        }

        if (tokenStart >= 0)
            tokens.Add((tokenStart, contentEnd));

        return tokens;
    }

    private static int FindMatchingParenthesis(string value, int start, int contentEnd)
    {
        var depth = 0;
        var insideQuotedString = false;
        var insideRawString = false;
        for (var offset = start; offset < contentEnd; offset++)
        {
            var current = value[offset];
            if (insideRawString)
            {
                if (current == '>' &&
                    offset + 1 < contentEnd &&
                    value[offset + 1] == '>')
                {
                    insideRawString = false;
                    offset++;
                }
                continue;
            }

            if (insideQuotedString)
            {
                if (current == '"')
                {
                    if (offset + 1 < contentEnd && value[offset + 1] == '"')
                        offset++;
                    else
                        insideQuotedString = false;
                }
                continue;
            }

            if (current == '"')
            {
                insideQuotedString = true;
                continue;
            }

            if (current == '<' &&
                offset + 1 < contentEnd &&
                value[offset + 1] == '<')
            {
                insideRawString = true;
                offset++;
                continue;
            }

            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current == ')' && --depth == 0)
                return offset;
        }

        return -1;
    }

    private static bool LooksLikeMacroName(string value)
    {
        return WmlPreprocessorToken.IsLiteralName(value);
    }

    private static bool[] BuildCommentMask(string text)
    {
        var result = new bool[text.Length];
        var insideQuotedString = false;
        var insideRawString = false;
        for (var offset = 0; offset < text.Length; offset++)
        {
            var current = text[offset];
            if (insideRawString)
            {
                if (current == '>' &&
                    offset + 1 < text.Length &&
                    text[offset + 1] == '>')
                {
                    insideRawString = false;
                    offset++;
                }
                continue;
            }

            if (insideQuotedString)
            {
                if (current == '"')
                {
                    if (offset + 1 < text.Length && text[offset + 1] == '"')
                        offset++;
                    else
                        insideQuotedString = false;
                }
                continue;
            }

            if (current == '"')
            {
                insideQuotedString = true;
                continue;
            }

            if (current == '<' &&
                offset + 1 < text.Length &&
                text[offset + 1] == '<')
            {
                insideRawString = true;
                offset++;
                continue;
            }

            if (current != '#')
                continue;

            while (offset < text.Length && text[offset] is not ('\r' or '\n'))
            {
                result[offset] = true;
                offset++;
            }

        }

        return result;
    }

    private static IReadOnlyList<int> BuildLineStarts(string text)
    {
        var result = new List<int> { 0 };
        for (var offset = 0; offset < text.Length; offset++)
        {
            if (text[offset] == '\n')
                result.Add(offset + 1);
        }
        return result;
    }

    private static WmlTextSpan CreateSpan(
        int startOffset,
        int endOffsetExclusive,
        IReadOnlyList<int> lineStarts)
    {
        var startLine = FindLine(lineStarts, startOffset);
        var endLine = FindLine(lineStarts, Math.Max(startOffset, endOffsetExclusive - 1));
        return new WmlTextSpan(
            startOffset,
            endOffsetExclusive,
            startLine,
            startOffset - lineStarts[startLine],
            endLine,
            endOffsetExclusive - lineStarts[endLine]);
    }

    private static int FindLine(IReadOnlyList<int> lineStarts, int offset)
    {
        var low = 0;
        var high = lineStarts.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (lineStarts[middle] <= offset)
            {
                if (middle == lineStarts.Count - 1 || lineStarts[middle + 1] > offset)
                    return middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return 0;
    }

    private sealed record InvocationCandidate(
        string Name,
        string RawText,
        WmlTextSpan Span,
        IReadOnlyList<WmlMacroArgumentSyntax> Arguments,
        bool IsInsideMacroDefinition);
}
