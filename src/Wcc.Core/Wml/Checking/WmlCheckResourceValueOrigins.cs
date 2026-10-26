namespace Wcc.Core.Wml.Checking;

/// <summary>
/// Resource-report navigation over one immutable evaluation. Only exactly mapped
/// value segments lend invocation ancestry; a key or owner cannot lend its frames.
/// This never establishes a scalar value, an effective owner or permission to edit.
/// </summary>
public sealed class WmlCheckResourceValueOrigins
{
    private readonly WmlStaticEvaluationResult _evaluation;
    private readonly HashSet<WmlStaticSource> _sources;
    private readonly bool _segmentsAreOrdered;

    public WmlCheckResourceValueOrigins(WmlStaticEvaluationResult evaluation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        _evaluation = evaluation;
        _sources = evaluation.Sources.ToHashSet();
        _segmentsAreOrdered = true;
        var previousEnd = 0;
        foreach (var segment in evaluation.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.OutputOffset < previousEnd || segment.OutputOffset < 0 ||
                segment.OutputOffset > evaluation.Text.Length - segment.Text.Length)
            {
                _segmentsAreOrdered = false;
                break;
            }
            previousEnd = segment.OutputOffset + segment.Text.Length;
        }
    }

    /// <summary>
    /// Returns captured authored invocation ranges, nearest value producers first.
    /// The report caller still applies selected-buffer, snapshot and cutoff gates.
    /// A recovered local projection supplies its offset into this full evaluation.
    /// </summary>
    public IReadOnlyList<WmlStaticSourceRange> FindInvocationOrigins(
        WmlStaticScalarContribution? contribution, string? valueText,
        int ownerOutputOffset = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_evaluation.IsComplete || !_segmentsAreOrdered || contribution is not { ValueMappingIsExact: true } ||
            valueText is null || valueText.Length == 0 || ownerOutputOffset < 0 ||
            contribution.ValueOutputOffset < 0 || contribution.ValueOutputLength != valueText.Length ||
            contribution.AssignmentOutputOffset < 0 || contribution.AssignmentOutputLength <= 0 ||
            contribution.ValueOutputOffset < contribution.AssignmentOutputOffset ||
            (long)contribution.ValueOutputOffset + contribution.ValueOutputLength >
                (long)contribution.AssignmentOutputOffset + contribution.AssignmentOutputLength)
            return [];

        var valueStart = (long)ownerOutputOffset + contribution.ValueOutputOffset;
        var valueEnd = valueStart + contribution.ValueOutputLength;
        if (valueStart > _evaluation.Text.Length || valueEnd > _evaluation.Text.Length ||
            (long)ownerOutputOffset + contribution.AssignmentOutputOffset + contribution.AssignmentOutputLength >
                _evaluation.Text.Length ||
            !_evaluation.Text.AsSpan((int)valueStart, contribution.ValueOutputLength).SequenceEqual(valueText))
            return [];

        var mapped = new List<(WmlStaticOutputSegment Segment, WmlStaticSourceRange Origin)>();
        var cursor = (int)valueStart;
        for (var index = FirstSegment(cursor); index < _evaluation.Segments.Count && cursor < valueEnd; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = _evaluation.Segments[index];
            var start = Math.Max(cursor, segment.OutputOffset);
            var end = (int)Math.Min(valueEnd, (long)segment.OutputOffset + segment.Text.Length);
            var sourceStart = (long)segment.Origin.StartOffset + start - segment.OutputOffset;
            var length = end - start;
            if (start != cursor || length <= 0 || segment.Origin.Length != segment.Text.Length ||
                !IsCapturedRange(segment.Origin) || sourceStart < 0 ||
                sourceStart > segment.Origin.Source.Text.Length - length ||
                !_evaluation.Text.AsSpan(start, length).SequenceEqual(segment.Text.AsSpan(start - segment.OutputOffset, length)) ||
                !_evaluation.Text.AsSpan(start, length).SequenceEqual(segment.Origin.Source.Text.AsSpan((int)sourceStart, length)))
                return [];
            mapped.Add((segment, new(segment.Origin.Source, (int)sourceStart, length)));
            cursor = end;
        }
        if (cursor != valueEnd) return [];

        var candidates = new Dictionary<WmlStaticSourceRange, Candidate>();
        var encounter = 0;
        foreach (var (segment, origin) in mapped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = segment.ExpansionStack.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = segment.ExpansionStack[index];
                if (!IsCapturedRange(frame.Definition) || frame.InvocationOrigins.Count == 0) continue;
                var validInvocations = true;
                foreach (var invocation in frame.InvocationOrigins)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCapturedRange(invocation)) { validInvocations = false; break; }
                }
                if (!validInvocations) continue;
                // Caller arguments expand before binding the callee. Their producing
                // frame can precede the callee in the stored stack, so reverse order
                // alone could navigate to the surrounding call instead of the helper
                // which actually supplied this value's mapped source characters.
                var producesOrigin = ReferenceEquals(frame.Definition.Source, origin.Source) &&
                    frame.Definition.StartOffset <= origin.StartOffset &&
                    (long)frame.Definition.StartOffset + frame.Definition.Length >= (long)origin.StartOffset + origin.Length;
                foreach (var invocation in frame.InvocationOrigins)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = new Candidate(invocation, producesOrigin, segment.ExpansionStack.Count, index, encounter++);
                    if (!candidates.TryGetValue(invocation, out var existing) || Precedes(candidate, existing))
                        candidates[invocation] = candidate;
                }
            }
        }
        return Array.AsReadOnly(candidates.Values.OrderByDescending(candidate => candidate.ProducesOrigin)
            .ThenByDescending(candidate => candidate.StackDepth).ThenByDescending(candidate => candidate.FrameIndex)
            .ThenBy(candidate => candidate.Encounter).Select(candidate => candidate.Origin).ToArray());
    }

    public static IReadOnlyList<WmlStaticSourceRange> FindInvocationOrigins(
        WmlStaticEvaluationResult evaluation, WmlStaticScalarContribution? contribution, string? valueText,
        int ownerOutputOffset = 0, CancellationToken cancellationToken = default)
        => new WmlCheckResourceValueOrigins(evaluation, cancellationToken)
            .FindInvocationOrigins(contribution, valueText, ownerOutputOffset, cancellationToken);

    private bool IsCapturedRange(WmlStaticSourceRange range)
        => range.Source.IsAuthored && _sources.Contains(range.Source) && range.StartOffset >= 0 && range.Length > 0 &&
           range.StartOffset <= range.Source.Text.Length - range.Length;

    private int FirstSegment(int offset)
    {
        var low = 0;
        var high = _evaluation.Segments.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var segment = _evaluation.Segments[middle];
            if (segment.OutputOffset + segment.Text.Length <= offset) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static bool Precedes(Candidate candidate, Candidate existing)
        => candidate.ProducesOrigin != existing.ProducesOrigin ? candidate.ProducesOrigin :
            candidate.StackDepth != existing.StackDepth ? candidate.StackDepth > existing.StackDepth :
            candidate.FrameIndex != existing.FrameIndex ? candidate.FrameIndex > existing.FrameIndex :
            candidate.Encounter < existing.Encounter;

    private sealed record Candidate(WmlStaticSourceRange Origin, bool ProducesOrigin,
        int StackDepth, int FrameIndex, int Encounter);
}
