using System.Collections.Concurrent;

namespace Wcc.Core.Library;

public enum WccLibraryWorkPhase
{
    LoadingManifest,
    CheckingPackageFiles,
    LoadingAnalysisCache,
    BuildingSnapshot
}

public sealed record WccLibraryProgress(
    WccLibraryWorkPhase Phase,
    int ProcessedItems,
    int TotalItems,
    string? CurrentItem);

public sealed class WccLibraryRefreshResult
{
    public required WccLibrarySnapshot Snapshot { get; init; }
    public required WccLibraryChangeSet ChangeSet { get; init; }
    public int CacheHitCount { get; init; }
    public int AnalyzedFileCount { get; init; }
}

public sealed record WccPackageRefreshRequest(
    WccPackageRevisionId PackageRevisionId,
    string PackageRoot,
    string? ProgressLabel = null);

/// <summary>
/// Application-facing orchestration boundary. It loads persisted analysis shards in parallel,
/// builds a complete replacement snapshot, and can refresh one package revision without
/// exposing storage details to Avalonia view models.
/// </summary>
public sealed class WccLibraryCoordinator
{
    private readonly IWccLibraryStore _store;
    private readonly WccPackageCfgIndexer _packageIndexer;
    private readonly int _maxAnalysisLoadConcurrency;

    public WccLibraryCoordinator(
        IWccLibraryStore store,
        WccPackageCfgIndexer? packageIndexer = null,
        int? maxAnalysisLoadConcurrency = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _packageIndexer = packageIndexer ?? new WccPackageCfgIndexer(store);
        _maxAnalysisLoadConcurrency = Math.Max(
            1,
            maxAnalysisLoadConcurrency ?? Math.Min(Environment.ProcessorCount, 8));
    }

    public async Task<WccLibrarySnapshot> LoadSnapshotAsync(
        IProgress<WccLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new WccLibraryProgress(
            WccLibraryWorkPhase.LoadingManifest,
            0,
            1,
            CurrentItem: null));

        var manifest = await _store.LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        return await BuildSnapshotAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WccLibraryRefreshResult> RefreshPackageAsync(
        WccPackageRevisionId packageRevisionId,
        string packageRoot,
        IProgress<WccLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await RefreshPackagesAsync(
                new[] { new WccPackageRefreshRequest(packageRevisionId, packageRoot) },
                progress,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Refreshes several package revisions but publishes only one replacement snapshot.
    /// This is important for a Wesnoth distribution containing many individually scoped
    /// campaign packages: consumers never observe a half-refreshed library and the complete
    /// snapshot is not rebuilt once per package.
    /// </summary>
    public async Task<WccLibraryRefreshResult> RefreshPackagesAsync(
        IReadOnlyList<WccPackageRefreshRequest> requests,
        IProgress<WccLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IEnumerable<WccDiagnosticRecord>? initialDiagnostics = null)
    {
        if (requests is null) throw new ArgumentNullException(nameof(requests));
        if (requests.Count == 0)
            throw new ArgumentException("At least one package refresh is required.", nameof(requests));
        if (requests.Select(x => x.PackageRevisionId).Distinct().Count() != requests.Count)
            throw new ArgumentException("A package revision can only occur once per batch.", nameof(requests));

        progress?.Report(new WccLibraryProgress(
            WccLibraryWorkPhase.LoadingManifest,
            0,
            1,
            CurrentItem: null));

        var manifest = await _store.LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var refreshedAnalyses = new Dictionary<WccSourceFileId, WccFileAnalysis>();
        var refreshDiagnostics = initialDiagnostics?.ToList() ?? new List<WccDiagnosticRecord>();
        var combinedChangeSet = new WccLibraryChangeSet();
        var cacheHits = 0;
        var analyzedFiles = 0;

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indexProgress = progress is null
                ? null
                : new SynchronousProgress<WccPackageIndexProgress>(value =>
                    progress.Report(new WccLibraryProgress(
                        WccLibraryWorkPhase.CheckingPackageFiles,
                        value.ProcessedFiles,
                        value.TotalFiles,
                        FormatCurrentItem(request.ProgressLabel, value.CurrentRelativePath))));

            var packageResult = await _packageIndexer
                .IndexAsync(
                    manifest,
                    request.PackageRevisionId,
                    request.PackageRoot,
                    indexProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            manifest = packageResult.Manifest;
            foreach (var analysis in packageResult.FileAnalyses)
                refreshedAnalyses[analysis.SourceFileId] = analysis;

            refreshDiagnostics.AddRange(packageResult.Diagnostics);
            combinedChangeSet.AddedSourceFileIds.AddRange(packageResult.ChangeSet.AddedSourceFileIds);
            combinedChangeSet.UpdatedSourceFileIds.AddRange(packageResult.ChangeSet.UpdatedSourceFileIds);
            combinedChangeSet.RemovedSourceFileIds.AddRange(packageResult.ChangeSet.RemovedSourceFileIds);
            cacheHits += packageResult.CacheHitCount;
            analyzedFiles += packageResult.AnalyzedFileCount;
        }

        var snapshot = await BuildSnapshotAsync(
                manifest,
                progress,
                cancellationToken,
                refreshDiagnostics,
                refreshedAnalyses.Values)
            .ConfigureAwait(false);

        return new WccLibraryRefreshResult
        {
            Snapshot = snapshot,
            ChangeSet = combinedChangeSet,
            CacheHitCount = cacheHits,
            AnalyzedFileCount = analyzedFiles
        };
    }

    private async Task<WccLibrarySnapshot> BuildSnapshotAsync(
        WccLibraryManifest manifest,
        IProgress<WccLibraryProgress>? progress,
        CancellationToken cancellationToken,
        IEnumerable<WccDiagnosticRecord>? initialDiagnostics = null,
        IEnumerable<WccFileAnalysis>? preloadedAnalyses = null)
    {
        var analyses = new ConcurrentBag<WccFileAnalysis>();
        var diagnostics = new ConcurrentBag<WccDiagnosticRecord>();
        var preloadedSourceFileIds = new HashSet<WccSourceFileId>();

        if (initialDiagnostics is not null)
        {
            foreach (var diagnostic in initialDiagnostics)
                diagnostics.Add(diagnostic);
        }

        if (preloadedAnalyses is not null)
        {
            foreach (var analysis in preloadedAnalyses)
            {
                if (preloadedSourceFileIds.Add(analysis.SourceFileId))
                    analyses.Add(analysis);
            }
        }

        var sourceFilesToLoad = manifest.SourceFiles
            .Where(x => !preloadedSourceFileIds.Contains(x.Id))
            .ToArray();
        var processed = preloadedSourceFileIds.Count;
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = _maxAnalysisLoadConcurrency
        };

        await Parallel.ForEachAsync(sourceFilesToLoad, options, async (sourceFile, token) =>
        {
            try
            {
                var analysis = await _store
                    .LoadFileAnalysisAsync(sourceFile.Id, token)
                    .ConfigureAwait(false);

                if (analysis is null)
                {
                    diagnostics.Add(new WccDiagnosticRecord
                    {
                        Code = "library.missing_file_analysis",
                        Message = $"No cached analysis exists for {sourceFile.RelativePath}.",
                        Severity = WccDiagnosticSeverity.Warning,
                        SourceFileId = sourceFile.Id
                    });
                }
                else
                {
                    analyses.Add(analysis);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "library.file_analysis_load_failed",
                    Message = $"Could not load cached analysis for {sourceFile.RelativePath}: {ex.Message}",
                    Severity = WccDiagnosticSeverity.Warning,
                    SourceFileId = sourceFile.Id
                });
            }
            finally
            {
                var completed = Interlocked.Increment(ref processed);
                progress?.Report(new WccLibraryProgress(
                    WccLibraryWorkPhase.LoadingAnalysisCache,
                    completed,
                    manifest.SourceFiles.Count,
                    sourceFile.RelativePath));
            }
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new WccLibraryProgress(
            WccLibraryWorkPhase.BuildingSnapshot,
            0,
            1,
            CurrentItem: null));

        var snapshot = await Task.Run(
                () => WccLibrarySnapshotBuilder.Build(manifest, analyses, diagnostics),
                cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new WccLibraryProgress(
            WccLibraryWorkPhase.BuildingSnapshot,
            1,
            1,
            CurrentItem: null));
        return snapshot;
    }

    /// <summary>
    /// Unlike Progress&lt;T&gt;, this does not capture a synchronization context. The caller's
    /// progress implementation decides how to marshal updates to its UI.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static string? FormatCurrentItem(string? label, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(label))
            return relativePath;
        if (string.IsNullOrWhiteSpace(relativePath))
            return label;

        return $"{label}: {relativePath}";
    }
}
