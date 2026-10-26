namespace Wcc.Core.Presets;

public sealed class RacePreset
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? MaleName { get; init; }
    public string? FemaleName { get; init; }
    public string? PluralName { get; init; }
    public string? Description { get; init; }
    public int? NumTraits { get; init; }
    public string? IgnoreGlobalTraits { get; init; }
    public required string RawText { get; init; }
}