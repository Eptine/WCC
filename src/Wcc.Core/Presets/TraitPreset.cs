namespace Wcc.Core.Presets;

public sealed class TraitPreset
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public required string RawText { get; init; }
}