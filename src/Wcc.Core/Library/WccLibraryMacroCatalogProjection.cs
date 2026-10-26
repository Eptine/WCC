using Wcc.Core.Macros;

namespace Wcc.Core.Library;

/// <summary>
/// Adapts lightweight package entities to the macro-purpose classifier. Raw definition
/// bodies remain on disk and are loaded separately when an editor or inspector needs them.
/// </summary>
public static class WccLibraryMacroCatalogProjection
{
    public static IReadOnlyList<WccMacroCatalogEntry> Build(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds = null)
        => WccMacroCatalogBuilder.Build(BuildRegistry(snapshot, visibleRevisionIds));

    public static WccMacroRegistry BuildRegistry(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        var registry = new WccMacroRegistry();

        foreach (var entity in snapshot.FindEntities(WccEntityTypes.MacroDefinition))
        {
            if (string.IsNullOrWhiteSpace(entity.ExportSymbol) ||
                !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile))
            {
                continue;
            }
            if (visibleRevisionIds is not null &&
                !visibleRevisionIds.Contains(sourceFile.PackageRevisionId))
            {
                continue;
            }

            snapshot.RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out var revision);
            WccPackageRecord? package = null;
            if (revision is not null)
                snapshot.PackagesById.TryGetValue(revision.PackageId, out package);
            var scope = CreateScope(package);
            var registryEntry = registry.GetOrCreate(entity.ExportSymbol, scope);
            registryEntry.DefinitionOccurrences.Add(new WccMacroDefinitionOccurrence(
                entity.ExportSymbol,
                sourceFile.StoragePath,
                Path.GetFileName(sourceFile.RelativePath),
                RawText: string.Empty,
                entity.SourceSpan?.StartLine ?? 0,
                entity.SourceSpan?.EndLineExclusive ?? entity.SourceSpan?.StartLine ?? 0,
                ParseSummaryList(entity.Summary, "direct_tags"),
                ParseSummaryList(entity.Summary, "direct_macro_invocations")));
        }

        return registry;
    }

    private static WccMacroScopeInfo CreateScope(WccPackageRecord? package)
    {
        if (package is null)
            return new WccMacroScopeInfo(WccMacroScopeKind.Unknown, null);

        if (package.SourceKind == WccPackageSourceKinds.WccManagedLibrary)
            return new WccMacroScopeInfo(WccMacroScopeKind.WccGenerated, package.Name);

        if (package.SourceKind == WccPackageSourceKinds.WccProject)
            return new WccMacroScopeInfo(WccMacroScopeKind.WccGenerated, package.Name);

        if (package.SourceKind == WccPackageSourceKinds.WesnothMainline)
        {
            return package.ContentKind == WccPackageContentKinds.Campaign
                ? new WccMacroScopeInfo(WccMacroScopeKind.CoreCampaign, package.Name)
                : new WccMacroScopeInfo(WccMacroScopeKind.CoreBase, null);
        }

        if (package.SourceKind == WccPackageSourceKinds.WesnothAddon)
            return new WccMacroScopeInfo(WccMacroScopeKind.ImportedAddOn, package.Name);

        return new WccMacroScopeInfo(WccMacroScopeKind.ImportedExternal, package.Name);
    }

    private static IReadOnlyList<string> ParseSummaryList(
        IReadOnlyDictionary<string, string> summary,
        string key)
    {
        if (!summary.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
