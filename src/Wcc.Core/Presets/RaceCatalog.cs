namespace Wcc.Core.Presets;

public sealed class RaceCatalog
{
    public List<RaceCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}