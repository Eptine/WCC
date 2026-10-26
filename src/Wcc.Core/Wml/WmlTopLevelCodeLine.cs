namespace Wcc.Core.Wml;

public sealed record WmlTopLevelCodeLine(
    int LineIndex,
    string RawLine,
    WmlTopLevelCodeKind Kind
);