using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Wcc.Core.Projects;

public enum WccTextDiskFileAbsenceCaptureStatus
{
    Captured, UnsupportedPlatform, InvalidProjectRoot, InvalidDestination,
    OutsideProjectRoot, ParentMissing, DestinationExists, UnsafePath, ChangedDuringCapture, IoFailure
}

/// <summary>An observation of a missing filename and optional absent parent chain beneath an identity-bound directory; never write authority.</summary>
public sealed class WccTextDiskFileAbsence
{
    internal WccTextDiskFileAbsence(string projectRootPath, string sourceParentPath, string sourcePath,
        string routeIdentitySha256, WccWindowsFileIdentity projectRootIdentity,
        WccWindowsFileIdentity sourceParentIdentity, IEnumerable<string>? requiredDirectories = null)
    {
        ProjectRootPath = projectRootPath;
        SourceParentPath = sourceParentPath;
        SourcePath = sourcePath;
        RouteIdentitySha256 = routeIdentitySha256;
        ProjectRootIdentity = projectRootIdentity;
        SourceParentIdentity = sourceParentIdentity;
        RequiredDirectories = Array.AsReadOnly((requiredDirectories ?? []).ToArray());
    }

    public string ProjectRootPath { get; }
    public string SourceParentPath { get; }
    public string SourcePath { get; }
    public string DestinationParentPath => Path.GetDirectoryName(SourcePath)!;
    /// <summary>Ordered absent descendants of SourceParentPath; creation still requires the checked writer.</summary>
    public IReadOnlyList<string> RequiredDirectories { get; }
    public string RouteIdentitySha256 { get; }
    public WccWindowsFileIdentity ProjectRootIdentity { get; }
    public WccWindowsFileIdentity SourceParentIdentity { get; }
    public bool AuthorizesPersistence => false;

    internal bool IsSameAbsenceAs(WccTextDiskFileAbsence other) => other is not null
        && string.Equals(ProjectRootPath, other.ProjectRootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(SourceParentPath, other.SourceParentPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(SourcePath, other.SourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && RouteIdentitySha256 == other.RouteIdentitySha256
        && ProjectRootIdentity == other.ProjectRootIdentity && SourceParentIdentity == other.SourceParentIdentity
        && RequiredDirectories.SequenceEqual(other.RequiredDirectories, StringComparer.Ordinal);
}

public sealed record WccTextDiskFileAbsenceCaptureResult(WccTextDiskFileAbsenceCaptureStatus Status,
    WccTextDiskFileAbsence? Absence = null, string? SourcePath = null, string? ErrorMessage = null)
{
    public bool IsCaptured => Status == WccTextDiskFileAbsenceCaptureStatus.Captured;
    public bool AuthorizesPersistence => false;
}

public sealed partial class WccTextDiskRevisionCapture
{
    public static WccTextDiskFileAbsenceCaptureResult CaptureAbsence(string projectRoot, string? sourcePath) =>
        CaptureAbsenceWithVerificationHook(projectRoot, sourcePath, null);

    /// <summary>Captures missing descendants without making directories or granting persistence authority.</summary>
    public static WccTextDiskFileAbsenceCaptureResult CaptureCreationAbsence(string projectRoot, string? sourcePath) =>
        CaptureCreationAbsenceWithVerificationHook(projectRoot, sourcePath, null);

    internal static WccTextDiskRouteLease AcquireAbsenceRouteLease(WccTextDiskFileAbsence expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return AcquirePinnedDirectoryRouteLease(expected.ProjectRootPath, expected.SourceParentPath,
            expected.ProjectRootIdentity, expected.SourceParentIdentity, expected.RouteIdentitySha256);
    }

    private static WccTextDiskFileAbsenceCaptureResult CaptureAbsenceWithVerificationHook(
        string projectRoot, string? sourcePath, Action? betweenVerificationPasses) =>
        CaptureFileAbsenceCore(projectRoot, sourcePath, betweenVerificationPasses, allowMissingParents: false);

    private static WccTextDiskFileAbsenceCaptureResult CaptureCreationAbsenceWithVerificationHook(
        string projectRoot, string? sourcePath, Action? betweenVerificationPasses) =>
        CaptureFileAbsenceCore(projectRoot, sourcePath, betweenVerificationPasses, allowMissingParents: true);

    private static WccTextDiskFileAbsenceCaptureResult CaptureFileAbsenceCore(
        string projectRoot, string? sourcePath, Action? betweenVerificationPasses, bool allowMissingParents)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.UnsupportedPlatform,
            sourcePath, "Stable missing-file capture is not implemented for this platform.");
        if (string.IsNullOrWhiteSpace(projectRoot)) return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.InvalidProjectRoot,
            sourcePath, "The project root is missing.");
        if (string.IsNullOrWhiteSpace(sourcePath)) return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.InvalidDestination,
            sourcePath, "The destination path is missing.");
        if (!IsPortableNewFilename(Path.GetFileName(sourcePath)))
            return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.InvalidDestination,
                sourcePath, "The new filename is not a portable ordinary filename.");
        string root;
        string destination;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
            destination = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.InvalidDestination, sourcePath, exception.Message); }
        if (!IsPathInsideRoot(root, destination)) return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.OutsideProjectRoot,
            destination, "The new file must be inside the existing project root.");
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !IsPortableNewFilename(Path.GetFileName(destination))
            || Path.GetRelativePath(root, destination).Contains(':', StringComparison.Ordinal))
            return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.InvalidDestination,
                destination, "The new filename is not a portable ordinary filename.");
        try
        {
            var first = allowMissingParents ? CaptureCreationAbsencePass(root, parent, destination) : CaptureAbsencePass(root, parent, destination);
            betweenVerificationPasses?.Invoke();
            var second = allowMissingParents ? CaptureCreationAbsencePass(root, parent, destination) : CaptureAbsencePass(root, parent, destination);
            return first.IsSameAbsenceAs(second)
                ? new(WccTextDiskFileAbsenceCaptureStatus.Captured, second, destination)
                : AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.ChangedDuringCapture, destination,
                    "The directory identities changed during missing-file capture.");
        }
        catch (DestinationExistsException exception)
        { return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.DestinationExists, destination, exception.Message); }
        catch (WccTextDiskRouteChangedException exception)
        { return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.ChangedDuringCapture, destination, exception.Message); }
        catch (Exception exception) when (exception is ProjectRootMissingException or SourceMissingException)
        { return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.ParentMissing, destination, exception.Message); }
        catch (Exception exception) when (exception is ReparsePointRejectedException or InvalidProjectRootException
            or InvalidSourceException or OutsideProjectRootException)
        { return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.UnsafePath, destination, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        { return AbsentFailure(WccTextDiskFileAbsenceCaptureStatus.IoFailure, destination, exception.Message); }
    }

    private static WccTextDiskFileAbsence CaptureCreationAbsencePass(string root, string parent, string destination)
    {
        if (OperatingSystem.IsLinux()) return CaptureLinuxCreationAbsencePass(root, parent, destination);
        var relativeParts = Path.GetRelativePath(root, destination).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relativeParts.Any(part => !IsPortableNewFilename(part)))
            throw new InvalidSourceException("Every created path component must be a portable ordinary filename.");
        var missing = new List<string>();
        var anchor = parent;
        while (!Directory.Exists(anchor))
        {
            if (!IsPathInsideRoot(root, anchor) || missing.Count >= 16)
                throw new SourceMissingException("A bounded existing project directory is required for creation staging.");
            missing.Add(anchor);
            anchor = Path.GetDirectoryName(anchor) ?? throw new SourceMissingException("The creation staging directory is missing.");
        }
        missing.Reverse();
        // Existing spelling and membership are exact; a case-equivalent path is never adopted as a new directory.
        var current = root;
        var anchorRelative = Path.GetRelativePath(root, anchor);
        if (anchorRelative != ".")
            foreach (var component in anchorRelative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                var matching = Directory.EnumerateFileSystemEntries(current).Where(path =>
                    string.Equals(Path.GetFileName(path), component, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matching.Length != 1 || Path.GetFileName(matching[0]) != component)
                    throw new InvalidSourceException("The existing creation route has ambiguous or mismatched directory casing.");
                current = matching[0];
            }
        var captured = CaptureAbsencePass(root, anchor, missing.Count == 0 ? destination : missing[0]);
        return new(root, anchor, destination, captured.RouteIdentitySha256, captured.ProjectRootIdentity,
            captured.SourceParentIdentity, missing);
    }

    private static WccTextDiskFileAbsence CaptureAbsencePass(string root, string parent, string destination)
    {
        if (OperatingSystem.IsLinux()) return CaptureLinuxAbsencePass(root, parent, destination);
        var chain = BuildDirectoryChain(root, parent);
        var handles = new List<SafeFileHandle>();
        try
        {
            var identities = new List<PathIdentity>();
            for (var index = 0; index < chain.Paths.Count; index++)
            {
                var path = chain.Paths[index];
                var handle = OpenPath(path, readContents: false, projectRoot: index <= chain.ProjectRootIndex);
                handles.Add(handle);
                var snapshot = ReadHandleSnapshot(handle);
                RejectReparsePoint(path, snapshot);
                if (!snapshot.IsDirectory || snapshot.IsDevice)
                    throw new InvalidSourceException("The new file route must consist of existing ordinary directories.");
                identities.Add(new(path, snapshot.Identity));
            }
            VerifyDeclaredProjectRoot(root, identities[chain.ProjectRootIndex].Identity, handles);
            var name = Path.GetFileName(destination);
            if (Directory.EnumerateFileSystemEntries(parent).Any(path =>
                    string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)))
                throw new DestinationExistsException("The new filename already exists, including a case-equivalent file or directory.");
            return new(root, parent, destination, HashRouteIdentities(identities),
                identities[chain.ProjectRootIndex].Identity, identities[^1].Identity);
        }
        finally { DisposeHandles(handles); }
    }

    private static bool IsPortableNewFilename(string name)
    {
        if (name.Length is 0 or > 255 || name[^1] is '.' or ' ' || !ContainsOnlyValidUnicode(name)
            || name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character))) return false;
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')
                && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)));
    }

    private static WccTextDiskFileAbsenceCaptureResult AbsentFailure(WccTextDiskFileAbsenceCaptureStatus status,
        string? path, string message) => new(status, SourcePath: path, ErrorMessage: message);
    private sealed class DestinationExistsException(string message) : Exception(message);
}
