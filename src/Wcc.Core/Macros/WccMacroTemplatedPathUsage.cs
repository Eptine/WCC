using Wcc.Core.References;

namespace Wcc.Core.Macros;

public sealed record WccMacroTemplatedPathUsage(
    string MacroName,
    WccAssetReferenceKind AssetKind,
    string OwnerBlockKind,
    string SourceFieldKey,
    string RawValue,
    string? LookupPath,
    string? SourceFilePath
);