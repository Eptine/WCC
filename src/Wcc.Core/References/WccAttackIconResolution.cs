namespace Wcc.Core.References;

public sealed class WccAttackIconResolution
{
    public string? ResolvedAbsolutePath { get; init; }
    public string? LookupPath { get; init; }
    public bool IsInferredDefault { get; init; }
    public bool HasReference => !string.IsNullOrWhiteSpace(LookupPath);
    public bool IsResolved => !string.IsNullOrWhiteSpace(ResolvedAbsolutePath);
}
