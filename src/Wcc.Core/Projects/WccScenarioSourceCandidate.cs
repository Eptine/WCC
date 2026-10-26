using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

/// <summary>
/// Conservative source inventory for scenario navigation, independent of folder names.
/// A candidate still needs captured campaign membership; this grants no write authority.
/// Complete helper-only definitions remain available through the ordinary source capture.
/// </summary>
public static class WccScenarioSourceCandidate
{
    public static bool MayDeclareScenario(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var structure = WmlAuthoredStructureScanner.Scan(text);
        if (HasScenarioOrUnknownRootOutput(structure)) return true;
        // Nested definitions are unusual enough to retain for RAW inspection.
        if (structure.Directives.Any(directive => directive.Name == "define" && directive.DefinitionDepth != 0)) return true;
        WmlAuthoredDirective? opening = null;
        foreach (var directive in structure.Directives)
        {
            if (directive.Name == "define" && directive.DefinitionDepth == 0)
            {
                opening = directive;
                var header = structure.Lines[directive.Line];
                if (text[header.StartOffset..header.ContentEndOffset].Trim().Length <= "#define".Length) return true;
            }
            else if (directive.Name == "enddef" && directive.DefinitionDepth == 1)
            {
                if (opening is not { } start) return true;
                var closing = structure.Lines[directive.Line];
                // Inline terminal directives may have scalar prefix content. Preserve
                // those uncommon forms instead of cutting their bodies speculatively.
                if (!text[closing.StartOffset..closing.ContentEndOffset].TrimStart().StartsWith("#enddef", StringComparison.OrdinalIgnoreCase)) return true;
                var body = text[structure.Lines[start.Line].EndOffsetExclusive..closing.StartOffset];
                var bodyStructure = WmlAuthoredStructureScanner.Scan(body);
                if (HasScenarioOrUnknownRootOutput(bodyStructure, definitionBody: true)) return true;
                opening = null;
            }
        }
        return opening is not null;
    }

    private static bool HasScenarioOrUnknownRootOutput(WmlAuthoredStructure structure, bool definitionBody = false)
    {
        if (structure.Problem is not null) return true;
        var blocks = WmlBlockScanner.FindTopLevelBlockSpans(structure.MaskedText);
        if (blocks.Any(block => block.TagName.TrimStart('+').Equals("scenario", StringComparison.OrdinalIgnoreCase))) return true;
        // Computed tag names can become scenario openings; ordinary helper calls
        // inside a definition register no standalone scenario in this source.
        foreach (var macro in structure.Macros)
            if (structure.MaskedText.LastIndexOf('[', macro.StartOffset) >
                structure.MaskedText.LastIndexOf(']', macro.StartOffset)) return true;
        if (!definitionBody && structure.Macros.Any(macro => !blocks.Any(block =>
                macro.StartOffset >= block.StartIndex && macro.EndOffsetExclusive <= block.EndIndexExclusive))) return true;
        var remainder = structure.MaskedText.ToCharArray();
        foreach (var block in blocks) Array.Fill(remainder, ' ', block.StartIndex, block.EndIndexExclusive - block.StartIndex);
        // Incomplete/dynamic tags and unbalanced fragments stay visible as sources.
        return remainder.Any(character => character is '[' or ']' or '{' or '}');
    }
}
