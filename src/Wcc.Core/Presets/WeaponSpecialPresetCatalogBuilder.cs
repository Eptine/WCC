using Wcc.Core.Macros;

namespace Wcc.Core.Presets;

public static class WeaponSpecialPresetCatalogBuilder
{
    public static void RebuildFromMacroRegistry(WeaponSpecialPresetCatalog catalog, WccMacroRegistry registry)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        catalog.Clear();

        foreach (var macro in WccMacroCatalogBuilder.Build(registry)
                     .Where(x => x.PurposeKind == WccMacroPurposeKind.WeaponSpecial && x.IsPickerEligible)
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var preferred = WccMacroCatalogDisplaySelection.SelectDefinition(macro);
            if (preferred is null)
                continue;

            catalog.Entries.Add(new WeaponSpecialPresetCatalogEntry
            {
                Name = macro.Name,
                ScopeSummary = macro.ScopeSummary,
                PreferredDefinitionScopeLabel = macro.PreferredDefinitionScopeLabel,
                PreferredDefinitionRawText = preferred.Value.Occurrence.RawText,
                PurposeConfidence = macro.PurposeConfidence,
                PurposeEvidenceSummary = macro.PurposeEvidenceSummary,
                SourceFileName = preferred.Value.Occurrence.SourceFileName,
                SourcePath = preferred.Value.Occurrence.SourceFilePath,
                Origin = WccMacroCatalogDisplaySelection.ToOrigin(preferred.Value.Entry.ScopeKind)
            });
        }
    }
}