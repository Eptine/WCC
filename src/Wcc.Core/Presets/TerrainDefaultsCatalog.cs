namespace Wcc.Core.Presets;

public sealed class TerrainDefaultsCatalog
{
    public List<TerrainDefaultsCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}