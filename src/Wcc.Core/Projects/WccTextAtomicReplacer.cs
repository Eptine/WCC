using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wcc.Core.Projects;

public enum WccTextAtomicReplaceStatus
{
    Succeeded,
    SucceededWithCleanupWarning,
    UnsupportedPlatform,
    InvalidExpectedRevision,
    InvalidCandidate,
    CandidateHashMismatch,
    NoChange,
    StaleRevision,
    UnsafePath,
    IoFailure,
    RolledBackStaleRevision, // Reserved legacy values; rollback is no longer performed.
    RolledBackValidationFailure,
    RecoveryRequired,
    FileInUse
}

public sealed record WccTextAtomicReplaceResult(
    WccTextAtomicReplaceStatus Status,
    string? SourcePath = null,
    string? RecoveryBackupPath = null,
    string? ErrorMessage = null)
{
    /// <summary>A retained staging path after a partial move or unsafe cleanup; diagnostic, not write authority.</summary>
    public string? RecoveryCandidatePath { get; init; }

    public bool IsSuccess =>
        Status is WccTextAtomicReplaceStatus.Succeeded
            or WccTextAtomicReplaceStatus.SucceededWithCleanupWarning;

    public bool AuthorizesPersistence => false;
}

/// <summary>
/// The non-public mutating boundary for replacing one captured text revision.
/// Public result data is diagnostic only and never grants another write.
/// </summary>
public sealed partial class WccTextAtomicReplacer
{
    private static readonly ConcurrentDictionary<string, object> SourceLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static StringComparer DiskPathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison DiskPathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private WccTextAtomicReplacer()
    {
    }

    internal static WccTextAtomicReplaceResult Replace(
        WccTextDiskRevision expected,
        string candidateText,
        string expectedCandidateTextSha256) =>
        ReplaceWithTestHooks(
            expected,
            candidateText,
            expectedCandidateTextSha256,
            beforeReplace: null,
            afterReplace: null);

    private static WccTextAtomicReplaceResult ReplaceWithTestHooks(
        WccTextDiskRevision expected,
        string candidateText,
        string expectedCandidateTextSha256,
        Action? beforeReplace,
        Action? afterReplace) =>
        ReplaceWithNativeTestHooks(expected, candidateText, expectedCandidateTextSha256,
            beforeReplace, afterReplace, null);

    private static WccTextAtomicReplaceResult ReplaceWithNativeTestHooks(
        WccTextDiskRevision expected,
        string candidateText,
        string expectedCandidateTextSha256,
        Action? beforeReplace,
        Action? afterReplace,
        Func<string, string, string, int>? replaceOverride)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return Failure(WccTextAtomicReplaceStatus.UnsupportedPlatform, expected?.SourcePath, "Atomic replacement is implemented for Windows and Linux.");

        if (!IsUsableExpectedRevision(expected))
            return Failure(WccTextAtomicReplaceStatus.InvalidExpectedRevision, expected?.SourcePath, "The expected disk revision is missing or malformed.");

        if (candidateText is null || !ContainsOnlyValidUnicode(candidateText))
            return Failure(WccTextAtomicReplaceStatus.InvalidCandidate, expected.SourcePath, "The candidate is missing or contains an unpaired surrogate.");

        if (!IsSha256(expectedCandidateTextSha256))
            return Failure(WccTextAtomicReplaceStatus.InvalidCandidate, expected.SourcePath, "The candidate text SHA-256 is malformed.");

        string actualCandidateHash;
        byte[] candidateBytes;
        try
        {
            actualCandidateHash = HashText(candidateText);
            candidateBytes = Encode(candidateText, expected.EncodingKind);
        }
        catch (EncoderFallbackException ex)
        {
            return Failure(WccTextAtomicReplaceStatus.InvalidCandidate, expected.SourcePath, ex.Message);
        }

        if (!string.Equals(actualCandidateHash, expectedCandidateTextSha256, StringComparison.OrdinalIgnoreCase))
            return Failure(WccTextAtomicReplaceStatus.CandidateHashMismatch, expected.SourcePath, "The candidate text does not match its expected SHA-256.");

        if (string.Equals(candidateText, expected.Text, StringComparison.Ordinal))
            return Failure(WccTextAtomicReplaceStatus.NoChange, expected.SourcePath, "The candidate does not change the captured text.");

        var sourceLock = SourceLocks.GetOrAdd(expected.SourcePath, static _ => new object());
        lock (sourceLock)
        {
            return ReplaceSerialized(
                expected,
                candidateText,
                actualCandidateHash,
                candidateBytes,
                beforeReplace,
                afterReplace,
                replaceOverride);
        }
    }

    private static WccTextAtomicReplaceResult ReplaceSerialized(
        WccTextDiskRevision expected,
        string candidateText,
        string candidateTextHash,
        byte[] candidateBytes,
        Action? beforeReplace,
        Action? afterReplace,
        Func<string, string, string, int>? replaceOverride)
    {
        var initial = RecaptureExpected(expected);
        if (initial is not null)
            return initial;

        var stagePath = Path.Combine(expected.SourceParentPath, $".wcc-stage-{Guid.NewGuid():N}.tmp");
        // Linux exchange retains the displaced inode at the exact staging name.
        // There is no fallible second rename between publication and recovery.
        var backupPath = OperatingSystem.IsLinux() ? stagePath
            : Path.Combine(expected.SourceParentPath, $".wcc-backup-{Guid.NewGuid():N}.tmp");
        WccWindowsFileIdentity? stageIdentity = null;
        WccTextDiskRouteLease? routeLease = null;
        var replacementCompleted = false;
        var retainStage = false;
        string? stageCleanupError = null;
        WccTextAtomicReplaceResult result;

        try
        {
            result = Execute();
        }
        // Once replacement has succeeded, every failure preserves the displaced source.
        // Never perform an automatic second replacement: that could erase a newer save.
        catch (Exception ex) when (replacementCompleted)
        {
            result = Recovery($"The replacement completed but could not be verified: {ex.Message}");
        }
        catch (WccTextDiskRouteChangedException ex)
        {
            result = Failure(WccTextAtomicReplaceStatus.StaleRevision, expected.SourcePath, ex.Message);
        }
        catch (Exception ex) when (IsIoException(ex))
        {
            result = Failure(IsFileInUse(ex) ? WccTextAtomicReplaceStatus.FileInUse
                : WccTextAtomicReplaceStatus.IoFailure, expected.SourcePath, ex.Message);
        }
        finally
        {
            try
            {
                // After a successful move the staging name is no longer ours. Do not
                // touch a new file another program might subsequently create there.
                if (!replacementCompleted && !retainStage && stageIdentity is not null)
                    stageCleanupError = TryDeleteOwnedFile(stagePath, stageIdentity,
                        candidateBytes.LongLength, HashBytes(candidateBytes), 1);
            }
            finally
            {
                // Keep the directory chain pinned until all temporary cleanup ends.
                routeLease?.Dispose();
            }
        }

        return stageCleanupError is null ? result : result with
        {
            RecoveryCandidatePath = stagePath,
            ErrorMessage = $"{result.ErrorMessage} The staging artifact was retained: {stageCleanupError}".Trim()
        };

        WccTextAtomicReplaceResult Recovery(string message) =>
            new(WccTextAtomicReplaceStatus.RecoveryRequired, expected.SourcePath, backupPath, message);

        WccTextAtomicReplaceResult Execute()
        {
            routeLease = WccTextDiskRevisionCapture.AcquireRouteLease(expected);
            if (OperatingSystem.IsLinux()) WriteExclusiveLinux(routeLease, stagePath, candidateBytes, out stageIdentity);
            else WriteExclusive(stagePath, candidateBytes, out stageIdentity);
            var staged = WccTextDiskRevisionCapture.Capture(expected.ProjectRootPath, stagePath, candidateText);
            if (!IsExactStagedCandidate(staged, expected, candidateTextHash, candidateBytes.LongLength)
                || staged.Revision!.SourceIdentity != stageIdentity)
            {
                return Failure(WccTextAtomicReplaceStatus.IoFailure, expected.SourcePath,
                    "The flushed staging file did not recapture as the exact candidate.");
            }

            var finalCheck = RecaptureExpected(expected);
            if (finalCheck is not null)
                return finalCheck;

            beforeReplace?.Invoke();
            var error = replaceOverride is not null ? replaceOverride(expected.SourcePath, stagePath, backupPath)
                : OperatingSystem.IsLinux() ? ReplaceLinux(expected, stagePath, stageIdentity!, routeLease, candidateBytes)
                : ReplaceNative(expected.SourcePath, stagePath, backupPath);
            if (error != 0)
            {
                if (OperatingSystem.IsLinux())
                {
                    retainStage = true;
                    return new(WccTextAtomicReplaceStatus.RecoveryRequired, expected.SourcePath,
                        ErrorMessage: new Win32Exception(error, "Linux could not confirm the atomic exchange; inspect the retained source and staging names before saving again.").Message)
                        { RecoveryCandidatePath = stagePath };
                }
                // With a requested backup, 1175/1176 leave both original names intact.
                // 1177 instead moves the source to backup and leaves the candidate staged.
                // An unexpected backup on another error is also evidence we must retain.
                if (error == ErrorUnableToMoveReplacement2 || File.Exists(backupPath))
                {
                    retainStage = true;
                    return Recovery(new Win32Exception(error,
                        "Windows reported an incomplete replacement; inspect the retained backup and candidate before saving again.").Message)
                        with { RecoveryCandidatePath = stagePath };
                }

                return Failure(error is ErrorSharingViolation or ErrorLockViolation
                        ? WccTextAtomicReplaceStatus.FileInUse : WccTextAtomicReplaceStatus.IoFailure,
                    expected.SourcePath, new Win32Exception(error,
                        error is ErrorSharingViolation or ErrorLockViolation
                            ? "The file is in use. Close the other writer and try saving again."
                            : "Windows could not atomically replace the source file.").Message);
            }

            replacementCompleted = true; // Must precede hooks and any fallible verification.
            if (OperatingSystem.IsLinux()) FlushAndVerifyLinuxRoute(routeLease);
            afterReplace?.Invoke();
            var backupCapture = WccTextDiskRevisionCapture.Capture(
                expected.ProjectRootPath, backupPath, expected.Text);
            var destinationCapture = WccTextDiskRevisionCapture.Capture(
                expected.ProjectRootPath, expected.SourcePath, candidateText);
            if (!IsExactDisplacedRevision(backupCapture, expected)
                || !IsExactDestinationCandidate(destinationCapture, expected,
                    AssertRevision(staged), candidateTextHash, candidateBytes.LongLength))
            {
                return Recovery(
                    "The source changed during saving or the replacement could not be verified. The current source and displaced backup were left untouched; resolve the conflict before saving again.");
            }

            var cleanupError = TryDeleteOwnedFile(backupPath, expected.SourceIdentity,
                expected.RawByteLength, expected.RawByteSha256, expected.SourceHardLinkCount);
            return cleanupError is null
                ? new(WccTextAtomicReplaceStatus.Succeeded, expected.SourcePath)
                : new(WccTextAtomicReplaceStatus.SucceededWithCleanupWarning, expected.SourcePath, backupPath,
                    $"The change succeeded, but its baseline backup was retained: {cleanupError}");
        }
    }

    private static int ReplaceNative(string sourcePath, string stagePath, string backupPath) =>
        ReplaceFileWindows(sourcePath, stagePath, backupPath, 0, IntPtr.Zero, IntPtr.Zero)
            ? 0 : Marshal.GetLastWin32Error();

    private static WccTextAtomicReplaceResult? RecaptureExpected(WccTextDiskRevision expected)
    {
        var capture = WccTextDiskRevisionCapture.Capture(
            expected.ProjectRootPath,
            expected.SourcePath,
            expected.Text);
        if (capture.NativeErrorCode is { } error && IsNativeFileInUseError(error))
            return Failure(WccTextAtomicReplaceStatus.FileInUse, expected.SourcePath,
                "The file is in use. Close the other writer and try saving again.");

        if (capture.Status == WccTextDiskRevisionCaptureStatus.ReparsePointRejected
            || capture.Status is WccTextDiskRevisionCaptureStatus.InvalidProjectRoot
                or WccTextDiskRevisionCaptureStatus.InvalidSource
                or WccTextDiskRevisionCaptureStatus.OutsideProjectRoot)
        {
            return Failure(WccTextAtomicReplaceStatus.UnsafePath, expected.SourcePath, capture.ErrorMessage ?? "The source route is unsafe.");
        }

        if (!capture.IsCaptured || capture.Revision is null || !expected.IsSameRevisionAs(capture.Revision))
            return Failure(WccTextAtomicReplaceStatus.StaleRevision, expected.SourcePath, capture.ErrorMessage ?? "The source revision is stale.");

        return null;
    }

    private static bool IsExactStagedCandidate(
        WccTextDiskRevisionCaptureResult capture,
        WccTextDiskRevision expected,
        string textHash,
        long byteLength) =>
        capture.Revision is { } revision
        && capture.IsCaptured
        && revision.EncodingKind == expected.EncodingKind
        && revision.RawByteLength == byteLength
        && string.Equals(revision.DecodedTextSha256, textHash, StringComparison.Ordinal)
        && revision.ProjectRootIdentity == expected.ProjectRootIdentity
        && revision.SourceParentIdentity == expected.SourceParentIdentity
        && string.Equals(revision.RouteIdentitySha256, expected.RouteIdentitySha256, StringComparison.Ordinal)
        && revision.SourceHardLinkCount == 1;

    private static bool IsExactDisplacedRevision(
        WccTextDiskRevisionCaptureResult capture,
        WccTextDiskRevision expected) =>
        capture.Revision is { } revision
        && capture.IsCaptured
        && revision.EncodingKind == expected.EncodingKind
        && revision.RawByteLength == expected.RawByteLength
        && string.Equals(revision.RawByteSha256, expected.RawByteSha256, StringComparison.Ordinal)
        && string.Equals(revision.DecodedTextSha256, expected.DecodedTextSha256, StringComparison.Ordinal)
        && revision.ProjectRootIdentity == expected.ProjectRootIdentity
        && revision.SourceParentIdentity == expected.SourceParentIdentity
        && revision.SourceIdentity == expected.SourceIdentity
        && revision.SourceHardLinkCount == expected.SourceHardLinkCount
        && string.Equals(revision.RouteIdentitySha256, expected.RouteIdentitySha256, StringComparison.Ordinal);

    private static bool IsExactDestinationCandidate(
        WccTextDiskRevisionCaptureResult capture,
        WccTextDiskRevision expected,
        WccTextDiskRevision staged,
        string textHash,
        long byteLength) =>
        capture.Revision is { } revision
        && capture.IsCaptured
        && revision.EncodingKind == expected.EncodingKind
        && revision.RawByteLength == byteLength
        && string.Equals(revision.RawByteSha256, staged.RawByteSha256, StringComparison.Ordinal)
        && string.Equals(revision.DecodedTextSha256, textHash, StringComparison.Ordinal)
        && revision.ProjectRootIdentity == expected.ProjectRootIdentity
        && revision.SourceParentIdentity == expected.SourceParentIdentity
        && revision.SourceIdentity == staged.SourceIdentity
        && revision.SourceHardLinkCount == 1
        && string.Equals(revision.RouteIdentitySha256, expected.RouteIdentitySha256, StringComparison.Ordinal);

    private static WccTextDiskRevision AssertRevision(WccTextDiskRevisionCaptureResult result) =>
        result.Revision ?? throw new InvalidOperationException("A verified staged revision was unexpectedly absent.");

    private static bool IsUsableExpectedRevision(WccTextDiskRevision? revision) =>
        revision is not null
        && Path.IsPathFullyQualified(revision.ProjectRootPath)
        && Path.IsPathFullyQualified(revision.SourceParentPath)
        && Path.IsPathFullyQualified(revision.SourcePath)
        && string.Equals(Path.GetDirectoryName(revision.SourcePath), revision.SourceParentPath, DiskPathComparison)
        && IsSha256(revision.RawByteSha256)
        && IsSha256(revision.DecodedTextSha256)
        && IsSha256(revision.RouteIdentitySha256)
        && ContainsOnlyValidUnicode(revision.Text)
        && string.Equals(HashText(revision.Text), revision.DecodedTextSha256, StringComparison.OrdinalIgnoreCase)
        && revision.RawByteLength >= 0
        && revision.SourceHardLinkCount > 0
        && !revision.ProjectRootIdentity.IsEmpty
        && !revision.SourceParentIdentity.IsEmpty
        && !revision.SourceIdentity.IsEmpty;

    private static byte[] Encode(string text, WccTextDiskEncodingKind kind)
    {
        Encoding encoding = kind switch
        {
            WccTextDiskEncodingKind.Utf8WithoutBom => new UTF8Encoding(false, true),
            WccTextDiskEncodingKind.Utf8WithBom => new UTF8Encoding(true, true),
            WccTextDiskEncodingKind.Utf16LittleEndianWithBom => new UnicodeEncoding(false, true, true),
            WccTextDiskEncodingKind.Utf16BigEndianWithBom => new UnicodeEncoding(true, true, true),
            WccTextDiskEncodingKind.Utf32LittleEndianWithBom => new UTF32Encoding(false, true, true),
            WccTextDiskEncodingKind.Utf32BigEndianWithBom => new UTF32Encoding(true, true, true),
            _ => throw new EncoderFallbackException("The captured encoding kind is unsupported.")
        };
        var content = encoding.GetBytes(text);
        var preamble = kind == WccTextDiskEncodingKind.Utf8WithoutBom
            ? ReadOnlySpan<byte>.Empty
            : encoding.GetPreamble();
        var bytes = new byte[preamble.Length + content.Length];
        preamble.CopyTo(bytes);
        content.CopyTo(bytes.AsSpan(preamble.Length));
        return bytes;
    }

    private static void WriteExclusive(string path, ReadOnlySpan<byte> bytes, out WccWindowsFileIdentity? identity)
    {
        identity = null;
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        // Capture ownership before the write so even a partial write cannot cause
        // cleanup to delete a different file later occupying this pathname.
        identity = WccTextDiskRevisionCapture.ReadHandleIdentityForVerification(stream.SafeFileHandle);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static RawFileSnapshot CaptureRawFile(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleWindows(handle, out var before))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read verification metadata.");
        var beforeIdentity = WccTextDiskRevisionCapture.ReadHandleIdentityForVerification(handle);
        if ((before.FileAttributes & (FileAttributeDirectory | FileAttributeDevice | FileAttributeReparsePoint)) != 0)
            throw new IOException("A replacement artifact is not a regular non-reparse file.");
        var length = ((ulong)before.FileSizeHigh << 32) | before.FileSizeLow;
        if (length > (ulong)Array.MaxLength)
            throw new IOException("A replacement artifact is too large to verify.");
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0)
                throw new IOException("A replacement artifact changed while reading.");
            offset += read;
        }
        Span<byte> probe = stackalloc byte[1];
        if (RandomAccess.Read(handle, probe, bytes.LongLength) != 0
            || !GetFileInformationByHandleWindows(handle, out var after))
            throw new IOException("A replacement artifact changed while being verified.");
        var afterIdentity = WccTextDiskRevisionCapture.ReadHandleIdentityForVerification(handle);
        if (beforeIdentity != afterIdentity || !SameMetadata(before, after))
            throw new IOException("A replacement artifact changed while being verified.");
        return new RawFileSnapshot(
            bytes,
            afterIdentity,
            after.NumberOfLinks);
    }

    private static bool SameMetadata(ByHandleFileInformation left, ByHandleFileInformation right) =>
        left.FileAttributes == right.FileAttributes
        && left.FileSizeHigh == right.FileSizeHigh
        && left.FileSizeLow == right.FileSizeLow
        && left.NumberOfLinks == right.NumberOfLinks
        && left.VolumeSerialNumber == right.VolumeSerialNumber
        && left.FileIndexHigh == right.FileIndexHigh
        && left.FileIndexLow == right.FileIndexLow
        && left.LastWriteTime.High == right.LastWriteTime.High
        && left.LastWriteTime.Low == right.LastWriteTime.Low;

    // Verification and deletion use the SAME handle, opened without write/delete
    // sharing. A pathname check followed by File.Delete would reopen the race.
    private static string? TryDeleteOwnedFile(
        string path, WccWindowsFileIdentity identity, long length, string rawHash, uint linkCount)
    {
        // Linux has no unlink-by-verified-fd operation. A stat/open followed by
        // unlinkat would delete an unverified replacement at this name. Keep the
        // artifact and use the existing diagnostic/cleanup-warning contract.
        if (OperatingSystem.IsLinux())
            return "Linux retained this artifact because a named unlink cannot guarantee deletion of the same verified inode.";
        try
        {
            using var handle = CreateFileWindows(path, GenericRead | DeleteAccess, FileShareRead,
                IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                return error is 2 or 3 ? null : new Win32Exception(error).Message;
            }
            var snapshot = CaptureRawFile(handle);
            if (snapshot.Identity != identity || snapshot.Bytes.LongLength != length
                || snapshot.NumberOfLinks != linkCount
                || !string.Equals(HashBytes(snapshot.Bytes), rawHash, StringComparison.Ordinal))
                return "The temporary file no longer matches its captured identity and bytes.";

            var disposition = new FileDispositionInformation { DeleteFile = 1 };
            return SetFileInformationByHandleWindows(handle, FileDispositionInfo, ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>())
                ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        catch (Exception ex) when (IsIoException(ex))
        {
            return ex.Message;
        }
    }

    private static bool ContainsOnlyValidUnicode(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index]))
                    return false;
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }
        return true;
    }

    private static string HashText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text)));

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsFileInUse(Exception ex) =>
        ex is Win32Exception native && IsNativeFileInUseError(native.NativeErrorCode)
        || ex is IOException && (ex.HResult & 0xffff) is ErrorSharingViolation or ErrorLockViolation;

    private static bool IsNativeFileInUseError(int error) => OperatingSystem.IsLinux()
        ? error is 11 or 16 or 26 // EAGAIN/EWOULDBLOCK, EBUSY, ETXTBSY.
        : error is ErrorSharingViolation or ErrorLockViolation;

    private static bool IsIoException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or Win32Exception
            or InvalidDataException;

    private static WccTextAtomicReplaceResult Failure(
        WccTextAtomicReplaceStatus status,
        string? sourcePath,
        string message) =>
        new(status, sourcePath, ErrorMessage: message);

    private sealed record RawFileSnapshot(
        byte[] Bytes,
        WccWindowsFileIdentity Identity,
        uint NumberOfLinks);

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;
    // ReplaceFileW's write-through flag is unsupported. Do not ignore ACL/merge
    // errors either. Only the staging write has an explicit Flush(true).
    private const int ErrorUnableToMoveReplacement2 = 1177;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int FileDispositionInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public byte DeleteFile; // Win32 BOOLEAN, not BOOL.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "ReplaceFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFileWindows(
        string replacedFileName,
        string replacementFileName,
        string backupFileName,
        uint replaceFlags,
        IntPtr exclude,
        IntPtr reserved);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWindows(
        string path,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleWindows(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandleWindows(
        SafeFileHandle handle,
        int informationClass,
        ref FileDispositionInformation information,
        uint bufferSize);
}
