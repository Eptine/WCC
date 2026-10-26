namespace Wcc.Core.Wml;

public sealed record WmlMacroInvocationOccurrence(
    string Name,
    string RawInvocation,
    int LineIndex,
    string RawLine,
    int StartOffset = -1,
    int EndOffsetExclusive = -1,
    int ColumnIndex = -1,
    int NestingDepth = 0,
    bool IsInsideMacroDefinition = false
);
