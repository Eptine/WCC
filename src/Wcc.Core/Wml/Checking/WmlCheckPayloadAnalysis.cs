namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Diagnostic confidence over the existing lexer/string scanner. A likely shifted
/// quote defers the unreliable tail; this never repairs text or changes editor tokens.
/// </summary>
public sealed record WmlCheckPayloadAnalysis(bool HasOpenQuotedPayload, bool HasOpenRawPayload,
    WmlCheckDiagnostic? ShiftedQuoteDiagnostic)
{
    internal static WmlCheckPayloadAnalysis Build(WmlCheckDocument document, CancellationToken cancellationToken)
    {
        var text = document.Source.Text;
        var state = new WmlStringScanState();
        var attributeQuotes = new HashSet<int>();
        var missingOpeningQuotes = new Dictionary<int, int>();
        for (var start = 0; start < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n'))
            {
                if ((end & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                end++;
            }
            var line = text.AsSpan(start, end - start);
            if (!state.InsideMultilineString &&
                MissingOpeningQuoteCandidate(line, cancellationToken) is { } candidate)
                missingOpeningQuotes.Add(start + candidate.QuoteOffset, start + candidate.ValueOffset);
            WmlRawStringScanner.UpdateState(line, ref state);
            if (AttributeQuoteOffset(line, cancellationToken) is { } relativeQuote)
                attributeQuotes.Add(start + relativeQuote);
            start = end + (end < text.Length && text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }
        if (!state.InsideQuotedString) return new(false, state.InsideRawString, null);

        var openings = new List<WmlEditorTagOccurrence>();
        var tagIndex = 0;
        var tokenIndex = 0;
        var definitionDepth = 0;
        var argumentDepth = 0;
        var conditionalDepth = 0;
        foreach (var region in document.Analysis.QuotedRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (tagIndex < document.Analysis.TagOccurrences.Count &&
                   document.Analysis.TagOccurrences[tagIndex].StartOffset < region.StartOffset)
            {
                var tag = document.Analysis.TagOccurrences[tagIndex++];
                AdvanceControls(tag.StartOffset);
                if (tag.IsComputedName) { openings.Clear(); continue; }
                if (!tag.IsComplete || InUncertainScope()) continue;
                if (!tag.IsClosing) openings.Add(tag);
                else
                {
                    var index = openings.FindLastIndex(opening => opening.Name == tag.Name);
                    if (index >= 0) openings.RemoveRange(index, openings.Count - index);
                }
            }
            AdvanceControls(region.StartOffset);
            if (!InUncertainScope() && openings.Count > 0 &&
                missingOpeningQuotes.TryGetValue(region.StartOffset, out var valueOffset) &&
                MissingOpeningQuoteDiagnostic(document, region, openings[^1], valueOffset,
                    attributeQuotes, cancellationToken) is { } missingOpening)
                return new(true, false, missingOpening);
            if (InUncertainScope() || openings.Count == 0 || !attributeQuotes.Contains(region.StartOffset) ||
                region.EndOffsetExclusive <= region.StartOffset + 1 ||
                text[region.EndOffsetExclusive - 1] != '"' ||
                !attributeQuotes.Contains(region.EndOffsetExclusive - 1) ||
                !HasFollowingPayload(text, region.EndOffsetExclusive)) continue;

            // The lexer thinks a later value's OPENING quote closes this value.
            // Require both a swallowed owner closer and the following same owner,
            // with EOF still inside quotes. A newline or tag-shaped text alone
            // is valid multiline WML and must not trigger this heuristic.
            var owner = openings[^1];
            var swallowedClose = -1;
            var swallowedOpen = -1;
            for (var start = NextLine(text, region.StartOffset); start < region.EndOffsetExclusive;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = start;
                while (end < region.EndOffsetExclusive && text[end] is not ('\r' or '\n'))
                {
                    if ((end & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    end++;
                }
                var line = text.AsSpan(start, end - start);
                var leading = line.Length - line.TrimStart().Length;
                var trimmed = line.Trim();
                if (swallowedClose < 0 && trimmed.SequenceEqual($"[/{owner.Name}]")) swallowedClose = start + leading;
                else if (swallowedClose >= 0 && trimmed.SequenceEqual($"[{owner.Name}]"))
                {
                    swallowedOpen = start + leading;
                    break;
                }
                start = NextLine(text, end);
            }
            if (swallowedClose < 0 || swallowedOpen < 0) continue;

            var diagnostic = new WmlCheckDiagnostic("WML-S006", WmlCheckModuleId.Structure,
                WmlCheckSeverity.Error, WmlCheckConfidence.NeedsContext, document.Locate(region.StartOffset, 1),
                "This quoted value likely lost its closing quote before the following tag.",
                $"The value swallows [/{owner.Name}] and another [{owner.Name}], then appears to close at the next value's opening quote. " +
                "The source also remains quoted at EOF. Review the closing quote of this value first. " +
                "Later tag, directive and reference diagnostics are deferred until this quote is fixed, because their lexical boundaries are unreliable. " +
                "Quoted WML can contain multiline/tag-shaped text, so this is a likely cause, not proof of the intended edit; no source was repaired.")
            {
                RelatedLocations = Array.AsReadOnly(new[]
                {
                    document.Locate(owner.StartOffset, owner.Length),
                    document.Locate(swallowedClose, owner.Name.Length + 3),
                    document.Locate(region.EndOffsetExclusive - 1, 1)
                })
            };
            return new(true, false, diagnostic);
        }
        return new(true, false, null);

        bool InUncertainScope() => definitionDepth > 0 || argumentDepth > 0 || conditionalDepth > 0;

        void AdvanceControls(int offset)
        {
            while (tokenIndex < document.Analysis.Tokens.Count && document.Analysis.Tokens[tokenIndex].StartOffset < offset)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = document.Analysis.Tokens[tokenIndex++];
                if (token.Kind != WmlEditorTokenKind.Directive &&
                    !(token.Kind == WmlEditorTokenKind.Comment && argumentDepth > 0)) continue;
                var payload = text.AsSpan(token.StartOffset + 1, Math.Max(0, token.Length - 1));
                var end = 0;
                while (end < payload.Length && char.IsAsciiLetter(payload[end])) end++;
                if (end < payload.Length && !char.IsWhiteSpace(payload[end])) continue;
                var name = payload[..end].ToString();
                if (name == "define") definitionDepth++;
                else if (name == "enddef") { definitionDepth = Math.Max(0, definitionDepth - 1); argumentDepth = 0; }
                else if (name == "arg") argumentDepth++;
                else if (name == "endarg") argumentDepth = Math.Max(0, argumentDepth - 1);
                else if (WmlAuthoredStructureScanner.IsConditionalStart(name)) conditionalDepth++;
                else if (name == "endif") conditionalDepth = Math.Max(0, conditionalDepth - 1);
                else if (name != "else") continue;
                // Do not infer owners across definition/default/branch fences.
                // These fragments need expansion context; generic EOF diagnostics
                // remain available without inventing an authored quote owner.
                openings.Clear();
            }
        }
    }

    private static int? AttributeQuoteOffset(ReadOnlySpan<char> line, CancellationToken cancellationToken)
    {
        if (AttributeValueOffset(line, cancellationToken) is not { } offset) return null;
        if (offset < line.Length && line[offset] == '_')
        {
            offset++;
            while (offset < line.Length && line[offset] is ' ' or '\t') offset++;
        }
        return offset < line.Length && line[offset] == '"' ? offset : null;
    }

    private static int? AttributeValueOffset(ReadOnlySpan<char> line, CancellationToken cancellationToken)
    {
        var offset = line.Length - line.TrimStart().Length;
        var keyStart = offset;
        while (offset < line.Length && line[offset] != '=')
        {
            if ((offset & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!(char.IsAsciiLetterOrDigit(line[offset]) || line[offset] is '_' or ',' or ' ' or '\t')) return null;
            offset++;
        }
        if (offset == keyStart || offset == line.Length) return null;
        offset++;
        while (offset < line.Length && line[offset] is ' ' or '\t') offset++;
        return offset;
    }

    private static (int QuoteOffset, int ValueOffset)? MissingOpeningQuoteCandidate(
        ReadOnlySpan<char> line, CancellationToken cancellationToken)
    {
        if (AttributeValueOffset(line, cancellationToken) is not { } offset ||
            offset + 1 >= line.Length || line[offset] != '_' || line[offset + 1] is not (' ' or '\t'))
            return null;
        offset++;
        while (offset < line.Length && line[offset] is ' ' or '\t') offset++;
        var end = line.Length;
        while (end > offset && line[end - 1] is ' ' or '\t') end--;
        if (end <= offset + 1 || line[end - 1] != '"') return null;
        var prose = line[offset..(end - 1)].TrimEnd();
        if (prose.IsEmpty || prose[^1] is not ('.' or '!' or '?')) return null;

        // An unquoted translation marker is legal. Do not diagnose it alone:
        // this candidate requires plain multiword prose and an isolated trailing
        // quote, then shifted quote/owner evidence from the existing lexer below.
        var hasLetter = false;
        var hasWordBoundary = false;
        for (var index = 0; index < prose.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var current = prose[index];
            if (current is '"' or '#' or '+' or '{' or '}' or '$' or '<' or '>' or '[' or ']' or '=')
                return null;
            if (char.IsLetter(current)) hasLetter = true;
            else if (hasLetter && current is (' ' or '\t')) hasWordBoundary = true;
        }
        return hasLetter && hasWordBoundary ? (end - 1, offset) : null;
    }

    private static WmlCheckDiagnostic? MissingOpeningQuoteDiagnostic(WmlCheckDocument document,
        WmlEditorQuotedRegion region, WmlEditorTagOccurrence owner, int valueOffset,
        IReadOnlySet<int> attributeQuotes, CancellationToken cancellationToken)
    {
        var text = document.Source.Text;
        if (region.EndOffsetExclusive <= region.StartOffset + 1 ||
            text[region.EndOffsetExclusive - 1] != '"' ||
            !HasFollowingPayload(text, region.EndOffsetExclusive)) return null;
        var borrowedQuote = region.EndOffsetExclusive - 1;
        var borrowedLineStart = borrowedQuote;
        while (borrowedLineStart > 0 && text[borrowedLineStart - 1] is not ('\r' or '\n'))
        {
            if ((borrowedLineStart & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            borrowedLineStart--;
        }
        var borrowedPrefix = text.AsSpan(borrowedLineStart, borrowedQuote - borrowedLineStart).TrimStart();
        if (!attributeQuotes.Contains(borrowedQuote) &&
            (borrowedPrefix.IsEmpty || borrowedPrefix[0] != '{')) return null;

        var hasSwallowedAssignment = false;
        for (var start = NextLine(text, region.StartOffset); start < borrowedQuote;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = start;
            while (end < borrowedQuote && text[end] is not ('\r' or '\n'))
            {
                if ((end & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                end++;
            }
            if (AttributeValueOffset(text.AsSpan(start, end - start), cancellationToken) is { } offset &&
                start + offset < end) hasSwallowedAssignment = true;
            start = NextLine(text, end);
        }
        if (!hasSwallowedAssignment || document.Analysis.QuotedRegions.Count == 0) return null;
        var finalRegion = document.Analysis.QuotedRegions[^1];
        if (finalRegion.StartOffset < region.EndOffsetExclusive || finalRegion.EndOffsetExclusive != text.Length)
            return null;

        var swallowedClose = -1;
        for (var start = NextLine(text, finalRegion.StartOffset); start < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n'))
            {
                if ((end & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                end++;
            }
            var line = text.AsSpan(start, end - start);
            if (line.Trim().SequenceEqual($"[/{owner.Name}]"))
            {
                swallowedClose = start + line.Length - line.TrimStart().Length;
                break;
            }
            start = NextLine(text, end);
        }
        if (swallowedClose < 0) return null;

        return new("WML-S007", WmlCheckModuleId.Structure,
            WmlCheckSeverity.Error, WmlCheckConfidence.NeedsContext, document.Locate(valueOffset, 1),
            "This translated prose value likely lost its opening quote.",
            "Plain prose after the translation marker ends with a quote that instead starts a quoted payload. " +
            "That payload swallows a following assignment and appears to close at a later value or macro argument's opening quote. " +
            $"The shifted source remains quoted at EOF and swallows [/{owner.Name}]. Review the start of this value first. " +
            "Later tag, directive and reference diagnostics are deferred until this quote is fixed, because their lexical boundaries are unreliable. " +
            "Unquoted and concatenated WML values can be valid, so this is a likely cause, not proof of the intended edit; no source was repaired.")
        {
            RelatedLocations = Array.AsReadOnly(new[]
            {
                document.Locate(region.StartOffset, 1),
                document.Locate(borrowedQuote, 1),
                document.Locate(owner.StartOffset, owner.Length),
                document.Locate(swallowedClose, owner.Name.Length + 3)
            })
        };
    }

    private static bool HasFollowingPayload(string text, int offset)
    {
        while (offset < text.Length && text[offset] is ' ' or '\t') offset++;
        // A multiline value can legitimately end on a quoted attribute example.
        // Comments, concatenation and multiple assignments can follow that close.
        // None of those is evidence that it was actually the next value's opener.
        return offset < text.Length && text[offset] is not ('\r' or '\n' or '#' or '+' or ',' or '"');
    }

    private static int NextLine(string text, int offset)
    {
        while (offset < text.Length && text[offset] is not ('\r' or '\n')) offset++;
        if (offset == text.Length) return offset;
        return offset + (text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1);
    }
}
