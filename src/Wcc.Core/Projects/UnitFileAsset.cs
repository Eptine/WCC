using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

public sealed class UnitFileAsset
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("unitfile");

    public required string FileName { get; init; }

    /// <summary>
    /// Optional original source path of the imported file.
    /// Useful for provenance and manual refresh; not required for WCC to keep working.
    /// </summary>
    public string? SourcePath { get; init; }

    public WccSourceOrigin Origin { get; init; } = WccSourceOrigin.ImportedExternal;

    /// <summary>
    /// WCC-owned raw text snapshot used for internal persistence.
    /// This lets the catalog survive outside file changes/deletions.
    /// </summary>
    public string? RawTextSnapshot { get; set; }

    public List<UnitTypeDocument> Documents { get; } = new();
    public List<WmlMacroDefinition> MacroDefinitions { get; } = new();
}