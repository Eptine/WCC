using System.Linq;
using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>
/// Renders patched unit_type documents back into file text.
/// This keeps patch logic inside Wcc.Core instead of the UI layer.
/// </summary>
public static class UnitTypeFileRenderer
{
    public static string RenderPatchedFile(IReadOnlyList<UnitTypeDocument> documents)
        => RenderPatchedFileCore(documents, null, null);

    public static string RenderPatchedFile(IReadOnlyList<UnitTypeDocument> documents, string destinationTextDomain,
        IReadOnlyDictionary<UnitTypeDocument, IReadOnlySet<string>> structuredTouchedFields)
    {
        ArgumentNullException.ThrowIfNull(destinationTextDomain);
        ArgumentNullException.ThrowIfNull(structuredTouchedFields);
        return RenderPatchedFileCore(documents, destinationTextDomain, structuredTouchedFields);
    }

    private static string RenderPatchedFileCore(IReadOnlyList<UnitTypeDocument> documents, string? destinationTextDomain,
        IReadOnlyDictionary<UnitTypeDocument, IReadOnlySet<string>>? structuredTouchedFields)
    {
        if (documents is null) throw new ArgumentNullException(nameof(documents));
        if (documents.Count == 0) return string.Empty;

        var original = documents[0].OriginalText;

        if (destinationTextDomain is not null)
        {
            // Earlier RAW edits may deliberately change the domain of later units.
            // Capture their already-rendered prefix rather than the old file prefix.
            var rendered = new StringBuilder();
            var cursor = 0;
            foreach (var document in documents.OrderBy(document => document.BlockSpan.StartIndex))
            {
                if (document.OriginalText != original || document.BlockSpan.StartIndex < cursor
                    || document.BlockSpan.EndIndexExclusive > original.Length)
                    throw new InvalidOperationException("The scoped unit documents need disjoint spans in one exact original file.");
                rendered.Append(original.AsSpan(cursor, document.BlockSpan.StartIndex - cursor));
                var fields = structuredTouchedFields?.GetValueOrDefault(document);
                rendered.Append(RenderUpdatedUnitBlockCore(document, destinationTextDomain, fields,
                    fields is { Count: > 0 } ? rendered.ToString() : null));
                cursor = document.BlockSpan.EndIndexExclusive;
            }
            rendered.Append(original.AsSpan(cursor));
            return rendered.ToString();
        }

        var replacements = documents
            .Select(d => new
            {
                Start = d.BlockSpan.StartIndex,
                End = d.BlockSpan.EndIndexExclusive,
                Replacement = RenderUpdatedUnitBlockCore(d, destinationTextDomain,
                    structuredTouchedFields?.GetValueOrDefault(d))
            })
            .OrderByDescending(r => r.Start)
            .ToList();

        var result = original;
        foreach (var r in replacements)
        {
            result = result.Substring(0, r.Start)
                   + r.Replacement
                   + result.Substring(r.End);
        }

        return result;
    }

    public static string RenderUpdatedUnitBlock(UnitTypeDocument document)
        => RenderUpdatedUnitBlockCore(document, null, null);

    private static string RenderUpdatedUnitBlockCore(UnitTypeDocument document, string? destinationTextDomain,
        IReadOnlySet<string>? structuredTouchedFields, string? renderedPrefix = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var scoped = destinationTextDomain is not null && structuredTouchedFields is not null
            ? UnitTypeStructuredTextDomainComposer.SelectFields(document, structuredTouchedFields) : null;
        UnitTypePatcher.Apply(document.Unit, document.EditableBlock, scoped);

        ReplaceGenderVariantBlocks(document);
        ReplaceEditableChildBlocks(document, scoped);

        document.EditableBlock.RefreshClosingTagLineIndex();
        var raw = document.EditableBlock.ToRawText();
        return destinationTextDomain is not null && scoped is not null
            ? UnitTypeStructuredTextDomainComposer.Compose(document, raw, destinationTextDomain, scoped, renderedPrefix) : raw;
    }

    private static void ReplaceGenderVariantBlocks(UnitTypeDocument document)
    {
        var replacements = new List<(int Start, int EndExclusive, List<string> Lines)>();

        foreach (var kind in Enum.GetValues<UnitGenderVariantKind>())
        {
            var slots = WmlDirectChildBlockScanner.FindSpans(
                document.EditableBlock,
                kind.ToWmlTagName());
            var variants = document.GenderVariantDocuments
                .Where(x => x.Kind == kind)
                .OrderBy(x => x.Span.StartLineIndex)
                .ToArray();

            // Add/remove support is intentionally separate. At this stage every imported
            // source slot must still have one lossless document, so comments and surrounding
            // unknown WML cannot be discarded by rebuilding the whole region.
            if (slots.Count != variants.Length)
                throw new InvalidOperationException(
                    $"Cannot render changed {kind.ToWmlTagName()} variant count losslessly.");

            for (var i = 0; i < slots.Count; i++)
            {
                UnitGenderVariantPatcher.Apply(variants[i].Variant, variants[i].EditableBlock);
                replacements.Add((
                    slots[i].StartLineIndex,
                    slots[i].EndLineIndexExclusive,
                    variants[i].EditableBlock.Lines));
            }
        }

        foreach (var replacement in replacements.OrderByDescending(x => x.Start))
        {
            WmlEditableBlockLineEditor.ReplaceLines(
                document.EditableBlock,
                replacement.Start,
                replacement.EndExclusive,
                replacement.Lines.ToList());
        }
    }

    private static void ReplaceEditableChildBlocks(UnitTypeDocument document, IReadOnlySet<string>? scopedFields)
    {
        var slots = ScanEditableChildSlots(document.EditableBlock);

        var renderedAttackBlocks = BuildRenderedAttackBlocks(document, scopedFields);
        var renderedAnimationBlocks = BuildRenderedAnimationBlocks(document);

        if (slots.Count == 0)
        {
            var insertedLines = BuildNormalizedReplacement(
                slots,
                renderedAttackBlocks,
                renderedAnimationBlocks);

            if (insertedLines.Count > 0)
            {
                WmlEditableBlockLineEditor.ReplaceLines(
                    document.EditableBlock,
                    document.EditableBlock.ClosingTagLineIndex,
                    document.EditableBlock.ClosingTagLineIndex,
                    insertedLines);
            }

            return;
        }

        var replacement = HasMatchingEditableChildCounts(slots, renderedAttackBlocks, renderedAnimationBlocks)
            ? BuildGapPreservingReplacement(document.EditableBlock, slots, renderedAttackBlocks, renderedAnimationBlocks)
            : BuildNormalizedReplacement(slots, renderedAttackBlocks, renderedAnimationBlocks);

        WmlEditableBlockLineEditor.ReplaceLines(
            document.EditableBlock,
            slots[0].StartLineIndex,
            slots[^1].EndLineIndexExclusive,
            replacement);
    }

    private static List<List<string>> BuildRenderedAttackBlocks(UnitTypeDocument document,
        IReadOnlySet<string>? scopedFields)
    {
        foreach (var attackDoc in document.AttackDocuments)
            AttackPatcher.Apply(attackDoc.Attack, attackDoc.EditableBlock,
                scopedFields?.Contains("attack:" + attackDoc.InternalId + ":description") == true);

        return document.AttackDocuments
            .Select(d => d.Span.StartLineIndex == 0 ? FormatNewChild(document, d.EditableBlock) : d.EditableBlock.Lines)
            .ToList();
    }

    private static List<List<string>> BuildRenderedAnimationBlocks(UnitTypeDocument document)
    {
        return document.AnimationDocuments
            .Select(d => d.Span.StartLineIndex == 0 ? FormatNewChild(document, d.EditableBlock) : d.EditableBlock.Lines)
            .ToList();
    }

    private static List<string> FormatNewChild(UnitTypeDocument document, WmlEditableBlock child)
    {
        // Captured children have a positive owner-relative span. A standalone
        // inserted/replaced block needs indentation in the unit's actual tag/macro context.
        var prefix = document.OriginalText[..document.BlockSpan.StartIndex]
            + document.EditableBlock.Lines[0] + child.NewLine;
        var raw = child.ToRawText();
        var candidate = prefix + raw + child.NewLine + "[/unit_type]"
            + document.OriginalText[document.BlockSpan.EndIndexExclusive..];
        var builder = new StringBuilder(raw);
        foreach (var change in WmlFormatting.Build(candidate).Changes.Where(change => change.StartOffset >= prefix.Length
            && change.StartOffset + change.Length <= prefix.Length + raw.Length).OrderByDescending(change => change.StartOffset))
            builder.Remove(change.StartOffset - prefix.Length, change.Length).Insert(change.StartOffset - prefix.Length, change.ReplacementText);
        return builder.ToString().Split(child.NewLine, StringSplitOptions.None).ToList();
    }

    private static List<EditableChildSlot> ScanEditableChildSlots(WmlEditableBlock unitBlock)
    {
        var attackSlots = WmlDirectChildBlockScanner.FindSpans(unitBlock, "attack")
            .Select(s => new EditableChildSlot(EditableChildKind.Attack, s.StartLineIndex, s.EndLineIndexExclusive));

        var animationSlots = UnitAnimationTagCatalog.DirectChildTagNames
            .SelectMany(tag => WmlDirectChildBlockScanner.FindSpans(unitBlock, tag))
            .Select(s => new EditableChildSlot(EditableChildKind.Animation, s.StartLineIndex, s.EndLineIndexExclusive));

        return attackSlots
            .Concat(animationSlots)
            .OrderBy(s => s.StartLineIndex)
            .ToList();
    }

    private static bool HasMatchingEditableChildCounts(
        IReadOnlyList<EditableChildSlot> slots,
        IReadOnlyList<List<string>> renderedAttackBlocks,
        IReadOnlyList<List<string>> renderedAnimationBlocks)
    {
        int attackSlotCount = slots.Count(s => s.Kind == EditableChildKind.Attack);
        int animationSlotCount = slots.Count - attackSlotCount;

        return attackSlotCount == renderedAttackBlocks.Count
               && animationSlotCount == renderedAnimationBlocks.Count;
    }

    private static List<string> BuildGapPreservingReplacement(
        WmlEditableBlock unitBlock,
        IReadOnlyList<EditableChildSlot> slots,
        IReadOnlyList<List<string>> renderedAttackBlocks,
        IReadOnlyList<List<string>> renderedAnimationBlocks)
    {
        var attackQueue = new Queue<List<string>>(renderedAttackBlocks);
        var animationQueue = new Queue<List<string>>(renderedAnimationBlocks);
        var replacement = new List<string>();

        for (int i = 0; i < slots.Count; i++)
        {
            replacement.AddRange(DequeueRenderedBlock(slots[i].Kind, attackQueue, animationQueue));

            if (i == slots.Count - 1)
                continue;

            int gapStart = slots[i].EndLineIndexExclusive;
            int gapLength = slots[i + 1].StartLineIndex - gapStart;
            if (gapLength > 0)
                replacement.AddRange(unitBlock.Lines.Skip(gapStart).Take(gapLength));
        }

        return replacement;
    }

    private static List<string> BuildNormalizedReplacement(
        IReadOnlyList<EditableChildSlot> slots,
        IReadOnlyList<List<string>> renderedAttackBlocks,
        IReadOnlyList<List<string>> renderedAnimationBlocks)
    {
        var attackQueue = new Queue<List<string>>(renderedAttackBlocks);
        var animationQueue = new Queue<List<string>>(renderedAnimationBlocks);
        var orderedBlocks = new List<List<string>>();

        int lastAttackSlotIndex = FindLastSlotIndex(slots, EditableChildKind.Attack);
        int lastAnimationSlotIndex = FindLastSlotIndex(slots, EditableChildKind.Animation);

        for (int i = 0; i < slots.Count; i++)
        {
            EnqueueNextRenderedBlockIfAvailable(slots[i].Kind, attackQueue, animationQueue, orderedBlocks);

            if (i == lastAttackSlotIndex)
                EnqueueRemainingRenderedBlocks(attackQueue, orderedBlocks);

            if (i == lastAnimationSlotIndex)
                EnqueueRemainingRenderedBlocks(animationQueue, orderedBlocks);
        }

        if (lastAttackSlotIndex < 0)
            EnqueueRemainingRenderedBlocks(attackQueue, orderedBlocks);

        if (lastAnimationSlotIndex < 0)
            EnqueueRemainingRenderedBlocks(animationQueue, orderedBlocks);

        return JoinBlocksWithBlankLines(orderedBlocks);
    }

    private static int FindLastSlotIndex(IReadOnlyList<EditableChildSlot> slots, EditableChildKind kind)
    {
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (slots[i].Kind == kind)
                return i;
        }

        return -1;
    }

    private static List<string> DequeueRenderedBlock(
        EditableChildKind kind,
        Queue<List<string>> attackQueue,
        Queue<List<string>> animationQueue)
    {
        return kind switch
        {
            EditableChildKind.Attack when attackQueue.Count > 0 => attackQueue.Dequeue(),
            EditableChildKind.Animation when animationQueue.Count > 0 => animationQueue.Dequeue(),
            EditableChildKind.Attack => throw new InvalidOperationException("Missing rendered attack block."),
            EditableChildKind.Animation => throw new InvalidOperationException("Missing rendered animation block."),
            _ => throw new InvalidOperationException($"Unsupported editable child kind '{kind}'.")
        };
    }

    private static void EnqueueNextRenderedBlockIfAvailable(
        EditableChildKind kind,
        Queue<List<string>> attackQueue,
        Queue<List<string>> animationQueue,
        List<List<string>> orderedBlocks)
    {
        if (kind == EditableChildKind.Attack)
        {
            if (attackQueue.Count > 0)
                orderedBlocks.Add(attackQueue.Dequeue());

            return;
        }

        if (animationQueue.Count > 0)
            orderedBlocks.Add(animationQueue.Dequeue());
    }

    private static void EnqueueRemainingRenderedBlocks(
        Queue<List<string>> queue,
        List<List<string>> orderedBlocks)
    {
        while (queue.Count > 0)
            orderedBlocks.Add(queue.Dequeue());
    }

    private static List<string> JoinBlocksWithBlankLines(IReadOnlyList<List<string>> blocks)
    {
        if (blocks.Count == 0)
            return [];

        var joined = new List<string>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
                joined.Add(string.Empty);

            joined.AddRange(blocks[i]);
        }

        return joined;
    }

    private enum EditableChildKind
    {
        Attack,
        Animation
    }

    private sealed record EditableChildSlot(
        EditableChildKind Kind,
        int StartLineIndex,
        int EndLineIndexExclusive);
}
