using Wcc.Core.Presets;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels.Wcc;

public sealed class ItemCatalogEntryVm : ViewModelBase
{
    private readonly WccCatalogItem? _libraryItem;
    private string _rawText;

    public ItemCatalogEntryVm(ItemCatalogEntry entry, WccCatalogItem? libraryItem = null)
    {
        Entry = entry;
        _libraryItem = libraryItem;
        _rawText = entry.RawText;
    }

    public ItemCatalogEntry Entry { get; }
    public WccEntityId? LibraryEntityId => _libraryItem?.EntityId;
    public string SelectionKey => LibraryEntityId is { } id
        ? "library:" + id.Value
        : "projection:" + Entry.InternalId;
    public string RawText => _rawText;

    public void SetRawText(string rawText)
        => SetField(ref _rawText, rawText, nameof(RawText));

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Entry.Name)
            ? Entry.Name!
            : !string.IsNullOrWhiteSpace(Entry.Image)
                ? Entry.Image!
                : !string.IsNullOrWhiteSpace(Entry.Halo)
                    ? Entry.Halo!
                    : "item";

    public string PositionText =>
        !string.IsNullOrWhiteSpace(Entry.X) || !string.IsNullOrWhiteSpace(Entry.Y)
            ? $"{Entry.X ?? "?"},{Entry.Y ?? "?"}"
            : "";
    public string CatalogDisplayName => $"{DisplayName} [{OriginLabel}]";

    public string? Name => Entry.Name;
    public string? X => Entry.X;
    public string? Y => Entry.Y;
    public string? Image => Entry.Image;
    public string? Halo => Entry.Halo;
    public string? TeamName => Entry.TeamName;
    public string? VisibleInFog => Entry.VisibleInFog;
    public string? Submerge => Entry.Submerge;
    public string? ZOrder => Entry.ZOrder;
    public string? SourcePath => Entry.SourcePath;
    public string SourceFileName => Entry.SourceFileName;

    public string OriginLabel => _libraryItem?.PackageDisplayName ?? Entry.Origin switch
    {
        WccSourceOrigin.CoreInstall => "core",
        WccSourceOrigin.WccGenerated => "wcc",
        _ => "imported"
    };
}
