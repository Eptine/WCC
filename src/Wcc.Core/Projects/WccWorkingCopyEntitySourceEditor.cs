using Wcc.Core.Library;

namespace Wcc.Core.Projects;

/// <summary>
/// Replaces one indexed entity span inside a current project file. The indexed
/// file hash and the guarded text writer together prevent stale-span or racing
/// edits from overwriting external changes.
/// </summary>
public static class WccWorkingCopyEntitySourceEditor
{
    public static WccWorkingCopyTextSaveResult Replace(
        string projectRoot,
        WccSourceFileRecord sourceFile,
        WccEntityRecord entity,
        string replacementRawText)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidProjectRoot,
                ErrorMessage: "The open project does not have a valid root folder.");
        }
        if (string.IsNullOrWhiteSpace(sourceFile.StoragePath))
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                ErrorMessage: "The indexed source file has no storage path.");
        }
        if (!IsPathInsideRoot(projectRoot, sourceFile.StoragePath))
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.OutsideProjectRoot,
                sourceFile.StoragePath,
                "WCC will only edit entity sources inside the open project folder.");
        }
        if (entity.SourceSpan?.StartOffset is not { } start ||
            entity.SourceSpan.EndOffsetExclusive is not { } end ||
            start < 0 || end < start)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.RenderFailure,
                sourceFile.StoragePath,
                "The indexed entity has no exact source span.");
        }

        string currentText;
        try
        {
            currentText = File.ReadAllText(sourceFile.StoragePath);
        }
        catch (FileNotFoundException)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.SourceMissing,
                sourceFile.StoragePath,
                "The indexed source file no longer exists.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.IoFailure,
                sourceFile.StoragePath,
                ex.Message);
        }

        if (!string.Equals(
                WccContentHash.ComputeText(currentText),
                sourceFile.ContentHash,
                StringComparison.OrdinalIgnoreCase) ||
            end > currentText.Length)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.ExternalChangeDetected,
                sourceFile.StoragePath,
                "The project file changed after this entity was indexed.");
        }

        var indexedSlice = currentText[start..end];
        if (string.IsNullOrWhiteSpace(entity.ContentFingerprint) ||
            !MatchesEntityFingerprint(indexedSlice, entity.ContentFingerprint))
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.RenderFailure,
                sourceFile.StoragePath,
                "The definition is assembled from non-contiguous or merged source WML and cannot be edited as one exact span.");
        }

        var updatedText = currentText[..start] + replacementRawText + currentText[end..];
        return WccWorkingCopyTextFileSaver.Save(
            projectRoot,
            sourceFile.StoragePath,
            currentText,
            updatedText);
    }

    private static bool IsPathInsideRoot(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(
                Path.GetFullPath(root),
                Path.GetFullPath(path));
            return !Path.IsPathRooted(relative) &&
                   !string.Equals(relative, "..", StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool MatchesEntityFingerprint(
        string sourceSlice,
        string expectedFingerprint)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            sourceSlice,
            sourceSlice.Trim(),
            sourceSlice.TrimEnd(),
            sourceSlice.Replace("\r\n", "\n", StringComparison.Ordinal),
            sourceSlice.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
            sourceSlice.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd(),
            Dedent(sourceSlice)
        };
        return candidates.Any(candidate => string.Equals(
            WccContentHash.ComputeText(candidate),
            expectedFingerprint,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
        var indentation = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        return string.Join(
            "\n",
            lines.Select(line => line.Length >= indentation
                ? line[indentation..]
                : line));
    }
}
