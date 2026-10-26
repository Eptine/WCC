namespace Wcc.Core.Presets;

public sealed class AbilityPresetCatalog
{
    public List<AbilityPresetCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}