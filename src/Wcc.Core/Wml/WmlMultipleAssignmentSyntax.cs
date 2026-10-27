using System.Diagnostics.CodeAnalysis;

namespace Wcc.Core.Wml;

/// <summary>Exact ordered physical component; syntax is not edit authority.</summary>
public sealed record WmlMultipleAssignmentComponent(string Key, int KeyOffset, int KeyLength,
    int ValueOffset, int ValueLength, bool IsImplicitEmpty);

/// <summary>One bounded assignment, with offsets relative to Text. StartOffset
/// locates Text in the original input when read through TryReadAt.</summary>
public sealed class WmlMultipleAssignment
{
    internal WmlMultipleAssignment(string text, int startOffset, int equalsOffset,
        int valueEndOffset, int? commentOffset, IReadOnlyList<WmlMultipleAssignmentComponent> components)
    {
        Text = text; StartOffset = startOffset; EqualsOffset = equalsOffset;
        ValueEndOffset = valueEndOffset; CommentOffset = commentOffset;
        Components = Array.AsReadOnly(components.ToArray());
        Keys = Array.AsReadOnly(components.Select(component => component.Key).ToArray());
    }
    public string Text { get; }
    public int StartOffset { get; }
    public int EndOffsetExclusive => StartOffset + Text.Length;
    public int EqualsOffset { get; }
    public int ValueEndOffset { get; }
    public int SuffixOffset => ValueEndOffset;
    public int? CommentOffset { get; }
    public string TrailingSuffix => Text[SuffixOffset..];
    public IReadOnlyList<string> Keys { get; }
    public IReadOnlyList<WmlMultipleAssignmentComponent> Components { get; }
}

/// <summary>
/// Ordered WML assignment syntax shared by projection, checking and physical source
/// editing. Only the first key-count minus one unprotected commas distribute values;
/// the last key receives the remainder and missing values are explicit empty results.
/// Quoted/raw/macro token bounds come from the existing lexer. This neither expands
/// macros nor validates a field, source mapping, owner or requested edit.
/// </summary>
public static class WmlMultipleAssignmentSyntax
{
    private const int MaximumKeys = 64;

    public static bool TryRead(string text, [NotNullWhen(true)] out WmlMultipleAssignment? assignment,
        out string? problem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryReadAt(text, 0, out assignment, out problem, cancellationToken)) return false;
        if (!OnlySyntaxWhitespace(text.AsSpan(assignment.EndOffsetExclusive)))
        { assignment = null; problem = "The supplied text contains more than one bounded assignment."; return false; }
        if (assignment.Text.Length != text.Length)
            assignment = new(text, 0, assignment.EqualsOffset, assignment.ValueEndOffset,
                assignment.CommentOffset, assignment.Components);
        return true;
    }

    public static bool TryReadAt(string text, int offset,
        [NotNullWhen(true)] out WmlMultipleAssignment? assignment, out string? problem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        assignment = null; problem = null;
        if (offset < 0 || offset >= text.Length)
        { problem = "Choose an existing assignment source offset."; return false; }
        if (!FindBounds(text, offset, out var codeEnd, out var statementEnd, out var comment, out problem, cancellationToken)) return false;
        var equals = text.IndexOf('=', offset, codeEnd - offset);
        if (equals < offset + 1) { problem = "The bounded source has no assignment operator."; return false; }
        var keys = new List<(string Key, int Offset, int Length)>();
        var keyStart = offset;
        for (var cursor = offset; cursor <= equals; cursor++)
        {
            if (cursor != equals && text[cursor] != ',') continue;
            var from = keyStart; var to = cursor;
            while (from < to && text[from] is ' ' or '\t') from++;
            while (to > from && text[to - 1] is ' ' or '\t') to--;
            var key = text[from..to];
            if (!IsKey(key))
            { problem = "Use ordered literal WML attribute names."; return false; }
            keys.Add((key, from - offset, to - from));
            if (keys.Count > MaximumKeys) { problem = "The assignment exceeds the bounded key count."; return false; }
            keyStart = cursor + 1;
        }
        var valueEnd = codeEnd;
        while (valueEnd > equals + 1 && IsSyntaxWhitespace(text[valueEnd - 1])) valueEnd--;
        var values = new List<(int Start, int Length, bool Implicit)>();
        var componentStart = equals + 1;
        for (var cursor = componentStart; cursor < valueEnd;)
        {
            if ((cursor & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (WmlEditorAnalysis.TryReadProtectedValueRegion(text, cursor, out var protectedEnd, out var complete, cancellationToken))
            {
                if (!complete || protectedEnd > valueEnd) { problem = "A protected assignment value token is not closed."; return false; }
                cursor = protectedEnd; continue;
            }
            if (text[cursor] == '#')
            { cursor = PhysicalLineEnd(text, cursor); continue; }
            if (text[cursor] == ',' && values.Count < keys.Count - 1)
            { AddValue(componentStart, cursor); componentStart = cursor + 1; }
            cursor++;
        }
        AddValue(componentStart, valueEnd);
        while (values.Count < keys.Count) values.Add((valueEnd, 0, true));
        var components = keys.Select((key, index) => new WmlMultipleAssignmentComponent(key.Key, key.Offset, key.Length,
            values[index].Start - offset, values[index].Length, values[index].Implicit)).ToArray();
        assignment = new(text[offset..statementEnd], offset, equals - offset, valueEnd - offset,
            comment is { } commentOffset ? commentOffset - offset : null, components);
        return true;

        void AddValue(int from, int to)
        {
            while (from < to && IsSyntaxWhitespace(text[from])) from++;
            while (to > from && IsSyntaxWhitespace(text[to - 1])) to--;
            values.Add((from, to - from, false));
        }
    }

    private static bool FindBounds(string text, int start, out int codeEnd, out int statementEnd,
        out int? commentOffset, out string? problem, CancellationToken cancellationToken)
    {
        codeEnd = statementEnd = text.Length; commentOffset = null; problem = null;
        var previous = '\0';
        for (var cursor = start; cursor < text.Length;)
        {
            if ((cursor & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (WmlEditorAnalysis.TryReadProtectedValueRegion(text, cursor, out var tokenEnd, out var complete, cancellationToken))
            {
                if (!complete) { problem = "An assignment contains an unclosed quoted, raw or macro token."; return false; }
                previous = text[tokenEnd - 1]; cursor = tokenEnd; continue;
            }
            if (text[cursor] is '#' or '\r' or '\n')
            {
                if (previous == '+' && FindContinuedOperand(text, cursor, cancellationToken) is { } operand)
                { cursor = operand; continue; }
                codeEnd = cursor;
                statementEnd = text[cursor] == '#' ? PhysicalLineEnd(text, cursor) : cursor;
                if (text[cursor] == '#') commentOffset = cursor;
                return true;
            }
            if (!IsSyntaxWhitespace(text[cursor])) previous = text[cursor];
            cursor++;
        }
        return true;
    }

    internal static int? FindContinuedOperand(string text, int offset, CancellationToken cancellationToken)
    {
        var cursor = offset;
        while (cursor < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (cursor < text.Length && IsSyntaxWhitespace(text[cursor])) cursor++;
            if (cursor == text.Length || text[cursor] != '#') break;
            var end = PhysicalLineEnd(text, cursor);
            // Existing statement policy permits ordinary comments and textdomain
            // metadata between literal operands, never conditional/control code.
            var wordEnd = cursor + 1;
            while (wordEnd < end && char.IsAsciiLetter(text[wordEnd])) wordEnd++;
            var directive = text[(cursor + 1)..wordEnd];
            if (directive is "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver" or
                "define" or "undef" or "else" or "endif" or "enddef" or "arg" or "endarg" or "error" or "warning" or "deprecated") return null;
            cursor = end;
        }
        if (cursor < text.Length && text[cursor] == '_')
        { cursor++; while (cursor < text.Length && text[cursor] is ' ' or '\t') cursor++; }
        if (cursor >= text.Length || text[cursor] is '[' or ']' or '#') return null;
        var nextLineEnd = PhysicalLineEnd(text, cursor);
        var nextEquals = text.IndexOf('=', cursor, nextLineEnd - cursor);
        if (nextEquals > cursor && text[cursor..nextEquals].Split(',')
                .All(key => IsKey(key.Trim(' ', '\t')))) return null;
        if (WmlEditorAnalysis.TryReadProtectedValueRegion(text, cursor, out _, out var complete, cancellationToken))
            return complete ? cursor : null;
        // Native WML also joins unquoted operands across a continuation. Do not
        // borrow a following assignment/tag as that operand when it is malformed.
        for (var end = cursor; end < text.Length && text[end] is not ('\r' or '\n' or '#' or ',' or '+'); end++)
            if (text[end] is '=' or '[' or ']') return null;
        return cursor;
    }

    private static bool IsKey(string key) => key.Length != 0 &&
        key.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static bool IsSyntaxWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n';
    private static bool OnlySyntaxWhitespace(ReadOnlySpan<char> text)
    {
        foreach (var character in text) if (!IsSyntaxWhitespace(character)) return false;
        return true;
    }
    private static int PhysicalLineEnd(string text, int offset)
    {
        while (offset < text.Length && text[offset] is not ('\r' or '\n')) offset++;
        return offset;
    }
}
