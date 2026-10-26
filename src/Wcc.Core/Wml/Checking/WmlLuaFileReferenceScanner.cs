using System.Text;

namespace Wcc.Core.Wml.Checking;

/// <summary>Offsets address the original scanner input, including an optional WML raw-code wrapper.</summary>
public sealed record WmlLuaFileReference(string? Filename, bool IsRequire, int StartOffset, int Length);

/// <summary>
/// Bounded lexical extraction of literal wesnoth.require/dofile arguments. No Lua execution,
/// scope inference, syntax validation, alias resolution or general dependency graph is attempted.
/// Strings and comments are skipped as payloads so example calls inside them are never references.
/// </summary>
public static class WmlLuaFileReferenceScanner
{
    public static IReadOnlyList<WmlLuaFileReference> Find(string rawCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rawCode);
        cancellationToken.ThrowIfCancellationRequested();
        var code = rawCode;
        var codeOffset = 0;
        var trimmedStart = 0;
        var trimmedEnd = rawCode.Length;
        while (trimmedStart < trimmedEnd && char.IsWhiteSpace(rawCode[trimmedStart])) trimmedStart++;
        while (trimmedEnd > trimmedStart && char.IsWhiteSpace(rawCode[trimmedEnd - 1])) trimmedEnd--;
        if (trimmedEnd - trimmedStart >= 4 && rawCode.AsSpan(trimmedStart, trimmedEnd - trimmedStart).StartsWith("<<") &&
            rawCode.AsSpan(trimmedStart, trimmedEnd - trimmedStart).EndsWith(">>"))
        {
            codeOffset = trimmedStart + 2;
            code = rawCode[codeOffset..(trimmedEnd - 2)];
        }
        var tokens = Lex(code, cancellationToken);
        var result = new List<WmlLuaFileReference>();
        for (var index = 0; index + 3 < tokens.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tokens[index].Text != "wesnoth" || tokens[index + 1].Text != "." ||
                tokens[index + 2].Text is not ("require" or "dofile") ||
                index > 0 && tokens[index - 1].Text is "." or ":" or "function") continue;
            var argumentIndex = index + 3;
            var parenthesized = tokens[argumentIndex].Text == "(";
            if (parenthesized) argumentIndex++;
            if (argumentIndex >= tokens.Count) continue;
            var argument = tokens[argumentIndex];
            if (!parenthesized && !argument.IsString && argument.Text != "{") continue;
            var next = argumentIndex + 1 < tokens.Count ? tokens[argumentIndex + 1].Text : "";
            var literal = argument.IsString && argument.Decoded is not null &&
                (parenthesized ? next is ")" or "," : next is not (".." or "." or ":" or "[" or "("));
            result.Add(new(literal ? argument.Decoded : null, tokens[index + 2].Text == "require",
                codeOffset + argument.Offset, argument.Length));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private sealed record Token(string Text, int Offset, int Length, bool IsString = false, string? Decoded = null);

    private static List<Token> Lex(string text, CancellationToken cancellationToken)
    {
        var result = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (char.IsWhiteSpace(text[index])) { index++; continue; }
            if (text.AsSpan(index).StartsWith("--"))
            {
                index += 2;
                if (TryLongString(text, index, out var end, out _, out _, out _)) index = end;
                else while (index < text.Length && text[index] is not ('\r' or '\n')) index++;
                continue;
            }
            var start = index;
            if (TryLongString(text, index, out var longEnd, out var payloadStart, out var payloadLength, out var longClosed))
            {
                index = longEnd;
                result.Add(new("string", start, index - start, true,
                    longClosed ? DecodeLongString(text, payloadStart, payloadLength) : null));
                continue;
            }
            if (text[index] is '\'' or '"')
            {
                var quote = text[index++];
                var decoded = new StringBuilder();
                var supported = true;
                var closed = false;
                while (index < text.Length)
                {
                    var character = text[index++];
                    if (character == quote) { closed = true; break; }
                    if (character == '\\')
                    {
                        if (index == text.Length) { supported = false; break; }
                        character = text[index++];
                        if (character is not ('\\' or '\'' or '"')) supported = false;
                    }
                    if (character is '\r' or '\n') supported = false;
                    decoded.Append(character);
                }
                result.Add(new("string", start, index - start, true, closed && supported ? decoded.ToString() : null));
                continue;
            }
            if (char.IsLetter(text[index]) || text[index] == '_')
            {
                while (++index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) { }
            }
            else if (text.AsSpan(index).StartsWith("..")) index += 2;
            else index++;
            result.Add(new(text[start..index], start, index - start));
        }
        return result;
    }

    private static string DecodeLongString(string text, int payloadStart, int payloadLength)
    {
        // Lua ignores one initial line break and normalizes each line ending.
        if (payloadLength > 0 && text[payloadStart] is ('\r' or '\n'))
        {
            var first = text[payloadStart++];
            payloadLength--;
            if (payloadLength > 0 && text[payloadStart] is ('\r' or '\n') && text[payloadStart] != first)
            {
                payloadStart++;
                payloadLength--;
            }
        }
        var decoded = new StringBuilder(payloadLength);
        var payloadEnd = payloadStart + payloadLength;
        for (var index = payloadStart; index < payloadEnd; index++)
        {
            var character = text[index];
            if (character is '\r' or '\n')
            {
                if (index + 1 < payloadEnd && text[index + 1] is ('\r' or '\n') && text[index + 1] != character)
                    index++;
                decoded.Append('\n');
            }
            else decoded.Append(character);
        }
        return decoded.ToString();
    }

    private static bool TryLongString(string text, int start, out int end, out int payloadStart, out int payloadLength,
        out bool closed)
    {
        end = start; payloadStart = start; payloadLength = 0; closed = false;
        if (start >= text.Length || text[start] != '[') return false;
        var cursor = start + 1;
        while (cursor < text.Length && text[cursor] == '=') cursor++;
        if (cursor >= text.Length || text[cursor] != '[') return false;
        payloadStart = cursor + 1;
        var close = "]" + new string('=', cursor - start - 1) + "]";
        var closingOffset = text.IndexOf(close, payloadStart, StringComparison.Ordinal);
        if (closingOffset < 0) { end = text.Length; return true; }
        payloadLength = closingOffset - payloadStart;
        end = closingOffset + close.Length;
        closed = true;
        return true;
    }
}
