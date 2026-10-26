using Wcc.Core.Presets;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels.Wcc;

public sealed class TerrainTypeCatalogEntryVm : ViewModelBase
{
    private readonly WccCatalogItem? _libraryItem;
    private string _rawText;

    public TerrainTypeCatalogEntryVm(TerrainTypeCatalogEntry entry, WccCatalogItem? libraryItem = null)
    {
        Entry = entry;
        _libraryItem = libraryItem;
        _rawText = entry.RawText;
    }

    public TerrainTypeCatalogEntry Entry { get; }
    public WccEntityId? LibraryEntityId => _libraryItem?.EntityId;
    public string SelectionKey => LibraryEntityId is { } id
        ? "library:" + id.Value
        : "projection:" + Entry.InternalId;
    public string RawText => _rawText;

    public void SetRawText(string rawText)
        => SetField(ref _rawText, rawText, nameof(RawText));

    public string Id => Entry.Id;
    public string DisplayNameText =>
        !string.IsNullOrWhiteSpace(Entry.EditorName)
            ? $"{Entry.Id} — {Entry.EditorName}"
            : !string.IsNullOrWhiteSpace(Entry.Name)
                ? $"{Entry.Id} — {Entry.Name}"
                : Entry.Id;

    public string? Name => Entry.Name;
    public string? EditorName => Entry.EditorName;
    public string? StringCode => Entry.StringCode;
    public string? EditorGroup => Entry.EditorGroup;
    public string? SymbolImage => Entry.SymbolImage;
    public string? EditorImage => Entry.EditorImage;
    public string? Description => Entry.Description;
    public string CatalogDisplayName => $"{DisplayNameText} [{OriginLabel}]";
    public string? SourcePath => Entry.SourcePath;
    public string SourceFileName => Entry.SourceFileName;

    public string OriginLabel => _libraryItem?.PackageDisplayName ?? Entry.Origin switch
    {
        WccSourceOrigin.CoreInstall => "core",
        WccSourceOrigin.WccGenerated => "wcc",
        _ => "imported"
    };
}
