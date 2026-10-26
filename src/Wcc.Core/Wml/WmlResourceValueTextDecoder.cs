using System.Text;
using Wcc.Core.Scenarios;

namespace Wcc.Core.Wml;

public enum WmlResourceValueTextIssueKind { UnsupportedSyntax, Translation, UnexpandedMacro, LimitExceeded }
public sealed record WmlResourceValueTextIssue(WmlResourceValueTextIssueKind Kind, string Reason);

/// <summary>Literal text observation only; no effective scalar, ownership or write authority.</summary>
public sealed record WmlResourceValueTextDecodeResult(string ValueText, WmlResourceValueTextIssue? Issue)
{
    public bool IsDecoded => Issue is null;
}

/// <summary>
/// Decodes a bounded, already evaluated, nontranslated WML resource value. Pinned
/// 1.19.26 serialization/parser.cpp appends quoted and unquoted tokens and removes
/// concatenation plus tokens; adjacent unquoted word tokens gain one space.
/// This does not evaluate macros, variables, formulas or image modifiers.
/// </summary>
public static class WmlResourceValueTextDecoder
{
    private const int MaxCharacters = 128 * 1024;

    public static WmlResourceValueTextDecodeResult Decode(string? raw, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (raw is null) return Failure(WmlResourceValueTextIssueKind.UnsupportedSyntax, "The resource value has no captured text.");
        if (raw.Length > MaxCharacters) return Failure(WmlResourceValueTextIssueKind.LimitExceeded, "The resource value exceeds the bounded decoding length.");
        var decoded = new StringBuilder(raw.Length);
        var cursor = 0;
        var previousWord = false;
        var sawOperand = false;
        var pendingPlus = false;
        while (true)
        {
            var token = ReadToken(raw, ref cursor, cancellationToken, out var issue);
            if (issue is not null) return new("", issue);
            if (token.Kind == TokenKind.End)
            {
                if (pendingPlus) return Failure(WmlResourceValueTextIssueKind.UnsupportedSyntax, "A resource-value concatenation has no following operand.");
                return new(decoded.ToString(), null);
            }
            if (token.Kind == TokenKind.Plus)
            {
                if (!sawOperand || pendingPlus) return Failure(WmlResourceValueTextIssueKind.UnsupportedSyntax, "A resource-value concatenation has no complete preceding operand.");
                pendingPlus = true;
                // The pinned parser does not reset its previous-string state on '+'.
                continue;
            }
            if (token.Kind == TokenKind.Underscore)
            {
                var following = ReadToken(raw, ref cursor, cancellationToken, out issue);
                if (issue is not null) return new("", issue);
                if (following.Kind == TokenKind.Quoted)
                    return Failure(WmlResourceValueTextIssueKind.Translation, "A translatable resource value needs locale/textdomain context; its filename was not guessed.");
                if (following.Kind == TokenKind.Plus)
                    return Failure(WmlResourceValueTextIssueKind.UnsupportedSyntax, "An underscore token followed by concatenation is outside the bounded resource-value syntax.");
                decoded.Append('_').Append(following.Text);
                previousWord = following.Kind == TokenKind.Word;
                sawOperand = true;
                pendingPlus = false;
                if (following.Kind == TokenKind.End) return new(decoded.ToString(), null);
                continue;
            }
            if (token.Kind == TokenKind.Word && previousWord) decoded.Append(' ');
            decoded.Append(token.Text);
            previousWord = token.Kind == TokenKind.Word;
            sawOperand = true;
            pendingPlus = false;
        }
    }

    public static bool TryDecode(string? raw, out string value, out string reason)
    {
        var result = Decode(raw);
        value = result.ValueText;
        reason = result.Issue?.Reason ?? "";
        return result.IsDecoded;
    }

    private static Token ReadToken(string raw, ref int cursor, CancellationToken cancellationToken,
        out WmlResourceValueTextIssue? issue)
    {
        cancellationToken.ThrowIfCancellationRequested();
        issue = null;
        while (cursor < raw.Length && raw[cursor] is ' ' or '\t') cursor++;
        if (cursor == raw.Length) return new(TokenKind.End, "");
        var current = raw[cursor];
        if (current is '\r' or '\n' || char.IsControl(current))
            return Refuse(WmlResourceValueTextIssueKind.UnsupportedSyntax, "Multiline or control-character resource values are outside this bounded single-line decoding syntax.", out issue);
        if (current is '{' or '}')
            return Refuse(WmlResourceValueTextIssueKind.UnexpandedMacro, "An unexpanded macro remains in this resource value; no substitution was invented.", out issue);
        if (current == '#' || current == '<' && cursor + 1 < raw.Length && raw[cursor + 1] == '<')
            return Refuse(WmlResourceValueTextIssueKind.UnsupportedSyntax, "Raw payloads, comments and directives are outside this resource-value decoding syntax.", out issue);
        if (current == '"')
        {
            var start = cursor++;
            while (cursor < raw.Length)
            {
                if ((cursor & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                // Quoted WML is allowed to span lines. Preserve its whitespace:
                // animation-list splitting trims entries, while a newline inside
                // a filename still remains invalid resource syntax. This is text
                // observation only, not a change to the strict scalar parser.
                if (char.IsControl(raw[cursor]) && raw[cursor] is not ('\r' or '\n' or '\t'))
                    return Refuse(WmlResourceValueTextIssueKind.UnsupportedSyntax, "A quoted resource value contains unsupported control-character text.", out issue);
                if (raw[cursor] is '{' or '}')
                    return Refuse(WmlResourceValueTextIssueKind.UnexpandedMacro, "An unexpanded macro remains in this quoted resource value; no substitution was invented.", out issue);
                if (raw[cursor] != '"') { cursor++; continue; }
                if (cursor + 1 < raw.Length && raw[cursor + 1] == '"') { cursor += 2; continue; }
                cursor++;
                // Reuse the author's literal codec: doubled quotes escape a
                // quote in WML; backslashes never become C/Python escapes.
                return new(TokenKind.Quoted, WccScenarioFieldValueCodec.DecodeLiteral(raw[start..cursor]));
            }
            return Refuse(WmlResourceValueTextIssueKind.UnsupportedSyntax, "A quoted resource value has no closing quote.", out issue);
        }
        if (current == '+') { cursor++; return new(TokenKind.Plus, "+"); }
        if (current == '_' && (cursor + 1 == raw.Length || !IsWordCharacter(raw[cursor + 1])))
        { cursor++; return new(TokenKind.Underscore, "_"); }
        if (IsWordCharacter(current) || current == '$')
        {
            var start = cursor++;
            while (cursor < raw.Length && (IsWordCharacter(raw[cursor]) || raw[cursor] == '$'))
            {
                if ((cursor & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                cursor++;
            }
            return new(TokenKind.Word, raw[start..cursor]);
        }
        cursor++;
        return new(TokenKind.Misc, current.ToString());
    }

    private static bool IsWordCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';
    private static WmlResourceValueTextDecodeResult Failure(WmlResourceValueTextIssueKind kind, string reason) => new("", new(kind, reason));
    private static Token Refuse(WmlResourceValueTextIssueKind kind, string reason, out WmlResourceValueTextIssue? issue)
    { issue = new(kind, reason); return new(TokenKind.End, ""); }
    private enum TokenKind { End, Word, Quoted, Misc, Plus, Underscore }
    private readonly record struct Token(TokenKind Kind, string Text);
}
