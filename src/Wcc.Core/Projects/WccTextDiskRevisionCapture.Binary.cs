using System.ComponentModel;

namespace Wcc.Core.Projects;

/// <summary>Immutable byte and filesystem evidence, never persistence authority.</summary>
public sealed class WccBinaryDiskRevision
{
    private readonly byte[] _bytes;

    internal WccBinaryDiskRevision(string projectRootPath, string sourceParentPath, string sourcePath,
        byte[] bytes, string rawByteSha256, string routeIdentitySha256,
        WccWindowsFileIdentity projectRootIdentity, WccWindowsFileIdentity sourceParentIdentity,
        WccWindowsFileIdentity sourceIdentity, uint sourceHardLinkCount)
    {
        ProjectRootPath = projectRootPath; SourceParentPath = sourceParentPath; SourcePath = sourcePath;
        _bytes = (byte[])bytes.Clone(); RawByteSha256 = rawByteSha256; RouteIdentitySha256 = routeIdentitySha256;
        ProjectRootIdentity = projectRootIdentity; SourceParentIdentity = sourceParentIdentity;
        SourceIdentity = sourceIdentity; SourceHardLinkCount = sourceHardLinkCount;
    }

    public string ProjectRootPath { get; }
    public string SourceParentPath { get; }
    public string SourcePath { get; }
    public long RawByteLength => _bytes.LongLength;
    public string RawByteSha256 { get; }
    public string RouteIdentitySha256 { get; }
    public WccWindowsFileIdentity ProjectRootIdentity { get; }
    public WccWindowsFileIdentity SourceParentIdentity { get; }
    public WccWindowsFileIdentity SourceIdentity { get; }
    public uint SourceHardLinkCount { get; }
    public bool AuthorizesPersistence => false;
    public byte[] CopyBytes() => (byte[])_bytes.Clone();

    internal bool IsSameRevisionAs(WccBinaryDiskRevision other) => other is not null
        && string.Equals(ProjectRootPath, other.ProjectRootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(SourceParentPath, other.SourceParentPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(SourcePath, other.SourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && RawByteLength == other.RawByteLength && RawByteSha256 == other.RawByteSha256
        && RouteIdentitySha256 == other.RouteIdentitySha256 && ProjectRootIdentity == other.ProjectRootIdentity
        && SourceParentIdentity == other.SourceParentIdentity && SourceIdentity == other.SourceIdentity
        && SourceHardLinkCount == other.SourceHardLinkCount && _bytes.AsSpan().SequenceEqual(other._bytes);
}

public sealed record WccBinaryDiskRevisionCaptureResult(WccTextDiskRevisionCaptureStatus Status,
    WccBinaryDiskRevision? Revision = null, string? SourcePath = null, string? ErrorMessage = null)
{
    public int? NativeErrorCode { get; init; }
    public bool IsCaptured => Status == WccTextDiskRevisionCaptureStatus.Captured;
    public bool AuthorizesPersistence => false;
}

public sealed partial class WccTextDiskRevisionCapture
{
    internal static WccTextDiskRouteLease AcquireBinaryRouteLease(WccBinaryDiskRevision expected) =>
        AcquirePinnedDirectoryRouteLease(expected.ProjectRootPath, expected.SourceParentPath,
            expected.ProjectRootIdentity, expected.SourceParentIdentity, expected.RouteIdentitySha256);

    public const long MaximumResourceBytes = 16L * 1024 * 1024;

    /// <summary>Reads arbitrary bytes twice through the same guarded platform path capture.</summary>
    public static WccBinaryDiskRevisionCaptureResult CaptureBinary(string projectRoot, string? sourcePath,
        long maximumBytes = MaximumResourceBytes) =>
        CaptureBinaryWithVerificationHook(projectRoot, sourcePath, maximumBytes, null);

    private static WccBinaryDiskRevisionCaptureResult CaptureBinaryWithVerificationHook(string projectRoot,
        string? sourcePath, long maximumBytes, Action? betweenVerificationPasses)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return BinaryFailure(WccTextDiskRevisionCaptureStatus.UnsupportedPlatform,
            sourcePath, "Stable binary disk revision capture is not implemented for this platform.");
        if (string.IsNullOrWhiteSpace(projectRoot)) return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidProjectRoot,
            sourcePath, "The resource root is missing.");
        if (string.IsNullOrWhiteSpace(sourcePath)) return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidSource,
            sourcePath, "The resource path is missing.");
        if (maximumBytes is < 1 or > MaximumResourceBytes) return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidBaseline,
            sourcePath, "A binary capture requires a byte limit between 1 byte and 16 MiB.");
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidProjectRoot, sourcePath, error.Message); }
        string path;
        try { path = Path.GetFullPath(sourcePath); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidSource, sourcePath, error.Message); }
        if (!IsPathInsideRoot(root, path)) return BinaryFailure(WccTextDiskRevisionCaptureStatus.OutsideProjectRoot,
            path, "The resource file is outside its declared root.");
        if (Path.GetRelativePath(root, path).Contains(':', StringComparison.Ordinal))
            return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidSource, path,
                "Alternate data streams are not valid resource files.");
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent)) return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidSource,
            path, "The resource file has no parent directory.");
        try
        {
            var first = CapturePass(root, parent, path, maximumBytes);
            betweenVerificationPasses?.Invoke();
            var second = CaptureVerificationPass(root, parent, path, maximumBytes);
            if (!first.IsSameRevision(second)) return BinaryFailure(WccTextDiskRevisionCaptureStatus.ChangedDuringCapture,
                path, "The resource path, filesystem identities, link count or bytes changed during capture.");
            var revision = new WccBinaryDiskRevision(root, parent, path, second.SourceBytes,
                HashBytes(second.SourceBytes), second.RouteIdentitySha256, second.ProjectRootIdentity,
                second.SourceParentIdentity, second.SourceIdentity, second.SourceHardLinkCount);
            return new(WccTextDiskRevisionCaptureStatus.Captured, revision, path);
        }
        catch (BaselineSizeMismatchException error)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidSource, path, "The resource exceeds its bounded byte limit: " + error.Message); }
        catch (DiskChangedDuringCaptureException error)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.ChangedDuringCapture, path, error.Message); }
        catch (ProjectRootMissingException error)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.ProjectRootMissing, path, error.Message); }
        catch (SourceMissingException error)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.SourceMissing, path, error.Message); }
        catch (InvalidProjectRootException error)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidProjectRoot, path, error.Message); }
        catch (InvalidSourceException error)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.InvalidSource, path, error.Message); }
        catch (OutsideProjectRootException error)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.OutsideProjectRoot, path, error.Message); }
        catch (ReparsePointRejectedException error)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.ReparsePointRejected, path, error.Message); }
        catch (Win32Exception error)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.IoFailure, path, error.Message) with { NativeErrorCode = error.NativeErrorCode }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return BinaryFailure(WccTextDiskRevisionCaptureStatus.IoFailure, path, error.Message); }
    }

    private static WccBinaryDiskRevisionCaptureResult BinaryFailure(WccTextDiskRevisionCaptureStatus status,
        string? path, string message) => new(status, SourcePath: path, ErrorMessage: message);
}
