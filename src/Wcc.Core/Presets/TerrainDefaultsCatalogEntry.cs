using Wcc.Core.Projects;

namespace Wcc.Core.Presets;

public sealed class TerrainDefaultsCatalogEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("terraindef");

    public required int OrdinalInFile { get; init; }
    public required IReadOnlyList<string> DirectChildTagNames { get; init; }
    public required string RawText { get; init; }
    public required string SourceFileName { get; init; }
    public string? SourcePath { get; init; }
    public WccSourceOrigin Origin { get; init; }
}