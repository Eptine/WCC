using System.Security.Cryptography;

namespace Wcc.Core.References;

public sealed record WccWesnothImageRenderContext(
    string ImageExpression,
    string UserDataDirectory,
    string CacheIdentity,
    bool HasUnresolvedNestedReferences);

/// <summary>
/// Creates an isolated Wesnoth userdata image root for resolved non-Core assets.
/// Wesnoth always searches &lt;userdata&gt;/images for logical image references, so
/// this avoids modifying the game installation or loading unrelated add-ons.
/// </summary>
public sealed class WccWesnothImageRenderContextBuilder
{
    private readonly string _userDataDirectory;

    public WccWesnothImageRenderContextBuilder(string userDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(userDataDirectory))
        {
            throw new ArgumentException(
                "A Wesnoth render-context userdata directory is required.",
                nameof(userDataDirectory));
        }

        _userDataDirectory = Path.GetFullPath(userDataDirectory);
    }

    public WccWesnothImageRenderContext Build(
        string executablePath,
        WccImageExpression expression,
        string resolvedBaseImagePath)
    {
        Directory.CreateDirectory(_userDataDirectory);

        var coreExpression = WccWesnothImageExpressionMapper.TryMapCoreExpression(
            executablePath,
            expression,
            resolvedBaseImagePath);
        if (coreExpression is not null)
        {
            return new WccWesnothImageRenderContext(
                coreExpression,
                _userDataDirectory,
                "core",
                HasUnresolvedNestedReferences: false);
        }

        var absoluteSourcePath = Path.GetFullPath(resolvedBaseImagePath);
        using var source = File.OpenRead(absoluteSourcePath);
        var contentHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        var nestedReferences = WccImageExpressionNestedReferenceScanner.FindBaseReferences(expression);
        var imageRoot = TryInferImageRoot(absoluteSourcePath, expression.BaseReference);
        var resolvedNested = new List<(string LogicalPath, string SourcePath, string ContentHash)>();
        var hasUnresolvedNestedReferences = false;
        foreach (var nestedReference in nestedReferences)
        {
            if (imageRoot is null ||
                !TryResolveNestedReference(imageRoot, nestedReference, out var nestedSourcePath))
            {
                hasUnresolvedNestedReferences = true;
                continue;
            }

            using var nestedSource = File.OpenRead(nestedSourcePath);
            var nestedHash = Convert.ToHexString(SHA256.HashData(nestedSource)).ToLowerInvariant();
            resolvedNested.Add((NormalizeLogicalPath(nestedReference), nestedSourcePath, nestedHash));
        }

        var contextIdentity = ComputeContextIdentity(
            imageRoot,
            resolvedNested,
            hasUnresolvedNestedReferences);
        var contextUserDataDirectory = resolvedNested.Count > 0
            ? Path.Combine(_userDataDirectory, "contexts", contextIdentity)
            : _userDataDirectory;
        var extension = Path.GetExtension(absoluteSourcePath);
        var logicalPath = $"wcc/{contentHash}{extension}";
        var stagedPath = Path.Combine(
            contextUserDataDirectory,
            "images",
            "wcc",
            contentHash + extension);
        StageFile(absoluteSourcePath, stagedPath);
        foreach (var nested in resolvedNested)
        {
            StageFile(
                nested.SourcePath,
                Path.Combine(
                    contextUserDataDirectory,
                    "images",
                    nested.LogicalPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        return new WccWesnothImageRenderContext(
            logicalPath + expression.ModifierSuffix,
            contextUserDataDirectory,
            contextIdentity,
            hasUnresolvedNestedReferences);
    }

    private static string? TryInferImageRoot(string absoluteSourcePath, string logicalBaseReference)
    {
        var logicalPath = NormalizeLogicalPath(logicalBaseReference);
        if (logicalPath.Length == 0 || logicalPath.StartsWith('~') || Path.IsPathRooted(logicalPath))
            return null;

        var relativePath = logicalPath.Replace('/', Path.DirectorySeparatorChar);
        if (!absoluteSourcePath.EndsWith(
                relativePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return null;
        }

        var rootLength = absoluteSourcePath.Length - relativePath.Length;
        if (rootLength <= 0)
            return null;
        return absoluteSourcePath[..rootLength].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryResolveNestedReference(
        string imageRoot,
        string logicalReference,
        out string sourcePath)
    {
        sourcePath = string.Empty;
        var logicalPath = NormalizeLogicalPath(logicalReference);
        if (logicalPath.Length == 0 || logicalPath.StartsWith('~') || Path.IsPathRooted(logicalPath))
            return false;

        var root = Path.GetFullPath(imageRoot);
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            logicalPath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(
                rootPrefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) ||
            !File.Exists(candidate))
        {
            return false;
        }

        sourcePath = candidate;
        return true;
    }

    private static string NormalizeLogicalPath(string value)
    {
        var parsed = WccImageExpressionParser.TryParse(value);
        return (parsed?.BaseReference ?? value)
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static string ComputeContextIdentity(
        string? imageRoot,
        IReadOnlyList<(string LogicalPath, string SourcePath, string ContentHash)> resolvedNested,
        bool hasUnresolvedNestedReferences)
    {
        var identity = string.Join(
            '\n',
            new[]
            {
                "wcc-same-package-image-context-v1",
                imageRoot ?? string.Empty,
                hasUnresolvedNestedReferences ? "unresolved" : "complete"
            }.Concat(resolvedNested
                .OrderBy(x => x.LogicalPath, StringComparer.Ordinal)
                .Select(x => $"{x.LogicalPath}\t{x.ContentHash}")));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private static void StageFile(string sourcePath, string stagedPath)
    {
        if (File.Exists(stagedPath))
            return;

        var directory = Path.GetDirectoryName(stagedPath) ??
                        throw new InvalidOperationException(
                            "The staged image path has no containing directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(stagedPath)}.{Guid.NewGuid():N}.wcc-stage");
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            try
            {
                File.Move(temporaryPath, stagedPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(stagedPath))
            {
                // Another render request published the same content-addressed file.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
