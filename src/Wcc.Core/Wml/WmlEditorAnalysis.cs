namespace Wcc.Core.Wml;

public enum WmlEditorTokenKind
{
    Tag,
    AttributeName,
    String,
    Comment,
    Directive,
    MacroInvocation,
    Number,
    RawCode
}

public enum WmlEditorReferenceKind
{
    Macro,
    Include,
    Computed
}

/// <summary>UTF-16 offsets into the exact supplied document, including its original line endings.</summary>
public sealed record WmlEditorToken(WmlEditorTokenKind Kind, int StartOffset, int Length);

/// <summary>A balanced multiline presentation region, from its opening token through its closing token.</summary>
public sealed record WmlEditorFold(int StartOffset, int EndOffsetExclusive, string Label);

/// <summary>A lexical occurrence. Its kind is a navigation hint, not resolved definition or write authority.</summary>
public sealed record WmlEditorReference(
    WmlEditorReferenceKind Kind,
    string Name,
    string RawText,
    int StartOffset,
    int Length);

/// <summary>
/// One tag-shaped source occurrence. A partner index is a lexical presentation hint,
/// never proof of runtime WML structure; macros and preprocessing may supply other tags.
/// </summary>
public sealed record WmlEditorTagOccurrence(string Name, bool IsClosing, int StartOffset, int Length)
{
    public int? MatchingTagIndex { get; init; }
    public bool IsComplete { get; init; } = true;
    /// <summary>A physically closed header whose name contains unevaluated substitutions.</summary>
    public bool IsComputedName { get; init; }
}

/// <summary>An exact quoted region already identified by the shared lexer, including its delimiters when present.</summary>
public sealed record WmlEditorQuotedRegion(int StartOffset, int EndOffsetExclusive);

/// <summary>An invocation region with the closing-brace result from the existing lexical traversal.</summary>
public sealed record WmlEditorInvocationRegion(int StartOffset, int EndOffsetExclusive, bool HasClosingBrace);

/// <summary>
/// Lossless presentation analysis for authored and partially edited WML. It does not evaluate
/// preprocessor branches, infer macro argument ownership, validate a campaign, or authorize writes.
/// Tokens are ordered and non-overlapping; nested invocation occurrences remain separate references.
/// </summary>
public sealed record WmlEditorAnalysis(
    IReadOnlyList<WmlEditorToken> Tokens,
    IReadOnlyList<WmlEditorFold> Folds,
    IReadOnlyList<WmlEditorReference> References)
{
    public IReadOnlyList<WmlEditorTagOccurrence> TagOccurrences { get; init; } = [];
    public IReadOnlyList<WmlEditorQuotedRegion> QuotedRegions { get; init; } = [];
    public IReadOnlyList<WmlEditorInvocationRegion> InvocationRegions { get; init; } = [];

    /// <summary>
    /// Finds a tag containing the caret in the existing snapshot without rescanning source.
    /// The position immediately after its closing bracket is included; an adjacent next tag wins.
    /// </summary>
    public WmlEditorTagOccurrence? FindTagAtCaret(int offset)
    {
        if (offset < 0) return null;
        var low = 0;
        var high = TagOccurrences.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (TagOccurrences[middle].StartOffset <= offset) low = middle + 1;
            else high = middle;
        }
        if (low == 0) return null;
        var occurrence = TagOccurrences[low - 1];
        return offset - occurrence.StartOffset <= occurrence.Length ? occurrence : null;
    }

    public static WmlEditorAnalysis Build(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length == 0)
            return new([], [], []);

        // The shared invocation scanner intentionally finds quoted WML substitutions. Raw code is
        // opaque to this editor: Lua table literals must not become apparent WML macro references.
        var referenceText = MaskRawContent(text, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var references = WmlMacroInvocationSyntaxScanner.Scan(referenceText)
            .Select(invocation => new WmlEditorReference(
                ClassifyReference(invocation.Name), invocation.Name,
                text[invocation.Span.StartOffset..invocation.Span.EndOffsetExclusive],
                invocation.Span.StartOffset, invocation.Span.Length))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var tokens = new List<WmlEditorToken>();
        var folds = new List<WmlEditorFold>();
        var tagOccurrences = new List<WmlEditorTagOccurrence>();
        var quotedRegions = new List<WmlEditorQuotedRegion>();
        var invocationRegions = new List<WmlEditorInvocationRegion>();
        var tags = new List<TagOpening>();
        var regions = new List<DirectiveOpening>();
        var scope = 0;
        var nextScope = 0;
        var lineStart = 0;
        var previousTagEnd = -1;

        for (var offset = 0; offset < text.Length;)
        {
            ObserveCancellation(offset, cancellationToken);
            var current = text[offset];
            if (current is '\r' or '\n')
            {
                if (current == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n')
                    offset++;
                lineStart = ++offset;
                previousTagEnd = -1;
                continue;
            }

            if (current == '"')
            {
                var end = ReadQuotedEnd(text, offset, cancellationToken);
                quotedRegions.Add(new(offset, end));
                AddQuotedTokens(tokens, references, offset, end);
                UpdateLineStart(text, offset, end, ref lineStart);
                offset = end;
                continue;
            }
            if (StartsRaw(text, offset))
            {
                var end = ReadRawEnd(text, offset, cancellationToken);
                tokens.Add(new(WmlEditorTokenKind.RawCode, offset, end - offset));
                UpdateLineStart(text, offset, end, ref lineStart);
                offset = end;
                continue;
            }
            if (current == '{')
            {
                // An argument may contain arbitrary WML. Even an incomplete/invalid invocation
                // remains opaque for folds instead of borrowing its tags for an outer owner.
                var end = ReadBraceEnd(text, offset, cancellationToken, out var hasClosingBrace);
                invocationRegions.Add(new(offset, end, hasClosingBrace));
                tokens.Add(new(WmlEditorTokenKind.MacroInvocation, offset, end - offset));
                UpdateLineStart(text, offset, end, ref lineStart);
                offset = end;
                continue;
            }
            if (current == '#')
            {
                var end = ReadLineEnd(text, offset);
                var name = ReadDirectiveName(text, offset, end);
                var atLineStart = IsWhitespace(text, lineStart, offset);
                var inlineEnd = name == "enddef" && regions.Any(region => region.Kind == "define");
                var isDirective = name is not null && (atLineStart || inlineEnd);
                tokens.Add(new(isDirective ? WmlEditorTokenKind.Directive : WmlEditorTokenKind.Comment,
                    offset, end - offset));
                if (isDirective)
                    ProcessDirective(text, offset, end, name!, regions, tags, folds, ref scope, ref nextScope);
                offset = end;
                continue;
            }
            if (current == '[' &&
                (IsWhitespace(text, lineStart, offset) ||
                 (previousTagEnd >= lineStart && IsWhitespace(text, previousTagEnd, offset))) &&
                TryReadTag(text, offset, cancellationToken, out var tagName, out var closing, out var tagEnd,
                    out var computedName, out var tagInvocations))
            {
                tokens.Add(new(WmlEditorTokenKind.Tag, offset, tagEnd - offset));
                var occurrenceIndex = tagOccurrences.Count;
                tagOccurrences.Add(new(tagName, closing, offset, tagEnd - offset)
                    { IsComputedName = computedName });
                if (computedName)
                {
                    // A substitution can produce more than a literal name. Retain its
                    // exact physical header and references without inventing a partner,
                    // or pairing an older literal owner through this uncertain boundary.
                    foreach (var opening in tags) opening.PairValid = false;
                    invocationRegions.AddRange(tagInvocations!);
                    UpdateLineStart(text, offset, tagEnd, ref lineStart);
                }
                else if (!closing)
                    tags.Add(new(tagName, offset, scope, occurrenceIndex));
                else if (tags.Count > 0 && tags[^1].Name == tagName && tags[^1].Scope == scope)
                {
                    var opening = tags[^1];
                    tags.RemoveAt(tags.Count - 1);
                    if (opening.Valid)
                    {
                        if (opening.PairValid)
                        {
                            tagOccurrences[opening.OccurrenceIndex] = tagOccurrences[opening.OccurrenceIndex]
                                with { MatchingTagIndex = occurrenceIndex };
                            tagOccurrences[occurrenceIndex] = tagOccurrences[occurrenceIndex]
                                with { MatchingTagIndex = opening.OccurrenceIndex };
                        }
                        AddFold(text, folds, opening.StartOffset, tagEnd, "[" + opening.Name + "]");
                    }
                }
                else
                {
                    // Do not match across a malformed tag or a conditional/definition boundary.
                    foreach (var opening in tags)
                        opening.Valid = false;
                }
                previousTagEnd = tagEnd;
                offset = tagEnd;
                continue;
            }
            if (current == '[' &&
                (IsWhitespace(text, lineStart, offset) ||
                 (previousTagEnd >= lineStart && IsWhitespace(text, previousTagEnd, offset))) &&
                TryReadIncompleteTag(text, offset, out var incompleteName, out var incompleteClosing, out var incompleteEnd))
            {
                tagOccurrences.Add(new(incompleteName, incompleteClosing, offset, incompleteEnd - offset)
                    { IsComplete = false });
                // Keep established fold behavior while declining to highlight a parent partner
                // through a malformed tag that may be an unfinished nested owner.
                foreach (var opening in tags) opening.PairValid = false;
            }
            if (IsNameStart(current))
            {
                var end = offset + 1;
                while (end < text.Length && IsNamePart(text[end])) end++;
                var namesEnd = end;
                var equals = SkipHorizontalWhitespace(text, end);
                while (equals < text.Length && text[equals] == ',')
                {
                    var next = SkipHorizontalWhitespace(text, equals + 1);
                    if (next >= text.Length || !IsNameStart(text[next])) break;
                    next++;
                    while (next < text.Length && IsNamePart(text[next])) next++;
                    namesEnd = next;
                    equals = SkipHorizontalWhitespace(text, next);
                }
                if (equals < text.Length && text[equals] == '=')
                {
                    tokens.Add(new(WmlEditorTokenKind.AttributeName, offset, namesEnd - offset));
                    offset = namesEnd;
                }
                else
                    offset = end;
                continue;
            }
            if (TryReadNumber(text, offset, out var numberEnd))
            {
                tokens.Add(new(WmlEditorTokenKind.Number, offset, numberEnd - offset));
                offset = numberEnd;
                continue;
            }
            offset++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(tokens.ToArray(), folds.OrderBy(fold => fold.StartOffset)
            .ThenByDescending(fold => fold.EndOffsetExclusive).ToArray(), references)
            {
                TagOccurrences = Array.AsReadOnly(tagOccurrences.ToArray()),
                QuotedRegions = Array.AsReadOnly(quotedRegions.ToArray()),
                InvocationRegions = Array.AsReadOnly(invocationRegions.ToArray())
            };
    }

    /// <summary>
    /// Reads only tag headers from text whose strings, comments and macro payloads
    /// have already been masked without changing offsets. No presentation tokens,
    /// folds, references or pairing authority are produced. Null means that the
    /// bounded tag inventory was exceeded.
    /// </summary>
    internal static IReadOnlyList<WmlEditorTagOccurrence>? ReadMaskedTagOccurrences(
        string masked, int maximumTags, CancellationToken cancellationToken, Action chargeWork)
    {
        ArgumentNullException.ThrowIfNull(masked);
        ArgumentNullException.ThrowIfNull(chargeWork);
        if (maximumTags <= 0) throw new ArgumentOutOfRangeException(nameof(maximumTags));
        cancellationToken.ThrowIfCancellationRequested();
        // Charge every input block before lexing, including characters that the
        // existing tag readers advance over in one operation.
        for (long offset = 0; offset < masked.Length; offset += 1024)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chargeWork();
        }
        var tags = new List<WmlEditorTagOccurrence>();
        var statementPrefix = true;
        for (var offset = 0; offset < masked.Length;)
        {
            ObserveCancellation(offset, cancellationToken);
            var character = masked[offset];
            if (character is '\r' or '\n')
            { statementPrefix = true; offset++; continue; }
            if (char.IsWhiteSpace(character) || character == '\uFEFF')
            { offset++; continue; }
            if (statementPrefix && character == '[')
            {
                if (TryReadTag(masked, offset, cancellationToken, out var name, out var closing,
                    out var end, out var computed, out _))
                {
                    if (!Add(new(name, closing, offset, end - offset) { IsComputedName = computed })) return null;
                    offset = end;
                    continue; // A complete header permits an adjacent header.
                }
                if (TryReadIncompleteTag(masked, offset, out name, out closing, out end))
                {
                    if (!Add(new(name, closing, offset, end - offset) { IsComplete = false })) return null;
                    offset = end;
                    statementPrefix = false;
                    continue;
                }
            }
            statementPrefix = false;
            offset++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(tags.ToArray());

        bool Add(WmlEditorTagOccurrence tag)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chargeWork();
            if (tags.Count >= maximumTags) return false;
            tags.Add(tag);
            return true;
        }
    }

    private static string MaskRawContent(string text, CancellationToken cancellationToken)
    {
        char[]? masked = null;
        for (var offset = 0; offset < text.Length;)
        {
            ObserveCancellation(offset, cancellationToken);
            if (text[offset] == '#')
                offset = ReadLineEnd(text, offset);
            else if (text[offset] == '"')
                offset = ReadQuotedEnd(text, offset, cancellationToken);
            else if (StartsRaw(text, offset))
            {
                var end = ReadRawEnd(text, offset, cancellationToken);
                masked ??= text.ToCharArray();
                var contentEnd = end >= offset + 4 && text[end - 2] == '>' && text[end - 1] == '>'
                    ? end - 2 : end;
                for (var cursor = offset + 2; cursor < contentEnd; cursor++)
                {
                    ObserveCancellation(cursor, cancellationToken);
                    if (masked[cursor] is not ('\r' or '\n')) masked[cursor] = ' ';
                }
                offset = end;
            }
            else
                offset++;
        }
        return masked is null ? text : new string(masked);
    }

    private static WmlEditorReferenceKind ClassifyReference(string name)
    {
        if (name.Contains('{') || name.Contains('$')) return WmlEditorReferenceKind.Computed;
        return name.StartsWith('~') || name.StartsWith('.') || name.Contains('/') || name.Contains('\\') ||
               name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
            ? WmlEditorReferenceKind.Include : WmlEditorReferenceKind.Macro;
    }

    private static void AddQuotedTokens(List<WmlEditorToken> tokens,
        IReadOnlyList<WmlEditorReference> references, int start, int end)
    {
        // Find only occurrences wholly inside this quoted region, preserving sorted disjoint spans.
        var low = 0;
        var high = references.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (references[middle].StartOffset < start) low = middle + 1;
            else high = middle;
        }
        var cursor = start;
        for (var index = low; index < references.Count && references[index].StartOffset < end; index++)
        {
            var reference = references[index];
            var referenceEnd = reference.StartOffset + reference.Length;
            if (reference.StartOffset < cursor || referenceEnd > end) continue;
            if (reference.StartOffset > cursor)
                tokens.Add(new(WmlEditorTokenKind.String, cursor, reference.StartOffset - cursor));
            tokens.Add(new(WmlEditorTokenKind.MacroInvocation, reference.StartOffset, reference.Length));
            cursor = referenceEnd;
        }
        if (cursor < end) tokens.Add(new(WmlEditorTokenKind.String, cursor, end - cursor));
    }

    private static void ProcessDirective(string text, int start, int end, string name,
        List<DirectiveOpening> regions, List<TagOpening> tags, List<WmlEditorFold> folds,
        ref int scope, ref int nextScope)
    {
        if (name == "define" || WmlAuthoredStructureScanner.IsConditionalStart(name))
        {
            var payload = text[(start + name.Length + 1)..end].Trim();
            var valid = payload.Length > 0 && !payload.StartsWith('#');
            if (name == "define")
            {
                var nameEnd = 0;
                while (nameEnd < payload.Length && !char.IsWhiteSpace(payload[nameEnd])) nameEnd++;
                valid = valid && WmlPreprocessorToken.IsLiteralName(payload[..nameEnd]);
            }
            regions.Add(new(name == "define" ? "define" : "conditional", start, scope, tags.Count, valid));
            scope = ++nextScope;
            return;
        }
        if (name == "else")
        {
            if (regions.Count > 0 && regions[^1].Kind == "conditional")
            {
                var region = regions[^1];
                if (region.HasElse) region.Valid = false;
                region.HasElse = true;
                DiscardScopedTags(tags, region.TagBaseCount);
                scope = ++nextScope;
            }
            else
                foreach (var region in regions) region.Valid = false;
            return;
        }
        if (name is not ("endif" or "enddef")) return;
        var expected = name == "enddef" ? "define" : "conditional";
        var match = regions.FindLastIndex(region => region.Kind == expected);
        if (match < 0)
        {
            foreach (var region in regions) region.Valid = false;
            foreach (var tag in tags) tag.Valid = false;
            return;
        }
        if (match != regions.Count - 1)
        {
            foreach (var region in regions) region.Valid = false;
            foreach (var tag in tags) tag.Valid = false;
        }
        var opening = regions[match];
        DiscardScopedTags(tags, opening.TagBaseCount);
        regions.RemoveRange(match, regions.Count - match);
        scope = opening.ParentScope;
        if (opening.Valid)
            AddFold(text, folds, opening.StartOffset, end, text[opening.StartOffset..ReadLineEnd(text, opening.StartOffset)].Trim());
    }

    private static void DiscardScopedTags(List<TagOpening> tags, int baseCount)
    {
        if (tags.Count > baseCount) tags.RemoveRange(baseCount, tags.Count - baseCount);
    }

    private static void AddFold(string text, List<WmlEditorFold> folds, int start, int end, string label)
    {
        if (text.AsSpan(start, end - start).IndexOfAny('\r', '\n') >= 0)
            folds.Add(new(start, end, label));
    }

    private static string? ReadDirectiveName(string text, int start, int end)
    {
        var cursor = start + 1;
        while (cursor < end && char.IsLetter(text[cursor])) cursor++;
        if (cursor == start + 1 || (cursor < end && !char.IsWhiteSpace(text[cursor]))) return null;
        var name = text[(start + 1)..cursor];
        return name is "define" or "enddef" or "arg" or "endarg" or "else" or "endif" or "textdomain" or "undef" or
            "deprecated" or "warning" or "error" || WmlAuthoredStructureScanner.IsConditionalStart(name)
            ? name : null;
    }

    private static bool TryReadTag(string text, int start, CancellationToken cancellationToken,
        out string name, out bool closing, out int end, out bool computedName,
        out List<WmlEditorInvocationRegion>? invocations)
    {
        name = string.Empty;
        closing = false;
        end = start;
        computedName = false;
        invocations = null;
        var cursor = SkipHorizontalWhitespace(text, start + 1);
        if (cursor < text.Length && text[cursor] is '/' or '+')
        {
            closing = text[cursor] == '/';
            cursor = SkipHorizontalWhitespace(text, cursor + 1);
        }
        if (cursor >= text.Length || !(IsNameStart(text[cursor]) || text[cursor] == '{')) return false;
        var nameStart = cursor;
        while (cursor < text.Length)
        {
            ObserveCancellation(cursor, cancellationToken);
            if (IsNamePart(text[cursor])) cursor++;
            else if (text[cursor] == '{')
            {
                // Reuse the same opaque brace traversal as ordinary invocations;
                // braces/quotes/brackets inside arguments are not tag delimiters.
                var invocationEnd = ReadBraceEnd(text, cursor, cancellationToken, out var hasClosingBrace);
                if (!hasClosingBrace) return false;
                invocations ??= [];
                invocations.Add(new(cursor, invocationEnd, HasClosingBrace: true));
                computedName = true;
                cursor = invocationEnd;
            }
            else break;
        }
        name = text[nameStart..cursor];
        cursor = SkipHorizontalWhitespace(text, cursor);
        if (cursor >= text.Length || text[cursor] != ']') return false;
        end = cursor + 1;
        return true;
    }

    private static bool TryReadIncompleteTag(string text, int start, out string name, out bool closing, out int end)
    {
        name = string.Empty;
        closing = false;
        end = start;
        var cursor = SkipHorizontalWhitespace(text, start + 1);
        if (cursor < text.Length && text[cursor] is '/' or '+')
        {
            closing = text[cursor] == '/';
            cursor = SkipHorizontalWhitespace(text, cursor + 1);
        }
        if (cursor >= text.Length || !IsNameStart(text[cursor])) return false;
        var nameStart = cursor++;
        while (cursor < text.Length && IsNamePart(text[cursor])) cursor++;
        name = text[nameStart..cursor];
        cursor = SkipHorizontalWhitespace(text, cursor);
        if (cursor < text.Length && text[cursor] == ']') return false;
        // Keep only the attempted literal header. A comment, quote, raw-code block or macro
        // after this prefix must not become part of the caret's apparent tag source span.
        end = cursor;
        return end > start;
    }

    private static bool TryReadNumber(string text, int start, out int end)
    {
        end = start;
        if (start > 0 && (IsNamePart(text[start - 1]) || text[start - 1] == '.')) return false;
        var cursor = start;
        if (text[cursor] is '+' or '-') cursor++;
        if (cursor >= text.Length || !char.IsAsciiDigit(text[cursor])) return false;
        while (cursor < text.Length && char.IsAsciiDigit(text[cursor])) cursor++;
        if (cursor + 1 < text.Length && text[cursor] == '.' && char.IsAsciiDigit(text[cursor + 1]))
        {
            cursor++;
            while (cursor < text.Length && char.IsAsciiDigit(text[cursor])) cursor++;
        }
        if (cursor < text.Length && (IsNamePart(text[cursor]) || text[cursor] == '.')) return false;
        end = cursor;
        return true;
    }

    private static int ReadQuotedEnd(string text, int start, CancellationToken cancellationToken)
    {
        for (var cursor = start + 1; cursor < text.Length; cursor++)
        {
            ObserveCancellation(cursor, cancellationToken);
            if (text[cursor] != '"') continue;
            if (cursor + 1 < text.Length && text[cursor + 1] == '"') cursor++;
            else return cursor + 1;
        }
        return text.Length;
    }

    private static int ReadRawEnd(string text, int start, CancellationToken cancellationToken)
    {
        for (var cursor = start + 2; cursor + 1 < text.Length; cursor++)
        {
            ObserveCancellation(cursor, cancellationToken);
            if (text[cursor] == '>' && text[cursor + 1] == '>') return cursor + 2;
        }
        return text.Length;
    }

    private static int ReadBraceEnd(string text, int start, CancellationToken cancellationToken, out bool hasClosingBrace)
    {
        hasClosingBrace = false;
        var depth = 0;
        for (var cursor = start; cursor < text.Length; cursor++)
        {
            ObserveCancellation(cursor, cancellationToken);
            if (text[cursor] == '"') cursor = ReadQuotedEnd(text, cursor, cancellationToken) - 1;
            else if (StartsRaw(text, cursor)) cursor = ReadRawEnd(text, cursor, cancellationToken) - 1;
            else if (text[cursor] == '#') cursor = ReadLineEnd(text, cursor) - 1;
            else if (text[cursor] == '{') depth++;
            else if (text[cursor] == '}' && --depth == 0)
            {
                hasClosingBrace = true;
                return cursor + 1;
            }
        }
        return text.Length;
    }

    private static int ReadLineEnd(string text, int start)
    {
        var cursor = start;
        while (cursor < text.Length && text[cursor] is not ('\r' or '\n')) cursor++;
        return cursor;
    }

    private static void UpdateLineStart(string text, int start, int end, ref int lineStart)
    {
        for (var cursor = end - 1; cursor >= start; cursor--)
        {
            if (text[cursor] is not ('\r' or '\n')) continue;
            lineStart = cursor + 1;
            return;
        }
    }

    private static bool IsWhitespace(string text, int start, int end)
    {
        for (var cursor = start; cursor < end; cursor++)
            if (!char.IsWhiteSpace(text[cursor]) && text[cursor] != '\uFEFF') return false;
        return true;
    }

    private static int SkipHorizontalWhitespace(string text, int start)
    {
        while (start < text.Length && text[start] is ' ' or '\t') start++;
        return start;
    }

    private static bool StartsRaw(string text, int offset)
        => text[offset] == '<' && offset + 1 < text.Length && text[offset + 1] == '<';

    private static bool IsNameStart(char character) => char.IsAsciiLetter(character) || character == '_';
    private static bool IsNamePart(char character) => IsNameStart(character) || char.IsAsciiDigit(character);

    private static void ObserveCancellation(int offset, CancellationToken cancellationToken)
    {
        if ((offset & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class TagOpening(string name, int startOffset, int scope, int occurrenceIndex)
    {
        public string Name { get; } = name;
        public int StartOffset { get; } = startOffset;
        public int Scope { get; } = scope;
        public int OccurrenceIndex { get; } = occurrenceIndex;
        public bool Valid { get; set; } = true;
        public bool PairValid { get; set; } = true;
    }

    private sealed class DirectiveOpening(string kind, int startOffset, int parentScope, int tagBaseCount, bool valid)
    {
        public string Kind { get; } = kind;
        public int StartOffset { get; } = startOffset;
        public int ParentScope { get; } = parentScope;
        public int TagBaseCount { get; } = tagBaseCount;
        public bool Valid { get; set; } = valid;
        public bool HasElse { get; set; }
    }
}
