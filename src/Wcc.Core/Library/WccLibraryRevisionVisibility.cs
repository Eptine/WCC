namespace Wcc.Core.Library;

/// <summary>
/// Computes the exact package-revision scope visible from a project or package revision.
/// Consumers use this closure instead of searching the entire installed library, where an
/// unrelated add-on may happen to define the same WML symbol.
/// </summary>
public static class WccLibraryRevisionVisibility
{
    public static IReadOnlySet<WccPackageRevisionId> GetVisibleRevisionIds(
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId rootRevisionId)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (!snapshot.RevisionsById.ContainsKey(rootRevisionId))
            return new HashSet<WccPackageRevisionId>();

        var visible = new HashSet<WccPackageRevisionId>();
        var pending = new Queue<WccPackageRevisionId>();
        pending.Enqueue(rootRevisionId);

        while (pending.TryDequeue(out var revisionId))
        {
            if (!visible.Add(revisionId) ||
                !snapshot.RevisionsById.TryGetValue(revisionId, out var revision))
            {
                continue;
            }

            foreach (var dependencyRevisionId in revision.DependencyRevisionIds)
            {
                if (!visible.Contains(dependencyRevisionId) &&
                    snapshot.RevisionsById.ContainsKey(dependencyRevisionId))
                {
                    pending.Enqueue(dependencyRevisionId);
                }
            }
        }

        return visible;
    }

    public static bool ContainsEntity(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds,
        WccEntityRecord entity)
        => snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile) &&
           visibleRevisionIds.Contains(sourceFile.PackageRevisionId);
}
