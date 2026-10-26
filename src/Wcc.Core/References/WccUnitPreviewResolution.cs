namespace Wcc.Core.References;

public sealed class WccUnitPreviewResolution
{
    public string? ResolvedAbsolutePath { get; init; }
    public string? RawExpression { get; init; }
    public string? SourceLabel { get; init; }
    public bool HasReference { get; init; }
    public bool IsSuppressed { get; init; }
    public bool IsDynamicReference { get; init; }
    public string? UnresolvedMacroName { get; init; }
    public string? UnavailableReason { get; init; }

    public bool IsResolved => !string.IsNullOrWhiteSpace(ResolvedAbsolutePath);
}
