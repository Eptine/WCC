namespace Wcc.Core.Presets;

public sealed class ItemPreset
{
    public string? Name { get; init; }
    public string? X { get; init; }
    public string? Y { get; init; }
    public string? Image { get; init; }
    public string? Halo { get; init; }
    public string? TeamName { get; init; }
    public string? VisibleInFog { get; init; }
    public string? Submerge { get; init; }
    public string? ZOrder { get; init; }
    public required string RawText { get; init; }
}