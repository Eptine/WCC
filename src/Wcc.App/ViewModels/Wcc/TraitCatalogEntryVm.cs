using Wcc.Core.Presets;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels.Wcc;

public sealed class TraitCatalogEntryVm : ViewModelBase
{
    private readonly WccCatalogItem? _libraryItem;
    private string _rawText;

    public TraitCatalogEntryVm(TraitCatalogEntry entry, WccCatalogItem? libraryItem = null)
    {
        Entry = entry;
        _libraryItem = libraryItem;
        _rawText = entry.RawText;
    }

    public TraitCatalogEntry Entry { get; }
    public WccEntityId? LibraryEntityId => _libraryItem?.EntityId;
    public string SelectionKey => LibraryEntityId is { } id
        ? "library:" + id.Value
        : "projection:" + Entry.InternalId;
    public string RawText => _rawText;

    public void SetRawText(string rawText)
        => SetField(ref _rawText, rawText, nameof(RawText));

    public string Id => Entry.Id;
    public string DisplayNameText => string.IsNullOrWhiteSpace(Entry.DisplayName) ? Entry.Id : $"{Entry.Id} — {Entry.DisplayName}";
    public string? DisplayName => Entry.DisplayName;
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
