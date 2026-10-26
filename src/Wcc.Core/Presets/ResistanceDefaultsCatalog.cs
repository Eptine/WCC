namespace Wcc.Core.Presets;

public sealed class ResistanceDefaultsCatalog
{
    public List<ResistanceDefaultsCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}