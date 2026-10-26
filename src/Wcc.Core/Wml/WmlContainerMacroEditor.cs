using System.Linq;

namespace Wcc.Core.Wml;

/// <summary>
/// Edits one-line macro invocations inside container tags like:
/// [abilities] {ABILITY_SUBMERGE} [/abilities]
/// [specials] {WEAPON_SPECIAL_DRAIN} [/specials]
///
/// v0.1 scope:
/// - only top-level one-line macros inside the container
/// - preserves all non-macro lines and nested tags
/// - edits only the first matching direct child container in the block
/// - inserts the container if missing and macros are provided
/// </summary>
public static class WmlContainerMacroEditor
{
    public static void SetTopLevelMacros(
        WmlEditableBlock block,
        string containerTagName,
        IReadOnlyList<string> macros)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (string.IsNullOrWhiteSpace(containerTagName))
            throw new ArgumentException("Container tag name must be non-empty.", nameof(containerTagName));

        var spans = WmlDirectChildBlockScanner.FindSpans(block, containerTagName);

        if (spans.Count == 0)
        {
            if (macros.Count == 0)
                return;

            InsertNewContainer(block, containerTagName, macros);
            return;
        }

        var span = spans[0];

        var macroLineIndices = FindTopLevelMacroLineIndices(
            block.Lines,
            span.StartLineIndex + 1,
            span.EndLineIndexExclusive - 1);

        // Rendering an unchanged model must not move macros across comments,
        // nested tags or preprocessing branches. Preserve the exact source.
        if (macroLineIndices.Select(index => block.Lines[index].Trim())
            .SequenceEqual(macros.Select(NormalizeMacro), StringComparer.Ordinal))
            return;

        int insertionIndex = macroLineIndices.Count > 0
            ? macroLineIndices[0]
            : span.EndLineIndexExclusive - 1;

        string macroIndent = macroLineIndices.Count > 0
            ? TakeLeadingWhitespace(block.Lines[macroLineIndices[0]])
            : GuessNestedIndent(block.Lines[span.StartLineIndex]);

        // Remove existing macro lines bottom-up so indices remain valid
        for (int i = macroLineIndices.Count - 1; i >= 0; i--)
        {
            RemoveLine(block, macroLineIndices[i]);
        }

        // Insert new macro lines at the first old macro position,
        // or before the closing container tag if none existed.
        for (int i = 0; i < macros.Count; i++)
        {
            InsertLine(block, insertionIndex + i, macroIndent + NormalizeMacro(macros[i]));
        }
    }

    private static void InsertNewContainer(
        WmlEditableBlock block,
        string containerTagName,
        IReadOnlyList<string> macros)
    {
        string containerIndent = GuessNestedIndent(block.Lines[block.OpeningTagLineIndex]);
        string macroIndent = containerIndent + "    ";

        int insertIndex = block.ClosingTagLineIndex;

        var linesToInsert = new List<string>
        {
            $"{containerIndent}[{containerTagName}]"
        };

        linesToInsert.AddRange(macros.Select(m => macroIndent + NormalizeMacro(m)));
        linesToInsert.Add($"{containerIndent}[/{containerTagName}]");

        for (int i = 0; i < linesToInsert.Count; i++)
        {
            InsertLine(block, insertIndex + i, linesToInsert[i]);
        }
    }

    private static List<int> FindTopLevelMacroLineIndices(
        IReadOnlyList<string> lines,
        int startLineIndex,
        int endLineIndexExclusive)
    {
        var result = new List<int>();
        int depth = 0;

        for (int i = startLineIndex; i < endLineIndexExclusive; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0)
                continue;

            if (IsAnyOpeningTagLine(t))
            {
                depth++;
                continue;
            }

            if (IsAnyClosingTagLine(t))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0)
                continue;

            if (t.StartsWith('{') && t.EndsWith('}'))
                result.Add(i);
        }

        return result;
    }

    private static void InsertLine(WmlEditableBlock block, int index, string line)
    {
        block.Lines.Insert(index, line);

        foreach (var key in block.Known.Keys.ToList())
        {
            var e = block.Known[key];
            if (e.LineIndex >= index)
                block.Known[key] = e with { LineIndex = e.LineIndex + 1 };
        }

        if (block.ClosingTagLineIndex >= index)
            block.ClosingTagLineIndex++;
    }

    private static void RemoveLine(WmlEditableBlock block, int index)
    {
        block.Lines.RemoveAt(index);

        foreach (var key in block.Known.Keys.ToList())
        {
            var e = block.Known[key];
            if (e.LineIndex > index)
                block.Known[key] = e with { LineIndex = e.LineIndex - 1 };
        }

        if (block.ClosingTagLineIndex > index)
            block.ClosingTagLineIndex--;
    }

    private static string NormalizeMacro(string raw)
    {
        var t = raw.Trim();

        if (t.Length == 0)
            throw new ArgumentException("Macro text must be non-empty.");

        if (t.StartsWith('{') && t.EndsWith('}'))
            return t;

        return "{" + t + "}";
    }

    private static string GuessNestedIndent(string openingTagLine)
    {
        return TakeLeadingWhitespace(openingTagLine) + "    ";
    }

    private static string TakeLeadingWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return s[..i];
    }

    private static bool IsAnyOpeningTagLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        return inner.Length > 0 && !inner.StartsWith('/');
    }

    private static bool IsAnyClosingTagLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('[') || !trimmedLine.EndsWith(']'))
            return false;

        var inner = trimmedLine[1..^1].Trim();
        return inner.StartsWith('/');
    }
}