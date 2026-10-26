using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Creates evaluators bound to one manifest-verified authoritative source
/// revision. A coordinator can compare its full rendered baseline against this
/// binding before any expensive preprocessing begins.
/// </summary>
public interface IScenarioScalarSemanticEvaluatorFactory
{
    string AuthoritativeSourcePath { get; }
    string ExpectedBaselineFileHash { get; }

    bool IsBaselineManifestSourceRevision(
        string sourcePath,
        string expectedTextHash);

    IScenarioScalarSemanticEvaluator Create(string candidateFileText);
}

/// <summary>
/// Production factory connecting scenario scalar validation to the paired
/// Wesnoth overlay evaluator.
/// </summary>
public sealed class WesnothScenarioScalarSemanticEvaluatorFactory :
    IScenarioScalarSemanticEvaluatorFactory
{
    private readonly IWesnothPreprocessOverlayEvaluationRunner _overlayRunner;
    private readonly WesnothPreprocessRequest _baselineRequest;

    public WesnothScenarioScalarSemanticEvaluatorFactory(
        IWesnothPreprocessOverlayEvaluationRunner overlayRunner,
        WesnothPreprocessRequest baselineRequest,
        string authoritativeSourcePath,
        string expectedBaselineFileHash)
    {
        _overlayRunner = overlayRunner ??
                         throw new ArgumentNullException(nameof(overlayRunner));
        ArgumentNullException.ThrowIfNull(baselineRequest);
        if (string.IsNullOrWhiteSpace(authoritativeSourcePath))
        {
            throw new ArgumentException(
                "An authoritative source path is required.",
                nameof(authoritativeSourcePath));
        }
        if (!IsSha256(expectedBaselineFileHash))
        {
            throw new ArgumentException(
                "A valid baseline SHA-256 text hash is required.",
                nameof(expectedBaselineFileHash));
        }

        AuthoritativeSourcePath = Path.GetFullPath(authoritativeSourcePath);
        ExpectedBaselineFileHash = expectedBaselineFileHash;
        _baselineRequest = Snapshot(baselineRequest);
    }

    public string AuthoritativeSourcePath { get; }
    public string ExpectedBaselineFileHash { get; }

    public bool IsBaselineManifestSourceRevision(
        string sourcePath,
        string expectedTextHash)
    {
        if (!TryFullPath(sourcePath, out var expectedPath) ||
            !IsSha256(expectedTextHash))
        {
            return false;
        }

        foreach (var scope in _baselineRequest.DependencyScopes ?? [])
        {
            if (!TryFullPath(scope.RootPath, out var rootPath))
                continue;
            foreach (var file in scope.ExpectedWmlFiles ?? [])
            {
                if (!TryManifestPath(rootPath, file.RelativePath, out var manifestPath))
                    continue;
                if (PathsEqual(expectedPath, manifestPath) &&
                    string.Equals(
                        expectedTextHash,
                        file.ContentHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public IScenarioScalarSemanticEvaluator Create(string candidateFileText)
    {
        ArgumentNullException.ThrowIfNull(candidateFileText);
        return new WesnothScenarioScalarSemanticEvaluator(
            _overlayRunner,
            _baselineRequest,
            AuthoritativeSourcePath,
            ExpectedBaselineFileHash,
            candidateFileText);
    }

    private static WesnothPreprocessRequest Snapshot(
        WesnothPreprocessRequest request)
    {
        var scopes = (request.DependencyScopes ?? [])
            .Select(scope => new WesnothPreprocessDependencyScope(
                scope.Identity,
                scope.RootPath,
                (scope.ExpectedWmlFiles ?? []).ToArray()))
            .ToArray();
        var roots = (request.RoutingRoots ?? [])
            .Select(root => new WesnothPreprocessRoutingRoot(
                root.Identity,
                root.RootPath))
            .ToArray();
        return request with
        {
            Defines = (request.Defines ?? []).ToArray(),
            DependencyScopes = scopes,
            RoutingFiles = (request.RoutingFiles ?? []).ToArray(),
            RoutingRoots = roots
        };
    }

    private static bool TryManifestPath(
        string rootPath,
        string? relativePath,
        out string manifestPath)
    {
        manifestPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            return false;
        }
        try
        {
            manifestPath = Path.GetFullPath(Path.Combine(
                rootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return IsPathInsideOrEqual(manifestPath, rootPath);
        }
        catch (Exception ex) when (ex is
                                   ArgumentException or
                                   NotSupportedException or
                                   PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryFullPath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is
                                   ArgumentException or
                                   NotSupportedException or
                                   PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, PathComparison);

    private static bool IsPathInsideOrEqual(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return PathsEqual(path, normalizedRoot) ||
               path.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Evaluates both complete files for one declared context through a single
/// private overlay and exposes only the paired normalized outputs, their
/// verified scenario-origin maps, and their shared environment fingerprint.
/// </summary>
public sealed class WesnothScenarioScalarSemanticEvaluator :
    IScenarioScalarSemanticEvaluator
{
    private readonly IWesnothPreprocessOverlayEvaluationRunner _overlayRunner;
    private readonly WesnothPreprocessRequest _baselineRequest;
    private readonly string _authoritativeSourcePath;
    private readonly string _expectedBaselineFileHash;
    private readonly string _candidateFileText;

    internal WesnothScenarioScalarSemanticEvaluator(
        IWesnothPreprocessOverlayEvaluationRunner overlayRunner,
        WesnothPreprocessRequest baselineRequest,
        string authoritativeSourcePath,
        string expectedBaselineFileHash,
        string candidateFileText)
    {
        _overlayRunner = overlayRunner;
        _baselineRequest = baselineRequest;
        _authoritativeSourcePath = authoritativeSourcePath;
        _expectedBaselineFileHash = expectedBaselineFileHash;
        _candidateFileText = candidateFileText;
    }

    public async ValueTask<ScenarioScalarSemanticEvaluationPairResult> EvaluatePairAsync(
        WmlEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var contextRequest = _baselineRequest with
        {
            Defines = context.DefinedSymbols
                .Order(StringComparer.Ordinal)
                .ToArray(),
            DependencyIdentity = WccContentHash.ComputeText(string.Join(
                "\n",
                "wcc-scenario-scalar-semantic-pair-v1",
                _baselineRequest.DependencyIdentity,
                context.SemanticKey))
        };

        WesnothPreprocessOverlayEvaluation? evaluation;
        try
        {
            evaluation = await _overlayRunner.EvaluateAsync(
                    new WesnothPreprocessOverlayRequest(
                        contextRequest,
                        _authoritativeSourcePath,
                        _expectedBaselineFileHash,
                        _candidateFileText),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ScenarioScalarSemanticEvaluationPairResult.Failure(
                $"The paired Wesnoth overlay threw {ex.GetType().Name}: {ex.Message}");
        }

        if (evaluation is null)
        {
            return ScenarioScalarSemanticEvaluationPairResult.Failure(
                "The paired Wesnoth overlay returned no evaluation.");
        }
        if (!evaluation.IsSuccess ||
            evaluation.BaselineResult?.NormalizedWml is not { } baseline ||
            evaluation.BaselineResult.ScenarioOrigins is not { } baselineOrigins ||
            evaluation.CandidateResult?.NormalizedWml is not { } candidate ||
            evaluation.CandidateResult.ScenarioOrigins is not { } candidateOrigins ||
            !IsSha256(evaluation.SharedEnvironmentFingerprint))
        {
            return ScenarioScalarSemanticEvaluationPairResult.Failure(
                BuildFailureDiagnostic(evaluation));
        }

        return ScenarioScalarSemanticEvaluationPairResult.Success(
            baseline,
            baselineOrigins,
            candidate,
            candidateOrigins,
            evaluation.SharedEnvironmentFingerprint!);
    }

    private static string BuildFailureDiagnostic(
        WesnothPreprocessOverlayEvaluation evaluation)
    {
        var result = evaluation.FailureResult ??
                     (evaluation.BaselineResult?.IsSuccess == false
                         ? evaluation.BaselineResult
                         : null) ??
                     (evaluation.CandidateResult?.IsSuccess == false
                         ? evaluation.CandidateResult
                         : null);
        if (!string.IsNullOrWhiteSpace(result?.Diagnostic))
            return result.Diagnostic!.Trim();
        if (result is not null)
            return $"Wesnoth preprocessing returned status '{result.Status}'.";
        if (evaluation.BaselineResult is null)
            return "The paired overlay did not produce a baseline result.";
        if (evaluation.CandidateResult is null)
            return "The paired overlay did not produce a candidate result.";
        if (evaluation.BaselineResult.NormalizedWml is null ||
            evaluation.CandidateResult.NormalizedWml is null)
        {
            return "The paired overlay did not produce both complete normalized outputs.";
        }
        if (evaluation.BaselineResult.ScenarioOrigins is null ||
            evaluation.CandidateResult.ScenarioOrigins is null)
        {
            return "The paired overlay did not produce both scenario-origin maps.";
        }
        return "The paired overlay did not prove one valid shared environment fingerprint.";
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }
}
