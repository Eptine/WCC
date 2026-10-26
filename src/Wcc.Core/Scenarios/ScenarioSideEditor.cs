using System.Linq;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record ScenarioSideRawValidationResult(bool IsValid, string? Problem = null);

public static class ScenarioSideEditor
{
    public static IReadOnlyList<WmlLineBlockSpan> FindLiteralSideSpans(
        WmlEditableBlock scenarioBlock)
    {
        if (scenarioBlock is null) throw new ArgumentNullException(nameof(scenarioBlock));
        var tree = WmlProjectionTreeParser.Parse(scenarioBlock.ToRawText());
        var scenario = tree.Roots.FirstOrDefault(x =>
            string.Equals(x.TagName, "scenario", StringComparison.OrdinalIgnoreCase));
        if (scenario is null)
            return [];
        return scenario.Children
            .Where(x => string.Equals(x.TagName, "side", StringComparison.OrdinalIgnoreCase))
            .Select(x => new WmlLineBlockSpan("side", x.StartLine, x.EndLineExclusive))
            .ToArray();
    }

    public static ScenarioSideRawValidationResult ValidateRawSide(string rawWml)
    {
        var result = WmlSingleBlockValidator.Validate(rawWml, "side");
        return new ScenarioSideRawValidationResult(result.IsValid, result.Problem);
    }

    public static ScenarioSideDocument ReplaceSideRaw(
        ScenarioDocument scenarioDocument,
        int sideIndex,
        string rawWml)
    {
        if (scenarioDocument is null) throw new ArgumentNullException(nameof(scenarioDocument));
        var validation = ValidateRawSide(rawWml);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Problem, nameof(rawWml));
        if (sideIndex < 0 || sideIndex >= scenarioDocument.SideDocuments.Count)
            throw new ArgumentOutOfRangeException(nameof(sideIndex));
        var current = scenarioDocument.SideDocuments[sideIndex];
        var wrapper = current.IsConditional
            ? $"[scenario]\n#ifdef WCC_RAW_SIDE_CONTEXT\n{rawWml}\n#endif\n[/scenario]"
            : $"[scenario]\n{rawWml}\n[/scenario]";
        var parsedScenario = ScenarioImporter.ImportAllFromText(wrapper).Single();
        var parsed = parsedScenario.SideDocuments.Single();
        var replacement = new ScenarioSideDocument
        {
            InternalId = current.InternalId,
            Span = current.Span,
            EditableBlock = parsed.EditableBlock,
            Side = parsed.Side,
            IsConditional = parsed.IsConditional
        };
        replacement.LeaderDocuments.AddRange(parsed.LeaderDocuments);

        var spans = FindLiteralSideSpans(scenarioDocument.EditableBlock);
        var span = spans[sideIndex];
        var lines = rawWml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        WmlEditableBlockLineEditor.ReplaceLines(
            scenarioDocument.EditableBlock,
            span.StartLineIndex,
            span.EndLineIndexExclusive,
            lines);
        scenarioDocument.SideDocuments[sideIndex] = replacement;
        scenarioDocument.Scenario.Sides[sideIndex] = replacement.Side;
        return replacement;
    }

    public static ScenarioSideDocument InsertNewSide(
        ScenarioDocument scenarioDocument,
        int insertIndex,
        ScenarioSide? initialSide = null)
    {
        if (scenarioDocument is null) throw new ArgumentNullException(nameof(scenarioDocument));
        if (insertIndex < 0 || insertIndex > scenarioDocument.SideDocuments.Count)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        var nextSideNumber =
            scenarioDocument.Scenario.Sides.Count == 0
                ? 1
                : scenarioDocument.Scenario.Sides
                    .Where(s => s.SideNumber.HasValue)
                    .Select(s => s.SideNumber!.Value)
                    .DefaultIfEmpty(0)
                    .Max() + 1;

        var side = initialSide ?? new ScenarioSide
        {
            SideNumber = nextSideNumber
        };

        var initialNestedLeaders = side.NestedLeaders.ToArray();
        side.NestedLeaders.Clear();
        var editable = CreateEditableSide(scenarioDocument.EditableBlock.NewLine, GuessSideIndent(scenarioDocument));
        ScenarioSidePatcher.Apply(side, editable);

        var newDoc = new ScenarioSideDocument
        {
            Span = new WmlLineBlockSpan("side", -1, -1),
            EditableBlock = editable,
            Side = side
        };
        if (side.LegacyLeader is { } legacy)
        {
            newDoc.LeaderDocuments.Add(new ScenarioLeaderDocument
            {
                SourceKind = ScenarioLeaderSourceKind.LegacyInline,
                EditableBlock = editable,
                Leader = legacy
            });
        }
        foreach (var leader in initialNestedLeaders)
            ScenarioLeaderEditor.InsertNewLeader(newDoc, newDoc.NestedLeaderDocuments.Count, leader);

        int insertLineIndex = GetSideInsertLineIndex(scenarioDocument, insertIndex);

        InsertLines(scenarioDocument.EditableBlock, insertLineIndex, editable.Lines);

        scenarioDocument.SideDocuments.Insert(insertIndex, newDoc);
        scenarioDocument.Scenario.Sides.Insert(insertIndex, side);

        return newDoc;
    }

    public static void RemoveSideAt(ScenarioDocument scenarioDocument, int index)
    {
        if (scenarioDocument is null) throw new ArgumentNullException(nameof(scenarioDocument));
        if (index < 0 || index >= scenarioDocument.SideDocuments.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (scenarioDocument.SideDocuments[index].IsConditional)
            throw new InvalidOperationException("Conditional [side] source cannot be removed structurally.");

        var currentSpans = FindLiteralSideSpans(scenarioDocument.EditableBlock);
        var span = currentSpans[index];

        RemoveLines(
            scenarioDocument.EditableBlock,
            span.StartLineIndex,
            span.EndLineIndexExclusive - span.StartLineIndex);

        scenarioDocument.SideDocuments.RemoveAt(index);
        scenarioDocument.Scenario.Sides.RemoveAt(index);
    }

    private static WmlEditableBlock CreateEditableSide(string newLine, string sideIndent)
    {
        var raw = string.Join(newLine, new[]
        {
            $"{sideIndent}[side]",
            $"{sideIndent}[/side]"
        });

        var block = new WmlBlock("side", raw);
        return WmlEditableBlockParser.Parse(block, ScenarioSideSchema.KnownKeys);
    }

    private static string GuessSideIndent(ScenarioDocument scenarioDocument)
    {
        var spans = FindLiteralSideSpans(scenarioDocument.EditableBlock);
        if (spans.Count > 0)
        {
            var firstSideOpenLine = scenarioDocument.EditableBlock.Lines[spans[0].StartLineIndex];
            return TakeLeadingWhitespace(firstSideOpenLine);
        }

        var scenarioOpenLine = scenarioDocument.EditableBlock.Lines[scenarioDocument.EditableBlock.OpeningTagLineIndex];
        return TakeLeadingWhitespace(scenarioOpenLine) + "    ";
    }

    private static int GetSideInsertLineIndex(ScenarioDocument document, int insertIndex)
    {
        var spans = FindLiteralSideSpans(document.EditableBlock);
        if (insertIndex < spans.Count)
        {
            var span = spans[insertIndex];
            return WmlPreprocessorScanner.FindEnclosingDirectChildRegion(
                       document.EditableBlock,
                       span.StartLineIndex)?.StartLineIndex ?? span.StartLineIndex;
        }
        if (spans.Count > 0)
        {
            var span = spans[^1];
            return WmlPreprocessorScanner.FindEnclosingDirectChildRegion(
                       document.EditableBlock,
                       span.StartLineIndex)?.EndLineIndexExclusive ?? span.EndLineIndexExclusive;
        }
        var events = ScenarioEventEditor.FindDirectEventSpans(document);
        if (events.Count > 0)
        {
            var first = events[0];
            return WmlPreprocessorScanner.FindEnclosingDirectChildRegion(
                       document.EditableBlock,
                       first.StartLineIndex)?.StartLineIndex ?? first.StartLineIndex;
        }
        document.EditableBlock.RefreshClosingTagLineIndex();
        return document.EditableBlock.ClosingTagLineIndex;
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
