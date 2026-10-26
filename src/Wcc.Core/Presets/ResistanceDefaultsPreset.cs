namespace Wcc.Core.Presets;

public sealed class ResistanceDefaultsPreset
{
    public required int OrdinalInFile { get; init; }
    public required IReadOnlyList<string> DirectChildTagNames { get; init; }
    public required string RawText { get; init; }
}