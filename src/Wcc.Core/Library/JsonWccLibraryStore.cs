using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wcc.Core.Library;

/// <summary>
/// BCL-only, sharded JSON store for the first library foundation.
/// The manifest is lightweight; each file analysis is persisted separately so one changed
/// source file does not rewrite every parsed entity/reference in the library.
/// </summary>
public sealed class JsonWccLibraryStore : IWccLibraryStore
{
    private const string ManifestFileName = "manifest.json";
    private const string AnalysisDirectoryName = "analysis";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const long DefaultMaxCachedAnalysisBytes = 128L * 1024 * 1024;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly WccFileAnalysisMemoryCache _analysisCache;

    public JsonWccLibraryStore(string libraryRoot)
        : this(libraryRoot, DefaultMaxCachedAnalysisBytes)
    {
    }

    /// <param name="maxCachedAnalysisBytes">Maximum conservative retained-graph accounting;
    /// zero disables reuse. This is not a process-memory limit.</param>
    public JsonWccLibraryStore(string libraryRoot, long maxCachedAnalysisBytes)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("Library root must be set.", nameof(libraryRoot));

        LibraryRoot = Path.GetFullPath(libraryRoot);
        _analysisCache = new WccFileAnalysisMemoryCache(maxCachedAnalysisBytes);
    }

    public string LibraryRoot { get; }

    public WccFileAnalysisCacheStatistics AnalysisCacheStatistics => _analysisCache.Statistics;

    /// <summary>Releases retained raw shards without changing durable data or counters.</summary>
    public void ClearFileAnalysisCache() => _analysisCache.Clear();

    public async Task<WccLibraryManifest> LoadManifestAsync(
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(LibraryRoot, ManifestFileName);
        if (!File.Exists(path))
            return new WccLibraryManifest();

        var manifest = await LoadJsonAsync<WccLibraryManifest>(path, cancellationToken)
            .ConfigureAwait(false);

        if (manifest is null)
            throw new InvalidDataException($"Library manifest is empty: {path}");

        EnsureSupportedVersion(
            manifest.FormatVersion,
            WccLibraryFormat.CurrentManifestVersion,
            "library manifest");

        return manifest;
    }

    public async Task SaveManifestAsync(
        WccLibraryManifest manifest,
        CancellationToken cancellationToken = default)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        EnsureSupportedVersion(
            manifest.FormatVersion,
            WccLibraryFormat.CurrentManifestVersion,
            "library manifest");

        var path = Path.Combine(LibraryRoot, ManifestFileName);
        await SaveJsonAtomicallyAsync(path, manifest, cancellationToken).ConfigureAwait(false);
        _analysisCache.Retain(manifest.SourceFiles.Select(file => file.Id));
    }

    public async Task<WccFileAnalysis?> LoadFileAnalysisAsync(
        WccSourceFileId sourceFileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetAnalysisPath(sourceFileId);
        var before = ReadAnalysisReceipt(path);
        if (before is null)
        {
            _analysisCache.Remove(sourceFileId);
            return null;
        }

        var generation = _analysisCache.Generation;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920, useAsync: true);
            // A metadata-only receipt could hide externally replaced/corrupt cache JSON.
            // Hash the actual opened bytes even on reuse; only deserialization is skipped.
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false));
            var afterHash = ReadAnalysisReceipt(path);
            var stableReceipt = before == afterHash;
            if (stableReceipt && _analysisCache.TryLoad(sourceFileId, digest) is { } cached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return cached;
            }

            stream.Position = 0;
            _analysisCache.RecordDeserialization();
            // Pair a retained graph with the bytes actually parsed. An in-place writer
            // on Linux can change the open file between the first hash and this read.
            using var parsedHash = SHA256.Create();
            await using var parsedStream = new CryptoStream(stream, parsedHash,
                CryptoStreamMode.Read, leaveOpen: true);
            var analysis = await JsonSerializer.DeserializeAsync<WccFileAnalysis>(
                    parsedStream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (analysis is null)
                throw new InvalidDataException($"File analysis is empty: {path}");

            EnsureSupportedVersion(analysis.FormatVersion,
                WccLibraryFormat.CurrentFileAnalysisVersion, "file analysis");
            if (analysis.SourceFileId != sourceFileId)
            {
                throw new InvalidDataException(
                    $"File analysis id {analysis.SourceFileId} does not match requested id {sourceFileId}.");
            }

            if (parsedStream.HasFlushedFinalBlock && stableReceipt && before == ReadAnalysisReceipt(path))
                _analysisCache.Remember(analysis, Convert.ToHexString(parsedHash.Hash!), generation);
            return analysis;
        }
        catch
        {
            _analysisCache.Remove(sourceFileId);
            throw;
        }
    }

    public async Task SaveFileAnalysisAsync(
        WccFileAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        if (analysis is null) throw new ArgumentNullException(nameof(analysis));

        EnsureSupportedVersion(
            analysis.FormatVersion,
            WccLibraryFormat.CurrentFileAnalysisVersion,
            "file analysis");

        _analysisCache.Remove(analysis.SourceFileId);
        try
        {
            await SaveJsonAtomicallyAsync(GetAnalysisPath(analysis.SourceFileId),
                analysis, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _analysisCache.Remove(analysis.SourceFileId);
        }
    }

    public async Task DeleteFileAnalysisAsync(
        WccSourceFileId sourceFileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _analysisCache.Remove(sourceFileId);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetAnalysisPath(sourceFileId);
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _analysisCache.Remove(sourceFileId);
            _writeLock.Release();
        }
    }

    private static AnalysisReceipt? ReadAnalysisReceipt(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? new AnalysisReceipt(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc)
            : null;
    }

    private sealed record AnalysisReceipt(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);

    private string GetAnalysisPath(WccSourceFileId sourceFileId)
        => Path.Combine(
            LibraryRoot,
            AnalysisDirectoryName,
            sourceFileId + ".json");

    private static async Task<T?> LoadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SaveJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;

        try
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException($"Could not determine directory for {path}.");

            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            _writeLock.Release();
        }
    }

    private static void EnsureSupportedVersion(int actual, int supported, string documentKind)
    {
        if (actual <= 0)
            throw new InvalidDataException($"Invalid {documentKind} format version: {actual}.");

        if (actual > supported)
        {
            throw new NotSupportedException(
                $"This WCC version supports {documentKind} format {supported}, but format {actual} was found.");
        }
    }
}
