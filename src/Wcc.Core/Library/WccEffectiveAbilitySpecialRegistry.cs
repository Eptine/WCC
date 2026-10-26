using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Library;

public sealed record WccEffectiveAbilitySpecialDefinition(
    WesnothAbilitySpecialRegistryKind Kind,
    string RegistryId,
    string TagName,
    string? Id,
    string? UniqueId,
    string? DisplayNameWml,
    WccPackageRevisionId PackageRevisionId,
    WccPackageId? PackageId,
    string PackageName,
    WccSourceFileId SourceFileId,
    string SourcePath,
    int? SourceLine,
    bool IsConditional,
    bool IsMacroGenerated,
    string? MacroName,
    WccEntityId? SourceEntityId,
    string? ResolvedRawText = null)
{
    public string DisplayLabel
        => string.IsNullOrWhiteSpace(DisplayNameWml)
            ? RegistryId
            : $"{RegistryId}  ({DisplayNameWml.Trim()})";
}

public sealed record WccAbilitySpecialRegistryCollision(
    WesnothAbilitySpecialRegistryKind Kind,
    string RegistryId,
    WccEffectiveAbilitySpecialDefinition Winner,
    WccEffectiveAbilitySpecialDefinition Shadowed);

public sealed record WccAbilitySpecialRegistryIssue(
    string Code,
    string Message,
    WccDiagnosticSeverity Severity,
    WesnothAbilitySpecialRegistryKind? Kind = null,
    WccPackageRevisionId? PackageRevisionId = null,
    WccSourceFileId? SourceFileId = null);

public sealed class WccEffectiveAbilitySpecialRegistry
{
    internal WccEffectiveAbilitySpecialRegistry(
        IReadOnlyList<WccEffectiveAbilitySpecialDefinition> abilities,
        IReadOnlyList<WccEffectiveAbilitySpecialDefinition> weaponSpecials,
        IReadOnlyList<WccAbilitySpecialRegistryCollision> collisions,
        IReadOnlyList<WccAbilitySpecialRegistryIssue> issues,
        bool hasExactLoadOrder)
    {
        Abilities = abilities;
        WeaponSpecials = weaponSpecials;
        Collisions = collisions;
        Issues = issues;
        HasExactLoadOrder = hasExactLoadOrder;
    }

    public IReadOnlyList<WccEffectiveAbilitySpecialDefinition> Abilities { get; }
    public IReadOnlyList<WccEffectiveAbilitySpecialDefinition> WeaponSpecials { get; }
    public IReadOnlyList<WccAbilitySpecialRegistryCollision> Collisions { get; }
    public IReadOnlyList<WccAbilitySpecialRegistryIssue> Issues { get; }

    /// <summary>
    /// True only where indexed source contains at most one registry container. WCC's
    /// package dependency graph is deterministic but does not yet retain Wesnoth's full
    /// preprocessor include order, so multiple containers require engine validation.
    /// </summary>
    public bool HasExactLoadOrder { get; }

    public WccEffectiveAbilitySpecialDefinition? Find(
        WesnothAbilitySpecialRegistryKind kind,
        string registryId)
        => GetDefinitions(kind).FirstOrDefault(x => string.Equals(
            x.RegistryId,
            registryId,
            StringComparison.Ordinal));

    public IReadOnlyList<WccEffectiveAbilitySpecialDefinition> GetDefinitions(
        WesnothAbilitySpecialRegistryKind kind)
        => kind == WesnothAbilitySpecialRegistryKind.Ability
            ? Abilities
            : WeaponSpecials;
}

/// <summary>
/// Builds a read-only view of the Wesnoth 1.20 ability and weapon-special
/// registries for one exact revision scope. Source macros are expanded only for
/// analysis; generated WML is never written back to the owning files.
/// </summary>
public static class WccEffectiveAbilitySpecialRegistryComposer
{
    public static WccEffectiveAbilitySpecialRegistry Compose(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds,
        string? currentSourcePath = null,
        WccPackageRevisionId? rootRevisionId = null,
        IWmlMacroExpansionService? macroExpansionService = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (visibleRevisionIds is null) throw new ArgumentNullException(nameof(visibleRevisionIds));

        var issues = new List<WccAbilitySpecialRegistryIssue>();
        var containers = FindContainers(snapshot, visibleRevisionIds).ToArray();
        var registryRevisionIds = containers
            .Select(x => x.RevisionId)
            .ToHashSet();
        var order = BuildRevisionOrder(
            snapshot,
            visibleRevisionIds,
            rootRevisionId ?? FindSourceRevision(snapshot, currentSourcePath),
            registryRevisionIds,
            issues);
        var orderByRevision = order
            .Select((revisionId, index) => (revisionId, index))
            .ToDictionary(x => x.revisionId, x => x.index);
        var orderedContainers = containers
            .OrderBy(x => orderByRevision.GetValueOrDefault(x.RevisionId, int.MaxValue))
            .ThenBy(x => x.SourceFile.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Entity.SourceSpan?.StartLine ?? int.MaxValue)
            .ThenBy(x => x.Entity.Id.Value)
            .ToArray();

        var hasExactLoadOrder = orderedContainers
            .GroupBy(x => x.Kind)
            .All(group => group.Count() <= 1);
        if (!hasExactLoadOrder)
        {
            issues.Add(new WccAbilitySpecialRegistryIssue(
                "registry.load_order_approximation",
                "Several registry containers are visible. WCC uses deterministic dependency/file order, but exact first-definition precedence still requires Wesnoth preprocessor validation.",
                WccDiagnosticSeverity.Warning));
        }

        var winners = new Dictionary<RegistryKey, WccEffectiveAbilitySpecialDefinition>();
        var orderedWinners = new List<WccEffectiveAbilitySpecialDefinition>();
        var collisions = new List<WccAbilitySpecialRegistryCollision>();
        var expansion = macroExpansionService ?? new WmlMacroExpansionService();

        foreach (var container in orderedContainers)
        {
            var sourceText = TryReadSource(container.SourceFile, issues, container.Kind);
            if (sourceText is null)
            {
                AddIndexedDefinitions(
                    snapshot,
                    container,
                    winners,
                    orderedWinners,
                    collisions,
                    issues);
                continue;
            }

            if (!string.Equals(
                    WccContentHash.ComputeText(sourceText),
                    container.SourceFile.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new WccAbilitySpecialRegistryIssue(
                    "registry.source_changed_since_index",
                    $"Registry source '{container.SourceFile.RelativePath}' changed after indexing; the current source was analyzed but should be re-indexed.",
                    WccDiagnosticSeverity.Warning,
                    container.Kind,
                    container.RevisionId,
                    container.SourceFile.Id));
                // Every entity/container span belongs to the indexed content hash.
                // Applying it to a newer file can cut through a definition and must
                // never feed editable projected WML. Keep only the indexed summary
                // until the package refresh publishes matching spans.
                AddIndexedDefinitions(
                    snapshot,
                    container,
                    winners,
                    orderedWinners,
                    collisions,
                    issues);
                continue;
            }

            var rawContainer = TrySlice(sourceText, container.Entity.SourceSpan);
            if (rawContainer is null)
            {
                issues.Add(new WccAbilitySpecialRegistryIssue(
                    "registry.source_span_unavailable",
                    $"The indexed registry span in '{container.SourceFile.RelativePath}' is unavailable; only literal indexed entries were projected.",
                    WccDiagnosticSeverity.Warning,
                    container.Kind,
                    container.RevisionId,
                    container.SourceFile.Id));
                AddIndexedDefinitions(
                    snapshot,
                    container,
                    winners,
                    orderedWinners,
                    collisions,
                    issues);
                continue;
            }

            var resolver = new WccLibraryMacroExpansionDefinitionResolver(
                snapshot,
                visibleRevisionIds,
                container.SourceFile.StoragePath);
            var expanded = expansion.Expand(rawContainer, fallbackResolver: resolver);
            foreach (var expansionIssue in expanded.Issues)
            {
                issues.Add(new WccAbilitySpecialRegistryIssue(
                    "registry.macro_" + expansionIssue.Kind.ToString().ToLowerInvariant(),
                    expansionIssue.Message,
                    expansionIssue.Kind is WmlMacroExpansionIssueKind.Unresolved or WmlMacroExpansionIssueKind.Ambiguous
                        ? WccDiagnosticSeverity.Warning
                        : WccDiagnosticSeverity.Information,
                    container.Kind,
                    container.RevisionId,
                    container.SourceFile.Id));
            }

            var parsed = WesnothAbilitySpecialRegistrySourceParser.ParseDefinitions(
                container.Kind,
                expanded.ExpandedText,
                expanded);
            foreach (var parseIssue in parsed.Issues)
            {
                issues.Add(new WccAbilitySpecialRegistryIssue(
                    parseIssue.Code,
                    parseIssue.Message,
                    WccDiagnosticSeverity.Warning,
                    parseIssue.Kind,
                    container.RevisionId,
                    container.SourceFile.Id));
            }

            foreach (var definition in parsed.Definitions)
            {
                var provenance = expanded.FindProvenance(definition.MacroExpansionId);
                var sourceEntity = definition.IsMacroGenerated
                    ? FindMacroDefinitionEntity(snapshot, provenance)
                    : FindIndexedDefinitionEntity(snapshot, container, definition);
                var projected = new WccEffectiveAbilitySpecialDefinition(
                    definition.Kind,
                    definition.RegistryId,
                    definition.TagName,
                    definition.Id,
                    definition.UniqueId,
                    definition.DisplayNameWml,
                    container.RevisionId,
                    container.Package?.Id,
                    container.Package?.Name ?? "(unknown package)",
                    container.SourceFile.Id,
                    provenance?.SourcePath ?? container.SourceFile.StoragePath ?? container.SourceFile.RelativePath,
                    provenance?.DefinitionStartLine ?? AddLines(container.Entity.SourceSpan?.StartLine, definition.StartLine),
                    container.IsConditional || definition.IsConditional,
                    definition.IsMacroGenerated,
                    provenance?.MacroName,
                    sourceEntity?.Id,
                    definition.RawText);
                Register(projected, winners, orderedWinners, collisions);
            }
        }

        return new WccEffectiveAbilitySpecialRegistry(
            orderedWinners.Where(x => x.Kind == WesnothAbilitySpecialRegistryKind.Ability).ToArray(),
            orderedWinners.Where(x => x.Kind == WesnothAbilitySpecialRegistryKind.WeaponSpecial).ToArray(),
            collisions,
            issues,
            hasExactLoadOrder);
    }

    private static IEnumerable<ContainerContext> FindContainers(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds)
    {
        foreach (var pair in new[]
                 {
                     (WccEntityTypes.AbilityRegistry, WesnothAbilitySpecialRegistryKind.Ability),
                     (WccEntityTypes.WeaponSpecialRegistry, WesnothAbilitySpecialRegistryKind.WeaponSpecial)
                 })
        {
            foreach (var entity in snapshot.FindEntities(pair.Item1))
            {
                if (!snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var sourceFile) ||
                    !visibleRevisionIds.Contains(sourceFile.PackageRevisionId) ||
                    !snapshot.RevisionsById.TryGetValue(sourceFile.PackageRevisionId, out var revision))
                {
                    continue;
                }

                snapshot.PackagesById.TryGetValue(revision.PackageId, out var package);
                yield return new ContainerContext(
                    pair.Item2,
                    entity,
                    sourceFile,
                    sourceFile.PackageRevisionId,
                    package,
                    entity.Summary.TryGetValue("conditional", out var conditional) &&
                    bool.TryParse(conditional, out var parsedConditional) && parsedConditional);
            }
        }
    }

    private static IReadOnlyList<WccPackageRevisionId> BuildRevisionOrder(
        WccLibrarySnapshot snapshot,
        IReadOnlySet<WccPackageRevisionId> visibleRevisionIds,
        WccPackageRevisionId? preferredRoot,
        IReadOnlySet<WccPackageRevisionId> registryRevisionIds,
        List<WccAbilitySpecialRegistryIssue> issues)
    {
        var result = new List<WccPackageRevisionId>();
        var state = new Dictionary<WccPackageRevisionId, byte>();
        var activePath = new List<WccPackageRevisionId>();
        var reportedCycles = new HashSet<string>(StringComparer.Ordinal);

        void Visit(WccPackageRevisionId revisionId)
        {
            if (!visibleRevisionIds.Contains(revisionId) || !snapshot.RevisionsById.ContainsKey(revisionId))
                return;
            if (state.TryGetValue(revisionId, out var currentState))
            {
                if (currentState == 1)
                {
                    var cycleStart = activePath.FindIndex(x => x == revisionId);
                    var cycle = cycleStart >= 0
                        ? activePath.Skip(cycleStart).ToArray()
                        : new[] { revisionId };
                    var cycleKey = string.Join(
                        ",",
                        cycle.Select(x => x.Value).OrderBy(x => x));
                    if (cycle.Any(registryRevisionIds.Contains) && reportedCycles.Add(cycleKey))
                    {
                        issues.Add(new WccAbilitySpecialRegistryIssue(
                            "registry.dependency_cycle",
                            "A dependency cycle contains a package with an ability or weapon-special registry. Registry order remains deterministic but requires Wesnoth validation.",
                            WccDiagnosticSeverity.Warning,
                            PackageRevisionId: revisionId));
                    }
                }
                return;
            }

            state[revisionId] = 1;
            activePath.Add(revisionId);
            foreach (var dependencyId in snapshot.RevisionsById[revisionId].DependencyRevisionIds)
                Visit(dependencyId);
            activePath.RemoveAt(activePath.Count - 1);
            state[revisionId] = 2;
            result.Add(revisionId);
        }

        if (preferredRoot is { } root)
            Visit(root);

        foreach (var revisionId in visibleRevisionIds
                     .OrderBy(id => PackageOrder(snapshot, id))
                     .ThenBy(id => PackageName(snapshot, id), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(id => id.Value))
        {
            Visit(revisionId);
        }

        return result;
    }

    private static int PackageOrder(WccLibrarySnapshot snapshot, WccPackageRevisionId revisionId)
    {
        var package = FindPackage(snapshot, revisionId);
        if (package is null) return 9;
        if (package.SourceKind == WccPackageSourceKinds.WesnothCore) return 0;
        if (package.SourceKind == WccPackageSourceKinds.WesnothMainline) return 1;
        if (package.SourceKind == WccPackageSourceKinds.WesnothAddon) return 2;
        if (package.SourceKind == WccPackageSourceKinds.WccProject) return 4;
        return 3;
    }

    private static string PackageName(WccLibrarySnapshot snapshot, WccPackageRevisionId revisionId)
        => FindPackage(snapshot, revisionId)?.Name ?? string.Empty;

    private static WccPackageRecord? FindPackage(
        WccLibrarySnapshot snapshot,
        WccPackageRevisionId revisionId)
    {
        if (!snapshot.RevisionsById.TryGetValue(revisionId, out var revision))
            return null;
        return snapshot.PackagesById.GetValueOrDefault(revision.PackageId);
    }

    private static WccPackageRevisionId? FindSourceRevision(
        WccLibrarySnapshot snapshot,
        string? currentSourcePath)
    {
        if (string.IsNullOrWhiteSpace(currentSourcePath)) return null;
        return snapshot.SourceFilesById.Values
            .FirstOrDefault(x => PathsEqual(x.StoragePath, currentSourcePath))
            ?.PackageRevisionId;
    }

    private static void AddIndexedDefinitions(
        WccLibrarySnapshot snapshot,
        ContainerContext container,
        Dictionary<RegistryKey, WccEffectiveAbilitySpecialDefinition> winners,
        List<WccEffectiveAbilitySpecialDefinition> orderedWinners,
        List<WccAbilitySpecialRegistryCollision> collisions,
        List<WccAbilitySpecialRegistryIssue> issues)
    {
        var type = container.Kind == WesnothAbilitySpecialRegistryKind.Ability
            ? WccEntityTypes.AbilityDefinition
            : WccEntityTypes.WeaponSpecialDefinition;
        var found = snapshot.FindEntities(type)
            .Where(x => x.ParentEntityId == container.Entity.Id)
            .OrderBy(x => x.SourceSpan?.StartLine ?? int.MaxValue)
            .ToArray();
        foreach (var entity in found)
        {
            if (!entity.Summary.TryGetValue("registry_id", out var registryId) ||
                string.IsNullOrWhiteSpace(registryId))
            {
                continue;
            }

            var definition = new WccEffectiveAbilitySpecialDefinition(
                container.Kind,
                registryId,
                entity.Summary.GetValueOrDefault("tag_name") ?? "unknown",
                NullIfEmpty(entity.Summary.GetValueOrDefault("id")),
                NullIfEmpty(entity.Summary.GetValueOrDefault("unique_id")),
                NullIfEmpty(entity.Summary.GetValueOrDefault("name_wml")),
                container.RevisionId,
                container.Package?.Id,
                container.Package?.Name ?? "(unknown package)",
                container.SourceFile.Id,
                container.SourceFile.StoragePath ?? container.SourceFile.RelativePath,
                entity.SourceSpan?.StartLine,
                container.IsConditional ||
                (entity.Summary.TryGetValue("conditional", out var conditional) &&
                 bool.TryParse(conditional, out var parsed) && parsed),
                false,
                null,
                entity.Id,
                null);
            Register(definition, winners, orderedWinners, collisions);
        }

        if (found.Length == 0)
        {
            issues.Add(new WccAbilitySpecialRegistryIssue(
                "registry.source_unavailable",
                $"Registry source '{container.SourceFile.RelativePath}' could not be read, so macro-generated entries are unavailable.",
                WccDiagnosticSeverity.Warning,
                container.Kind,
                container.RevisionId,
                container.SourceFile.Id));
        }
    }

    private static WccEntityRecord? FindIndexedDefinitionEntity(
        WccLibrarySnapshot snapshot,
        ContainerContext container,
        WesnothAbilitySpecialRegistryDefinitionSource definition)
    {
        var type = definition.Kind == WesnothAbilitySpecialRegistryKind.Ability
            ? WccEntityTypes.AbilityDefinition
            : WccEntityTypes.WeaponSpecialDefinition;
        return snapshot.FindEntities(type).FirstOrDefault(x =>
            x.ParentEntityId == container.Entity.Id &&
            string.Equals(x.ExportSymbol, definition.RegistryId, StringComparison.Ordinal) &&
            (!x.Summary.TryGetValue("tag_name", out var tagName) ||
             string.Equals(tagName, definition.TagName, StringComparison.OrdinalIgnoreCase)));
    }

    private static WccEntityRecord? FindMacroDefinitionEntity(
        WccLibrarySnapshot snapshot,
        WmlMacroExpansionProvenance? provenance)
    {
        if (provenance is null || string.IsNullOrWhiteSpace(provenance.MacroName))
            return null;

        return snapshot.FindEntities(WccEntityTypes.MacroDefinition)
            .Where(x => string.Equals(
                x.ExportSymbol,
                provenance.MacroName,
                StringComparison.OrdinalIgnoreCase))
            .Where(x => snapshot.SourceFilesById.TryGetValue(x.SourceFileId, out var sourceFile) &&
                        (string.IsNullOrWhiteSpace(provenance.PackageRevisionId) ||
                         string.Equals(
                             sourceFile.PackageRevisionId.ToString(),
                             provenance.PackageRevisionId,
                             StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrWhiteSpace(provenance.SourcePath) ||
                         PathsEqual(sourceFile.StoragePath, provenance.SourcePath) ||
                         string.Equals(
                             sourceFile.RelativePath.Replace('\\', '/'),
                             provenance.SourcePath.Replace('\\', '/'),
                             StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => provenance.DefinitionStartLine is { } line && x.SourceSpan?.StartLine == line
                ? 0
                : 1)
            .ThenBy(x => x.SourceSpan?.StartLine ?? int.MaxValue)
            .FirstOrDefault();
    }

    private static void Register(
        WccEffectiveAbilitySpecialDefinition definition,
        Dictionary<RegistryKey, WccEffectiveAbilitySpecialDefinition> winners,
        List<WccEffectiveAbilitySpecialDefinition> orderedWinners,
        List<WccAbilitySpecialRegistryCollision> collisions)
    {
        var key = new RegistryKey(definition.Kind, definition.RegistryId);
        if (winners.TryAdd(key, definition))
        {
            orderedWinners.Add(definition);
            return;
        }

        collisions.Add(new WccAbilitySpecialRegistryCollision(
            definition.Kind,
            definition.RegistryId,
            winners[key],
            definition));
    }

    private static string? TryReadSource(
        WccSourceFileRecord sourceFile,
        List<WccAbilitySpecialRegistryIssue> issues,
        WesnothAbilitySpecialRegistryKind kind)
    {
        try
        {
            return string.IsNullOrWhiteSpace(sourceFile.StoragePath) || !File.Exists(sourceFile.StoragePath)
                ? null
                : File.ReadAllText(sourceFile.StoragePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new WccAbilitySpecialRegistryIssue(
                "registry.source_read_failed",
                $"Could not read registry source '{sourceFile.RelativePath}': {ex.Message}",
                WccDiagnosticSeverity.Warning,
                kind,
                sourceFile.PackageRevisionId,
                sourceFile.Id));
            return null;
        }
    }

    private static string? TrySlice(string sourceText, WccSourceSpan? span)
    {
        if (span?.StartOffset is { } start && span.EndOffsetExclusive is { } end &&
            start >= 0 && end >= start && end <= sourceText.Length)
        {
            return sourceText[start..end];
        }

        if (span?.StartLine is not { } startLine || span.EndLineExclusive is not { } endLine)
            return null;
        var lines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (startLine < 0 || endLine < startLine || endLine > lines.Length)
            return null;
        return string.Join("\n", lines.Skip(startLine).Take(endLine - startLine));
    }

    private static int? AddLines(int? baseLine, int relativeLine)
        => baseLine is { } line ? line + relativeLine : null;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
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

    private readonly record struct RegistryKey(
        WesnothAbilitySpecialRegistryKind Kind,
        string RegistryId);

    private sealed record ContainerContext(
        WesnothAbilitySpecialRegistryKind Kind,
        WccEntityRecord Entity,
        WccSourceFileRecord SourceFile,
        WccPackageRevisionId RevisionId,
        WccPackageRecord? Package,
        bool IsConditional);
}
