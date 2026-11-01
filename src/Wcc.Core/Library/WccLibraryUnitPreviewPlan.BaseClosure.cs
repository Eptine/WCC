using Wcc.Core.Wml;

namespace Wcc.Core.Library;

public sealed partial class WccLibraryUnitPreviewPlan
{
    /// <summary>A refusal to extend this display-only registration; the original selected preview remains usable.</summary>
    public string? BaseClosureUnavailableReason { get; private set; }

    /// <summary>
    /// Adds only literal sibling base-unit dependencies from this exact indexed revision.
    /// The ordinary registration planner still proves activation, membership and include order.
    /// This does not activate another campaign entry or difficulty, or authorize persistence.
    /// </summary>
    public static WccLibraryUnitPreviewPlan? TryCreateWithLiteralBaseClosure(WccStaticSourceCaptureResult capture,
        WccLibrarySnapshot snapshot, WccPackageRevisionId revisionId, string packageRoot,
        IReadOnlyList<string> selectedPaths, string? targetCompatibility = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selectedPaths);
        var original = TryCreateBatch(capture, packageRoot, selectedPaths, targetCompatibility, cancellationToken);
        if (original is null) return null;
        var paths = new HashSet<string>(original._selectedSources.Select(source => source.Path),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var files = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == revisionId && file.StoragePath is not null)
            .ToArray();
        var entities = snapshot.FindEntities(WccEntityTypes.UnitType)
            .Where(entity => snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) && file.PackageRevisionId == revisionId)
            .ToArray();
        // Most files need no sibling bases. Cached reference observations are a
        // cheap negative filter; every dependency that is added below still has
        // its identity/range checked against the immutable captured bytes.
        if (!entities.Any(entity => snapshot.SourceFilesById[entity.SourceFileId].StoragePath is { } path && paths.Contains(path) &&
                snapshot.GetOutgoingReferences(entity.Id).Any(reference => reference.ReferenceType == WccReferenceTypes.BaseUnit))) return original;
        var bySymbol = entities.Where(entity => WmlUnitReferenceProjection.TryLiteral(entity.ExportSymbol, out var id) && id.Length != 0)
            .GroupBy(entity => Literal(entity.ExportSymbol!), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byFile = entities.ToLookup(entity => entity.SourceFileId);
        var completed = new HashSet<WccEntityId>();
        var visiting = new HashSet<WccEntityId>();
        var checkedSources = new Dictionary<WmlStaticSource, List<SourceBlock>>();
        var count = 0;
        foreach (var selected in original._selectedSources)
        {
            var records = files.Where(file => SamePath(file.StoragePath!, selected.Path) && file.ContentHash == selected.ContentHash).Take(2).ToArray();
            if (records.Length != 1) return Refuse("The indexed unit file does not match its exact captured source.");
            foreach (var entity in byFile[records[0].Id])
                if (Visit(entity, 0) is { } problem) return Refuse(problem);
        }
        if (paths.Count == original._selectedSources.Length) return original;
        var expanded = TryCreateBatch(capture, packageRoot, paths.ToArray(), targetCompatibility, cancellationToken);
        return expanded ?? Refuse("The sibling base-unit files do not share one proven active registration include with this unit.");

        WccLibraryUnitPreviewPlan Refuse(string reason)
        {
            original.BaseClosureUnavailableReason = reason;
            return original;
        }

        string? Visit(WccEntityRecord entity, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visiting.Contains(entity.Id)) return "The literal sibling base-unit chain contains a cycle.";
            if (completed.Contains(entity.Id)) return null;
            if (depth >= 64 || ++count > 512) return "The literal sibling base-unit closure exceeds its bounded preview limit.";
            if (!snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) || file.PackageRevisionId != revisionId ||
                file.StoragePath is not { } path || entity.SourceSpan?.StartOffset is not { } start ||
                entity.SourceSpan.EndOffsetExclusive is not { } end || start < 0 || end <= start)
                return "A sibling base-unit declaration has no exact indexed source range.";
            var sources = capture.Sources.Where(source => source.IsAuthored && SamePath(source.Path, path) && source.ContentHash == file.ContentHash).Take(2).ToArray();
            if (sources.Length != 1 || end > sources[0].Text.Length)
                return "A sibling base-unit declaration does not match its exact captured source.";
            var source = sources[0];
            if (!checkedSources.TryGetValue(source, out var blocks))
            {
                if (source.Text.Length > 4 * 1024 * 1024) return "A sibling unit source exceeds the bounded preview size.";
                var structure = WmlAuthoredStructureScanner.Scan(source.Text);
                if (structure.Problem is not null || ReadBlocks(structure.MaskedText, cancellationToken) is not { } scanned)
                    return "A sibling base-unit source has unresolved authored structure.";
                checkedSources.Add(source, blocks = scanned);
            }
            if (!blocks.Any(block => block.Depth == 0 && block.Name == "unit_type" && block.Start == start && block.End == end))
                return "A sibling base-unit range is not one exact physical unit definition.";
            var raw = source.Text[start..end];
            var editable = WmlEditableBlockParser.Parse(new WmlBlock("unit_type", raw), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" });
            var idState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(editable, "id");
            if (idState.Kind != WmlFieldSafetyKind.PlainEditable || !WmlUnitReferenceProjection.TryLiteral(idState.PlainValue, out var actualId) ||
                !WmlUnitReferenceProjection.TryLiteral(entity.ExportSymbol, out var indexedId) || actualId != indexedId)
                return "A sibling base-unit identity is not a proven literal in its captured source.";
            if (paths.Add(source.Path) && paths.Count > MaximumBatchFiles)
                return "The literal sibling base-unit closure exceeds 64 physical files.";
            visiting.Add(entity.Id);
            foreach (var span in WmlDirectChildBlockScanner.FindSpans(editable, "base_unit"))
            {
                var baseRaw = string.Join(editable.NewLine, editable.Lines.Skip(span.StartLineIndex).Take(span.EndLineIndexExclusive - span.StartLineIndex));
                var baseBlock = WmlEditableBlockParser.Parse(new WmlBlock("base_unit", baseRaw), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" });
                var state = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(baseBlock, "id");
                if (state.Kind != WmlFieldSafetyKind.PlainEditable || !WmlUnitReferenceProjection.TryLiteral(state.PlainValue, out var baseId) || baseId.Length == 0)
                    return "A sibling base-unit reference needs preprocessing before its literal dependency can be established.";
                // Core/dependency declarations already in the captured prefix remain
                // the ordinary evaluator/registry's responsibility. Never search other packages.
                if (!bySymbol.TryGetValue(baseId, out var candidates)) continue;
                if (candidates.Length != 1) return "Base unit '" + baseId + "' has several indexed definitions in this package; its active source is ambiguous.";
                if (Visit(candidates[0], depth + 1) is { } problem) return problem;
            }
            visiting.Remove(entity.Id);
            completed.Add(entity.Id);
            return null;
        }

        static string Literal(string raw) { WmlUnitReferenceProjection.TryLiteral(raw, out var literal); return literal; }
    }
}
