using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.Presets;

namespace Wcc.App.ViewModels;

public sealed class TerrainTypePresetManagerAdapter : IPresetManagerAdapter
{
    public string ManagerTitle => "Manage Terrain Types";
    public string LibraryTitle => "Terrain Type Library";
    public string SearchWatermark => "Search terrain types";
    public string ItemKindLabel => "Terrain Type";

    public IReadOnlyList<PresetManagerItemVm> GetEntries(AppShellViewModel shell)
        => shell.TerrainTypeCatalog.Entries.Select(entry =>
        {
            var package = shell.GetProjectedCatalogPackageLabel(entry.InternalId, entry.Origin);
            var canEdit = shell.CanEditManagedPreset(entry.InternalId, WccManagedLibraryFileKind.TerrainType);
            var display = entry.EditorName ?? entry.Name;
            return new PresetManagerItemVm
            {
                InternalId = entry.InternalId,
                ValueId = entry.Id,
                DisplayName = string.IsNullOrWhiteSpace(display) ? entry.Id : $"{entry.Id} — {display}",
                SecondaryText = $"{package} — {entry.SourceFileName}",
                Description = entry.Description,
                RawText = entry.RawText,
                OriginLabel = package,
                SourceFileName = entry.SourceFileName,
                CanEdit = canEdit,
                CanDelete = canEdit
            };
        }).ToList();

    public string BuildNewTemplate(AppShellViewModel shell)
        => ReusableWmlTemplateHelper.CreateTerrainType(
            shell.CreateUniqueTerrainTypeIdSuggestion("custom_terrain"));

    public string? BuildDuplicateTemplate(AppShellViewModel shell, PresetManagerItemVm selected)
        => ReusableWmlTemplateHelper.ReplaceTagId(
            selected.RawText,
            "terrain_type",
            shell.CreateUniqueTerrainTypeIdSuggestion(selected.ValueId));

    public void SaveNew(AppShellViewModel shell, string rawText)
        => shell.UpsertWccTerrainTypeFromRawText(rawText);

    public void SaveExisting(AppShellViewModel shell, PresetManagerItemVm selected, string rawText)
        => shell.UpsertWccTerrainTypeFromRawText(rawText, selected.InternalId);

    public void Delete(AppShellViewModel shell, PresetManagerItemVm selected)
        => shell.DeleteWccTerrainTypeByInternalId(selected.InternalId);
}
