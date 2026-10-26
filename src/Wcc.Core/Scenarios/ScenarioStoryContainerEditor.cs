using System.Linq;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static class ScenarioStoryContainerEditor
{
    private static readonly HashSet<string> NoKnownKeys = new(StringComparer.OrdinalIgnoreCase);

    public static ScenarioStoryDocument EnsureFirstStory(ScenarioDocument scenarioDocument)
    {
        if (scenarioDocument is null) throw new ArgumentNullException(nameof(scenarioDocument));

        if (scenarioDocument.StoryDocuments.Count > 0)
            return scenarioDocument.StoryDocuments[0];

        var story = new ScenarioStory();

        var editable = CreateEditableStory(scenarioDocument.EditableBlock.NewLine, GuessStoryIndent(scenarioDocument));

        int insertLineIndex = WmlChildInsertionPlanner.GetInsertLineIndex(
            scenarioDocument.EditableBlock,
            "story",
            scenarioDocument.StoryDocuments.Count,
            "side",
            "event"
        );

        InsertLines(scenarioDocument.EditableBlock, insertLineIndex, editable.Lines);

        var doc = new ScenarioStoryDocument
        {
            Span = new WmlLineBlockSpan("story", -1, -1),
            EditableBlock = editable,
            Story = story
        };

        scenarioDocument.StoryDocuments.Insert(0, doc);
        scenarioDocument.Scenario.Stories.Insert(0, story);

        return doc;
    }

    private static WmlEditableBlock CreateEditableStory(string newLine, string storyIndent)
    {
        var raw = string.Join(newLine, new[]
        {
            $"{storyIndent}[story]",
            $"{storyIndent}[/story]"
        });

        var block = new WmlBlock("story", raw);
        return WmlEditableBlockParser.Parse(block, NoKnownKeys);
    }

    private static string GuessStoryIndent(ScenarioDocument scenarioDocument)
    {
        var storySpans = WmlLineBlockScanner.FindSpans(scenarioDocument.EditableBlock.Lines, "story");
        if (storySpans.Count > 0)
        {
            var firstStoryOpenLine = scenarioDocument.EditableBlock.Lines[storySpans[0].StartLineIndex];
            return TakeLeadingWhitespace(firstStoryOpenLine);
        }

        var scenarioOpenLine = scenarioDocument.EditableBlock.Lines[scenarioDocument.EditableBlock.OpeningTagLineIndex];
        return TakeLeadingWhitespace(scenarioOpenLine) + "    ";
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

    private static string TakeLeadingWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return s[..i];
    }
}