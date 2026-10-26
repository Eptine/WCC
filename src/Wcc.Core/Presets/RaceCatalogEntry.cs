using Wcc.Core.Projects;

namespace Wcc.Core.Presets;

public sealed class RaceCatalogEntry
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("race");

    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? MaleName { get; init; }
    public string? FemaleName { get; init; }
    public string? PluralName { get; init; }
    public string? Description { get; init; }
    public int? NumTraits { get; init; }
    public string? IgnoreGlobalTraits { get; init; }
    public required string RawText { get; init; }
    public required string SourceFileName { get; init; }
    public string? SourcePath { get; init; }
    public WccSourceOrigin Origin { get; init; }
}