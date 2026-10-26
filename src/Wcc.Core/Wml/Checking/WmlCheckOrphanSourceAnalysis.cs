namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Locates unsupported own-line literals using the shared authored lexer. This is
/// a source-report observation, not another parser, evaluated tree or edit proof.
/// </summary>
public static class WmlCheckOrphanSourceAnalysis
{
    public static IReadOnlyList<WmlCheckDiagnostic> Analyze(WmlCheckDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var text = document.Source.Text;
        var tokens = document.Analysis.Tokens;
        var tags = document.Analysis.TagOccurrences.ToDictionary(tag => tag.StartOffset);
        var owners = new List<WmlEditorTagOccurrence>();
        var findings = new List<WmlCheckDiagnostic>();
        var tokenIndex = 0;
        var definitions = 0;
        var defaults = 0;
        var conditionals = 0;
        var previousBoundary = false;
        var continuation = false;
        for (var start = 0; start < document.DiagnosticEndOffset;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = start;
            while (end < document.DiagnosticEndOffset && text[end] is not ('\r' or '\n'))
            {
                if ((end & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                end++;
            }
            // A deferred delimiter begins on this physical line. Do not interpret
            // its prefix as a complete statement with a reliable newline boundary.
            if (end == document.DiagnosticEndOffset && end < text.Length) break;
            while (tokenIndex < tokens.Count && tokens[tokenIndex].StartOffset + tokens[tokenIndex].Length <= start)
                tokenIndex++;
            var lineTokensEnd = tokenIndex;
            var contentEnd = end;
            while (lineTokensEnd < tokens.Count && tokens[lineTokensEnd].StartOffset < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = tokens[lineTokensEnd++];
                if (token.Kind == WmlEditorTokenKind.Comment && token.StartOffset >= start)
                    contentEnd = Math.Min(contentEnd, token.StartOffset);
            }
            var first = start;
            while (first < contentEnd && char.IsWhiteSpace(text[first])) first++;
            var last = contentEnd;
            while (last > first && char.IsWhiteSpace(text[last - 1])) last--;
            if (first == last)
            {
                start = NextLine(text, end);
                continue;
            }

            var hasControl = false;
            var hasMacro = false;
            var hasOpaque = false;
            var spansFollowingLine = false;
            var leadingAttribute = false;
            var leadingTag = false;
            var endsInOpaque = false;
            for (var current = tokenIndex; current < lineTokensEnd; current++)
            {
                var token = tokens[current];
                if (token.StartOffset >= contentEnd) break;
                var tokenEnd = token.StartOffset + token.Length;
                if (token.Kind == WmlEditorTokenKind.Directive ||
                    token.Kind == WmlEditorTokenKind.Comment && defaults > 0)
                {
                    var name = DirectiveName(text, token);
                    if (name == "define") definitions++;
                    else if (name == "enddef") { definitions = Math.Max(0, definitions - 1); defaults = 0; }
                    else if (name == "arg") defaults++;
                    else if (name == "endarg") defaults = Math.Max(0, defaults - 1);
                    else if (WmlAuthoredStructureScanner.IsConditionalStart(name)) conditionals++;
                    else if (name == "endif") conditionals = Math.Max(0, conditionals - 1);
                    hasControl = true;
                }
                if (token.Kind is WmlEditorTokenKind.String or WmlEditorTokenKind.RawCode or WmlEditorTokenKind.MacroInvocation)
                {
                    hasOpaque = true;
                    hasMacro |= token.Kind == WmlEditorTokenKind.MacroInvocation;
                    spansFollowingLine |= tokenEnd > end;
                    endsInOpaque |= token.StartOffset <= last - 1 && tokenEnd > last - 1;
                }
                if (token.StartOffset == first)
                {
                    leadingAttribute |= token.Kind == WmlEditorTokenKind.AttributeName;
                    leadingTag |= token.Kind == WmlEditorTokenKind.Tag;
                }
            }
            if (hasControl || definitions > 0 || defaults > 0)
            {
                // Definitions and defaults can legitimately be scalar fragments.
                // Never classify their literal text as standalone WML statements.
                previousBoundary = false;
                continuation = false;
                owners.Clear();
                start = NextLine(text, end);
                continue;
            }
            var continued = continuation;
            continuation = last > first && text[last - 1] == '+' && !endsInOpaque;
            if (!continued && !hasOpaque && !leadingAttribute && !leadingTag &&
                previousBoundary && owners.Count > 0 && IsBareLiteral(text.AsSpan(first, last - first)))
            {
                var owner = owners[^1];
                var proven = owner.MatchingTagIndex.HasValue && conditionals == 0;
                var literal = text[first..last];
                var display = literal.Length > 80 ? literal[..77] + "..." : literal;
                findings.Add(new("WML-S121", WmlCheckModuleId.Structure,
                    proven ? WmlCheckSeverity.Error : WmlCheckSeverity.Information,
                    proven ? WmlCheckConfidence.Proven : WmlCheckConfidence.NeedsContext,
                    document.Locate(first, last - first),
                    $"Bare source text '{display}' has no assignment ('=').",
                    "This own-line literal follows a physical statement boundary in a literal tag body. " +
                    "The Wesnoth WML parser expects an attribute name followed by '='; a bare value such as '100' is not a statement. " +
                    "Review this line for a deleted field name or other stray source text. Quoted/raw/invocation payloads, " +
                    "definition/default fragments and '+' value continuations are excluded. " +
                    (proven ? "The enclosing literal tag has an authored partner. No source was repaired and no editing authority was inferred."
                        : "The surrounding fragment or conditional still requires its actual expansion context."))
                {
                    RelatedLocations = Array.AsReadOnly(new[] { document.Locate(owner.StartOffset, owner.Length) })
                });
            }

            if (!continued && !hasOpaque)
                for (var current = tokenIndex; current < lineTokensEnd; current++)
                {
                    var token = tokens[current];
                    if (token.StartOffset >= contentEnd) break;
                    if (token.Kind != WmlEditorTokenKind.Tag || !tags.TryGetValue(token.StartOffset, out var tag)) continue;
                    if (!tag.IsComplete || tag.IsComputedName) { owners.Clear(); continue; }
                    if (!tag.IsClosing) owners.Add(tag);
                    else
                    {
                        var match = owners.FindLastIndex(opening => opening.Name == tag.Name);
                        if (match >= 0) owners.RemoveRange(match, owners.Count - match);
                        else owners.Clear();
                    }
                }
            // An unevaluated macro could emit a trailing '+' or incomplete value.
            // A later literal assignment/tag can establish a new physical boundary.
            previousBoundary = !hasMacro && !spansFollowingLine && !continuation &&
                (leadingAttribute || leadingTag || continued || previousBoundary && !hasOpaque &&
                    IsBareLiteral(text.AsSpan(first, last - first)));
            start = NextLine(text, end);
        }
        return Array.AsReadOnly(findings.ToArray());
    }

    private static bool IsBareLiteral(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !(char.IsAsciiLetterOrDigit(value[0]) || value[0] is '_' or '-')) return false;
        foreach (var character in value)
            if (character is '=' or '[' or ']' or '{' or '}' or '"' or '<' or '>' or '$' or '#') return false;
        return true;
    }

    private static int NextLine(string text, int end) => end >= text.Length ? text.Length :
        end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);

    private static string DirectiveName(string text, WmlEditorToken token)
    {
        var start = token.StartOffset + 1;
        var end = start;
        while (end < token.StartOffset + token.Length && char.IsAsciiLetter(text[end])) end++;
        return text[start..end];
    }
}
