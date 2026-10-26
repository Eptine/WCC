namespace Wcc.Core.Wml;

public enum WmlTopLevelFieldKind
{
    None,
    Plain,
    ConditionalOrMultiple
}

public sealed record WmlTopLevelFieldOccurrence(
    int LineIndex,
    string Value,
    bool IsConditional,
    string RawLine
);

public sealed class WmlTopLevelFieldAnalysis
{
    public required string Key { get; init; }
    public required WmlTopLevelFieldKind Kind { get; init; }
    public string? PlainValue { get; init; }
    public required IReadOnlyList<WmlTopLevelFieldOccurrence> Occurrences { get; init; }
}
