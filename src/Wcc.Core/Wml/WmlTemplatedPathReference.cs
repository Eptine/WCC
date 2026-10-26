namespace Wcc.Core.Wml;

public sealed record WmlTemplatedPathReference(
    string RawText,
    string MacroName,
    string MacroRawInvocation,
    string Suffix
);