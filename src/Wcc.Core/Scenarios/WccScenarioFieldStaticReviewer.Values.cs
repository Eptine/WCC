using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static bool HasExactProposedScalarAncestry(WmlStaticScalarOwner owner, string key,
        WccScenarioFieldCandidate edit, WmlStaticEvaluationResult evaluation)
    {
        if (edit.ProposedValueOrigin is not { } authored || authored.Source.ContentHash != edit.CandidateSource.ContentHash
            || !PathComparer.Equals(authored.Source.Path, edit.CandidateSource.Path)
            || authored.Text != edit.RequestedSourceValue
            || !owner.Fields.TryGetValue(key, out var field) || field.WinningContribution is not { ValueMappingIsExact: true } contribution
            || contribution.ValueOutputLength <= 0) return false;

        // Inspect only the actual winning value's output segments. A macro used
        // in the assignment key or another field cannot lend it invocation proof.
        var start = contribution.ValueOutputOffset;
        var end = start + contribution.ValueOutputLength;
        var position = start;
        var low = 0; var high = evaluation.Segments.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var segment = evaluation.Segments[middle];
            if (segment.OutputOffset + segment.Text.Length <= start) low = middle + 1;
            else high = middle;
        }
        for (var index = low; index < evaluation.Segments.Count; index++)
        {
            var segment = evaluation.Segments[index];
            if (segment.OutputOffset >= end) break;
            var segmentStart = Math.Max(start, segment.OutputOffset);
            var segmentEnd = Math.Min(end, segment.OutputOffset + segment.Text.Length);
            if (segmentStart != position || segmentEnd <= segmentStart || segment.Origin.Length != segment.Text.Length) return false;
            var origin = new WmlStaticSourceRange(segment.Origin.Source,
                segment.Origin.StartOffset + segmentStart - segment.OutputOffset, segmentEnd - segmentStart);
            if (!InsideProposedScalar(origin, authored) && !segment.ExpansionStack.Any(frame =>
                    frame.InvocationOrigins.Any(invocation => InsideProposedScalar(invocation, authored)))) return false;
            position = segmentEnd;
        }
        return position == end;
    }

    private static bool InsideProposedScalar(WmlStaticSourceRange range, WmlStaticSourceRange scalar) =>
        PathComparer.Equals(range.Source.Path, scalar.Source.Path) && range.Source.ContentHash == scalar.Source.ContentHash
        && range.StartOffset >= scalar.StartOffset && range.StartOffset + range.Length <= scalar.StartOffset + scalar.Length;
}
