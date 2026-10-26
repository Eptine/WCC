using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.Presets;

namespace Wcc.App.ViewModels;

public sealed class TraitPresetManagerAdapter : IPresetManagerAdapter
{
    public string ManagerTitle => "Manage Traits";
    public string LibraryTitle => "Trait Library";
    public string SearchWatermark => "Search traits";
    public string ItemKindLabel => "Trait";

    public IReadOnlyList<PresetManagerItemVm> GetEntries(AppShellViewModel shell)
        => shell.TraitCatalog.Entries.Select(entry =>
        {
            var package = shell.GetProjectedCatalogPackageLabel(entry.InternalId, entry.Origin);
            var canEdit = shell.CanEditManagedPreset(entry.InternalId, WccManagedLibraryFileKind.Trait);
            return new PresetManagerItemVm
            {
                InternalId = entry.InternalId,
                ValueId = entry.Id,
                DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                    ? entry.Id
                    : $"{entry.Id} — {entry.DisplayName}",
                SecondaryText = $"{package} — {entry.SourceFileName}",
                Description = null,
                RawText = entry.RawText,
                OriginLabel = package,
                SourceFileName = entry.SourceFileName,
                CanEdit = canEdit,
                CanDelete = canEdit
            };
        }).ToList();

    public string BuildNewTemplate(AppShellViewModel shell)
        => ReusableWmlTemplateHelper.CreateTrait(
            shell.CreateUniqueTraitIdSuggestion("custom_trait"));

    public string? BuildDuplicateTemplate(AppShellViewModel shell, PresetManagerItemVm selected)
        => ReusableWmlTemplateHelper.ReplaceTagId(
            selected.RawText,
            "trait",
            shell.CreateUniqueTraitIdSuggestion(selected.ValueId));

    public void SaveNew(AppShellViewModel shell, string rawText)
        => shell.UpsertWccTraitFromRawText(rawText);

    public void SaveExisting(AppShellViewModel shell, PresetManagerItemVm selected, string rawText)
        => shell.UpsertWccTraitFromRawText(rawText, selected.InternalId);

    public void Delete(AppShellViewModel shell, PresetManagerItemVm selected)
        => shell.DeleteWccTraitByInternalId(selected.InternalId);
}
