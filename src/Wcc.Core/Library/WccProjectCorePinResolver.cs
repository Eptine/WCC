using Wcc.Core.Projects;

namespace Wcc.Core.Library;

/// <summary>A local index binding for a portable Core pin, never write or runtime authority.</summary>
public sealed class WccProjectCorePinResolution
{
    internal WccProjectCorePinResolution(WccLibraryManifest manifest, WccProjectCoreRevisionPin pin,
        WccPackageRecord? package, WccPackageRevisionRecord? revision, bool rebuilt, string diagnostic)
    {
        ManifestIdentity = manifest; Pin = pin; Package = package; Revision = revision;
        IsRebuiltIdentity = rebuilt; Diagnostic = diagnostic;
    }

    public WccLibraryManifest ManifestIdentity { get; }
    public WccProjectCoreRevisionPin Pin { get; }
    public WccPackageRecord? Package { get; }
    public WccPackageRevisionRecord? Revision { get; }
    public bool IsResolved => Package is not null && Revision is not null;
    public bool IsRebuiltIdentity { get; }
    public string Diagnostic { get; }
}

/// <summary>
/// Resolves durable project intent without changing its sidecar. Exact local identities
/// win; only a fully rebuilt index may bind the same version, target and content to new IDs.
/// Fresh capture and the normal checked writer still prove actual source and persistence.
/// </summary>
public static class WccProjectCorePinResolver
{
    public static WccProjectCorePinResolution Resolve(WccLibraryManifest manifest, WccProjectCoreRevisionPin pin)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pin);
        WccProjectCorePinResolution Refuse(string message) => new(manifest, pin, null, null, false, message);
        if (pin.PackageId == default || pin.PackageRevisionId == default ||
            string.IsNullOrWhiteSpace(pin.ExactWesnothVersion) || string.IsNullOrWhiteSpace(pin.TargetWesnothVersion))
            return Refuse("The project Core pin has no complete identity, exact version or target.");
        if (manifest.Packages.Select(package => package.Id).Distinct().Count() != manifest.Packages.Count ||
            manifest.PackageRevisions.Select(revision => revision.Id).Distinct().Count() != manifest.PackageRevisions.Count)
            return Refuse("The package index has ambiguous package or revision identities.");

        var packages = manifest.Packages.Where(package => package.Id == pin.PackageId).ToArray();
        var revisions = manifest.PackageRevisions.Where(revision => revision.Id == pin.PackageRevisionId).ToArray();
        if (packages.Length != 0 || revisions.Length != 0)
        {
            if (packages.Length != 1 || revisions.Length != 1 || revisions[0].PackageId != packages[0].Id ||
                !IsAvailableDistribution(packages[0]) || !Matches(pin, revisions[0], requireFingerprint: false))
                return Refuse("The retained Core identity is unavailable, ambiguous or conflicts with the project pin; another revision was not substituted.");
            return new(manifest, pin, packages[0], revisions[0], false, "The exact retained Core revision is available.");
        }

        if (!IsSha256(pin.ContentFingerprint))
            return Refuse("A rebuilt Library needs the project's nonempty SHA256 Core fingerprint; version alone cannot identify its Core.");
        var candidates = new List<(WccPackageRecord Package, WccPackageRevisionRecord Revision)>();
        foreach (var package in manifest.Packages.Where(IsAvailableDistribution))
        {
            if (package.CurrentRevisionId is not { } current || manifest.Packages.Count(row => row.Id == package.Id) != 1)
                continue;
            var selected = manifest.PackageRevisions.Where(revision => revision.Id == current).ToArray();
            if (selected.Length == 1 && selected[0].PackageId == package.Id && Matches(pin, selected[0], requireFingerprint: true))
                candidates.Add((package, selected[0]));
        }
        if (candidates.Count != 1)
            return Refuse(candidates.Count == 0
                ? "No available indexed Core has the project's exact version, target and fingerprint."
                : "More than one available indexed Core matches the project's exact version, target and fingerprint.");
        var candidate = candidates[0];
        return new(manifest, pin, candidate.Package, candidate.Revision, true,
            "The rebuilt Library contains the same exact Core content under new local identities; the project pin is unchanged.");
    }

    private static bool Matches(WccProjectCoreRevisionPin pin, WccPackageRevisionRecord revision, bool requireFingerprint)
        => string.Equals(revision.Version, pin.ExactWesnothVersion, StringComparison.Ordinal) &&
           string.Equals(revision.TargetWesnothVersion, pin.TargetWesnothVersion, StringComparison.Ordinal) &&
           (!requireFingerprint && string.IsNullOrWhiteSpace(pin.ContentFingerprint) ||
            (!requireFingerprint || IsSha256(revision.ContentFingerprint)) &&
            string.Equals(revision.ContentFingerprint, pin.ContentFingerprint, StringComparison.OrdinalIgnoreCase));

    private static bool IsAvailableDistribution(WccPackageRecord package)
        => package.IsAvailable && package.StorageMode == WccPackageStorageMode.ExternalReadOnly && package.ParentPackageId is null &&
           (package.SourceKind == WccPackageSourceKinds.WesnothCore || package.SourceKind == WccPackageSourceKinds.WesnothMainline) &&
           (package.ContentKind == WccPackageContentKinds.Core || package.ContentKind == WccPackageContentKinds.ResourcePack) &&
           !string.IsNullOrWhiteSpace(package.SourceLocation) && Path.IsPathFullyQualified(package.SourceLocation);

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
