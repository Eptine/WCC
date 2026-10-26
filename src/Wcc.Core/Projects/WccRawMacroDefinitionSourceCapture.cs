using System.Text;
using System.Text.Json;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

/// <summary>
/// Captures the physical declaration behind one static definition candidate for manual RAW editing.
/// This read-only observation proves neither activation at an invocation nor permission to persist.
/// The editing host must still create a fresh WccRawMacroDefinitionEditSession with its own policy.
/// </summary>
public static class WccRawMacroDefinitionSourceCapture
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <param name="sourceStartLine">The zero-based physical declaration line, when captured by the index.</param>
    /// <param name="sourceContentHash">The containing file hash, never the synthetic declaration source hash.</param>
    public static WmlStaticSourceRange? TryCapture(WccLibrarySnapshot snapshot,
        WccPackageRevisionId rootRevisionId, string projectRootPath, string macroName,
        WmlStaticSourceRange candidate, int? sourceStartLine, string? sourceRevisionId,
        string? sourceContentHash, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            if (snapshot is null || candidate?.Source is null || string.IsNullOrWhiteSpace(macroName) ||
                !WmlPreprocessorToken.IsLiteralName(macroName) || sourceStartLine is < 0 ||
                candidate.StartOffset < 0 || candidate.Length <= 0 ||
                candidate.StartOffset > candidate.Source.Text.Length ||
                candidate.Length > candidate.Source.Text.Length - candidate.StartOffset)
                return Refuse("One exact static declaration candidate and physical source scope are required.", out diagnostic);
            if (sourceRevisionId is not null &&
                (!Guid.TryParse(sourceRevisionId, out var revision) || revision != rootRevisionId.Value))
                return Refuse("The static definition candidate belongs to another or historical revision.", out diagnostic);

            var manifestIdentity = WccContentHash.ComputeText(JsonSerializer.Serialize(snapshot.Manifest));
            if (CheckScope(snapshot, rootRevisionId, projectRootPath, candidate.Source.Path,
                    out var root, out var file) is { } scopeProblem)
                return Refuse(scopeProblem, out diagnostic);
            var indexedFile = file!;
            if (sourceContentHash is not null &&
                !string.Equals(sourceContentHash, indexedFile.ContentHash, StringComparison.OrdinalIgnoreCase))
                return Refuse("The captured containing-file hash differs from the current source index.", out diagnostic);
            if (candidate.Source.IsAuthored &&
                !string.Equals(candidate.Source.ContentHash, indexedFile.ContentHash, StringComparison.OrdinalIgnoreCase))
                return Refuse("The authored candidate does not match the current indexed containing file.", out diagnostic);

            // Bootstrap the expected text only after the exact indexed path is established.
            // Native Capture re-decodes strictly and verifies bytes, encoding, identities and route;
            // this ordinary read alone never supplies physical-source evidence.
            var text = candidate.Source.IsAuthored ? candidate.Source.Text
                : File.ReadAllText(indexedFile.StoragePath!, new UTF8Encoding(false, true));
            var captured = WccTextDiskRevisionCapture.Capture(root!, indexedFile.StoragePath, text);
            if (!captured.IsCaptured || captured.Revision is null)
                return Refuse(captured.ErrorMessage ?? "The physical source revision could not be captured.", out diagnostic);
            var disk = captured.Revision;
            if (!string.Equals(disk.DecodedTextSha256, indexedFile.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                disk.RawByteLength != indexedFile.Length)
                return Refuse("The physical file differs from its exact indexed hash or byte length.", out diagnostic);

            var source = new WmlStaticSource(disk.SourcePath, disk.Text);
            var structure = WmlAuthoredStructureScanner.Scan(source.Text);
            if (structure.Problem is { } structureProblem)
                return Refuse("The physical declaration source is malformed: " + structureProblem, out diagnostic);
            var matches = new List<WmlStaticSourceRange>();
            foreach (var opening in structure.Directives.Where(item => item.Name == "define" && item.DefinitionDepth == 0))
            {
                if (sourceStartLine is { } line && opening.Line != line) continue;
                var first = structure.Lines[opening.Line];
                var header = source.Text.AsSpan(first.StartOffset, first.ContentEndOffset - first.StartOffset);
                var words = header.Trim().ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2 || words[0] != "#define" ||
                    !string.Equals(words[1], macroName, StringComparison.Ordinal)) continue;
                var close = structure.Directives.FirstOrDefault(item => item.Name == "enddef" &&
                    item.DefinitionDepth == 1 && item.Line > opening.Line);
                if (close.Name is null) continue;
                var last = structure.Lines[close.Line];
                if (candidate.Source.IsAuthored)
                {
                    var tokenStart = first.StartOffset + header.Length - header.TrimStart().Length;
                    var end = candidate.StartOffset + candidate.Length;
                    if ((candidate.StartOffset != first.StartOffset && candidate.StartOffset != tokenStart) ||
                        (end != last.ContentEndOffset && end != last.EndOffsetExclusive)) continue;
                    matches.Add(new(source, candidate.StartOffset, candidate.Length));
                }
                else
                {
                    var physical = new WmlStaticSourceRange(source, first.StartOffset,
                        last.EndOffsetExclusive - first.StartOffset);
                    if (MatchesDeclaration(candidate.Text, physical.Text)) matches.Add(physical);
                }
            }
            if (matches.Count != 1)
                return Refuse(matches.Count == 0
                    ? "The static candidate does not match one complete physical same-name declaration at its captured line."
                    : "Multiple physical declarations match this static candidate; an exact declaration line is required.", out diagnostic);
            if (manifestIdentity != WccContentHash.ComputeText(JsonSerializer.Serialize(snapshot.Manifest)) ||
                CheckScope(snapshot, rootRevisionId, projectRootPath, candidate.Source.Path, out _, out _) is not null)
                return Refuse("The campaign source index or scope changed during physical capture.", out diagnostic);
            return matches[0];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Refuse("The physical definition source could not be captured: " + error.Message, out diagnostic);
        }
    }

    private static bool MatchesDeclaration(string candidate, string physical)
        => string.Equals(candidate, physical, StringComparison.Ordinal) ||
           (physical.EndsWith("\r\n", StringComparison.Ordinal) &&
            string.Equals(candidate, physical[..^2], StringComparison.Ordinal)) ||
           (physical.EndsWith('\n') && !physical.EndsWith("\r\n", StringComparison.Ordinal) &&
            string.Equals(candidate, physical[..^1], StringComparison.Ordinal));

    private static string? CheckScope(WccLibrarySnapshot snapshot, WccPackageRevisionId revisionId,
        string projectRoot, string sourcePath, out string? root, out WccSourceFileRecord? file)
    {
        root = null;
        file = null;
        if (!Path.IsPathFullyQualified(projectRoot) || !Path.IsPathFullyQualified(sourcePath))
            return "The campaign root and candidate source must be fully qualified physical paths.";
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var path = Path.GetFullPath(sourcePath);
        if (!snapshot.RevisionsById.TryGetValue(revisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable ||
            package.ParentPackageId is not null || package.SourceKind != WccPackageSourceKinds.WccProject ||
            package.StorageMode != WccPackageStorageMode.WorkingCopy || package.CurrentRevisionId != revisionId ||
            (package.ContentKind != WccPackageContentKinds.Campaign && package.ContentKind != WccPackageContentKinds.Project) ||
            string.IsNullOrWhiteSpace(package.SourceLocation) || !Path.IsPathFullyQualified(package.SourceLocation) ||
            !PathComparer.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SourceLocation))))
            return "The candidate requires the selected available current campaign WorkingCopy revision.";
        if (!IsInsideRoot(root, path)) return "The candidate source is outside the selected campaign root.";
        var owners = snapshot.PackagesById.Values.Where(item => item.IsAvailable &&
            !string.IsNullOrWhiteSpace(item.SourceLocation) &&
            (!Path.IsPathFullyQualified(item.SourceLocation) || IsInsideRoot(Path.GetFullPath(item.SourceLocation), path))).ToArray();
        if (owners.Length != 1 || owners[0].Id != package.Id)
            return "The candidate physical source has ambiguous package ownership.";
        var files = snapshot.SourceFilesById.Values.Where(item => item.PackageRevisionId == revisionId &&
            item.StoragePath is not null && Path.IsPathFullyQualified(item.StoragePath) &&
            PathComparer.Equals(Path.GetFullPath(item.StoragePath), path)).ToArray();
        if (files.Length != 1 || files[0].FileType != WccSourceFileTypes.Wml ||
            !IsCanonicalRelativePath(files[0].RelativePath) ||
            !PathComparer.Equals(Path.GetFullPath(Path.Combine(root, files[0].RelativePath)), path))
            return "The candidate must match one exact indexed campaign WML file and relative path.";
        file = files[0];
        return null;
    }

    private static bool IsCanonicalRelativePath(string relative)
        => !string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative) && !relative.Contains(':') &&
           relative.Replace('\\', '/').Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    private static bool IsInsideRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.TrimEndingDirectorySeparator(root), path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static WmlStaticSourceRange? Refuse(string problem, out string? diagnostic)
    {
        diagnostic = problem;
        return null;
    }
}
