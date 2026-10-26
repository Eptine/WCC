namespace Wcc.Core.Wml;

/// <summary>
/// A tolerant, read-only tree over already-expanded WML. It is intended for
/// projections and diagnostics, never as the authoritative persistence model.
/// </summary>
public sealed class WmlProjectionNode
{
    public required string TagName { get; init; }
    public required int StartLine { get; init; }
    public int EndLineExclusive { get; set; }
    public bool PreprocessorConditional { get; init; }
    public WmlProjectionNode? Parent { get; init; }
    public required IReadOnlyList<string> SourceLines { get; init; }
    public List<WmlProjectionNode> Children { get; } = [];

    public IReadOnlyList<string> Lines => SourceLines
        .Skip(StartLine)
        .Take(EndLineExclusive - StartLine)
        .ToArray();

    public string RawText => string.Join("\n", Lines);

    public IEnumerable<WmlProjectionNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        foreach (var nested in child.DescendantsAndSelf())
            yield return nested;
    }

    public IEnumerable<WmlProjectionNode> Ancestors()
    {
        for (var current = Parent; current is not null; current = current.Parent)
            yield return current;
    }
}

public sealed class WmlProjectionTree
{
    public List<WmlProjectionNode> Roots { get; } = [];
}

public static class WmlProjectionTreeParser
{
    public static WmlProjectionTree Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var result = new WmlProjectionTree();
        var stack = new Stack<WmlProjectionNode>();
        var preprocessorDepth = 0;
        var stringState = new WmlStringScanState();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var lineStartedInsideMultilineString = stringState.InsideMultilineString;
            var rawTrimmed = lines[lineIndex].Trim();
            if (!lineStartedInsideMultilineString &&
                rawTrimmed.StartsWith('{') &&
                TryReadWholeInvocation(lines, lineIndex, out var consumedLineCount))
            {
                // Keep lexical state correct even when the invocation owns
                // raw-string arguments spread over its consumed lines.
                for (var offset = 0; offset < consumedLineCount; offset++)
                    WmlRawStringScanner.UpdateState(lines[lineIndex + offset], ref stringState);
                lineIndex += consumedLineCount - 1;
                continue;
            }

            WmlRawStringScanner.UpdateState(lines[lineIndex], ref stringState);
            if (lineStartedInsideMultilineString)
                continue;

            var directive = WmlPreprocessorScanner.GetDirectiveKind(rawTrimmed);
            if (directive == WmlPreprocessorDirectiveKind.EndIf)
                preprocessorDepth = Math.Max(0, preprocessorDepth - 1);

            // A tag may carry a trailing WML comment. Parse only the code part;
            // otherwise a comment ending in "[something]" can become part of
            // the tag name and leave the projection stack permanently open.
            var tagCode = WmlInlineCommentSplitter.Split(lines[lineIndex]).Value.Trim();

            if (TryReadClosingTag(tagCode, out var closingTag))
            {
                if (stack.Count > 0 &&
                    string.Equals(stack.Peek().TagName, closingTag, StringComparison.OrdinalIgnoreCase))
                {
                    stack.Pop().EndLineExclusive = lineIndex + 1;
                }
            }
            else if (TryReadOpeningTag(tagCode, out var openingTag))
            {
                var parent = stack.Count > 0 ? stack.Peek() : null;
                var node = new WmlProjectionNode
                {
                    TagName = openingTag,
                    StartLine = lineIndex,
                    EndLineExclusive = lineIndex + 1,
                    PreprocessorConditional = preprocessorDepth > 0,
                    Parent = parent,
                    SourceLines = lines
                };
                if (parent is null)
                    result.Roots.Add(node);
                else
                    parent.Children.Add(node);
                stack.Push(node);
            }

            if (directive == WmlPreprocessorDirectiveKind.IfStart)
                preprocessorDepth++;
        }

        while (stack.Count > 0)
            stack.Pop().EndLineExclusive = lines.Length;
        return result;
    }

    private static bool TryReadWholeInvocation(
        IReadOnlyList<string> lines,
        int startLineIndex,
        out int consumedLineCount)
    {
        return WmlWholeLineMacroInvocationReader.TryRead(
            lines,
            startLineIndex,
            out _,
            out _,
            out consumedLineCount);
    }

    private static bool TryReadOpeningTag(string line, out string tagName)
    {
        tagName = string.Empty;
        if (!line.StartsWith('[') || !line.EndsWith(']') || line.Contains("][/", StringComparison.Ordinal))
            return false;
        var inner = line[1..^1].Trim();
        if (inner.Length == 0 || inner.StartsWith('/'))
            return false;
        tagName = ReadTagName(inner).TrimStart('+', '-');
        return tagName.Length > 0;
    }

    private static bool TryReadClosingTag(string line, out string tagName)
    {
        tagName = string.Empty;
        if (!line.StartsWith("[/", StringComparison.Ordinal) || !line.EndsWith(']'))
            return false;
        tagName = ReadTagName(line[2..^1].Trim()).TrimStart('+', '-');
        return tagName.Length > 0;
    }

    private static string ReadTagName(string value)
    {
        var stop = value.IndexOfAny([' ', '\t']);
        return stop < 0 ? value : value[..stop];
    }
}
