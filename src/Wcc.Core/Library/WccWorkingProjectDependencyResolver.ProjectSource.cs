namespace Wcc.Core.Library;

public static partial class WccWorkingProjectDependencyResolver
{
    private sealed record CurrentProjectDependencySource(WccPackageRecord Package,
        WccPackageRevisionRecord Revision, string AddonId);

    // A resource package may declare the campaign which owns it as a dependency.
    // Recognize the actual current project namespace for presentation only. This
    // does not collapse an external revision or authorize any source evaluation.
    private static CurrentProjectDependencySource? FindCurrentProjectDependencySource(
        WccLibrarySnapshot snapshot, WccWorkingProjectDescriptor project,
        WccPackageRevisionId? coreId, IReadOnlyCollection<WccPackageRevisionId> reportRoots)
    {
        if (coreId is not { } selectedCoreId ||
            !snapshot.RevisionsById.TryGetValue(selectedCoreId, out var core) ||
            !snapshot.PackagesById.TryGetValue(core.PackageId, out var corePackage) ||
            !corePackage.IsAvailable || !IsDistributionCore(corePackage))
            return null;
        var working = snapshot.PackagesById.Values.Where(package => package.IsAvailable &&
            package.SourceKind == WccPackageSourceKinds.WccProject &&
            package.StorageMode == WccPackageStorageMode.WorkingCopy &&
            SameProjectDependencyRoot(package.SourceLocation, project.RootDirectory)).ToArray();
        if (working.Length != 1 || working[0].CurrentRevisionId is not { } workingId ||
            !snapshot.RevisionsById.TryGetValue(workingId, out var revision) ||
            revision.PackageId != working[0].Id || string.IsNullOrWhiteSpace(revision.ContentFingerprint) ||
            !revision.DependencyRevisionIds.Contains(selectedCoreId) ||
            !revision.DependencyRevisionIds.ToHashSet().SetEquals(reportRoots))
            return null;
        var closure = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, workingId);
        var packagePins = new Dictionary<WccPackageId, WccPackageRevisionId>();
        foreach (var id in closure)
        {
            var member = snapshot.RevisionsById[id];
            if (!snapshot.PackagesById.TryGetValue(member.PackageId, out var package) ||
                !package.IsAvailable || member.DependencyRevisionIds.Any(dependency => !snapshot.RevisionsById.ContainsKey(dependency)) ||
                packagePins.TryGetValue(package.Id, out var previous) && previous != id ||
                IsDistributionCore(package) && id != selectedCoreId)
                return null;
            packagePins[package.Id] = id;
        }
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootDirectory)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return null; }
        var mainPath = Path.Combine(root, "_main.cfg");
        var mains = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == workingId &&
            file.FileType == WccSourceFileTypes.Wml && file.RelativePath == "_main.cfg" &&
            SameProjectDependencyRoot(file.StoragePath, mainPath) && !string.IsNullOrWhiteSpace(file.ContentHash)).ToArray();
        if (mains.Length != 1) return null;
        try
        {
            if (WccContentHash.ComputeText(File.ReadAllText(mainPath)) != mains[0].ContentHash) return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             ArgumentException or NotSupportedException) { return null; }
        // Exactly the same namespace precedence as WccStaticSourceCapture.
        var idValue = working[0].SourceIdentifier ?? revision.AddonMetadata?.AddonId ?? Path.GetFileName(root);
        return string.IsNullOrEmpty(idValue) ? null : new(working[0], revision, idValue);
    }

    private static bool SameProjectDependencyRoot(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return WccPackagePlanEditor.LocationsEqual(left, WccPackagePlanEditor.NormalizeLocation(right));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
    }
}
