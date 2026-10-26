using Wcc.Core.Projects;

namespace Wcc.Core.Presets;

public sealed class TerrainTypeCatalogEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("terraintype");

    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? EditorName { get; init; }
    public string? StringCode { get; init; }
    public string? EditorGroup { get; init; }
    public string? SymbolImage { get; init; }
    public string? EditorImage { get; init; }
    public string? Description { get; init; }
    public required string RawText { get; init; }
    public required string SourceFileName { get; init; }
    public string? SourcePath { get; init; }
    public WccSourceOrigin Origin { get; init; }
}
