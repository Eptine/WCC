namespace Wcc.Core.Projects;

public static class WccCoreInstallPaths
{
    public static string GetCoreDataRoot(string wesnothInstallPath)
    {
        if (string.IsNullOrWhiteSpace(wesnothInstallPath))
            throw new ArgumentException("Wesnoth install path must be set.", nameof(wesnothInstallPath));

        var full = Path.GetFullPath(wesnothInstallPath);

        var installStyle = Path.Combine(full, "data", "core");
        if (Directory.Exists(installStyle))
            return installStyle;

        var dataStyle = Path.Combine(full, "core");
        if (Directory.Exists(dataStyle))
            return dataStyle;

        if (Directory.Exists(Path.Combine(full, "units")) &&
            Directory.Exists(Path.Combine(full, "images")))
        {
            return full;
        }

        return installStyle;
    }

    public static string GetCoreUnitsDirectory(string wesnothInstallPath)
        => Path.Combine(GetCoreDataRoot(wesnothInstallPath), "units");

    public static string GetCoreMacrosDirectory(string wesnothInstallPath)
        => Path.Combine(GetCoreDataRoot(wesnothInstallPath), "macros");

    public static string GetCampaignsRoot(string wesnothInstallPath)
    {
        if (string.IsNullOrWhiteSpace(wesnothInstallPath))
            throw new ArgumentException("Wesnoth install path must be set.", nameof(wesnothInstallPath));

        var full = Path.GetFullPath(wesnothInstallPath);

        var installStyle = Path.Combine(full, "data", "campaigns");
        if (Directory.Exists(installStyle))
            return installStyle;

        var dataStyle = Path.Combine(full, "campaigns");
        if (Directory.Exists(dataStyle))
            return dataStyle;

        return installStyle;
    }
}