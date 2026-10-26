using Wcc.Core.Projects;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Library;

public sealed class WccLoadedLibraryUnitEntity
{
    public required UnitFileAsset SourceFile { get; init; }
    public string? PreferredUnitDocumentInternalId { get; init; }
    public bool MatchesIndexedContentHash { get; init; }
}

/// <summary>
/// Materializes one indexed unit source only when an editor opens it. The package index
/// remains summary-only; full unit documents and macro bodies are not retained by catalog rows.
/// </summary>
public static class WccLibraryUnitEntityLoader
{
    public static async Task<WccLoadedLibraryUnitEntity> LoadAsync(
        WccLibrarySnapshot snapshot,
        WccEntityId entityId,
        CancellationToken cancellationToken = default)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (!snapshot.EntitiesById.TryGetValue(entityId, out var entity))
            throw new KeyNotFoundException($"Library entity was not found: {entityId}");
        if (entity.EntityType != WccEntityTypes.UnitType)
            throw new ArgumentException("The selected library entity is not a unit type.", nameof(entityId));

        var content = await WccLibraryEntityContentLoader
            .LoadAsync(snapshot, entityId, cancellationToken)
            .ConfigureAwait(false);

        var asset = new UnitFileAsset
        {
            FileName = Path.GetFileName(content.SourceFile.StoragePath)
                       ?? Path.GetFileName(content.SourceFile.RelativePath),
            SourcePath = content.SourceFile.StoragePath,
            Origin = ResolveSourceOrigin(snapshot, content.SourceFile),
            RawTextSnapshot = content.SourceFileText
        };

        asset.Documents.AddRange(UnitTypeImporter.ImportAllFromText(content.SourceFileText));
        asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(content.SourceFileText));

        var preferred = asset.Documents.FirstOrDefault(document =>
                            string.Equals(
                                WccContentHash.ComputeText(document.BlockSpan.RawText),
                                entity.ContentFingerprint,
                                StringComparison.OrdinalIgnoreCase))
                        ?? asset.Documents.FirstOrDefault(document =>
                            string.Equals(
                                document.Unit.Id,
                                entity.ExportSymbol,
                                StringComparison.Ordinal));

        return new WccLoadedLibraryUnitEntity
        {
            SourceFile = asset,
            PreferredUnitDocumentInternalId = preferred?.InternalId,
            MatchesIndexedContentHash = content.MatchesIndexedContentHash
        };
    }

    private static WccSourceOrigin ResolveSourceOrigin(
        WccLibrarySnapshot snapshot,
        WccSourceFileRecord sourceFile)
    {
        if (!snapshot.RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package))
        {
            return WccSourceOrigin.ImportedExternal;
        }

        if (package.SourceKind == WccPackageSourceKinds.WesnothMainline)
            return WccSourceOrigin.CoreInstall;

        if (package.SourceKind == WccPackageSourceKinds.WccManagedLibrary &&
            package.StorageMode == WccPackageStorageMode.ManagedSnapshot)
        {
            return WccSourceOrigin.WccGenerated;
        }

        return WccSourceOrigin.ImportedExternal;
    }
}
