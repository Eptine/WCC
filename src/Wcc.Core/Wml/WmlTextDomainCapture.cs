namespace Wcc.Core.Wml;

/// <summary>Captures an explicit lexical domain without guessing inherited include context or a runtime branch.</summary>
public static class WmlTextDomainCapture
{
    public static string? BeforeOffset(string source, int beforeOffset)
    {
        if (beforeOffset < 0 || beforeOffset > source.Length) return null;
        var structure = WmlAuthoredStructureScanner.Scan(source);
        return BeforeLine(source, structure, structure.Lines.TakeWhile(line => line.StartOffset < beforeOffset).Count());
    }

    public static string? BeforeLine(string source, int beforeLineIndex) =>
        BeforeLine(source, WmlAuthoredStructureScanner.Scan(source), beforeLineIndex);

    private readonly record struct Branch(int OpeningLine, bool Alternative);

    private static string? BeforeLine(string source, WmlAuthoredStructure structure, int beforeLineIndex)
    {
        if (beforeLineIndex <= 0) return null;
        var ancestry = new List<Branch>();
        var candidates = new List<(string? Value, Branch[] Ancestry)>();
        foreach (var directive in structure.Directives.Where(item => item.DefinitionDepth == 0 && item.Line < beforeLineIndex)
                     .OrderBy(item => item.Line))
        {
            if (directive.Line < 0 || directive.Line >= structure.Lines.Count) return null;
            var line = structure.Lines[directive.Line];
            var raw = source[line.StartOffset..line.ContentEndOffset].TrimStart();
            var text = "#" + WmlInlineCommentSplitter.Split(raw[1..]).Value.TrimEnd();
            if (WmlAuthoredStructureScanner.IsConditionalStart(directive.Name))
                ancestry.Add(new(directive.Line, false));
            else if (directive.Name == "else")
            {
                if (ancestry.Count == 0 || ancestry[^1].Alternative || text != "#else") return null;
                ancestry[^1] = ancestry[^1] with { Alternative = true };
            }
            else if (directive.Name == "endif")
            {
                if (ancestry.Count == 0 || text != "#endif") return null;
                ancestry.RemoveAt(ancestry.Count - 1);
            }
            else if (directive.Name == "textdomain")
            {
                if (!text.StartsWith("#textdomain", StringComparison.Ordinal)) return null;
                var value = text["#textdomain".Length..].Trim();
                candidates.Add((value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
                    ? value : null, ancestry.ToArray()));
            }
        }
        string? domain = null;
        foreach (var candidate in candidates)
        {
            // A physical ELSE sibling cannot execute when the target is reached.
            // Otherwise a closed/optional branch might have changed the domain;
            // its text is not evidence for one chosen runtime condition.
            if (candidate.Ancestry.Any(branch => ancestry.Any(target => target.OpeningLine == branch.OpeningLine
                    && target.Alternative != branch.Alternative))) continue;
            domain = candidate.Ancestry.Length <= ancestry.Count && candidate.Ancestry.SequenceEqual(ancestry.Take(candidate.Ancestry.Length))
                ? candidate.Value : null;
        }
        return domain;
    }
}