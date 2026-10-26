namespace Wcc.Core.Macros;

public sealed record WccMacroDefinitionOccurrence(
    string Name,
    string? SourceFilePath,
    string SourceFileName,
    string RawText,
    int StartLineIndex,
    int EndLineIndexExclusive,
    IReadOnlyList<string> DirectTagNames,
    IReadOnlyList<string> DirectInvokedMacroNames)
{
    public WccMacroDefinitionOccurrence(
        string name,
        string? sourceFilePath,
        string sourceFileName,
        string rawText,
        int startLineIndex,
        int endLineIndexExclusive)
        : this(
            name,
            sourceFilePath,
            sourceFileName,
            rawText,
            startLineIndex,
            endLineIndexExclusive,
            Array.Empty<string>(),
            Array.Empty<string>())
    {
    }
}