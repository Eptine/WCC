using Wcc.Core.Presets;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Maps;

public sealed record WesnothTerrainVisualLayer(
    string TerrainCode,
    string? TerrainId,
    string? DisplayName,
    string? ImagePath,
    bool IsKnown);

public sealed record WesnothTerrainVisual(
    string TerrainCode,
    IReadOnlyList<WesnothTerrainVisualLayer> Layers,
    IReadOnlyList<string> UnresolvedCodes)
{
    public bool IsFullyResolved => UnresolvedCodes.Count == 0 &&
                                   Layers.Count > 0 &&
                                   Layers.All(x => !string.IsNullOrWhiteSpace(x.ImagePath));
}

/// <summary>
/// Resolves a terrain code to the simple editor/symbol tile images declared by
/// terrain_type. It intentionally does not execute terrain-graphics rules,
/// transitions, random variations, or adjacency-dependent composition.
/// </summary>
public sealed class WesnothTerrainVisualCatalog
{
    private readonly IReadOnlyDictionary<string, TerrainEntry> _entries;
    private readonly Dictionary<string, WesnothTerrainVisual> _resolved =
        new(StringComparer.Ordinal);

    private WesnothTerrainVisualCatalog(
        IReadOnlyDictionary<string, TerrainEntry> entries,
        string? problem)
    {
        _entries = entries;
        Problem = problem;
    }

    public string? Problem { get; }
    public int EntryCount => _entries.Count;

    public static WesnothTerrainVisualCatalog LoadCore(string? wesnothInstallPath)
    {
        if (string.IsNullOrWhiteSpace(wesnothInstallPath))
        {
            return new WesnothTerrainVisualCatalog(
                new Dictionary<string, TerrainEntry>(StringComparer.Ordinal),
                "Configure a Wesnoth installation to load Core terrain textures.");
        }

        var coreRoot = WccCoreInstallPaths.GetCoreDataRoot(wesnothInstallPath);
        var terrainConfigPath = Path.Combine(coreRoot, "terrain.cfg");
        var terrainImageRoot = Path.Combine(coreRoot, "images", "terrain");
        if (!File.Exists(terrainConfigPath))
        {
            return new WesnothTerrainVisualCatalog(
                new Dictionary<string, TerrainEntry>(StringComparer.Ordinal),
                $"Core terrain definitions were not found at '{terrainConfigPath}'.");
        }

        try
        {
            var presets = TerrainTypePresetImporter.ImportAllFromText(
                File.ReadAllText(terrainConfigPath));
            var entries = new Dictionary<string, TerrainEntry>(StringComparer.Ordinal);
            foreach (var preset in presets)
            {
                var code = NormalizeScalar(preset.StringCode);
                if (string.IsNullOrWhiteSpace(code) || entries.ContainsKey(code))
                    continue;

                var imageExpression = NormalizeScalar(preset.EditorImage) ??
                                      NormalizeScalar(preset.SymbolImage);
                entries[code] = new TerrainEntry(
                    code,
                    preset.Id,
                    WmlDisplayStringNormalizer.Normalize(preset.EditorName) ??
                    WmlDisplayStringNormalizer.Normalize(preset.Name),
                    ResolveTerrainImagePath(terrainImageRoot, imageExpression),
                    NormalizeScalar(preset.DefaultBase));
            }

            return new WesnothTerrainVisualCatalog(entries, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new WesnothTerrainVisualCatalog(
                new Dictionary<string, TerrainEntry>(StringComparer.Ordinal),
                $"Core terrain definitions could not be loaded: {ex.Message}");
        }
    }

    public WesnothTerrainVisual Resolve(string terrainCode)
    {
        if (terrainCode is null) throw new ArgumentNullException(nameof(terrainCode));
        if (_resolved.TryGetValue(terrainCode, out var cached))
            return cached;

        var layers = new List<WesnothTerrainVisualLayer>();
        var unresolved = new List<string>();
        ResolveInto(terrainCode.Trim(), layers, unresolved, new HashSet<string>(StringComparer.Ordinal));
        var visual = new WesnothTerrainVisual(terrainCode, layers, unresolved);
        _resolved[terrainCode] = visual;
        return visual;
    }

    private void ResolveInto(
        string code,
        List<WesnothTerrainVisualLayer> layers,
        List<string> unresolved,
        HashSet<string> activeCodes)
    {
        if (code.Length == 0 || !activeCodes.Add(code))
        {
            AddUnresolved(unresolved, code.Length == 0 ? "(empty)" : code);
            return;
        }

        try
        {
            if (_entries.TryGetValue(code, out var exact))
            {
                if (code.StartsWith('^') &&
                    !string.IsNullOrWhiteSpace(exact.DefaultBase) &&
                    layers.Count == 0)
                {
                    ResolveInto(exact.DefaultBase!, layers, unresolved, activeCodes);
                }

                AddEntryLayer(exact, layers, unresolved);
                return;
            }

            var overlayIndex = code.IndexOf('^');
            if (overlayIndex > 0)
            {
                ResolveInto(code[..overlayIndex], layers, unresolved, activeCodes);
                ResolveInto(code[overlayIndex..], layers, unresolved, activeCodes);
                return;
            }

            AddUnresolved(unresolved, code);
        }
        finally
        {
            activeCodes.Remove(code);
        }
    }

    private static void AddEntryLayer(
        TerrainEntry entry,
        List<WesnothTerrainVisualLayer> layers,
        List<string> unresolved)
    {
        layers.Add(new WesnothTerrainVisualLayer(
            entry.Code,
            entry.Id,
            entry.DisplayName,
            entry.ImagePath,
            IsKnown: true));
        if (string.IsNullOrWhiteSpace(entry.ImagePath))
            AddUnresolved(unresolved, entry.Code);
    }

    private static void AddUnresolved(List<string> unresolved, string code)
    {
        if (!unresolved.Contains(code, StringComparer.Ordinal))
            unresolved.Add(code);
    }

    private static string? ResolveTerrainImagePath(string root, string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Contains('{'))
            return null;

        var logicalPath = expression.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (!Path.HasExtension(logicalPath))
            logicalPath += ".png";

        var fullPath = Path.GetFullPath(Path.Combine(root, logicalPath));
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(fullPath) ? fullPath : null;
    }

    private static string? NormalizeScalar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private sealed record TerrainEntry(
        string Code,
        string Id,
        string? DisplayName,
        string? ImagePath,
        string? DefaultBase);
}
