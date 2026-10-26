using System.Collections.Frozen;

namespace Wcc.Core.Library;

public readonly record struct WccScopedSymbolKey(
    WccPackageRevisionId PackageRevisionId,
    WccEntityTypeId EntityType,
    string Symbol);

public readonly record struct WccUnscopedSymbolKey(
    WccEntityTypeId EntityType,
    string Symbol);

/// <summary>
/// Immutable-by-public-contract query snapshot built from a manifest and cached analyses.
/// A refresh builds a new instance off-thread, then the application can replace the old
/// snapshot in one UI-thread operation.
/// </summary>
public sealed class WccLibrarySnapshot
{
    internal WccLibrarySnapshot(
        WccLibraryManifest manifest,
        FrozenDictionary<WccPackageId, WccPackageRecord> packagesById,
        FrozenDictionary<WccPackageRevisionId, WccPackageRevisionRecord> revisionsById,
        FrozenDictionary<WccSourceFileId, WccSourceFileRecord> sourceFilesById,
        FrozenDictionary<WccEntityId, WccEntityRecord> entitiesById,
        FrozenDictionary<WccReferenceId, WccReferenceRecord> referencesById,
        FrozenDictionary<WccEntityTypeId, WccEntityRecord[]> entitiesByType,
        FrozenDictionary<WccScopedSymbolKey, WccEntityRecord[]> definitionsByScopedSymbol,
        FrozenDictionary<WccUnscopedSymbolKey, WccEntityRecord[]> definitionsByUnscopedSymbol,
        FrozenDictionary<WccEntityId, WccReferenceRecord[]> outgoingReferences,
        FrozenDictionary<WccEntityId, WccReferenceRecord[]> incomingReferences,
        FrozenDictionary<WccSourceFileId, WccReferenceRecord[]> fileReferences,
        IReadOnlyList<WccDiagnosticRecord> diagnostics)
    {
        Manifest = manifest;
        PackagesById = packagesById;
        RevisionsById = revisionsById;
        SourceFilesById = sourceFilesById;
        EntitiesById = entitiesById;
        ReferencesById = referencesById;
        _entitiesByType = entitiesByType;
        _definitionsByScopedSymbol = definitionsByScopedSymbol;
        _definitionsByUnscopedSymbol = definitionsByUnscopedSymbol;
        _outgoingReferences = outgoingReferences;
        _incomingReferences = incomingReferences;
        _fileReferences = fileReferences;
        Diagnostics = diagnostics;
    }

    private readonly FrozenDictionary<WccEntityTypeId, WccEntityRecord[]> _entitiesByType;
    private readonly FrozenDictionary<WccScopedSymbolKey, WccEntityRecord[]> _definitionsByScopedSymbol;
    private readonly FrozenDictionary<WccUnscopedSymbolKey, WccEntityRecord[]> _definitionsByUnscopedSymbol;
    private readonly FrozenDictionary<WccEntityId, WccReferenceRecord[]> _outgoingReferences;
    private readonly FrozenDictionary<WccEntityId, WccReferenceRecord[]> _incomingReferences;
    private readonly FrozenDictionary<WccSourceFileId, WccReferenceRecord[]> _fileReferences;

    public WccLibraryManifest Manifest { get; }
    public FrozenDictionary<WccPackageId, WccPackageRecord> PackagesById { get; }
    public FrozenDictionary<WccPackageRevisionId, WccPackageRevisionRecord> RevisionsById { get; }
    public FrozenDictionary<WccSourceFileId, WccSourceFileRecord> SourceFilesById { get; }
    public FrozenDictionary<WccEntityId, WccEntityRecord> EntitiesById { get; }
    public FrozenDictionary<WccReferenceId, WccReferenceRecord> ReferencesById { get; }
    public IReadOnlyList<WccDiagnosticRecord> Diagnostics { get; }

    public IReadOnlyList<WccEntityRecord> FindEntities(WccEntityTypeId entityType)
        => _entitiesByType.TryGetValue(entityType, out var entries)
            ? entries
            : Array.Empty<WccEntityRecord>();

    public IReadOnlyList<WccEntityRecord> FindDefinitions(
        WccPackageRevisionId packageRevisionId,
        WccEntityTypeId entityType,
        string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Array.Empty<WccEntityRecord>();

        var key = new WccScopedSymbolKey(packageRevisionId, entityType, symbol);
        return _definitionsByScopedSymbol.TryGetValue(key, out var entries)
            ? entries
            : Array.Empty<WccEntityRecord>();
    }

    public IReadOnlyList<WccEntityRecord> FindDefinitionsAcrossPackages(
        WccEntityTypeId entityType,
        string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Array.Empty<WccEntityRecord>();

        var key = new WccUnscopedSymbolKey(entityType, symbol);
        return _definitionsByUnscopedSymbol.TryGetValue(key, out var entries)
            ? entries
            : Array.Empty<WccEntityRecord>();
    }

    public IReadOnlyList<WccReferenceRecord> GetOutgoingReferences(WccEntityId entityId)
        => _outgoingReferences.TryGetValue(entityId, out var entries)
            ? entries
            : Array.Empty<WccReferenceRecord>();

    public IReadOnlyList<WccReferenceRecord> GetIncomingReferences(WccEntityId entityId)
        => _incomingReferences.TryGetValue(entityId, out var entries)
            ? entries
            : Array.Empty<WccReferenceRecord>();

    public IReadOnlyList<WccReferenceRecord> GetFileReferences(WccSourceFileId sourceFileId)
        => _fileReferences.TryGetValue(sourceFileId, out var entries)
            ? entries
            : Array.Empty<WccReferenceRecord>();

    public WccPackageRevisionRecord? FindRevisionForEntity(WccEntityId entityId)
    {
        if (!EntitiesById.TryGetValue(entityId, out var entity) ||
            !SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile) ||
            !RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out var revision))
        {
            return null;
        }

        return revision;
    }

    public WccPackageRecord? FindPackageForEntity(WccEntityId entityId)
    {
        var revision = FindRevisionForEntity(entityId);
        if (revision is null || !PackagesById.TryGetValue(revision.PackageId, out var package))
            return null;

        return package;
    }
}

public static class WccLibrarySnapshotBuilder
{
    public static WccLibrarySnapshot Build(
        WccLibraryManifest manifest,
        IEnumerable<WccFileAnalysis> analyses,
        IEnumerable<WccDiagnosticRecord>? additionalDiagnostics = null)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (analyses is null) throw new ArgumentNullException(nameof(analyses));

        var diagnostics = additionalDiagnostics?.ToList() ?? new List<WccDiagnosticRecord>();
        var packages = BuildUniqueDictionary(
            manifest.Packages,
            x => x.Id,
            "library.duplicate_package_id",
            diagnostics);
        foreach (var package in packages.Values.Where(x => !x.IsAvailable))
        {
            diagnostics.Add(new WccDiagnosticRecord
            {
                Code = "library.package_source_unavailable",
                Message = $"Package '{package.Name}' is retained for an immutable revision pin, but its source is not currently available.",
                Severity = WccDiagnosticSeverity.Information,
                PackageId = package.Id,
                PackageRevisionId = package.CurrentRevisionId
            });
        }
        var revisions = BuildUniqueDictionary(
            manifest.PackageRevisions,
            x => x.Id,
            "library.duplicate_revision_id",
            diagnostics);
        foreach (var package in packages.Values.Where(x => x.CurrentRevisionId is not null))
        {
            var currentRevisionId = package.CurrentRevisionId!.Value;
            if (!revisions.TryGetValue(currentRevisionId, out var currentRevision) ||
                currentRevision.PackageId != package.Id)
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "library.invalid_current_revision",
                    Message = $"Package '{package.Name}' selects revision {currentRevisionId}, but that revision is unavailable or belongs to another package.",
                    Severity = WccDiagnosticSeverity.Error,
                    PackageId = package.Id,
                    PackageRevisionId = currentRevisionId
                });
            }
        }
        var sourceFiles = BuildUniqueDictionary(
            manifest.SourceFiles,
            x => x.Id,
            "library.duplicate_source_file_id",
            diagnostics);

        var entities = new Dictionary<WccEntityId, WccEntityRecord>();
        var references = new Dictionary<WccReferenceId, WccReferenceRecord>();

        foreach (var analysis in analyses)
        {
            if (!sourceFiles.TryGetValue(analysis.SourceFileId, out var sourceFile))
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "library.analysis_without_source_file",
                    Message = $"Analysis refers to unknown source file {analysis.SourceFileId}.",
                    Severity = WccDiagnosticSeverity.Warning,
                    SourceFileId = analysis.SourceFileId
                });
                continue;
            }

            if (analysis.FormatVersion != WccLibraryFormat.CurrentFileAnalysisVersion)
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "library.unsupported_file_analysis_version",
                    Message = $"Cached analysis for {sourceFile.RelativePath} uses format {analysis.FormatVersion} and was ignored.",
                    Severity = WccDiagnosticSeverity.Warning,
                    SourceFileId = sourceFile.Id
                });
                continue;
            }

            if (!string.Equals(
                    sourceFile.ContentHash,
                    analysis.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "library.stale_file_analysis",
                    Message = $"Cached analysis for {sourceFile.RelativePath} has a different content hash and was ignored.",
                    Severity = WccDiagnosticSeverity.Warning,
                    SourceFileId = sourceFile.Id
                });
                continue;
            }

            diagnostics.AddRange(analysis.Diagnostics);

            foreach (var entity in analysis.Entities)
            {
                if (!entities.TryAdd(entity.Id, entity))
                {
                    diagnostics.Add(new WccDiagnosticRecord
                    {
                        Code = "library.duplicate_entity_id",
                        Message = $"Duplicate entity id {entity.Id} was ignored.",
                        Severity = WccDiagnosticSeverity.Error,
                        SourceFileId = entity.SourceFileId,
                        SourceEntityId = entity.Id
                    });
                }
            }

            foreach (var reference in analysis.References)
            {
                if (!references.TryAdd(reference.Id, reference))
                {
                    diagnostics.Add(new WccDiagnosticRecord
                    {
                        Code = "library.duplicate_reference_id",
                        Message = $"Duplicate reference id {reference.Id} was ignored.",
                        Severity = WccDiagnosticSeverity.Error,
                        SourceFileId = reference.SourceFileId,
                        SourceEntityId = reference.SourceEntityId
                    });
                }
            }
        }

        references = WccLibraryReferenceResolver.Resolve(
            packages,
            revisions,
            sourceFiles,
            entities,
            references,
            diagnostics);

        var entitiesByType = entities.Values
            .GroupBy(x => x.EntityType)
            .ToFrozenDictionary(x => x.Key, x => x.ToArray());

        var definitionsByScopedSymbol = entities.Values
            .Where(x => !string.IsNullOrWhiteSpace(x.ExportSymbol))
            .Select(x => new
            {
                Entity = x,
                SourceFile = sourceFiles.GetValueOrDefault(x.SourceFileId)
            })
            .Where(x => x.SourceFile is not null)
            .GroupBy(x => new WccScopedSymbolKey(
                x.SourceFile!.PackageRevisionId,
                x.Entity.EntityType,
                x.Entity.ExportSymbol!))
            .ToFrozenDictionary(x => x.Key, x => x.Select(y => y.Entity).ToArray());

        // Macro expansion and cross-package catalog queries address symbols
        // without a revision first, then apply visibility/precedence. Building
        // this second immutable index once avoids walking every scoped symbol
        // for each unique macro encountered in a large scenario.
        var definitionsByUnscopedSymbol = entities.Values
            .Where(x => !string.IsNullOrWhiteSpace(x.ExportSymbol))
            .GroupBy(x => new WccUnscopedSymbolKey(x.EntityType, x.ExportSymbol!))
            .ToFrozenDictionary(x => x.Key, x => x.ToArray());

        var outgoingReferences = references.Values
            .Where(x => x.SourceEntityId is not null)
            .GroupBy(x => x.SourceEntityId!.Value)
            .ToFrozenDictionary(x => x.Key, x => x.ToArray());

        var incomingReferences = references.Values
            .Where(x => x.TargetEntityId is not null)
            .GroupBy(x => x.TargetEntityId!.Value)
            .ToFrozenDictionary(x => x.Key, x => x.ToArray());

        var fileReferences = references.Values
            .Where(x => x.SourceEntityId is null)
            .GroupBy(x => x.SourceFileId)
            .ToFrozenDictionary(x => x.Key, x => x.ToArray());

        return new WccLibrarySnapshot(
            manifest,
            packages.ToFrozenDictionary(),
            revisions.ToFrozenDictionary(),
            sourceFiles.ToFrozenDictionary(),
            entities.ToFrozenDictionary(),
            references.ToFrozenDictionary(),
            entitiesByType,
            definitionsByScopedSymbol,
            definitionsByUnscopedSymbol,
            outgoingReferences,
            incomingReferences,
            fileReferences,
            diagnostics.ToArray());
    }

    private static Dictionary<TKey, TValue> BuildUniqueDictionary<TKey, TValue>(
        IEnumerable<TValue> values,
        Func<TValue, TKey> keySelector,
        string diagnosticCode,
        List<WccDiagnosticRecord> diagnostics)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();

        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = diagnosticCode,
                    Message = $"Duplicate library id {key} was ignored.",
                    Severity = WccDiagnosticSeverity.Error
                });
            }
        }

        return result;
    }
}
