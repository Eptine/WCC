namespace Wcc.Core.Library;

public sealed class WccCatalogQuery
{
    public required WccEntityTypeId EntityType { get; init; }
    public WccPackageId? PackageId { get; init; }
    public IReadOnlySet<WccPackageRevisionId>? VisibleRevisionIds { get; init; }
    public string? SearchText { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 200;
}

public sealed class WccCatalogItem
{
    public required WccEntityId EntityId { get; init; }
    public required WccSourceFileId SourceFileId { get; init; }
    public required WccEntityTypeId EntityType { get; init; }
    public WccPackageId? PackageId { get; init; }
    public WccPackageRevisionId? PackageRevisionId { get; init; }
    public string? ExportSymbol { get; init; }
    public required string DisplayName { get; init; }
    public required string PackageDisplayName { get; init; }
    public required string SourceRelativePath { get; init; }
    public IReadOnlyDictionary<string, string> Summary { get; init; } =
        new Dictionary<string, string>();
}

public sealed class WccCatalogPage
{
    public required IReadOnlyList<WccCatalogItem> Items { get; init; }
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

/// <summary>
/// Lightweight catalog queries over a snapshot. These results do not parse full WML
/// documents or resolve previews, which keeps pickers and inspectors inexpensive.
/// </summary>
public static class WccLibraryCatalogProjection
{
    public static WccCatalogPage Query(WccLibrarySnapshot snapshot, WccCatalogQuery query)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (query.Skip < 0) throw new ArgumentOutOfRangeException(nameof(query.Skip));
        if (query.Take <= 0) throw new ArgumentOutOfRangeException(nameof(query.Take));

        var search = query.SearchText?.Trim();
        var candidates = snapshot.FindEntities(query.EntityType)
            .Select(entity => CreateItem(snapshot, entity))
            .Where(item => item is not null)
            .Cast<WccCatalogItem>();

        if (query.PackageId is not null)
            candidates = candidates.Where(x => x.PackageId == query.PackageId);

        if (query.VisibleRevisionIds is not null)
        {
            candidates = candidates.Where(x =>
                x.PackageRevisionId is { } revisionId &&
                query.VisibleRevisionIds.Contains(revisionId));
        }

        if (!string.IsNullOrWhiteSpace(search))
            candidates = candidates.Where(x => MatchesSearch(x, search));

        var ordered = candidates
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ExportSymbol, StringComparer.Ordinal)
            .ThenBy(x => x.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceRelativePath, StringComparer.Ordinal)
            .ToList();

        return new WccCatalogPage
        {
            Items = ordered.Skip(query.Skip).Take(query.Take).ToArray(),
            TotalCount = ordered.Count,
            Skip = query.Skip,
            Take = query.Take
        };
    }

    private static WccCatalogItem? CreateItem(
        WccLibrarySnapshot snapshot,
        WccEntityRecord entity)
    {
        if (!snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile))
            return null;

        snapshot.RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out var revision);
        WccPackageRecord? package = null;
        if (revision is not null)
            snapshot.PackagesById.TryGetValue(revision.PackageId, out package);

        return new WccCatalogItem
        {
            EntityId = entity.Id,
            SourceFileId = entity.SourceFileId,
            EntityType = entity.EntityType,
            PackageId = package?.Id,
            PackageRevisionId = revision?.Id,
            ExportSymbol = entity.ExportSymbol,
            DisplayName = entity.DisplayName ?? entity.ExportSymbol ?? "(unnamed)",
            PackageDisplayName = package?.Name ?? "(unknown package)",
            SourceRelativePath = sourceFile.RelativePath,
            Summary = entity.Summary
        };
    }

    private static bool MatchesSearch(WccCatalogItem item, string search)
    {
        if (item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (item.ExportSymbol?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            item.PackageDisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.SourceRelativePath.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return item.Summary.Any(x =>
            x.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            x.Value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class WccEntitySourceContent
{
    public required WccEntityId EntityId { get; init; }
    public required WccSourceFileRecord SourceFile { get; init; }
    public required string SourceFileText { get; init; }
    public required string EntityRawText { get; init; }
    public bool MatchesIndexedContentHash { get; init; }
}

/// <summary>
/// Loads detailed source only when an editor/inspector opens an entity.
/// Catalog browsing therefore does not retain every parsed WML document in memory.
/// </summary>
public static class WccLibraryEntityContentLoader
{
    public static async Task<WccEntitySourceContent> LoadAsync(
        WccLibrarySnapshot snapshot,
        WccEntityId entityId,
        CancellationToken cancellationToken = default)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (!snapshot.EntitiesById.TryGetValue(entityId, out var entity))
            throw new KeyNotFoundException($"Library entity was not found: {entityId}");
        if (!snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile))
            throw new InvalidDataException($"Entity {entityId} refers to a missing source file.");
        if (string.IsNullOrWhiteSpace(sourceFile.StoragePath))
            throw new FileNotFoundException("The source file has no available storage path.");

        var sourceText = await File
            .ReadAllTextAsync(sourceFile.StoragePath, cancellationToken)
            .ConfigureAwait(false);
        var currentHash = WccContentHash.ComputeText(sourceText);
        var rawEntityText = TrySliceEntityText(sourceText, entity.SourceSpan) ?? sourceText;

        return new WccEntitySourceContent
        {
            EntityId = entityId,
            SourceFile = sourceFile,
            SourceFileText = sourceText,
            EntityRawText = rawEntityText,
            MatchesIndexedContentHash = string.Equals(
                currentHash,
                sourceFile.ContentHash,
                StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string? TrySliceEntityText(string sourceText, WccSourceSpan? span)
    {
        if (span?.StartOffset is not int start || span.EndOffsetExclusive is not int end)
            return null;
        if (start < 0 || end < start || end > sourceText.Length)
            return null;

        return sourceText[start..end];
    }
}
