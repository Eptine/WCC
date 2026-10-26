namespace Wcc.Core.Presets;

public sealed class TerrainTypePreset
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? EditorName { get; init; }
    public string? StringCode { get; init; }
    public string? EditorGroup { get; init; }
    public string? SymbolImage { get; init; }
    public string? EditorImage { get; init; }
    public string? DefaultBase { get; init; }
    public string? Description { get; init; }
    public required string RawText { get; init; }
}
