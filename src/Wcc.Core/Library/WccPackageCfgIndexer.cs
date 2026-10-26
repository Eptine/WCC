using Wcc.Core.References;

namespace Wcc.Core.Library;

public sealed record WccPackageIndexProgress(
    int ProcessedFiles,
    int TotalFiles,
    string? CurrentRelativePath);

public sealed class WccLibraryChangeSet
{
    public List<WccSourceFileId> AddedSourceFileIds { get; } = new();
    public List<WccSourceFileId> UpdatedSourceFileIds { get; } = new();
    public List<WccSourceFileId> RemovedSourceFileIds { get; } = new();

    public bool HasChanges =>
        AddedSourceFileIds.Count > 0 ||
        UpdatedSourceFileIds.Count > 0 ||
        RemovedSourceFileIds.Count > 0;
}

public sealed class WccPackageIndexResult
{
    public required WccLibraryManifest Manifest { get; init; }
    public required IReadOnlyList<WccFileAnalysis> FileAnalyses { get; init; }
    public required WccLibraryChangeSet ChangeSet { get; init; }
    public int CacheHitCount { get; init; }
    public int AnalyzedFileCount { get; init; }
    public List<WccDiagnosticRecord> Diagnostics { get; } = new();
}

/// <summary>
/// Incrementally indexes cfg files and music resources in one declared package revision.
/// It enumerates the package once and reads each new/changed file once. Existing focused WML
/// importers then share that in-memory text through <see cref="WccCfgFileAnalyzer"/>.
/// </summary>
public sealed class WccPackageCfgIndexer
{
    private readonly IWccLibraryStore _store;
    private readonly WccCfgFileAnalyzer _analyzer;

    public WccPackageCfgIndexer(
        IWccLibraryStore store,
        WccCfgFileAnalyzer? analyzer = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _analyzer = analyzer ?? new WccCfgFileAnalyzer();
    }

    public async Task<WccPackageIndexResult> IndexAsync(
        WccLibraryManifest manifest,
        WccPackageRevisionId packageRevisionId,
        string packageRoot,
        IProgress<WccPackageIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (string.IsNullOrWhiteSpace(packageRoot))
            throw new ArgumentException("Package root must be set.", nameof(packageRoot));
        if (!Directory.Exists(packageRoot))
            throw new DirectoryNotFoundException(packageRoot);
        if (!manifest.PackageRevisions.Any(x => x.Id == packageRevisionId))
            throw new ArgumentException("Package revision is not present in the manifest.", nameof(packageRevisionId));

        var packageRevision = manifest.PackageRevisions.Single(x => x.Id == packageRevisionId);
        var package = manifest.Packages.Single(x => x.Id == packageRevision.PackageId);
        var usesStableWorkingFileIds = package.StorageMode == WccPackageStorageMode.WorkingCopy;

        var fullRoot = Path.GetFullPath(packageRoot);
        var discoveredFiles = await Task.Run(
                () => Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
                    .Where(path => IsIndexedSourceFile(fullRoot, path))
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList(),
                cancellationToken)
            .ConfigureAwait(false);

        var existingByRelativePath = manifest.SourceFiles
            .Where(x => x.PackageRevisionId == packageRevisionId)
            .ToDictionary(x => NormalizeRelativePath(x.RelativePath), StringComparer.Ordinal);

        var indexedFiles = new List<WccSourceFileRecord>(discoveredFiles.Count);
        var analyses = new List<WccFileAnalysis>(discoveredFiles.Count);
        var seenRelativePaths = new HashSet<string>(StringComparer.Ordinal);
        var changeSet = new WccLibraryChangeSet();
        var diagnostics = new List<WccDiagnosticRecord>();
        var cacheHits = 0;
        var analyzedFiles = 0;

        for (var index = 0; index < discoveredFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = discoveredFiles[index];
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(fullRoot, path));
            var isMusic = IsMusicResource(fullRoot, path);
            seenRelativePaths.Add(relativePath);
            progress?.Report(new WccPackageIndexProgress(index, discoveredFiles.Count, relativePath));

            existingByRelativePath.TryGetValue(relativePath, out var existingFile);

            try
            {
                var info = new FileInfo(path);
                var lastWriteTimeUtc = new DateTimeOffset(info.LastWriteTimeUtc);
                WccFileAnalysis? analysis = null;
                WccSourceFileRecord sourceFile;

                if (existingFile is not null &&
                    // A copied project retains its stable IDs, lengths and timestamps.
                    // Re-read a new physical route before reusing its cached analysis.
                    WccPackagePlanEditor.LocationsEqual(existingFile.StoragePath, path) &&
                    existingFile.FileType == (isMusic ? WccSourceFileTypes.Audio : WccSourceFileTypes.Wml) &&
                    existingFile.Length == info.Length &&
                    existingFile.LastWriteTimeUtc == lastWriteTimeUtc)
                {
                    analysis = await _store
                        .LoadFileAnalysisAsync(existingFile.Id, cancellationToken)
                        .ConfigureAwait(false);

                    if (analysis is not null &&
                        analysis.FormatVersion == WccLibraryFormat.CurrentFileAnalysisVersion &&
                        string.Equals(
                            analysis.ContentHash,
                            existingFile.ContentHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        sourceFile = existingFile;
                        cacheHits++;
                        indexedFiles.Add(sourceFile);
                        analyses.Add(analysis);
                        continue;
                    }
                }

                // Music is byte-hashed without invoking the WML parser.
                var text = isMusic ? null : await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var contentHash = isMusic ? await WccContentHash.ComputeFileAsync(path, cancellationToken).ConfigureAwait(false)
                    : WccContentHash.ComputeText(text!);

                sourceFile = new WccSourceFileRecord
                {
                    Id = existingFile?.Id ?? (usesStableWorkingFileIds
                        ? WccLibraryScopedIdFactory.CreateSourceFileId(packageRevisionId, relativePath)
                        : WccSourceFileId.New()),
                    PackageRevisionId = packageRevisionId,
                    RelativePath = relativePath,
                    FileType = isMusic ? WccSourceFileTypes.Audio : WccSourceFileTypes.Wml,
                    ContentHash = contentHash,
                    Length = info.Length,
                    LastWriteTimeUtc = lastWriteTimeUtc,
                    StoragePath = path,
                    LicenseOverride = existingFile?.LicenseOverride
                };

                if (existingFile is not null &&
                    string.Equals(existingFile.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    analysis = await _store
                        .LoadFileAnalysisAsync(existingFile.Id, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (analysis is null ||
                    analysis.FormatVersion != WccLibraryFormat.CurrentFileAnalysisVersion ||
                    !string.Equals(analysis.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    analysis = isMusic ? new WccFileAnalysis { SourceFileId = sourceFile.Id, ContentHash = contentHash }
                        : _analyzer.Analyze(sourceFile, text!);
                    await _store.SaveFileAnalysisAsync(analysis, cancellationToken).ConfigureAwait(false);
                    analyzedFiles++;
                }
                else
                {
                    cacheHits++;
                }

                indexedFiles.Add(sourceFile);
                analyses.Add(analysis);

                if (existingFile is null)
                    changeSet.AddedSourceFileIds.Add(sourceFile.Id);
                else if (!SourceFileMetadataEquals(existingFile, sourceFile))
                    changeSet.UpdatedSourceFileIds.Add(sourceFile.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "library.source_file_index_failed",
                    Message = $"Could not index '{relativePath}': {ex.Message}",
                    Severity = WccDiagnosticSeverity.Warning,
                    SourceFileId = existingFile?.Id
                });

                // Keep the last known-good record/analysis published if refreshing it failed.
                if (existingFile is not null)
                {
                    var lastKnownAnalysis = await _store
                        .LoadFileAnalysisAsync(existingFile.Id, cancellationToken)
                        .ConfigureAwait(false);

                    indexedFiles.Add(existingFile);
                    if (lastKnownAnalysis is not null)
                        analyses.Add(lastKnownAnalysis);
                }
            }
        }

        progress?.Report(new WccPackageIndexProgress(
            discoveredFiles.Count,
            discoveredFiles.Count,
            CurrentRelativePath: null));

        foreach (var removed in existingByRelativePath.Values
                     .Where(x => !seenRelativePaths.Contains(NormalizeRelativePath(x.RelativePath))))
        {
            changeSet.RemovedSourceFileIds.Add(removed.Id);
        }

        // Do not publish a fingerprint for a partial/failed refresh. In that case the
        // revision continues to describe its last completely indexed state.
        var packageFingerprint = diagnostics.Count == 0
            ? ComputePackageFingerprint(indexedFiles)
            : packageRevision.ContentFingerprint;

        var nextManifest = new WccLibraryManifest
        {
            FormatVersion = manifest.FormatVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Packages = manifest.Packages.ToList(),
            PackageRevisions = manifest.PackageRevisions
                .Select(x => x.Id == packageRevisionId
                    ? CopyRevisionWithContentFingerprint(x, packageFingerprint)
                    : x)
                .ToList(),
            SourceFiles = manifest.SourceFiles
                .Where(x => x.PackageRevisionId != packageRevisionId)
                .Concat(indexedFiles)
                .OrderBy(x => x.PackageRevisionId.Value)
                .ThenBy(x => x.RelativePath, StringComparer.Ordinal)
                .ToList()
        };

        await _store.SaveManifestAsync(nextManifest, cancellationToken).ConfigureAwait(false);

        // Delete obsolete shards only after the new manifest no longer references them.
        foreach (var removedId in changeSet.RemovedSourceFileIds)
        {
            await _store.DeleteFileAnalysisAsync(removedId, cancellationToken).ConfigureAwait(false);
        }

        var result = new WccPackageIndexResult
        {
            Manifest = nextManifest,
            FileAnalyses = analyses,
            ChangeSet = changeSet,
            CacheHitCount = cacheHits,
            AnalyzedFileCount = analyzedFiles
        };
        result.Diagnostics.AddRange(diagnostics);
        return result;
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    internal static bool IsIndexedSourceFile(string root, string path) =>
        string.Equals(Path.GetExtension(path), ".cfg", StringComparison.OrdinalIgnoreCase) || IsMusicResource(root, path);

    private static bool IsMusicResource(string root, string path)
    {
        var relative = NormalizeRelativePath(Path.GetRelativePath(root, path));
        return (relative.StartsWith("music/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/music/", StringComparison.OrdinalIgnoreCase)) &&
            WccPackageMusicImporter.FilePatterns.Contains("*" + Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static bool SourceFileMetadataEquals(
        WccSourceFileRecord left,
        WccSourceFileRecord right)
        => left.ContentHash == right.ContentHash && left.FileType == right.FileType &&
           left.Length == right.Length &&
           left.LastWriteTimeUtc == right.LastWriteTimeUtc &&
           string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal) &&
           string.Equals(left.StoragePath, right.StoragePath, StringComparison.Ordinal);

    private static string ComputePackageFingerprint(
        IEnumerable<WccSourceFileRecord> sourceFiles)
    {
        var canonical = string.Join(
            "\n",
            sourceFiles
                .OrderBy(x => NormalizeRelativePath(x.RelativePath), StringComparer.Ordinal)
                .Select(x =>
                    $"{NormalizeRelativePath(x.RelativePath)}\0{x.ContentHash}"));
        return WccContentHash.ComputeText(canonical);
    }

    private static WccPackageRevisionRecord CopyRevisionWithContentFingerprint(
        WccPackageRevisionRecord revision,
        string? contentFingerprint)
        => new()
        {
            Id = revision.Id,
            PackageId = revision.PackageId,
            Version = revision.Version,
            ContentFingerprint = contentFingerprint,
            TargetWesnothVersion = revision.TargetWesnothVersion,
            ImportedAtUtc = revision.ImportedAtUtc,
            License = revision.License,
            AddonMetadata = revision.AddonMetadata,
            DependencyRevisionIds = revision.DependencyRevisionIds.ToList()
        };
}
