using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    /// <summary>
    /// A bounded candidate filter for one immutable evaluation. Matching an
    /// origin or invocation source is necessary, never sufficient, for region
    /// membership. The original containment and literal-ancestry predicates
    /// still decide every span and active-definition check.
    /// </summary>
    private sealed class ProjectRegionSegmentIndex
    {
        private const int MaxIndexedSegmentReferences = 2_097_152;
        private const int MaxIndexedSources = 8_192;
        private readonly WmlStaticEvaluationResult _evaluation;
        private readonly WccSourceSaveDiagnosticCollector? _diagnostics;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<WmlStaticSource, List<int>>? _segments;

        public ProjectRegionSegmentIndex(WmlStaticEvaluationResult evaluation,
            WccSourceSaveDiagnosticCollector? diagnostics, CancellationToken cancellationToken,
            int maxSegmentReferences = MaxIndexedSegmentReferences, int maxSources = MaxIndexedSources)
        {
            _evaluation = evaluation;
            _diagnostics = diagnostics;
            _cancellationToken = cancellationToken;
            diagnostics?.Add(WccSourceSaveCount.RegionIndexBuilds);
            var segments = new Dictionary<WmlStaticSource, List<int>>(RegionSourceComparer.Instance);
            var sources = new HashSet<WmlStaticSource>(RegionSourceComparer.Instance);
            var references = 0;
            for (var ordinal = 0; ordinal < evaluation.Segments.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = evaluation.Segments[ordinal];
                sources.Clear();
                sources.Add(segment.Origin.Source);
                foreach (var frame in segment.ExpansionStack)
                    foreach (var origin in frame.InvocationOrigins)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        sources.Add(origin.Source);
                        if (sources.Count > maxSources)
                        {
                            diagnostics?.Add(WccSourceSaveCount.RegionIndexFallbacks);
                            return;
                        }
                    }
                foreach (var source in sources)
                {
                    if (++references > maxSegmentReferences
                        || !segments.ContainsKey(source) && segments.Count >= maxSources)
                    {
                        // Discard the whole partial index. Every later query
                        // uses the full ordered oracle rather than a truncation.
                        diagnostics?.Add(WccSourceSaveCount.RegionIndexFallbacks);
                        return;
                    }
                    if (!segments.TryGetValue(source, out var ordinals))
                        segments.Add(source, ordinals = []);
                    ordinals.Add(ordinal);
                }
            }
            _segments = segments;
            diagnostics?.Add(WccSourceSaveCount.RegionIndexedSegmentReferences, references);
        }

        public IEnumerable<WmlStaticOutputSegment> Get(WmlStaticSourceRange range)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_segments is null)
            {
                foreach (var segment in _evaluation.Segments)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    _diagnostics?.Add(WccSourceSaveCount.RegionCandidateSegmentVisits);
                    yield return segment;
                }
            }
            else if (_segments.TryGetValue(range.Source, out var ordinals))
                foreach (var ordinal in ordinals)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    _diagnostics?.Add(WccSourceSaveCount.RegionCandidateSegmentVisits);
                    yield return _evaluation.Segments[ordinal];
                }
        }

        private sealed class RegionSourceComparer : IEqualityComparer<WmlStaticSource>
        {
            public static readonly RegionSourceComparer Instance = new();
            // Invocation containment deliberately does not require authored
            // sources. Literal membership applies its stricter rule afterwards.
            public bool Equals(WmlStaticSource? left, WmlStaticSource? right) =>
                ReferenceEquals(left, right) || left is not null && right is not null
                && PathComparer.Equals(left.Path, right.Path) && left.ContentHash == right.ContentHash;

            public int GetHashCode(WmlStaticSource source) =>
                HashCode.Combine(PathComparer.GetHashCode(source.Path), StringComparer.Ordinal.GetHashCode(source.ContentHash));
        }
    }
}
