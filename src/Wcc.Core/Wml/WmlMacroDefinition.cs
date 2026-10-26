namespace Wcc.Core.Wml;

public sealed record WmlMacroDefinition(
    string Name,
    int StartLineIndex,
    int EndLineIndexExclusive,
    string RawText
);