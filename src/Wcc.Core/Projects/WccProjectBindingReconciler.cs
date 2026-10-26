using Wcc.Core.Library;

namespace Wcc.Core.Projects;

public sealed class WccProjectBindingReconciliationResult
{
    public required WccProjectMetadata Metadata { get; init; }
    public int ReconciledBindingCount { get; init; }
    public int UnresolvedBindingCount { get; init; }
    public int AmbiguousBindingCount { get; init; }
    public bool HasChanges => ReconciledBindingCount > 0;
}

/// <summary>
/// Repairs exact ids after an older/transient index or a rebuilt library by requiring the
/// stored package descriptor, entity symbol, type, and content fingerprint to identify one
/// visible target. It never guesses when the fallback locator is incomplete or ambiguous.
/// </summary>
public static class WccProjectBindingReconciler
{
    public static WccProjectBindingReconciliationResult Reconcile(
        WccProjectMetadata metadata,
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (visibleRevisionIds is null) throw new ArgumentNullException(nameof(visibleRevisionIds));

        var reconciledCount = 0;
        var unresolvedCount = 0;
        var ambiguousCount = 0;
        var bindings = new List<WccProjectEntityBinding>(metadata.EntityBindings.Count);

        foreach (var binding in metadata.EntityBindings)
        {
            if (IsCurrentTargetValid(binding, snapshot, visibleRevisionIds))
            {
                bindings.Add(binding);
                continue;
            }

            var candidates = FindFallbackCandidates(binding, snapshot, visibleRevisionIds)
                .ToArray();
            if (candidates.Length == 1)
            {
                var candidate = candidates[0];
                bindings.Add(CopyWithTarget(binding, candidate.Entity, candidate.Package, candidate.Revision));
                reconciledCount++;
            }
            else
            {
                bindings.Add(binding);
                if (candidates.Length == 0)
                    unresolvedCount++;
                else
                    ambiguousCount++;
            }
        }

        return new WccProjectBindingReconciliationResult
        {
            Metadata = reconciledCount == 0
                ? metadata
                : new WccProjectMetadata
                {
                    FormatVersion = metadata.FormatVersion,
                    ProjectId = metadata.ProjectId,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Compatibility = metadata.Compatibility,
                    Authoring = metadata.Authoring,
                    EntityBindings = bindings
                },
            ReconciledBindingCount = reconciledCount,
            UnresolvedBindingCount = unresolvedCount,
            AmbiguousBindingCount = ambiguousCount
        };
    }

    private static bool IsCurrentTargetValid(
        WccProjectEntityBinding binding,
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds)
    {
        if (!snapshot.EntitiesById.TryGetValue(binding.TargetEntityId, out var entity) ||
            !TryGetContext(snapshot, entity, out var revision, out var package) ||
            !visibleRevisionIds.Contains(revision.Id))
        {
            return false;
        }

        return EntityMatches(binding, entity) && PackageMatches(binding, package);
    }

    private static IEnumerable<TargetContext> FindFallbackCandidates(
        WccProjectEntityBinding binding,
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds)
    {
        if (string.IsNullOrWhiteSpace(binding.TargetExportSymbol) ||
            string.IsNullOrWhiteSpace(binding.TargetContentFingerprint))
        {
            return Enumerable.Empty<TargetContext>();
        }

        return snapshot.FindEntities(binding.TargetEntityType)
            .Where(entity => EntityMatches(binding, entity))
            .Select(entity => TryGetContext(snapshot, entity, out var revision, out var package)
                ? new TargetContext(entity, revision, package)
                : null)
            .Where(x => x is not null &&
                        visibleRevisionIds.Contains(x.Revision.Id) &&
                        PackageMatches(binding, x.Package))
            .Cast<TargetContext>();
    }

    private static bool EntityMatches(
        WccProjectEntityBinding binding,
        WccEntityRecord entity)
        => entity.EntityType == binding.TargetEntityType &&
           string.Equals(
               entity.ExportSymbol,
               binding.TargetExportSymbol,
               StringComparison.Ordinal) &&
           string.Equals(
               entity.ContentFingerprint,
               binding.TargetContentFingerprint,
               StringComparison.OrdinalIgnoreCase);

    private static bool PackageMatches(
        WccProjectEntityBinding binding,
        WccPackageRecord package)
        => package.SourceKind == binding.TargetPackageSourceKind &&
           package.ContentKind == binding.TargetPackageContentKind &&
           string.Equals(package.Name, binding.TargetPackageName, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetContext(
        WccLibrarySnapshot snapshot,
        WccEntityRecord entity,
        out WccPackageRevisionRecord revision,
        out WccPackageRecord package)
    {
        if (snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile) &&
            snapshot.RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out revision!) &&
            snapshot.PackagesById.TryGetValue(revision.PackageId, out package!))
        {
            return true;
        }

        revision = null!;
        package = null!;
        return false;
    }

    private static WccProjectEntityBinding CopyWithTarget(
        WccProjectEntityBinding binding,
        WccEntityRecord entity,
        WccPackageRecord package,
        WccPackageRevisionRecord revision)
        => new()
        {
            OwnerRelativeSourcePath = binding.OwnerRelativeSourcePath,
            OwnerEntityType = binding.OwnerEntityType,
            OwnerExportSymbol = binding.OwnerExportSymbol,
            OwnerEntityOrdinal = binding.OwnerEntityOrdinal,
            FieldPath = binding.FieldPath,
            SelectionOrder = binding.SelectionOrder,
            EmittedValue = binding.EmittedValue,
            TargetEntityId = entity.Id,
            TargetEntityType = entity.EntityType,
            TargetExportSymbol = entity.ExportSymbol,
            TargetContentFingerprint = entity.ContentFingerprint,
            TargetPackageId = package.Id,
            TargetPackageRevisionId = revision.Id,
            TargetPackageName = package.Name,
            TargetPackageSourceKind = package.SourceKind,
            TargetPackageContentKind = package.ContentKind,
            TargetPackageRevisionFingerprint = revision.ContentFingerprint
        };

    private sealed record TargetContext(
        WccEntityRecord Entity,
        WccPackageRevisionRecord Revision,
        WccPackageRecord Package);
}
