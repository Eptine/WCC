using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    // Additional lexical evidence for a physical single-caller claim. This never
    // evaluates a branch or grants source ownership. Borrowed Core helpers can
    // use computed names; only files inside the selected project get this gate.
    internal static bool HasCompleteStoryDefinitionInvocationInventory(IEnumerable<WmlStaticSource> sources, string projectRootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRootPath)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var count = 0; long characters = 0;
        foreach (var source in sources.Where(source => source.IsAuthored && Path.GetFullPath(source.Path).StartsWith(root, comparison)))
        {
            if (++count > 200_000 || source.Text.Length > 8 * 1024 * 1024 || (characters += source.Text.Length) > 64L * 1024 * 1024)
                return false;
            if (WmlAuthoredStructureScanner.Scan(source.Text).Problem is not null || !CompleteDefinitionInventoryText(source.Text, 0))
                return false;
        }
        return count != 0;
    }

    // Offsets survive so an executable caller found in this view still maps to
    // the original source. Lua table literals and comments cannot become callers.
    internal static string StoryDefinitionInventoryText(string text) => MaskDefinitionInventoryText(text, WmlEditorAnalysis.Build(text));

    private static string MaskDefinitionInventoryText(string text, WmlEditorAnalysis analysis)
    {
        var masked = text.ToCharArray();
        foreach (var token in analysis.Tokens.Where(token => token.Kind is WmlEditorTokenKind.Comment or WmlEditorTokenKind.RawCode or WmlEditorTokenKind.Directive))
            for (var offset = token.StartOffset; offset < token.StartOffset + token.Length; offset++)
                if (masked[offset] is not ('\r' or '\n')) masked[offset] = ' ';
        return new(masked);
    }

    private static bool CompleteDefinitionInventoryText(string text, int depth)
    {
        if (depth > 64) return false;
        var analysis = WmlEditorAnalysis.Build(text);
        var scopes = new Stack<(string Kind, bool HadElse)>();
        foreach (var token in analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive))
        {
            var instruction = text.Substring(token.StartOffset, token.Length);
            var comment = instruction.IndexOf('#', 1);
            if (comment >= 0) instruction = instruction[..comment];
            var words = instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var name = words[0][1..];
            if (name == "define")
            {
                if (words.Length < 2 || !LiteralInventoryName(words[1]) || scopes.Any(scope => scope.Kind == "define")) return false;
                scopes.Push(("define", false));
            }
            else if (WmlAuthoredStructureScanner.IsConditionalStart(name))
            {
                if (words.Length < 2 || words.Skip(1).Any(word => word.IndexOfAny(['{', '}']) >= 0)) return false;
                scopes.Push(("conditional", false));
            }
            else if (name == "else")
            {
                if (scopes.Count == 0 || scopes.Peek().Kind != "conditional" || scopes.Peek().HadElse) return false;
                scopes.Pop(); scopes.Push(("conditional", true));
            }
            else if (name is "endif" or "enddef")
            {
                var expected = name == "endif" ? "conditional" : "define";
                if (scopes.Count == 0 || scopes.Pop().Kind != expected) return false;
            }
            else if (name == "arg")
            {
                if (words.Length != 2 || !LiteralInventoryName(words[1]) || !scopes.Any(scope => scope.Kind == "define")) return false;
                scopes.Push(("arg", false));
            }
            else if (name == "endarg")
            {
                if (scopes.Count == 0 || scopes.Pop().Kind != "arg") return false;
            }
        }
        if (scopes.Count != 0) return false;
        var sourceText = MaskDefinitionInventoryText(text, analysis); var quoted = false;
        for (var offset = 0; offset < sourceText.Length; offset++)
        {
            if (sourceText[offset] == '"') { quoted = !quoted; continue; }
            if (sourceText[offset] == '}' && !quoted) return false;
            if (sourceText[offset] != '{') continue;
            var end = DefinitionInventoryBraceEnd(sourceText, offset);
            if (end < 0 || !WmlMacroInvocationSyntaxScanner.TryParseSingle(sourceText[offset..(end + 1)], out var call)
                || call is null || !LiteralInventoryName(call.Name)) return false;
            foreach (var argument in call.Arguments)
                if (!CompleteDefinitionInventoryText(argument.RawText, depth + 1)) return false;
            offset = end;
        }
        return !quoted;
    }

    private static bool LiteralInventoryName(string name) => WmlPreprocessorToken.IsLiteralName(name) && !name.Contains('$');

    private static int DefinitionInventoryBraceEnd(string text, int start)
    {
        var braces = 0; var quoted = false; var raw = false;
        for (var offset = start; offset < text.Length; offset++)
        {
            var character = text[offset];
            if (raw)
            {
                if (character == '>' && offset + 1 < text.Length && text[offset + 1] == '>') { raw = false; offset++; }
                continue;
            }
            if (character == '"') { quoted = !quoted; continue; }
            if (!quoted && character == '<' && offset + 1 < text.Length && text[offset + 1] == '<') { raw = true; offset++; continue; }
            if (!quoted && character == '#') { while (offset < text.Length && text[offset] is not ('\r' or '\n')) offset++; continue; }
            if (quoted) continue;
            if (character == '{') braces++;
            else if (character == '}' && --braces == 0) return offset;
        }
        return -1;
    }
}
