using System.Text;
using Wcc.Core.Library;

namespace Wcc.Core.Wml;

/// <summary>
/// Resolves includes against an immutable, explicit inventory, without inspecting the filesystem.
/// A complete inventory contains every file and directory beneath the supplied data/user roots;
/// directoryPaths can represent empty directories, since these cannot be inferred from files.
/// Path casing is significant on every platform. This is source evidence, not write authority.
/// </summary>
public sealed class WmlStaticSourceSet : IWmlStaticIncludeResolver
{
    private const int MaxInventoryEntries = 262144;
    private const int MaxDirectoryDepth = 128;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _caseAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _dataRoot;
    private readonly string? _userRoot;
    private readonly bool _isInventoryComplete;
    private readonly string _inventoryIdentity;

    public WmlStaticSourceSet(
        IEnumerable<WmlStaticSource> sources,
        string? dataRoot = null,
        string? userRoot = null,
        bool isInventoryComplete = false,
        IEnumerable<string>? directoryPaths = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _dataRoot = dataRoot is null ? null : Normalize(dataRoot);
        _userRoot = userRoot is null ? null : Normalize(userRoot);
        _isInventoryComplete = isInventoryComplete;
        var suppliedCount = 0;
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            CheckCount(++suppliedCount);
            var entry = GetOrAdd(Normalize(source.Path));
            if (!entry.Sources.Any(existing => existing.ContentHash == source.ContentHash))
                entry.Sources.Add(source);
            AddParents(entry);
        }

        foreach (var path in directoryPaths ?? [])
        {
            CheckCount(++suppliedCount);
            AddDirectory(Normalize(path));
        }
        if (_dataRoot is not null) AddDirectory(_dataRoot);
        if (_userRoot is not null) AddDirectory(_userRoot);

        var identity = new StringBuilder("wml-static-inventory-v1;");
        Append(identity, _dataRoot ?? "");
        Append(identity, _userRoot ?? "");
        Append(identity, _isInventoryComplete ? "complete" : "partial");
        foreach (var entry in _entries.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            Append(identity, entry.Path);
            Append(identity, entry.IsDirectory ? "directory" : "file");
            foreach (var source in entry.Sources.OrderBy(source => source.ContentHash, StringComparer.Ordinal))
                Append(identity, source.ContentHash);
        }
        _inventoryIdentity = WccContentHash.ComputeText(identity.ToString());
    }

    public WmlStaticIncludeResolution Resolve(string includePath, WmlStaticSource includingSource)
    {
        ArgumentNullException.ThrowIfNull(includingSource);
        if (!TryResolvePath(includePath, includingSource, out var resolvedPath))
            return Result(WmlStaticTruth.Unknown, includePath ?? "", "", [],
                "The include path is dynamic, unsupported, or has no declared source root.");

        if (HasAmbiguousPath(resolvedPath))
            return Result(WmlStaticTruth.Unknown, includePath, resolvedPath, [],
                "The source inventory has conflicting identities or ambiguous path casing.");
        if (!_entries.TryGetValue(resolvedPath, out var entry))
            return Result(IsCompleteAt(resolvedPath) ? WmlStaticTruth.False : WmlStaticTruth.Unknown,
                includePath, resolvedPath, [], IsCompleteAt(resolvedPath) ? null :
                    "The partial inventory cannot prove that this include is absent.");
        if (entry.Sources.Count > 1 || (entry.IsDirectory && entry.Sources.Count > 0))
            return Result(WmlStaticTruth.Unknown, includePath, resolvedPath, [],
                "The source inventory contains conflicting revisions or file/directory identities.");
        if (!entry.IsDirectory && includePath.EndsWith('/'))
            return Result(WmlStaticTruth.Unknown, includePath, resolvedPath, [],
                "A directory include path refers to a file in the source inventory.");
        if (!entry.IsDirectory)
            return Result(WmlStaticTruth.True, includePath, resolvedPath, entry.Sources, null);

        var ordered = new List<WmlStaticSource>();
        if (!TryExpandDirectory(entry, ordered, 0, out var diagnostic))
            return Result(WmlStaticTruth.Unknown, includePath, resolvedPath, [], diagnostic);
        return Result(WmlStaticTruth.True, includePath, resolvedPath, ordered, null);
    }

    private bool TryExpandDirectory(Entry directory, List<WmlStaticSource> ordered, int depth,
        out string? diagnostic)
    {
        diagnostic = null;
        if (depth >= MaxDirectoryDepth)
        {
            diagnostic = "The include directory nesting exceeds the supported bound.";
            return false;
        }

        var mainPath = Path.Combine(directory.Path, "_main.cfg");
        if (HasAmbiguousPath(mainPath))
        {
            diagnostic = "The include directory has ambiguous or mismatched package entry casing.";
            return false;
        }
        if (_entries.TryGetValue(mainPath, out var main) && !main.IsDirectory)
        {
            if (main.Sources.Count != 1)
            {
                diagnostic = "The package entry has conflicting source revisions.";
                return false;
            }
            ordered.Add(main.Sources[0]);
            return true;
        }
        if (!IsCompleteAt(directory.Path))
        {
            diagnostic = "The partial inventory cannot establish the complete directory include order.";
            return false;
        }

        // An included directory contributes immediate cfg files and immediate child
        // package _main.cfg files. Ordinary subdirectories require an explicit include.
        var files = new List<Entry>();
        foreach (var child in directory.Children.Values)
        {
            var name = Path.GetFileName(child.Path);
            if (name.StartsWith('.') || child.IsDirectory && name is "images" or "sounds") continue;
            if (HasAmbiguousPath(child.Path) || child.Sources.Count > 1 ||
                (child.IsDirectory && child.Sources.Count > 0))
            {
                diagnostic = "The include directory contains ambiguous source identities.";
                return false;
            }
            if (child.IsDirectory)
            {
                var childMainPath = Path.Combine(child.Path, "_main.cfg");
                if (HasAmbiguousPath(childMainPath))
                {
                    diagnostic = "The child package entry has ambiguous casing.";
                    return false;
                }
                if (!_entries.TryGetValue(childMainPath, out var childMain)) continue;
                if (childMain.IsDirectory || childMain.Sources.Count != 1)
                {
                    diagnostic = "The child package entry has ambiguous source identities.";
                    return false;
                }
                files.Add(childMain);
            }
            else if (child.Path.EndsWith(".cfg", StringComparison.Ordinal)) files.Add(child);
        }
        // Wesnoth sorts generic paths: '/' must compare after '.' and before '0'
        // on Windows too (for example faeries.cfg, faeries/_main.cfg, faeries0.cfg).
        foreach (var file in files.OrderBy(file => Rank(Path.GetFileName(file.Path)))
                     .ThenBy(file => file.Path.Replace('\\', '/'), StringComparer.Ordinal))
            ordered.Add(file.Sources[0]);
        return true;
    }

    private static int Rank(string name) => name switch { "_initial.cfg" => -1, "_final.cfg" => 1, _ => 0 };

    private bool TryResolvePath(string? includePath, WmlStaticSource caller, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(includePath) || includePath.Any(char.IsWhiteSpace) ||
            includePath.IndexOfAny(['\\', '{', '}', '$', '*', '?', '[', ']', '(', ')', ':', '"', '\'', '|', '<', '>', '#']) >= 0)
            return false;

        string? root;
        string relative;
        if (includePath.StartsWith("./", StringComparison.Ordinal))
        {
            root = Path.GetDirectoryName(caller.Path);
            relative = includePath[2..];
        }
        else if (includePath.StartsWith('~'))
        {
            root = _userRoot;
            relative = includePath[1..];
        }
        else
        {
            root = _dataRoot;
            relative = includePath;
        }
        if (root is null || relative.Length == 0 || relative.Contains('~') ||
            relative.Contains("..", StringComparison.Ordinal)) return false;
        if (relative.EndsWith('/')) relative = relative[..^1];
        var parts = relative.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) return false;
        try
        {
            path = Normalize(Path.Combine(root, Path.Combine(parts)));
            return Contains(root, path);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private bool IsCompleteAt(string path) => _isInventoryComplete &&
        ((_dataRoot is not null && Contains(_dataRoot, path)) ||
         (_userRoot is not null && Contains(_userRoot, path)));

    private bool HasAmbiguousPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (_caseAliases.TryGetValue(current, out var aliases) &&
                (aliases.Count != 1 || !aliases.Contains(current))) return true;
            if (_entries.TryGetValue(current, out var entry) &&
                (entry.Sources.Count > 1 || (entry.IsDirectory && entry.Sources.Count > 0))) return true;
        }
        return false;
    }

    private WmlStaticIncludeResolution Result(WmlStaticTruth exists, string requestedPath, string resolvedPath,
        IEnumerable<WmlStaticSource> sources, string? diagnostic)
    {
        var snapshot = Array.AsReadOnly(sources.ToArray());
        var identity = new StringBuilder(_inventoryIdentity);
        Append(identity, requestedPath);
        Append(identity, resolvedPath);
        Append(identity, exists.ToString());
        foreach (var source in snapshot)
        {
            Append(identity, source.Path);
            Append(identity, source.ContentHash);
        }
        return new(exists, snapshot, WccContentHash.ComputeText(identity.ToString()), diagnostic);
    }

    private Entry GetOrAdd(string path)
    {
        if (_entries.TryGetValue(path, out var existing)) return existing;
        CheckCount(_entries.Count + 1);
        var entry = new Entry(path);
        _entries.Add(path, entry);
        if (!_caseAliases.TryGetValue(path, out var aliases))
            _caseAliases.Add(path, aliases = new HashSet<string>(StringComparer.Ordinal));
        aliases.Add(path);
        return entry;
    }

    private void AddDirectory(string path)
    {
        var entry = GetOrAdd(path);
        entry.IsDirectory = true;
        AddParents(entry);
    }

    private void AddParents(Entry child)
    {
        for (var parentPath = Path.GetDirectoryName(child.Path); parentPath is not null;
             parentPath = Path.GetDirectoryName(child.Path))
        {
            var parent = GetOrAdd(parentPath);
            parent.IsDirectory = true;
            parent.Children[child.Path] = child;
            child = parent;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Contains(string root, string path) => path.Equals(root, StringComparison.Ordinal) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
    private static void Append(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value);
    private static void CheckCount(int count)
    {
        if (count > MaxInventoryEntries)
            throw new ArgumentException("The static source inventory exceeds the supported entry bound.");
    }

    private sealed class Entry(string path)
    {
        public string Path { get; } = path;
        public bool IsDirectory { get; set; }
        public List<WmlStaticSource> Sources { get; } = [];
        public Dictionary<string, Entry> Children { get; } = new(StringComparer.Ordinal);
    }
}
