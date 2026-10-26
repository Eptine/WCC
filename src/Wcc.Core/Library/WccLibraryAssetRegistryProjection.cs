using Wcc.Core.Projects;
using Wcc.Core.References;

namespace Wcc.Core.Library;

/// <summary>
/// Projects package resource usages into the current asset inspector.
/// Resource files themselves will become target entities in the later export-closure slice.
/// </summary>
public static class WccLibraryAssetRegistryProjection
{
    public static WccAssetRegistry Build(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        var registry = new WccAssetRegistry();
        Rebuild(registry, snapshot, visibleRevisionIds);
        return registry;
    }

    public static void Rebuild(
        WccAssetRegistry registry,
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds = null)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        registry.Clear();

        foreach (var reference in snapshot.ReferencesById.Values
                     .Where(x => x.ReferenceType == WccReferenceTypes.Resource)
                     .Where(x => IsVisible(snapshot, x, visibleRevisionIds))
                     .OrderBy(x => x.SourceFileId.Value)
                     .ThenBy(x => x.Id.Value))
        {
            if (string.IsNullOrWhiteSpace(reference.LookupValue))
                continue;

            var assetReference = CreateAssetReference(reference);
            registry.AddReference(assetReference, ResolveOrigin(snapshot, reference.SourceFileId));
        }
    }

    private static bool IsVisible(
        WccLibrarySnapshot snapshot,
        WccReferenceRecord reference,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds)
        => visibleRevisionIds is null ||
           snapshot.SourceFilesById.TryGetValue(reference.SourceFileId, out var sourceFile) &&
           visibleRevisionIds.Contains(sourceFile.PackageRevisionId);

    private static WccAssetReference CreateAssetReference(WccReferenceRecord reference)
    {
        var metadata = reference.Metadata;
        var kind = metadata.TryGetValue("asset_kind", out var kindText) &&
                   Enum.TryParse<WccAssetReferenceKind>(kindText, ignoreCase: true, out var parsedKind)
            ? parsedKind
            : WccAssetReferenceKind.Image;

        return new WccAssetReference
        {
            Kind = kind,
            OwnerBlockKind = metadata.GetValueOrDefault("owner_block_kind") ?? "wml",
            OwnerInternalId = reference.SourceEntityId?.ToString(),
            SourceFieldKey = metadata.GetValueOrDefault("source_field") ?? "resource",
            RawValue = reference.RawValue,
            LookupPath = reference.LookupValue,
            IsTemplatedPath = metadata.TryGetValue("templated", out var templated) &&
                              string.Equals(templated, "true", StringComparison.OrdinalIgnoreCase),
            TemplatedMacroName = metadata.GetValueOrDefault("templated_macro_name"),
            TemplatedMacroRawInvocation = metadata.GetValueOrDefault("templated_macro_invocation"),
            TemplatedPathSuffix = metadata.GetValueOrDefault("templated_path_suffix")
        };
    }

    private static WccSourceOrigin ResolveOrigin(
        WccLibrarySnapshot snapshot,
        WccSourceFileId sourceFileId)
    {
        if (!snapshot.SourceFilesById.TryGetValue(sourceFileId, out var sourceFile) ||
            !snapshot.RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package))
        {
            return WccSourceOrigin.ImportedExternal;
        }

        if (package.SourceKind == WccPackageSourceKinds.WesnothMainline)
            return WccSourceOrigin.CoreInstall;
        if (package.SourceKind == WccPackageSourceKinds.WccManagedLibrary)
            return WccSourceOrigin.WccGenerated;

        return WccSourceOrigin.ImportedExternal;
    }
}
