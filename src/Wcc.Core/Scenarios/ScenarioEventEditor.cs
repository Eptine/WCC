using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record ScenarioEventRawValidationResult(bool IsValid, string? Problem = null);

public static class ScenarioEventEditor
{
    private static readonly HashSet<string> EventNameKey =
        new(["name"], StringComparer.OrdinalIgnoreCase);

    public static string CreateNewEventTemplate(ScenarioDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var indent = GuessEventIndent(document);
        var childIndent = indent + "    ";
        return string.Join(document.EditableBlock.NewLine,
        [
            $"{indent}[event]",
            $"{childIndent}name=",
            $"{indent}[/event]"
        ]);
    }

    public static IReadOnlyList<WmlLineBlockSpan> FindDirectEventSpans(ScenarioDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var tree = WmlProjectionTreeParser.Parse(document.EditableBlock.ToRawText());
        var scenario = tree.Roots.FirstOrDefault(x =>
            string.Equals(x.TagName, "scenario", StringComparison.OrdinalIgnoreCase));
        if (scenario is null)
            return [];

        return scenario.Children
            .Where(x => string.Equals(x.TagName, "event", StringComparison.OrdinalIgnoreCase))
            .Where(x => !WmlInlineCommentSplitter.Split(x.SourceLines[x.StartLine]).Value.Trim()
                .StartsWith("[+event", StringComparison.OrdinalIgnoreCase))
            .Select(x => new WmlLineBlockSpan("event", x.StartLine, x.EndLineExclusive))
            .ToArray();
    }

    public static ScenarioEventRawValidationResult ValidateRawEvent(
        string rawWml,
        bool requireDirectTriggerName)
    {
        if (string.IsNullOrWhiteSpace(rawWml))
            return new ScenarioEventRawValidationResult(false, "Event WML cannot be empty.");

        if (!TryValidateBalancedTags(rawWml, out var balanceProblem))
            return new ScenarioEventRawValidationResult(false, balanceProblem);
        if (!TryValidatePreprocessorBalance(rawWml, out var preprocessorProblem))
            return new ScenarioEventRawValidationResult(false, preprocessorProblem);

        var tree = WmlProjectionTreeParser.Parse(rawWml);
        if (tree.Roots.Count != 1 ||
            !string.Equals(tree.Roots[0].TagName, "event", StringComparison.OrdinalIgnoreCase))
        {
            return new ScenarioEventRawValidationResult(
                false,
                "The editor must contain exactly one complete [event]...[/event] block.");
        }

        var eventNode = tree.Roots[0];
        var sourceLines = rawWml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var firstContentLine = Array.FindIndex(sourceLines, x => !string.IsNullOrWhiteSpace(x));
        var lastContentLine = Array.FindLastIndex(sourceLines, x => !string.IsNullOrWhiteSpace(x));
        if (eventNode.StartLine != firstContentLine ||
            eventNode.EndLineExclusive - 1 != lastContentLine ||
            lastContentLine < 0 ||
            !string.Equals(sourceLines[lastContentLine].Trim(), "[/event]", StringComparison.OrdinalIgnoreCase))
        {
            return new ScenarioEventRawValidationResult(
                false,
                "Text outside the single [event] block cannot be inserted into the scenario.");
        }

        if (requireDirectTriggerName)
        {
            var values = WmlKeyValueExtractor.Extract(
                new WmlBlock("event", eventNode.RawText),
                EventNameKey);
            if (!values.Known.TryGetValue("name", out var name) ||
                string.IsNullOrWhiteSpace(WmlDisplayStringNormalizer.Normalize(name)))
            {
                return new ScenarioEventRawValidationResult(
                    false,
                    "A new event needs a non-empty direct name= trigger.");
            }
        }

        return new ScenarioEventRawValidationResult(true);
    }

    public static void ReplaceDirectEvent(
        ScenarioDocument document,
        int directEventOrdinal,
        string rawWml)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var validation = ValidateRawEvent(rawWml, requireDirectTriggerName: false);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Problem, nameof(rawWml));

        var spans = FindDirectEventSpans(document);
        if (directEventOrdinal < 0 || directEventOrdinal >= spans.Count)
            throw new ArgumentOutOfRangeException(nameof(directEventOrdinal));

        var span = spans[directEventOrdinal];
        WmlEditableBlockLineEditor.ReplaceLines(
            document.EditableBlock,
            span.StartLineIndex,
            span.EndLineIndexExclusive,
            SplitLines(rawWml));
    }

    public static int InsertDirectEvent(ScenarioDocument document, string rawWml)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var validation = ValidateRawEvent(rawWml, requireDirectTriggerName: true);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Problem, nameof(rawWml));

        var spans = FindDirectEventSpans(document);
        var eventCount = spans.Count;
        var insertLine = spans.Count == 0
            ? document.EditableBlock.ClosingTagLineIndex
            : WmlPreprocessorScanner.FindEnclosingDirectChildRegion(
                    document.EditableBlock,
                    spans[^1].StartLineIndex)?.EndLineIndexExclusive ??
              spans[^1].EndLineIndexExclusive;
        var replacement = SplitLines(rawWml);
        if (insertLine > 0 &&
            !string.IsNullOrWhiteSpace(document.EditableBlock.Lines[insertLine - 1]) &&
            replacement.Count > 0)
        {
            replacement.Insert(0, string.Empty);
        }

        WmlEditableBlockLineEditor.ReplaceLines(
            document.EditableBlock,
            insertLine,
            insertLine,
            replacement);
        return eventCount;
    }

    private static List<string> SplitLines(string rawWml)
    {
        var normalized = rawWml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static bool TryValidateBalancedTags(string rawWml, out string? problem)
    {
        problem = null;
        var stack = new Stack<string>();
        var lines = rawWml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var trimmed = lines[lineIndex].Trim();
            if (!TryReadTagLine(trimmed, out var tagName, out var closing))
                continue;

            if (!closing)
            {
                stack.Push(tagName!);
                continue;
            }

            if (stack.Count == 0 ||
                !string.Equals(stack.Peek(), tagName, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"Line {lineIndex + 1} closes [/{tagName}] without a matching open tag.";
                return false;
            }
            stack.Pop();
        }

        if (stack.Count == 0)
            return true;

        problem = $"The [{stack.Peek()}] tag is not closed.";
        return false;
    }

    private static bool TryReadTagLine(
        string trimmed,
        out string? tagName,
        out bool closing)
    {
        tagName = null;
        closing = false;
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            return false;

        var inner = trimmed[1..^1].Trim();
        if (inner.Length == 0)
            return false;
        closing = inner.StartsWith('/');
        if (closing)
            inner = inner[1..].TrimStart();
        var stop = inner.IndexOfAny([' ', '\t']);
        tagName = (stop < 0 ? inner : inner[..stop]).TrimStart('+', '-');
        return tagName.Length > 0;
    }

    private static bool TryValidatePreprocessorBalance(string rawWml, out string? problem)
    {
        problem = null;
        var depth = 0;
        var lines = rawWml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var kind = WmlPreprocessorScanner.GetDirectiveKind(lines[lineIndex].Trim());
            if (kind == WmlPreprocessorDirectiveKind.IfStart)
                depth++;
            else if (kind == WmlPreprocessorDirectiveKind.EndIf)
            {
                if (depth == 0)
                {
                    problem = $"Line {lineIndex + 1} contains #endif without a matching conditional start.";
                    return false;
                }
                depth--;
            }
        }

        if (depth == 0)
            return true;

        problem = "A preprocessor conditional inside the event is missing #endif.";
        return false;
    }

    private static string GuessEventIndent(ScenarioDocument document)
    {
        var spans = FindDirectEventSpans(document);
        if (spans.Count > 0)
            return TakeLeadingWhitespace(document.EditableBlock.Lines[spans[0].StartLineIndex]);

        return TakeLeadingWhitespace(
                   document.EditableBlock.Lines[document.EditableBlock.OpeningTagLineIndex]) +
               "    ";
    }

    private static string TakeLeadingWhitespace(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
            index++;
        return value[..index];
    }
}
