using System.Security.Cryptography;

namespace Wcc.Core.References;

public sealed record WccPackageMusicImportResult(string? WmlPath, string? DestinationPath, string? Problem,
    bool Copied = false)
{
    public bool IsSuccess => Problem is null && WmlPath is not null;
}

/// <summary>Copies selected music into a writable package without overwriting existing assets.</summary>
public static class WccPackageMusicImporter
{
    public static IReadOnlyList<string> FilePatterns { get; } = Array.AsReadOnly(new[]
    {
        "*.ogg", "*.oga", "*.opus", "*.mp3", "*.flac", "*.wav", "*.aif", "*.aiff",
        "*.mid", "*.midi", "*.mod", "*.s3m", "*.xm", "*.it", "*.voc", "*.aac", "*.m4a"
    });

    public static WccPackageMusicImportResult Import(string packageRoot, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(packageRoot) || string.IsNullOrWhiteSpace(sourcePath))
            return new(null, null, "Choose a music file and a writable campaign folder.");
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
            var source = Path.GetFullPath(sourcePath);
            if (!Directory.Exists(root) || !File.Exists(source))
                return new(null, null, "The campaign folder or selected music file is unavailable.");
            var extension = Path.GetExtension(source);
            if (!FilePatterns.Contains("*" + extension, StringComparer.OrdinalIgnoreCase))
                return new(null, null, "Select a music file supported by Wesnoth's audio decoder.");
            if (new FileInfo(source).Length == 0)
                return new(null, null, "The selected music file is empty.");
            var name = Path.GetFileName(source);
            if (name.IndexOfAny(['{', '}', '$', '\r', '\n']) >= 0)
                return new(null, null, "The music filename contains WML expression characters.");

            var musicRoot = Path.Combine(root, "music");
            var physicalRoot = ResolveDirectory(root);
            if (!IsInside(physicalRoot, ResolveDirectory(musicRoot)))
                return new(null, null, "The campaign music folder points outside its writable folder.");
            if (IsInside(musicRoot, source) && IsInside(physicalRoot, ResolveFile(source)))
                return Success(musicRoot, source, false);
            Directory.CreateDirectory(musicRoot);
            var stem = Path.GetFileNameWithoutExtension(name);
            for (var suffix = 1; ; suffix++)
            {
                var destination = Path.Combine(musicRoot, suffix == 1 ? name : $"{stem}_{suffix}{extension}");
                if (!IsInside(physicalRoot, ResolveFile(destination)))
                    return new(null, null, "The music destination points outside the writable campaign.");
                if (File.Exists(destination))
                {
                    if (SameBytes(source, destination)) return Success(musicRoot, destination, false);
                    continue;
                }
                var temporary = Path.Combine(musicRoot, ".wcc-music-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.Copy(source, temporary, overwrite: false);
                    try { File.Move(temporary, destination, overwrite: false); }
                    catch (IOException) when (File.Exists(destination)) { continue; }
                    return Success(musicRoot, destination, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or CryptographicException)
        {
            return new(null, null, "Could not import this music file: " + exception.Message);
        }
    }

    private static WccPackageMusicImportResult Success(string root, string destination, bool copied) =>
        new(Path.GetRelativePath(root, destination).Replace('\\', '/'), destination, null, copied);

    private static bool SameBytes(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var first = File.OpenRead(left);
        using var second = File.OpenRead(right);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(first), SHA256.HashData(second));
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ResolveDirectory(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target)
            return Path.GetFullPath(target.FullName);
        return info.Parent is null ? info.FullName : Path.Combine(ResolveDirectory(info.Parent.FullName), info.Name);
    }

    private static string ResolveFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target)
            return target.FullName;
        return Path.Combine(ResolveDirectory(info.DirectoryName!), info.Name);
    }
}
