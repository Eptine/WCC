namespace Wcc.Core.Presets;

public sealed class SchedulePresetCatalog
{
    public List<SchedulePresetCatalogEntry> Entries { get; } = new();

    public void Clear() => Entries.Clear();
}