using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    /// <summary>Frozen source inspection context. It never grants a definition edit or file write.</summary>
    public async Task<WmlEditorReferenceContext> BuildRawEditorReferenceContextAsync(string text,
        string? sourcePath, WmlStaticSourceRange? origin = null, CancellationToken cancellationToken = default)
    {
        var snapshot = LibrarySnapshot;
        var revisions = snapshot is null ? null : IsProjectWorkspace &&
            FindCurrentProjectRevisionId(snapshot) is { } projectRevision
                ? new System.Collections.Generic.HashSet<WccPackageRevisionId>(
                    WccProjectAuthoringRevisionScope.GetVisibleRevisionIds(snapshot, projectRevision))
                : new System.Collections.Generic.HashSet<WccPackageRevisionId>(_activeLibraryRevisionIds);
        var context = ScenarioEditor.CreateRawEditorReferenceContext(text, sourcePath, [], origin);
        var candidates = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolver = snapshot is null ? null
                : new WccLibraryMacroExpansionDefinitionResolver(snapshot, revisions, sourcePath);
            var graph = new WmlMacroSourceGraphBuilder().Build(text, sourcePath, fallbackResolver: resolver);
            cancellationToken.ThrowIfCancellationRequested();
            var indexedFiles = snapshot?.SourceFilesById.Values.Where(file => file.StoragePath is not null)
                .ToLookup(file => InspectionSourcePath(file.StoragePath!),
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            return graph.Definitions.Select(node =>
            {
                // The graph contains exact frozen definition text, not the containing file revision.
                // A synthetic source preserves that distinction and cannot become authored write authority.
                var source = new WmlStaticSource(node.SourcePath ?? "definition:" + node.Id,
                    node.RawText, isAuthored: false);
                var indexedHashes = node.SourcePath is null || indexedFiles is null ? Array.Empty<string>()
                    : indexedFiles[InspectionSourcePath(node.SourcePath)]
                        .Where(file => node.PackageRevisionId is null ||
                            string.Equals(file.PackageRevisionId.ToString(), node.PackageRevisionId, StringComparison.Ordinal))
                        .Select(file => file.ContentHash).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return new WmlEditorDefinitionCandidate(node.MacroName!, new(source, 0, source.Text.Length),
                    node.SourceLabel)
                {
                    SourceStartLine = node.SourceStartLine,
                    SourceRevisionId = node.PackageRevisionId,
                    SourceContentHash = indexedHashes.Length == 1 ? indexedHashes[0] : null
                };
            }).ToArray();
        }, cancellationToken);
        return context.WithCandidates(candidates);
    }

    private static string InspectionSourcePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return path; }
    }
}
