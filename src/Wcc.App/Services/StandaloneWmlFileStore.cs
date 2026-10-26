using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Wcc.App.Services;

public enum StandaloneWmlFileEncoding
{
    Utf8WithoutBom,
    Utf8WithBom,
    Utf16LittleEndianWithBom,
    Utf16BigEndianWithBom,
    Utf32LittleEndianWithBom,
    Utf32BigEndianWithBom
}

public sealed record StandaloneWmlFileSnapshot(string Path, string Text,
    StandaloneWmlFileEncoding Encoding = StandaloneWmlFileEncoding.Utf8WithoutBom);

/// <summary>
/// Portable, full-file RAW editing. This service does not evaluate WML, consult
/// project indexes or detect external edits. Saving explicitly overwrites the
/// selected file using its original supported encoding and exact buffer text.
/// </summary>
public class StandaloneWmlFileStore
{
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetExtension(fullPath), ".cfg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Open a WML .cfg file.", nameof(path));
        return fullPath;
    }

    public virtual async Task<StandaloneWmlFileSnapshot> OpenAsync(string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(path);
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var (encodingKind, encoding, prefixLength) = DetectEncoding(bytes);
        return new StandaloneWmlFileSnapshot(fullPath,
            encoding.GetString(bytes, prefixLength, bytes.Length - prefixLength), encodingKind);
    }

    /// <summary>
    /// Revalidates a captured owned route before a campaign-scoped RAW operation.
    /// Standalone file editing retains its explicit linked-file behavior.
    /// </summary>
    public virtual void ValidateOwnedSourcePath(string rootPath, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var source = NormalizePath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!source.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("The checked file is outside its captured campaign root. Rerun checks before applying.");
        var file = new FileInfo(source);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget is not null)
            throw new InvalidOperationException("The checked file route changed since capture. Rerun checks before applying.");
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
                throw new InvalidOperationException("The checked source directory route changed since capture. Rerun checks before applying.");
    }

    public virtual async Task SaveAsync(StandaloneWmlFileSnapshot file, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        var fullPath = NormalizePath(file.Path);
        // Keep the selected entry (and tab identity) intact when it is a file
        // symlink. Resolve its current final target for this Save attempt.
        var selectedFile = new FileInfo(fullPath);
        var destinationPath = selectedFile.LinkTarget is not null
            ? selectedFile.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath : fullPath;
        UnixFileMode? unixMode = !OperatingSystem.IsWindows() && File.Exists(destinationPath)
            ? File.GetUnixFileMode(destinationPath) : null;
        var encoding = GetEncoding(file.Encoding);
        var payload = encoding.GetBytes(text);
        var preamble = encoding.GetPreamble();
        var directory = System.IO.Path.GetDirectoryName(destinationPath)!;
        var temporaryPath = System.IO.Path.Combine(directory, ".wcc-wml-" + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemporaryPath = false;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                ownsTemporaryPath = true;
                await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows() && unixMode is { } mode)
                File.SetUnixFileMode(temporaryPath, mode);
            // Same-folder rename replaces a complete staged file. This is a
            // portable single-file save, not the guarded WorkingCopy writer.
            File.Move(temporaryPath, destinationPath, overwrite: true);
            ownsTemporaryPath = false;
        }
        finally
        {
            if (ownsTemporaryPath)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    // The original save exception remains visible. A failed
                    // cleanup never authorizes deleting another source file.
                }
            }
        }
    }

    private static (StandaloneWmlFileEncoding Kind, Encoding Encoding, int PrefixLength)
        DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
            return (StandaloneWmlFileEncoding.Utf32LittleEndianWithBom,
                GetEncoding(StandaloneWmlFileEncoding.Utf32LittleEndianWithBom), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
            return (StandaloneWmlFileEncoding.Utf32BigEndianWithBom,
                GetEncoding(StandaloneWmlFileEncoding.Utf32BigEndianWithBom), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            return (StandaloneWmlFileEncoding.Utf8WithBom,
                GetEncoding(StandaloneWmlFileEncoding.Utf8WithBom), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
            return (StandaloneWmlFileEncoding.Utf16LittleEndianWithBom,
                GetEncoding(StandaloneWmlFileEncoding.Utf16LittleEndianWithBom), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            return (StandaloneWmlFileEncoding.Utf16BigEndianWithBom,
                GetEncoding(StandaloneWmlFileEncoding.Utf16BigEndianWithBom), 2);
        return (StandaloneWmlFileEncoding.Utf8WithoutBom,
            GetEncoding(StandaloneWmlFileEncoding.Utf8WithoutBom), 0);
    }

    private static Encoding GetEncoding(StandaloneWmlFileEncoding kind) => kind switch
    {
        StandaloneWmlFileEncoding.Utf8WithoutBom => new UTF8Encoding(false, true),
        StandaloneWmlFileEncoding.Utf8WithBom => new UTF8Encoding(true, true),
        StandaloneWmlFileEncoding.Utf16LittleEndianWithBom => new UnicodeEncoding(false, true, true),
        StandaloneWmlFileEncoding.Utf16BigEndianWithBom => new UnicodeEncoding(true, true, true),
        StandaloneWmlFileEncoding.Utf32LittleEndianWithBom => new UTF32Encoding(false, true, true),
        StandaloneWmlFileEncoding.Utf32BigEndianWithBom => new UTF32Encoding(true, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
