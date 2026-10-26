namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Low-severity authored-source observations over the shared document lexer.
/// Empty does not mean invalid: clearing a setting or selecting a default can
/// be intentional. This helper establishes neither requiredness nor game values.
/// </summary>
public static class WmlEmptyAssignmentCheck
{
    public static IReadOnlyList<WmlCheckDiagnostic> Analyze(WmlCheckDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var findings = new List<WmlCheckDiagnostic>();
        var text = document.Source.Text;
        var tokens = document.Analysis.Tokens;
        var definitions = 0;
        var defaults = 0;
        var completeTags = document.Analysis.TagOccurrences.Where(tag => tag.IsComplete)
            .Select(tag => tag.StartOffset).ToHashSet();
        for (var index = 0; index < tokens.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = tokens[index];
            if (token.StartOffset >= document.DiagnosticEndOffset) break;
            if (token.Kind == WmlEditorTokenKind.Directive || token.Kind == WmlEditorTokenKind.Comment && defaults > 0)
            {
                if (IsDirective(text, token, "define")) definitions++;
                else if (IsDirective(text, token, "arg")) defaults++;
                else if (IsDirective(text, token, "endarg")) defaults = Math.Max(0, defaults - 1);
                else if (IsDirective(text, token, "enddef"))
                {
                    definitions = Math.Max(0, definitions - 1);
                    defaults = 0;
                }
                continue;
            }
            if (token.Kind != WmlEditorTokenKind.AttributeName || definitions > 0 || defaults > 0 ||
                !IsLineLeading(document, token) || HasPrecedingContinuation(text, tokens, index, cancellationToken)) continue;

            var equals = token.StartOffset + token.Length;
            while (equals < text.Length && text[equals] is ' ' or '\t') equals++;
            if (equals >= document.DiagnosticEndOffset || text[equals] != '=') continue;
            var lineEnd = equals + 1;
            while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n'))
            {
                if ((lineEnd & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                lineEnd++;
            }
            var value = WmlInlineCommentSplitter.Split(text[(equals + 1)..lineEnd]).Value;
            if (value.Length != 0 || !HasLiteralStatementBoundary(document, tokens, index, equals + 1,
                    completeTags, cancellationToken)) continue;

            var keys = text.Substring(token.StartOffset, token.Length);
            findings.Add(new("WML-S120", WmlCheckModuleId.Structure, WmlCheckSeverity.Information,
                WmlCheckConfidence.Proven, document.Locate(token.StartOffset, equals + 1 - token.StartOffset),
                $"Literal assignment '{keys}' has no authored value.",
                "The selected source explicitly writes this assignment without a value before the next literal statement boundary. " +
                "This is a source-review notice: an empty value may intentionally clear a setting or select an engine default. " +
                "Quoted empty strings, macro values and uncertain continuations are not classified here. " +
                "Requiredness, runtime values and whether a default exists have not been established."));
        }
        return Array.AsReadOnly(findings.ToArray());
    }

    private static bool HasLiteralStatementBoundary(WmlCheckDocument document,
        IReadOnlyList<WmlEditorToken> tokens, int index, int cursor,
        IReadOnlySet<int> completeTags, CancellationToken cancellationToken)
    {
        var text = document.Source.Text;
        for (var next = index + 1; next < tokens.Count; next++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = tokens[next];
            if (token.StartOffset >= document.DiagnosticEndOffset) return false;
            // Bare text between lexer tokens is still a possible value. Never
            // swallow it or a following string/raw/macro payload as empty.
            if (!text.AsSpan(cursor, token.StartOffset - cursor).Trim().IsEmpty) return false;
            if (token.Kind == WmlEditorTokenKind.Comment)
            {
                cursor = token.StartOffset + token.Length;
                continue;
            }
            return token.Kind == WmlEditorTokenKind.AttributeName && IsLineLeading(document, token) ||
                token.Kind == WmlEditorTokenKind.Tag && completeTags.Contains(token.StartOffset);
        }
        // A deferred delimiter tail is not the document's actual statement end.
        return document.DiagnosticEndOffset == text.Length && text.AsSpan(cursor).Trim().IsEmpty;
    }

    private static bool HasPrecedingContinuation(string text, IReadOnlyList<WmlEditorToken> tokens,
        int index, CancellationToken cancellationToken)
    {
        var cursor = tokens[index].StartOffset;
        var previous = index - 1;
        while (cursor > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (cursor > 0 && char.IsWhiteSpace(text[cursor - 1]))
            {
                if ((cursor & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                cursor--;
            }
            if (cursor == 0) return false;
            while (previous >= 0 && tokens[previous].StartOffset >= cursor) previous--;
            if (previous >= 0 && tokens[previous].StartOffset + tokens[previous].Length >= cursor &&
                tokens[previous].Kind is WmlEditorTokenKind.Comment or WmlEditorTokenKind.Directive)
            {
                cursor = tokens[previous].StartOffset;
                previous--;
                continue;
            }
            return text[cursor - 1] == '+';
        }
        return false;
    }

    private static bool IsLineLeading(WmlCheckDocument document, WmlEditorToken token)
    {
        var lineStart = token.StartOffset - document.Locate(token.StartOffset).Column + 1;
        return document.Source.Text.AsSpan(lineStart, token.StartOffset - lineStart).Trim().IsEmpty;
    }

    private static bool IsDirective(string text, WmlEditorToken token, string name)
    {
        var value = text.AsSpan(token.StartOffset, token.Length);
        return value.Length >= name.Length + 1 && value[0] == '#' &&
            value.Slice(1, name.Length).SequenceEqual(name) &&
            (value.Length == name.Length + 1 || char.IsWhiteSpace(value[name.Length + 1]));
    }
}
