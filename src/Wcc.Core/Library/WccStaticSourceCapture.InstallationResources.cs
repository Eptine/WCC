using Wcc.Core.Compatibility;
using Wcc.Core.Wml;

namespace Wcc.Core.Library;

public static partial class WccStaticSourceCapture
{
    // Transient resource-only aliases are tied to the exact pinned external Core.
    // They do not add package records, source pins, macro preludes or dependency edges.
    private static void AddInstalledResourceRoots(WccLibrarySnapshot snapshot, List<Root> roots,
        WccPackageRevisionId rootRevisionId, CancellationToken cancellationToken)
    {
        var cores = roots.Where(root => root.IsCore).Take(2).ToArray();
        if (cores.Length != 1 || !snapshot.RevisionsById.TryGetValue(cores[0].Revision, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) ||
            package.StorageMode != WccPackageStorageMode.ExternalReadOnly || !WesnothVersion.TryParse(revision.Version, out _)) return;
        var selected = roots.Where(root => root.Revision == rootRevisionId).Take(2).ToArray();
        if (selected.Length > 1) return;
        var counterpart = selected.SingleOrDefault() is { IsCore: false } owner ? owner.Path : null;
        foreach (var resource in WccInstalledResourceRootDiscovery.Discover(cores[0].Path, counterpart, cancellationToken))
        {
            // Reuse only an exact already pinned asset root. A conflicting mount or
            // physical owner still reaches the existing ambiguity checks below.
            if (roots.Any(root => root.IsResourceOnly && root.Mount == resource.Mount && DiskComparer.Equals(root.Path, resource.Path))) continue;
            var pathIdentity = OperatingSystem.IsWindows() ? resource.Path.ToUpperInvariant() : resource.Path;
            var identity = WccContentHash.ComputeText("wcc-captured-installation-assets-v1\n" + cores[0].Revision + "\n" + resource.Mount + "\n" + pathIdentity);
            var transientRevision = new WccPackageRevisionId(Guid.ParseExact(identity[..32], "N"));
            roots.Add(new(resource.Path, resource.Mount, transientRevision, false, false, [])
            { IsResourceOnly = true, IsOptionalInstallationResource = true });
        }
    }
}
