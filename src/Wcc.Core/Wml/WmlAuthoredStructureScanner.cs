namespace Wcc.Core.Wml;

internal readonly record struct WmlPhysicalSourceLine(int StartOffset, int ContentEndOffset, int EndOffsetExclusive);
internal readonly record struct WmlAuthoredMacroSpan(int StartOffset, int EndOffsetExclusive);
internal readonly record struct WmlAuthoredDirective(string Name, int Line, int DefinitionDepth);

/// <summary>
/// Lexical masking for authored RAW structure. Original offsets and line endings
/// survive; string contents, comments, definitions and brace arguments cannot
/// masquerade as literal tags. This is not preprocessing or semantic validation.
/// </summary>
internal sealed record WmlAuthoredStructure(
    string MaskedText,
    IReadOnlyList<WmlPhysicalSourceLine> Lines,
    IReadOnlyList<WmlAuthoredMacroSpan> Macros,
    IReadOnlyList<WmlAuthoredDirective> Directives,
    IReadOnlyList<bool> ConditionalLines,
    string? Problem);

internal static class WmlAuthoredStructureScanner
{
    public static WmlAuthoredStructure Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = SplitLines(text);
        var masked = text.ToCharArray();
        var macros = new List<WmlAuthoredMacroSpan>();
        var directives = new List<WmlAuthoredDirective>();
        var conditionalLines = new bool[lines.Count];
        var stringState = new WmlStringScanState();
        var definitionDepth = 0;
        var macroDepth = 0;
        var macroStart = -1;
        var conditionalDepth = 0;
        string? problem = null;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var raw = text.AsSpan(line.StartOffset, line.ContentEndOffset - line.StartOffset);
            var hashIndex = FindOutsideComment(raw, stringState);
            WmlRawStringScanner.CopyWithoutStringContent(raw,
                masked.AsSpan(line.StartOffset, raw.Length), ref stringState);
            if (lineIndex == 0 && raw.Length > 0 && raw[0] == '\uFEFF')
                masked[line.StartOffset] = ' ';

            var directiveName = hashIndex >= 0 ? ReadDirective(raw[hashIndex..]) : null;
            var directiveAtStart = hashIndex >= 0 && raw[..hashIndex].Trim().IsEmpty;
            // A macro argument's text is opaque here, including any directives.
            // Inline #enddef is valid after a definition's scalar body.
            var definitionEnd = directiveName == "enddef" && definitionDepth > 0;
            var isDirective = macroDepth == 0 && directiveName is not null &&
                              (directiveAtStart || definitionEnd);
            var wasInDefinition = definitionDepth > 0;
            if (isDirective)
            {
                directives.Add(new WmlAuthoredDirective(directiveName!, lineIndex, definitionDepth));
                if (directiveName == "define")
                    definitionDepth++;
                else if (directiveName == "enddef")
                {
                    if (definitionDepth == 0)
                        problem ??= "An #enddef has no matching #define.";
                    else
                        definitionDepth--;
                }
                else if (!wasInDefinition)
                {
                    if (directiveName == "endif")
                        conditionalDepth = Math.Max(0, conditionalDepth - 1);
                    else if (IsConditionalStart(directiveName!))
                        conditionalDepth++;
                }
            }
            conditionalLines[lineIndex] = conditionalDepth > 0;

            if (wasInDefinition || definitionDepth > 0 || (isDirective && directiveName is "define" or "enddef"))
            {
                Mask(masked, line.StartOffset, line.ContentEndOffset);
                continue;
            }

            for (var offset = line.StartOffset; offset < line.ContentEndOffset; offset++)
            {
                if (masked[offset] == '{')
                {
                    if (macroDepth++ == 0)
                        macroStart = offset;
                }
                else if (masked[offset] == '}')
                {
                    if (macroDepth == 0)
                        problem ??= "A closing macro brace has no matching opening brace.";
                    else if (--macroDepth == 0)
                        macros.Add(new WmlAuthoredMacroSpan(macroStart, offset + 1));
                }
            }
        }

        foreach (var macro in macros)
            Mask(masked, macro.StartOffset, macro.EndOffsetExclusive);
        if (macroDepth > 0)
        {
            Mask(masked, macroStart, masked.Length);
            problem ??= "A macro invocation is missing its closing brace.";
        }
        if (definitionDepth > 0)
            problem ??= "A #define is missing its #enddef.";
        if (stringState.InsideMultilineString)
            problem ??= "A quoted or raw WML string is not closed.";

        return new WmlAuthoredStructure(new string(masked), lines, macros, directives, conditionalLines, problem);
    }

    public static bool IsConditionalStart(string directive)
        => directive is "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver";

    private static string? ReadDirective(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value[0] != '#') return null;
        var end = 1;
        while (end < value.Length && char.IsLetter(value[end])) end++;
        if (end < value.Length && !char.IsWhiteSpace(value[end])) return null;
        var name = value[1..end].ToString().ToLowerInvariant();
        return name is "define" or "enddef" or "else" or "endif" or "textdomain" || IsConditionalStart(name) ? name : null;
    }

    private static int FindOutsideComment(ReadOnlySpan<char> line, WmlStringScanState state)
    {
        var cursor = 0;
        while (cursor < line.Length)
        {
            var relative = line[cursor..].IndexOf('#');
            if (relative < 0) return -1;
            var hash = cursor + relative;
            WmlRawStringScanner.UpdateState(line[cursor..hash], ref state);
            if (!state.InsideMultilineString) return hash;
            WmlRawStringScanner.UpdateState(line.Slice(hash, 1), ref state);
            cursor = hash + 1;
        }
        return -1;
    }

    private static void Mask(char[] characters, int start, int end)
    {
        for (var offset = start; offset < end; offset++)
            if (characters[offset] is not ('\r' or '\n')) characters[offset] = ' ';
    }

    private static IReadOnlyList<WmlPhysicalSourceLine> SplitLines(string text)
    {
        var result = new List<WmlPhysicalSourceLine>();
        var start = 0;
        for (var offset = 0; offset < text.Length; offset++)
        {
            if (text[offset] is not ('\r' or '\n')) continue;
            var contentEnd = offset;
            if (text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n') offset++;
            result.Add(new WmlPhysicalSourceLine(start, contentEnd, offset + 1));
            start = offset + 1;
        }
        result.Add(new WmlPhysicalSourceLine(start, text.Length, text.Length));
        return result;
    }
}
