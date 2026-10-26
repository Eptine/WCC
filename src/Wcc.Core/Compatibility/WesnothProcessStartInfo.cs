using System.Diagnostics;

namespace Wcc.Core.Compatibility;

/// <summary>Shared terminal-only game launch environment for identity, WML and image requests.</summary>
internal static class WesnothProcessStartInfo
{
    // A Steam container sets its own library search path. Prepend the game's
    // bundled libraries inside it, as Wesnoth's start.sh does. Paths and caller
    // arguments are positional parameters, never interpolated into shell code.
    private const string ContainerLaunch =
        "if [ -d \"$1/lib\" ]; then export LD_LIBRARY_PATH=\"$1/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}\"; fi; " +
        "shift; exec \"$@\"";

    public static ProcessStartInfo Create(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (!OperatingSystem.IsLinux()) return start;

        var runtime = FindSteamRuntime(executablePath);
        if (runtime is not null)
        {
            start.FileName = runtime;
            start.Environment["PRESSURE_VESSEL_BATCH"] = "1";
            start.ArgumentList.Add("--");
            start.ArgumentList.Add("/bin/sh");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(ContainerLaunch);
            start.ArgumentList.Add("wcc");
            start.ArgumentList.Add(directory);
            start.ArgumentList.Add(executablePath);
            ShareDirectory(start, directory, writable: false);
        }
        else
        {
            var libraries = Path.Combine(directory, "lib");
            if (Directory.Exists(libraries))
            {
                start.Environment.TryGetValue("LD_LIBRARY_PATH", out var inherited);
                start.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(inherited)
                    ? libraries : libraries + ":" + inherited;
            }
        }
        return start;
    }

    public static void ShareDirectory(ProcessStartInfo start, string? directory, bool writable)
    {
        // Only container launches have a command separator. Direct launches must
        // keep the engine's own ArgumentList unchanged. Mount the specific game,
        // staged input and requested output directories, never the whole host.
        if (!OperatingSystem.IsLinux() || !start.ArgumentList.Contains("--") || string.IsNullOrWhiteSpace(directory))
            return;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (writable) Directory.CreateDirectory(path);
        var prefix = "--filesystem=" + path + ":";
        if (start.ArgumentList.Contains(prefix + "rw")) return;
        if (start.ArgumentList.Contains(prefix + "ro"))
        {
            if (!writable) return;
            start.ArgumentList.Remove(prefix + "ro");
        }
        start.ArgumentList.Insert(0, prefix + (writable ? "rw" : "ro"));
    }

    public static TimeSpan IdentityTimeout(string executablePath)
        => TimeSpan.FromSeconds(OperatingSystem.IsLinux() && FindSteamRuntime(executablePath) is not null ? 15 : 3);

    private static string? FindSteamRuntime(string executablePath)
    {
        // Only a selected Steam game can opt into Steam's container. Distro and
        // standalone installations keep their own executable and environment.
        var directory = new DirectoryInfo(Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory);
        for (var depth = 0; depth < 3 && directory is not null; depth++, directory = directory.Parent)
        {
            if (directory.Name != "common" || directory.Parent?.Name != "steamapps") continue;
            var sibling = RuntimeIn(directory.FullName);
            if (sibling is not null) return sibling;

            // Steam can keep its runtime in the primary library while the game
            // lives on another drive. Inspect only conventional Steam locations.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            foreach (var steamRoot in new[]
                     {
                         Path.Combine(home, ".local", "share", "Steam"),
                         Path.Combine(home, ".steam", "root"),
                         Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam")
                     })
            {
                var runtime = RuntimeIn(Path.Combine(steamRoot, "steamapps", "common"));
                if (runtime is not null) return runtime;
            }
            return null;
        }
        return null;
    }

    private static string? RuntimeIn(string commonDirectory)
    {
        var root = Path.Combine(commonDirectory, "SteamLinuxRuntime_4");
        var launcher = Path.Combine(root, "run");
        var versions = Path.Combine(root, "VERSIONS.txt");
        try
        {
            if (!File.Exists(launcher) || !File.Exists(versions) || new FileInfo(versions).Length > 16 * 1024)
                return null;
            return File.ReadLines(versions).Any(line => line.StartsWith("steamrt4\t", StringComparison.Ordinal))
                ? launcher : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
