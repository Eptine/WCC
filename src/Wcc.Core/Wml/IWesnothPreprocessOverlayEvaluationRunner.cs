namespace Wcc.Core.Wml;

/// <summary>
/// Runs one baseline/candidate pair inside a shared verified overlay. This
/// seam keeps scenario transaction orchestration testable without weakening
/// the concrete overlay's filesystem and dependency checks.
/// </summary>
public interface IWesnothPreprocessOverlayEvaluationRunner
{
    Task<WesnothPreprocessOverlayEvaluation> EvaluateAsync(
        WesnothPreprocessOverlayRequest request,
        CancellationToken cancellationToken = default);
}
