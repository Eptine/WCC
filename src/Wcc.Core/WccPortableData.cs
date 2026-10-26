using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wcc.Core;

/// <summary>
/// Portable state belongs to the executable directory, never the working directory.
/// Existing data is used in place; no previous installation or user-data directory is
/// imported automatically. Moving a portable folder rebases its owned manifest paths.
/// </summary>
public static class WccPortableData
{
    private const string LocationFile = ".wcc-location.json";

    public static void InitializeDefault()
    {
        Initialize(WccAppPaths.GetApplicationRoot());
    }

    public static void Initialize(string applicationDirectory)
    {
        var app = Path.GetFullPath(applicationDirectory);
        var library = Path.Combine(app, "Library");
        EnsureWritable(app);
        Directory.CreateDirectory(library);
        RejectLink(library);
        var locationPath = Path.Combine(library, LocationFile);
        if (File.Exists(locationPath))
        {
            RejectLink(locationPath);
            var location = JsonSerializer.Deserialize<Location>(File.ReadAllText(locationPath))
                ?? throw new InvalidDataException("The portable Library location is empty.");
            if (location.FormatVersion != 1 || string.IsNullOrWhiteSpace(location.LibraryRoot))
                throw new InvalidDataException("The portable Library location is unsupported.");
            if (!PathsEqual(location.LibraryRoot, library))
                RebaseManifest(library, location.LibraryRoot, library);
        }
        WriteLocation(library, library);
    }

    private static void RebaseManifest(string library, string previousRoot, string currentRoot)
    {
        var manifest = Path.Combine(library, "index-v1", "manifest.json");
        if (!File.Exists(manifest)) return;
        RejectLink(Path.GetDirectoryName(manifest)!);
        RejectLink(manifest);
        var document = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject
            ?? throw new InvalidDataException("The Library manifest must be an object.");
        var changed = false;
        foreach (var (arrayName, property) in new[] { ("packages", "sourceLocation"), ("sourceFiles", "storagePath") })
        {
            if (document[arrayName] is not JsonArray rows) continue;
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (row[property] is not JsonValue value || !value.TryGetValue<string>(out var path)) continue;
                var rebased = RebasePath(path, previousRoot, currentRoot);
                if (rebased == path) continue;
                row[property] = rebased;
                changed = true;
            }
        }
        if (!changed) return;
        // Old analysis may contain absolute diagnostic/resource paths. Preserve it as
        // a backup, but force the ordinary indexer to regenerate it at the new root.
        var analysis = Path.Combine(library, "index-v1", "analysis");
        if (Directory.Exists(analysis))
        {
            RejectLink(analysis);
            Directory.Move(analysis, analysis + ".before-relocation-" + Guid.NewGuid().ToString("N"));
        }
        WriteAtomically(manifest, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string RebasePath(string path, string previousRoot, string currentRoot)
    {
        // Serialized Windows paths can be moved to Linux. Compare separators here only;
        // WML source and unrelated external installation/project paths are never rewritten.
        var oldRoot = previousRoot.Replace('\\', '/').TrimEnd('/');
        var candidate = path.Replace('\\', '/');
        var comparison = previousRoot.Contains('\\') || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (candidate.Equals(oldRoot, comparison)) return currentRoot;
        if (!candidate.StartsWith(oldRoot + "/", comparison)) return path;
        return Path.Combine(currentRoot, candidate[(oldRoot.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static void EnsureWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        var probe = Path.Combine(directory, $".wcc-write-check-{Guid.NewGuid():N}.tmp");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        File.Delete(probe);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Portable data cannot follow a symbolic link: {path}");
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void WriteLocation(string library, string root) =>
        WriteAtomically(Path.Combine(library, LocationFile), JsonSerializer.Serialize(new Location(1, root)));

    private static void WriteAtomically(string path, string text)
    {
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(staging, text);
        File.Move(staging, path, overwrite: true);
    }

    private sealed record Location(int FormatVersion, string LibraryRoot);
}
