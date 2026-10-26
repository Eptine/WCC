using System;
using System.Collections.Generic;
using System.IO;
using Wcc.Core.Library;
using Wcc.Core.Macros;
using Wcc.Core.Presets;

namespace Wcc.App.ViewModels;

public class MacroPresetManagerAdapter : IPresetManagerAdapter, IPresetManagerManagedMacroCopyAdapter
{
    public virtual string ManagerTitle => "Manage Macros";
    public virtual string LibraryTitle => "Macro Library";
    public virtual string SearchWatermark => "Search macros";
    public virtual string ItemKindLabel => "Macro";
    protected virtual WccMacroPurposeKind? RequiredPurpose => null;
    protected virtual WccManagedLibraryFileKind ManagedKind => WccManagedLibraryFileKind.Macro;
    public WccManagedLibraryFileKind ManagedMacroCopyKind => ManagedKind;
    protected virtual string DefaultName => "WCC_CUSTOM_MACRO";

    public IReadOnlyList<PresetManagerItemVm> GetEntries(AppShellViewModel shell)
    {
        if (shell.LibrarySnapshot is not { } snapshot)
            return Array.Empty<PresetManagerItemVm>();

        var result = new List<PresetManagerItemVm>();
        foreach (var entity in snapshot.FindEntities(WccEntityTypes.MacroDefinition))
        {
            if (!shell.IsVisibleInActiveLibrary(entity))
                continue;

            var name = entity.ExportSymbol ?? entity.DisplayName ?? "(unnamed)";
            var catalogEntry = shell.FindMacroCatalogEntry(name);
            if (RequiredPurpose is { } purpose && catalogEntry?.PurposeKind != purpose)
                continue;
            if (!snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile))
                continue;

            var package = snapshot.FindPackageForEntity(entity.Id)?.Name ?? "(unknown package)";
            var canEdit = shell.CanEditManagedPreset(entity.Id, ManagedKind);
            var purposeText = catalogEntry is null
                ? null
                : $"{catalogEntry.PurposeLabel}, {catalogEntry.PurposeConfidenceLabel} confidence. {catalogEntry.PurposeEvidenceSummary}";

            result.Add(new PresetManagerItemVm
            {
                InternalId = entity.Id.ToString(),
                LibraryEntityId = entity.Id,
                ValueId = name,
                DisplayName = name,
                SecondaryText = $"{package} — {Path.GetFileName(sourceFile.RelativePath)}",
                Description = purposeText,
                RawText = string.Empty,
                OriginLabel = package,
                SourceFileName = Path.GetFileName(sourceFile.RelativePath),
                CanEdit = canEdit,
                CanDelete = canEdit
            });
        }

        return result;
    }

    public virtual string BuildNewTemplate(AppShellViewModel shell)
        => ReusableWmlTemplateHelper.CreateMacro(
            shell.CreateUniqueMacroNameSuggestion(DefaultName));

    public string? BuildDuplicateTemplate(AppShellViewModel shell, PresetManagerItemVm selected)
        => AbilityPresetWmlTemplateHelper.ReplaceMacroName(
            selected.RawText,
            shell.CreateUniqueMacroNameSuggestion(selected.ValueId));

    public void SaveNew(AppShellViewModel shell, string rawText)
        => shell.UpsertWccMacroFromRawText(rawText, newKind: ManagedKind);

    public void SaveExisting(AppShellViewModel shell, PresetManagerItemVm selected, string rawText)
    {
        if (selected.LibraryEntityId is { } entityId)
            shell.UpsertWccMacroFromRawText(rawText, entityId, ManagedKind);
    }

    public void Delete(AppShellViewModel shell, PresetManagerItemVm selected)
    {
        if (selected.LibraryEntityId is { } entityId)
            shell.DeleteWccMacroByEntityId(entityId, ManagedKind);
    }
}
