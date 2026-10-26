using Wcc.Core.Wml;

namespace Wcc.Core.Library;

/// <summary>
/// Lazily materializes only the macro definitions requested by a central macro
/// expansion operation. Resolution follows the current source revision first
/// and then its pinned dependency levels, matching the package reference model.
/// This supplies editor previews; it does not prove runtime activation or edit authority.
/// </summary>
public sealed class WccLibraryMacroExpansionDefinitionResolver : IWmlMacroExpansionDefinitionResolver
{
    private readonly WccLibrarySnapshot _snapshot;
    private readonly IReadOnlySet<WccPackageRevisionId>? _visibleRevisionIds;
    private readonly WccPackageRevisionId? _sourceRevisionId;
    private readonly Dictionary<WccSourceFileId, string?> _sourceTextCache = new();
    private readonly Dictionary<string, IReadOnlyList<WmlMacroExpansionDefinition>> _definitionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WccSourceFileId, IReadOnlyList<WmlMacroDefinition>> _fileDefinitionCache = new();
    private readonly IReadOnlyList<IReadOnlySet<WccPackageRevisionId>> _revisionLevels;

    public WccLibraryMacroExpansionDefinitionResolver(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId>? visibleRevisionIds = null,
        string? currentSourcePath = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _visibleRevisionIds = visibleRevisionIds;
        _sourceRevisionId = FindSourceRevision(currentSourcePath) ?? FindVisibleRootRevision();
        _revisionLevels = _sourceRevisionId is { } sourceRevision
            ? BuildRevisionLevels(sourceRevision)
            : [];
    }

    public IReadOnlyList<WmlMacroExpansionDefinition> FindDefinitions(string macroName)
    {
        if (string.IsNullOrWhiteSpace(macroName))
            return Array.Empty<WmlMacroExpansionDefinition>();
        if (_definitionCache.TryGetValue(macroName, out var cached))
            return cached;

        var candidates = _snapshot
            .FindDefinitionsAcrossPackages(WccEntityTypes.MacroDefinition, macroName)
            .Where(IsVisible)
            .ToArray();
        var selected = SelectNearestCandidates(candidates);
        var result = selected
            .Select(TryLoadDefinition)
            .Where(x => x is not null)
            .Cast<WmlMacroExpansionDefinition>()
            .ToArray();
        _definitionCache[macroName] = result;
        return result;
    }

    private IReadOnlyList<WccEntityRecord> SelectNearestCandidates(
        IReadOnlyList<WccEntityRecord> candidates)
    {
        if (candidates.Count <= 1 || _sourceRevisionId is null)
            return candidates;

        foreach (var level in _revisionLevels)
        {
            var matches = candidates
                .Where(entity =>
                    _snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) &&
                    level.Contains(file.PackageRevisionId))
                .ToArray();
            if (matches.Length > 0)
                return matches;
        }

        return Array.Empty<WccEntityRecord>();
    }

    private IReadOnlyList<IReadOnlySet<WccPackageRevisionId>> BuildRevisionLevels(
        WccPackageRevisionId rootRevisionId)
    {
        var result = new List<IReadOnlySet<WccPackageRevisionId>>();
        var visited = new HashSet<WccPackageRevisionId>();
        var level = new[] { rootRevisionId };
        while (level.Length > 0)
        {
            var current = level.Where(visited.Add).ToArray();
            if (current.Length == 0)
                break;

            result.Add(current.ToHashSet());
            level = current
                .SelectMany(id => _snapshot.RevisionsById.TryGetValue(id, out var revision)
                    ? revision.DependencyRevisionIds
                    : Enumerable.Empty<WccPackageRevisionId>())
                .ToArray();
        }
        return result;
    }

    private WmlMacroExpansionDefinition? TryLoadDefinition(WccEntityRecord entity)
    {
        if (!_snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile))
            return null;

        var sourceText = ReadSourceText(sourceFile);
        if (sourceText is null)
            return null;

        var raw = ReadEntityRawText(sourceText, entity.SourceSpan);
        var parsed = WmlMacroDefinitionScanner.Scan(raw)
            .FirstOrDefault(x => string.Equals(x.Name, entity.ExportSymbol, StringComparison.OrdinalIgnoreCase));
        if (parsed is null)
        {
            parsed = GetFileDefinitions(sourceFile, sourceText)
                .Where(x => string.Equals(x.Name, entity.ExportSymbol, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(x => entity.SourceSpan?.StartLine is not { } startLine ||
                                     x.StartLineIndex == startLine);
        }
        if (parsed is null)
            return null;

        _snapshot.RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out var revision);
        WccPackageRecord? package = null;
        if (revision is not null)
            _snapshot.PackagesById.TryGetValue(revision.PackageId, out package);

        return new WmlMacroExpansionDefinition(
            parsed.Name,
            parsed.RawText,
            sourceFile.StoragePath ?? sourceFile.RelativePath,
            package?.Name,
            sourceFile.PackageRevisionId.ToString(),
            entity.SourceSpan?.StartLine);
    }

    private IReadOnlyList<WmlMacroDefinition> GetFileDefinitions(
        WccSourceFileRecord sourceFile,
        string sourceText)
    {
        if (_fileDefinitionCache.TryGetValue(sourceFile.Id, out var cached))
            return cached;
        var definitions = WmlMacroDefinitionScanner.Scan(sourceText);
        _fileDefinitionCache[sourceFile.Id] = definitions;
        return definitions;
    }

    private string? ReadSourceText(WccSourceFileRecord sourceFile)
    {
        if (_sourceTextCache.TryGetValue(sourceFile.Id, out var cached))
            return cached;

        string? result = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(sourceFile.StoragePath) &&
                File.Exists(sourceFile.StoragePath))
            {
                result = File.ReadAllText(sourceFile.StoragePath);
            }
        }
        catch (IOException)
        {
            result = null;
        }
        catch (UnauthorizedAccessException)
        {
            result = null;
        }

        _sourceTextCache[sourceFile.Id] = result;
        return result;
    }

    private static string ReadEntityRawText(string sourceText, WccSourceSpan? span)
    {
        if (span?.StartOffset is { } start &&
            span.EndOffsetExclusive is { } end &&
            start >= 0 &&
            end >= start &&
            end <= sourceText.Length)
        {
            return sourceText[start..end];
        }

        return sourceText;
    }

    private bool IsVisible(WccEntityRecord entity)
        => _visibleRevisionIds is null ||
           (_snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) &&
            _visibleRevisionIds.Contains(file.PackageRevisionId));

    private WccPackageRevisionId? FindSourceRevision(string? currentSourcePath)
    {
        if (string.IsNullOrWhiteSpace(currentSourcePath))
            return null;

        return _snapshot.SourceFilesById.Values
            .Where(x => (_visibleRevisionIds is null || _visibleRevisionIds.Contains(x.PackageRevisionId))
                && PathsEqual(x.StoragePath, currentSourcePath))
            .Select(x => (WccPackageRevisionId?)x.PackageRevisionId)
            .FirstOrDefault();
    }

    private WccPackageRevisionId? FindVisibleRootRevision()
    {
        if (_visibleRevisionIds is null || _visibleRevisionIds.Count == 0)
            return null;

        var dependencyIds = _visibleRevisionIds
            .SelectMany(id => _snapshot.RevisionsById.TryGetValue(id, out var revision)
                ? revision.DependencyRevisionIds
                : Enumerable.Empty<WccPackageRevisionId>())
            .ToHashSet();
        var roots = _visibleRevisionIds.Where(x => !dependencyIds.Contains(x)).ToArray();
        return roots.Length == 1 ? roots[0] : null;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
