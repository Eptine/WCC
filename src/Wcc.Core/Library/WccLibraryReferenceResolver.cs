namespace Wcc.Core.Library;

/// <summary>
/// Resolves symbol-like references against the source package revision and its explicitly
/// pinned dependency revisions. It deliberately does not search every installed package:
/// an unrelated campaign that happens to define the same WML symbol is not visible.
/// </summary>
internal static class WccLibraryReferenceResolver
{
    public static Dictionary<WccReferenceId, WccReferenceRecord> Resolve(
        IReadOnlyDictionary<WccPackageId, WccPackageRecord> packages,
        IReadOnlyDictionary<WccPackageRevisionId, WccPackageRevisionRecord> revisions,
        IReadOnlyDictionary<WccSourceFileId, WccSourceFileRecord> sourceFiles,
        IReadOnlyDictionary<WccEntityId, WccEntityRecord> entities,
        IReadOnlyDictionary<WccReferenceId, WccReferenceRecord> references,
        List<WccDiagnosticRecord> diagnostics)
    {
        var definitions = entities.Values
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
            .ToDictionary(x => x.Key, x => x.Select(y => y.Entity).ToArray());

        DiagnoseMissingDependencies(revisions, diagnostics);

        var result = new Dictionary<WccReferenceId, WccReferenceRecord>(references.Count);
        // A large Wesnoth snapshot contains many occurrences of the same macro and unit
        // references. Visibility depends only on the source revision, target type, and
        // exact lookup value; occurrence-specific state is applied later by ResolveCandidateSet.
        var dependencyLevelsByRevision =
            new Dictionary<WccPackageRevisionId, IReadOnlyList<WccPackageRevisionId>[]>();
        var candidatesByLookup =
            new Dictionary<ReferenceLookupKey, WccEntityRecord[]>();
        foreach (var reference in references.Values)
        {
            result[reference.Id] = ResolveOne(
                reference,
                packages,
                revisions,
                sourceFiles,
                definitions,
                dependencyLevelsByRevision,
                candidatesByLookup,
                diagnostics);
        }

        return result;
    }

    private static WccReferenceRecord ResolveOne(
        WccReferenceRecord reference,
        IReadOnlyDictionary<WccPackageId, WccPackageRecord> packages,
        IReadOnlyDictionary<WccPackageRevisionId, WccPackageRevisionRecord> revisions,
        IReadOnlyDictionary<WccSourceFileId, WccSourceFileRecord> sourceFiles,
        IReadOnlyDictionary<WccScopedSymbolKey, WccEntityRecord[]> definitions,
        Dictionary<WccPackageRevisionId, IReadOnlyList<WccPackageRevisionId>[]> dependencyLevelsByRevision,
        Dictionary<ReferenceLookupKey, WccEntityRecord[]> candidatesByLookup,
        List<WccDiagnosticRecord> diagnostics)
    {
        if (reference.ResolutionState != WccReferenceResolutionState.Unresolved ||
            !TryGetTargetEntityType(reference.ReferenceType, out var targetType) ||
            string.IsNullOrWhiteSpace(reference.LookupValue) ||
            !sourceFiles.TryGetValue(reference.SourceFileId, out var sourceFile) ||
            !revisions.TryGetValue(sourceFile.PackageRevisionId, out var sourceRevision))
        {
            return reference;
        }

        var lookupKey = new ReferenceLookupKey(
            sourceRevision.Id,
            targetType,
            reference.LookupValue);
        if (!candidatesByLookup.TryGetValue(lookupKey, out var candidates))
        {
            candidates = FindVisibleDefinitions(
                sourceRevision,
                targetType,
                reference.LookupValue,
                revisions,
                definitions,
                dependencyLevelsByRevision);
            candidatesByLookup[lookupKey] = candidates;
        }

        if (candidates.Length == 0)
            return reference;

        return ResolveCandidateSet(
            reference,
            candidates,
            sourceRevision.Id,
            packages,
            revisions,
            sourceFiles,
            diagnostics);
    }

    private static WccEntityRecord[] FindVisibleDefinitions(
        WccPackageRevisionRecord sourceRevision,
        WccEntityTypeId targetType,
        string lookupValue,
        IReadOnlyDictionary<WccPackageRevisionId, WccPackageRevisionRecord> revisions,
        IReadOnlyDictionary<WccScopedSymbolKey, WccEntityRecord[]> definitions,
        Dictionary<WccPackageRevisionId, IReadOnlyList<WccPackageRevisionId>[]> dependencyLevelsByRevision)
    {
        var localCandidates = FindDefinitions(
            definitions,
            sourceRevision.Id,
            targetType,
            lookupValue);
        if (localCandidates.Count > 0)
            return localCandidates as WccEntityRecord[] ?? localCandidates.ToArray();

        if (!dependencyLevelsByRevision.TryGetValue(sourceRevision.Id, out var dependencyLevels))
        {
            dependencyLevels = EnumerateDependencyLevels(sourceRevision, revisions)
                .ToArray();
            dependencyLevelsByRevision[sourceRevision.Id] = dependencyLevels;
        }

        foreach (var dependencyLevel in dependencyLevels)
        {
            var candidates = dependencyLevel
                .SelectMany(revisionId => FindDefinitions(
                    definitions,
                    revisionId,
                    targetType,
                    lookupValue))
                .ToArray();

            if (candidates.Length > 0)
                return candidates;
        }

        return [];
    }

    private static WccReferenceRecord ResolveCandidateSet(
        WccReferenceRecord reference,
        IReadOnlyList<WccEntityRecord> candidates,
        WccPackageRevisionId sourceRevisionId,
        IReadOnlyDictionary<WccPackageId, WccPackageRecord> packages,
        IReadOnlyDictionary<WccPackageRevisionId, WccPackageRevisionRecord> revisions,
        IReadOnlyDictionary<WccSourceFileId, WccSourceFileRecord> sourceFiles,
        List<WccDiagnosticRecord> diagnostics)
    {
        if (candidates.Count != 1)
        {
            diagnostics.Add(new WccDiagnosticRecord
            {
                Code = "library.ambiguous_reference",
                Message =
                    $"Reference '{reference.LookupValue}' ({reference.ReferenceType}) has " +
                    $"{candidates.Count} equally visible definitions.",
                Severity = WccDiagnosticSeverity.Warning,
                SourceFileId = reference.SourceFileId,
                SourceEntityId = reference.SourceEntityId,
                SourceSpan = reference.SourceSpan
            });
            return CopyReference(
                reference,
                WccReferenceResolutionState.Ambiguous,
                targetEntityId: null,
                reference.Confidence);
        }

        var target = candidates[0];
        var targetRevisionId = sourceFiles.TryGetValue(target.SourceFileId, out var targetFile)
            ? targetFile.PackageRevisionId
            : sourceRevisionId;
        var state = targetRevisionId == sourceRevisionId
            ? WccReferenceResolutionState.Resolved
            : IsCoreRevision(targetRevisionId, packages, revisions)
                ? WccReferenceResolutionState.CoreDependency
                : WccReferenceResolutionState.PackageDependency;
        var confidence = reference.Confidence;

        // Macro availability also depends on preprocessing order, active defines, #undef,
        // and include branches. We retain the best visible candidate but label it conditional
        // until the package preprocessor context is modeled or Wesnoth validates it.
        if (reference.ReferenceType == WccReferenceTypes.MacroInvocation)
        {
            state = WccReferenceResolutionState.Conditional;
            confidence = WccReferenceConfidence.Medium;
        }

        return CopyReference(reference, state, target.Id, confidence);
    }

    private static IReadOnlyList<WccEntityRecord> FindDefinitions(
        IReadOnlyDictionary<WccScopedSymbolKey, WccEntityRecord[]> definitions,
        WccPackageRevisionId revisionId,
        WccEntityTypeId entityType,
        string symbol)
        => definitions.TryGetValue(
            new WccScopedSymbolKey(revisionId, entityType, symbol),
            out var candidates)
            ? candidates
            : Array.Empty<WccEntityRecord>();

    private static IEnumerable<IReadOnlyList<WccPackageRevisionId>> EnumerateDependencyLevels(
        WccPackageRevisionRecord sourceRevision,
        IReadOnlyDictionary<WccPackageRevisionId, WccPackageRevisionRecord> revisions)
    {
        var visited = new HashSet<WccPackageRevisionId> { sourceRevision.Id };
        var level = sourceRevision.DependencyRevisionIds
            .Where(visited.Add)
            .ToArray();

        while (level.Length > 0)
        {
            yield return level;

            level = level
                .SelectMany(x => revisions.TryGetValue(x, out var revision)
                    ? revision.DependencyRevisionIds
                    : Enumerable.Empty<WccPackageRevisionId>())
                .Where(visited.Add)
                .ToArray();
        }
    }

    private static bool IsCoreRevision(
        WccPackageRevisionId revisionId,
        IReadOnlyDictionary<WccPackageId, WccPackageRecord> packages,
        IReadOnlyDictionary<WccPackageRevisionId, WccPackageRevisionRecord> revisions)
        => revisions.TryGetValue(revisionId, out var revision) &&
           packages.TryGetValue(revision.PackageId, out var package) &&
           package.SourceKind == WccPackageSourceKinds.WesnothMainline &&
           package.ContentKind != WccPackageContentKinds.Campaign;

    private static bool TryGetTargetEntityType(
        WccReferenceTypeId referenceType,
        out WccEntityTypeId entityType)
    {
        if (referenceType == WccReferenceTypes.Race)
            entityType = WccEntityTypes.Race;
        else if (referenceType == WccReferenceTypes.MovementType)
            entityType = WccEntityTypes.MovementType;
        else if (referenceType == WccReferenceTypes.UnitType)
            entityType = WccEntityTypes.UnitType;
        else if (referenceType == WccReferenceTypes.BaseUnit)
            entityType = WccEntityTypes.UnitType;
        else if (referenceType == WccReferenceTypes.AdvancesTo)
            entityType = WccEntityTypes.UnitType;
        else if (referenceType == WccReferenceTypes.CampaignFirstScenario ||
                 referenceType == WccReferenceTypes.ScenarioNextScenario)
            entityType = WccEntityTypes.Scenario;
        else if (referenceType == WccReferenceTypes.MacroInvocation)
            entityType = WccEntityTypes.MacroDefinition;
        else
        {
            entityType = default;
            return false;
        }

        return true;
    }

    private static WccReferenceRecord CopyReference(
        WccReferenceRecord source,
        WccReferenceResolutionState resolutionState,
        WccEntityId? targetEntityId,
        WccReferenceConfidence confidence)
        => new()
        {
            Id = source.Id,
            SourceFileId = source.SourceFileId,
            SourceEntityId = source.SourceEntityId,
            ReferenceType = source.ReferenceType,
            RawValue = source.RawValue,
            LookupValue = source.LookupValue,
            SourceSpan = source.SourceSpan,
            ResolutionState = resolutionState,
            Confidence = confidence,
            TargetEntityId = targetEntityId,
            Condition = source.Condition,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal)
        };

    private static void DiagnoseMissingDependencies(
        IReadOnlyDictionary<WccPackageRevisionId, WccPackageRevisionRecord> revisions,
        List<WccDiagnosticRecord> diagnostics)
    {
        foreach (var revision in revisions.Values)
        {
            foreach (var missingId in revision.DependencyRevisionIds.Where(x => !revisions.ContainsKey(x)))
            {
                diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = "library.missing_dependency_revision",
                    Message = $"Package revision {revision.Id} depends on missing revision {missingId}.",
                    Severity = WccDiagnosticSeverity.Warning
                });
            }
        }
    }

    private readonly record struct ReferenceLookupKey(
        WccPackageRevisionId SourceRevisionId,
        WccEntityTypeId TargetType,
        string LookupValue);

}
