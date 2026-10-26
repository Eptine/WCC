namespace Wcc.Core.Presets;

public sealed class TraitCatalog
{
    public List<TraitCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}