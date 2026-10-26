namespace Wcc.Core.Presets;

public sealed class TerrainTypeCatalog
{
    public List<TerrainTypeCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}