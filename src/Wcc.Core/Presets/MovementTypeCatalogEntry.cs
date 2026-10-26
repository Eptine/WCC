using Wcc.Core.Projects;

namespace Wcc.Core.Presets;

public sealed class MovementTypeCatalogEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("movetype");

    public required string Name { get; init; }
    public required string RawText { get; init; }
    public required string SourceFileName { get; init; }
    public string? SourcePath { get; init; }
    public WccSourceOrigin Origin { get; init; }
}