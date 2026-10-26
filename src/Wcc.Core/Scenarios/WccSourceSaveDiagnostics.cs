using System.Diagnostics;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum WccSourceSavePhase
{
    Operation, QueueWait, SessionValidation, DiskCapture, SourceCapture, ContextVerification,
    BaselineAnalysis, CandidateAnalysis, Preprocessing, ScalarProjection, OwnerLookupBuild,
    OwnerLookup, Composition, FieldValidation, RegionValidation, RegionMasking, MaskedProjection,
    OwnerComparison, HistoryComparison, OutputComparison, ClosureIdentity, FinalCapture,
    FinalValidation, DiskRecheck, BatchWrite, CommitFreshness, NativeCommit,
    SelectedCampaignScoping
}

public enum WccSourceSaveCount
{
    RequestedFiles, RequestedFields, RequestedRawPatches, RequestedStructuralOperations, SessionStates,
    ReviewContexts, SourceCaptures, CapturedSources, CapturedSourceCharacters, DiskCaptures,
    CapturedDiskBytes, ContextVerifications, PreprocessingEvaluations, BaselineAnalyses,
    CandidateAnalyses, ScalarProjections, MaskedProjections, ExpandedCharacters, OutputSegments,
    ProjectionOwners, ProjectionFields, OwnerLookupBuilds, OwnerLookupQueries, ComposedFiles,
    SourcePatches, AuthorizedRegions, FieldValidations, RegionValidations, RegionMaskAttempts,
    OwnerComparisons, HistoryComparisons, OutputComparisons, BatchWriteAttempts, CommitFreshnessChecks,
    NativeCommitAttempts, ReplacedFiles, RegionSpanRequests, RegionSpanBuilds, RegionSpanCacheHits,
    RegionSpanCachedRanges, RegionSpanRetainedSpans, RegionIndexBuilds, RegionIndexFallbacks,
    RegionIndexedSegmentReferences, RegionCandidateSegmentVisits, UnchangedRegionMasks,
    ActiveDefinitionFrameChecks, ActiveDefinitionFrameCacheHits, ActiveDefinitionRecipeChecks,
    ActiveDefinitionRecipeCacheHits, ActiveDefinitionLookupFallbacks,
    MaskedAnalysisReuses, MaskedContributionReuses, MaskedAnalysisFallbacks,
    SelectedCampaignScopeAttempts, SelectedCampaignScopeFastPaths, SelectedCampaignScopeLegacyFallbacks,
    SelectedCampaignScopeNoops, SelectedCampaignMaskProjections,
    CompactOwnerComparisonAttempts, CompactOwnerComparisonSuccesses,
    CompactOwnerComparisonFallbacks, CompactOwnerComparisonOwnerBudgetFallbacks
}

public sealed record WccSourceSavePhaseMeasurement(WccSourceSavePhase Phase, long Invocations, TimeSpan Duration);
public sealed record WccSourceSaveCounter(WccSourceSaveCount Counter, long Value);

/// <summary>
/// Immutable optional telemetry, never review or persistence authority. Durations
/// are inclusive: nested phases overlap and must not be added to find total time.
/// Counts include observed attempts and repeated captures/analyses, not unique files.
/// Allocation and GC deltas cover the whole process during the operation and may
/// include concurrent background work; they are not thread-local allocations.
/// </summary>
public sealed class WccSourceSaveDiagnostics
{
    internal WccSourceSaveDiagnostics(string operationName, string? resultStatus, bool cancelled, bool failed,
        TimeSpan elapsed, IEnumerable<WccSourceSavePhaseMeasurement> phases,
        IEnumerable<WccSourceSaveCounter> counters, long processAllocatedBytesDelta, IEnumerable<int> processGcCollections)
    {
        OperationName = operationName;
        ResultStatus = resultStatus;
        WasCancelled = cancelled;
        Failed = failed;
        Elapsed = elapsed;
        Phases = Array.AsReadOnly(phases.ToArray());
        Counters = Array.AsReadOnly(counters.ToArray());
        ProcessAllocatedBytesDelta = processAllocatedBytesDelta;
        ProcessGcCollections = Array.AsReadOnly(processGcCollections.ToArray());
    }
    public string OperationName { get; }
    public string? ResultStatus { get; }
    public bool WasCancelled { get; }
    public bool Failed { get; }
    public TimeSpan Elapsed { get; }
    public IReadOnlyList<WccSourceSavePhaseMeasurement> Phases { get; }
    public IReadOnlyList<WccSourceSaveCounter> Counters { get; }
    public long ProcessAllocatedBytesDelta { get; }
    public IReadOnlyList<int> ProcessGcCollections { get; }
    public long Count(WccSourceSaveCount counter) => Counters.FirstOrDefault(item => item.Counter == counter)?.Value ?? 0;
    public bool AuthorizesPersistence => false;
}

/// <summary>Run-local collector shared by scalar and composed saves. Null means no instrumentation.</summary>
internal sealed class WccSourceSaveDiagnosticCollector(string operationName)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly long _allocated = GC.GetTotalAllocatedBytes(precise: false);
    private readonly int[] _gc = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private readonly long[] _ticks = new long[Enum.GetValues<WccSourceSavePhase>().Length];
    private readonly long[] _invocations = new long[Enum.GetValues<WccSourceSavePhase>().Length];
    private readonly long[] _counters = new long[Enum.GetValues<WccSourceSaveCount>().Length];

    public IDisposable Measure(WccSourceSavePhase phase) => new Measurement(this, phase);
    public void Add(WccSourceSaveCount counter, long value = 1) => Interlocked.Add(ref _counters[(int)counter], value);
    public void ObserveCapture(WccStaticSourceCaptureResult capture)
    {
        Add(WccSourceSaveCount.CapturedSources, capture.Sources.Count);
        Add(WccSourceSaveCount.CapturedSourceCharacters, capture.Sources.Sum(source => (long)source.Text.Length));
    }
    public void ObserveEvaluation(WmlStaticEvaluationResult evaluation)
    {
        Add(WccSourceSaveCount.ExpandedCharacters, evaluation.Text.Length);
        Add(WccSourceSaveCount.OutputSegments, evaluation.Segments.Count);
    }
    public void ObserveProjection(WmlStaticScalarProjection projection)
    {
        Add(WccSourceSaveCount.ProjectionOwners, projection.Owners.Count);
        Add(WccSourceSaveCount.ProjectionFields, projection.Owners.Sum(owner => (long)owner.Fields.Count));
    }
    public WccSourceSaveDiagnostics Complete(string? resultStatus, bool cancelled = false, bool failed = false)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - _started;
        var allocated = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - _allocated);
        var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var phases = Enum.GetValues<WccSourceSavePhase>().Select(phase => phase == WccSourceSavePhase.Operation
            ? new WccSourceSavePhaseMeasurement(phase, 1, ToDuration(elapsedTicks))
            : new WccSourceSavePhaseMeasurement(phase, Volatile.Read(ref _invocations[(int)phase]),
                ToDuration(Volatile.Read(ref _ticks[(int)phase])))).Where(item => item.Invocations != 0);
        var counters = Enum.GetValues<WccSourceSaveCount>().Select(counter =>
            new WccSourceSaveCounter(counter, Volatile.Read(ref _counters[(int)counter]))).Where(item => item.Value != 0);
        return new(operationName, resultStatus, cancelled, failed, ToDuration(elapsedTicks), phases, counters,
            allocated, gc.Select((value, index) => Math.Max(0, value - _gc[index])));
    }
    private static TimeSpan ToDuration(long ticks) => TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
    private sealed class Measurement(WccSourceSaveDiagnosticCollector owner, WccSourceSavePhase phase) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Add(ref owner._ticks[(int)phase], Stopwatch.GetTimestamp() - _started);
            Interlocked.Increment(ref owner._invocations[(int)phase]);
        }
    }
}
