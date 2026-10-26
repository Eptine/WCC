using System.Linq;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Core editing operations for [story] / [part] blocks:
/// add, remove, reorder.
/// </summary>
public static class ScenarioStoryEditor
{
    public static StoryPartDocument InsertNewPart(
        ScenarioStoryDocument storyDocument,
        int insertIndex,
        StoryPart? initialPart = null)
    {
        if (storyDocument is null) throw new ArgumentNullException(nameof(storyDocument));
        if (insertIndex < 0 || insertIndex > storyDocument.PartDocuments.Count)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        var part = initialPart ?? new StoryPart();

        var newPartEditable = CreateEditablePart(storyDocument.EditableBlock.NewLine, GuessPartIndent(storyDocument));
        StoryPartPatcher.Apply(part, newPartEditable);

        int insertLineIndex = WmlChildInsertionPlanner.GetInsertLineIndex(
            storyDocument.EditableBlock,
            "part",
            insertIndex
        );

        InsertLines(storyDocument.EditableBlock, insertLineIndex, newPartEditable.Lines);

        var newDoc = new StoryPartDocument
        {
            Span = new WmlLineBlockSpan("part", -1, -1),
            EditableBlock = newPartEditable,
            Part = part
        };

        storyDocument.PartDocuments.Insert(insertIndex, newDoc);
        storyDocument.Story.Parts.Insert(insertIndex, part);

        return newDoc;
    }

    public static void RemovePartAt(ScenarioStoryDocument storyDocument, int index)
    {
        if (storyDocument is null) throw new ArgumentNullException(nameof(storyDocument));
        if (index < 0 || index >= storyDocument.PartDocuments.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var currentPartSpans = WmlDirectChildBlockScanner.FindSpans(storyDocument.EditableBlock, "part");
        var span = currentPartSpans[index];

        RemoveLines(
            storyDocument.EditableBlock,
            span.StartLineIndex,
            span.EndLineIndexExclusive - span.StartLineIndex);

        storyDocument.PartDocuments.RemoveAt(index);
        storyDocument.Story.Parts.RemoveAt(index);
    }

    public static void MovePart(ScenarioStoryDocument storyDocument, int fromIndex, int toIndex)
    {
        if (storyDocument is null) throw new ArgumentNullException(nameof(storyDocument));
        if (fromIndex < 0 || fromIndex >= storyDocument.PartDocuments.Count)
            throw new ArgumentOutOfRangeException(nameof(fromIndex));
        if (toIndex < 0 || toIndex >= storyDocument.PartDocuments.Count)
            throw new ArgumentOutOfRangeException(nameof(toIndex));
        if (fromIndex == toIndex)
            return;

        var currentPartSpans = WmlLineBlockScanner.FindSpans(storyDocument.EditableBlock.Lines, "part");
        var movingSpan = currentPartSpans[fromIndex];

        var movingLines = storyDocument.EditableBlock.Lines
            .Skip(movingSpan.StartLineIndex)
            .Take(movingSpan.EndLineIndexExclusive - movingSpan.StartLineIndex)
            .ToList();

        var movingDoc = storyDocument.PartDocuments[fromIndex];
        var movingPart = storyDocument.Story.Parts[fromIndex];

        RemoveLines(
            storyDocument.EditableBlock,
            movingSpan.StartLineIndex,
            movingSpan.EndLineIndexExclusive - movingSpan.StartLineIndex);

        storyDocument.PartDocuments.RemoveAt(fromIndex);
        storyDocument.Story.Parts.RemoveAt(fromIndex);

        var spansAfterRemoval = WmlDirectChildBlockScanner.FindSpans(storyDocument.EditableBlock, "part");

        int insertLineIndex =
            toIndex >= spansAfterRemoval.Count
                ? storyDocument.EditableBlock.ClosingTagLineIndex
                : spansAfterRemoval[toIndex].StartLineIndex;

        InsertLines(storyDocument.EditableBlock, insertLineIndex, movingLines);

        storyDocument.PartDocuments.Insert(toIndex, movingDoc);
        storyDocument.Story.Parts.Insert(toIndex, movingPart);
    }

    private static WmlEditableBlock CreateEditablePart(string newLine, string partIndent)
    {
        var fieldIndent = partIndent + "    ";

        var raw = string.Join(newLine, new[]
        {
            $"{partIndent}[part]",
            $"{partIndent}[/part]"
        });

        var block = new WmlBlock("part", raw);
        return WmlEditableBlockParser.Parse(block, StoryPartSchema.KnownKeys);
    }

    private static string GuessPartIndent(ScenarioStoryDocument storyDocument)
    {
        var currentPartSpans = WmlDirectChildBlockScanner.FindSpans(storyDocument.EditableBlock, "part");
        if (currentPartSpans.Count > 0)
        {
            var firstPartOpenLine = storyDocument.EditableBlock.Lines[currentPartSpans[0].StartLineIndex];
            return TakeLeadingWhitespace(firstPartOpenLine);
        }

        // Fallback: indent one level deeper than [story]
        var storyOpenLine = storyDocument.EditableBlock.Lines[storyDocument.EditableBlock.OpeningTagLineIndex];
        return TakeLeadingWhitespace(storyOpenLine) + "    ";
    }

    private static void InsertLines(WmlEditableBlock block, int insertIndex, List<string> linesToInsert)
    {
        block.Lines.InsertRange(insertIndex, linesToInsert);

        foreach (var key in block.Known.Keys.ToList())
        {
            var entry = block.Known[key];
            if (entry.LineIndex >= insertIndex)
                block.Known[key] = entry with { LineIndex = entry.LineIndex + linesToInsert.Count };
        }

        if (block.ClosingTagLineIndex >= insertIndex)
            block.ClosingTagLineIndex += linesToInsert.Count;
    }

    private static void RemoveLines(WmlEditableBlock block, int startIndex, int count)
    {
        block.Lines.RemoveRange(startIndex, count);

        foreach (var key in block.Known.Keys.ToList())
        {
            var entry = block.Known[key];

            if (entry.LineIndex >= startIndex + count)
                block.Known[key] = entry with { LineIndex = entry.LineIndex - count };
        }

        if (block.ClosingTagLineIndex >= startIndex + count)
            block.ClosingTagLineIndex -= count;
    }

    private static string TakeLeadingWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return s[..i];
    }
}