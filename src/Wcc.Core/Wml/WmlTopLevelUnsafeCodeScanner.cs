namespace Wcc.Core.Wml;

public sealed record WmlTopLevelUnsafeLine(
    int LineIndex,
    string RawLine,
    WmlTopLevelCodeKind Kind
);

/// <summary>
/// Backward-compatible adapter over the central top-level code scanner.
/// We still expose the older "unsafe lines" API because the existing field
/// safety logic uses it, but classification now lives in one place.
/// </summary>
public static class WmlTopLevelUnsafeCodeScanner
{
    public static IReadOnlyList<WmlTopLevelUnsafeLine> CollectUnsafeTopLevelLines(WmlEditableBlock block)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));

        var source = block.ToRawText();
        var structure = WmlAuthoredStructureScanner.Scan(source);
        var analysis = structure.Problem is null ? WmlEditorAnalysis.Build(source) : null;
        var classified = WmlTopLevelCodeScanner.Collect(block);
        // Payload text can resemble a child tag to the older line classifier.
        // Recover real direct-child controls from the same lexical scope before
        // ignoring harmless metadata; payload tags cannot hide a later command.
        if (analysis is not null)
            classified = classified.Concat(CollectLexicalControls(block, structure, analysis))
                .DistinctBy(line => line.LineIndex).OrderBy(line => line.LineIndex).ToArray();
        return classified
            .Where(line => analysis is null || !IsNonExecutable(line, structure, analysis))
            .Select(x => new WmlTopLevelUnsafeLine(x.LineIndex, x.RawLine, x.Kind))
            .ToList();
    }

    private static bool IsNonExecutable(WmlTopLevelCodeLine line, WmlAuthoredStructure structure, WmlEditorAnalysis analysis)
    {
        if (line.LineIndex < 0 || line.LineIndex >= structure.Lines.Count) return false;
        var physical = structure.Lines[line.LineIndex];
        var text = line.RawLine.TrimStart();
        var start = physical.StartOffset + line.RawLine.Length - text.Length;
        // A complete shared lexical payload must cover the whole visible line.
        // Text following its closing delimiter cannot borrow this exemption.
        if (analysis.Tokens.Any(token => token.Kind is WmlEditorTokenKind.String or WmlEditorTokenKind.RawCode
                && token.StartOffset <= start && token.StartOffset + token.Length >= physical.ContentEndOffset)) return true;
        if (line.Kind != WmlTopLevelCodeKind.OtherHashDirective || !text.StartsWith('#')) return false;
        var token = analysis.Tokens.FirstOrDefault(token => token.StartOffset == start
            && token.StartOffset + token.Length == physical.ContentEndOffset);
        if (token is null) return false;
        const string marker = "#textdomain";
        if (text.StartsWith(marker, StringComparison.Ordinal))
        {
            if (token.Kind != WmlEditorTokenKind.Directive || text.Length == marker.Length
                || text[marker.Length] is not (' ' or '\t')) return false;
            var split = WmlInlineCommentSplitter.Split(text[marker.Length..]);
            var domain = split.Value.Trim();
            return domain.Length > 0 && domain.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
                && (split.InlineCommentSuffix.Length == 0 || IsPlainComment(split.InlineCommentSuffix.TrimStart()));
        }
        if (token.Kind != WmlEditorTokenKind.Comment) return false;
        // Unknown prose is a comment in Wesnoth. Misspelled or malformed known
        // commands remain conservative, even if the lexer reports a comment.
        return IsPlainComment(text);
    }

    private static bool IsPlainComment(string text)
        => text.StartsWith('#') && !ReservedCommands.Any(name => text[1..].TrimStart().StartsWith(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<WmlTopLevelCodeLine> CollectLexicalControls(WmlEditableBlock block,
        WmlAuthoredStructure structure, WmlEditorAnalysis analysis)
    {
        var opening = block.Lines[block.OpeningTagLineIndex];
        var owningTagStart = structure.Lines[block.OpeningTagLineIndex].StartOffset + opening.Length - opening.TrimStart().Length;
        for (var index = block.OpeningTagLineIndex + 1; index < block.ClosingTagLineIndex; index++)
        {
            if (index >= structure.Lines.Count) yield break;
            var physical = structure.Lines[index];
            var raw = block.Lines[index]; var trimmed = raw.TrimStart();
            var start = physical.StartOffset + raw.Length - trimmed.Length;
            if (analysis.TagOccurrences.Any(tag => !tag.IsClosing && tag.StartOffset > owningTagStart
                    && tag.StartOffset < start && tag.MatchingTagIndex is { } partner
                    && analysis.TagOccurrences[partner].StartOffset > start)) continue;
            foreach (var token in analysis.Tokens.Where(token => token.StartOffset >= start && token.StartOffset < physical.ContentEndOffset))
            {
                var tokenText = raw[(token.StartOffset - physical.StartOffset)..].TrimEnd();
                var atStart = token.StartOffset == start;
                if (token.Kind == WmlEditorTokenKind.MacroInvocation && !atStart) continue;
                if (token.Kind is not (WmlEditorTokenKind.Directive or WmlEditorTokenKind.Comment or WmlEditorTokenKind.MacroInvocation)
                    || token.Kind == WmlEditorTokenKind.Comment && !atStart && IsPlainComment(tokenText)) continue;
                var kind = WmlTopLevelCodeScanner.ClassifyTrimmedLine(tokenText);
                if (kind != WmlTopLevelCodeKind.None) yield return new(index, raw, kind);
            }
        }
    }

    private static readonly string[] ReservedCommands =
    ["define", "enddef", "undef", "ifdef", "ifndef", "ifhave", "ifnhave", "ifver", "ifnver", "else", "endif",
     "arg", "endarg", "warning", "error", "deprecated", "lua", "endlua", "include", "textdomain"];
}