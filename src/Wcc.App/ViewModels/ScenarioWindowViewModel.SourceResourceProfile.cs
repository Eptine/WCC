using System;
using Wcc.Core.Maps;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private string? _wesnothSourceInstallPath;
    private string? _wesnothSourceAddonsPath;
    private string _wesnothSourceProfileIdentity = "unconfigured-runtime";

    /// <summary>
    /// Binds WCC source capture and display resources independently from the
    /// executable runtime. The host proves the selected source inventory; this
    /// setter grants neither engine admission nor persistence authority.
    /// </summary>
    public void SetWesnothSourceResourceProfile(string? installPath, string? addonsPath, string? profileIdentity)
        => ApplyWesnothSourceResourceProfile(installPath, addonsPath, profileIdentity);

    private bool ApplyWesnothSourceResourceProfile(string? installPath, string? addonsPath, string? profileIdentity)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var normalizedInstallPath = NormalizeRuntimeInstallRoot(installPath);
            var normalizedAddonsPath = NormalizeDirectoryPath(addonsPath);
            var normalizedProfileIdentity = string.IsNullOrWhiteSpace(profileIdentity)
                ? "unconfigured-runtime" : profileIdentity;
            if (string.Equals(_wesnothSourceInstallPath, normalizedInstallPath, PathComparison) &&
                string.Equals(_wesnothSourceAddonsPath, normalizedAddonsPath, PathComparison) &&
                string.Equals(_wesnothSourceProfileIdentity, normalizedProfileIdentity, StringComparison.Ordinal))
                return false;

            _savedSourcePreviewNavigationGeneration++;
            ClearPendingWccFieldSelection();
            CancelBackgroundOperations();
            InvalidateSourceEditingSession();
            _wesnothSourceInstallPath = normalizedInstallPath;
            _wesnothSourceAddonsPath = normalizedAddonsPath;
            _wesnothSourceProfileIdentity = normalizedProfileIdentity;
            _terrainVisualCatalog = WesnothTerrainVisualCatalog.LoadCore(normalizedInstallPath);
            RaiseEditingStateChanged();
            RaiseWccInlineDraftsChanged();
            RefreshMapPreview();
            RefreshSideLeaderPreview();
            return true;
        }
    }

    private bool IsProtectedWesnothInstallPath(string? path) =>
        IsPathInsideRoot(path, _wesnothSourceInstallPath) || IsPathInsideRoot(path, _wesnothInstallPath);
}
