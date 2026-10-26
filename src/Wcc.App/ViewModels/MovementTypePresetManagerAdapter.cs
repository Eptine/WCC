using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Presets;

namespace Wcc.App.ViewModels;

public sealed class MovementTypePresetManagerAdapter : IPresetManagerAdapter
{
    private readonly MainWindowViewModel? _context;

    public MovementTypePresetManagerAdapter()
    {
    }

    public MovementTypePresetManagerAdapter(MainWindowViewModel? context)
    {
        _context = context;
    }

    public string ManagerTitle => "Manage Movement Types";
    public string LibraryTitle => "Movement Type Library";
    public string SearchWatermark => "Search movement types";
    public string ItemKindLabel => "Movement Type";

    public IReadOnlyList<PresetManagerItemVm> GetEntries(AppShellViewModel shell)
    {
        var standalone = shell.MovementTypeCatalog.Entries
            .Select(entry =>
            {
                var packageLabel = shell.GetProjectedCatalogPackageLabel(entry.InternalId, entry.Origin);
                return (entry.SourcePath, ValueId: entry.Name, Item: new PresetManagerItemVm
                {
                    InternalId = entry.InternalId,
                    ValueId = entry.Name,
                    DisplayName = entry.Name,
                    SecondaryText = $"{packageLabel} — {entry.SourceFileName}",
                    Description = null,
                    RawText = entry.RawText,
                    OriginLabel = packageLabel,
                    SourceFileName = entry.SourceFileName,
                    CanEdit = shell.CanEditManagedPreset(
                        entry.InternalId,
                        global::Wcc.Core.Library.WccManagedLibraryFileKind.MovementType),
                    CanDelete = shell.CanEditManagedPreset(
                        entry.InternalId,
                        global::Wcc.Core.Library.WccManagedLibraryFileKind.MovementType)
                });
            })
            .ToArray();
        return _context is null
            ? standalone.Select(row => row.Item).ToArray()
            : ContextualUnitPresetManagerEntries.Build(shell, _context.GetMovementTypeCatalogOptions(),
                global::Wcc.Core.Library.WccEntityTypes.MovementType, standalone);
    }

    public string BuildNewTemplate(AppShellViewModel shell)
    {
        var name = shell.CreateUniqueMovementTypeNameSuggestion("custom_movement");
        return MovementTypeWmlTemplateHelper.CreateNewMovementTypeTemplate(name);
    }

    public string? BuildDuplicateTemplate(AppShellViewModel shell, PresetManagerItemVm selected)
    {
        var newName = shell.CreateUniqueMovementTypeNameSuggestion(selected.ValueId);
        return MovementTypeWmlTemplateHelper.ReplaceMovementTypeName(selected.RawText, newName);
    }

    public void SaveNew(AppShellViewModel shell, string rawText)
        => shell.UpsertWccMovementTypeFromRawText(rawText);

    public void SaveExisting(AppShellViewModel shell, PresetManagerItemVm selected, string rawText)
        => shell.UpsertWccMovementTypeFromRawText(rawText, selected.InternalId);

    public void Delete(AppShellViewModel shell, PresetManagerItemVm selected)
        => shell.DeleteWccMovementTypeByInternalId(selected.InternalId);
}
