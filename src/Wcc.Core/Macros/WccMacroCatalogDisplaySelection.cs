using Wcc.Core.Projects;

namespace Wcc.Core.Macros;

/// <summary>
/// Shared presentation preference for catalog labels and preset source previews.
/// This ordering does not establish active definitions, include order, ambiguity
/// resolution, or authority to edit a macro; those require captured source evidence.
/// </summary>
internal static class WccMacroCatalogDisplaySelection
{
    public static WccMacroRegistryEntry? SelectEntry(IEnumerable<WccMacroRegistryEntry> entries)
        => entries
            .Where(entry => entry.DefinitionCount > 0)
            .OrderBy(entry => GetScopePriority(entry.ScopeKind))
            .ThenByDescending(entry => entry.DefinitionCount)
            .ThenBy(entry => entry.ScopeLabel, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    public static WccMacroDefinitionOccurrence? SelectOccurrence(WccMacroRegistryEntry entry)
        => entry.DefinitionOccurrences
            .OrderBy(occurrence => occurrence.SourceFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(occurrence => occurrence.StartLineIndex)
            .FirstOrDefault();

    public static (WccMacroRegistryEntry Entry, WccMacroDefinitionOccurrence Occurrence)? SelectDefinition(
        WccMacroCatalogEntry macro)
    {
        var entry = SelectEntry(macro.ScopedEntries);
        if (entry is null)
            return null;

        var occurrence = SelectOccurrence(entry);
        return occurrence is null ? null : (entry, occurrence);
    }

    public static WccSourceOrigin ToOrigin(WccMacroScopeKind kind)
        => kind switch
        {
            WccMacroScopeKind.CoreBase => WccSourceOrigin.CoreInstall,
            WccMacroScopeKind.CoreCampaign => WccSourceOrigin.CoreInstall,
            WccMacroScopeKind.WccGenerated => WccSourceOrigin.WccGenerated,
            _ => WccSourceOrigin.ImportedExternal
        };

    private static int GetScopePriority(WccMacroScopeKind kind)
        => kind switch
        {
            WccMacroScopeKind.CoreBase => 0,
            WccMacroScopeKind.WccGenerated => 1,
            WccMacroScopeKind.ImportedAddOn => 2,
            WccMacroScopeKind.CoreCampaign => 3,
            WccMacroScopeKind.ImportedExternal => 4,
            _ => 5
        };
}
