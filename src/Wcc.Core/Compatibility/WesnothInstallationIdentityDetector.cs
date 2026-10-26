using System.Diagnostics;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Wcc.Core.Projects;
using Wcc.Core.References;

namespace Wcc.Core.Compatibility;

public static class WesnothInstallationIdentityDetector
{
    private static readonly Regex ChangelogVersionPattern = new(
        @"^##\s+Version\s+(?<version>\d+\.\d+(?:\.\d+)?(?:[-+ ][^\r\n]*)?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static WesnothInstallationDetectionResult Detect(string? configuredInstallPath)
    {
        if (string.IsNullOrWhiteSpace(configuredInstallPath))
        {
            return Failure("No Wesnoth installation is configured.");
        }

        string configuredPath;
        try
        {
            configuredPath = Path.GetFullPath(configuredInstallPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return Failure($"The configured Wesnoth path is invalid: {ex.Message}");
        }

        var executablePath = WccWesnothExecutableLocator.Resolve(configuredPath);
        if (executablePath is null)
        {
            return Failure(
                "No Wesnoth executable was found in the configured installation folder.");
        }

        var installRoot = ResolveInstallRoot(configuredPath, executablePath);
        var coreDataRoot = WccCoreInstallPaths.GetCoreDataRoot(installRoot);
        // ELF has no reliable Windows file-version metadata. A nearby changelog
        // cannot prove that a split-layout executable belongs to these data files.
        var version = OperatingSystem.IsLinux() ? null : DetectVersion(executablePath, installRoot);
        if (OperatingSystem.IsLinux())
        {
            var native = WesnothNativeInstallationProbe.Detect(executablePath);
            if (native is null)
                return Failure("The Linux Wesnoth executable did not report a valid data path and exact version. " +
                               "Select its installed game data folder and check that the executable can run. " +
                               "For Steam installations, also check that Steam Linux Runtime 4 is installed.");

            var nativeCore = WccCoreInstallPaths.GetCoreDataRoot(native.Value.DataRoot);
            if (Directory.Exists(coreDataRoot) && !PathComparer.Equals(Path.GetFullPath(coreDataRoot), Path.GetFullPath(nativeCore)))
                return Failure("The selected Wesnoth data folder does not match the executable's reported data path. " +
                               "Select the data folder belonging to this installed version.");

            installRoot = native.Value.DataRoot;
            coreDataRoot = nativeCore;
            version = native.Value.Version;
        }
        if (!Directory.Exists(coreDataRoot))
        {
            return Failure($"The Wesnoth Core data folder was not found: {coreDataRoot}");
        }

        if (version is null)
        {
            return Failure(
                "The Wesnoth executable was found, but its exact version could not be detected.");
        }

        var dataRoot = Directory.GetParent(coreDataRoot)?.FullName ??
                       Path.Combine(installRoot, "data");
        var schemaRoot = Path.Combine(dataRoot, "schema");
        var campaignsRoot = WccCoreInstallPaths.GetCampaignsRoot(installRoot);
        var warnings = new List<string>();
        if (!Directory.Exists(schemaRoot))
        {
            warnings.Add(
                $"No schema folder was found at {schemaRoot}; schema-backed validation will be unavailable.");
        }

        if (!Directory.Exists(campaignsRoot))
        {
            warnings.Add(
                $"No mainline campaigns folder was found at {campaignsRoot}.");
        }

        return new WesnothInstallationDetectionResult
        {
            Identity = new WesnothInstallationIdentity
            {
                InstallRoot = installRoot,
                ExecutablePath = Path.GetFullPath(executablePath),
                CoreDataRoot = Path.GetFullPath(coreDataRoot),
                CampaignsRoot = Path.GetFullPath(campaignsRoot),
                SchemaRoot = Path.GetFullPath(schemaRoot),
                Version = version
            },
            Warnings = warnings
        };
    }

    private static string ResolveInstallRoot(string configuredPath, string executablePath)
    {
        var configuredRoot = File.Exists(configuredPath)
            ? Path.GetDirectoryName(configuredPath)
            : configuredPath;
        var executableRoot = Path.GetDirectoryName(executablePath);
        var candidates = new[]
            {
                configuredRoot,
                executableRoot,
                executableRoot is null ? null : Directory.GetParent(executableRoot)?.FullName
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Path.GetFullPath(x!))
            .Distinct(PathComparer)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(WccCoreInstallPaths.GetCoreDataRoot(candidate)))
                return candidate;
        }

        return candidates.FirstOrDefault() ?? Path.GetDirectoryName(executablePath)!;
    }

    private static WesnothVersion? DetectVersion(string executablePath, string installRoot)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            foreach (var candidate in new[] { info.ProductVersion, info.FileVersion })
            {
                if (WesnothVersion.TryParse(candidate, out var version))
                    return version;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
                                   UnauthorizedAccessException or Win32Exception)
        {
            // Some Unix builds and test fixtures do not expose native file-version metadata.
            // The installed changelog is an official build artifact and is the next source.
        }

        foreach (var changelogPath in new[]
                 {
                     Path.Combine(installRoot, "changelog.txt"),
                     Path.Combine(installRoot, "changelog.md")
                 })
        {
            var version = DetectVersionFromChangelog(changelogPath);
            if (version is not null)
                return version;
        }

        return null;
    }

    private static WesnothVersion? DetectVersionFromChangelog(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var reader = File.OpenText(path);
            for (var lineNumber = 0; lineNumber < 100 && reader.ReadLine() is { } line; lineNumber++)
            {
                var match = ChangelogVersionPattern.Match(line);
                if (match.Success &&
                    WesnothVersion.TryParse(match.Groups["version"].Value, out var version))
                {
                    return version;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static WesnothInstallationDetectionResult Failure(string message)
        => new() { Error = message };

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
