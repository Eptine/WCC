using System;
using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Settings;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    // Resource indexing expanded from CFG-only to CFG plus music. A retained
    // legacy pin can still describe the exact same WML source inventory. This
    // separate binding supplies source/preview resources, never an executable
    // profile or permission to migrate the project's Core revision.
    private WccWesnothInstallationProfile? ResolvePinnedProjectSourceResourceProfile()
    {
        if (_currentProjectMetadata?.Compatibility is { CoreRevision: { } sourcePin } compatibility &&
            !string.Equals(compatibility.TargetWesnothVersion, sourcePin.TargetWesnothVersion, StringComparison.Ordinal))
            return null;
        var exactRuntimeProfile = ResolvePinnedProjectInstallationProfile();
        if (exactRuntimeProfile is not null) return exactRuntimeProfile;
        if (_isCurrentProjectMetadataLoadPending ||
            _currentProjectMetadata?.Compatibility?.CoreRevision is not { } pin ||
            LibrarySnapshot is not { } snapshot)
            return null;
        var resolution = WccProjectCorePinResolver.Resolve(snapshot.Manifest, pin);
        if (resolution is not { IsResolved: true, Revision: { } pinnedRevision, Package: { } package } ||
            !string.Equals(pinnedRevision.ContentFingerprint, pin.ContentFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pinnedRevision.Version, pin.ExactWesnothVersion, StringComparison.Ordinal) ||
            !string.Equals(pinnedRevision.TargetWesnothVersion, pin.TargetWesnothVersion, StringComparison.Ordinal) ||
            package.CurrentRevisionId is not { } currentRevisionId ||
            !snapshot.RevisionsById.TryGetValue(currentRevisionId, out var currentRevision) ||
            !WccCoreSourceInventoryCompatibility.Check(snapshot, pinnedRevision.Id, currentRevisionId).IsCompatible)
            return null;

        var projectRoot = ResolveCurrentProjectOrSessionRoot();
        var activeProfileId = FindActiveWesnothInstallationProfile()?.Id;
        return _settings.WesnothInstallationProfiles
            .Where(profile =>
            {
                var detected = profile.LastDetectedInstallation;
                var installRoot = profile.InstallPath ?? detected?.InstallRoot;
                if (detected is null || string.IsNullOrWhiteSpace(installRoot) ||
                    string.IsNullOrWhiteSpace(package.SourceLocation) ||
                    string.IsNullOrWhiteSpace(detected.CoreDataRoot) ||
                    !PathsEqual(detected.CoreDataRoot, package.SourceLocation) ||
                    !string.Equals(detected.ExactVersion, pin.ExactWesnothVersion, StringComparison.Ordinal) ||
                    !string.Equals(detected.CompatibilityTarget, pin.TargetWesnothVersion, StringComparison.Ordinal) ||
                    !string.Equals(profile.CompatibilityTarget, pin.TargetWesnothVersion, StringComparison.Ordinal) ||
                    !string.Equals(detected.CoreContentFingerprint, currentRevision.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
                    return false;
                try
                {
                    return PathsEqual(WccCoreInstallPaths.GetCoreDataRoot(installRoot), package.SourceLocation);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    return false;
                }
            })
            .OrderByDescending(profile => ProfileAddonsContainRoot(profile, projectRoot))
            .ThenByDescending(profile => string.Equals(profile.Id, activeProfileId, StringComparison.Ordinal))
            .ThenBy(profile => profile.Id ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(profile => profile.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private string BuildWesnothSourceResourceProfileIdentity(WccWesnothInstallationProfile? profile)
    {
        var pin = _currentProjectMetadata?.Compatibility?.CoreRevision;
        return string.Join("\u001f", "wcc-source-resources", BuildWesnothRuntimeProfileIdentity(profile),
            pin?.PackageId.ToString() ?? string.Empty, pin?.PackageRevisionId.ToString() ?? string.Empty,
            pin?.ContentFingerprint ?? string.Empty);
    }
}
