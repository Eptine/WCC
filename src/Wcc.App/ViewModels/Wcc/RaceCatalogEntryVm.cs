using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.Presets;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

public sealed class RaceCatalogEntryVm : ViewModelBase
{
    private readonly WccCatalogItem? _libraryItem;
    private readonly bool? _canEditOverride;
    private string _rawText;

    public RaceCatalogEntryVm(
        RaceCatalogEntry entry,
        WccCatalogItem? libraryItem = null,
        bool? canEditOverride = null)
    {
        Entry = entry;
        _libraryItem = libraryItem;
        _canEditOverride = canEditOverride;
        _rawText = entry.RawText;
    }

    public RaceCatalogEntry Entry { get; }
    public WccEntityId? LibraryEntityId => _libraryItem?.EntityId;
    public string SelectionKey => LibraryEntityId is { } id
        ? "library:" + id.Value
        : "projection:" + Entry.InternalId;
    public string RawText => _rawText;

    public void SetRawText(string rawText)
        => SetField(ref _rawText, rawText, nameof(RawText));

    public string Id => Entry.Id;

    public string DisplayNameText
    {
        get
        {
            var label = FirstNonEmpty(
                WmlDisplayStringNormalizer.Normalize(Entry.PluralName),
                WmlDisplayStringNormalizer.Normalize(Entry.Name),
                WmlDisplayStringNormalizer.Normalize(Entry.MaleName),
                WmlDisplayStringNormalizer.Normalize(Entry.FemaleName),
                WmlDisplayStringNormalizer.HumanizeIdentifier(Entry.Id));

            return label is null ? Entry.Id : $"{Entry.Id} — {label}";
        }
    }

    public string? Name => WmlDisplayStringNormalizer.Normalize(Entry.Name);
    public string? MaleName => WmlDisplayStringNormalizer.Normalize(Entry.MaleName);
    public string? FemaleName => WmlDisplayStringNormalizer.Normalize(Entry.FemaleName);
    public string? PluralName => WmlDisplayStringNormalizer.Normalize(Entry.PluralName);
    public string? Description => WmlDisplayStringNormalizer.Normalize(Entry.Description);
    public string CatalogDisplayName => $"{DisplayNameText} [{OriginLabel}]";

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    public int? NumTraits => Entry.NumTraits;
    public string? IgnoreGlobalTraits => Entry.IgnoreGlobalTraits;
    public string? SourcePath => Entry.SourcePath;
    public string SourceFileName => Entry.SourceFileName;
    public bool IsWccOwned => Entry.Origin == WccSourceOrigin.WccGenerated;
    public bool CanEdit => _canEditOverride ?? IsWccOwned;
    public bool CanDelete => CanEdit;
    public bool IsReadOnly => !CanEdit;

    public string ManagerSecondaryText =>
        $"{OriginLabel} — {SourceFileName}";

    public string OriginLabel => _libraryItem?.PackageDisplayName ?? Entry.Origin switch
    {
        WccSourceOrigin.CoreInstall => "core",
        WccSourceOrigin.WccGenerated => "wcc",
        _ => "imported"
    };
}
