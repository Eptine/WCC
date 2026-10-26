namespace Wcc.Core.Library;

/// <summary>
/// Measurements for one store's bounded raw-analysis reuse. Bytes are a conservative
/// accounting estimate, not a measurement of the managed heap or process memory.
/// </summary>
public sealed record WccFileAnalysisCacheStatistics(
    long HitCount,
    long MissCount,
    long DeserializationCount,
    int RetainedAnalysisCount,
    long EstimatedRetainedBytes,
    long MaximumRetainedBytes);

/// <summary>
/// Owns detached raw analyses, never a manifest or a closure-resolved snapshot. The public
/// analysis model is mutable, so both boundaries copy its mutable collections and records.
/// Immutable strings and init-only source spans/diagnostics can safely be shared.
/// </summary>
internal sealed class WccFileAnalysisMemoryCache
{
    private readonly object _gate = new();
    private readonly long _maximumBytes;
    private readonly Dictionary<WccSourceFileId, Entry> _entries = new();
    private long _retainedBytes;
    private long _hits;
    private long _misses;
    private long _deserializations;
    private long _generation;

    internal WccFileAnalysisMemoryCache(long maximumBytes)
    {
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    internal long Generation
    {
        get
        {
            lock (_gate)
                return _generation;
        }
    }

    internal WccFileAnalysisCacheStatistics Statistics
    {
        get
        {
            lock (_gate)
                return new(_hits, _misses, _deserializations, _entries.Count,
                    _retainedBytes, _maximumBytes);
        }
    }

    internal WccFileAnalysis? TryLoad(WccSourceFileId sourceFileId, string digest)
    {
        WccFileAnalysis owned;
        lock (_gate)
        {
            if (!_entries.TryGetValue(sourceFileId, out var entry) ||
                !string.Equals(entry.Digest, digest, StringComparison.Ordinal))
            {
                _misses++;
                RemoveCore(sourceFileId);
                return null;
            }

            _hits++;
            owned = entry.Analysis;
        }

        // Nothing outside this class can mutate the owned graph. Copy outside the gate
        // so parallel snapshot loads do not serialize their allocation work.
        return Detach(owned);
    }

    internal void RecordDeserialization()
    {
        lock (_gate)
            _deserializations++;
    }

    internal void Remember(WccFileAnalysis analysis, string digest, long generation)
    {
        // Legacy/externally written malformed JSON must keep the store's existing
        // deserialization behavior; reuse is optional and must not add model validation.
        if (_maximumBytes == 0 || analysis.Entities is null || analysis.References is null ||
            analysis.Diagnostics is null ||
            analysis.Entities.Any(entity => entity is null || entity.Summary is null) ||
            analysis.References.Any(reference => reference is null || reference.Metadata is null) ||
            analysis.Diagnostics.Any(diagnostic => diagnostic is null))
            return;
        var estimate = EstimateBytes(analysis);
        if (estimate > _maximumBytes)
            return;

        lock (_gate)
        {
            if (_generation != generation || !HasRoom(analysis.SourceFileId, estimate))
                return;
        }
        var owned = Detach(analysis);
        lock (_gate)
        {
            if (_generation != generation || !HasRoom(analysis.SourceFileId, estimate))
                return;
            RemoveCore(analysis.SourceFileId);
            // Broad refreshes walk the same library repeatedly. Replacing admitted
            // shards with every unseen file would make an over-budget scan miss on
            // every pass. Keep validated admissions until invalidation or pruning
            // frees capacity; a bypassed analysis still participates in this refresh.
            _entries.Add(analysis.SourceFileId, new(digest, owned, estimate));
            _retainedBytes += estimate;
        }
    }

    internal void Remove(WccSourceFileId sourceFileId)
    {
        lock (_gate)
        {
            _generation++;
            RemoveCore(sourceFileId);
        }
    }

    internal void Retain(IEnumerable<WccSourceFileId> sourceFileIds)
    {
        var retained = sourceFileIds.ToHashSet();
        lock (_gate)
        {
            _generation++;
            foreach (var sourceFileId in _entries.Keys.Where(id => !retained.Contains(id)).ToArray())
                RemoveCore(sourceFileId);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _entries.Clear();
            _retainedBytes = 0;
        }
    }

    private bool HasRoom(WccSourceFileId sourceFileId, long estimate)
        => _retainedBytes - (_entries.TryGetValue(sourceFileId, out var existing) ? existing.EstimatedBytes : 0)
           <= _maximumBytes - estimate;

    private void RemoveCore(WccSourceFileId sourceFileId)
    {
        if (!_entries.Remove(sourceFileId, out var entry))
            return;
        _retainedBytes -= entry.EstimatedBytes;
    }

    private static WccFileAnalysis Detach(WccFileAnalysis source)
        => new()
        {
            FormatVersion = source.FormatVersion,
            SourceFileId = source.SourceFileId,
            ContentHash = source.ContentHash,
            AnalyzedAtUtc = source.AnalyzedAtUtc,
            Entities = source.Entities.Select(entity => new WccEntityRecord
            {
                Id = entity.Id,
                SourceFileId = entity.SourceFileId,
                EntityType = entity.EntityType,
                ParentEntityId = entity.ParentEntityId,
                ExportSymbol = entity.ExportSymbol,
                DisplayName = entity.DisplayName,
                ContentFingerprint = entity.ContentFingerprint,
                SourceSpan = entity.SourceSpan,
                IsExportRootCandidate = entity.IsExportRootCandidate,
                Summary = new Dictionary<string, string>(entity.Summary, entity.Summary.Comparer)
            }).ToList(),
            References = source.References.Select(reference => new WccReferenceRecord
            {
                Id = reference.Id,
                SourceFileId = reference.SourceFileId,
                SourceEntityId = reference.SourceEntityId,
                ReferenceType = reference.ReferenceType,
                RawValue = reference.RawValue,
                LookupValue = reference.LookupValue,
                SourceSpan = reference.SourceSpan,
                ResolutionState = reference.ResolutionState,
                Confidence = reference.Confidence,
                TargetEntityId = reference.TargetEntityId,
                Condition = reference.Condition,
                Metadata = new Dictionary<string, string>(reference.Metadata, reference.Metadata.Comparer)
            }).ToList(),
            Diagnostics = source.Diagnostics.ToList()
        };

    private static long EstimateBytes(WccFileAnalysis analysis)
    {
        // Charge every string occurrence even when shared/interned, plus deliberately
        // generous object/list/dictionary costs. This bounds retained entries without
        // retaining another payload just to calculate an exact serialized length.
        long bytes = 512 + StringBytes(analysis.ContentHash);
        foreach (var entity in analysis.Entities)
        {
            bytes += 384 + StringBytes(entity.EntityType.Value) +
                     StringBytes(entity.ExportSymbol) + StringBytes(entity.DisplayName) +
                     StringBytes(entity.ContentFingerprint);
            foreach (var pair in entity.Summary)
                bytes += 128 + StringBytes(pair.Key) + StringBytes(pair.Value);
        }
        foreach (var reference in analysis.References)
        {
            bytes += 384 + StringBytes(reference.ReferenceType.Value) +
                     StringBytes(reference.RawValue) + StringBytes(reference.LookupValue) +
                     StringBytes(reference.Condition);
            foreach (var pair in reference.Metadata)
                bytes += 128 + StringBytes(pair.Key) + StringBytes(pair.Value);
        }
        foreach (var diagnostic in analysis.Diagnostics)
            bytes += 256 + StringBytes(diagnostic.Code) + StringBytes(diagnostic.Message);
        return bytes;
    }

    private static long StringBytes(string? value) => value is null ? 0 : 32L + 2L * value.Length;

    private sealed record Entry(string Digest, WccFileAnalysis Analysis, long EstimatedBytes);
}