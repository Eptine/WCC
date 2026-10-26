using Wcc.Core.Projects;

namespace Wcc.Core.References;

/// <summary>
/// Converts a physically resolved Core image back into the logical image path
/// understood by Wesnoth's image locator. Wesnoth 1.18 may silently render a
/// transparent surface when --render-image receives an absolute filesystem path.
/// Non-Core paths need an explicit package binary-path context and are therefore
/// not guessed here.
/// </summary>
public static class WccWesnothImageExpressionMapper
{
    public static string? TryMapCoreExpression(
        string executablePath,
        WccImageExpression expression,
        string resolvedBaseImagePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            expression is null ||
            string.IsNullOrWhiteSpace(resolvedBaseImagePath))
        {
            return null;
        }

        try
        {
            var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (string.IsNullOrWhiteSpace(executableDirectory))
                return null;

            foreach (var installCandidate in EnumerateInstallCandidates(executableDirectory))
            {
                var coreImagesRoot = Path.Combine(
                    WccCoreInstallPaths.GetCoreDataRoot(installCandidate),
                    "images");
                if (!Directory.Exists(coreImagesRoot))
                    continue;

                var relativePath = TryGetContainedRelativePath(
                    coreImagesRoot,
                    resolvedBaseImagePath);
                if (relativePath is not null)
                    return relativePath.Replace('\\', '/') + expression.ModifierSuffix;
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateInstallCandidates(string executableDirectory)
    {
        yield return executableDirectory;

        var parent = Directory.GetParent(executableDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
            yield return parent!;
    }

    private static string? TryGetContainedRelativePath(string rootPath, string candidatePath)
    {
        var root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(candidatePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(root, comparison))
            return null;

        var relative = Path.GetRelativePath(root, candidate);
        return Path.IsPathRooted(relative) ||
               relative.Equals("..", StringComparison.Ordinal) ||
               relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? null
            : relative;
    }
}
