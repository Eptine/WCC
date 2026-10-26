namespace Wcc.Core.Wml;

public sealed partial class WmlStaticScalarProjection
{
    /// <summary>
    /// Reuses complete unchanged root graphs for a verified whole-root erasure;
    /// otherwise parses changed roots and outside text over the direct masked view. A cold replay
    /// borrows only unchanged assignment mappings and constructs owners afresh.
    /// </summary>
    internal static WmlStaticScalarProjection CreateMasked(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection original, CancellationToken cancellationToken,
        out bool analysisReused, out int reusedContributions, int maximumCachedContributions = 262_144)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();
        var roots = maximumCachedContributions > 0
            ? RootMaskReuseScope.Create(evaluation, original, cancellationToken) : null;
        if (roots is not null)
        {
            if (roots.TryCreateErasureProjection(cancellationToken, out var rootProjection, out reusedContributions))
            {
                analysisReused = true;
                return rootProjection!;
            }
            if (roots.CanReplay)
            {
                var replay = new Parser(evaluation, cancellationToken) { RootReuse = roots };
                var replayed = replay.Run();
                if (replay.RootGraphsReused)
                {
                    analysisReused = true;
                    reusedContributions = replay.ReusedContributions;
                    return replayed;
                }
                // Uncertain structure cannot be repaired by a partial projection.
                // Discard the attempt and retain the existing complete replay path.
            }
        }
        var scope = MaskedContributionReuseScope.Create(evaluation, original, cancellationToken,
            maximumCachedContributions);
        analysisReused = scope is not null;
        var parser = new Parser(evaluation, cancellationToken, scope);
        var projection = parser.Run();
        reusedContributions = parser.ReusedContributions;
        return projection;
    }

    private sealed class MaskedContributionReuseScope(WmlStaticEvaluationResult original,
        Dictionary<(int Offset, string Key), (string Tag, WmlStaticScalarContribution Contribution)> entries)
    {
        public static MaskedContributionReuseScope? Create(WmlStaticEvaluationResult evaluation,
            WmlStaticScalarProjection projection, CancellationToken cancellationToken, int maximumEntries)
        {
            if (maximumEntries <= 0 || projection.SourceEvaluation is not { } source
                || !ReferenceEquals(source, evaluation.MaskedOriginalEvaluation)
                || !source.IsComplete || projection.Diagnostics.Count != 0) return null;
            // Public evaluation results can contain inconsistent segment text.
            // The masked factory regenerates it from the view, which could repair
            // an earlier negative mapping verdict even in an unchanged statement.
            foreach (var segment in source.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (segment.OutputOffset < 0 || segment.OutputOffset > source.Text.Length - segment.Text.Length
                    || segment.Origin.Length != segment.Text.Length
                    || !source.Text.AsSpan(segment.OutputOffset, segment.Text.Length).SequenceEqual(segment.Text.AsSpan()))
                    return null;
            }
            // The optional smaller budget is a test seam. Production cannot
            // retain more evidence than this fixed, run-local bound.
            maximumEntries = Math.Min(maximumEntries, 262_144);
            var entries = new Dictionary<(int Offset, string Key),
                (string Tag, WmlStaticScalarContribution Contribution)>();
            foreach (var owner in projection.Owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var field in owner.Fields.Values)
                    foreach (var contribution in field.Contributions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entries.Count == maximumEntries || !entries.TryAdd(
                                (contribution.AssignmentOutputOffset, field.Key), (owner.TagName, contribution)))
                            return null; // Discard the entire partial index on overflow or ambiguity.
                    }
            }
            return new(source, entries);
        }

        public WmlStaticScalarContribution? Find(string tag, string key, int offset, string code,
            int valueOffset, int valueLength, IReadOnlyList<string> compositeKeys)
        {
            if (!entries.TryGetValue((offset, key), out var entry) || entry.Tag != tag) return null;
            var contribution = entry.Contribution;
            if (contribution.AssignmentOutputLength != code.Length
                || contribution.ValueOutputOffset != valueOffset || contribution.ValueOutputLength != valueLength
                || !contribution.CompositeAssignmentKeys.SequenceEqual(compositeKeys, StringComparer.Ordinal)
                || offset < 0 || offset > original.Text.Length - code.Length
                || !original.Text.AsSpan(offset, code.Length).SequenceEqual(code.AsSpan())) return null;
            return contribution;
        }
    }
}
