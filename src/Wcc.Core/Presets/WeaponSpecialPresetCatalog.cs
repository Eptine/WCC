namespace Wcc.Core.Presets;

public sealed class WeaponSpecialPresetCatalog
{
    public List<WeaponSpecialPresetCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}