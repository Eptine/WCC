namespace Wcc.Core.Wml;

public sealed record WmlMacroDefinitionFingerprint(
    IReadOnlyList<string> DirectTagNames,
    IReadOnlyList<string> DirectInvokedMacroNames
);