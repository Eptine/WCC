using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wcc.Core.Library;

namespace Wcc.Core.Wml;

public enum WesnothPreprocessStatus
{
    CacheHit,
    Preprocessed,
    InvalidRequest,
    MissingExecutable,
    MissingInput,
    DependencyUnverified,
    DependenciesChanged,
    ProcessFailed,
    OutputMissing,
    TimedOut,
    IoFailure
}

public sealed record WesnothPreprocessDependencyFile(
    string RelativePath,
    string ContentHash);

/// <summary>
/// One complete, indexed runtime package participating in preprocessing. Expected WML
/// files prove that the package currently on disk is still the immutable revision selected
/// by WCC; auxiliary preprocessor-readable files are fingerprinted from disk for cache safety.
/// </summary>
public sealed record WesnothPreprocessDependencyScope(
    string Identity,
    string RootPath,
    IReadOnlyList<WesnothPreprocessDependencyFile> ExpectedWmlFiles);

/// <summary>
/// A complete runtime include tree. All file bytes below the root
/// are content-fingerprinted so persistent output cannot survive an on-disk routing change.
/// </summary>
public sealed record WesnothPreprocessRoutingRoot(
    string Identity,
    string RootPath);

public sealed record WesnothPreprocessRequest(
    string ExecutablePath,
    string InputPath,
    IReadOnlyList<string> Defines,
    string DependencyIdentity,
    string? UserDataDirectory = null,
    string? UserCacheDirectory = null,
    IReadOnlyList<WesnothPreprocessDependencyScope>? DependencyScopes = null,
    IReadOnlyList<string>? RoutingFiles = null,
    IReadOnlyList<WesnothPreprocessRoutingRoot>? RoutingRoots = null,
    string? DataDirectory = null);

public sealed record WesnothPreprocessResult(
    WesnothPreprocessStatus Status,
    string? NormalizedWml = null,
    string? Diagnostic = null,
    string? CacheKey = null,
    string? VerifiedDependencyFingerprint = null,
    IReadOnlyList<WesnothPreprocessedScenarioOrigin>? ScenarioOrigins = null,
    IReadOnlyList<WesnothPreprocessedScalarOwner>? ScalarOwners = null)
{
    public bool IsSuccess => Status is
        WesnothPreprocessStatus.CacheHit or
        WesnothPreprocessStatus.Preprocessed;
}

/// <summary>
/// Runs Wesnoth's authoritative preprocessor in an isolated output directory.
/// The normalized .cfg output is cached by engine, input bytes, selected defines,
/// configured profile paths, and an explicit dependency identity from the caller.
/// </summary>
public sealed class WesnothPreprocessor
{
    private const string CacheSchema = "wcc-wesnoth-preprocess-v8";
    private const int DefaultMaximumCacheEntries = 24;
    private const long DefaultMaximumCacheBytes = 256L * 1024 * 1024;
    private static readonly string[] RoutingDependencyExtensions = [".cfg", ".lua", ".map", ".mask", ".txt"];
    private static readonly SemaphoreSlim PreprocessGate = new(1, 1);

    private readonly string _cacheRoot;
    private readonly string _temporaryRoot;
    private readonly IWesnothPreprocessProcessRunner _processRunner;
    private readonly TimeSpan _processTimeout;
    private readonly int _maximumCacheEntries;
    private readonly long _maximumCacheBytes;

    public WesnothPreprocessor(
        string cacheRoot,
        IWesnothPreprocessProcessRunner? processRunner = null,
        TimeSpan? timeout = null,
        string? temporaryRoot = null,
        int maximumCacheEntries = DefaultMaximumCacheEntries,
        long maximumCacheBytes = DefaultMaximumCacheBytes)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
            throw new ArgumentException("A Wesnoth preprocess cache root is required.", nameof(cacheRoot));

        _cacheRoot = Path.GetFullPath(cacheRoot);
        _temporaryRoot = Path.GetFullPath(temporaryRoot ?? Path.Combine(
            Path.GetTempPath(),
            "WCC",
            "wesnoth-preprocess-v1"));
        _processRunner = processRunner ?? new WesnothPreprocessProcessRunner();
        _processTimeout = timeout ?? TimeSpan.FromMinutes(2);
        if (_processTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The preprocess timeout must be positive.");
        if (maximumCacheEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCacheEntries));
        if (maximumCacheBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCacheBytes));
        _maximumCacheEntries = maximumCacheEntries;
        _maximumCacheBytes = maximumCacheBytes;
    }

    public async Task<WesnothPreprocessResult> PreprocessAsync(
        WesnothPreprocessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryNormalizeRequest(request, out var normalized, out var validationDiagnostic))
        {
            return new WesnothPreprocessResult(
                WesnothPreprocessStatus.InvalidRequest,
                Diagnostic: validationDiagnostic);
        }

        if (!File.Exists(normalized.ExecutablePath))
        {
            return new WesnothPreprocessResult(
                WesnothPreprocessStatus.MissingExecutable,
                Diagnostic: "The configured Wesnoth executable could not be found.");
        }

        if (!File.Exists(normalized.InputPath))
        {
            return new WesnothPreprocessResult(
                WesnothPreprocessStatus.MissingInput,
                Diagnostic: "The Wesnoth preprocess input could not be found.");
        }

        try
        {
            await PreprocessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var verification = await VerifyDependencyScopesAsync(
                    normalized.DependencyScopes,
                    normalized.RoutingFiles.Append(normalized.InputPath).Distinct(PathComparer).ToArray(),
                    normalized.RoutingRoots,
                    cancellationToken).ConfigureAwait(false);
                if (!verification.IsVerified)
                {
                    return new WesnothPreprocessResult(
                        WesnothPreprocessStatus.DependencyUnverified,
                        Diagnostic: verification.Diagnostic);
                }

                var cacheKey = await ComputeCacheKeyAsync(
                    normalized,
                    verification.Fingerprint!,
                    cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(_cacheRoot);
                var cached = await TryReadCachedAsync(
                    cacheKey,
                    verification.Fingerprint!,
                    normalized.DependencyScopes,
                    cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    var confirmedVerification = await VerifyDependencyScopesAsync(
                        normalized.DependencyScopes,
                        normalized.RoutingFiles.Append(normalized.InputPath).Distinct(PathComparer).ToArray(),
                        normalized.RoutingRoots,
                        cancellationToken).ConfigureAwait(false);
                    if (!confirmedVerification.IsVerified)
                    {
                        return new WesnothPreprocessResult(
                            WesnothPreprocessStatus.DependenciesChanged,
                            Diagnostic: confirmedVerification.Diagnostic,
                            CacheKey: cacheKey);
                    }

                    var confirmedCacheKey = await ComputeCacheKeyAsync(
                        normalized,
                        confirmedVerification.Fingerprint!,
                        cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(cacheKey, confirmedCacheKey, StringComparison.Ordinal))
                    {
                        return new WesnothPreprocessResult(
                            WesnothPreprocessStatus.DependenciesChanged,
                            Diagnostic: "The Wesnoth runtime inputs changed while cached output was being verified.",
                            CacheKey: cacheKey);
                    }

                    TryTouchCacheEntry(cacheKey);
                    return new WesnothPreprocessResult(
                        WesnothPreprocessStatus.CacheHit,
                        cached.NormalizedWml,
                        CacheKey: cacheKey,
                        VerifiedDependencyFingerprint: confirmedVerification.Fingerprint,
                        ScenarioOrigins: cached.ScenarioOrigins,
                        ScalarOwners: normalized.UserDataDirectory is not null && confirmedVerification.HasTrustedSourceMarkers
                            ? cached.ScalarOwners : null);
                }

                return await RunAndCacheAsync(
                    normalized,
                    cacheKey,
                    verification.Fingerprint!,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                PreprocessGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return new WesnothPreprocessResult(
                WesnothPreprocessStatus.IoFailure,
                Diagnostic: ex.Message);
        }
    }

    /// <summary>
    /// Runs a dependency-verified preprocess without reading or writing WCC's
    /// persistent normalized-output cache. This is intended for unique staged
    /// inputs whose lifetime is bounded by a validation transaction.
    /// </summary>
    public async Task<WesnothPreprocessResult> PreprocessTransientAsync(
        WesnothPreprocessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryNormalizeRequest(request, out var normalized, out var validationDiagnostic))
        {
            return new WesnothPreprocessResult(
                WesnothPreprocessStatus.InvalidRequest,
                Diagnostic: validationDiagnostic);
        }

        if (!File.Exists(normalized.ExecutablePath))
        {
            return new WesnothPreprocessResult(
                WesnothPreprocessStatus.MissingExecutable,
                Diagnostic: "The configured Wesnoth executable could not be found.");
        }

        if (!File.Exists(normalized.InputPath))
        {
            return new WesnothPreprocessResult(
                WesnothPreprocessStatus.MissingInput,
                Diagnostic: "The Wesnoth preprocess input could not be found.");
        }

        try
        {
            await PreprocessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var verification = await VerifyDependencyScopesAsync(
                    normalized.DependencyScopes,
                    normalized.RoutingFiles.Append(normalized.InputPath).Distinct(PathComparer).ToArray(),
                    normalized.RoutingRoots,
                    cancellationToken).ConfigureAwait(false);
                if (!verification.IsVerified)
                {
                    return new WesnothPreprocessResult(
                        WesnothPreprocessStatus.DependencyUnverified,
                        Diagnostic: verification.Diagnostic);
                }

                var transientIdentity = await ComputeCacheKeyAsync(
                    normalized,
                    verification.Fingerprint!,
                    cancellationToken).ConfigureAwait(false);
                return await RunAndCacheAsync(
                    normalized,
                    transientIdentity,
                    verification.Fingerprint!,
                    cancellationToken,
                    persistOutput: false).ConfigureAwait(false);
            }
            finally
            {
                PreprocessGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return new WesnothPreprocessResult(
                WesnothPreprocessStatus.IoFailure,
                Diagnostic: ex.Message);
        }
    }

    private async Task<WesnothPreprocessResult> RunAndCacheAsync(
        NormalizedRequest request,
        string cacheKey,
        string dependencyFingerprint,
        CancellationToken cancellationToken,
        bool persistOutput = true)
    {
        Directory.CreateDirectory(_temporaryRoot);
        var outputDirectory = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var invocation = new WesnothPreprocessInvocation(
                request.ExecutablePath,
                request.InputPath,
                outputDirectory,
                request.Defines,
                request.UserDataDirectory,
                request.UserCacheDirectory,
                request.DataDirectory);
            WesnothPreprocessProcessResult processResult;
            using (var processTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                processTimeoutSource.CancelAfter(_processTimeout);
                try
                {
                    processResult = await _processRunner.RunAsync(
                        invocation,
                        processTimeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new WesnothPreprocessResult(
                        WesnothPreprocessStatus.TimedOut,
                        Diagnostic: $"The Wesnoth preprocess process exceeded {_processTimeout.TotalSeconds:0} seconds.",
                        CacheKey: cacheKey);
                }
            }
            if (processResult.ExitCode != 0)
            {
                return new WesnothPreprocessResult(
                    WesnothPreprocessStatus.ProcessFailed,
                    Diagnostic: BuildProcessDiagnostic(processResult),
                    CacheKey: cacheKey);
            }

            var outputName = Path.GetFileNameWithoutExtension(request.InputPath) + ".cfg";
            var normalizedOutputPath = Path.Combine(outputDirectory, outputName);
            if (!IsUsableOutput(normalizedOutputPath))
            {
                return new WesnothPreprocessResult(
                    WesnothPreprocessStatus.OutputMissing,
                    Diagnostic: $"Wesnoth exited successfully but did not produce normalized output '{outputName}'.",
                    CacheKey: cacheKey);
            }

            var normalizedWml = await File.ReadAllTextAsync(
                normalizedOutputPath,
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            var scenarioOrigins = await WesnothPreprocessedScenarioOriginReader.ReadAsync(
                normalizedOutputPath + ".plain",
                request.DependencyScopes
                    .SelectMany(scope => scope.ExpectedWmlFiles.Select(file => Path.Combine(
                        scope.RootPath,
                        file.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
                    .ToArray(),
                request.RoutingRoots.Select(root => root.RootPath).ToArray(),
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<WesnothPreprocessedScalarOwner>? scalarOwners = await WesnothPreprocessedScalarOwnerReader.ReadAsync(
                normalizedOutputPath + ".plain",
                request.DependencyScopes.Select(scope => new WesnothPreprocessDependencyScope(
                    scope.Identity, scope.RootPath, scope.ExpectedWmlFiles)).ToArray(),
                request.RoutingRoots.Select(root => root.RootPath).ToArray(),
                cancellationToken).ConfigureAwait(false);

            var confirmedVerification = await VerifyDependencyScopesAsync(
                request.DependencyScopes,
                request.RoutingFiles.Append(request.InputPath).Distinct(PathComparer).ToArray(),
                request.RoutingRoots,
                cancellationToken).ConfigureAwait(false);
            if (!confirmedVerification.IsVerified)
            {
                return new WesnothPreprocessResult(
                    WesnothPreprocessStatus.DependenciesChanged,
                    Diagnostic: confirmedVerification.Diagnostic,
                    CacheKey: cacheKey);
            }

            var confirmedCacheKey = await ComputeCacheKeyAsync(
                request,
                confirmedVerification.Fingerprint!,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(cacheKey, confirmedCacheKey, StringComparison.Ordinal) ||
                !string.Equals(
                    dependencyFingerprint,
                    confirmedVerification.Fingerprint,
                    StringComparison.Ordinal))
            {
                return new WesnothPreprocessResult(
                    WesnothPreprocessStatus.DependenciesChanged,
                    Diagnostic: "The Wesnoth runtime inputs changed while preprocessing; the output was discarded.",
                    CacheKey: cacheKey);
            }

            // Wesnoth passes an authored raw 0xfe command byte through to .plain.
            // Any such input can forge another file's location. Valid UTF-8 U+00FE
            // has different bytes and does not weaken source marker trust.
            if (request.UserDataDirectory is null || !confirmedVerification.HasTrustedSourceMarkers)
                scalarOwners = null;

            if (persistOutput)
            {
                var cachedPath = GetCachedPath(cacheKey);
                var cachedOriginsPath = GetCachedOriginsPath(cacheKey);
                var cachedOwnersPath = GetCachedOwnersPath(cacheKey);
                var temporaryCachePath = cachedPath + $".{Guid.NewGuid():N}.tmp";
                var temporaryOriginsPath = cachedOriginsPath + $".{Guid.NewGuid():N}.tmp";
                var temporaryOwnersPath = cachedOwnersPath + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(normalizedOutputPath, temporaryCachePath, overwrite: true);
                    await File.WriteAllTextAsync(
                        temporaryOriginsPath,
                        JsonSerializer.Serialize(scenarioOrigins),
                        Encoding.UTF8,
                        cancellationToken).ConfigureAwait(false);
                    var ownersJson = JsonSerializer.Serialize(scalarOwners);
                    await File.WriteAllTextAsync(
                        temporaryOwnersPath,
                        JsonSerializer.Serialize(new CachedScalarOwnerEvidence(
                            cacheKey,
                            confirmedVerification.Fingerprint!,
                            WccContentHash.ComputeText(normalizedWml),
                            WccContentHash.ComputeText(ownersJson),
                            ownersJson)),
                        Encoding.UTF8,
                        cancellationToken).ConfigureAwait(false);
                    File.Move(temporaryCachePath, cachedPath, overwrite: true);
                    File.Move(temporaryOriginsPath, cachedOriginsPath, overwrite: true);
                    File.Move(temporaryOwnersPath, cachedOwnersPath, overwrite: true);
                    PruneCache(cacheKey);
                }
                finally
                {
                    TryDeleteFile(temporaryCachePath);
                    TryDeleteFile(temporaryOriginsPath);
                    TryDeleteFile(temporaryOwnersPath);
                }
            }

            return new WesnothPreprocessResult(
                WesnothPreprocessStatus.Preprocessed,
                normalizedWml,
                CacheKey: cacheKey,
                VerifiedDependencyFingerprint: confirmedVerification.Fingerprint,
                ScenarioOrigins: scenarioOrigins,
                ScalarOwners: scalarOwners);
        }
        finally
        {
            TryDeleteDirectory(outputDirectory);
        }
    }

    private async Task<CachedOutput?> TryReadCachedAsync(
        string cacheKey,
        string dependencyFingerprint,
        IReadOnlyList<NormalizedDependencyScope> dependencyScopes,
        CancellationToken cancellationToken)
    {
        var path = GetCachedPath(cacheKey);
        var originsPath = GetCachedOriginsPath(cacheKey);
        if (!IsUsableOutput(path) || !IsUsableOutput(originsPath))
            return null;

        try
        {
            var normalizedWml = await File.ReadAllTextAsync(
                path,
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            var serializedOrigins = await File.ReadAllTextAsync(
                originsPath,
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            var scenarioOrigins = JsonSerializer.Deserialize<WesnothPreprocessedScenarioOrigin[]>(
                serializedOrigins);
            if (scenarioOrigins is null)
                return null;
            var scalarOwners = await TryReadCachedScalarOwnersAsync(
                cacheKey, dependencyFingerprint, normalizedWml, dependencyScopes, cancellationToken)
                .ConfigureAwait(false);
            return new CachedOutput(normalizedWml, scenarioOrigins, scalarOwners);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string GetCachedPath(string cacheKey) => Path.Combine(_cacheRoot, cacheKey + ".cfg");

    private string GetCachedOriginsPath(string cacheKey) =>
        Path.Combine(_cacheRoot, cacheKey + ".origins.json");

    private string GetCachedOwnersPath(string cacheKey) =>
        Path.Combine(_cacheRoot, cacheKey + ".owners.json");

    private async Task<IReadOnlyList<WesnothPreprocessedScalarOwner>?> TryReadCachedScalarOwnersAsync(
        string cacheKey,
        string dependencyFingerprint,
        string normalizedWml,
        IReadOnlyList<NormalizedDependencyScope> dependencyScopes,
        CancellationToken cancellationToken)
    {
        var path = GetCachedOwnersPath(cacheKey);
        try
        {
            if (!IsUsableOutput(path))
                return null;
            var serialized = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            var evidence = JsonSerializer.Deserialize<CachedScalarOwnerEvidence>(serialized);
            if (evidence is null || evidence.OwnersJson is null ||
                !string.Equals(evidence.CacheKey, cacheKey, StringComparison.Ordinal) ||
                !string.Equals(evidence.DependencyFingerprint, dependencyFingerprint, StringComparison.Ordinal) ||
                !string.Equals(evidence.NormalizedTextHash, WccContentHash.ComputeText(normalizedWml), StringComparison.Ordinal) ||
                !string.Equals(evidence.OwnersHash, WccContentHash.ComputeText(evidence.OwnersJson), StringComparison.Ordinal))
            {
                return null;
            }

            var owners = JsonSerializer.Deserialize<WesnothPreprocessedScalarOwner[]>(evidence.OwnersJson);
            if (owners is null)
                return null;
            var sourceHashes = new Dictionary<string, string>(PathComparer);
            foreach (var scope in dependencyScopes)
            foreach (var file in scope.ExpectedWmlFiles)
            {
                var sourcePath = Path.GetFullPath(Path.Combine(
                    scope.RootPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (sourceHashes.TryGetValue(sourcePath, out var existingHash) &&
                    !string.Equals(existingHash, file.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                sourceHashes[sourcePath] = file.ContentHash;
            }

            var frozenOwners = new List<WesnothPreprocessedScalarOwner>(owners.Length);
            foreach (var owner in owners)
            {
                if (!TryFreezeCachedOwner(owner, "scenario", sourceHashes, out var frozen))
                    return null;
                frozenOwners.Add(frozen!);
            }
            return frozenOwners.AsReadOnly();
        }
        catch (Exception ex) when (ex is JsonException || IsIoFailure(ex))
        {
            // The normalized engine output remains useful without optional owner proof.
            // Damaged or incompatible evidence must never acquire a best-effort owner.
            return null;
        }
    }

    private static bool TryFreezeCachedOwner(
        WesnothPreprocessedScalarOwner? owner,
        string expectedTag,
        IReadOnlyDictionary<string, string> sourceHashes,
        out WesnothPreprocessedScalarOwner? frozen)
    {
        frozen = null;
        if (owner is null || !string.Equals(owner.TagName, expectedTag, StringComparison.Ordinal) ||
            owner.Scalars is null || owner.Children is null ||
            (expectedTag == "side" && owner.Children.Count != 0))
        {
            return false;
        }
        if (owner.Opening is { } opening &&
            (!Path.IsPathFullyQualified(opening.SourcePath) ||
             !sourceHashes.TryGetValue(Path.GetFullPath(opening.SourcePath), out var expectedHash) ||
             !string.Equals(expectedHash, opening.ContentHash, StringComparison.OrdinalIgnoreCase) ||
             opening.StartOffset < 0 || opening.Length <= 0 ||
             opening.StartOffset > int.MaxValue - opening.Length))
        {
            return false;
        }
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in owner.Scalars)
        {
            if (value is null ||
                (expectedTag == "scenario" ? key != "turns" : key is not ("side" or "gold" or "income")))
            {
                return false;
            }
            scalars.Add(key, value);
        }
        var children = new List<WesnothPreprocessedScalarOwner>(owner.Children.Count);
        foreach (var child in owner.Children)
        {
            if (!TryFreezeCachedOwner(child, "side", sourceHashes, out var frozenChild))
                return false;
            children.Add(frozenChild!);
        }
        frozen = owner with
        {
            Scalars = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(scalars),
            Children = children.AsReadOnly()
        };
        return true;
    }

    private async Task<DependencyVerification> VerifyDependencyScopesAsync(
        IReadOnlyList<NormalizedDependencyScope> scopes,
        IReadOnlyList<string> routingFiles,
        IReadOnlyList<NormalizedRoutingRoot> routingRoots,
        CancellationToken cancellationToken)
    {
        if (scopes.Count == 0)
        {
            return DependencyVerification.Failure(
                "Exact preprocessing requires a complete, indexed runtime-package dependency scope.");
        }

        if (!routingRoots.Any(root => !root.AllowMissing))
        {
            return DependencyVerification.Failure(
                "Exact preprocessing requires a complete runtime include-tree routing root.");
        }

        var canonical = new StringBuilder();
        var hashesByPath = new Dictionary<string, DependencyFileHashes>(PathComparer);
        foreach (var routingRoot in routingRoots
                     .OrderBy(x => x.Identity, StringComparer.Ordinal)
                     .ThenBy(x => x.RootPath, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            canonical.Append("routing-root\0")
                .Append(routingRoot.Identity).Append('\0')
                .Append(routingRoot.RootPath).Append('\n');
            if (!Directory.Exists(routingRoot.RootPath))
            {
                if (routingRoot.AllowMissing)
                {
                    canonical.Append("missing\n");
                    continue;
                }
                return DependencyVerification.Failure(
                    $"Wesnoth routing root '{routingRoot.Identity}' is unavailable at '{routingRoot.RootPath}'.");
            }

            canonical.Append("exists\n");
            AppendDirectoryNamespace(canonical, routingRoot.RootPath, cancellationToken);
            // Explicit includes accept any extension, and expanding discarded
            // macro arguments can change definitions without leaving .plain
            // markers. Both bytes and complete filename membership are inputs.
            var routedPaths = Directory.EnumerateFiles(
                    routingRoot.RootPath, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    RelativePath = NormalizeRelativePath(Path.GetRelativePath(routingRoot.RootPath, path)),
                    FullPath = path
                })
                .OrderBy(x => x.RelativePath, StringComparer.Ordinal)
                .ToArray();
            foreach (var routed in routedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hashes = await GetDependencyFileHashesAsync(
                    routed.FullPath,
                    hashesByPath,
                    cancellationToken,
                    inspectText: IsKnownTextDependency(routed.FullPath)).ConfigureAwait(false);
                canonical.Append("routed\0")
                    .Append(routed.RelativePath.Length).Append(':').Append(routed.RelativePath).Append('\0')
                    .Append(hashes.ByteHash).Append('\n');
            }
        }

        foreach (var routingFile in routingFiles.OrderBy(x => x, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(routingFile))
            {
                return DependencyVerification.Failure(
                    $"Wesnoth routing file '{routingFile}' is unavailable.");
            }

            var hashes = await GetDependencyFileHashesAsync(
                routingFile,
                hashesByPath,
                cancellationToken).ConfigureAwait(false);
            canonical.Append("routing\0")
                .Append(routingFile).Append('\0')
                .Append(hashes.ByteHash).Append('\n');
        }

        foreach (var scope in scopes
                     .OrderBy(x => x.Identity, StringComparer.Ordinal)
                     .ThenBy(x => x.RootPath, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(scope.RootPath))
            {
                return DependencyVerification.Failure(
                    $"Runtime dependency '{scope.Identity}' is unavailable at '{scope.RootPath}'.");
            }

            var actualPaths = Directory
                .EnumerateFiles(scope.RootPath, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    RelativePath = NormalizeRelativePath(Path.GetRelativePath(scope.RootPath, path)),
                    FullPath = path
                })
                .OrderBy(x => x.RelativePath, StringComparer.Ordinal)
                .ToArray();
            var actualByRelativePath = new Dictionary<string, string>(PathComparer);
            foreach (var file in actualPaths.Where(file =>
                         string.Equals(Path.GetExtension(file.FullPath), ".cfg", StringComparison.OrdinalIgnoreCase)))
            {
                if (!actualByRelativePath.TryAdd(file.RelativePath, file.FullPath))
                {
                    return DependencyVerification.Failure(
                        $"Runtime dependency '{scope.Identity}' contains duplicate WML path '{file.RelativePath}'.");
                }
            }

            var expectedByRelativePath = scope.ExpectedWmlFiles.ToDictionary(
                x => x.RelativePath,
                PathComparer);
            var missing = expectedByRelativePath.Keys
                .Where(path => !actualByRelativePath.ContainsKey(path))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            if (missing is not null)
            {
                return DependencyVerification.Failure(
                    $"Runtime dependency '{scope.Identity}' no longer contains indexed WML file '{missing}'. Refresh the Library before using exact output.");
            }

            var added = actualByRelativePath.Keys
                .Where(path => !expectedByRelativePath.ContainsKey(path))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            if (added is not null)
            {
                return DependencyVerification.Failure(
                    $"Runtime dependency '{scope.Identity}' contains unindexed WML file '{added}'. Refresh the Library before using exact output.");
            }

            canonical.Append("scope\0")
                .Append(scope.Identity).Append('\0')
                .Append(scope.RootPath).Append('\n');
            AppendDirectoryNamespace(canonical, scope.RootPath, cancellationToken);
            foreach (var expected in scope.ExpectedWmlFiles.OrderBy(
                         x => x.RelativePath,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = actualByRelativePath[expected.RelativePath];
                var hashes = await GetDependencyFileHashesAsync(
                    fullPath,
                    hashesByPath,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                        hashes.TextHash,
                        expected.ContentHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return DependencyVerification.Failure(
                        $"Runtime dependency '{scope.Identity}' changed on disk at '{expected.RelativePath}'. Refresh the Library before using exact output.");
                }

                canonical.Append("wml\0")
                    .Append(expected.RelativePath).Append('\0')
                    .Append(hashes.ByteHash).Append('\n');
            }

            foreach (var file in actualPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hashes = await GetDependencyFileHashesAsync(
                    file.FullPath,
                    hashesByPath,
                    cancellationToken,
                    inspectText: IsKnownTextDependency(file.FullPath)).ConfigureAwait(false);
                canonical.Append("scope-file\0")
                    .Append(file.RelativePath.Length).Append(':').Append(file.RelativePath).Append('\0')
                    .Append(hashes.ByteHash).Append('\n');
            }
        }

        return DependencyVerification.Success(WccContentHash.ComputeText(canonical.ToString())) with
        {
            HasTrustedSourceMarkers = hashesByPath.Values.All(hashes => !hashes.HasReservedSourceMarker)
        };
    }

    private async Task<DependencyFileHashes> GetDependencyFileHashesAsync(
        string path,
        Dictionary<string, DependencyFileHashes> hashesByPath,
        CancellationToken cancellationToken,
        bool inspectText = true)
    {
        var fullPath = Path.GetFullPath(path);
        if (hashesByPath.TryGetValue(fullPath, out var cached) &&
            (!inspectText || cached.TextHash is not null))
            return cached;

        var fileInfo = new FileInfo(fullPath);
        var lengthBeforeRead = fileInfo.Length;
        var lastWriteBeforeRead = fileInfo.LastWriteTimeUtc;
        DependencyFileHashes result;
        if (inspectText)
        {
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var byteHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var textHash = WccContentHash.ComputeText(reader.ReadToEnd());
            result = new DependencyFileHashes(byteHash, textHash, bytes.Contains((byte)0xfe));
        }
        else
        {
            // Images, audio and arbitrary-extension includes share the same byte
            // identity. Do not decode binary resources or treat a marker-like
            // byte in an unrelated resource as authored preprocessor text.
            await using var stream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            result = new DependencyFileHashes(
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)),
                null,
                false);
        }
        if (cached is not null && !string.Equals(cached.ByteHash, result.ByteHash, StringComparison.Ordinal))
        {
            throw new IOException(
                $"Wesnoth runtime input '{fullPath}' changed while it was being fingerprinted.");
        }
        hashesByPath[fullPath] = result;
        fileInfo.Refresh();
        if (fileInfo.Length != lengthBeforeRead ||
            fileInfo.LastWriteTimeUtc != lastWriteBeforeRead)
        {
            throw new IOException(
                $"Wesnoth runtime input '{fullPath}' changed while it was being fingerprinted.");
        }
        return result;
    }

    private static bool IsKnownTextDependency(string path) =>
        RoutingDependencyExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static void AppendDirectoryNamespace(
        StringBuilder canonical,
        string rootPath,
        CancellationToken cancellationToken)
    {
        // #ifhave tests filesystem existence, including empty directories.
        foreach (var path in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
                     .Select(path => NormalizeRelativePath(Path.GetRelativePath(rootPath, path)))
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            canonical.Append("directory\0").Append(path.Length).Append(':').Append(path).Append('\n');
        }
    }

    private static async Task<string> ComputeCacheKeyAsync(
        NormalizedRequest request,
        string verifiedDependencyFingerprint,
        CancellationToken cancellationToken)
    {
        await using var executable = File.OpenRead(request.ExecutablePath);
        var executableHash = Convert.ToHexString(
            await SHA256.HashDataAsync(executable, cancellationToken).ConfigureAwait(false));
        await using var input = File.OpenRead(request.InputPath);
        var inputHash = Convert.ToHexString(
            await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        var sortedDefines = request.Defines
            .Order(StringComparer.Ordinal)
            .ToArray();
        var identity = string.Join('\n',
            CacheSchema,
            request.ExecutablePath,
            executableHash,
            request.InputPath,
            inputHash,
            string.Join(',', sortedDefines),
            request.DependencyIdentity,
            verifiedDependencyFingerprint,
            request.UserDataDirectory ?? string.Empty,
            request.UserCacheDirectory ?? string.Empty,
            request.DataDirectory ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private static bool TryNormalizeRequest(
        WesnothPreprocessRequest request,
        out NormalizedRequest normalized,
        out string? diagnostic)
    {
        normalized = default!;
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            diagnostic = "A Wesnoth executable path is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.InputPath))
        {
            diagnostic = "A Wesnoth preprocess input path is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.DependencyIdentity))
        {
            diagnostic = "A preprocess dependency identity is required for safe caching.";
            return false;
        }

        var defines = new List<string>();
        foreach (var rawDefine in request.Defines ?? [])
        {
            var define = rawDefine?.Trim() ?? string.Empty;
            if (define.Length == 0)
                continue;
            if (define.IndexOfAny([',', '\r', '\n']) >= 0)
            {
                diagnostic = $"Preprocess define '{define}' contains an unsupported separator.";
                return false;
            }

            if (!defines.Contains(define, StringComparer.Ordinal))
                defines.Add(define);
        }

        try
        {
            if (!TryNormalizeDependencyScopes(
                    request.DependencyScopes,
                    out var dependencyScopes,
                    out diagnostic))
            {
                return false;
            }
            if (!TryNormalizeRoutingFiles(
                    request.RoutingFiles,
                    out var routingFiles,
                    out diagnostic))
            {
                return false;
            }
            if (!TryNormalizeRoutingRoots(
                    request.RoutingRoots,
                    out var routingRoots,
                    out diagnostic))
            {
                return false;
            }

            var userDataDirectory = NormalizeOptionalPath(request.UserDataDirectory);
            if (userDataDirectory is not null)
            {
                // ~/ includes are rooted at the entire userdata/data tree, not
                // only its add-ons child. A missing tree is a stable empty namespace.
                var userDataRoot = Path.Combine(userDataDirectory, "data");
                if (!routingRoots.Any(root => PathComparer.Equals(root.RootPath, userDataRoot)))
                {
                    routingRoots = [.. routingRoots, new NormalizedRoutingRoot(
                        "wcc-configured-user-data", userDataRoot, AllowMissing: true)];
                }
            }
            normalized = new NormalizedRequest(
                Path.GetFullPath(request.ExecutablePath),
                Path.GetFullPath(request.InputPath),
                defines,
                request.DependencyIdentity,
                userDataDirectory,
                NormalizeOptionalPath(request.UserCacheDirectory),
                dependencyScopes,
                routingFiles,
                routingRoots,
                NormalizeOptionalPath(request.DataDirectory));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostic = ex.Message;
            return false;
        }
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static bool TryNormalizeDependencyScopes(
        IReadOnlyList<WesnothPreprocessDependencyScope>? rawScopes,
        out IReadOnlyList<NormalizedDependencyScope> scopes,
        out string? diagnostic)
    {
        diagnostic = null;
        var normalized = new List<NormalizedDependencyScope>();
        var seenRoots = new HashSet<string>(PathComparer);
        foreach (var rawScope in rawScopes ?? [])
        {
            if (rawScope is null || string.IsNullOrWhiteSpace(rawScope.Identity))
            {
                diagnostic = "Every preprocess dependency scope requires an identity.";
                scopes = [];
                return false;
            }
            if (string.IsNullOrWhiteSpace(rawScope.RootPath))
            {
                diagnostic = $"Runtime dependency '{rawScope.Identity}' has no package root.";
                scopes = [];
                return false;
            }

            var rootPath = Path.GetFullPath(rawScope.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!seenRoots.Add(rootPath))
            {
                diagnostic = $"Runtime dependency root '{rootPath}' was supplied more than once.";
                scopes = [];
                return false;
            }

            var files = new List<WesnothPreprocessDependencyFile>();
            var seenRelativePaths = new HashSet<string>(PathComparer);
            foreach (var rawFile in rawScope.ExpectedWmlFiles ?? [])
            {
                var relativePath = NormalizeRelativePath(rawFile.RelativePath ?? string.Empty);
                if (relativePath.Length == 0 ||
                    Path.IsPathFullyQualified(relativePath) ||
                    relativePath.Equals("..", StringComparison.Ordinal) ||
                    relativePath.StartsWith("../", StringComparison.Ordinal))
                {
                    diagnostic = $"Runtime dependency '{rawScope.Identity}' contains invalid relative path '{rawFile.RelativePath}'.";
                    scopes = [];
                    return false;
                }
                if (!IsSha256(rawFile.ContentHash))
                {
                    diagnostic = $"Runtime dependency '{rawScope.Identity}' has no valid SHA-256 content hash for '{relativePath}'.";
                    scopes = [];
                    return false;
                }
                if (!seenRelativePaths.Add(relativePath))
                {
                    diagnostic = $"Runtime dependency '{rawScope.Identity}' contains duplicate WML path '{relativePath}'.";
                    scopes = [];
                    return false;
                }

                var resolvedPath = Path.GetFullPath(Path.Combine(
                    rootPath,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsPathInsideRoot(resolvedPath, rootPath))
                {
                    diagnostic = $"Runtime dependency path '{relativePath}' escapes package root '{rootPath}'.";
                    scopes = [];
                    return false;
                }

                files.Add(new WesnothPreprocessDependencyFile(
                    relativePath,
                    rawFile.ContentHash.ToLowerInvariant()));
            }

            if (files.Count == 0)
            {
                diagnostic = $"Runtime dependency '{rawScope.Identity}' has no indexed WML manifest.";
                scopes = [];
                return false;
            }

            normalized.Add(new NormalizedDependencyScope(
                rawScope.Identity.Trim(),
                rootPath,
                files));
        }

        scopes = normalized;
        return true;
    }

    private static bool TryNormalizeRoutingFiles(
        IReadOnlyList<string>? rawFiles,
        out IReadOnlyList<string> files,
        out string? diagnostic)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var rawFile in rawFiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawFile))
            {
                diagnostic = "A Wesnoth preprocess routing-file path is empty.";
                files = [];
                return false;
            }

            var fullPath = Path.GetFullPath(rawFile);
            if (seen.Add(fullPath))
                normalized.Add(fullPath);
        }

        files = normalized;
        diagnostic = null;
        return true;
    }

    private static bool TryNormalizeRoutingRoots(
        IReadOnlyList<WesnothPreprocessRoutingRoot>? rawRoots,
        out IReadOnlyList<NormalizedRoutingRoot> roots,
        out string? diagnostic)
    {
        var normalized = new List<NormalizedRoutingRoot>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var rawRoot in rawRoots ?? [])
        {
            if (rawRoot is null || string.IsNullOrWhiteSpace(rawRoot.Identity))
            {
                diagnostic = "Every Wesnoth preprocess routing root requires an identity.";
                roots = [];
                return false;
            }
            if (string.IsNullOrWhiteSpace(rawRoot.RootPath))
            {
                diagnostic = $"Wesnoth routing root '{rawRoot.Identity}' has no path.";
                roots = [];
                return false;
            }

            var fullPath = Path.GetFullPath(rawRoot.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!seen.Add(fullPath))
            {
                diagnostic = $"Wesnoth routing root '{fullPath}' was supplied more than once.";
                roots = [];
                return false;
            }

            normalized.Add(new NormalizedRoutingRoot(rawRoot.Identity.Trim(), fullPath));
        }

        roots = normalized;
        diagnostic = null;
        return true;
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }
        return true;
    }

    private static bool IsPathInsideRoot(string path, string root)
    {
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, PathComparison);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static bool IsUsableOutput(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static string BuildProcessDiagnostic(WesnothPreprocessProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        detail = detail.Trim();
        const int maxDetailLength = 4000;
        if (detail.Length > maxDetailLength)
            detail = detail[..maxDetailLength] + "…";
        return detail.Length == 0
            ? $"Wesnoth preprocessing failed with exit code {result.ExitCode}."
            : $"Wesnoth preprocessing failed with exit code {result.ExitCode}: {detail}";
    }

    private static bool IsIoFailure(Exception ex) => ex is
        IOException or
        InvalidDataException or
        UnauthorizedAccessException or
        ArgumentException or
        NotSupportedException or
        CryptographicException or
        InvalidOperationException or
        Win32Exception;

    private void TryTouchCacheEntry(string cacheKey)
    {
        try
        {
            File.SetLastWriteTimeUtc(GetCachedPath(cacheKey), DateTime.UtcNow);
            File.SetLastWriteTimeUtc(GetCachedOriginsPath(cacheKey), DateTime.UtcNow);
            if (File.Exists(GetCachedOwnersPath(cacheKey)))
                File.SetLastWriteTimeUtc(GetCachedOwnersPath(cacheKey), DateTime.UtcNow);
        }
        catch
        {
            // Cache-recency metadata is best effort.
        }
    }

    private void PruneCache(string retainedCacheKey)
    {
        try
        {
            var retainedPath = GetCachedPath(retainedCacheKey);
            var entries = Directory
                .EnumerateFiles(_cacheRoot, "*.cfg", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.LastWriteTimeUtc)
                .ThenBy(info => info.Name, StringComparer.Ordinal)
                .ToList();
            var entrySizes = entries.ToDictionary(
                info => info.FullName,
                info => info.Length +
                        OptionalCacheFileLength(GetCachedOriginsPath(Path.GetFileNameWithoutExtension(info.Name))) +
                        OptionalCacheFileLength(GetCachedOwnersPath(Path.GetFileNameWithoutExtension(info.Name))),
                PathComparer);
            var totalBytes = entrySizes.Values.Sum();
            var entryCount = entries.Count;
            foreach (var entry in entries)
            {
                if (entryCount <= _maximumCacheEntries && totalBytes <= _maximumCacheBytes)
                    break;
                if (string.Equals(entry.FullName, retainedPath, PathComparison))
                    continue;

                try
                {
                    File.Delete(entry.FullName);
                    TryDeleteFile(GetCachedOriginsPath(Path.GetFileNameWithoutExtension(entry.Name)));
                    TryDeleteFile(GetCachedOwnersPath(Path.GetFileNameWithoutExtension(entry.Name)));
                    totalBytes -= entrySizes[entry.FullName];
                    entryCount--;
                }
                catch
                {
                    // A cache entry may be in use by another process.
                }
            }
        }
        catch
        {
            // Cache pruning must never invalidate a successful preprocess result.
        }
    }

    private static long OptionalCacheFileLength(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cache cleanup is best effort.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // This path is a unique directory created by this request.
        }
    }

    private sealed record NormalizedRequest(
        string ExecutablePath,
        string InputPath,
        IReadOnlyList<string> Defines,
        string DependencyIdentity,
        string? UserDataDirectory,
        string? UserCacheDirectory,
        IReadOnlyList<NormalizedDependencyScope> DependencyScopes,
        IReadOnlyList<string> RoutingFiles,
        IReadOnlyList<NormalizedRoutingRoot> RoutingRoots,
        string? DataDirectory);

    private sealed record NormalizedDependencyScope(
        string Identity,
        string RootPath,
        IReadOnlyList<WesnothPreprocessDependencyFile> ExpectedWmlFiles);

    private sealed record NormalizedRoutingRoot(
        string Identity,
        string RootPath,
        bool AllowMissing = false);

    private sealed record DependencyFileHashes(
        string ByteHash,
        string? TextHash,
        bool HasReservedSourceMarker);

    private sealed record CachedOutput(
        string NormalizedWml,
        IReadOnlyList<WesnothPreprocessedScenarioOrigin> ScenarioOrigins,
        IReadOnlyList<WesnothPreprocessedScalarOwner>? ScalarOwners);

    private sealed record CachedScalarOwnerEvidence(
        string CacheKey,
        string DependencyFingerprint,
        string NormalizedTextHash,
        string OwnersHash,
        string OwnersJson);

    private sealed record DependencyVerification(
        bool IsVerified,
        string? Fingerprint,
        string? Diagnostic)
    {
        public bool HasTrustedSourceMarkers { get; init; }

        public static DependencyVerification Success(string fingerprint) =>
            new(true, fingerprint, null);

        public static DependencyVerification Failure(string diagnostic) =>
            new(false, null, diagnostic);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
