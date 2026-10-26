namespace Wcc.Core.Presets;

public sealed class ItemCatalog
{
    public List<ItemCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}