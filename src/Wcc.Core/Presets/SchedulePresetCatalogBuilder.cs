using Wcc.Core.Macros;
using Wcc.Core.Wml;

namespace Wcc.Core.Presets;

public static class SchedulePresetCatalogBuilder
{
    public static void RebuildFromMacroRegistry(SchedulePresetCatalog catalog, WccMacroRegistry registry)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        catalog.Clear();

        var groupedRegistry = registry.Entries
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.ScopeLabel, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var groupedCatalog = WccMacroCatalogBuilder.Build(registry);
        var slotMemo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var macro in groupedCatalog
                     .Where(x => x.PurposeKind == WccMacroPurposeKind.Schedule)
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!groupedRegistry.TryGetValue(macro.Name, out var scopedEntries))
                continue;

            var preferred = WccMacroCatalogDisplaySelection.SelectEntry(scopedEntries);
            if (preferred is null)
                continue;

            var preferredDefinition = WccMacroCatalogDisplaySelection.SelectOccurrence(preferred);
            if (preferredDefinition is null)
                continue;

            var slotCount = CountReachableTimeSlots(
                macro.Name,
                groupedRegistry,
                slotMemo,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            catalog.Entries.Add(new SchedulePresetCatalogEntry
            {
                Name = macro.Name,
                ScopeSummary = macro.ScopeSummary,
                PreferredDefinitionScopeLabel = macro.PreferredDefinitionScopeLabel,
                TimeSlotCount = Math.Max(1, slotCount),
                PreferredDefinitionRawText = preferredDefinition.RawText,
                PurposeConfidence = macro.PurposeConfidence,
                PurposeEvidenceSummary = macro.PurposeEvidenceSummary
            });
        }
    }

    private static int CountReachableTimeSlots(
        string macroName,
        IReadOnlyDictionary<string, List<WccMacroRegistryEntry>> groupedRegistry,
        Dictionary<string, int> memo,
        HashSet<string> visiting)
    {
        if (memo.TryGetValue(macroName, out var cached))
            return cached;

        if (!visiting.Add(macroName))
            return 0;

        try
        {
            if (!groupedRegistry.TryGetValue(macroName, out var entries))
            {
                memo[macroName] = 0;
                return 0;
            }

            int total = 0;

            foreach (var entry in entries)
            {
                foreach (var def in entry.DefinitionOccurrences)
                {
                    total += WmlTopLevelFileBlockScanner.FindBlocks(def.RawText, "time").Count;

                    foreach (var inv in WmlMacroInvocationScanner.Scan(def.RawText))
                    {
                        total += CountReachableTimeSlots(inv.Name, groupedRegistry, memo, visiting);
                    }
                }
            }

            memo[macroName] = total;
            return total;
        }
        finally
        {
            visiting.Remove(macroName);
        }
    }
}
