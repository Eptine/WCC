namespace Wcc.Core.References;

public sealed class WccAssetResolutionResult
{
    public List<string> CandidatePaths { get; } = new();

    public string? ResolvedAbsolutePath { get; init; }

    public bool IsResolved => !string.IsNullOrWhiteSpace(ResolvedAbsolutePath);
}