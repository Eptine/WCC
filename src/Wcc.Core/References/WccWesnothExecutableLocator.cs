namespace Wcc.Core.References;

public static class WccWesnothExecutableLocator
{
    public static string? Resolve(string? configuredInstallPath)
    {
        if (string.IsNullOrWhiteSpace(configuredInstallPath))
            return null;

        try
        {
            var configured = Path.GetFullPath(configuredInstallPath);
            if (File.Exists(configured))
                return configured;

            foreach (var candidate in Candidates(configured))
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string configured)
    {
        var name = OperatingSystem.IsWindows() ? "wesnoth.exe" : "wesnoth";
        yield return Path.Combine(configured, name);
        yield return Path.Combine(configured, "bin", name);
        var leaf = new DirectoryInfo(configured);
        if (string.Equals(leaf.Name, "core", StringComparison.Ordinal) && leaf.Parent is not null)
            leaf = leaf.Parent;
        if (string.Equals(leaf.Name, "data", StringComparison.Ordinal) && leaf.Parent is not null)
        {
            yield return Path.Combine(leaf.Parent.FullName, name);
            yield return Path.Combine(leaf.Parent.FullName, "bin", name);
        }
        if (!OperatingSystem.IsLinux()) yield break;

        // Distro installations split the executable under <prefix>/bin or games
        // from the selected <prefix>/share/... data tree. Do not search PATH or
        // start a process here: this lookup also serves frequent image previews.
        var names = new List<string>();
        var directory = new DirectoryInfo(configured);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var suffix = directory.Name.StartsWith("wesnoth-", StringComparison.Ordinal)
                ? directory.Name[8..] : directory.Name;
            if (System.Text.RegularExpressions.Regex.IsMatch(suffix, @"^\d+\.\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                names.Add("wesnoth-" + suffix);
                names.Add("wesnoth" + suffix);
            }
            if (!string.Equals(directory.Name, "share", StringComparison.Ordinal) || directory.Parent is null) continue;
            names.Add(name);
            foreach (var executableName in names.Distinct(StringComparer.Ordinal))
            {
                yield return Path.Combine(directory.Parent.FullName, "games", executableName);
                yield return Path.Combine(directory.Parent.FullName, "bin", executableName);
            }
            yield break;
        }
    }
}
