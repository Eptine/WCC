using System.Text;

namespace Wcc.Core.Projects;

public enum WccWorkingCopyTextSaveStatus
{
    Saved,
    Unchanged,
    InvalidProjectRoot,
    InvalidSource,
    RenderFailure,
    OutsideProjectRoot,
    SourceMissing,
    MissingBaseline,
    ExternalChangeDetected,
    IoFailure
}

public sealed record WccWorkingCopyTextSaveResult(
    WccWorkingCopyTextSaveStatus Status,
    string? SourcePath = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess =>
        Status is WccWorkingCopyTextSaveStatus.Saved or WccWorkingCopyTextSaveStatus.Unchanged;

    public bool Changed => Status == WccWorkingCopyTextSaveStatus.Saved;
}

/// <summary>
/// Atomically replaces a text file inside the open project after verifying that
/// its on-disk contents still match the text WCC originally loaded.
/// </summary>
public static class WccWorkingCopyTextFileSaver
{
    public static WccWorkingCopyTextSaveResult Save(
        string projectRoot,
        string? sourcePath,
        string? expectedText,
        string updatedText)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidProjectRoot,
                ErrorMessage: "The open project does not have a valid root folder.");
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                ErrorMessage: "The current editor content is not backed by a project file.");
        }

        if (updatedText is null)
            throw new ArgumentNullException(nameof(updatedText));

        string normalizedRoot;
        string normalizedSource;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
            normalizedSource = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.InvalidSource,
                sourcePath,
                ex.Message);
        }

        if (!IsPathInsideRoot(normalizedRoot, normalizedSource))
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.OutsideProjectRoot,
                normalizedSource,
                "WCC will only save files located inside the open project folder.");
        }

        if (!File.Exists(normalizedSource))
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.SourceMissing,
                normalizedSource,
                "The source file no longer exists.");
        }

        if (expectedText is null)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.MissingBaseline,
                normalizedSource,
                "WCC has no loaded-file snapshot to verify before saving.");
        }

        string? temporaryPath = null;
        try
        {
            var sourceBytes = File.ReadAllBytes(normalizedSource);
            var encoding = DetectEncoding(sourceBytes, out var preambleLength);
            var currentText = encoding.GetString(sourceBytes, preambleLength, sourceBytes.Length - preambleLength);

            if (!string.Equals(currentText, expectedText, StringComparison.Ordinal))
            {
                return new WccWorkingCopyTextSaveResult(
                    WccWorkingCopyTextSaveStatus.ExternalChangeDetected,
                    normalizedSource,
                    "The file changed on disk after WCC loaded it. WCC left both versions untouched.");
            }

            if (string.Equals(currentText, updatedText, StringComparison.Ordinal))
            {
                return new WccWorkingCopyTextSaveResult(
                    WccWorkingCopyTextSaveStatus.Unchanged,
                    normalizedSource);
            }

            var directory = Path.GetDirectoryName(normalizedSource)!;
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(normalizedSource)}.{Guid.NewGuid():N}.wcc.tmp");

            WriteAllTextWithEncoding(temporaryPath, updatedText, encoding);
            File.Move(temporaryPath, normalizedSource, overwrite: true);
            temporaryPath = null;

            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.Saved,
                normalizedSource);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WccWorkingCopyTextSaveResult(
                WccWorkingCopyTextSaveStatus.IoFailure,
                normalizedSource,
                ex.Message);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup; the original project file was not replaced.
                }
            }
        }
    }

    private static bool IsPathInsideRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
               && !string.Equals(relative, "..", StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF32.GetPreamble()))
            return WithPreamble(Encoding.UTF32, out preambleLength);

        var utf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (bytes.AsSpan().StartsWith(utf32BigEndian.GetPreamble()))
            return WithPreamble(utf32BigEndian, out preambleLength);

        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            return WithPreamble(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), out preambleLength);

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
            return WithPreamble(Encoding.Unicode, out preambleLength);

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            return WithPreamble(Encoding.BigEndianUnicode, out preambleLength);

        preambleLength = 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static Encoding WithPreamble(Encoding encoding, out int preambleLength)
    {
        preambleLength = encoding.GetPreamble().Length;
        return encoding;
    }

    private static void WriteAllTextWithEncoding(string path, string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var content = encoding.GetBytes(text);

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);

        if (preamble.Length > 0)
            stream.Write(preamble);

        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }
}
