using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public static partial class WccUnitTypeMaterializationPlanner
{
    // The authored structure masker deliberately excludes #arg. Reuse the
    // editor lexer for exact declaration spans, including scalar#endarg, while
    // retaining all default payload for the ordinary executable-control proof.
    private static bool TryCaptureOptionalArguments(WmlStaticSource source, int bodyStart, int bodyEnd,
        IReadOnlyList<string> required, out IReadOnlyList<string> names,
        out IReadOnlyList<WmlEditorToken> declarations, out string reason)
    {
        var optional = new List<string>(); var spans = new List<WmlEditorToken>();
        var known = required.ToHashSet(StringComparer.Ordinal); string? active = null;
        names = optional; declarations = spans;
        reason = "Optional helper arguments need unique names and exact matched #arg/#endarg declarations.";
        foreach (var token in WmlEditorAnalysis.Build(source.Text).Tokens)
        {
            if (token.Kind is not (WmlEditorTokenKind.Directive or WmlEditorTokenKind.Comment)) continue;
            var text = source.Text.Substring(token.StartOffset, token.Length);
            if (!text.StartsWith('#')) continue;
            var end = 1; while (end < text.Length && char.IsLetter(text[end])) end++;
            if (end < text.Length && !char.IsWhiteSpace(text[end])) continue;
            var name = text[1..end];
            if (name is not ("arg" or "endarg")) continue;
            if (token.StartOffset < bodyStart || token.StartOffset + token.Length > bodyEnd) return false;
            var argument = WmlInlineCommentSplitter.Split(text[end..]).Value.Trim();
            if (name == "arg")
            {
                if (token.Kind != WmlEditorTokenKind.Directive || active is not null || !MacroName(argument) || !known.Add(argument)) return false;
                active = argument; optional.Add(argument); spans.Add(token);
            }
            else
            {
                if (active is null || argument.Length != 0) return false;
                active = null; spans.Add(token);
            }
        }
        return active is null;
    }
}
