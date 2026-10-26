namespace Wcc.Core;

public static class WccAppPaths
{
    /// <summary>
    /// The distributed native WCC host lives above its managed bin directory. Other
    /// hosts (development, tests and dotnet) retain their ordinary managed base directory.
    /// All mutable application data and instance identity use this same root policy.
    /// </summary>
    public static string GetApplicationRoot() =>
        ResolveApplicationRoot(AppContext.BaseDirectory, Environment.ProcessPath);

    public static string ResolveApplicationRoot(string baseDirectory, string? processPath)
    {
        var managedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath) ||
            !Path.GetFileName(managedBase).Equals("bin", comparison))
            return managedBase;

        var process = Path.GetFullPath(processPath);
        var expectedName = OperatingSystem.IsWindows() ? "WCC.exe" : "WCC";
        var parent = Path.GetDirectoryName(managedBase);
        return parent is not null && Path.GetFileName(process).Equals(expectedName, comparison) &&
               string.Equals(Path.GetDirectoryName(process), parent, comparison)
            ? parent : managedBase;
    }

    public static string GetDefaultLibraryRoot()
    {
        return Path.Combine(GetApplicationRoot(), "Library");
    }

    public static string GetDefaultSettingsFilePath()
    {
        return Path.Combine(GetApplicationRoot(), "settings.json");
    }

    public static string GetImageRenderCacheRoot()
    {
        return Path.Combine(GetApplicationRoot(), "Cache", "image-render-v2");
    }

    public static string GetWesnothImageRenderContextRoot()
    {
        return Path.Combine(GetApplicationRoot(), "Cache", "wesnoth-render-context-v1");
    }

    public static string GetWesnothPreprocessCacheRoot()
    {
        return Path.Combine(GetApplicationRoot(), "Cache", "wesnoth-preprocess-v1");
    }
}
