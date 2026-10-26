namespace Wcc.Core.Presets;

public sealed class MovementTypeCatalog
{
    public List<MovementTypeCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}