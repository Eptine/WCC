using System.Linq;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record ScenarioRenderedBlockSpan(
    string DocumentInternalId,
    int StartOffset,
    int EndOffsetExclusive)
{
    public int Length => EndOffsetExclusive - StartOffset;
}

public sealed record ScenarioFileRenderResult(
    string Text,
    IReadOnlyList<ScenarioRenderedBlockSpan> ScenarioBlocks)
{
    public ScenarioRenderedBlockSpan? FindScenarioBlock(ScenarioDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var matches = ScenarioBlocks.Where(block =>
            string.Equals(
                block.DocumentInternalId,
                document.InternalId,
                StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

public static class ScenarioFileRenderer
{
    public static string RenderPatchedFile(IReadOnlyList<ScenarioDocument> documents)
        => RenderPatchedFileWithMap(documents).Text;

    public static ScenarioFileRenderResult RenderPatchedFileWithMap(
        IReadOnlyList<ScenarioDocument> documents)
    {
        if (documents is null) throw new ArgumentNullException(nameof(documents));
        if (documents.Count == 0)
            return new ScenarioFileRenderResult(string.Empty, []);

        var original = documents[0].OriginalText;
        ValidateDocuments(documents, original);

        var replacements = documents
            .Select(d => new
            {
                Document = d,
                Start = d.BlockSpan.StartIndex,
                End = d.BlockSpan.EndIndexExclusive,
                Replacement = RenderUpdatedScenarioBlock(d)
            })
            .ToList();

        var mappedBlocks = new List<ScenarioRenderedBlockSpan>(replacements.Count);
        var shift = 0;
        foreach (var replacement in replacements.OrderBy(r => r.Start))
        {
            var mappedStart = replacement.Start + shift;
            mappedBlocks.Add(new ScenarioRenderedBlockSpan(
                replacement.Document.InternalId,
                mappedStart,
                mappedStart + replacement.Replacement.Length));
            shift += replacement.Replacement.Length - (replacement.End - replacement.Start);
        }

        var result = original;
        foreach (var r in replacements.OrderByDescending(r => r.Start))
        {
            result = result.Substring(0, r.Start)
                   + r.Replacement
                   + result.Substring(r.End);
        }

        return new ScenarioFileRenderResult(result, mappedBlocks);
    }

    private static void ValidateDocuments(
        IReadOnlyList<ScenarioDocument> documents,
        string original)
    {
        if (original is null)
            throw new InvalidOperationException("A scenario document has no original source text.");

        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        var orderedSpans = new List<(int Start, int End)>();
        foreach (var document in documents)
        {
            if (document is null)
                throw new InvalidOperationException("The scenario file contains a null document.");
            if (string.IsNullOrWhiteSpace(document.InternalId) ||
                !documentIds.Add(document.InternalId))
            {
                throw new InvalidOperationException(
                    "Scenario documents in one rendered file must have unique nonempty identities.");
            }
            if (!string.Equals(document.OriginalText, original, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Scenario documents from different original source files cannot be rendered together.");
            }

            var span = document.BlockSpan;
            if (span is null ||
                span.StartIndex < 0 ||
                span.EndIndexExclusive <= span.StartIndex ||
                span.EndIndexExclusive > original.Length ||
                !string.Equals(
                    span.RawText,
                    original[span.StartIndex..span.EndIndexExclusive],
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Scenario document '{document.InternalId}' has a stale or invalid original source span.");
            }
            orderedSpans.Add((span.StartIndex, span.EndIndexExclusive));
        }

        orderedSpans.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < orderedSpans.Count; index++)
        {
            if (orderedSpans[index].Start < orderedSpans[index - 1].End)
            {
                throw new InvalidOperationException(
                    "Scenario document source spans overlap and cannot produce an authoritative render map.");
            }
        }
    }

    public static string RenderUpdatedScenarioBlock(ScenarioDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        ScenarioPatcher.Apply(document.Scenario, document.EditableBlock);

        var currentStorySpans = WmlDirectChildBlockScanner.FindSpans(document.EditableBlock, "story");

        if (currentStorySpans.Count != document.StoryDocuments.Count)
        {
            throw new InvalidOperationException(
                $"Story count mismatch while rendering scenario '{document.Scenario.Id ?? "(no id)"}'. " +
                $"Parsed stories: {document.StoryDocuments.Count}, current story blocks: {currentStorySpans.Count}.");
        }

        for (int i = currentStorySpans.Count - 1; i >= 0; i--)
        {
            var span = currentStorySpans[i];
            var storyDoc = document.StoryDocuments[i];

            PatchStoryBlock(storyDoc);

            WmlEditableBlockLineEditor.ReplaceLines(
                document.EditableBlock,
                span.StartLineIndex,
                span.EndLineIndexExclusive,
                storyDoc.EditableBlock.Lines
            );
        }

        var currentSideSpans = ScenarioSideEditor.FindLiteralSideSpans(document.EditableBlock);

        if (currentSideSpans.Count != document.SideDocuments.Count)
        {
            throw new InvalidOperationException(
                $"Side count mismatch while rendering scenario '{document.Scenario.Id ?? "(no id)"}'. " +
                $"Parsed sides: {document.SideDocuments.Count}, current side blocks: {currentSideSpans.Count}.");
        }

        for (int i = currentSideSpans.Count - 1; i >= 0; i--)
        {
            var span = currentSideSpans[i];
            var sideDoc = document.SideDocuments[i];

            PatchSideBlock(sideDoc);

            WmlEditableBlockLineEditor.ReplaceLines(
                document.EditableBlock,
                span.StartLineIndex,
                span.EndLineIndexExclusive,
                sideDoc.EditableBlock.Lines
            );
        }

        document.EditableBlock.RefreshClosingTagLineIndex();
        return document.EditableBlock.ToRawText();
    }

    private static void PatchSideBlock(ScenarioSideDocument sideDoc)
    {
        if (sideDoc.CanEditTyped)
            ScenarioSidePatcher.Apply(sideDoc.Side, sideDoc.EditableBlock);

        var currentLeaderSpans = ScenarioLeaderEditor.FindLiteralLeaderSpans(
            sideDoc.EditableBlock);
        var nestedDocuments = sideDoc.NestedLeaderDocuments;
        if (currentLeaderSpans.Count != nestedDocuments.Count)
        {
            throw new InvalidOperationException(
                $"Leader count mismatch while rendering side '{sideDoc.Side.SideNumber?.ToString() ?? "(no number)"}'. " +
                $"Parsed leaders: {nestedDocuments.Count}, current leader blocks: {currentLeaderSpans.Count}.");
        }

        for (var index = currentLeaderSpans.Count - 1; index >= 0; index--)
        {
            var leaderDoc = nestedDocuments[index];
            if (leaderDoc.CanEditTyped)
                ScenarioLeaderPatcher.Apply(leaderDoc.Leader, leaderDoc.EditableBlock);
            var span = currentLeaderSpans[index];
            WmlEditableBlockLineEditor.ReplaceLines(
                sideDoc.EditableBlock,
                span.StartLineIndex,
                span.EndLineIndexExclusive,
                leaderDoc.EditableBlock.Lines);
        }

        sideDoc.EditableBlock.RefreshClosingTagLineIndex();
    }

    private static void PatchStoryBlock(ScenarioStoryDocument storyDoc)
    {
        var currentPartSpans = WmlDirectChildBlockScanner.FindSpans(storyDoc.EditableBlock, "part");

        if (currentPartSpans.Count != storyDoc.PartDocuments.Count)
        {
            throw new InvalidOperationException(
                $"Part count mismatch while rendering story block. " +
                $"Parsed parts: {storyDoc.PartDocuments.Count}, current part blocks: {currentPartSpans.Count}.");
        }

        for (int i = currentPartSpans.Count - 1; i >= 0; i--)
        {
            var span = currentPartSpans[i];
            var partDoc = storyDoc.PartDocuments[i];

            StoryPartPatcher.Apply(partDoc.Part, partDoc.EditableBlock);

            WmlEditableBlockLineEditor.ReplaceLines(
                storyDoc.EditableBlock,
                span.StartLineIndex,
                span.EndLineIndexExclusive,
                partDoc.EditableBlock.Lines
            );
        }

        storyDoc.EditableBlock.RefreshClosingTagLineIndex();
    }
}
