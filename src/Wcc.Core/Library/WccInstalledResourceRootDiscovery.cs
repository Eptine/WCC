using Wcc.Core.Wml;

namespace Wcc.Core.Library;

/// <summary>A known installed asset folder, not a package, source include or write permission.</summary>
public sealed record WccInstalledResourceRoot(string Path, string Mount, string Name)
{ public bool AuthorizesPersistence => false; }

/// <summary>Discovers only the engine's known game/data/core asset layout. It never
/// reads a sibling campaign's WML or searches recursively for a filename. Unproven,
/// linked or unreadable optional folders provide no resource evidence.</summary>
public static class WccInstalledResourceRootDiscovery
{
    private const int MaximumCampaignDirectories = 200_000;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static IReadOnlyList<WccInstalledResourceRoot> Discover(string coreDataRoot,
        string? workingProjectRoot = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coreDataRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var core = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(coreDataRoot));
        var data = System.IO.Path.GetDirectoryName(core);
        if (System.IO.Path.GetFileName(core) != "core" || data is null || System.IO.Path.GetFileName(data) != "data" ||
            System.IO.Path.GetDirectoryName(data) is not { } game || !IsOrdinary(core)) return [];
        var roots = new List<WccInstalledResourceRoot>();
        foreach (var folder in WmlResourceLookupPaths.SupportedInstallationResourceFolders)
            Add(System.IO.Path.Combine(game, folder), WmlResourceLookupPaths.InstallationResourceMountPrefix + folder,
                "Wesnoth installation " + folder);
        var campaignsPath = System.IO.Path.Combine(data, "campaigns");
        if (!IsOrdinary(campaignsPath)) return Array.AsReadOnly(roots.ToArray());
        DirectoryInfo[] campaigns;
        try
        {
            campaigns = new DirectoryInfo(campaignsPath).EnumerateDirectories().Take(MaximumCampaignDirectories + 1).ToArray();
            if (campaigns.Length > MaximumCampaignDirectories || campaigns.Select(item => item.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != campaigns.Length) return Array.AsReadOnly(roots.ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Array.AsReadOnly(roots.ToArray()); }
        var workingName = string.IsNullOrWhiteSpace(workingProjectRoot) ? null :
            System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(workingProjectRoot)));
        foreach (var campaign in campaigns.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // An opened/copied campaign supplies its own spellings. An installed
            // namesake must never fill a missing asset in that working source.
            if (PathComparer.Equals(campaign.Name, workingName) || !IsOrdinary(campaign.FullName)) continue;
            foreach (var folder in WmlResourceLookupPaths.SupportedInstallationResourceFolders)
            {
                var mount = WmlResourceLookupPaths.InstallationResourceMountPrefix + "campaigns/" + campaign.Name + "/" + folder;
                if (WmlResourceLookupPaths.TryGetInstallationCampaignResourceFolder(mount, out _, out _))
                    Add(System.IO.Path.Combine(campaign.FullName, folder), mount, "Wesnoth campaign assets " + campaign.Name + "/" + folder);
            }
        }
        return Array.AsReadOnly(roots.ToArray());

        void Add(string path, string mount, string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsOrdinary(path)) roots.Add(new(path, mount, name));
        }
    }

    private static bool IsOrdinary(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists) return false;
            for (var ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0 || ancestor.LinkTarget is not null) return false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }
}
