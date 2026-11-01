namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Bounded malformed-key observations in an already established authored statement.
/// The caller supplies the shared lexer's scope and continuation boundaries; this
/// helper neither parses a second WML tree nor guesses a custom field's semantics.
/// </summary>
internal static class WmlMalformedAssignmentCheck
{
    internal static WmlCheckDiagnostic? AnalyzeStatement(WmlCheckDocument document,
        int first, int last, int tokenStart, int tokenEnd, WmlEditorTagOccurrence owner,
        bool isConditional, CancellationToken cancellationToken)
    {
        var text = document.Source.Text;
        var equals = text.IndexOf('=', first, last - first);
        if (equals < first) return null;
        var keyEnd = equals;
        while (keyEnd > first && text[keyEnd - 1] is ' ' or '\t') keyEnd--;
        // A macro or runtime substitution may supply an entire name or statement.
        // Literal spelling alone cannot establish its eventual parser tokens.
        var key = text.AsSpan(first, keyEnd - first);
        if (key.IndexOfAny('$', '{', '}') >= 0) return null;
        for (var index = tokenStart; index < tokenEnd; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = document.Analysis.Tokens[index];
            if (token.StartOffset >= equals) break;
            if (token.StartOffset + token.Length > first && token.Kind is
                WmlEditorTokenKind.String or WmlEditorTokenKind.RawCode or WmlEditorTokenKind.MacroInvocation or
                WmlEditorTokenKind.Directive or WmlEditorTokenKind.Comment or WmlEditorTokenKind.Tag)
                return null;
        }

        // A normal literal name/list is recognized by the current shared assignment
        // syntax. Its rejection alone is not an error: native WML also accepts
        // spaced names, and checker support is intentionally narrower than a parser.
        if (WmlMultipleAssignmentSyntax.TryReadAt(text, first, out _, out _, cancellationToken)) return null;
        var emptyComponent = key.IsEmpty;
        var componentHasText = false;
        char? punctuation = null;
        foreach (var character in key)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (character == ',')
            {
                emptyComponent |= !componentHasText;
                componentHasText = false;
            }
            else if (character is not (' ' or '\t'))
            {
                componentHasText = true;
                // These ASCII punctuation tokens cannot belong to the native
                // parser's STRING-name token. Do not classify unfamiliar Unicode
                // or merely unknown/custom field names through this finite check.
                if (character is '.' or '-' or ':' or ';' or '/' or '\\' or '+' or
                    '[' or ']' or '(' or ')' or '!' or '?' or '@' or '&' or '*' or '%' or '|')
                    punctuation ??= character;
            }
        }
        emptyComponent |= !componentHasText;
        if (!emptyComponent && punctuation is null) return null;

        var proven = owner.MatchingTagIndex.HasValue && !isConditional;
        var display = key.Length > 80 ? key[..77].ToString() + "..." : key.ToString();
        var reason = emptyComponent ? "the attribute list contains an empty name"
            : $"'{punctuation}' is not a literal WML attribute-name character";
        return new("WML-S122", WmlCheckModuleId.Structure,
            WmlCheckSeverity.Error,
            proven ? WmlCheckConfidence.Proven : WmlCheckConfidence.NeedsContext,
            document.Locate(first, equals + 1 - first),
            $"Malformed assignment name '{display}': {reason}.",
            "This own-line assignment follows an established physical statement boundary in a literal tag body. " +
            "Wesnoth expects attribute names separated by commas and followed by '='. " +
            "For example, 'x.y=9,17' is not the combined assignment 'x,y=9,17'. " +
            "This malformed statement prevents reliable WML reading when reached; it can leave initial values and checked editing unavailable. " +
            "The checker reports syntax only; it does not rename fields, infer a repair, validate arbitrary custom keys or authorize edits. " +
            (proven ? "The enclosing literal tag has an authored partner."
                : "The source is conditional or an included fragment; whether this statement is reached requires its actual expansion context."))
        {
            RelatedLocations = Array.AsReadOnly(new[] { document.Locate(owner.StartOffset, owner.Length) })
        };
    }
}
