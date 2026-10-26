using Wcc.Core.Projects;

namespace Wcc.Core.Presets;

public sealed class TraitCatalogEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("trait");

    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public required string RawText { get; init; }
    public required string SourceFileName { get; init; }
    public string? SourcePath { get; init; }
    public WccSourceOrigin Origin { get; init; }
}