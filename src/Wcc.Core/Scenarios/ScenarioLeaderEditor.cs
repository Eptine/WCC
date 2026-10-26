using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record ScenarioLeaderRawValidationResult(bool IsValid, string? Problem = null);

/// <summary>
/// Structural operations for authoritative direct [leader] children. WML inside
/// unresolved macro arguments is deliberately excluded by the projection parser.
/// </summary>
public static class ScenarioLeaderEditor
{
    public static ScenarioLeaderRawValidationResult ValidateRawLeader(string rawWml)
    {
        var result = WmlSingleBlockValidator.Validate(rawWml, "leader");
        return new ScenarioLeaderRawValidationResult(result.IsValid, result.Problem);
    }

    public static ScenarioLeaderDocument ReplaceLiteralLeader(
        ScenarioSideDocument sideDocument,
        int nestedIndex,
        string rawWml)
    {
        if (sideDocument is null) throw new ArgumentNullException(nameof(sideDocument));
        var validation = ValidateRawLeader(rawWml);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Problem, nameof(rawWml));
        var nested = sideDocument.NestedLeaderDocuments;
        if (nestedIndex < 0 || nestedIndex >= nested.Count)
            throw new ArgumentOutOfRangeException(nameof(nestedIndex));
        var current = nested[nestedIndex];
        var spans = FindLiteralLeaderSpans(sideDocument.EditableBlock);
        var span = spans[nestedIndex];
        var replacementLines = rawWml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        if (replacementLines.Count > 1 && replacementLines[^1].Length == 0)
            replacementLines.RemoveAt(replacementLines.Count - 1);
        WmlEditableBlockLineEditor.ReplaceLines(
            sideDocument.EditableBlock,
            span.StartLineIndex,
            span.EndLineIndexExclusive,
            replacementLines);

        var imported = ScenarioImporter.ImportLiteralLeader(
            string.Join(sideDocument.EditableBlock.NewLine, replacementLines),
            new WmlLineBlockSpan("leader", span.StartLineIndex, span.StartLineIndex + replacementLines.Count),
            isConditional: current.IsConditional,
            current.InternalId);
        var combinedIndex = sideDocument.LeaderDocuments.IndexOf(current);
        sideDocument.LeaderDocuments[combinedIndex] = imported;
        sideDocument.Side.NestedLeaders[nestedIndex] = imported.Leader;
        return imported;
    }

    public static IReadOnlyList<WmlLineBlockSpan> FindLiteralLeaderSpans(
        WmlEditableBlock sideBlock)
    {
        if (sideBlock is null) throw new ArgumentNullException(nameof(sideBlock));
        var tree = WmlProjectionTreeParser.Parse(sideBlock.ToRawText());
        var side = tree.Roots.FirstOrDefault(x =>
            string.Equals(x.TagName, "side", StringComparison.OrdinalIgnoreCase));
        if (side is null)
            return [];

        return side.Children
            .Where(x => string.Equals(x.TagName, "leader", StringComparison.OrdinalIgnoreCase))
            .Select(x => new WmlLineBlockSpan("leader", x.StartLine, x.EndLineExclusive))
            .ToArray();
    }

    public static ScenarioLeaderDocument InsertNewLeader(
        ScenarioSideDocument sideDocument,
        int insertIndex,
        ScenarioLeader? initialLeader = null)
    {
        if (sideDocument is null) throw new ArgumentNullException(nameof(sideDocument));
        var nestedDocuments = sideDocument.NestedLeaderDocuments;
        if (insertIndex < 0 || insertIndex > nestedDocuments.Count)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        var leader = initialLeader ?? new ScenarioLeader { CanRecruit = "yes" };
        var editable = CreateEditableLeader(
            sideDocument.EditableBlock.NewLine,
            GuessLeaderIndent(sideDocument));
        ScenarioLeaderPatcher.Apply(leader, editable);

        var insertLine = WmlChildInsertionPlanner.GetInsertLineIndex(
            sideDocument.EditableBlock,
            "leader",
            insertIndex,
            "unit",
            "village",
            "ai",
            "variables");
        WmlEditableBlockLineEditor.ReplaceLines(
            sideDocument.EditableBlock,
            insertLine,
            insertLine,
            editable.Lines);

        var document = new ScenarioLeaderDocument
        {
            SourceKind = ScenarioLeaderSourceKind.LiteralNested,
            Span = new WmlLineBlockSpan("leader", -1, -1),
            EditableBlock = editable,
            Leader = leader
        };
        var combinedIndex = sideDocument.LeaderDocuments.FindIndex(x =>
            x.SourceKind == ScenarioLeaderSourceKind.LiteralNested);
        if (combinedIndex < 0)
            combinedIndex = sideDocument.LeaderDocuments.Count;
        sideDocument.LeaderDocuments.Insert(combinedIndex + insertIndex, document);
        sideDocument.Side.NestedLeaders.Insert(insertIndex, leader);
        return document;
    }

    public static void RemoveLeaderAt(ScenarioSideDocument sideDocument, int nestedIndex)
    {
        if (sideDocument is null) throw new ArgumentNullException(nameof(sideDocument));
        var nested = sideDocument.NestedLeaderDocuments;
        if (nestedIndex < 0 || nestedIndex >= nested.Count)
            throw new ArgumentOutOfRangeException(nameof(nestedIndex));
        if (!nested[nestedIndex].CanRemove)
            throw new InvalidOperationException("Conditional leader WML cannot be removed structurally.");

        var spans = FindLiteralLeaderSpans(sideDocument.EditableBlock);
        var span = spans[nestedIndex];
        WmlEditableBlockLineEditor.ReplaceLines(
            sideDocument.EditableBlock,
            span.StartLineIndex,
            span.EndLineIndexExclusive,
            []);

        sideDocument.LeaderDocuments.Remove(nested[nestedIndex]);
        sideDocument.Side.NestedLeaders.RemoveAt(nestedIndex);
    }

    public static void MoveLeader(
        ScenarioSideDocument sideDocument,
        int fromNestedIndex,
        int toNestedIndex)
    {
        if (sideDocument is null) throw new ArgumentNullException(nameof(sideDocument));
        var nested = sideDocument.NestedLeaderDocuments;
        if (fromNestedIndex < 0 || fromNestedIndex >= nested.Count)
            throw new ArgumentOutOfRangeException(nameof(fromNestedIndex));
        if (toNestedIndex < 0 || toNestedIndex >= nested.Count)
            throw new ArgumentOutOfRangeException(nameof(toNestedIndex));
        if (fromNestedIndex == toNestedIndex)
            return;
        if (nested.Any(x => x.IsConditional))
        {
            throw new InvalidOperationException(
                "Leader order cannot be changed while conditional [leader] blocks are present.");
        }

        var spans = FindLiteralLeaderSpans(sideDocument.EditableBlock);
        var movingSpan = spans[fromNestedIndex];
        var movingLines = sideDocument.EditableBlock.Lines
            .Skip(movingSpan.StartLineIndex)
            .Take(movingSpan.EndLineIndexExclusive - movingSpan.StartLineIndex)
            .ToList();
        var movingDocument = nested[fromNestedIndex];
        var movingLeader = sideDocument.Side.NestedLeaders[fromNestedIndex];

        WmlEditableBlockLineEditor.ReplaceLines(
            sideDocument.EditableBlock,
            movingSpan.StartLineIndex,
            movingSpan.EndLineIndexExclusive,
            []);
        sideDocument.LeaderDocuments.Remove(movingDocument);
        sideDocument.Side.NestedLeaders.RemoveAt(fromNestedIndex);

        var spansAfterRemoval = FindLiteralLeaderSpans(sideDocument.EditableBlock);
        var insertLine = toNestedIndex >= spansAfterRemoval.Count
            ? WmlChildInsertionPlanner.GetInsertLineIndex(
                sideDocument.EditableBlock,
                "leader",
                spansAfterRemoval.Count,
                "unit",
                "village",
                "ai",
                "variables")
            : spansAfterRemoval[toNestedIndex].StartLineIndex;
        WmlEditableBlockLineEditor.ReplaceLines(
            sideDocument.EditableBlock,
            insertLine,
            insertLine,
            movingLines);

        var firstNested = sideDocument.LeaderDocuments.FindIndex(x =>
            x.SourceKind == ScenarioLeaderSourceKind.LiteralNested);
        if (firstNested < 0)
            firstNested = sideDocument.LeaderDocuments.Count;
        sideDocument.LeaderDocuments.Insert(firstNested + toNestedIndex, movingDocument);
        sideDocument.Side.NestedLeaders.Insert(toNestedIndex, movingLeader);
    }

    private static WmlEditableBlock CreateEditableLeader(string newLine, string indent)
    {
        var raw = string.Join(newLine, [$"{indent}[leader]", $"{indent}[/leader]"]);
        return WmlEditableBlockParser.Parse(
            new WmlBlock("leader", raw),
            ScenarioLeaderSchema.KnownKeys);
    }

    private static string GuessLeaderIndent(ScenarioSideDocument sideDocument)
    {
        var spans = FindLiteralLeaderSpans(sideDocument.EditableBlock);
        if (spans.Count > 0)
            return TakeLeadingWhitespace(sideDocument.EditableBlock.Lines[spans[0].StartLineIndex]);
        return TakeLeadingWhitespace(
            sideDocument.EditableBlock.Lines[sideDocument.EditableBlock.OpeningTagLineIndex]) + "    ";
    }

    private static string TakeLeadingWhitespace(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
            index++;
        return value[..index];
    }
}
