using System.Security.Cryptography;
using System.Text;
using Wcc.Core.Library;

namespace Wcc.Core.Wml;

/// <summary>
/// One full-file candidate evaluated against a previously planned exact-runtime
/// request. The original hash is the authoritative open-file text hash, not a
/// timestamp or byte-length shortcut.
/// </summary>
public sealed record WesnothPreprocessOverlayRequest(
    WesnothPreprocessRequest BaselineRequest,
    string AuthoritativeSourcePath,
    string ExpectedOriginalTextHash,
    string CandidateFileText);

/// <summary>
/// Baseline and candidate outputs produced sequentially from one private routing
/// snapshot. A shared fingerprint is present only after both runs and final
/// authoritative/staged re-verification succeed.
/// </summary>
public sealed record WesnothPreprocessOverlayEvaluation(
    WesnothPreprocessResult? BaselineResult,
    WesnothPreprocessResult? CandidateResult,
    string? SharedEnvironmentFingerprint,
    WesnothPreprocessResult? FailureResult = null)
{
    public bool IsSuccess =>
        FailureResult is null &&
        BaselineResult?.IsSuccess == true &&
        CandidateResult?.IsSuccess == true &&
        !string.IsNullOrWhiteSpace(SharedEnvironmentFingerprint);
}

/// <summary>
/// Materializes a private Wesnoth data/user-data tree, replaces exactly one
/// manifest-verified WML file there, and preprocesses it without persistent
/// cache participation. The authoritative source tree is never written.
/// </summary>
public sealed class WesnothPreprocessOverlayEvaluator :
    IWesnothPreprocessOverlayEvaluationRunner
{
    private const string DataRoutingIdentity = "wesnoth-data";
    private const string AddonsRoutingIdentity = "wesnoth-add-ons";
    private static readonly HashSet<string> RoutedExtensions = new(
        [".cfg", ".lua", ".map", ".mask", ".txt"],
        StringComparer.OrdinalIgnoreCase);

    private readonly WesnothPreprocessor _preprocessor;
    private readonly string _temporaryRoot;

    public WesnothPreprocessOverlayEvaluator(
        WesnothPreprocessor preprocessor,
        string? temporaryRoot = null)
    {
        _preprocessor = preprocessor ?? throw new ArgumentNullException(nameof(preprocessor));
        _temporaryRoot = Path.GetFullPath(temporaryRoot ?? Path.Combine(
            Path.GetTempPath(),
            "WCC",
            "wesnoth-preprocess-overlay-v1"));
    }

    /// <summary>
    /// Compatibility entry point returning the candidate side of the paired
    /// evaluation. It still performs both transient runs in one overlay.
    /// </summary>
    public async Task<WesnothPreprocessResult> PreprocessAsync(
        WesnothPreprocessOverlayRequest request,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        return evaluation.CandidateResult ??
               evaluation.FailureResult ??
               evaluation.BaselineResult ??
               InvalidOverlay("The paired overlay evaluation produced no result.");
    }

    public async Task<WesnothPreprocessOverlayEvaluation> EvaluateAsync(
        WesnothPreprocessOverlayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string? overlayRoot = null;
        try
        {
            if (!TryValidateRequest(request, out var validated, out var validationResult))
                return FailedEvaluation(validationResult!);

            var originalVerification = await ReadStableTextHashAsync(
                validated!.AuthoritativeSourcePath,
                cancellationToken).ConfigureAwait(false);
            if (!originalVerification.IsStable ||
                !string.Equals(
                    originalVerification.ContentHash,
                    validated.ExpectedOriginalTextHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return FailedEvaluation(new WesnothPreprocessResult(
                    WesnothPreprocessStatus.DependencyUnverified,
                    Diagnostic: originalVerification.IsStable
                        ? "The authoritative source text no longer matches the expected original hash."
                        : "The authoritative source changed while its original text hash was being verified."));
            }

            Directory.CreateDirectory(_temporaryRoot);
            overlayRoot = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(overlayRoot);
            var layout = CreateLayout(validated, overlayRoot);
            var stagedFiles = new Dictionary<string, string>(PathComparer);
            foreach (var mapping in layout.RootMappings)
            {
                await StageRoutingRootAsync(
                    mapping,
                    stagedFiles,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var routingFile in validated.BaselineRequest.RoutingFiles ?? [])
            {
                if (!TryMapPath(routingFile, layout.RootMappings, out var stagedRoutingFile))
                {
                    return FailedEvaluation(OverlayRejected(
                        $"Routing file '{routingFile}' is outside the verified data/add-ons roots."));
                }

                StageFileOnce(routingFile, stagedRoutingFile!, stagedFiles);
            }

            if (!TryMapPath(
                    validated.AuthoritativeSourcePath,
                    layout.RootMappings,
                    out var stagedCandidatePath) ||
                !File.Exists(stagedCandidatePath))
            {
                return FailedEvaluation(OverlayRejected(
                    "The authoritative source could not be mapped into the staged routing tree."));
            }

            var preStageVerification = await ReadStableTextHashAsync(
                validated.AuthoritativeSourcePath,
                cancellationToken).ConfigureAwait(false);
            if (!preStageVerification.IsStable ||
                !string.Equals(
                    preStageVerification.ContentHash,
                    validated.ExpectedOriginalTextHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return FailedEvaluation(new WesnothPreprocessResult(
                    WesnothPreprocessStatus.DependenciesChanged,
                    Diagnostic: "The authoritative source changed while the private preprocess overlay was being staged."));
            }

            if (!TryBuildStagedRequest(
                    validated,
                    layout,
                    replacementText: null,
                    out var stagedBaselineRequest,
                    out var baselineRequestDiagnostic))
            {
                return FailedEvaluation(OverlayRejected(baselineRequestDiagnostic!));
            }

            var stagedEnvironment = await ComputeEnvironmentFingerprintAsync(
                validated,
                layout,
                useStagedPaths: true,
                cancellationToken).ConfigureAwait(false);
            var authoritativeEnvironment = await ComputeEnvironmentFingerprintAsync(
                validated,
                layout,
                useStagedPaths: false,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    stagedEnvironment,
                    authoritativeEnvironment,
                    StringComparison.Ordinal))
            {
                return FailedEvaluation(new WesnothPreprocessResult(
                    WesnothPreprocessStatus.DependenciesChanged,
                    Diagnostic: "The authoritative routing environment changed while its private snapshot was being staged."));
            }

            var baselineResult = await _preprocessor.PreprocessTransientAsync(
                stagedBaselineRequest!,
                cancellationToken).ConfigureAwait(false);
            if (!baselineResult.IsSuccess)
                return new WesnothPreprocessOverlayEvaluation(baselineResult, null, null);

            if (!TryRemapOrigins(
                    baselineResult.ScenarioOrigins ?? [],
                    layout.RootMappings,
                    out var remappedBaselineOrigins,
                    out var baselineRemapDiagnostic))
            {
                return new WesnothPreprocessOverlayEvaluation(
                    OverlayRejected(baselineRemapDiagnostic!, baselineResult.CacheKey),
                    null,
                    null);
            }
            baselineResult = baselineResult with
            {
                ScenarioOrigins = remappedBaselineOrigins,
                ScalarOwners = RemapScalarOwners(baselineResult.ScalarOwners, layout.RootMappings)
            };

            var afterBaselineVerification = await ReadStableTextHashAsync(
                validated.AuthoritativeSourcePath,
                cancellationToken).ConfigureAwait(false);
            if (!afterBaselineVerification.IsStable ||
                !string.Equals(
                    afterBaselineVerification.ContentHash,
                    validated.ExpectedOriginalTextHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new WesnothPreprocessOverlayEvaluation(
                    baselineResult,
                    new WesnothPreprocessResult(
                        WesnothPreprocessStatus.DependenciesChanged,
                        Diagnostic: "The authoritative source changed after baseline preprocessing and before candidate staging."),
                    null);
            }

            await ReplaceWithRegularFileAtomicallyAsync(
                stagedCandidatePath!,
                validated.CandidateFileText,
                cancellationToken).ConfigureAwait(false);

            if (!TryBuildStagedRequest(
                    validated,
                    layout,
                    validated.CandidateFileText,
                    out var stagedCandidateRequest,
                    out var candidateRequestDiagnostic))
            {
                return new WesnothPreprocessOverlayEvaluation(
                    baselineResult,
                    OverlayRejected(candidateRequestDiagnostic!),
                    null);
            }

            var candidateResult = await _preprocessor.PreprocessTransientAsync(
                stagedCandidateRequest!,
                cancellationToken).ConfigureAwait(false);
            if (!candidateResult.IsSuccess)
            {
                return new WesnothPreprocessOverlayEvaluation(
                    baselineResult,
                    candidateResult,
                    null);
            }

            var finalVerification = await ReadStableTextHashAsync(
                validated.AuthoritativeSourcePath,
                cancellationToken).ConfigureAwait(false);
            if (!finalVerification.IsStable ||
                !string.Equals(
                    finalVerification.ContentHash,
                    validated.ExpectedOriginalTextHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new WesnothPreprocessOverlayEvaluation(
                    baselineResult,
                    new WesnothPreprocessResult(
                        WesnothPreprocessStatus.DependenciesChanged,
                        Diagnostic: "The authoritative source changed while the staged candidate was being preprocessed.",
                        CacheKey: candidateResult.CacheKey),
                    null);
            }

            if (!TryRemapOrigins(
                    candidateResult.ScenarioOrigins ?? [],
                    layout.RootMappings,
                    out var remappedOrigins,
                    out var remapDiagnostic))
            {
                return new WesnothPreprocessOverlayEvaluation(
                    baselineResult,
                    OverlayRejected(remapDiagnostic!, candidateResult.CacheKey),
                    null);
            }
            candidateResult = candidateResult with
            {
                ScenarioOrigins = remappedOrigins,
                ScalarOwners = RemapScalarOwners(candidateResult.ScalarOwners, layout.RootMappings)
            };

            var finalStagedEnvironment = await ComputeEnvironmentFingerprintAsync(
                validated,
                layout,
                useStagedPaths: true,
                cancellationToken).ConfigureAwait(false);
            var finalAuthoritativeEnvironment = await ComputeEnvironmentFingerprintAsync(
                validated,
                layout,
                useStagedPaths: false,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(stagedEnvironment, finalStagedEnvironment, StringComparison.Ordinal) ||
                !string.Equals(stagedEnvironment, finalAuthoritativeEnvironment, StringComparison.Ordinal))
            {
                return new WesnothPreprocessOverlayEvaluation(
                    baselineResult,
                    new WesnothPreprocessResult(
                        WesnothPreprocessStatus.DependenciesChanged,
                        Diagnostic: "The staged or authoritative routing environment changed across paired preprocessing.",
                        CacheKey: candidateResult.CacheKey),
                    null);
            }

            var successGateVerification = await ReadStableTextHashAsync(
                validated.AuthoritativeSourcePath,
                cancellationToken).ConfigureAwait(false);
            if (!successGateVerification.IsStable ||
                !string.Equals(
                    successGateVerification.ContentHash,
                    validated.ExpectedOriginalTextHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new WesnothPreprocessOverlayEvaluation(
                    baselineResult,
                    new WesnothPreprocessResult(
                        WesnothPreprocessStatus.DependenciesChanged,
                        Diagnostic: "The authoritative source changed during final routing-environment verification.",
                        CacheKey: candidateResult.CacheKey),
                    null);
            }

            return new WesnothPreprocessOverlayEvaluation(
                baselineResult,
                candidateResult,
                stagedEnvironment);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return FailedEvaluation(new WesnothPreprocessResult(
                WesnothPreprocessStatus.IoFailure,
                Diagnostic: ex.Message));
        }
        finally
        {
            if (overlayRoot is not null)
                TryDeleteDirectory(overlayRoot);
        }
    }

    private static bool TryValidateRequest(
        WesnothPreprocessOverlayRequest request,
        out ValidatedOverlayRequest? validated,
        out WesnothPreprocessResult? failure)
    {
        validated = null;
        failure = null;
        if (request.BaselineRequest is null)
        {
            failure = InvalidOverlay("A verified baseline preprocess request is required.");
            return false;
        }
        if (request.CandidateFileText is null)
        {
            failure = InvalidOverlay("Full candidate-file text is required.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.AuthoritativeSourcePath))
        {
            failure = InvalidOverlay("An authoritative source path is required.");
            return false;
        }
        if (!IsSha256(request.ExpectedOriginalTextHash))
        {
            failure = InvalidOverlay("A valid expected original SHA-256 text hash is required.");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(request.BaselineRequest.DataDirectory))
        {
            failure = InvalidOverlay(
                "A baseline request with a pre-existing data-directory override cannot be staged safely.");
            return false;
        }

        string sourcePath;
        try
        {
            sourcePath = Path.GetFullPath(request.AuthoritativeSourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failure = InvalidOverlay(ex.Message);
            return false;
        }
        if (!File.Exists(sourcePath))
        {
            failure = new WesnothPreprocessResult(
                WesnothPreprocessStatus.MissingInput,
                Diagnostic: "The authoritative source file is unavailable.");
            return false;
        }
        if (!string.Equals(Path.GetExtension(sourcePath), ".cfg", StringComparison.OrdinalIgnoreCase))
        {
            failure = InvalidOverlay("Only one manifest-verified WML .cfg file may be overlaid.");
            return false;
        }

        var roots = request.BaselineRequest.RoutingRoots ?? [];
        var dataRoots = roots
            .Where(root => string.Equals(root.Identity, DataRoutingIdentity, StringComparison.Ordinal))
            .ToArray();
        var addonsRoots = roots
            .Where(root => string.Equals(root.Identity, AddonsRoutingIdentity, StringComparison.Ordinal))
            .ToArray();
        if (dataRoots.Length != 1 || addonsRoots.Length > 1 ||
            dataRoots.Length + addonsRoots.Length != roots.Count)
        {
            failure = InvalidOverlay(
                "The overlay requires exactly one wesnoth-data routing root and at most one wesnoth-add-ons routing root.");
            return false;
        }

        var dataRoot = Path.GetFullPath(dataRoots[0].RootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(dataRoot) ||
            !string.Equals(
                Path.GetFileName(dataRoot),
                "data",
                StringComparison.OrdinalIgnoreCase))
        {
            failure = InvalidOverlay("The wesnoth-data routing root is not a data directory.");
            return false;
        }

        string? addonsRoot = null;
        if (addonsRoots.Length == 1)
        {
            addonsRoot = Path.GetFullPath(addonsRoots[0].RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(addonsRoot) ||
                !string.Equals(
                    Path.GetFileName(addonsRoot),
                    "add-ons",
                    StringComparison.OrdinalIgnoreCase))
            {
                failure = InvalidOverlay("The wesnoth-add-ons routing root is not an add-ons directory.");
                return false;
            }
        }

        var manifestMatches = new List<ManifestMatch>();
        foreach (var scope in request.BaselineRequest.DependencyScopes ?? [])
        {
            var scopeRoot = Path.GetFullPath(scope.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsInsideOrEqual(sourcePath, scopeRoot))
                continue;

            var relativePath = NormalizeRelativePath(Path.GetRelativePath(scopeRoot, sourcePath));
            foreach (var file in scope.ExpectedWmlFiles ?? [])
            {
                if (PathComparer.Equals(
                        NormalizeRelativePath(file.RelativePath),
                        relativePath))
                {
                    manifestMatches.Add(new ManifestMatch(
                        scope.Identity,
                        scopeRoot,
                        relativePath,
                        file.ContentHash));
                }
            }
        }
        if (manifestMatches.Count != 1)
        {
            failure = new WesnothPreprocessResult(
                WesnothPreprocessStatus.DependencyUnverified,
                Diagnostic: manifestMatches.Count == 0
                    ? "The authoritative source is absent from the verified dependency manifest."
                    : "The authoritative source occurs more than once in the verified dependency manifest.");
            return false;
        }
        if (!string.Equals(
                manifestMatches[0].ContentHash,
                request.ExpectedOriginalTextHash,
                StringComparison.OrdinalIgnoreCase))
        {
            failure = new WesnothPreprocessResult(
                WesnothPreprocessStatus.DependencyUnverified,
                Diagnostic: "The authoritative source hash disagrees with its dependency-manifest hash.");
            return false;
        }

        var authoritativeRoots = new[] { dataRoot, addonsRoot }
            .Where(root => root is not null)
            .Cast<string>()
            .ToArray();
        if (authoritativeRoots.Count(root => IsInsideOrEqual(sourcePath, root)) != 1)
        {
            failure = InvalidOverlay(
                "The authoritative source does not belong to exactly one verified data/add-ons routing root.");
            return false;
        }
        foreach (var scope in request.BaselineRequest.DependencyScopes ?? [])
        {
            var scopeRoot = Path.GetFullPath(scope.RootPath);
            if (authoritativeRoots.Count(root => IsInsideOrEqual(scopeRoot, root)) != 1)
            {
                failure = InvalidOverlay(
                    $"Dependency scope '{scope.Identity}' does not belong to exactly one staged routing root.");
                return false;
            }
        }

        validated = new ValidatedOverlayRequest(
            request.BaselineRequest,
            sourcePath,
            request.ExpectedOriginalTextHash.ToLowerInvariant(),
            request.CandidateFileText,
            dataRoot,
            addonsRoot,
            manifestMatches[0]);
        return true;
    }

    private static OverlayLayout CreateLayout(
        ValidatedOverlayRequest request,
        string overlayRoot)
    {
        var runtimeRoot = Path.Combine(overlayRoot, "runtime");
        var userDataRoot = Path.Combine(overlayRoot, "user-data");
        var userCacheRoot = Path.Combine(overlayRoot, "user-cache");
        var stagedDataRoot = Path.Combine(runtimeRoot, "data");
        Directory.CreateDirectory(stagedDataRoot);
        // Wesnoth validates the data-directory shape even for headless work.
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "fonts"));
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "images"));
        Directory.CreateDirectory(Path.Combine(userDataRoot, "data", "add-ons"));
        Directory.CreateDirectory(userCacheRoot);

        var mappings = new List<RootMapping>
        {
            new(DataRoutingIdentity, request.DataRoot, stagedDataRoot)
        };
        if (request.AddonsRoot is not null)
        {
            mappings.Add(new RootMapping(
                AddonsRoutingIdentity,
                request.AddonsRoot,
                Path.Combine(userDataRoot, "data", "add-ons")));
        }

        return new OverlayLayout(runtimeRoot, userDataRoot, userCacheRoot, mappings);
    }

    private static async Task StageRoutingRootAsync(
        RootMapping mapping,
        Dictionary<string, string> stagedFiles,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(mapping.AuthoritativeRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        $"Routing directory '{childDirectory}' is a reparse point and cannot be staged safely.");
                }
                pending.Push(childDirectory);
            }

            foreach (var sourcePath in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        $"Routing file '{sourcePath}' is a reparse point and cannot be staged safely.");
                }
                if (!RoutedExtensions.Contains(Path.GetExtension(sourcePath)))
                    continue;

                if (!TryMapPath(sourcePath, [mapping], out var stagedPath))
                    throw new IOException($"Routing file '{sourcePath}' escaped its staged root.");
                StageFileOnce(sourcePath, stagedPath!, stagedFiles);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void StageFileOnce(
        string sourcePath,
        string stagedPath,
        Dictionary<string, string> stagedFiles)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        stagedPath = Path.GetFullPath(stagedPath);
        if (stagedFiles.TryGetValue(stagedPath, out var existingSource))
        {
            if (!PathComparer.Equals(existingSource, sourcePath))
                throw new IOException($"Two routing files map to staged path '{stagedPath}'.");
            return;
        }

        var directory = Path.GetDirectoryName(stagedPath) ??
                        throw new IOException($"Staged path '{stagedPath}' has no parent directory.");
        Directory.CreateDirectory(directory);
        File.Copy(sourcePath, stagedPath, overwrite: false);
        stagedFiles.Add(stagedPath, sourcePath);
    }

    private static async Task ReplaceWithRegularFileAtomicallyAsync(
        string stagedPath,
        string candidateText,
        CancellationToken cancellationToken)
    {
        stagedPath = Path.GetFullPath(stagedPath);
        var directory = Path.GetDirectoryName(stagedPath) ??
                        throw new IOException($"Staged path '{stagedPath}' has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(stagedPath)}.{Guid.NewGuid():N}.candidate.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes(candidateText);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            EnsureRegularFile(temporaryPath, "fresh staged candidate");
            EnsureRegularFile(stagedPath, "existing staged candidate");
            File.Move(temporaryPath, stagedPath, overwrite: true);
            EnsureRegularFile(stagedPath, "replaced staged candidate");
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void EnsureRegularFile(string path, string description)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory |
                           FileAttributes.ReparsePoint |
                           FileAttributes.Device)) != 0)
        {
            throw new IOException(
                $"The {description} path '{path}' is not a regular file.");
        }
    }

    private static bool TryBuildStagedRequest(
        ValidatedOverlayRequest request,
        OverlayLayout layout,
        string? replacementText,
        out WesnothPreprocessRequest? stagedRequest,
        out string? diagnostic)
    {
        stagedRequest = null;
        diagnostic = null;
        if (!TryMapPath(
                request.BaselineRequest.InputPath,
                layout.RootMappings,
                out var stagedInputPath))
        {
            diagnostic = "The baseline preprocess entry point is outside the staged routing roots.";
            return false;
        }

        var effectiveSourceHash = replacementText is null
            ? request.ExpectedOriginalTextHash
            : WccContentHash.ComputeText(replacementText);
        var scopes = new List<WesnothPreprocessDependencyScope>();
        foreach (var scope in request.BaselineRequest.DependencyScopes ?? [])
        {
            if (!TryMapPath(scope.RootPath, layout.RootMappings, out var stagedScopeRoot))
            {
                diagnostic = $"Dependency scope '{scope.Identity}' cannot be mapped into the overlay.";
                return false;
            }

            var expectedFiles = scope.ExpectedWmlFiles
                .Select(file =>
                {
                    var authoritativePath = Path.GetFullPath(Path.Combine(
                        scope.RootPath,
                        NormalizeRelativePath(file.RelativePath)
                            .Replace('/', Path.DirectorySeparatorChar)));
                    return new WesnothPreprocessDependencyFile(
                        NormalizeRelativePath(file.RelativePath),
                        PathComparer.Equals(authoritativePath, request.AuthoritativeSourcePath)
                            ? effectiveSourceHash
                            : file.ContentHash);
                })
                .ToArray();
            scopes.Add(new WesnothPreprocessDependencyScope(
                scope.Identity,
                stagedScopeRoot!,
                expectedFiles));
        }

        var routingFiles = new List<string>();
        foreach (var routingFile in request.BaselineRequest.RoutingFiles ?? [])
        {
            if (!TryMapPath(routingFile, layout.RootMappings, out var stagedRoutingFile))
            {
                diagnostic = $"Routing file '{routingFile}' cannot be mapped into the overlay.";
                return false;
            }
            routingFiles.Add(stagedRoutingFile!);
        }

        var routingRoots = layout.RootMappings
            .Select(mapping => new WesnothPreprocessRoutingRoot(
                mapping.Identity,
                mapping.StagedRoot))
            .ToArray();
        if (!TryMapPath(
                request.AuthoritativeSourcePath,
                layout.RootMappings,
                out var stagedSourcePath) ||
            !File.Exists(stagedInputPath) ||
            !File.Exists(stagedSourcePath))
        {
            diagnostic = "The staged preprocess entry point or candidate file is unavailable.";
            return false;
        }

        stagedRequest = request.BaselineRequest with
        {
            InputPath = stagedInputPath!,
            DependencyIdentity = WccContentHash.ComputeText(string.Join('\n',
                request.BaselineRequest.DependencyIdentity,
                request.AuthoritativeSourcePath,
                request.ExpectedOriginalTextHash,
                replacementText is null ? "baseline" : "candidate",
                effectiveSourceHash)),
            UserDataDirectory = layout.UserDataRoot,
            UserCacheDirectory = layout.UserCacheRoot,
            DependencyScopes = scopes,
            RoutingFiles = routingFiles,
            RoutingRoots = routingRoots,
            DataDirectory = layout.RuntimeRoot
        };
        return true;
    }

    private static async Task<string> ComputeEnvironmentFingerprintAsync(
        ValidatedOverlayRequest request,
        OverlayLayout layout,
        bool useStagedPaths,
        CancellationToken cancellationToken)
    {
        var canonical = new StringBuilder("wcc-preprocess-shared-environment-v1\n");
        canonical.Append("dependency-identity\0")
            .Append(request.BaselineRequest.DependencyIdentity).Append('\n');
        foreach (var define in request.BaselineRequest.Defines
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            canonical.Append("define\0").Append(define).Append('\n');
        }

        var executableHash = await ReadStableByteHashAsync(
            request.BaselineRequest.ExecutablePath,
            cancellationToken).ConfigureAwait(false);
        canonical.Append("executable\0")
            .Append(Path.GetFullPath(request.BaselineRequest.ExecutablePath)).Append('\0')
            .Append(executableHash).Append('\n');

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in layout.RootMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = useStagedPaths ? mapping.StagedRoot : mapping.AuthoritativeRoot;
            var excludedPath = IsInsideOrEqual(
                request.AuthoritativeSourcePath,
                mapping.AuthoritativeRoot)
                ? useStagedPaths
                    ? MapKnownPath(request.AuthoritativeSourcePath, mapping)
                    : request.AuthoritativeSourcePath
                : null;
            foreach (var path in EnumerateRoutedFiles(root))
            {
                if (excludedPath is not null && PathComparer.Equals(path, excludedPath))
                    continue;
                var logicalPath = mapping.Identity + "/" +
                                  NormalizeRelativePath(Path.GetRelativePath(root, path));
                if (!files.TryAdd(logicalPath, path))
                    throw new IOException($"Duplicate routed environment path '{logicalPath}'.");
            }
        }

        foreach (var routingFile in request.BaselineRequest.RoutingFiles ?? [])
        {
            var mapping = layout.RootMappings.SingleOrDefault(candidate =>
                IsInsideOrEqual(routingFile, candidate.AuthoritativeRoot));
            if (mapping is null)
                throw new IOException($"Routing file '{routingFile}' has no overlay mapping.");
            var selectedPath = useStagedPaths
                ? MapKnownPath(routingFile, mapping)
                : Path.GetFullPath(routingFile);
            var excludedPath = IsInsideOrEqual(
                request.AuthoritativeSourcePath,
                mapping.AuthoritativeRoot)
                ? useStagedPaths
                    ? MapKnownPath(request.AuthoritativeSourcePath, mapping)
                    : request.AuthoritativeSourcePath
                : null;
            if (excludedPath is not null && PathComparer.Equals(selectedPath, excludedPath))
            {
                continue;
            }
            var logicalPath = mapping.Identity + "/" + NormalizeRelativePath(
                Path.GetRelativePath(
                    useStagedPaths ? mapping.StagedRoot : mapping.AuthoritativeRoot,
                    selectedPath));
            files.TryAdd(logicalPath, selectedPath);
        }

        foreach (var file in files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            canonical.Append("file\0").Append(file.Key).Append('\0')
                .Append(await ReadStableByteHashAsync(
                    file.Value,
                    cancellationToken).ConfigureAwait(false))
                .Append('\n');
        }

        return WccContentHash.ComputeText(canonical.ToString());
    }

    private static IEnumerable<string> EnumerateRoutedFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Routing directory '{childDirectory}' became a reparse point.");
                pending.Push(childDirectory);
            }
            foreach (var file in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Routing file '{file}' became a reparse point.");
                if (RoutedExtensions.Contains(Path.GetExtension(file)))
                    yield return Path.GetFullPath(file);
            }
        }
    }

    private static string MapKnownPath(string authoritativePath, RootMapping mapping)
    {
        var relativePath = Path.GetRelativePath(
            mapping.AuthoritativeRoot,
            Path.GetFullPath(authoritativePath));
        var stagedPath = Path.GetFullPath(Path.Combine(mapping.StagedRoot, relativePath));
        if (!IsInsideOrEqual(stagedPath, mapping.StagedRoot))
            throw new IOException($"Path '{authoritativePath}' escaped its overlay mapping.");
        return stagedPath;
    }

    private static async Task<string> ReadStableByteHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var length = info.Length;
        var lastWrite = info.LastWriteTimeUtc;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var hash = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        info.Refresh();
        if (info.Length != length || info.LastWriteTimeUtc != lastWrite)
            throw new IOException($"Routing input '{path}' changed while it was being fingerprinted.");
        return hash;
    }

    private static bool TryRemapOrigins(
        IReadOnlyList<WesnothPreprocessedScenarioOrigin> origins,
        IReadOnlyList<RootMapping> mappings,
        out IReadOnlyList<WesnothPreprocessedScenarioOrigin> remapped,
        out string? diagnostic)
    {
        var results = new List<WesnothPreprocessedScenarioOrigin>(origins.Count);
        foreach (var origin in origins)
        {
            var sourcePaths = new List<string>();
            foreach (var sourcePath in origin.SourcePaths)
            {
                var matchingMappings = mappings
                    .Where(mapping => IsInsideOrEqual(sourcePath, mapping.StagedRoot))
                    .ToArray();
                if (matchingMappings.Length != 1)
                {
                    remapped = [];
                    diagnostic =
                        "Wesnoth returned a staged source-map path outside one authoritative routing mapping.";
                    return false;
                }

                var relativePath = Path.GetRelativePath(
                    matchingMappings[0].StagedRoot,
                    Path.GetFullPath(sourcePath));
                var authoritativePath = Path.GetFullPath(Path.Combine(
                    matchingMappings[0].AuthoritativeRoot,
                    relativePath));
                if (!IsInsideOrEqual(authoritativePath, matchingMappings[0].AuthoritativeRoot))
                {
                    remapped = [];
                    diagnostic = "A staged source-map path escaped its authoritative routing root.";
                    return false;
                }
                if (!sourcePaths.Contains(authoritativePath, PathComparer))
                    sourcePaths.Add(authoritativePath);
            }

            results.Add(new WesnothPreprocessedScenarioOrigin(
                origin.ScenarioId,
                sourcePaths));
        }

        remapped = results;
        diagnostic = null;
        return true;
    }

    private static IReadOnlyList<WesnothPreprocessedScalarOwner>? RemapScalarOwners(
        IReadOnlyList<WesnothPreprocessedScalarOwner>? owners,
        IReadOnlyList<RootMapping> mappings)
    {
        if (owners is null)
            return null;
        var results = new List<WesnothPreprocessedScalarOwner>(owners.Count);
        foreach (var owner in owners)
        {
            WesnothPreprocessedSourceOpening? remappedOpening = null;
            if (owner.Opening is { } opening)
            {
                var matchingMappings = mappings
                    .Where(mapping => IsInsideOrEqual(opening.SourcePath, mapping.StagedRoot))
                    .ToArray();
                if (matchingMappings.Length != 1)
                    return null;
                var relativePath = Path.GetRelativePath(
                    matchingMappings[0].StagedRoot, Path.GetFullPath(opening.SourcePath));
                var authoritativePath = Path.GetFullPath(Path.Combine(
                    matchingMappings[0].AuthoritativeRoot, relativePath));
                if (!IsInsideOrEqual(authoritativePath, matchingMappings[0].AuthoritativeRoot))
                    return null;
                remappedOpening = opening with { SourcePath = authoritativePath };
            }
            var children = RemapScalarOwners(owner.Children, mappings);
            if (children is null)
                return null;
            results.Add(owner with { Opening = remappedOpening, Children = children });
        }
        return results.AsReadOnly();
    }

    private static bool TryMapPath(
        string path,
        IReadOnlyList<RootMapping> mappings,
        out string? stagedPath)
    {
        stagedPath = null;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var matches = mappings
            .Where(mapping => IsInsideOrEqual(fullPath, mapping.AuthoritativeRoot))
            .ToArray();
        if (matches.Length != 1)
            return false;

        var relativePath = Path.GetRelativePath(matches[0].AuthoritativeRoot, fullPath);
        var mapped = Path.GetFullPath(Path.Combine(matches[0].StagedRoot, relativePath));
        if (!IsInsideOrEqual(mapped, matches[0].StagedRoot))
            return false;
        stagedPath = mapped;
        return true;
    }

    private static async Task<StableTextHash> ReadStableTextHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var length = info.Length;
        var lastWrite = info.LastWriteTimeUtc;
        var text = await File.ReadAllTextAsync(
            path,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        info.Refresh();
        return new StableTextHash(
            info.Length == length && info.LastWriteTimeUtc == lastWrite,
            WccContentHash.ComputeText(text));
    }

    private static bool IsInsideOrEqual(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return string.Equals(relative, ".", PathComparison) ||
               (!Path.IsPathRooted(relative) &&
                !string.Equals(relative, "..", PathComparison) &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static WesnothPreprocessResult InvalidOverlay(string diagnostic) => new(
        WesnothPreprocessStatus.InvalidRequest,
        Diagnostic: diagnostic);

    private static WesnothPreprocessOverlayEvaluation FailedEvaluation(
        WesnothPreprocessResult failure) => new(
        BaselineResult: null,
        CandidateResult: null,
        SharedEnvironmentFingerprint: null,
        FailureResult: failure);

    private static WesnothPreprocessResult OverlayRejected(
        string diagnostic,
        string? cacheKey = null) => new(
        WesnothPreprocessStatus.DependencyUnverified,
        Diagnostic: diagnostic,
        CacheKey: cacheKey);

    private static bool IsIoFailure(Exception ex) => ex is
        IOException or
        UnauthorizedAccessException or
        ArgumentException or
        NotSupportedException or
        InvalidOperationException;

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A unique candidate temporary file is cleaned on a best-effort basis.
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
            // Every overlay owns a unique directory; cleanup remains best effort.
        }
    }

    private sealed record ValidatedOverlayRequest(
        WesnothPreprocessRequest BaselineRequest,
        string AuthoritativeSourcePath,
        string ExpectedOriginalTextHash,
        string CandidateFileText,
        string DataRoot,
        string? AddonsRoot,
        ManifestMatch ManifestMatch);

    private sealed record ManifestMatch(
        string ScopeIdentity,
        string ScopeRoot,
        string RelativePath,
        string ContentHash);

    private sealed record OverlayLayout(
        string RuntimeRoot,
        string UserDataRoot,
        string UserCacheRoot,
        IReadOnlyList<RootMapping> RootMappings);

    private sealed record RootMapping(
        string Identity,
        string AuthoritativeRoot,
        string StagedRoot);

    private sealed record StableTextHash(bool IsStable, string ContentHash);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
