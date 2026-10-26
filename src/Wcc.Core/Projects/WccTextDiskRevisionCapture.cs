using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wcc.Core.Projects;

public enum WccTextDiskRevisionCaptureStatus
{
    Captured,
    UnsupportedPlatform,
    InvalidProjectRoot,
    InvalidSource,
    InvalidBaseline,
    ProjectRootMissing,
    OutsideProjectRoot,
    SourceMissing,
    ReparsePointRejected,
    UnsupportedEncoding,
    MalformedText,
    BaselineMismatch,
    ChangedDuringCapture,
    IoFailure
}

public enum WccTextDiskEncodingKind
{
    Utf8WithoutBom,
    Utf8WithBom,
    Utf16LittleEndianWithBom,
    Utf16BigEndianWithBom,
    Utf32LittleEndianWithBom,
    Utf32BigEndianWithBom
}

/// <summary>Historical compatibility name for an opaque platform identity: Windows file ID,
/// or Linux device/inode/mount ID. The components are never pathname authority.</summary>
public sealed record WccWindowsFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh)
{
    public bool IsEmpty => FileIdLow == 0 && FileIdHigh == 0;
}

/// <summary>
/// A read-only observation of one text file and the filesystem objects that route to it.
/// It is evidence for a later guarded operation, never permission to persist a change.
/// </summary>
public sealed class WccTextDiskRevision
{
    internal WccTextDiskRevision(
        string projectRootPath,
        string sourceParentPath,
        string sourcePath,
        string text,
        WccTextDiskEncodingKind encodingKind,
        long rawByteLength,
        string rawByteSha256,
        string decodedTextSha256,
        string routeIdentitySha256,
        WccWindowsFileIdentity projectRootIdentity,
        WccWindowsFileIdentity sourceParentIdentity,
        WccWindowsFileIdentity sourceIdentity,
        uint sourceHardLinkCount)
    {
        ProjectRootPath = projectRootPath;
        SourceParentPath = sourceParentPath;
        SourcePath = sourcePath;
        Text = text;
        EncodingKind = encodingKind;
        RawByteLength = rawByteLength;
        RawByteSha256 = rawByteSha256;
        DecodedTextSha256 = decodedTextSha256;
        RouteIdentitySha256 = routeIdentitySha256;
        ProjectRootIdentity = projectRootIdentity;
        SourceParentIdentity = sourceParentIdentity;
        SourceIdentity = sourceIdentity;
        SourceHardLinkCount = sourceHardLinkCount;
    }

    public string ProjectRootPath { get; }
    public string SourceParentPath { get; }
    public string SourcePath { get; }
    public string Text { get; }
    public WccTextDiskEncodingKind EncodingKind { get; }
    public long RawByteLength { get; }
    public string RawByteSha256 { get; }
    public string DecodedTextSha256 { get; }
    public string RouteIdentitySha256 { get; }
    public WccWindowsFileIdentity ProjectRootIdentity { get; }
    public WccWindowsFileIdentity SourceParentIdentity { get; }
    public WccWindowsFileIdentity SourceIdentity { get; }
    public uint SourceHardLinkCount { get; }

    public bool AuthorizesPersistence => false;

    internal bool IsSameRevisionAs(WccTextDiskRevision other) =>
        other is not null
        && string.Equals(ProjectRootPath, other.ProjectRootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(SourceParentPath, other.SourceParentPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(SourcePath, other.SourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(Text, other.Text, StringComparison.Ordinal)
        && EncodingKind == other.EncodingKind
        && RawByteLength == other.RawByteLength
        && string.Equals(RawByteSha256, other.RawByteSha256, StringComparison.Ordinal)
        && string.Equals(DecodedTextSha256, other.DecodedTextSha256, StringComparison.Ordinal)
        && string.Equals(RouteIdentitySha256, other.RouteIdentitySha256, StringComparison.Ordinal)
        && ProjectRootIdentity == other.ProjectRootIdentity
        && SourceParentIdentity == other.SourceParentIdentity
        && SourceIdentity == other.SourceIdentity
        && SourceHardLinkCount == other.SourceHardLinkCount;
}

public sealed record WccTextDiskRevisionCaptureResult(
    WccTextDiskRevisionCaptureStatus Status,
    WccTextDiskRevision? Revision = null,
    string? SourcePath = null,
    string? ErrorMessage = null)
{
    public int? NativeErrorCode { get; init; }

    public bool IsCaptured => Status == WccTextDiskRevisionCaptureStatus.Captured;

    public bool AuthorizesPersistence => false;
}

/// <summary>
/// Captures a stable, read-only disk revision for a text file below a project root.
/// Every path component from the filesystem root must be a non-reparse directory.
/// No successful or failed result authorizes or performs persistence.
/// </summary>
public sealed partial class WccTextDiskRevisionCapture
{
    private WccTextDiskRevisionCapture()
    {
    }

    public static WccTextDiskRevisionCaptureResult Capture(
        string projectRoot,
        string? sourcePath,
        string? expectedBaselineText) =>
        CaptureWithVerificationHook(projectRoot, sourcePath, expectedBaselineText, null);

    internal static WccTextDiskRouteLease AcquireRouteLease(WccTextDiskRevision expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return AcquirePinnedDirectoryRouteLease(expected.ProjectRootPath, expected.SourceParentPath,
            expected.ProjectRootIdentity, expected.SourceParentIdentity, expected.RouteIdentitySha256);
    }

    private static WccTextDiskRouteLease AcquirePinnedDirectoryRouteLease(string projectRootPath, string sourceParentPath,
        WccWindowsFileIdentity projectRootIdentity, WccWindowsFileIdentity sourceParentIdentity,
        string routeIdentitySha256)
    {
        if (OperatingSystem.IsLinux())
            return AcquireLinuxDirectoryRouteLease(projectRootPath, sourceParentPath,
                projectRootIdentity, sourceParentIdentity, routeIdentitySha256);
        var handles = new List<SafeFileHandle>();
        try
        {
            var chain = BuildDirectoryChain(projectRootPath, sourceParentPath);
            var identities = new List<PathIdentity>(chain.Paths.Count);
            for (var index = 0; index < chain.Paths.Count; index++)
            {
                var path = chain.Paths[index];
                var handle = OpenPath(path, readContents: false, projectRoot: index <= chain.ProjectRootIndex);
                handles.Add(handle);
                var snapshot = ReadHandleSnapshot(handle);
                RejectReparsePoint(path, snapshot);
                if (!snapshot.IsDirectory || snapshot.IsDevice)
                    throw new InvalidDataException($"Pinned route component '{path}' is not a regular directory.");
                identities.Add(new PathIdentity(path, snapshot.Identity));
            }

            // Root verification appends more handles; retain the actual source-parent handle first.
            var sourceParentHandle = handles[chain.Paths.Count - 1];
            VerifyDeclaredProjectRoot(
                projectRootPath,
                identities[chain.ProjectRootIndex].Identity,
                handles);

            if (identities[chain.ProjectRootIndex].Identity != projectRootIdentity
                || identities[^1].Identity != sourceParentIdentity
                || ReadHandleSnapshot(sourceParentHandle).Identity != sourceParentIdentity
                || !string.Equals(HashRouteIdentities(identities), routeIdentitySha256, StringComparison.Ordinal))
            {
                throw new WccTextDiskRouteChangedException(
                    "The project directory route no longer matches the captured revision.");
            }

            return new WccTextDiskRouteLease(handles, sourceParentHandle);
        }
        catch (Exception ex) when (
            ex is ProjectRootMissingException
                or SourceMissingException
                or InvalidProjectRootException
                or InvalidSourceException
                or OutsideProjectRootException
                or ReparsePointRejectedException
                or IOException
                or UnauthorizedAccessException
                or Win32Exception
                or InvalidDataException)
        {
            DisposeHandles(handles);
            throw new WccTextDiskRouteChangedException(
                "The project directory route became unavailable or invalid after capture.",
                ex);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    private static void DisposeHandles(IReadOnlyList<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
            handles[index].Dispose();
    }

    internal static WccWindowsFileIdentity ReadHandleIdentityForVerification(SafeFileHandle handle) =>
        ReadHandleSnapshot(handle).Identity;

    private static WccTextDiskRevisionCaptureResult CaptureWithVerificationHook(
        string projectRoot,
        string? sourcePath,
        string? expectedBaselineText,
        Action? betweenVerificationPasses)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.UnsupportedPlatform,
                sourcePath,
                "Stable disk revision capture is not implemented for this platform.");
        }

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.InvalidProjectRoot,
                sourcePath,
                "The project root is missing.");
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.InvalidSource,
                sourcePath,
                "The source path is missing.");
        }

        if (expectedBaselineText is null || !ContainsOnlyValidUnicode(expectedBaselineText))
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.InvalidBaseline,
                sourcePath,
                "The expected baseline is missing or contains an unpaired surrogate.");
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.InvalidProjectRoot,
                sourcePath,
                ex.Message);
        }

        string normalizedSource;
        try
        {
            normalizedSource = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.InvalidSource,
                sourcePath,
                ex.Message);
        }

        return CaptureNormalized(
            normalizedRoot,
            normalizedSource,
            expectedBaselineText,
            betweenVerificationPasses);
    }

    private static WccTextDiskRevisionCaptureResult CaptureNormalized(
        string normalizedRoot,
        string normalizedSource,
        string expectedBaselineText,
        Action? betweenVerificationPasses)
    {
        if (!IsPathInsideRoot(normalizedRoot, normalizedSource))
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.OutsideProjectRoot,
                normalizedSource,
                "The source file is outside the project root.");
        }

        var relativeSource = Path.GetRelativePath(normalizedRoot, normalizedSource);
        if (relativeSource.Contains(':', StringComparison.Ordinal))
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.InvalidSource,
                normalizedSource,
                "Alternate data streams are not valid text source files.");
        }

        var sourceParent = Path.GetDirectoryName(normalizedSource);
        if (string.IsNullOrEmpty(sourceParent))
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.InvalidSource,
                normalizedSource,
                "The source file does not have a parent directory.");
        }

        return CaptureVerified(
            normalizedRoot,
            sourceParent,
            normalizedSource,
            expectedBaselineText,
            betweenVerificationPasses);
    }

    private static WccTextDiskRevisionCaptureResult CaptureVerified(
        string normalizedRoot,
        string sourceParent,
        string normalizedSource,
        string expectedBaselineText,
        Action? betweenVerificationPasses)
    {
        try
        {
            var maximumExpectedBytes = 4L * expectedBaselineText.Length + 4;
            var first = CapturePass(
                normalizedRoot,
                sourceParent,
                normalizedSource,
                maximumExpectedBytes);
            var decoded = DecodeStrict(first.SourceBytes);
            if (!string.Equals(decoded.Text, expectedBaselineText, StringComparison.Ordinal))
            {
                return Failure(
                    WccTextDiskRevisionCaptureStatus.BaselineMismatch,
                    normalizedSource,
                    "The source file no longer matches the expected baseline text.");
            }

            betweenVerificationPasses?.Invoke();

            var second = CaptureVerificationPass(
                normalizedRoot,
                sourceParent,
                normalizedSource,
                maximumExpectedBytes);
            if (!first.IsSameRevision(second))
            {
                return Failure(
                    WccTextDiskRevisionCaptureStatus.ChangedDuringCapture,
                    normalizedSource,
                    "The source path, filesystem identities, link count, or bytes changed during capture.");
            }

            var verifiedDecoded = DecodeStrict(second.SourceBytes);
            if (verifiedDecoded.EncodingKind != decoded.EncodingKind
                || !string.Equals(verifiedDecoded.Text, decoded.Text, StringComparison.Ordinal)
                || !string.Equals(verifiedDecoded.Text, expectedBaselineText, StringComparison.Ordinal))
            {
                return Failure(
                    WccTextDiskRevisionCaptureStatus.ChangedDuringCapture,
                    normalizedSource,
                    "The decoded source text changed during capture.");
            }

            return CreateSuccess(
                normalizedRoot,
                sourceParent,
                normalizedSource,
                verifiedDecoded,
                second);
        }
        catch (BaselineSizeMismatchException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.BaselineMismatch,
                normalizedSource,
                ex.Message);
        }
        catch (DiskChangedDuringCaptureException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.ChangedDuringCapture,
                normalizedSource,
                ex.Message);
        }
        catch (ProjectRootMissingException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.ProjectRootMissing,
                normalizedSource,
                ex.Message);
        }
        catch (SourceMissingException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.SourceMissing,
                normalizedSource,
                ex.Message);
        }
        catch (InvalidProjectRootException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.InvalidProjectRoot,
                normalizedSource,
                ex.Message);
        }
        catch (InvalidSourceException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.InvalidSource,
                normalizedSource,
                ex.Message);
        }
        catch (OutsideProjectRootException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.OutsideProjectRoot,
                normalizedSource,
                ex.Message);
        }
        catch (ReparsePointRejectedException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.ReparsePointRejected,
                normalizedSource,
                ex.Message);
        }
        catch (UnsupportedTextEncodingException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.UnsupportedEncoding,
                normalizedSource,
                ex.Message);
        }
        catch (DecoderFallbackException ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.MalformedText,
                normalizedSource,
                ex.Message);
        }
        catch (Win32Exception ex)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.IoFailure,
                normalizedSource,
                ex.Message) with { NativeErrorCode = ex.NativeErrorCode };
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
        {
            return Failure(
                WccTextDiskRevisionCaptureStatus.IoFailure,
                normalizedSource,
                ex.Message);
        }
    }

    private static WccTextDiskRevisionCaptureResult CreateSuccess(
        string normalizedRoot,
        string sourceParent,
        string normalizedSource,
        DecodedText decoded,
        CapturePassResult pass)
    {
        var revision = new WccTextDiskRevision(
            normalizedRoot,
            sourceParent,
            normalizedSource,
            decoded.Text,
            decoded.EncodingKind,
            pass.SourceBytes.LongLength,
            HashBytes(pass.SourceBytes),
            HashText(decoded.Text),
            pass.RouteIdentitySha256,
            pass.ProjectRootIdentity,
            pass.SourceParentIdentity,
            pass.SourceIdentity,
            pass.SourceHardLinkCount);
        return new WccTextDiskRevisionCaptureResult(
            WccTextDiskRevisionCaptureStatus.Captured,
            revision,
            normalizedSource);
    }

    private static WccTextDiskRevisionCaptureResult Failure(
        WccTextDiskRevisionCaptureStatus status,
        string? sourcePath,
        string message) =>
        new(status, SourcePath: sourcePath, ErrorMessage: message);

    private static bool IsPathInsideRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
               && !string.Equals(relative, ".", StringComparison.Ordinal)
               && !string.Equals(relative, "..", StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool ContainsOnlyValidUnicode(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (char.IsHighSurrogate(value))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index]))
                    return false;
            }
            else if (char.IsLowSurrogate(value))
            {
                return false;
            }
        }

        return true;
    }

    private static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string HashText(string text) =>
        HashBytes(new UTF8Encoding(false, true).GetBytes(text));

    private static string HashRouteIdentities(IEnumerable<PathIdentity> identities)
    {
        var canonical = new StringBuilder(nameof(WccTextDiskRevision));
        foreach (var entry in identities)
        {
            AppendCanonicalValue(canonical, OperatingSystem.IsWindows() ? entry.Path.ToUpperInvariant() : entry.Path);
            AppendCanonicalValue(
                canonical,
                entry.Identity.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture));
            AppendCanonicalValue(
                canonical,
                entry.Identity.FileIdLow.ToString(CultureInfo.InvariantCulture));
            AppendCanonicalValue(
                canonical,
                entry.Identity.FileIdHigh.ToString(CultureInfo.InvariantCulture));
        }
        return HashText(canonical.ToString());
    }

    private static void AppendCanonicalValue(StringBuilder target, string value) =>
        target.Append(value.Length).Append(':').Append(value).Append('\n');

    private static CapturePassResult CapturePass(
        string projectRoot,
        string sourceParent,
        string sourcePath,
        long maximumExpectedBytes)
    {
        if (OperatingSystem.IsLinux()) return CaptureLinuxPass(projectRoot, sourceParent, sourcePath, maximumExpectedBytes);
        var directoryChain = BuildDirectoryChain(projectRoot, sourceParent);
        var directoryPaths = directoryChain.Paths;
        var openHandles = new List<SafeFileHandle>(directoryPaths.Count + 1);
        try
        {
            var directoryIdentities = new List<PathIdentity>(directoryPaths.Count);
            for (var index = 0; index < directoryPaths.Count; index++)
            {
                var directoryPath = directoryPaths[index];
                var handle = OpenPath(
                    directoryPath,
                    readContents: false,
                    projectRoot: index <= directoryChain.ProjectRootIndex);
                openHandles.Add(handle);
                var snapshot = ReadHandleSnapshot(handle);
                RejectReparsePoint(directoryPath, snapshot);
                if (!snapshot.IsDirectory || snapshot.IsDevice)
                {
                    if (index <= directoryChain.ProjectRootIndex)
                        throw new InvalidProjectRootException(
                            "The project root path does not consist only of directories.");
                    throw new InvalidSourceException(
                        $"Source path component '{directoryPath}' is not a directory.");
                }

                directoryIdentities.Add(new PathIdentity(directoryPath, snapshot.Identity));
            }

            VerifyDeclaredProjectRoot(
                projectRoot,
                directoryIdentities[directoryChain.ProjectRootIndex].Identity,
                openHandles);

            var sourceHandle = OpenPath(sourcePath, readContents: true, projectRoot: false);
            openHandles.Add(sourceHandle);
            var sourceBefore = ReadHandleSnapshot(sourceHandle);
            RejectReparsePoint(sourcePath, sourceBefore);
            if (sourceBefore.IsDirectory || sourceBefore.IsDevice)
                throw new InvalidSourceException("The source path is not a regular file.");

            var sourceBytes = ReadAllBytes(
                sourceHandle,
                sourceBefore.EndOfFile,
                maximumExpectedBytes);
            var sourceAfter = ReadHandleSnapshot(sourceHandle);
            if (!sourceBefore.IsSameFileState(sourceAfter)
                || sourceAfter.EndOfFile != sourceBytes.LongLength)
            {
                throw new DiskChangedDuringCaptureException(
                    "The source file changed while its bytes were being read.");
            }

            return new CapturePassResult(
                directoryIdentities,
                directoryIdentities[directoryChain.ProjectRootIndex].Identity,
                sourceAfter,
                sourceBytes);
        }
        finally
        {
            for (var index = openHandles.Count - 1; index >= 0; index--)
                openHandles[index].Dispose();
        }
    }

    private static CapturePassResult CaptureVerificationPass(
        string projectRoot,
        string sourceParent,
        string sourcePath,
        long maximumExpectedBytes)
    {
        try
        {
            return CapturePass(
                projectRoot,
                sourceParent,
                sourcePath,
                maximumExpectedBytes);
        }
        catch (Exception ex) when (
            ex is ProjectRootMissingException
                or SourceMissingException
                or InvalidProjectRootException
                or InvalidSourceException
                or OutsideProjectRootException
                or ReparsePointRejectedException
                or BaselineSizeMismatchException
                or IOException
                or UnauthorizedAccessException
                or Win32Exception
                or InvalidDataException)
        {
            throw new DiskChangedDuringCaptureException(
                "The project route or source became unavailable during verification.",
                ex);
        }
    }

    private static DirectoryChain BuildDirectoryChain(
        string projectRoot,
        string sourceParent)
    {
        var filesystemRoot = Path.GetPathRoot(projectRoot);
        if (string.IsNullOrEmpty(filesystemRoot))
            throw new InvalidProjectRootException("The project root has no filesystem root.");

        var paths = new List<string> { filesystemRoot };
        var relativeParent = Path.GetRelativePath(filesystemRoot, sourceParent);
        var current = filesystemRoot;
        if (!string.Equals(relativeParent, ".", StringComparison.Ordinal))
        {
            foreach (var component in relativeParent.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                paths.Add(current);
            }
        }

        var projectRootIndex = paths.FindIndex(path =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                projectRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (projectRootIndex < 0)
            throw new InvalidProjectRootException("The project root is not in its filesystem path chain.");

        return new DirectoryChain(paths, projectRootIndex);
    }

    private static void VerifyDeclaredProjectRoot(
        string projectRoot,
        WccWindowsFileIdentity sourceRouteRootIdentity,
        List<SafeFileHandle> openHandles)
    {
        // Windows may host case-sensitive directories. A lexical, case-insensitive
        // prefix alone does not prove that the declared root owns the source route.
        // Pin the independently spelled route too, so its ancestors cannot be swapped
        // while the source-route identity is being compared.
        var declaredChain = BuildDirectoryChain(projectRoot, projectRoot);
        WccWindowsFileIdentity? declaredRootIdentity = null;
        foreach (var path in declaredChain.Paths)
        {
            var handle = OpenPath(path, readContents: false, projectRoot: true);
            openHandles.Add(handle);
            var snapshot = ReadHandleSnapshot(handle);
            RejectReparsePoint(path, snapshot);
            if (!snapshot.IsDirectory || snapshot.IsDevice)
            {
                throw new InvalidProjectRootException(
                    "The declared project root path does not consist only of directories.");
            }

            declaredRootIdentity = snapshot.Identity;
        }

        if (declaredRootIdentity != sourceRouteRootIdentity)
        {
            throw new OutsideProjectRootException(
                "The declared project root is a different directory from the source route's root.");
        }
    }

    private static SafeFileHandle OpenPath(
        string path,
        bool readContents,
        bool projectRoot)
    {
        if (OperatingSystem.IsLinux()) return OpenLinuxPath(path, readContents, projectRoot);
        // Metadata-only opens (including access=0) do not enforce sharing restrictions.
        // Directory data-read access makes omission of FILE_SHARE_DELETE actually pin
        // the route against rename/delete while still allowing ordinary child writes.
        var handle = CreateFileWindows(
            path,
            readContents ? GenericRead : FileListDirectory,
            readContents ? FileShareRead : FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            if (projectRoot)
                throw new ProjectRootMissingException("The project root directory does not exist.");
            throw new SourceMissingException($"Source path component '{path}' does not exist.");
        }

        throw new Win32Exception(error, $"Could not open '{path}' for stable revision capture.");
    }

    private static HandleSnapshot ReadHandleSnapshot(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            var value = WccLinuxDiskNative.ReadSnapshot(handle);
            return new(value.Identity, value.IsDirectory ? FileAttributeDirectory : value.IsRegular ? 0 : FileAttributeDevice,
                value.Length, value.LinkCount, unchecked((ulong)value.ModifiedSeconds)) { LinuxState = value };
        }
        if (!GetFileIdInfoWindows(
                handle,
                FileIdInfoClass,
                out var idInfo,
                (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Could not obtain a stable Windows file identity"
                + $" (NativeErrorCode={error}): " + new Win32Exception(error).Message);
        }

        if (!GetBasicInfoWindows(handle, out var basicInfo))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Could not obtain Windows file metadata"
                + $" (NativeErrorCode={error}): " + new Win32Exception(error).Message);
        }

        var identity = new WccWindowsFileIdentity(
            idInfo.VolumeSerialNumber,
            idInfo.FileId.Low,
            idInfo.FileId.High);
        if (identity.IsEmpty)
            throw new InvalidDataException("Windows returned an empty file identity.");

        if (basicInfo.NumberOfLinks == 0)
            throw new InvalidDataException("Windows returned an unusable zero hard-link count.");

        var unsignedLength = ((ulong)basicInfo.FileSizeHigh << 32) | basicInfo.FileSizeLow;
        if (unsignedLength > long.MaxValue)
            throw new InvalidDataException("Windows returned an invalid source length.");

        var lastWriteTime =
            ((ulong)basicInfo.LastWriteTime.High << 32) | basicInfo.LastWriteTime.Low;
        return new HandleSnapshot(
            identity,
            basicInfo.FileAttributes,
            (long)unsignedLength,
            basicInfo.NumberOfLinks,
            lastWriteTime);
    }

    private static void RejectReparsePoint(string path, HandleSnapshot snapshot)
    {
        if (snapshot.IsReparsePoint)
        {
            throw new ReparsePointRejectedException(
                $"Path component '{path}' is a reparse point.");
        }
    }

    private static byte[] ReadAllBytes(
        SafeFileHandle handle,
        long expectedLength,
        long maximumExpectedBytes)
    {
        if (expectedLength > maximumExpectedBytes)
        {
            throw new BaselineSizeMismatchException(
                "The source byte length cannot represent the expected baseline text in a supported encoding.");
        }

        if (expectedLength < 0 || expectedLength > Array.MaxLength)
            throw new IOException("The source file is too large to capture safely.");

        var bytes = new byte[(int)expectedLength];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0)
                throw new DiskChangedDuringCaptureException("The source file became shorter while reading.");
            offset += read;
        }

        Span<byte> growthProbe = stackalloc byte[1];
        if (RandomAccess.Read(handle, growthProbe, expectedLength) != 0)
            throw new DiskChangedDuringCaptureException("The source file became longer while reading.");

        return bytes;
    }

    private sealed record PathIdentity(string Path, WccWindowsFileIdentity Identity);

    private sealed record DirectoryChain(IReadOnlyList<string> Paths, int ProjectRootIndex);

    private sealed record HandleSnapshot(
        WccWindowsFileIdentity Identity,
        uint Attributes,
        long EndOfFile,
        uint NumberOfLinks,
        ulong LastWriteTime)
    {
        internal LinuxFileSnapshot? LinuxState { get; init; }
        public bool IsDirectory => (Attributes & FileAttributeDirectory) != 0;

        public bool IsReparsePoint => (Attributes & FileAttributeReparsePoint) != 0;

        public bool IsDevice => (Attributes & FileAttributeDevice) != 0;

        public bool IsSameFileState(HandleSnapshot other) =>
            Identity == other.Identity
            && Attributes == other.Attributes
            && EndOfFile == other.EndOfFile
            && NumberOfLinks == other.NumberOfLinks
            && LastWriteTime == other.LastWriteTime
            && LinuxState == other.LinuxState;
    }

    private sealed class CapturePassResult(
        IReadOnlyList<PathIdentity> directoryIdentities,
        WccWindowsFileIdentity projectRootIdentity,
        HandleSnapshot sourceSnapshot,
        byte[] sourceBytes)
    {
        private readonly PathIdentity[] _directoryIdentities = [.. directoryIdentities];
        private readonly HandleSnapshot _sourceSnapshot = sourceSnapshot;

        public WccWindowsFileIdentity ProjectRootIdentity { get; } = projectRootIdentity;

        public string RouteIdentitySha256 { get; } =
            HashRouteIdentities(directoryIdentities);

        public WccWindowsFileIdentity SourceParentIdentity => _directoryIdentities[^1].Identity;

        public WccWindowsFileIdentity SourceIdentity => _sourceSnapshot.Identity;

        public uint SourceHardLinkCount => _sourceSnapshot.NumberOfLinks;

        public byte[] SourceBytes { get; } = sourceBytes;

        public bool IsSameRevision(CapturePassResult other)
        {
            if (_directoryIdentities.Length != other._directoryIdentities.Length)
                return false;

            for (var index = 0; index < _directoryIdentities.Length; index++)
            {
                var left = _directoryIdentities[index];
                var right = other._directoryIdentities[index];
                if (!string.Equals(left.Path, right.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || left.Identity != right.Identity)
                {
                    return false;
                }
            }

            return _sourceSnapshot.IsSameFileState(other._sourceSnapshot)
                   && SourceBytes.AsSpan().SequenceEqual(other.SourceBytes);
        }
    }

    private static DecodedText DecodeStrict(byte[] bytes)
    {
        if (HasUtf7Signature(bytes))
        {
            throw new UnsupportedTextEncodingException(
                "The source uses a UTF-7 signature; only UTF-8, UTF-16 BOM, and UTF-32 BOM text is accepted.");
        }

        Encoding encoding;
        WccTextDiskEncodingKind kind;
        var preambleLength = 0;

        if (bytes.AsSpan().StartsWith(Utf32LittleEndianPreamble))
        {
            encoding = new UTF32Encoding(false, true, true);
            kind = WccTextDiskEncodingKind.Utf32LittleEndianWithBom;
            preambleLength = Utf32LittleEndianPreamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(Utf32BigEndianPreamble))
        {
            encoding = new UTF32Encoding(true, true, true);
            kind = WccTextDiskEncodingKind.Utf32BigEndianWithBom;
            preambleLength = Utf32BigEndianPreamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(Utf8Preamble))
        {
            encoding = new UTF8Encoding(true, true);
            kind = WccTextDiskEncodingKind.Utf8WithBom;
            preambleLength = Utf8Preamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(Utf16LittleEndianPreamble))
        {
            encoding = new UnicodeEncoding(false, true, true);
            kind = WccTextDiskEncodingKind.Utf16LittleEndianWithBom;
            preambleLength = Utf16LittleEndianPreamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(Utf16BigEndianPreamble))
        {
            encoding = new UnicodeEncoding(true, true, true);
            kind = WccTextDiskEncodingKind.Utf16BigEndianWithBom;
            preambleLength = Utf16BigEndianPreamble.Length;
        }
        else
        {
            encoding = new UTF8Encoding(false, true);
            kind = WccTextDiskEncodingKind.Utf8WithoutBom;
        }

        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        if (!ContainsOnlyValidUnicode(text))
            throw new DecoderFallbackException("The decoded source contains an unpaired surrogate.");

        return new DecodedText(text, kind);
    }

    private static bool HasUtf7Signature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4
        && bytes[0] == 0x2b
        && bytes[1] == 0x2f
        && bytes[2] == 0x76
        && bytes[3] is 0x38 or 0x39 or 0x2b or 0x2f;

    private static readonly byte[] Utf8Preamble = [0xef, 0xbb, 0xbf];
    private static readonly byte[] Utf16LittleEndianPreamble = [0xff, 0xfe];
    private static readonly byte[] Utf16BigEndianPreamble = [0xfe, 0xff];
    private static readonly byte[] Utf32LittleEndianPreamble = [0xff, 0xfe, 0x00, 0x00];
    private static readonly byte[] Utf32BigEndianPreamble = [0x00, 0x00, 0xfe, 0xff];

    private sealed record DecodedText(string Text, WccTextDiskEncodingKind EncodingKind);

    private const uint GenericRead = 0x80000000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileIdInfoClass = 18;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileWindows(
        string path,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInfoWindows(
        SafeFileHandle handle,
        uint fileInformationClass,
        out FileIdInfo information,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandle",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasicInfoWindows(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    private sealed class ProjectRootMissingException(string message) : Exception(message);

    private sealed class SourceMissingException(string message) : Exception(message);

    private sealed class InvalidProjectRootException(string message) : Exception(message);

    private sealed class InvalidSourceException(string message) : Exception(message);

    private sealed class OutsideProjectRootException(string message) : Exception(message);

    private sealed class ReparsePointRejectedException(string message) : Exception(message);

    private sealed class UnsupportedTextEncodingException(string message) : Exception(message);

    private sealed class BaselineSizeMismatchException(string message) : Exception(message);

    private sealed class DiskChangedDuringCaptureException : Exception
    {
        public DiskChangedDuringCaptureException(string message)
            : base(message)
        {
        }

        public DiskChangedDuringCaptureException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

internal sealed class WccTextDiskRouteLease : IDisposable
{
    private readonly IReadOnlyList<SafeFileHandle> _handles;
    private readonly Action? _verifyCurrentRoute;

    internal WccTextDiskRouteLease(IReadOnlyList<SafeFileHandle> handles) : this(handles, handles[^1]) { }

    internal WccTextDiskRouteLease(IReadOnlyList<SafeFileHandle> handles, SafeFileHandle parentHandle)
    { _handles = handles; ParentHandle = parentHandle; }

    internal WccTextDiskRouteLease(IReadOnlyList<SafeFileHandle> handles, SafeFileHandle parentHandle, Action verifyCurrentRoute)
        : this(handles, parentHandle) => _verifyCurrentRoute = verifyCurrentRoute;

    internal SafeFileHandle ParentHandle { get; }

    // Linux descriptors keep the objects alive, but do not prevent namespace
    // renames. Checked writers call this around descriptor-relative publication.
    internal void VerifyCurrentRoute() => _verifyCurrentRoute?.Invoke();

    public void Dispose()
    {
        for (var index = _handles.Count - 1; index >= 0; index--)
            _handles[index].Dispose();
    }
}

internal sealed class WccTextDiskRouteChangedException : Exception
{
    public WccTextDiskRouteChangedException(string message)
        : base(message)
    {
    }

    public WccTextDiskRouteChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
