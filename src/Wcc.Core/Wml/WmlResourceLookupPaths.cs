namespace Wcc.Core.Wml;

/// <summary>Frozen captured lookup candidates, not resource-existence or lookup-precedence proof.</summary>
public sealed class WmlResourceLookupPathPlan
{
    internal WmlResourceLookupPathPlan(IEnumerable<string> candidates, bool isExplicitDataPath)
    {
        Candidates = Array.AsReadOnly(candidates.Distinct(StringComparer.Ordinal).ToArray());
        IsExplicitDataPath = isExplicitDataPath;
    }

    public IReadOnlyList<string> Candidates { get; }
    public bool IsExplicitDataPath { get; }
}

/// <summary>
/// Pure routing of already extracted resource filenames into existing captured mounts.
/// Pinned 1.19.26 filesystem.cpp also checks game/user-root filenames: supported explicit
/// data paths use their captured package directly. Unknown data roots stay unmapped.
/// Pinned picture.cpp tries same-stem .webp only for missing lowercase .png/.jpg images.
/// Binary filenames use filesystem.cpp's last-../ prefix reset and Boost.Filesystem v3
/// append semantics; neither rule is source-relative filesystem traversal.
/// No filesystem reads, decoding, resource activation or writer authority are supplied.
/// </summary>
public static class WmlResourceLookupPaths
{
    public const string InstallationResourceMountPrefix = "__wcc_install_resources__/";
    public static IReadOnlyList<string> SupportedInstallationResourceFolders { get; } =
        Array.AsReadOnly(new[] { "images", "sounds", "music", "maps" });

    public static WmlResourceLookupPathPlan Create(string filename, string? resourceFolder,
        IReadOnlyList<string> activeBinaryMounts, bool useImageFallback = false,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? installationResourceFolders = null)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(activeBinaryMounts);
        cancellationToken.ThrowIfCancellationRequested();
        if (resourceFolder is not null && TryNormalizeBinaryFilename(filename, out var binaryFilename))
            filename = binaryFilename;
        var explicitDataPath = resourceFolder is not null && filename.StartsWith("data/", StringComparison.Ordinal);
        var candidates = new List<string>();
        if (resourceFolder is null)
            candidates.Add(filename); // Ordered include/Lua paths keep their own routing semantics.
        else if (explicitDataPath)
        {
            var mapped = TryMapDataPath(filename, out var route) ? route : filename;
            // A sibling campaign's assets are independent game-root paths. Only
            // an explicitly captured, metadata-only folder supplies this alias;
            // it never becomes an ordinary binary path or a WML include root.
            var components = mapped.Split('/');
            var capturedFolder = components.Length >= 4 ? string.Join('/', components.Take(3)) : "";
            if (TryGetInstallationCampaignResourceFolder(InstallationResourceMountPrefix + capturedFolder,
                    out _, out _) && (installationResourceFolders ?? []).Contains(capturedFolder, StringComparer.Ordinal))
                candidates.Add(InstallationResourceMountPrefix + mapped);
            else candidates.Add(mapped);
        }
        else
        {
            // Pinned filesystem_common.cpp get_map_file tries a WML location
            // before binary maps. Only known capture spellings can supply this
            // direct evidence; the resolver still determines capture membership.
            if (resourceFolder == "maps" && IsSupportedWmlMountFilename(filename))
                candidates.Add(filename);
            foreach (var mount in activeBinaryMounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(mount);
                candidates.Add(mount + "/" + resourceFolder + "/" + filename);
            }
            // Only explicitly captured installation folders supply these routes.
            // The namespace of installed add-ons never establishes a user root.
            var capturedFolders = (installationResourceFolders ?? []).Where(folder =>
                SupportedInstallationResourceFolders.Contains(folder, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
            if (capturedFolders.Contains(resourceFolder))
                candidates.Add(InstallationResourceMountPrefix + resourceFolder + "/" + filename);
            var separator = filename.IndexOf('/');
            if (separator > 0 && capturedFolders.Contains(filename[..separator]))
                candidates.Add(InstallationResourceMountPrefix + filename);
        }

        // All original filenames precede all aliases, as the image loader first
        // searches the original name across its paths before trying the WebP name.
        if (useImageFallback && (filename.EndsWith(".png", StringComparison.Ordinal) || filename.EndsWith(".jpg", StringComparison.Ordinal)))
            foreach (var original in candidates.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.Add(original[..^4] + ".webp");
            }
        return new(candidates, explicitDataPath);
    }

    public static bool TryGetInstallationResourceFolder(string mount, out string folder)
    {
        ArgumentNullException.ThrowIfNull(mount);
        folder = mount.StartsWith(InstallationResourceMountPrefix, StringComparison.Ordinal)
            ? mount[InstallationResourceMountPrefix.Length..] : "";
        return SupportedInstallationResourceFolders.Contains(folder, StringComparer.Ordinal);
    }

    /// <summary>Recognizes only a reserved installed campaign/type asset folder, never a source mount.</summary>
    public static bool TryGetInstallationCampaignResourceFolder(string mount, out string campaign, out string folder)
    {
        ArgumentNullException.ThrowIfNull(mount);
        campaign = "";
        folder = "";
        if (!mount.StartsWith(InstallationResourceMountPrefix, StringComparison.Ordinal)) return false;
        var relative = mount[InstallationResourceMountPrefix.Length..];
        var parts = relative.Split('/');
        if (parts.Length != 3 || parts[0] != "campaigns" || parts[1].Length == 0 ||
            parts[1].Any(character => char.IsWhiteSpace(character) || "/\\~{}$*?[]():\"'|<>#".Contains(character)) ||
            parts[1] is "." or ".." || parts[1].Contains("..", StringComparison.Ordinal) ||
            !SupportedInstallationResourceFolders.Contains(parts[2], StringComparer.Ordinal)) return false;
        campaign = parts[1];
        folder = parts[2];
        return true;
    }

    /// <summary>Reserved metadata-only installation mounts; callers still require pinned mainline provenance.</summary>
    public static bool IsInstallationResourceMount(string mount) =>
        TryGetInstallationResourceFolder(mount, out _) || TryGetInstallationCampaignResourceFolder(mount, out _, out _);

    /// <summary>
    /// Returns the bounded lookup spelling for a binary filename. Pinned filesystem.cpp
    /// drops everything through the final ../ before checking a file. Boost.Filesystem v3
    /// then appends a slash-prefixed filename to the existing binary type path; it does
    /// not replace that path with an operating-system root. This is not WML include,
    /// Lua, [binary_path], or native filesystem path normalization.
    /// </summary>
    public static bool TryNormalizeBinaryFilename(string filename, out string normalized)
    {
        ArgumentNullException.ThrowIfNull(filename);
        normalized = "";
        // Unresolved input could add a later reset or native separator at runtime.
        // Do not turn a dynamic/foreign prefix into captured static evidence.
        if (filename.IndexOfAny(['$', '{', '}', '@', '\\', ':']) >= 0 || filename.Any(char.IsControl)) return false;
        var lastReset = filename.LastIndexOf("../", StringComparison.Ordinal);
        var suffix = lastReset >= 0 ? filename[(lastReset + 3)..] : filename;
        if (suffix.StartsWith('/')) suffix = suffix[1..];
        if (suffix.Length == 0 || suffix.Contains("..", StringComparison.Ordinal) ||
            suffix.Split('/').Any(component => component.Length == 0 || component == ".")) return false;
        normalized = suffix;
        return true;
    }

    private static bool IsSupportedWmlMountFilename(string filename)
    {
        var dataPath = "data/" + (filename.StartsWith("~add-ons/", StringComparison.Ordinal) ? filename[1..] : filename);
        return TryMapDataPath(dataPath, out var mount) && mount == filename;
    }

    /// <summary>
    /// Maps a supported game/user-root data path to the existing logical capture spelling.
    /// This does not establish that its package was captured, is present, or is activated.
    /// Also shared by [binary_path] routing so the prefix rules have one definition.
    /// </summary>
    public static bool TryMapDataPath(string value, out string mount)
    {
        ArgumentNullException.ThrowIfNull(value);
        mount = "";
        value = value.TrimEnd('/');
        // Binary filenames admit prefix-reset and leading-slash spellings. A declared
        // data root never gains either spelling through that broader resource parser.
        if (value.StartsWith('/') || value.Contains("..", StringComparison.Ordinal)) return false;
        // A route may contain the same literal parentheses as an image filename.
        // Reuse the locator syntax, but never accept a modifier or rewritten base.
        var syntax = WmlResourceReferenceSyntax.ParseImage(value);
        if (!syntax.IsComplete || syntax.Filenames.Count != 1 || syntax.Filenames[0] != value) return false;
        if (value == "data/core" || value.StartsWith("data/core/", StringComparison.Ordinal) ||
            value.StartsWith("data/campaigns/", StringComparison.Ordinal))
            mount = value[5..];
        else if (value.StartsWith("data/add-ons/", StringComparison.Ordinal))
            mount = "~" + value[5..];
        else if (value == "data/internal" || value.StartsWith("data/internal/", StringComparison.Ordinal) ||
            value == "data/ai" || value.StartsWith("data/ai/", StringComparison.Ordinal) ||
            value == "data/themes" || value.StartsWith("data/themes/", StringComparison.Ordinal) ||
            value == "data/lua" || value.StartsWith("data/lua/", StringComparison.Ordinal))
            mount = value[5..];
        return mount.Length != 0;
    }
}
