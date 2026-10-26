namespace Wcc.Core.Macros;

public sealed record WccMacroInvocationUsage(
    string MacroName,
    string RawInvocation,
    string? SourceFilePath,
    string SourceFileName,
    int LineIndex,
    string RawLine
);