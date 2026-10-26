namespace Wcc.Core.Library;

/// <summary>
/// Recognizes an installed add-on revision shared by its writable working-project overlay.
/// The installed source remains available to the standalone Library; an older unavailable
/// cycle back-edge can also be recognized without replacing its exact revision.
/// The alias is deliberately narrow: it must describe the same package bytes at the same
/// root and must be tied to the exact selected Core revision.
/// </summary>
public static class WccWorkingAddonOverlayRevisionAlias
{
    public static bool IsVerified(
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId workingRevisionId,
        WccPackageRevisionId candidateRevisionId,
        WccPackageRevisionId selectedCoreRevisionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (candidateRevisionId == workingRevisionId ||
            !snapshot.RevisionsById.TryGetValue(workingRevisionId, out var workingRevision) ||
            !snapshot.PackagesById.TryGetValue(workingRevision.PackageId, out var workingPackage) ||
            !snapshot.RevisionsById.TryGetValue(candidateRevisionId, out var candidateRevision) ||
            !snapshot.PackagesById.TryGetValue(candidateRevision.PackageId, out var candidatePackage) ||
            !snapshot.RevisionsById.TryGetValue(selectedCoreRevisionId, out var coreRevision) ||
            !snapshot.PackagesById.TryGetValue(coreRevision.PackageId, out var corePackage))
        {
            return false;
        }

        if (!workingPackage.IsAvailable ||
            workingPackage.SourceKind != WccPackageSourceKinds.WccProject ||
            workingPackage.StorageMode != WccPackageStorageMode.WorkingCopy ||
            workingPackage.CurrentRevisionId != workingRevisionId ||
            string.IsNullOrWhiteSpace(workingPackage.SourceLocation) ||
            candidatePackage.SourceKind != WccPackageSourceKinds.WesnothAddon ||
            candidatePackage.StorageMode != WccPackageStorageMode.ExternalReadOnly ||
            candidatePackage.CurrentRevisionId != candidateRevisionId ||
            candidatePackage.ParentPackageId != corePackage.Id ||
            string.IsNullOrWhiteSpace(candidatePackage.SourceLocation) ||
            !corePackage.IsAvailable ||
            corePackage.ParentPackageId is not null ||
            corePackage.ContentKind != WccPackageContentKinds.ResourcePack ||
            corePackage.StorageMode != WccPackageStorageMode.ExternalReadOnly ||
            corePackage.CurrentRevisionId != selectedCoreRevisionId ||
            !IsWesnothCorePackage(corePackage) ||
            !workingRevision.DependencyRevisionIds.Contains(selectedCoreRevisionId) ||
            !candidateRevision.DependencyRevisionIds.Contains(selectedCoreRevisionId) ||
            string.IsNullOrWhiteSpace(workingRevision.ContentFingerprint) ||
            string.IsNullOrWhiteSpace(candidateRevision.ContentFingerprint) ||
            !string.Equals(
                workingRevision.ContentFingerprint,
                candidateRevision.ContentFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var workingNamespace = GetNamespace(workingPackage, workingRevision);
        return workingNamespace is not null && string.Equals(workingNamespace,
                GetNamespace(candidatePackage, candidateRevision), StringComparison.Ordinal) &&
            LocationsEqual(workingPackage.SourceLocation, candidatePackage.SourceLocation);
    }

    private static string? GetNamespace(WccPackageRecord package, WccPackageRevisionRecord revision)
    {
        try
        {
            var identifier = package.SourceIdentifier ?? revision.AddonMetadata?.AddonId ??
                Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SourceLocation!)));
            return string.IsNullOrWhiteSpace(identifier) ? null : identifier;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static bool IsWesnothCorePackage(WccPackageRecord package) =>
        package.SourceKind == WccPackageSourceKinds.WesnothCore ||
        package.SourceKind == WccPackageSourceKinds.WesnothMainline;

    private static bool LocationsEqual(string left, string right)
    {
        try
        {
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            return comparer.Equals(
                Path.GetFullPath(left)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}
