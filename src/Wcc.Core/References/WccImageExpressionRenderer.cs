using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Wcc.Core.References;

public enum WccImageExpressionRenderStatus
{
    Unmodified,
    CacheHit,
    Rendered,
    InvalidExpression,
    MissingBaseImage,
    MissingExecutable,
    ProcessFailed,
    OutputMissing,
    UnsupportedBinaryPathContext,
    TimedOut,
    IoFailure
}

public sealed record WccImageExpressionRenderResult(
    WccImageExpressionRenderStatus Status,
    string? RenderedPath = null,
    string? Diagnostic = null,
    bool HasNestedReferenceWarning = false)
{
    public IReadOnlyList<WccImageRenderResourcePin> ReviewedResources { get; init; } = [];

    public bool IsSuccess => Status is
        WccImageExpressionRenderStatus.Unmodified or
        WccImageExpressionRenderStatus.CacheHit or
        WccImageExpressionRenderStatus.Rendered;
}

/// <summary>
/// Renders Wesnoth Image Path Functions through the configured game executable.
/// Rendering is globally serialized because starting multiple engine instances
/// is expensive. Results are cached by engine, expression, and base-image bytes.
/// </summary>
public sealed class WccImageExpressionRenderer
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private readonly string _cacheRoot;
    private readonly IWccImageRenderProcessRunner _processRunner;
    private readonly WccWesnothImageRenderContextBuilder _contextBuilder;
    private readonly TimeSpan _timeout;

    public WccImageExpressionRenderer(
        string cacheRoot,
        IWccImageRenderProcessRunner? processRunner = null,
        TimeSpan? timeout = null,
        string? renderContextRoot = null)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
            throw new ArgumentException("An image-render cache root is required.", nameof(cacheRoot));

        _cacheRoot = Path.GetFullPath(cacheRoot);
        _processRunner = processRunner ?? new WccImageRenderProcessRunner();
        _contextBuilder = new WccWesnothImageRenderContextBuilder(
            renderContextRoot ?? Path.Combine(_cacheRoot, "wesnoth-userdata-v1"));
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
    }

    public async Task<WccImageExpressionRenderResult> RenderAsync(
        string? wesnothInstallPath,
        string? rawExpression,
        string? resolvedBaseImagePath,
        CancellationToken cancellationToken = default)
    {
        return await RenderCoreAsync(wesnothInstallPath, rawExpression, resolvedBaseImagePath, cancellationToken, false);
    }

    /// <summary>Runs a fresh engine process even for identity expressions or an existing cache entry.
    /// Successful review images have distinct owned cache paths; ordinary previews keep their cache behavior.</summary>
    public Task<WccImageExpressionRenderResult> RenderForReviewAsync(string? wesnothInstallPath,
        string? rawExpression, string? resolvedBaseImagePath, CancellationToken cancellationToken = default) =>
        RenderCoreAsync(wesnothInstallPath, rawExpression, resolvedBaseImagePath, cancellationToken, true);
    private async Task<WccImageExpressionRenderResult> RenderCoreAsync(string? wesnothInstallPath,
        string? rawExpression, string? resolvedBaseImagePath,
        CancellationToken cancellationToken, bool forceReview)
    {
        if (forceReview) cancellationToken.ThrowIfCancellationRequested();
        var expression = WccImageExpressionParser.TryParse(rawExpression);
        if (expression is null)
        {
            return new WccImageExpressionRenderResult(
                WccImageExpressionRenderStatus.InvalidExpression,
                Diagnostic: "The image expression is empty or invalid.");
        }

        if (string.IsNullOrWhiteSpace(resolvedBaseImagePath) ||
            !File.Exists(resolvedBaseImagePath))
        {
            return new WccImageExpressionRenderResult(
                WccImageExpressionRenderStatus.MissingBaseImage,
                Diagnostic: "The expression's base image is not resolved on disk.",
                HasNestedReferenceWarning: expression.MayContainNestedImageReferences);
        }

        if (!forceReview && !expression.HasModifiers)
        {
            return new WccImageExpressionRenderResult(
                WccImageExpressionRenderStatus.Unmodified,
                Path.GetFullPath(resolvedBaseImagePath),
                HasNestedReferenceWarning: false);
        }

        var executablePath = WccWesnothExecutableLocator.Resolve(wesnothInstallPath);
        if (executablePath is null)
        {
            return new WccImageExpressionRenderResult(
                WccImageExpressionRenderStatus.MissingExecutable,
                Diagnostic: "The configured Wesnoth executable could not be found.",
                HasNestedReferenceWarning: expression.MayContainNestedImageReferences);
        }

        try
        {
            var renderContext = forceReview
                ? _contextBuilder.BuildForReview(executablePath, expression, resolvedBaseImagePath)
                : _contextBuilder.Build(executablePath, expression, resolvedBaseImagePath);

            var cacheKey = ComputeCacheKey(
                executablePath,
                resolvedBaseImagePath,
                renderContext.ImageExpression,
                renderContext.CacheIdentity);
            Directory.CreateDirectory(_cacheRoot);
            var cachedPath = Path.Combine(_cacheRoot, forceReview
                ? "review-" + cacheKey + "-" + Guid.NewGuid().ToString("N") + ".png" : cacheKey + ".png");
            if (!forceReview && IsUsableOutput(cachedPath))
            {
                return new WccImageExpressionRenderResult(
                    WccImageExpressionRenderStatus.CacheHit,
                    cachedPath,
                    HasNestedReferenceWarning: renderContext.HasUnresolvedNestedReferences);
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);
            try
            {
                await RenderGate.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                try
                {
                    // A preceding queued request may have populated the same key.
                    if (!forceReview && IsUsableOutput(cachedPath))
                    {
                        return new WccImageExpressionRenderResult(
                            WccImageExpressionRenderStatus.CacheHit,
                            cachedPath,
                            HasNestedReferenceWarning: renderContext.HasUnresolvedNestedReferences);
                    }

                    var temporaryPath = Path.Combine(
                        _cacheRoot,
                        $".{cacheKey}.{Guid.NewGuid():N}.wcc-render.png");
                    try
                    {
                        if (forceReview) WccWesnothImageRenderContextBuilder.ValidateReviewResources(renderContext.ReviewedResources);
                        var processResult = await _processRunner.RenderAsync(
                            executablePath,
                            renderContext.ImageExpression,
                            temporaryPath,
                            renderContext.UserDataDirectory,
                            timeoutSource.Token).ConfigureAwait(false);
                        if (forceReview) timeoutSource.Token.ThrowIfCancellationRequested();
                        if (forceReview) WccWesnothImageRenderContextBuilder.ValidateReviewResources(renderContext.ReviewedResources);
                        // A missing image may still produce Wesnoth's displayable placeholder
                        // and exit 0/1. That is not the image the user reviewed or requested.
                        // Restrict this refusal to explicit image-open/load diagnostics;
                        // locale/font warnings and the ordinary preview policy are unchanged.
                        if (forceReview && HasImageLoadFailure(processResult))
                        {
                            return new WccImageExpressionRenderResult(
                                WccImageExpressionRenderStatus.ProcessFailed,
                                Diagnostic: BuildFreshReviewDiagnostic(processResult,
                                    "Wesnoth could not open or load a requested image. A placeholder PNG cannot be used for image review or export."),
                                HasNestedReferenceWarning: renderContext.HasUnresolvedNestedReferences);
                        }
                        // Some engine versions return 1 after writing the requested image.
                        // Accept that result only from this request's fresh temporary PNG.
                        if (processResult.ExitCode is not (0 or 1))
                        {
                            return new WccImageExpressionRenderResult(
                                WccImageExpressionRenderStatus.ProcessFailed,
                                Diagnostic: forceReview ? BuildFreshReviewDiagnostic(processResult,
                                    $"Wesnoth image rendering failed with exit code {processResult.ExitCode}.") : BuildProcessDiagnostic(processResult),
                                HasNestedReferenceWarning: renderContext.HasUnresolvedNestedReferences);
                        }

                        if (!IsUsableOutput(temporaryPath))
                        {
                            return new WccImageExpressionRenderResult(
                                processResult.ExitCode == 1
                                    ? WccImageExpressionRenderStatus.ProcessFailed
                                    : WccImageExpressionRenderStatus.OutputMissing,
                                Diagnostic: forceReview ? BuildFreshReviewDiagnostic(processResult,
                                    "Wesnoth did not produce a displayable PNG.") : processResult.ExitCode == 1
                                    ? BuildProcessDiagnostic(processResult)
                                    : "Wesnoth exited successfully but did not produce a displayable PNG.",
                                HasNestedReferenceWarning: renderContext.HasUnresolvedNestedReferences);
                        }

                        File.Move(temporaryPath, cachedPath, overwrite: !forceReview);
                        return new WccImageExpressionRenderResult(
                            WccImageExpressionRenderStatus.Rendered,
                            cachedPath,
                            Diagnostic: forceReview ? BuildFreshReviewDiagnostic(processResult) : processResult.ExitCode == 1
                                ? BuildRenderedDiagnostic(processResult)
                                : null,
                            HasNestedReferenceWarning: renderContext.HasUnresolvedNestedReferences)
                        { ReviewedResources = forceReview ? renderContext.ReviewedResources : [] };
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                }
                finally
                {
                    RenderGate.Release();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new WccImageExpressionRenderResult(
                    WccImageExpressionRenderStatus.TimedOut,
                    Diagnostic: $"Wesnoth image rendering exceeded {_timeout.TotalSeconds:0} seconds.",
                    HasNestedReferenceWarning: expression.MayContainNestedImageReferences);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is
                                       IOException or
                                       UnauthorizedAccessException or
                                       ArgumentException or
                                       NotSupportedException or
                                       CryptographicException or
                                       InvalidOperationException)
        {
            return new WccImageExpressionRenderResult(
                WccImageExpressionRenderStatus.IoFailure,
                Diagnostic: ex.Message,
                HasNestedReferenceWarning: expression.MayContainNestedImageReferences);
        }
    }

    private static string ComputeCacheKey(
        string executablePath,
        string baseImagePath,
        string resolvedExpression,
        string renderContextIdentity)
    {
        var executable = new FileInfo(executablePath);
        using var image = File.OpenRead(baseImagePath);
        var imageHash = Convert.ToHexString(SHA256.HashData(image));
        var identity = string.Join('\n',
            "wcc-image-render-v3",
            Path.GetFullPath(executablePath),
            executable.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            executable.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            resolvedExpression,
            renderContextIdentity,
            imageHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static bool IsUsableOutput(string path)
    {
        // This is a bounded header check, not a full decoder. Callers still decode
        // the published image, and arbitrary text or a truncated header is never cached.
        if (!File.Exists(path))
            return false;

        using var stream = File.OpenRead(path);
        if (stream.Length < 33)
            return false;

        Span<byte> header = stackalloc byte[33];
        stream.ReadExactly(header);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!header[..8].SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 ||
            !header[12..16].SequenceEqual("IHDR"u8))
            return false;

        var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        return width is > 0 and <= int.MaxValue &&
               height is > 0 and <= int.MaxValue;
    }

    private static bool HasImageLoadFailure(WccImageRenderProcessResult result)
    {
        foreach (var output in new[] { result.StandardError, result.StandardOutput })
            foreach (var line in output.Split('\n'))
            {
                const string marker = "error image:";
                var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                var detail = line[(start + marker.Length)..].TrimStart();
                if (detail.StartsWith("could not open image ", StringComparison.OrdinalIgnoreCase) ||
                    detail.StartsWith("could not load image ", StringComparison.OrdinalIgnoreCase) ||
                    detail.StartsWith("failed to load image ", StringComparison.OrdinalIgnoreCase)) return true;
            }
        return false;
    }

    private static string? BuildFreshReviewDiagnostic(WccImageRenderProcessResult result, string? message = null)
    {
        var parts = new List<string>();
        if (message is not null) parts.Add(message);
        else if (result.ExitCode == 1) parts.Add("Wesnoth produced a displayable PNG and exited with code 1.");
        if (!string.IsNullOrWhiteSpace(result.StandardError)) parts.Add("Wesnoth stderr: " + result.StandardError.Trim());
        if (!string.IsNullOrWhiteSpace(result.StandardOutput)) parts.Add("Wesnoth stdout: " + result.StandardOutput.Trim());
        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }
    private static string BuildRenderedDiagnostic(WccImageRenderProcessResult result)
    {
        var detail = ProcessDetail(result);
        const string message = "Wesnoth produced a displayable PNG and exited with code 1.";
        return detail.Length == 0 ? message : $"{message} {detail}";
    }

    private static string BuildProcessDiagnostic(WccImageRenderProcessResult result)
    {
        var detail = ProcessDetail(result);
        return detail.Length == 0
            ? $"Wesnoth image rendering failed with exit code {result.ExitCode}."
            : $"Wesnoth image rendering failed with exit code {result.ExitCode}: {detail}";
    }

    private static string ProcessDetail(WccImageRenderProcessResult result) =>
        (string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError).Trim();
}
