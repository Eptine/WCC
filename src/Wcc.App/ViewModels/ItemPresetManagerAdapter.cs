using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.Presets;

namespace Wcc.App.ViewModels;

public sealed class ItemPresetManagerAdapter : IPresetManagerAdapter
{
    public string ManagerTitle => "Manage Items";
    public string LibraryTitle => "Item Template Library";
    public string SearchWatermark => "Search items";
    public string ItemKindLabel => "Item";

    public IReadOnlyList<PresetManagerItemVm> GetEntries(AppShellViewModel shell)
        => shell.ItemCatalog.Entries.Select(entry =>
        {
            var package = shell.GetProjectedCatalogPackageLabel(entry.InternalId, entry.Origin);
            var canEdit = shell.CanEditManagedPreset(entry.InternalId, WccManagedLibraryFileKind.Item);
            var value = entry.Name ?? entry.Image ?? entry.Halo ?? "item";
            var position = string.IsNullOrWhiteSpace(entry.X) && string.IsNullOrWhiteSpace(entry.Y)
                ? null
                : $"Position: {entry.X ?? "?"},{entry.Y ?? "?"}";
            return new PresetManagerItemVm
            {
                InternalId = entry.InternalId,
                ValueId = value,
                DisplayName = value,
                SecondaryText = $"{package} — {entry.SourceFileName}",
                Description = position,
                RawText = entry.RawText,
                OriginLabel = package,
                SourceFileName = entry.SourceFileName,
                CanEdit = canEdit,
                CanDelete = canEdit
            };
        }).ToList();

    public string BuildNewTemplate(AppShellViewModel shell)
    {
        var name = shell.CreateUniqueItemNameSuggestion("custom_item");
        return ReusableWmlTemplateHelper.CreateItem(name);
    }

    public string? BuildDuplicateTemplate(AppShellViewModel shell, PresetManagerItemVm selected)
        => ReusableWmlTemplateHelper.ReplaceItemName(
            selected.RawText,
            shell.CreateUniqueItemNameSuggestion(selected.ValueId));

    public void SaveNew(AppShellViewModel shell, string rawText)
        => shell.UpsertWccItemFromRawText(rawText);

    public void SaveExisting(AppShellViewModel shell, PresetManagerItemVm selected, string rawText)
        => shell.UpsertWccItemFromRawText(rawText, selected.InternalId);

    public void Delete(AppShellViewModel shell, PresetManagerItemVm selected)
        => shell.DeleteWccItemByInternalId(selected.InternalId);
}
