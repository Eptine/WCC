using Wcc.Core.Presets;
using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels.Wcc;

public sealed class MovementTypeCatalogEntryVm : ViewModelBase
{
    private readonly WccCatalogItem? _libraryItem;
    private string _rawText;

    public MovementTypeCatalogEntryVm(MovementTypeCatalogEntry entry, WccCatalogItem? libraryItem = null)
    {
        Entry = entry;
        _libraryItem = libraryItem;
        _rawText = entry.RawText;
    }

    public MovementTypeCatalogEntry Entry { get; }
    public WccEntityId? LibraryEntityId => _libraryItem?.EntityId;
    public string SelectionKey => LibraryEntityId is { } id
        ? "library:" + id.Value
        : "projection:" + Entry.InternalId;
    public string RawText => _rawText;

    public void SetRawText(string rawText)
        => SetField(ref _rawText, rawText, nameof(RawText));

    public string Name => Entry.Name;
    public string? SourcePath => Entry.SourcePath;
    public string SourceFileName => Entry.SourceFileName;

    public string OriginLabel => _libraryItem?.PackageDisplayName ?? Entry.Origin switch
    {
        WccSourceOrigin.CoreInstall => "core",
        WccSourceOrigin.WccGenerated => "wcc",
        _ => "imported"
    };

    public string DisplayName => $"{Name} [{OriginLabel}]";
}
