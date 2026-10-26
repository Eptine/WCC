namespace Wcc.Core.Wml;

public sealed record WmlMacroInvocationInfo(
    string RawText,
    string Name,
    IReadOnlyList<string> Arguments
);